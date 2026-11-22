using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

internal sealed class PlaytimeOutboxDispatcher(
    IPlaytimeOutbox outbox,
    Func<PlaytimeObservation, CancellationToken, Task> send,
    ILogger? logger = null,
    TimeSpan? attemptTimeout = null)
{
    private static readonly TimeSpan MaximumSubmissionAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);
    private readonly TimeSpan _attemptTimeout = ValidateTimeout(attemptTimeout ?? DefaultAttemptTimeout);

    public async Task DispatchAsync(Guid serverId, CancellationToken ct = default)
    {
        await _dispatchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pending = await outbox.GetPendingAsync(serverId, ct).ConfigureAwait(false);
            foreach (var observation in pending)
            {
                ct.ThrowIfCancellationRequested();
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(_attemptTimeout);
                var attemptToken = attemptCts.Token;
                var cutoff = DateTimeOffset.UtcNow - MaximumSubmissionAge;
                if (observation.EventUtc < cutoff)
                {
                    if (await outbox.DropExpiredAsync(serverId, observation.EventId, cutoff, attemptToken).ConfigureAwait(false))
                    {
                        logger?.LogWarning("Expired playtime observation {ObservationId} was dropped for server {ServerId}",
                            observation.EventId, serverId);
                    }
                    continue;
                }
                var prepared = await outbox.PreparePublishAsync(serverId, observation.EventId, DateTimeOffset.UtcNow, attemptToken).ConfigureAwait(false);
                if (prepared is null)
                {
                    continue;
                }

                await send(prepared, attemptToken).ConfigureAwait(false);
                if (!await outbox.AcknowledgeAsync(serverId, observation.EventId, attemptToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The sent playtime observation was no longer pending.");
                }
            }
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout)
        => timeout > TimeSpan.Zero && timeout < TimeSpan.FromSeconds(30)
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout));
}
