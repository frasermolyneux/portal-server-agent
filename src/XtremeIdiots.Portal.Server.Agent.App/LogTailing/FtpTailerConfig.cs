namespace XtremeIdiots.Portal.Server.Agent.App.LogTailing;

/// <summary>
/// Configuration for connecting to an FTP server and tailing a log file.
/// </summary>
public sealed record FtpTailerConfig
{
    /// <summary>
    /// The FTP server hostname or IP address.
    /// </summary>
    public required string Hostname { get; init; }

    /// <summary>
    /// The FTP server port.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// The FTP username for authentication.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// The FTP password for authentication.
    /// </summary>
    public required string Password { get; init; }

    /// <summary>
    /// The remote path to the log file to tail.
    /// </summary>
    public required string FilePath { get; init; }
}
