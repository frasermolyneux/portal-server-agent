using System.Text;
using System.Text.Json;

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Playtime;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Playtime;

public sealed class BlobPlaytimeOutboxTests
{
    [Fact]
    public async Task CoverageState_PersistsEpochAndIdempotentDropsAcrossRestart()
    {
        var storage = new InMemoryBlob();
        var serverId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var store = storage.CreateStore();
        await store.GetOrCreateCoverageAsync(serverId, epoch, started);
        await store.RecordCoverageDropAsync(serverId, epoch, PlaytimeDropReason.Capacity, 1);
        var restarted = storage.CreateStore();
        await restarted.RecordCoverageDropAsync(serverId, epoch, PlaytimeDropReason.Capacity, 1);
        await restarted.RecordGapAsync(serverId, PlaytimeGapReason.RosterStale);
        var state = await restarted.GetOrCreateCoverageAsync(serverId, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(epoch, state.Epoch);
        Assert.Equal(started, state.Coverage.ProducerStartedUtc);
        Assert.Equal(1, state.Coverage.DroppedCapacity);
        Assert.Equal(0, state.Coverage.DroppedAttempts);
        Assert.Equal(0, state.Coverage.DroppedAge);
        Assert.Equal(2, (await restarted.GetGapSummaryAsync(serverId)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.RecordCoverageDropAsync(serverId, Guid.NewGuid(), PlaytimeDropReason.Capacity, 1));
    }

    [Fact]
    public async Task CoverageState_WhenCommittedWriteReportsFailure_RetryDoesNotDoubleCount()
    {
        var storage = new InMemoryBlob();
        var serverId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var store = storage.CreateStore();
        await store.GetOrCreateCoverageAsync(serverId, epoch, DateTimeOffset.UtcNow);
        storage.FailAfterNextUpload = true;

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            store.RecordCoverageDropAsync(serverId, epoch, PlaytimeDropReason.Capacity, 1));
        await store.RecordCoverageDropAsync(serverId, epoch, PlaytimeDropReason.Capacity, 1);

        var state = await storage.CreateStore().GetOrCreateCoverageAsync(serverId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(1, state.Coverage.DroppedCapacity);
        Assert.Equal(1, (await store.GetGapSummaryAsync(serverId)).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CoverageState_RecordsOnlyExplicitDropReason(int value)
    {
        var reason = (PlaytimeDropReason)value;
        var storage = new InMemoryBlob();
        var serverId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var store = storage.CreateStore();
        await store.GetOrCreateCoverageAsync(serverId, epoch, DateTimeOffset.UtcNow);
        await store.RecordCoverageDropAsync(serverId, epoch, reason, 1);

        var state = await storage.CreateStore().GetOrCreateCoverageAsync(serverId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(reason == PlaytimeDropReason.Capacity ? 1 : 0, state.Coverage.DroppedCapacity);
        Assert.Equal(reason == PlaytimeDropReason.Attempts ? 1 : 0, state.Coverage.DroppedAttempts);
        Assert.Equal(reason == PlaytimeDropReason.Age ? 1 : 0, state.Coverage.DroppedAge);
    }

    [Fact]
    public async Task Producer_BacklogDropIsReportedInNextRosterAndEpochSurvivesRestart()
    {
        var storage = new InMemoryBlob();
        var serverId = Guid.NewGuid();
        var store = storage.CreateStore();
        var initial = await store.GetOrCreateCoverageAsync(serverId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1));
        var sent = new List<PlaytimeObservation>();
        var producer = new PlaytimeProducer(serverId, "CallOfDuty4", store,
            (item, _) => { sent.Add(item); return Task.CompletedTask; }, NullLogger.Instance);
        var roster = new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0);
        storage.UploadFailuresRemaining = 1;
        await Assert.ThrowsAsync<RequestFailedException>(() => producer.ObserveRosterAsync(roster, CancellationToken.None));

        await producer.ObserveRosterAsync(roster, CancellationToken.None);
        await producer.FlushAsync(CancellationToken.None);
        var restarted = new PlaytimeProducer(serverId, "CallOfDuty4", storage.CreateStore(),
            (item, _) => { sent.Add(item); return Task.CompletedTask; }, NullLogger.Instance);
        await restarted.ObserveRosterAsync(
            new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0), CancellationToken.None);

        Assert.Equal(2, sent.Count);
        Assert.Equal(initial.Epoch, sent[0].Epoch);
        Assert.Equal(initial.Epoch, sent[1].Epoch);
        Assert.Null(sent[1].ProducerSequence);
        Assert.Equal(0, sent[0].Coverage!.DroppedCapacity);
        Assert.Equal(1, sent[1].Coverage!.DroppedCapacity);
        Assert.Equal(0, sent[1].Coverage!.DroppedAttempts);
        Assert.Equal(0, sent[1].Coverage!.DroppedAge);
    }

    [Fact]
    public async Task Producer_AccumulatesBacklogDropsWhenCounterWritesFail()
    {
        var storage = new InMemoryBlob();
        var serverId = Guid.NewGuid();
        var store = storage.CreateStore();
        await store.GetOrCreateCoverageAsync(serverId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1));
        var sent = new List<PlaytimeObservation>();
        var producer = new PlaytimeProducer(serverId, "CallOfDuty4", store,
            (item, _) => { sent.Add(item); return Task.CompletedTask; }, NullLogger.Instance);
        var roster = new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0);
        storage.UploadFailuresRemaining = 1;
        await Assert.ThrowsAsync<RequestFailedException>(() => producer.ObserveRosterAsync(roster, CancellationToken.None));
        storage.DownloadFailuresRemaining = 2;
        await Assert.ThrowsAsync<RequestFailedException>(() => producer.ObserveRosterAsync(roster, CancellationToken.None));
        await Assert.ThrowsAsync<RequestFailedException>(() => producer.ObserveRosterAsync(roster, CancellationToken.None));
        await producer.FlushAsync(CancellationToken.None);
        await producer.ObserveRosterAsync(
            new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0), CancellationToken.None);

