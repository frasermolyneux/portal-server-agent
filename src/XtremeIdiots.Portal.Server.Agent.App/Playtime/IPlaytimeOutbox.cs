using System.Collections.Immutable;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

internal interface IPlaytimeOutbox
{
    Task EnqueueAsync(Guid serverId, PlaytimeObservation observation, CancellationToken ct = default);

    Task<ImmutableArray<PlaytimeObservation>> GetPendingAsync(Guid serverId, CancellationToken ct = default);

    Task<PlaytimeObservation?> PreparePublishAsync(Guid serverId, Guid eventId, DateTimeOffset publishedUtc, CancellationToken ct = default);

    Task<bool> AcknowledgeAsync(Guid serverId, Guid eventId, CancellationToken ct = default);

    Task<bool> DropExpiredAsync(Guid serverId, Guid eventId, DateTimeOffset cutoffUtc, CancellationToken ct = default);

    Task<PlaytimeSourceCursor?> GetSourceCursorAsync(Guid serverId, CancellationToken ct = default);

    Task CommitSourceBatchAsync(
        Guid serverId,
        PlaytimeSourceCursor? expectedCursor,
        PlaytimeSourceCursor nextCursor,
        IReadOnlyList<PlaytimeObservation> observations,
        CancellationToken ct = default);

    Task RecordGapAndResetCursorAsync(
        Guid serverId,
        PlaytimeSourceCursor expectedCursor,
        PlaytimeSourceCursor nextCursor,
        PlaytimeGapReason reason,
        CancellationToken ct = default);

    Task<PlaytimeGapSummary> GetGapSummaryAsync(Guid serverId, CancellationToken ct = default);

    Task RecordGapAsync(Guid serverId, PlaytimeGapReason reason, CancellationToken ct = default);

    Task<PlaytimeCoverageState> GetOrCreateCoverageAsync(
        Guid serverId, Guid epoch, DateTimeOffset startedUtc, CancellationToken ct = default);

    Task RecordCoverageDropAsync(
        Guid serverId, Guid epoch, PlaytimeDropReason reason, long cumulativeCount, CancellationToken ct = default);

    Task PauseSourceAsync(
        Guid serverId,
        PlaytimeSourceCursor expectedCursor,
        PlaytimeGapReason reason,
        CancellationToken ct = default);
}

internal sealed record PlaytimeSourceCursor(Guid SourceId, string FilePath, long Offset);

internal enum PlaytimeGapReason
{
    Restart,
    Rotation,
    PollFailure,
    Backlog,
    Capacity,
    ContinuityLost,
    UnverifiedFreshness,
    RosterUnavailable,
    RosterStale,
    InvalidRoster,
    DeliveryAttemptsExceeded,
    ObservationExpired
}

internal sealed record PlaytimeGapSummary(long Count, PlaytimeGapReason? LastReason);

internal enum PlaytimeDropReason
{
    Capacity,
    Attempts,
    Age
}

internal sealed record PlaytimeCoverageState(Guid Epoch, PlaytimeCoverage Coverage);

internal sealed class PlaytimeOutboxCapacityExceededException(int capacity)
    : InvalidOperationException($"The playtime outbox is full (capacity {capacity}); the incoming event was not persisted.")
{
    public int Capacity { get; } = capacity;
}

internal sealed class PlaytimeOutboxOwnershipException()
    : InvalidOperationException("The server lease could not be renewed; the playtime outbox operation was not completed.");
