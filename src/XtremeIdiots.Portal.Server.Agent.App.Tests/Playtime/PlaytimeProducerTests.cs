using System.Collections.Immutable;
using System.Text.Json;

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.Playtime;
using Shared = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Playtime;

public class PlaytimeProducerTests
{
    [Theory]
    [InlineData("CallOfDuty2", true)]
    [InlineData("callofduty4", true)]
    [InlineData("CallOfDuty5", true)]
    [InlineData("CallOfDuty4x", false)]
    [InlineData("Insurgency", false)]
    public void SupportsGame_ExcludesCod4xAndUnsupportedGames(string gameType, bool expected)
    {
        Assert.Equal(expected, PlaytimeProducer.SupportsGame(gameType));
    }

    [Fact]
    public void Constructor_RejectsCod4xRegardlessOfLegacyPluginMode()
    {
        Assert.Throws<ArgumentException>(() => new PlaytimeProducer(Guid.NewGuid(), "CallOfDuty4x",
            new Mock<IPlaytimeOutbox>().Object, (_, _) => Task.CompletedTask, NullLogger.Instance));
    }

    [Fact]
    public async Task ObserveRoster_SendsSourceCompletenessAndNullableBotWithDedicatedWireShape()
    {
        var id = Guid.NewGuid();
        var outbox = CreateOutbox(out var pending);
        var messages = new List<ServiceBusMessage>();
        var sender = new Mock<ServiceBusSender>();
        sender.Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((message, _) => messages.Add(message))
            .Returns(Task.CompletedTask);
        var producer = new PlaytimeProducer(id, "CallOfDuty4", outbox.Object,
            (item, ct) => PlaytimeProducer.SendAsync(sender.Object, item, ct), NullLogger.Instance);
        var observed = DateTime.UtcNow;
        var roster = new ServerRosterSnapshot(observed, RconRosterState.Complete,
            [new ServerRosterPlayer(7, "player-1", "Player", null)], 0, 0, 0, TimeSpan.FromSeconds(1));

        await producer.ObserveRosterAsync(roster, CancellationToken.None);

        var message = Assert.Single(messages);
        Assert.Empty(pending);
        using var doc = JsonDocument.Parse(message.Body.ToString());
        var root = doc.RootElement;
        var playtime = root.GetProperty("playtime");
        Assert.Equal(4, root.GetProperty("kind").GetInt32());
        Assert.Equal(3, playtime.GetProperty("timestampQuality").GetInt32());
        Assert.Equal(1, playtime.GetProperty("rosterCompleteness").GetInt32());
        Assert.Equal(observed, playtime.GetProperty("eventUtc").GetDateTime());
        Assert.Equal(message.MessageId, playtime.GetProperty("observationId").GetGuid().ToString("D"));
        Assert.Equal("player-1", root.GetProperty("players")[0].GetProperty("playerGuid").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("players")[0].GetProperty("isBot").ValueKind);
        Assert.False(root.TryGetProperty("ipAddress", out _));
        var received = JsonSerializer.Deserialize<Shared.PlaytimeObservationEvent>(message.Body.ToString(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true });
        Assert.NotNull(received);
        Assert.Equal(id, received.ServerId);
        Assert.Equal(Shared.PlaytimeObservationKind.ServerRoster, received.Kind);
        Assert.Equal(Shared.PlaytimeTimestampQuality.RosterSnapshotTimestamp, received.Playtime.TimestampQuality);
        Assert.Equal(Shared.PlaytimeRosterCompleteness.Complete, received.Playtime.RosterCompleteness);
        Assert.Equal(observed, received.Playtime.EventUtc);
        Assert.Equal(message.MessageId, received.Playtime.ObservationId.ToString("D"));
        Assert.NotEqual(Guid.Empty, received.Playtime.ProducerEpoch);
        Assert.Null(Assert.Single(received.Players!).IsBot);
        Assert.NotNull(received.PlaytimeCoverage);
        Assert.Equal(0, received.PlaytimeCoverage.DroppedCapacity);
        Assert.Equal(DateTimeKind.Utc, received.EventPublishedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, received.PlaytimeCoverage.ProducerStartedUtc.Kind);
    }

    [Theory]
    [InlineData(RconRosterState.Partial, 2)]
    [InlineData(RconRosterState.Failed, 3)]
    public async Task ObserveRoster_PreservesPartialAndFailed(RconRosterState state, int completeness)
    {
        var outbox = CreateOutbox(out _);
        PlaytimeObservation? sent = null;
        var producer = new PlaytimeProducer(Guid.NewGuid(), "CallOfDuty2", outbox.Object,
            (item, _) => { sent = item; return Task.CompletedTask; }, NullLogger.Instance);

        await producer.ObserveRosterAsync(new ServerRosterSnapshot(DateTime.UtcNow, state, [], 0, 0, 0), CancellationToken.None);

        Assert.NotNull(sent);
        Assert.Equal(completeness, (int)sent.Completeness);
        Assert.Empty(sent.Players);
    }

