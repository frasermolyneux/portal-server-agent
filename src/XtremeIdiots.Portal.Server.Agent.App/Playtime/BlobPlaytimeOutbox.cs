using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

/// <summary>
/// Stores a bounded, per-server playtime-only outbox in an atomically replaced blob.
/// The outbox shares the server lock blob, so every write is fenced by that blob's
/// lease as well as an ETag. Reads are preceded by a lease renewal.
/// </summary>
internal sealed class BlobPlaytimeOutbox : IPlaytimeOutbox
{
    private const int SchemaVersion = 1;
    private const int MaximumWriteAttempts = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly BlobContainerClient _container;
    private readonly IServerLock _serverLock;
    private readonly ConcurrentDictionary<Guid, string> _boundLeaseIds = new();

    public BlobPlaytimeOutbox(BlobServiceClient blobServiceClient, IServerLock serverLock)
    {
        ArgumentNullException.ThrowIfNull(blobServiceClient);
        _serverLock = serverLock ?? throw new ArgumentNullException(nameof(serverLock));
        _container = blobServiceClient.GetBlobContainerClient("server-locks");
    }

    public async Task EnqueueAsync(Guid serverId, PlaytimeObservation observation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.Validate();
        if (observation.ServerId != serverId)
        {
            throw new ArgumentException("The observation server ID must match its outbox partition.", nameof(observation));
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);

        await UpdateAsync(serverId, state =>
        {
            var existing = state.Events.FirstOrDefault(item => item.EventId == observation.EventId);
            if (existing is not null)
            {
                if (!HasSameSerializedContent(existing, observation))
                {
                    throw new InvalidOperationException("An event ID cannot be reused with different playtime metadata.");
                }

                return state;
            }

            if (state.Events.Length >= PlaytimeOutboxLimits.MaximumPendingEvents)
            {
                throw new PlaytimeOutboxCapacityExceededException(PlaytimeOutboxLimits.MaximumPendingEvents);
            }

            return state with { Events = state.Events.Add(observation) };
        }, ct).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<PlaytimeObservation>> GetPendingAsync(Guid serverId, CancellationToken ct = default)
    {
        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        var state = await ReadStateAsync(serverId, ct).ConfigureAwait(false);
        return state.Events;
    }

    public async Task<PlaytimeObservation?> PreparePublishAsync(
        Guid serverId, Guid eventId, DateTimeOffset publishedUtc, CancellationToken ct = default)
    {
        if (eventId == Guid.Empty || publishedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("An event ID and UTC publication timestamp are required.");
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        PlaytimeObservation? prepared = null;
        await UpdateAsync(serverId, state =>
        {
            var index = -1;
            for (var i = 0; i < state.Events.Length; i++)
            {
                if (state.Events[i].EventId == eventId)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                prepared = null;
                return state;
            }

            var existing = state.Events[index];
            prepared = existing.EventPublishedUtc is null
                ? existing with { EventPublishedUtc = publishedUtc }
                : existing;
            prepared.Validate();
            return ReferenceEquals(prepared, existing)
                ? state
                : state with { Events = state.Events.SetItem(index, prepared) };
        }, ct).ConfigureAwait(false);
        return prepared;
    }

    public async Task<PlaytimeSourceCursor?> GetSourceCursorAsync(Guid serverId, CancellationToken ct = default)
    {
        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        return (await ReadStateAsync(serverId, ct).ConfigureAwait(false)).SourceCursor;
    }

    public async Task CommitSourceBatchAsync(
        Guid serverId,
        PlaytimeSourceCursor? expectedCursor,
        PlaytimeSourceCursor nextCursor,
        IReadOnlyList<PlaytimeObservation> observations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nextCursor);
        ArgumentNullException.ThrowIfNull(observations);
        ValidateCursor(nextCursor);
        if (expectedCursor is not null)
        {
            ValidateCursor(expectedCursor);
            if (expectedCursor.SourceId != nextCursor.SourceId || expectedCursor.FilePath != nextCursor.FilePath ||
                nextCursor.Offset < expectedCursor.Offset)
            {
                throw new ArgumentException("A batch must advance the same source cursor.", nameof(nextCursor));
            }
        }
        else if (observations.Count != 0)
        {
            throw new ArgumentException("A new source must be initialized without historical observations.", nameof(observations));
        }

        foreach (var observation in observations)
        {
            ArgumentNullException.ThrowIfNull(observation);
            observation.Validate();
            if (observation.ServerId != serverId)
            {
                throw new ArgumentException("Observation server ID must match its outbox partition.", nameof(observations));
            }
        }

        if (observations.Select(o => o.EventId).Distinct().Count() != observations.Count)
        {
            throw new ArgumentException("Batch observation IDs must be unique.", nameof(observations));
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        await UpdateAsync(serverId, state =>
        {
            if (state.SourceCursor != expectedCursor)
            {
                if (state.SourceCursor == nextCursor && observations.All(item =>
                    state.Events.Any(existing => existing.EventId == item.EventId &&
                        HasSameSerializedContent(existing, item))))
                {
                    return state;
                }

                throw new InvalidOperationException("The playtime source cursor changed; the batch cannot be committed.");
            }

            if (state.SourcePaused)
            {
                throw new InvalidOperationException("The playtime source is paused after a coverage gap.");
            }

            if (state.Events.Length + observations.Count > PlaytimeOutboxLimits.MaximumPendingEvents)
            {
                throw new PlaytimeOutboxCapacityExceededException(PlaytimeOutboxLimits.MaximumPendingEvents);
            }

            if (observations.Any(item => state.Events.Any(existing => existing.EventId == item.EventId)))
            {
                throw new InvalidOperationException("A batch observation ID already exists in the outbox.");
            }

            return state with { SourceCursor = nextCursor, Events = state.Events.AddRange(observations) };
        }, ct).ConfigureAwait(false);
    }

    private static void ValidateCursor(PlaytimeSourceCursor cursor)
    {
        if (cursor.SourceId == Guid.Empty || string.IsNullOrWhiteSpace(cursor.FilePath) || cursor.Offset < 0)
        {
            throw new ArgumentException("The playtime source cursor is invalid.", nameof(cursor));
        }
    }

    public async Task<PlaytimeGapSummary> GetGapSummaryAsync(Guid serverId, CancellationToken ct = default)
    {
        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        var state = await ReadStateAsync(serverId, ct).ConfigureAwait(false);
        return new PlaytimeGapSummary(state.GapCount, state.LastGapReason);
    }

    public async Task RecordGapAsync(Guid serverId, PlaytimeGapReason reason, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        await UpdateAsync(serverId, state => state with
        {
            GapCount = checked(state.GapCount + 1),
            LastGapReason = reason
        }, ct).ConfigureAwait(false);
    }

    public async Task<PlaytimeCoverageState> GetOrCreateCoverageAsync(
        Guid serverId, Guid epoch, DateTimeOffset startedUtc, CancellationToken ct = default)
    {
        if (epoch == Guid.Empty || startedUtc == default || startedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A nonempty epoch and UTC producer start are required.");
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        PlaytimeCoverageState? coverage = null;
        await UpdateAsync(serverId, state =>
        {
            coverage = state.CoverageState ?? new PlaytimeCoverageState(epoch, new PlaytimeCoverage
            {
                ProducerStartedUtc = startedUtc,
                DroppedCapacity = 0,
                DroppedAttempts = 0,
                DroppedAge = 0
            });
            return state.CoverageState is null ? state with { CoverageState = coverage } : state;
        }, ct).ConfigureAwait(false);
        return coverage!;
    }

    public async Task RecordCoverageDropAsync(
        Guid serverId, Guid epoch, PlaytimeDropReason reason, long cumulativeCount, CancellationToken ct = default)
    {
        if (epoch == Guid.Empty || cumulativeCount < 0 || !Enum.IsDefined(reason))
        {
            throw new ArgumentException("A valid epoch, drop count and reason are required.");
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        await UpdateAsync(serverId, state =>
        {
            var current = state.CoverageState;
            if (current is null || current.Epoch != epoch)
            {
                throw new InvalidOperationException("The coverage epoch does not match the outbox.");
            }
            var previousCount = reason switch
            {
                PlaytimeDropReason.Capacity => current.Coverage.DroppedCapacity,
                PlaytimeDropReason.Attempts => current.Coverage.DroppedAttempts,
                PlaytimeDropReason.Age => current.Coverage.DroppedAge,
                _ => throw new ArgumentOutOfRangeException(nameof(reason))
            };
            if (cumulativeCount <= previousCount)
            {
                return state;
            }

            var counters = reason switch
            {
                PlaytimeDropReason.Capacity => current.Coverage with { DroppedCapacity = cumulativeCount },
                PlaytimeDropReason.Attempts => current.Coverage with { DroppedAttempts = cumulativeCount },
                PlaytimeDropReason.Age => current.Coverage with { DroppedAge = cumulativeCount },
                _ => throw new ArgumentOutOfRangeException(nameof(reason))
            };
            return state with
            {
                CoverageState = current with { Coverage = counters },
                GapCount = checked(state.GapCount + (cumulativeCount - previousCount)),
                LastGapReason = reason switch
                {
                    PlaytimeDropReason.Capacity => PlaytimeGapReason.Backlog,
                    PlaytimeDropReason.Attempts => PlaytimeGapReason.DeliveryAttemptsExceeded,
                    PlaytimeDropReason.Age => PlaytimeGapReason.ObservationExpired,
                    _ => throw new ArgumentOutOfRangeException(nameof(reason))
                }
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task RecordGapAndResetCursorAsync(
        Guid serverId,
        PlaytimeSourceCursor expectedCursor,
        PlaytimeSourceCursor nextCursor,
        PlaytimeGapReason reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedCursor);
        ArgumentNullException.ThrowIfNull(nextCursor);
        ValidateCursor(expectedCursor);
        ValidateCursor(nextCursor);
        if (nextCursor.SourceId == expectedCursor.SourceId || !Enum.IsDefined(reason))
        {
            throw new ArgumentException("A gap requires a new source generation and valid reason.");
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        await UpdateAsync(serverId, state =>
        {
            if (state.SourceCursor != expectedCursor)
            {
                throw new InvalidOperationException("The playtime source cursor changed; the gap cannot be committed.");
            }

            return state with
            {
                SourceCursor = nextCursor,
                SourcePaused = false,
                GapCount = checked(state.GapCount + 1),
                LastGapReason = reason
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task PauseSourceAsync(Guid serverId, PlaytimeSourceCursor expectedCursor, PlaytimeGapReason reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedCursor);
        ValidateCursor(expectedCursor);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        await UpdateAsync(serverId, state =>
        {
            if (state.SourceCursor != expectedCursor)
            {
                throw new InvalidOperationException("The playtime source cursor changed; the gap cannot be committed.");
            }
            if (state.SourcePaused)
            {
                return state;
            }

            return state with
            {
                SourcePaused = true,
                GapCount = checked(state.GapCount + 1),
                LastGapReason = reason
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> AcknowledgeAsync(Guid serverId, Guid eventId, CancellationToken ct = default)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An event ID is required.", nameof(eventId));
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        var acknowledged = false;
        await UpdateAsync(serverId, state =>
        {
            acknowledged = false;
            var index = -1;
            for (var eventIndex = 0; eventIndex < state.Events.Length; eventIndex++)
            {
                if (state.Events[eventIndex].EventId == eventId)
                {
                    index = eventIndex;
                    break;
                }
            }
            if (index < 0)
            {
                return state;
            }

            acknowledged = true;
            return state with { Events = state.Events.RemoveAt(index) };
        }, ct).ConfigureAwait(false);
        return acknowledged;
    }

    public async Task<bool> DropExpiredAsync(Guid serverId, Guid eventId, DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        if (eventId == Guid.Empty || cutoffUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("An event ID and UTC cutoff are required.");
        }

        await EnsureLeaseOwnershipAsync(serverId, ct).ConfigureAwait(false);
        var dropped = false;
        await UpdateAsync(serverId, state =>
        {
            dropped = false;
            var index = -1;
            for (var i = 0; i < state.Events.Length; i++)
            {
                if (state.Events[i].EventId == eventId)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0 || state.Events[index].EventUtc >= cutoffUtc)
            {
                return state;
            }

            var coverage = state.CoverageState;
            if (coverage is null || state.Events[index].Epoch != coverage.Epoch)
            {
                throw new InvalidOperationException("An expired observation cannot be dropped without matching coverage state.");
            }
            dropped = true;
            return state with
            {
                Events = state.Events.RemoveAt(index),
                CoverageState = coverage with
                {
                    Coverage = coverage.Coverage with { DroppedAge = checked(coverage.Coverage.DroppedAge + 1) }
                },
                GapCount = checked(state.GapCount + 1),
                LastGapReason = PlaytimeGapReason.ObservationExpired
            };
        }, ct).ConfigureAwait(false);
        return dropped;
    }

    private async Task EnsureLeaseOwnershipAsync(Guid serverId, CancellationToken ct)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("A server ID is required.", nameof(serverId));
        }

        if (!_serverLock.TryGetLeaseId(serverId, out var currentLeaseId))
        {
            throw new PlaytimeOutboxOwnershipException();
        }

        var boundLeaseId = _boundLeaseIds.GetOrAdd(serverId, currentLeaseId);
        if (!string.Equals(boundLeaseId, currentLeaseId, StringComparison.Ordinal) ||
            !await _serverLock.RenewAsync(serverId, ct).ConfigureAwait(false) ||
            !_serverLock.TryGetLeaseId(serverId, out currentLeaseId) ||
            !string.Equals(boundLeaseId, currentLeaseId, StringComparison.Ordinal))
        {
            throw new PlaytimeOutboxOwnershipException();
        }
    }

    private async Task UpdateAsync(
        Guid serverId,
        Func<OutboxState, OutboxState> update,
        CancellationToken ct)
    {
        var blob = GetBlob(serverId);
        for (var attempt = 0; attempt < MaximumWriteAttempts; attempt++)
        {
            var current = await ReadStateAsync(blob, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The acquired server lock blob is missing.");
            var next = update(current.State);
            if (ReferenceEquals(next, current.State))
            {
                return;
            }

            if (!_boundLeaseIds.TryGetValue(serverId, out var leaseId))
            {
                throw new PlaytimeOutboxOwnershipException();
            }

            var conditions = new BlobRequestConditions { IfMatch = current.ETag, LeaseId = leaseId };
            var options = new BlobUploadOptions
            {
                Conditions = conditions,
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
            };

            try
            {
                await blob.UploadAsync(
                    BinaryData.FromString(JsonSerializer.Serialize(next, JsonOptions)),
                    options,
                    ct).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 && attempt < MaximumWriteAttempts - 1)
            {
                // Another writer changed the state; reload before applying the operation again.
            }
        }

        throw new RequestFailedException(412, "Playtime outbox changed too frequently to update safely.");
    }

    private async Task<OutboxState> ReadStateAsync(Guid serverId, CancellationToken ct)
        => await ReadStateAsync(GetBlob(serverId), ct).ConfigureAwait(false) is { } stored
            ? stored.State
            : throw new InvalidOperationException("The acquired server lock blob is missing.");

    private static async Task<StoredOutbox?> ReadStateAsync(BlobClient blob, CancellationToken ct)
    {
        try
        {
            var response = await blob.DownloadContentAsync(ct).ConfigureAwait(false);
            if (response.Value.Content.ToMemory().IsEmpty)
            {
                return new StoredOutbox(OutboxState.Empty, response.Value.Details.ETag);
            }
            var state = JsonSerializer.Deserialize<OutboxState>(response.Value.Content, JsonOptions)
                ?? throw new JsonException("The playtime outbox blob contained a null state.");
            if (state.SchemaVersion != SchemaVersion
                || state.Events.IsDefault
                || state.Events.Any(observation => observation is null)
                || state.Events.Length > PlaytimeOutboxLimits.MaximumPendingEvents
                || state.GapCount < 0
                || state.LastGapReason is { } reason && !Enum.IsDefined(reason))
            {
                throw new JsonException("The playtime outbox blob has an unsupported or invalid state.");
            }

            if (state.CoverageState is { } coverage)
            {
                if (coverage.Epoch == Guid.Empty || coverage.Coverage is null)
                {
                    throw new JsonException("The playtime coverage state is invalid.");
                }
                coverage.Coverage.Validate(DateTimeOffset.MaxValue);
            }

            foreach (var observation in state.Events)
            {
                observation.Validate();
            }
            if (state.SourceCursor is not null)
            {
                ValidateCursor(state.SourceCursor);
            }

            return new StoredOutbox(state, response.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private BlobClient GetBlob(Guid serverId) => _container.GetBlobClient($"{serverId}.lock");

    private static bool HasSameSerializedContent(PlaytimeObservation left, PlaytimeObservation right)
        => JsonSerializer.Serialize(left, JsonOptions) == JsonSerializer.Serialize(right, JsonOptions);

    private sealed record OutboxState
    {
        public required int SchemaVersion { get; init; }
        public required ImmutableArray<PlaytimeObservation> Events { get; init; }
        public PlaytimeSourceCursor? SourceCursor { get; init; }
        public bool SourcePaused { get; init; }
        public long GapCount { get; init; }
        public PlaytimeGapReason? LastGapReason { get; init; }
        public PlaytimeCoverageState? CoverageState { get; init; }

        public static OutboxState Empty { get; } = new()
        {
            SchemaVersion = BlobPlaytimeOutbox.SchemaVersion,
            Events = []
        };
    }

    private sealed record StoredOutbox(OutboxState State, ETag ETag);
}
