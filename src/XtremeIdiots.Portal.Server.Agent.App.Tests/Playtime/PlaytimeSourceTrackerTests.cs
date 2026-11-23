using Moq;
using System.Text.Json;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Playtime;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Playtime;

public class PlaytimeSourceTrackerTests
{
    [Theory]
    [InlineData("CallOfDuty2", "123456")]
    [InlineData("CallOfDuty4", "0123456789abcdef0123456789abcdef")]
    [InlineData("CallOfDuty5", "12345678")]
    public async Task ObservePoll_SkipsCatchupAndPersistsReadTimeEvents(string game, string guid)
    {
        var fixture = new Fixture(game, 100);
        await fixture.InitializeAsync();
        fixture.SetPoll(140,
        [
            new("J;" + guid + ";1;Old", 80, 100),
            new("J;" + guid + ";1;New", 100, 120),
            new("Q;" + guid + ";1;New", 120, 140)
        ]);

        await fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None);

        var batch = Assert.Single(fixture.Batches);
        Assert.Equal(100, batch.Expected.Offset);
        Assert.Equal(140, batch.Next.Offset);
        Assert.Collection(batch.Events,
            item => { Assert.Equal(PlaytimeEventKind.Join, item.Kind); Assert.Equal("New", item.Username); },
            item => { Assert.Equal(PlaytimeEventKind.Leave, item.Kind); Assert.Equal(guid, item.GameIdentifier); });
        Assert.All(batch.Events, item =>
        {
            Assert.Equal(PlaytimeTimestampQuality.ParseTime, item.TimestampQuality);
            Assert.Equal(PlaytimeFreshness.Unknown, item.Freshness);
            Assert.NotEqual(Guid.Empty, item.EventId);
        });
    }

    [Fact]
    public async Task ObservePoll_DiscardsFirstLineWhenEofCouldBisectALine()
    {
        var fixture = new Fixture("CallOfDuty2", 100);
        await fixture.InitializeAsync();
        fixture.SetPoll(140,
        [
            new("J;123456;1;PotentialFragment", 100, 120),
            new("J;123456;1;Live", 120, 140)
        ]);

        await fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None);

        Assert.Equal("Live", Assert.Single(Assert.Single(fixture.Batches).Events).Username);
    }

    [Fact]
    public async Task ObservePoll_RetryKeepsTheSameBatchIdentityAndTimestamp()
    {
        var fixture = new Fixture("CallOfDuty2", 0);
        await fixture.InitializeAsync();
        fixture.SetPoll(20, [new("J;123456;1;Player", 0, 20)]);
        IReadOnlyList<PlaytimeObservation>? first = null;
        var attempts = 0;
        fixture.Outbox.Setup(o => o.CommitSourceBatchAsync(fixture.ServerId, It.IsAny<PlaytimeSourceCursor>(),
                It.IsAny<PlaytimeSourceCursor>(), It.IsAny<IReadOnlyList<PlaytimeObservation>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, PlaytimeSourceCursor?, PlaytimeSourceCursor, IReadOnlyList<PlaytimeObservation>, CancellationToken>(
                (_, _, _, events, _) => first ??= events)
            .Returns(() => ++attempts == 1 ? Task.FromException(new IOException("storage unavailable")) : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() => fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None));
        Assert.True(fixture.Tracker.HasPending);
        await fixture.Tracker.FlushPendingAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        fixture.Outbox.Verify(o => o.CommitSourceBatchAsync(fixture.ServerId, It.IsAny<PlaytimeSourceCursor>(),
            It.IsAny<PlaytimeSourceCursor>(), It.Is<IReadOnlyList<PlaytimeObservation>>(items => items == first),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.False(fixture.Tracker.HasPending);
    }

    [Fact]
    public async Task ObservePoll_MapReentryAndStaleLeaveDoNotReplaceNewSlotOccupant()
    {
        var fixture = new Fixture("CallOfDuty5", 0);
        await fixture.InitializeAsync();
        fixture.SetPoll(120,
        [
            new("J;12345678;1;First", 0, 20),
            new("InitGame: \\mapname\\mp_test\\g_gametype\\war", 20, 40),
            new("JT;12345678;1;allies;First", 40, 60),
            new("J;87654321;1;Second", 60, 80),
            new("Q;12345678;1;First", 80, 100),
            new("Q;87654321;1;Second", 100, 120)
        ]);

        await fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None);

        var events = Assert.Single(fixture.Batches).Events;
        Assert.Equal([PlaytimeEventKind.Join, PlaytimeEventKind.Map, PlaytimeEventKind.Join, PlaytimeEventKind.Leave, PlaytimeEventKind.Leave],
            events.Select(item => item.Kind));
        Assert.Equal("12345678", events[^2].GameIdentifier);
        Assert.Equal("87654321", events[^1].GameIdentifier);
    }

    [Fact]
    public async Task ObservePoll_ContinuityLossSkipsBacklogAndResetsAtEnd()
    {
        var fixture = new Fixture("CallOfDuty2", 0);
        await fixture.InitializeAsync();
        fixture.Tailer.SetupGet(t => t.ContinuityVersion).Returns(1);
        fixture.SetPoll(20, [new("J;123456;1;Backlog", 0, 20)], reachedEnd: false);
        await fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None);
        fixture.SetPoll(40, [new("J;123456;1;Backlog", 20, 40)], reachedEnd: true);
        await fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None);

        Assert.Empty(fixture.Batches);
        fixture.Outbox.Verify(o => o.RecordGapAndResetCursorAsync(fixture.ServerId,
            It.Is<PlaytimeSourceCursor>(item => item.Offset == 0),
            It.Is<PlaytimeSourceCursor>(item => item.Offset == 40),
            PlaytimeGapReason.ContinuityLost, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task ObserveRoster_OnlyCompleteAbsenceClearsMapReentry(bool complete, int expectedJoins)
    {
        var fixture = new Fixture("CallOfDuty2", 0);
        await fixture.InitializeAsync();
        fixture.SetPoll(40,
        [
            new("J;123456;1;Player", 0, 20),
            new("InitGame: \\mapname\\mp_test\\g_gametype\\war", 20, 40)
        ]);
        await fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None);
        fixture.Tracker.ObserveRoster(new ServerRosterSnapshot(DateTime.UtcNow,
            complete ? RconRosterState.Complete : RconRosterState.Partial,
            [], 0, 0, 0), complete ? PlaytimeCompleteness.Complete : PlaytimeCompleteness.Partial);
        fixture.SetPoll(60, [new("J;123456;1;Player", 40, 60)]);

        await fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None);

        Assert.Equal(expectedJoins, fixture.Batches.SelectMany(batch => batch.Events).Count(item => item.Kind == PlaytimeEventKind.Join));
    }

    [Fact]
    public async Task ObservePoll_SplitsLargeBatchesWithoutLosingObservations()
    {
        var fixture = new Fixture("CallOfDuty2", 0);
        await fixture.InitializeAsync();
        var records = Enumerable.Range(0, 130)
            .Select(index => new LogTailRecord($"J;123456;1;Player{index}", index * 10, (index + 1) * 10))
            .ToArray();
        fixture.SetPoll(1300, records);

        await fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None);

        Assert.Equal([128, 2], fixture.Batches.Select(batch => batch.Events.Count));
        Assert.Equal(fixture.Batches[0].Next, fixture.Batches[1].Expected);
        Assert.Equal(130, fixture.Batches.SelectMany(batch => batch.Events).Select(item => item.EventId).Distinct().Count());
    }

    [Fact]
    public async Task ObservePoll_SerializesReleasedReadTimeContract()
    {
        var fixture = new Fixture("CallOfDuty2", 0);
        await fixture.InitializeAsync();
        fixture.SetPoll(20, [new("J;123456;1;Player", 0, 20)]);
        var before = DateTimeOffset.UtcNow;
        await fixture.Tracker.ObservePollAsync(fixture.Tailer.Object, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;
        var observation = Assert.Single(Assert.Single(fixture.Batches).Events);

        using var json = JsonDocument.Parse(PlaytimeWireSerializer.Serialize(observation with { EventPublishedUtc = after }));
        var metadata = json.RootElement.GetProperty("playtime");
        Assert.Equal(1, json.RootElement.GetProperty("kind").GetInt32());
        Assert.Equal(4, metadata.GetProperty("timestampQuality").GetInt32());
        Assert.Equal(0, metadata.GetProperty("freshness").GetInt32());
        Assert.Equal(0, metadata.GetProperty("rosterCompleteness").GetInt32());
        Assert.Equal(observation.EventId, metadata.GetProperty("observationId").GetGuid());
        Assert.InRange(metadata.GetProperty("eventUtc").GetDateTimeOffset(), before, after);
        Assert.Equal("123456", json.RootElement.GetProperty("playerGuid").GetString());
        Assert.Equal("Player", json.RootElement.GetProperty("username").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("slotId").GetInt32());
    }

    private sealed class Fixture
    {
        public readonly Guid ServerId = Guid.NewGuid();
        public readonly Mock<IPlaytimeOutbox> Outbox = new();
        public readonly Mock<ILogTailer> Tailer = new();
        public readonly List<(PlaytimeSourceCursor Expected, PlaytimeSourceCursor Next, IReadOnlyList<PlaytimeObservation> Events)> Batches = [];
        public readonly PlaytimeSourceTracker Tracker;
        private long _sequence;

        public Fixture(string gameType, long initialEnd)
        {
            Tailer.SetupGet(t => t.CurrentFilePath).Returns("/game.log");
            Tailer.SetupGet(t => t.InitialEndOffset).Returns(initialEnd);
            Outbox.Setup(o => o.CommitSourceBatchAsync(ServerId, It.IsAny<PlaytimeSourceCursor>(),
                    It.IsAny<PlaytimeSourceCursor>(), It.IsAny<IReadOnlyList<PlaytimeObservation>>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, PlaytimeSourceCursor?, PlaytimeSourceCursor, IReadOnlyList<PlaytimeObservation>, CancellationToken>(
                    (_, expected, next, events, _) => { if (expected is not null) { Batches.Add((expected, next, events)); } })
                .Returns(Task.CompletedTask);
            Tracker = new PlaytimeSourceTracker(Outbox.Object, ServerId, gameType, _ => Task.CompletedTask, () => ++_sequence);
        }

        public Task InitializeAsync() => Tracker.InitializeAsync(Tailer.Object, CancellationToken.None);

        public void SetPoll(long offset, IReadOnlyList<LogTailRecord> records, bool reachedEnd = true)
        {
            Tailer.SetupGet(t => t.CurrentOffset).Returns(offset);
            Tailer.SetupGet(t => t.LastPollRecords).Returns(records);
            Tailer.SetupGet(t => t.LastPollReachedEnd).Returns(reachedEnd);
        }
    }
}