    [Fact]
    public async Task ObserveRoster_RejectsStaleAndInvalidCompleteRosterWithoutClaimingAbsence()
    {
        var outbox = CreateOutbox(out var pending);
        var producer = new PlaytimeProducer(Guid.NewGuid(), "CallOfDuty5", outbox.Object,
            (_, _) => throw new InvalidOperationException("must not send"), NullLogger.Instance);
        var stale = new ServerRosterSnapshot(DateTime.UtcNow.AddMinutes(-1), RconRosterState.Complete, [], 0, 0, 0);
        var slow = new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0, TimeSpan.FromSeconds(11));
        var incomplete = new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Complete, [], 1, 0, 0);

        await producer.ObserveRosterAsync(stale, CancellationToken.None);
        await producer.ObserveRosterAsync(slow, CancellationToken.None);
        await producer.ObserveRosterAsync(incomplete, CancellationToken.None);

        Assert.Empty(pending);
        outbox.Verify(o => o.RecordGapAsync(It.IsAny<Guid>(), PlaytimeGapReason.RosterStale, It.IsAny<CancellationToken>()), Times.Exactly(2));
        outbox.Verify(o => o.RecordGapAsync(It.IsAny<Guid>(), PlaytimeGapReason.InvalidRoster, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(RconRosterState.Complete)]
    [InlineData(RconRosterState.Partial)]
    public async Task ObserveRoster_MissingNamePreservesOtherPlayersWithoutClaimingCompleteness(RconRosterState state)
    {
        var outbox = CreateOutbox(out var pending);
        var producer = new PlaytimeProducer(Guid.NewGuid(), "CallOfDuty5", outbox.Object,
            (_, _) => Task.CompletedTask, NullLogger.Instance);
        var roster = new ServerRosterSnapshot(DateTime.UtcNow, state,
            [new ServerRosterPlayer(1, "one", "Player", null), new ServerRosterPlayer(2, "two", null, null)],
            0, 0, 0);

        await producer.CaptureRosterAsync(roster, CancellationToken.None);

        var observation = Assert.Single(pending);
        Assert.Equal(PlaytimeCompleteness.Partial, observation.Completeness);
        var player = Assert.Single(observation.Players);
        Assert.Equal("one", player.GameIdentifier);
        Assert.Null(player.IsBot);
    }

    [Fact]
    public async Task ObserveRoster_WhenSendFails_PersistsBeforeSendAndRetriesSameIdentity()
    {
        var outbox = CreateOutbox(out var pending);
        var sent = new List<PlaytimeObservation>();
        var fail = true;
        var producer = new PlaytimeProducer(Guid.NewGuid(), "CallOfDuty4", outbox.Object, (item, _) =>
        {
            sent.Add(item);
            Assert.Contains(item, pending);
            if (fail)
            {
                fail = false;
                throw new IOException("send failed");
            }
            return Task.CompletedTask;
        }, NullLogger.Instance);
        var roster = new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0);

        await Assert.ThrowsAsync<IOException>(() => producer.ObserveRosterAsync(roster, CancellationToken.None));
        await producer.FlushAsync(CancellationToken.None);

        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0], sent[1]);
        Assert.Equal(0, sent[0].Coverage!.DroppedAttempts);
        Assert.Empty(pending);
    }

    [Fact]
    public async Task CaptureRoster_PersistsBeforeDispatchAndPreservesCaptureTime()
    {
        var outbox = CreateOutbox(out var pending);
        PlaytimeObservation? sent = null;
        var producer = new PlaytimeProducer(Guid.NewGuid(), "CallOfDuty4", outbox.Object,
            (item, _) => { sent = item; return Task.CompletedTask; }, NullLogger.Instance);
        var observed = DateTime.UtcNow;
        var roster = new ServerRosterSnapshot(observed, RconRosterState.Partial, [], 0, 0, 0);

        await producer.CaptureRosterAsync(roster, CancellationToken.None);

        Assert.Null(sent);
        Assert.Equal(observed, Assert.Single(pending).EventUtc.UtcDateTime);
        await producer.FlushAsync(CancellationToken.None);
        Assert.NotNull(sent);
        Assert.Equal(observed, sent.EventUtc.UtcDateTime);
    }

    [Fact]
    public async Task CompleteRosterRetry_PreservesSnapshotTimeCompletenessAndUnknownBot()
    {
        var outbox = CreateOutbox(out _);
        var bodies = new List<string>();
        var producer = new PlaytimeProducer(Guid.NewGuid(), "CallOfDuty4", outbox.Object, (item, _) =>
        {
            bodies.Add(PlaytimeWireSerializer.Serialize(item));
            if (bodies.Count == 1)
            {
                throw new IOException("send failed");
            }
            return Task.CompletedTask;
        }, NullLogger.Instance);
        var observed = DateTime.UtcNow;
        var roster = new ServerRosterSnapshot(observed, RconRosterState.Complete,
            [new ServerRosterPlayer(7, "player-1", "Player", null)], 0, 0, 0, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<IOException>(() => producer.ObserveRosterAsync(roster, CancellationToken.None));
        await producer.FlushAsync(CancellationToken.None);

        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
        using var doc = JsonDocument.Parse(bodies[1]);
        var metadata = doc.RootElement.GetProperty("playtime");
        Assert.Equal(observed, metadata.GetProperty("eventUtc").GetDateTime());
        Assert.Equal(3, metadata.GetProperty("timestampQuality").GetInt32());
        Assert.Equal(1, metadata.GetProperty("rosterCompleteness").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("players")[0].GetProperty("isBot").ValueKind);
    }

    [Fact]
    public void Serialize_RosterCoverageIsAtRootWithStableUtcAndNumericMetadata()
    {
        var when = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var observation = new PlaytimeObservation
        {
            EventId = Guid.NewGuid(),
            ServerId = Guid.NewGuid(),
            GameType = "CallOfDuty4",
            EventGeneratedUtc = when,
            EventPublishedUtc = when,
            EventUtc = when,
            SequenceId = 12,
            Kind = PlaytimeEventKind.Roster,
            TimestampQuality = PlaytimeTimestampQuality.RosterSnapshotTimestamp,
            Freshness = PlaytimeFreshness.Unknown,
            Completeness = PlaytimeCompleteness.Partial,
            Epoch = Guid.NewGuid(),
            ProducerSequence = 12,
            Players = [],
            Coverage = new PlaytimeCoverage
            {
                ProducerStartedUtc = when,
                DroppedCapacity = (long)int.MaxValue + 1,
                DroppedAttempts = 2,
                DroppedAge = 3
            }
        };

        var first = PlaytimeWireSerializer.Serialize(observation);
        Assert.Equal(first, PlaytimeWireSerializer.Serialize(observation));
        using var doc = JsonDocument.Parse(first);
        var root = doc.RootElement;
        var playtime = root.GetProperty("playtime");
        Assert.Equal(observation.EventId, playtime.GetProperty("observationId").GetGuid());
        Assert.Equal(0, playtime.GetProperty("freshness").GetInt32());
        Assert.Equal(2, playtime.GetProperty("rosterCompleteness").GetInt32());
        Assert.Equal((long)int.MaxValue + 1, root.GetProperty("playtimeCoverage").GetProperty("droppedCapacity").GetInt64());
        Assert.False(playtime.TryGetProperty("playtimeCoverage", out _));
        Assert.Equal(DateTimeKind.Utc, root.GetProperty("eventPublishedUtc").GetDateTime().Kind);
        var received = JsonSerializer.Deserialize<Shared.PlaytimeObservationEvent>(first,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true });
        Assert.NotNull(received);
        Assert.Equal(observation.Epoch, received.Playtime.ProducerEpoch);
        Assert.Equal(observation.EventId, received.Playtime.ObservationId);
        Assert.Equal(when.UtcDateTime, received.EventPublishedUtc);
        Assert.Equal(when.UtcDateTime, received.PlaytimeCoverage!.ProducerStartedUtc);
        Assert.Equal((long)int.MaxValue + 1, received.PlaytimeCoverage.DroppedCapacity);
        Assert.Equal(2, received.PlaytimeCoverage.DroppedAttempts);
        Assert.Equal(3, received.PlaytimeCoverage.DroppedAge);
    }

    private static Mock<IPlaytimeOutbox> CreateOutbox(out List<PlaytimeObservation> events)
    {
        var pending = new List<PlaytimeObservation>();
        events = pending;
        var outbox = new Mock<IPlaytimeOutbox>();
        outbox.Setup(o => o.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<PlaytimeObservation>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, PlaytimeObservation, CancellationToken>((_, item, _) => pending.Add(item))
            .Returns(Task.CompletedTask);
        outbox.Setup(o => o.GetPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => pending.ToImmutableArray());
        outbox.Setup(o => o.PreparePublishAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid id, DateTimeOffset when, CancellationToken _) =>
            {
                var index = pending.FindIndex(item => item.EventId == id);
                if (index < 0)
                {
                    return null;
                }
                pending[index] = pending[index] with { EventPublishedUtc = pending[index].EventPublishedUtc ?? when };
                return pending[index];
            });
        outbox.Setup(o => o.AcknowledgeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid id, CancellationToken _) => pending.RemoveAll(item => item.EventId == id) != 0);
        outbox.Setup(o => o.RecordGapAsync(It.IsAny<Guid>(), It.IsAny<PlaytimeGapReason>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        PlaytimeCoverageState? coverage = null;
        outbox.Setup(o => o.GetOrCreateCoverageAsync(It.IsAny<Guid>(), It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid epoch, DateTimeOffset started, CancellationToken _) =>
                coverage ??= new PlaytimeCoverageState(epoch, new PlaytimeCoverage
                {
                    ProducerStartedUtc = started,
                    DroppedCapacity = 0,
                    DroppedAttempts = 0,
                    DroppedAge = 0
                }));
        return outbox;
    }
}
