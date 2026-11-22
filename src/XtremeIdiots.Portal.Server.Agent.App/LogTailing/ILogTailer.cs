namespace XtremeIdiots.Portal.Server.Agent.App.LogTailing;

/// <summary>
/// Polls a remote log file and returns new lines since the last poll.
/// Implementations must handle reconnection, log rotation, and partial lines.
/// </summary>
public interface ILogTailer : IAsyncDisposable
{
    /// <summary>
    /// Connect to the remote server and prepare for tailing.
    /// If <paramref name="startOffset"/> is provided and the file size is at least that large,
    /// resumes from that offset; otherwise starts from the end of the file.
    /// </summary>
    Task ConnectAsync(FileTransportTailerConfig config, long? startOffset = null, CancellationToken ct = default);

    /// <summary>
    /// Backwards-compatible overload for legacy transport-shaped call sites.
    /// </summary>
    Task ConnectAsync(FtpTailerConfig config, long? startOffset = null, CancellationToken ct = default)
        => ConnectAsync(config.ToTransportConfig(), startOffset, ct);

    /// <summary>
    /// Poll for new lines. Returns empty if no new data.
    /// Handles log rotation (file size decreased) by resetting to the beginning.
    /// </summary>
    Task<IReadOnlyList<string>> PollAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether the tailer is currently connected to the FTP server.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// The byte offset after the last complete line read. Incomplete lines are
    /// excluded so they can be reread after a restart.
    /// </summary>
    long CurrentOffset { get; }

    /// <summary>
    /// File size observed at initial connection. This is not evidence that the game flushed its log.
    /// </summary>
    long InitialEndOffset { get; }

    /// <summary>
    /// Changes when a poll detects a failure, reconnect, truncation or suspected rotation.
    /// An unchanged value does not prove that the file has not been replaced.
    /// </summary>
    long ContinuityVersion { get; }

    /// <summary>
    /// The remote file path currently being tailed, or null if not connected.
    /// </summary>
    string? CurrentFilePath { get; }
}