        Assert.Equal(2, sent.Count);
        Assert.Equal(0, sent[0].Coverage!.DroppedCapacity);
        Assert.Equal(2, sent[1].Coverage!.DroppedCapacity);
    }

    [Fact]
    public async Task Producer_RetainsRosterAndBacklogDropsBeforeCoverageInitialization()
    {
        var storage = new InMemoryBlob { DownloadFailuresRemaining = 3 };
        var serverId = Guid.NewGuid();
        var sent = new List<PlaytimeObservation>();
        var producer = new PlaytimeProducer(serverId, "CallOfDuty4", storage.CreateStore(),
            (item, _) => { sent.Add(item); return Task.CompletedTask; }, NullLogger.Instance);
        var roster = new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0);

        await Assert.ThrowsAsync<RequestFailedException>(() => producer.ObserveRosterAsync(roster, CancellationToken.None));
        await Assert.ThrowsAsync<RequestFailedException>(() => producer.ObserveRosterAsync(roster, CancellationToken.None));
        await Assert.ThrowsAsync<RequestFailedException>(() => producer.ObserveRosterAsync(roster, CancellationToken.None));
        Assert.True(producer.HasUnpersistedObservation);
        await producer.FlushAsync(CancellationToken.None);

        Assert.False(producer.HasUnpersistedObservation);
        Assert.Equal(2, Assert.Single(sent).Coverage!.DroppedCapacity);
    }

    [Fact]
    public async Task DispatchAsync_ExpiredObservationIsAtomicallyDroppedAndReportedByNextRoster()
    {
        var storage = new InMemoryBlob();
        var store = storage.CreateStore();
        var serverId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow.AddDays(-2);
        await store.GetOrCreateCoverageAsync(serverId, epoch, started);
        var old = CreateJoin() with
        {
            ServerId = serverId,
            Epoch = epoch,
            EventUtc = DateTimeOffset.UtcNow.AddDays(-1).AddSeconds(-1)
        };
        await store.EnqueueAsync(serverId, old);
        var sends = new List<PlaytimeObservation>();
        var dispatcher = new PlaytimeOutboxDispatcher(store,
            (item, _) => { sends.Add(item); return Task.CompletedTask; });
        storage.FailAfterNextUpload = true;

        await Assert.ThrowsAsync<RequestFailedException>(() => dispatcher.DispatchAsync(serverId));
        await dispatcher.DispatchAsync(serverId);
        Assert.Empty(sends);
        Assert.Empty(await store.GetPendingAsync(serverId));
        var coverage = await storage.CreateStore().GetOrCreateCoverageAsync(serverId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(1, coverage.Coverage.DroppedAge);
        Assert.Equal(1, (await store.GetGapSummaryAsync(serverId)).Count);

        var producer = new PlaytimeProducer(serverId, "CallOfDuty4", storage.CreateStore(),
            (item, _) => { sends.Add(item); return Task.CompletedTask; }, NullLogger.Instance);
        await producer.ObserveRosterAsync(
            new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0), CancellationToken.None);
        Assert.Equal(1, Assert.Single(sends).Coverage!.DroppedAge);
    }

    [Fact]
    public async Task DropExpiredAsync_RequiresOlderEventAndLeavesItPendingIfWriteFails()
    {
        var storage = new InMemoryBlob();
        var store = storage.CreateStore();
        var observation = CreateJoin();
        await store.GetOrCreateCoverageAsync(observation.ServerId, observation.Epoch!.Value, observation.EventUtc.AddHours(-1));
        await store.EnqueueAsync(observation.ServerId, observation);

        Assert.False(await store.DropExpiredAsync(observation.ServerId, observation.EventId, observation.EventUtc));
        storage.UploadFailuresRemaining = 1;
        await Assert.ThrowsAsync<RequestFailedException>(() =>
            store.DropExpiredAsync(observation.ServerId, observation.EventId, observation.EventUtc.AddTicks(1)));
        Assert.Single(await store.GetPendingAsync(observation.ServerId));
        var state = await store.GetOrCreateCoverageAsync(observation.ServerId, Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(0, state.Coverage.DroppedAge);

        Assert.True(await store.DropExpiredAsync(observation.ServerId, observation.EventId, observation.EventUtc.AddTicks(1)));
        Assert.False(await store.DropExpiredAsync(observation.ServerId, observation.EventId, observation.EventUtc.AddTicks(1)));
        Assert.Equal(1, (await store.GetOrCreateCoverageAsync(observation.ServerId, Guid.NewGuid(), DateTimeOffset.UtcNow)).Coverage.DroppedAge);
    }

    [Fact]
    public async Task RecordGapAsync_PersistsWithoutChangingPendingObservations()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var store = storage.CreateStore();
        await store.EnqueueAsync(observation.ServerId, observation);
        await store.RecordGapAsync(observation.ServerId, PlaytimeGapReason.RosterStale);

        var restarted = storage.CreateStore();
        Assert.Equal(new PlaytimeGapSummary(1, PlaytimeGapReason.RosterStale),
            await restarted.GetGapSummaryAsync(observation.ServerId));
        Assert.Equal(observation, Assert.Single(await restarted.GetPendingAsync(observation.ServerId)));
    }

    [Fact]
    public async Task DispatchAsync_PersistsFirstAttemptTimeBeforeSendAndRetainsItAcrossRestart()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin() with { EventPublishedUtc = null };
        var store = storage.CreateStore();
        await store.EnqueueAsync(observation.ServerId, observation);
        var sent = new List<PlaytimeObservation>();
        var failed = new PlaytimeOutboxDispatcher(store, (item, _) =>
        {
            sent.Add(item);
            throw new IOException("send failed");
        });

        await Assert.ThrowsAsync<IOException>(() => failed.DispatchAsync(observation.ServerId));
        var saved = Assert.Single(await storage.CreateStore().GetPendingAsync(observation.ServerId));
        Assert.NotNull(saved.EventPublishedUtc);
        var restarted = new PlaytimeOutboxDispatcher(storage.CreateStore(), (item, _) =>
        {
            sent.Add(item);
            return Task.CompletedTask;
        });
        await restarted.DispatchAsync(observation.ServerId);

        Assert.Equal(saved, sent[0]);
        Assert.Equal(saved, sent[1]);
    }

    [Fact]
    public async Task DispatchAsync_WhenPreparationFails_DoesNotSend()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin() with { EventPublishedUtc = null };
        var store = storage.CreateStore();
        await store.EnqueueAsync(observation.ServerId, observation);
        storage.UploadFailuresRemaining = 1;
        var sends = 0;
        var dispatcher = new PlaytimeOutboxDispatcher(store, (_, _) =>
        {
            sends++;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<RequestFailedException>(() => dispatcher.DispatchAsync(observation.ServerId));

        Assert.Equal(0, sends);
        Assert.Null(Assert.Single(await store.GetPendingAsync(observation.ServerId)).EventPublishedUtc);
    }

    [Fact]
    public async Task DispatchAsync_CoveragePreservesEpochAndReportTimeOnRetry()
    {
        var storage = new InMemoryBlob();
        var initial = CreateJoin();
        var observation = initial with
        {
            Kind = PlaytimeEventKind.Roster,
            TimestampQuality = PlaytimeTimestampQuality.RosterSnapshotTimestamp,
            Completeness = PlaytimeCompleteness.Partial,
            EventPublishedUtc = null,
            CorrelationId = null,
            GameIdentifier = null,
            Username = null,
            Slot = null,
            SteamId = null,
            Players = [],
            Coverage = new PlaytimeCoverage
            {
                ProducerStartedUtc = initial.EventUtc.AddMinutes(-1),
                DroppedCapacity = 1,
                DroppedAttempts = 2,
                DroppedAge = 3
            }
        };
        var store = storage.CreateStore();
        await store.GetOrCreateCoverageAsync(observation.ServerId, observation.Epoch!.Value, observation.Coverage.ProducerStartedUtc);
        await store.RecordCoverageDropAsync(observation.ServerId, observation.Epoch.Value, PlaytimeDropReason.Capacity, 1);
        await store.RecordCoverageDropAsync(observation.ServerId, observation.Epoch.Value, PlaytimeDropReason.Attempts, 2);
        await store.RecordCoverageDropAsync(observation.ServerId, observation.Epoch.Value, PlaytimeDropReason.Age, 3);
        await store.EnqueueAsync(observation.ServerId, observation);
        var bodies = new List<string>();
        var dispatcher = new PlaytimeOutboxDispatcher(store, (item, _) =>
        {
            bodies.Add(PlaytimeWireSerializer.Serialize(item));
            throw new IOException("send failed");
        });

        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync(observation.ServerId));
        var persisted = Assert.Single(await storage.CreateStore().GetPendingAsync(observation.ServerId));
        await store.RecordCoverageDropAsync(observation.ServerId, observation.Epoch.Value, PlaytimeDropReason.Capacity, 2);
        var restarted = new PlaytimeOutboxDispatcher(storage.CreateStore(), (item, _) =>
        {
            bodies.Add(PlaytimeWireSerializer.Serialize(item));
            return Task.CompletedTask;
        });
        await restarted.DispatchAsync(observation.ServerId);

        Assert.Equal(bodies[0], bodies[1]);
        using var json = JsonDocument.Parse(bodies[0]);
        var root = json.RootElement;
        Assert.Equal(observation.Epoch, root.GetProperty("playtime").GetProperty("producerEpoch").GetGuid());
        Assert.Equal(persisted.EventPublishedUtc!.Value.UtcDateTime, root.GetProperty("eventPublishedUtc").GetDateTime());
        var coverage = root.GetProperty("playtimeCoverage");
        Assert.Equal(observation.Coverage.ProducerStartedUtc.UtcDateTime, coverage.GetProperty("producerStartedUtc").GetDateTime());
        Assert.Equal(1, coverage.GetProperty("droppedCapacity").GetInt64());
        Assert.Equal(2, coverage.GetProperty("droppedAttempts").GetInt64());
        Assert.Equal(3, coverage.GetProperty("droppedAge").GetInt64());
    }

    [Fact]
    public async Task PreparePublishAsync_WhenCoverageStartIsAfterReportTime_DoesNotPersistReportTime()
    {
        var storage = new InMemoryBlob();
        var initial = CreateJoin();
        var roster = initial with
        {
            Kind = PlaytimeEventKind.Roster,
            Completeness = PlaytimeCompleteness.Partial,
            EventPublishedUtc = null,
            CorrelationId = null,
            GameIdentifier = null,
            Username = null,
            Slot = null,
            SteamId = null,
            Players = [],
            Coverage = new PlaytimeCoverage
            {
                ProducerStartedUtc = initial.EventUtc,
                DroppedCapacity = 0,
                DroppedAttempts = 0,
                DroppedAge = 0
            }
        };
        var store = storage.CreateStore();
        await store.EnqueueAsync(roster.ServerId, roster);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PreparePublishAsync(roster.ServerId, roster.EventId, roster.EventUtc.AddSeconds(-1)));

        Assert.Null(Assert.Single(await store.GetPendingAsync(roster.ServerId)).EventPublishedUtc);
    }

    [Fact]
    public async Task DispatchAsync_ConcurrentCallsOnSameDispatcher_DoNotSendTwice()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var store = storage.CreateStore();
        await store.EnqueueAsync(observation.ServerId, observation);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = 0;
        var dispatcher = new PlaytimeOutboxDispatcher(store, async (_, ct) =>
        {
            Interlocked.Increment(ref sent);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var first = dispatcher.DispatchAsync(observation.ServerId, cts.Token);
        await started.Task.WaitAsync(cts.Token);
        var second = dispatcher.DispatchAsync(observation.ServerId, cts.Token);
        release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, sent);
        Assert.Empty(await store.GetPendingAsync(observation.ServerId));
    }

    [Fact]
    public async Task DispatchAsync_WhenSendFails_RetriesSameObservationBeforeLaterItems()
    {
        var storage = new InMemoryBlob();
        var first = CreateJoin();
        var second = CreateJoin() with { ServerId = first.ServerId };
        var store = storage.CreateStore();
        await store.EnqueueAsync(first.ServerId, first);
        await store.EnqueueAsync(first.ServerId, second);
        var sent = new List<PlaytimeObservation>();
        var fail = true;
        var dispatcher = new PlaytimeOutboxDispatcher(store, (item, _) =>
        {
            sent.Add(item);
            if (fail)
            {
                fail = false;
                throw new IOException("send failed");
            }

            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<IOException>(() => dispatcher.DispatchAsync(first.ServerId));
        Assert.Equal(new[] { first, second }, await store.GetPendingAsync(first.ServerId));
        await dispatcher.DispatchAsync(first.ServerId);

        Assert.Equal(new[] { first, first, second }, sent);
        Assert.Empty(await store.GetPendingAsync(first.ServerId));
    }

    [Fact]
    public async Task DispatchAsync_WhenSendTimesOut_RetainsPreparedObservationForRetry()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin() with { EventPublishedUtc = null };
        var store = storage.CreateStore();
        await store.EnqueueAsync(observation.ServerId, observation);
        var sent = new List<PlaytimeObservation>();
        var dispatcher = new PlaytimeOutboxDispatcher(store, async (item, ct) =>
        {
            sent.Add(item);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, attemptTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(observation.ServerId));
        var saved = Assert.Single(await storage.CreateStore().GetPendingAsync(observation.ServerId));
        Assert.NotNull(saved.EventPublishedUtc);
        var retry = new PlaytimeOutboxDispatcher(storage.CreateStore(), (item, _) =>
        {
            sent.Add(item);
            return Task.CompletedTask;
        });
        await retry.DispatchAsync(observation.ServerId);

        Assert.Equal(2, sent.Count);
        Assert.Equal(saved, sent[0]);
        Assert.Equal(saved, sent[1]);
        Assert.Empty(await store.GetPendingAsync(observation.ServerId));
    }

    [Fact]
    public async Task DispatchAsync_WhenAcknowledgementFails_ReplaysIdenticalObservationAfterRestart()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var store = storage.CreateStore();
        await store.EnqueueAsync(observation.ServerId, observation);
        var sent = new List<PlaytimeObservation>();
        var dispatcher = new PlaytimeOutboxDispatcher(store, (item, _) =>
        {
            sent.Add(item);
            storage.UploadFailuresRemaining = 1;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<RequestFailedException>(() => dispatcher.DispatchAsync(observation.ServerId));
        var restartedStore = storage.CreateStore();
        var restarted = new PlaytimeOutboxDispatcher(restartedStore, (item, _) =>
        {
            sent.Add(item);
            return Task.CompletedTask;
        });
        await restarted.DispatchAsync(observation.ServerId);

        Assert.Equal(new[] { observation, observation }, sent);
        Assert.Empty(await restartedStore.GetPendingAsync(observation.ServerId));
    }

    [Fact]
    public async Task DispatchAsync_WhenOwnershipLostAfterSend_StopsWithoutAcknowledgingOrSendingNext()
    {
        var storage = new InMemoryBlob();
        var first = CreateJoin();
        var second = CreateJoin() with { ServerId = first.ServerId };
        var store = storage.CreateStore();
        await store.EnqueueAsync(first.ServerId, first);
        await store.EnqueueAsync(first.ServerId, second);
        var sent = new List<PlaytimeObservation>();
        var dispatcher = new PlaytimeOutboxDispatcher(store, (item, _) =>
        {
            sent.Add(item);
            storage.LeaseOwned = false;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<PlaytimeOutboxOwnershipException>(() => dispatcher.DispatchAsync(first.ServerId));
        Assert.Equal(new[] { first }, sent);
        storage.LeaseOwned = true;
        Assert.Equal(new[] { first, second }, await storage.CreateStore().GetPendingAsync(first.ServerId));
    }

    [Fact]
    public async Task EnqueueAsync_PersistsIdentityAndMetadataAcrossStoreRestartAndRetries()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var serverId = observation.ServerId;
        var firstStore = storage.CreateStore();

        await firstStore.EnqueueAsync(serverId, observation);
        storage.VerifyOutboxPath(serverId);

        var restartedStore = storage.CreateStore();
        await restartedStore.EnqueueAsync(serverId, observation);
        var pending = await restartedStore.GetPendingAsync(serverId);

        var saved = Assert.Single(pending);
        Assert.Equal(observation, saved);
        Assert.True(await restartedStore.AcknowledgeAsync(serverId, observation.EventId));
        Assert.Empty(await restartedStore.GetPendingAsync(serverId));
        Assert.False(await restartedStore.AcknowledgeAsync(serverId, observation.EventId));
    }

    [Fact]
    public async Task CommitSourceBatchAsync_AtomicallyPersistsCursorAndObservationsAcrossRestart()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var initial = new PlaytimeSourceCursor(Guid.NewGuid(), "/logs/game.log", 100);
        var next = initial with { Offset = 200 };
        var store = storage.CreateStore();
        await store.CommitSourceBatchAsync(observation.ServerId, null, initial, []);
        await store.CommitSourceBatchAsync(observation.ServerId, initial, next, [observation]);

        var restarted = storage.CreateStore();
        Assert.Equal(next, await restarted.GetSourceCursorAsync(observation.ServerId));
        Assert.Equal(observation, Assert.Single(await restarted.GetPendingAsync(observation.ServerId)));
        await restarted.CommitSourceBatchAsync(observation.ServerId, initial, next, [observation]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.CommitSourceBatchAsync(observation.ServerId, initial, next, [observation with { EventId = Guid.NewGuid() }]));
    }

    [Fact]
    public async Task CommitSourceBatchAsync_WhenUploadFails_DoesNotAdvanceCursorOrQueue()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var cursor = new PlaytimeSourceCursor(Guid.NewGuid(), "/logs/game.log", 100);
        var store = storage.CreateStore();
        await store.CommitSourceBatchAsync(observation.ServerId, null, cursor, []);
        storage.UploadFailuresRemaining = 1;

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            store.CommitSourceBatchAsync(observation.ServerId, cursor, cursor with { Offset = 200 }, [observation]));

        Assert.Equal(cursor, await store.GetSourceCursorAsync(observation.ServerId));
        Assert.Empty(await store.GetPendingAsync(observation.ServerId));
    }

    [Fact]
    public async Task CommitSourceBatchAsync_NewSourceCannotIncludeBacklog()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var cursor = new PlaytimeSourceCursor(Guid.NewGuid(), "/logs/game.log", 100);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.CreateStore().CommitSourceBatchAsync(observation.ServerId, null, cursor, [observation]));
        Assert.Equal(0, storage.DownloadCount);
    }

    [Fact]
    public async Task RecordGapAndResetCursorAsync_RotationPreservesPendingAndRecordsGapAtomically()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var initial = new PlaytimeSourceCursor(Guid.NewGuid(), "/logs/game.log", 100);
        var advanced = initial with { Offset = 200 };
        var rotated = new PlaytimeSourceCursor(Guid.NewGuid(), initial.FilePath, 20);
        var store = storage.CreateStore();
        await store.CommitSourceBatchAsync(observation.ServerId, null, initial, []);
        await store.CommitSourceBatchAsync(observation.ServerId, initial, advanced, [observation]);
        await store.RecordGapAndResetCursorAsync(observation.ServerId, advanced, rotated, PlaytimeGapReason.Rotation);

        var restarted = storage.CreateStore();
        Assert.Equal(rotated, await restarted.GetSourceCursorAsync(observation.ServerId));
        Assert.Equal(new PlaytimeGapSummary(1, PlaytimeGapReason.Rotation),
            await restarted.GetGapSummaryAsync(observation.ServerId));
        Assert.Equal(observation, Assert.Single(await restarted.GetPendingAsync(observation.ServerId)));
        await restarted.RecordGapAndResetCursorAsync(observation.ServerId, advanced, rotated, PlaytimeGapReason.Rotation);
        Assert.Equal(new PlaytimeGapSummary(1, PlaytimeGapReason.Rotation), await restarted.GetGapSummaryAsync(observation.ServerId));
    }

    [Fact]
    public async Task SourceReset_WhenCommittedResponseIsLost_RetriesSameGenerationWithoutDoubleCounting()
    {
        var storage = new InMemoryBlob();
        var serverId = Guid.NewGuid();
        var store = storage.CreateStore();
        var tailer = new Mock<ILogTailer>();
        tailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/game.log");
        tailer.SetupGet(t => t.LastPollReachedEnd).Returns(true);
        tailer.SetupGet(t => t.CurrentOffset).Returns(20);
        var tracker = new PlaytimeSourceTracker(store, serverId, "CallOfDuty2", _ => Task.CompletedTask, () => 1);
        await tracker.InitializeAsync(tailer.Object, CancellationToken.None);
        tailer.SetupGet(t => t.ContinuityVersion).Returns(1);
        storage.FailAfterNextUpload = true;

        await Assert.ThrowsAsync<RequestFailedException>(() => tracker.ObservePollAsync(tailer.Object, CancellationToken.None));
        var committed = await store.GetSourceCursorAsync(serverId);
        tracker.MarkSkippedPoll();
        await tracker.FlushPendingAsync(CancellationToken.None);
        Assert.Equal(committed, await store.GetSourceCursorAsync(serverId));
        Assert.Equal(new PlaytimeGapSummary(1, PlaytimeGapReason.ContinuityLost), await store.GetGapSummaryAsync(serverId));

        tailer.SetupGet(t => t.CurrentOffset).Returns(40);
        await tracker.ObservePollAsync(tailer.Object, CancellationToken.None);
        Assert.Equal(new PlaytimeGapSummary(2, PlaytimeGapReason.Backlog), await store.GetGapSummaryAsync(serverId));
        Assert.Equal(40, (await store.GetSourceCursorAsync(serverId))!.Offset);
    }

    [Fact]
    public async Task Producer_RetriesAmbiguousSourceBatchBeforeRosterDispatch()
    {
        var storage = new InMemoryBlob();
        var serverId = Guid.NewGuid();
        var store = storage.CreateStore();
        var sent = new List<PlaytimeObservation>();
        var producer = new PlaytimeProducer(serverId, "CallOfDuty2", store,
            (item, _) => { sent.Add(item); return Task.CompletedTask; }, NullLogger.Instance);
        var tailer = new Mock<ILogTailer>();
        tailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/game.log");
        tailer.SetupGet(t => t.CurrentOffset).Returns(20);
        tailer.SetupGet(t => t.LastPollRecords).Returns([new LogTailRecord("J;123456;1;Player", 0, 20)]);
        await producer.SourceTracker.InitializeAsync(tailer.Object, CancellationToken.None);
        storage.FailAfterNextUpload = true;
        await Assert.ThrowsAsync<RequestFailedException>(() =>
            producer.SourceTracker.ObservePollAsync(tailer.Object, CancellationToken.None));

        await producer.ObserveRosterAsync(new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0),
            CancellationToken.None);

        Assert.False(producer.SourceTracker.HasPending);
        Assert.Equal([PlaytimeEventKind.Join, PlaytimeEventKind.Roster], sent.Select(item => item.Kind));
        Assert.Empty(await store.GetPendingAsync(serverId));
    }

    [Fact]
    public async Task Producer_SourceCapacityDispatchReconcilesAmbiguousRosterBeforePartialFailure()
    {
        var storage = new InMemoryBlob();
        var serverId = Guid.NewGuid();
        var store = storage.CreateStore();
        var sent = new List<PlaytimeObservation>();
        var fail = true;
        var producer = new PlaytimeProducer(serverId, "CallOfDuty2", store, (item, _) =>
        {
            if (fail && item.Kind == PlaytimeEventKind.Roster)
            {
                throw new IOException("send failed");
            }
            sent.Add(item);
            return Task.CompletedTask;
        }, NullLogger.Instance);
        var tailer = new Mock<ILogTailer>();
        tailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/game.log");
        tailer.SetupGet(t => t.CurrentOffset).Returns(40);
        tailer.SetupGet(t => t.LastPollRecords).Returns(
        [
            new LogTailRecord("J;123456;1;Player", 0, 20),
            new LogTailRecord("Q;123456;1;Player", 20, 40)
        ]);
        await producer.SourceTracker.InitializeAsync(tailer.Object, CancellationToken.None);
        await store.GetOrCreateCoverageAsync(serverId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(-1));
        for (var index = 0; index < 127; index++)
        {
            await store.EnqueueAsync(serverId, CreateJoin() with { ServerId = serverId, EventId = Guid.NewGuid() });
        }
        storage.FailAfterNextUpload = true;
        await Assert.ThrowsAsync<RequestFailedException>(() => producer.CaptureRosterAsync(
            new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial, [], 0, 0, 0), CancellationToken.None));

        await Assert.ThrowsAsync<IOException>(() => producer.SourceTracker.ObservePollAsync(tailer.Object, CancellationToken.None));
        fail = false;
        await producer.FlushAsync(CancellationToken.None);

        Assert.False(producer.HasUnpersistedObservation);
        Assert.Empty(await store.GetPendingAsync(serverId));
        Assert.Single(sent, item => item.Kind == PlaytimeEventKind.Roster);
        Assert.Equal(130, sent.Count);
    }

    [Fact]
    public async Task Producer_ReconcilesAcceptedRosterEvenWhenPersistenceFails()
    {
        var storage = new InMemoryBlob();
        var serverId = Guid.NewGuid();
        var store = storage.CreateStore();
        var producer = new PlaytimeProducer(serverId, "CallOfDuty2", store, (_, _) => Task.CompletedTask, NullLogger.Instance);
        var tailer = new Mock<ILogTailer>();
        tailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/game.log");
        tailer.SetupGet(t => t.CurrentOffset).Returns(40);
        tailer.SetupGet(t => t.LastPollRecords).Returns(
        [
            new LogTailRecord("J;123456;1;Player", 0, 20),
            new LogTailRecord("InitGame: \\mapname\\mp_test\\g_gametype\\war", 20, 40)
        ]);
        await producer.SourceTracker.InitializeAsync(tailer.Object, CancellationToken.None);
        await producer.SourceTracker.ObservePollAsync(tailer.Object, CancellationToken.None);
        storage.UploadFailuresRemaining = 1;
        await Assert.ThrowsAsync<RequestFailedException>(() => producer.CaptureRosterAsync(
            new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Complete, [], 0, 0, 0), CancellationToken.None));
        await producer.FlushAsync(CancellationToken.None);
        tailer.SetupGet(t => t.CurrentOffset).Returns(60);
        tailer.SetupGet(t => t.LastPollRecords).Returns([new LogTailRecord("J;123456;1;Player", 40, 60)]);

        await producer.SourceTracker.ObservePollAsync(tailer.Object, CancellationToken.None);

        Assert.Equal(PlaytimeEventKind.Join, Assert.Single(await store.GetPendingAsync(serverId)).Kind);
    }

    [Fact]
    public async Task RecordGapAndResetCursorAsync_WhenWriteFails_LeavesGapAndCursorUnchanged()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var initial = new PlaytimeSourceCursor(Guid.NewGuid(), "/logs/game.log", 100);
        var rotated = new PlaytimeSourceCursor(Guid.NewGuid(), initial.FilePath, 20);
        var store = storage.CreateStore();
        await store.CommitSourceBatchAsync(observation.ServerId, null, initial, []);
        storage.UploadFailuresRemaining = 1;

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            store.RecordGapAndResetCursorAsync(observation.ServerId, initial, rotated, PlaytimeGapReason.Rotation));

        Assert.Equal(initial, await store.GetSourceCursorAsync(observation.ServerId));
        Assert.Equal(new PlaytimeGapSummary(0, null), await store.GetGapSummaryAsync(observation.ServerId));
    }

    [Fact]
    public async Task PauseSourceAsync_PersistsGapAndBlocksFurtherCommits()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var cursor = new PlaytimeSourceCursor(Guid.NewGuid(), "/logs/game.log", 100);
        var store = storage.CreateStore();
        await store.CommitSourceBatchAsync(observation.ServerId, null, cursor, []);
        await store.PauseSourceAsync(observation.ServerId, cursor, PlaytimeGapReason.ContinuityLost);
        await store.PauseSourceAsync(observation.ServerId, cursor, PlaytimeGapReason.ContinuityLost);

        var restarted = storage.CreateStore();
        Assert.Equal(new PlaytimeGapSummary(1, PlaytimeGapReason.ContinuityLost),
            await restarted.GetGapSummaryAsync(observation.ServerId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            restarted.CommitSourceBatchAsync(observation.ServerId, cursor, cursor with { Offset = 200 }, [observation]));
        Assert.Empty(await restarted.GetPendingAsync(observation.ServerId));
    }

    [Fact]
    public async Task EnqueueAsync_RejectsReusedIdentityWithDifferentMetadata()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var serverId = observation.ServerId;
        var store = storage.CreateStore();
        await store.EnqueueAsync(serverId, observation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EnqueueAsync(serverId, observation with { SequenceId = 42 }));

        Assert.Equal(observation, Assert.Single(await store.GetPendingAsync(serverId)));
    }

    [Fact]
    public async Task EnqueueAsync_RejectsServerIdThatDoesNotMatchThePartition()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            storage.CreateStore().EnqueueAsync(Guid.NewGuid(), observation));

        Assert.Equal(0, storage.DownloadCount);
    }

    [Fact]
    public async Task EnqueueAsync_WhenUploadFails_DoesNotAcknowledgeAndCanRetrySameIdentity()
    {
        var storage = new InMemoryBlob { UploadFailuresRemaining = 1 };
        var observation = CreateJoin();
        var serverId = observation.ServerId;
        var store = storage.CreateStore();

        await Assert.ThrowsAsync<RequestFailedException>(() => store.EnqueueAsync(serverId, observation));
        Assert.Empty(await store.GetPendingAsync(serverId));

        await store.EnqueueAsync(serverId, observation);
        Assert.Equal(observation, Assert.Single(await store.GetPendingAsync(serverId)));
    }

    [Fact]
    public async Task AcknowledgeAsync_WhenUploadFails_LeavesEventPendingForRetry()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        var serverId = observation.ServerId;
        var store = storage.CreateStore();
        await store.EnqueueAsync(serverId, observation);
        storage.UploadFailuresRemaining = 1;

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            store.AcknowledgeAsync(serverId, observation.EventId));

        Assert.Equal(observation.EventId, Assert.Single(await store.GetPendingAsync(serverId)).EventId);
        Assert.True(await store.AcknowledgeAsync(serverId, observation.EventId));
        Assert.Empty(await store.GetPendingAsync(serverId));
    }

    [Fact]
    public async Task EnqueueAsync_StoresOnlyApprovedPlaytimeFields()
    {
        var storage = new InMemoryBlob();
        var observation = CreateJoin();
        await storage.CreateStore().EnqueueAsync(observation.ServerId, observation);

        var json = Encoding.UTF8.GetString(storage.Content!);
        using var document = JsonDocument.Parse(json);
        var savedObservation = document.RootElement.GetProperty("events")[0];
        var propertyNames = savedObservation.EnumerateObject().Select(property => property.Name).ToHashSet();

        Assert.Contains("eventId", propertyNames);
        Assert.Contains("serverId", propertyNames);
        Assert.Contains("gameType", propertyNames);
        Assert.Contains("eventGeneratedUtc", propertyNames);
        Assert.Contains("eventPublishedUtc", propertyNames);
        Assert.Contains("eventUtc", propertyNames);
        Assert.Contains("sequenceId", propertyNames);
        Assert.Contains("producerSequence", propertyNames);
        Assert.Contains("gameIdentifier", propertyNames);
        Assert.Contains("username", propertyNames);
        Assert.Contains("slot", propertyNames);
        Assert.Contains("steamId", propertyNames);
        Assert.DoesNotContain("coverage", propertyNames);
        Assert.DoesNotContain("mapName", propertyNames);
        Assert.DoesNotContain("players", propertyNames);
        Assert.Equal(1, savedObservation.GetProperty("kind").GetInt32());
        Assert.Equal(1, savedObservation.GetProperty("timestampQuality").GetInt32());
        Assert.DoesNotContain("chat", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ip", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("legacy", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ipAddress", propertyNames);
    }

    [Fact]
    public async Task EnqueueAsync_PersistsRosterShapeWithMapCoverageAndStringIdentifiers()
    {
        var storage = new InMemoryBlob();
        var roster = CreateJoin() with
        {
            Kind = PlaytimeEventKind.Roster,
            TimestampQuality = PlaytimeTimestampQuality.RosterSnapshotTimestamp,
            Completeness = PlaytimeCompleteness.Partial,
            CorrelationId = null,
            GameIdentifier = null,
            Username = null,
            Slot = null,
            SteamId = null,
            MapName = "mp_test",
            Players =
            [
                new PlaytimeRosterPlayer("player-guid-not-numeric", "Player One", 3, null)
            ],
            Coverage = new PlaytimeCoverage
            {
                ProducerStartedUtc = DateTimeOffset.UtcNow.AddHours(-1),
                DroppedCapacity = (long)int.MaxValue + 1,
                DroppedAttempts = 2,
                DroppedAge = 3
            }
        };
        await storage.CreateStore().EnqueueAsync(roster.ServerId, roster);

        using var document = JsonDocument.Parse(storage.Content!);
        var saved = document.RootElement.GetProperty("events")[0];
        var player = Assert.Single(saved.GetProperty("players").EnumerateArray().ToArray());
        var coverage = saved.GetProperty("coverage");

        Assert.Equal("mp_test", saved.GetProperty("mapName").GetString());
        Assert.Equal("player-guid-not-numeric", player.GetProperty("gameIdentifier").GetString());
        Assert.Equal("Player One", player.GetProperty("username").GetString());
        Assert.Equal(3, player.GetProperty("slot").GetInt32());
        Assert.Equal(JsonValueKind.Null, player.GetProperty("isBot").ValueKind);
        Assert.Equal(4, saved.GetProperty("kind").GetInt32());
        Assert.Contains("epoch", saved.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("correlationId", saved.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("steamId", saved.EnumerateObject().Select(property => property.Name));
        Assert.True(coverage.TryGetProperty("producerStartedUtc", out _));
        Assert.True(coverage.TryGetProperty("droppedAge", out _));
        Assert.Equal((long)int.MaxValue + 1, coverage.GetProperty("droppedCapacity").GetInt64());
        Assert.Equal(2, coverage.GetProperty("droppedAttempts").GetInt32());
        Assert.Equal(3, coverage.GetProperty("droppedAge").GetInt32());
    }

    [Fact]
    public async Task GetPendingAsync_WhenServerLeaseIsNotOwned_DoesNotReadBlob()
    {
        var storage = new InMemoryBlob { LeaseOwned = false };

        await Assert.ThrowsAsync<PlaytimeOutboxOwnershipException>(() =>
            storage.CreateStore().GetPendingAsync(Guid.NewGuid()));

        Assert.Equal(0, storage.DownloadCount);
    }

    [Fact]
    public async Task EnqueueAsync_WhenLeaseChangesBeforeWrite_RejectsStaleOwner()
    {
        var storage = new InMemoryBlob { LoseLeaseOnUpload = true };
        var observation = CreateJoin();
        var store = storage.CreateStore();

        await Assert.ThrowsAsync<RequestFailedException>(() => store.EnqueueAsync(observation.ServerId, observation));

        Assert.Empty(storage.Content!);
    }

    [Fact]
    public async Task EnqueueAsync_WhenCapacityIsReached_ReportsBackpressureWithoutDiscardingEvents()
    {
        var storage = new InMemoryBlob();
        var store = storage.CreateStore();
        var serverId = Guid.NewGuid();
        var observations = Enumerable.Range(0, PlaytimeOutboxLimits.MaximumPendingEvents + 1)
            .Select(index => CreateJoin() with
            {
                EventId = Guid.NewGuid(),
                ServerId = serverId,
                SequenceId = index,
                Epoch = Guid.NewGuid()
            })
            .ToArray();

        foreach (var observation in observations.Take(PlaytimeOutboxLimits.MaximumPendingEvents))
        {
            await store.EnqueueAsync(serverId, observation);
        }

        var exception = await Assert.ThrowsAsync<PlaytimeOutboxCapacityExceededException>(() =>
            store.EnqueueAsync(serverId, observations[^1]));

        Assert.Equal(PlaytimeOutboxLimits.MaximumPendingEvents, exception.Capacity);
        Assert.Equal(PlaytimeOutboxLimits.MaximumPendingEvents,
            (await store.GetPendingAsync(serverId)).Length);
    }

    [Fact]
    public void Validate_RejectsUnboundedOrUnapprovedMapData()
    {
        var observation = CreateJoin() with
        {
            Kind = PlaytimeEventKind.Map,
            GameIdentifier = null,
            Username = null,
            Slot = null,
            SteamId = null,
            MapName = "mp_test\r\nsensitive-data"
        };

        Assert.Throws<ArgumentException>(observation.Validate);
    }

    [Fact]
    public void Validate_EnforcesSequenceEpochAndCorrelationRules()
    {
        var observation = CreateJoin();
        Assert.Throws<ArgumentException>((observation with
        {
            TimestampQuality = PlaytimeTimestampQuality.Unknown
        }).Validate);

        Assert.Throws<ArgumentException>((observation with
        {
            Epoch = null,
            ProducerSequence = 12
        }).Validate);

        (observation with
        {
            TimestampQuality = PlaytimeTimestampQuality.ParseTime,
            Freshness = PlaytimeFreshness.Unknown
        }).Validate();
        (observation with
        {
            TimestampQuality = PlaytimeTimestampQuality.ParseTime,
            Freshness = PlaytimeFreshness.Live
        }).Validate();

        (observation with { Epoch = null, ProducerSequence = null }).Validate();
        Assert.Throws<ArgumentException>((observation with { Completeness = PlaytimeCompleteness.Complete }).Validate);

        var map = observation with
        {
            Kind = PlaytimeEventKind.Map,
            ProducerSequence = null,
            Epoch = null,
            CorrelationId = Guid.NewGuid(),
            GameIdentifier = null,
            Username = null,
            Slot = null,
            SteamId = null,
            MapName = "mp_test"
        };
        Assert.Throws<ArgumentException>(map.Validate);

        Assert.Throws<ArgumentException>((observation with
        {
            Kind = PlaytimeEventKind.Leave,
            SteamId = "76561198000000000"
        }).Validate);
    }

    [Fact]
    public void Validate_RosterCoverageIsOptionalAndCannotBeginAfterEvent()
    {
        var roster = CreateJoin() with
        {
            Kind = PlaytimeEventKind.Roster,
            Completeness = PlaytimeCompleteness.Partial,
            Epoch = null,
            ProducerSequence = null,
            CorrelationId = null,
            GameIdentifier = null,
            Username = null,
            Slot = null,
            SteamId = null,
            MapName = null,
            Players = []
        };
        roster.Validate();
        var validCoverage = new PlaytimeCoverage
        {
            ProducerStartedUtc = roster.EventUtc,
            DroppedCapacity = 0,
            DroppedAttempts = 0,
            DroppedAge = 0
        };
        Assert.Throws<ArgumentException>((roster with { Coverage = validCoverage }).Validate);
        Assert.Throws<ArgumentException>((roster with
        {
            Epoch = Guid.NewGuid(),
            Coverage = validCoverage with { ProducerStartedUtc = default }
        }).Validate);
        Assert.Throws<ArgumentException>((roster with
        {
            Epoch = Guid.NewGuid(),
            Coverage = validCoverage with { DroppedCapacity = -1 }
        }).Validate);
        Assert.Throws<ArgumentException>((roster with
        {
            Epoch = Guid.NewGuid(),
            Coverage = validCoverage with { DroppedAttempts = -1 }
        }).Validate);
        Assert.Throws<ArgumentException>((roster with
        {
            Epoch = Guid.NewGuid(),
            Coverage = validCoverage with { DroppedAge = -1 }
        }).Validate);
        Assert.Throws<ArgumentException>((roster with
        {
            Epoch = Guid.NewGuid(),
            Coverage = new PlaytimeCoverage
            {
                ProducerStartedUtc = roster.EventUtc.AddSeconds(1),
                DroppedCapacity = 0,
                DroppedAttempts = 0,
                DroppedAge = 0
            }
        }).Validate);
    }

    private static PlaytimeObservation CreateJoin() => new()
    {
        EventId = Guid.NewGuid(),
        ServerId = Guid.NewGuid(),
        GameType = "CallOfDuty4x",
        EventGeneratedUtc = DateTimeOffset.UtcNow,
        EventPublishedUtc = DateTimeOffset.UtcNow,
        EventUtc = DateTimeOffset.UtcNow,
        SequenceId = 1,
        Kind = PlaytimeEventKind.Join,
        TimestampQuality = PlaytimeTimestampQuality.ParsedLogTimestamp,
        Epoch = Guid.NewGuid(),
        ProducerSequence = 1,
        CorrelationId = Guid.NewGuid(),
        Completeness = PlaytimeCompleteness.NotApplicable,
        Freshness = PlaytimeFreshness.Unknown,
        GameIdentifier = "player-guid-not-numeric",
        Username = "Player One",
        Slot = 3,
        SteamId = "76561198000000000"
    };

    private sealed class InMemoryBlob
    {
        private readonly Mock<BlobServiceClient> _serviceClient = new();
        private readonly Mock<BlobContainerClient> _container = new();
        private readonly Mock<BlobClient> _blob = new();
        public string? BlobPath { get; private set; }
        private ETag _etag = new("\"1\"");
        private int _version = 1;
        private int _downloadCount;

        public byte[]? Content { get; private set; } = [];
        public int UploadFailuresRemaining { get; set; }
        public int DownloadFailuresRemaining { get; set; }
        public bool FailAfterNextUpload { get; set; }
        public bool LeaseOwned { get; set; } = true;
        public bool LoseLeaseOnUpload { get; set; }
        public string CurrentLeaseId { get; private set; } = "lease-id";
        public int DownloadCount => _downloadCount;

        public InMemoryBlob()
        {
            _serviceClient.Setup(client => client.GetBlobContainerClient("server-locks"))
                .Returns(_container.Object);
            _container.Setup(client => client.GetBlobClient(It.IsAny<string>()))
                .Callback<string>(blobPath => BlobPath = blobPath)
                .Returns(_blob.Object);
            _blob.Setup(client => client.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    _downloadCount++;
                    if (DownloadFailuresRemaining > 0)
                    {
                        DownloadFailuresRemaining--;
                        throw new RequestFailedException(500, "InternalServerError");
                    }
                    if (Content is null)
                    {
                        throw new RequestFailedException(404, "BlobNotFound");
                    }

                    var details = BlobsModelFactory.BlobDownloadDetails(eTag: _etag);
                    var result = BlobsModelFactory.BlobDownloadResult(new BinaryData(Content), details);
                    return Task.FromResult(Response.FromValue(result, Mock.Of<Response>()));
                });
            _blob.Setup(client => client.UploadAsync(
                    It.IsAny<BinaryData>(),
                    It.IsAny<BlobUploadOptions>(),
                    It.IsAny<CancellationToken>()))
                .Returns((BinaryData data, BlobUploadOptions options, CancellationToken _) =>
                {
                    if (LoseLeaseOnUpload)
                    {
                        CurrentLeaseId = "replacement-lease";
                        LoseLeaseOnUpload = false;
                    }

                    if (options.Conditions?.LeaseId != CurrentLeaseId)
                    {
                        throw new RequestFailedException(412, "LeaseIdMismatchWithBlobOperation");
                    }

                    if (UploadFailuresRemaining > 0)
                    {
                        UploadFailuresRemaining--;
                        throw new RequestFailedException(500, "InternalServerError");
                    }

                    if (options.Conditions?.IfNoneMatch == ETag.All && Content is not null
                        || options.Conditions?.IfMatch is { } expected && Content is not null && expected != _etag)
                    {
                        throw new RequestFailedException(412, "ConditionNotMet");
                    }

                    Content = data.ToArray();
                    _version++;
                    _etag = new ETag($"\"{_version}\"");
                    if (FailAfterNextUpload)
                    {
                        FailAfterNextUpload = false;
                        throw new RequestFailedException(500, "ResponseLost");
                    }
                    return Task.FromResult(Mock.Of<Response<BlobContentInfo>>());
                });
        }

        public BlobPlaytimeOutbox CreateStore()
        {
            var serverLock = new Mock<IServerLock>();
            serverLock.Setup(lockClient => lockClient.RenewAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => LeaseOwned);
            var leaseId = "lease-id";
            serverLock.Setup(lockClient => lockClient.TryGetLeaseId(It.IsAny<Guid>(), out leaseId))
                .Returns(true);
            return new BlobPlaytimeOutbox(_serviceClient.Object, serverLock.Object);
        }

        public void VerifyOutboxPath(Guid serverId)
        {
            _serviceClient.Verify(client => client.GetBlobContainerClient("server-locks"), Times.Once);
            _container.Verify(client => client.GetBlobClient($"{serverId}.lock"), Times.Once);
            Assert.Equal($"{serverId}.lock", BlobPath);
        }
    }
}
