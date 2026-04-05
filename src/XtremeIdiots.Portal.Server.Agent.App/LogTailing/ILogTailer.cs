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
    Task ConnectAsync(FtpTailerConfig config, long? startOffset = null, CancellationToken ct = default);

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
    /// The current byte offset in the tailed file (i.e. how far we have read).
    /// </summary>
    long CurrentOffset { get; }

    /// <summary>
    /// The remote file path currently being tailed, or null if not connected.
    /// </summary>
    string? CurrentFilePath { get; }
}
