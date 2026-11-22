using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

/// <summary>
/// Establishes an EOF cursor and records gaps. Until source freshness is verified,
/// newly read records are not converted into playtime observations.
/// </summary>
internal sealed class PlaytimeSourceTracker(IPlaytimeOutbox outbox, Guid serverId, ILogger? logger = null)
{
    private PlaytimeSourceCursor? _cursor;
    private long _continuityVersion;
    private bool _paused;

    public async Task InitializeAsync(ILogTailer tailer, CancellationToken ct)
    {
        var path = tailer.CurrentFilePath ?? throw new InvalidOperationException("The log tailer has no file path.");
        var next = new PlaytimeSourceCursor(Guid.NewGuid(), path, tailer.InitialEndOffset);
        var previous = await outbox.GetSourceCursorAsync(serverId, ct).ConfigureAwait(false);
        if (previous is null)
        {
            await outbox.CommitSourceBatchAsync(serverId, null, next, [], ct).ConfigureAwait(false);
        }
        else
        {
            await outbox.RecordGapAndResetCursorAsync(serverId, previous, next, PlaytimeGapReason.Restart, ct).ConfigureAwait(false);
            logger?.LogWarning("Playtime source coverage gap for server {ServerId}: {Reason}", serverId, PlaytimeGapReason.Restart);
        }

        _cursor = next;
        _continuityVersion = tailer.ContinuityVersion;
        _paused = false;
    }

    public async Task ObservePollAsync(ILogTailer tailer, CancellationToken ct)
    {
        if (_paused)
        {
            return;
        }

        var cursor = _cursor ?? throw new InvalidOperationException("The playtime source has not been initialized.");
        PlaytimeGapReason? reason = tailer.ContinuityVersion != _continuityVersion
            ? PlaytimeGapReason.ContinuityLost
            : tailer.CurrentOffset > cursor.Offset ? PlaytimeGapReason.UnverifiedFreshness : null;
        if (reason is not null)
        {
            await outbox.PauseSourceAsync(serverId, cursor, reason.Value, ct).ConfigureAwait(false);
            logger?.LogWarning("Playtime source coverage gap for server {ServerId}: {Reason}", serverId, reason.Value);
            _paused = true;
        }
    }

    public async Task ObservePollFailureAsync(CancellationToken ct)
    {
        if (_paused)
        {
            return;
        }

        var cursor = _cursor ?? throw new InvalidOperationException("The playtime source has not been initialized.");
        await outbox.PauseSourceAsync(serverId, cursor, PlaytimeGapReason.PollFailure, ct).ConfigureAwait(false);
        logger?.LogWarning("Playtime source coverage gap for server {ServerId}: {Reason}", serverId, PlaytimeGapReason.PollFailure);
        _paused = true;
    }
}
