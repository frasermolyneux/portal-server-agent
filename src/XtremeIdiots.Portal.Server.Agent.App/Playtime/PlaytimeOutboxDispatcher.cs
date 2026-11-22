using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

internal sealed class PlaytimeOutboxDispatcher(
    IPlaytimeOutbox outbox,
    Func<PlaytimeObservation, CancellationToken, Task> send,
    ILogger? logger = null)
{
    private static readonly TimeSpan MaximumSubmissionAge = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);

    public async Task DispatchAsync(Guid serverId, CancellationToken ct = default)
    {
        await _dispatchLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pending = await outbox.GetPendingAsync(serverId, ct).ConfigureAwait(false);
            foreach (var observation in pending)
            {
                ct.ThrowIfCancellationRequested();
                var cutoff = DateTimeOffset.UtcNow - MaximumSubmissionAge;
                if (observation.EventUtc < cutoff)
                {
                    if (await outbox.DropExpiredAsync(serverId, observation.EventId, cutoff, ct).ConfigureAwait(false))
                    {
                        logger?.LogWarning("Expired playtime observation {ObservationId} was dropped for server {ServerId}",
                            observation.EventId, serverId);
                    }
                    continue;
                }
                var prepared = await outbox.PreparePublishAsync(serverId, observation.EventId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                if (prepared is null)
                {
                    continue;
                }

                await send(prepared, ct).ConfigureAwait(false);
                if (!await outbox.AcknowledgeAsync(serverId, observation.EventId, ct).ConfigureAwait(false))
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
}
