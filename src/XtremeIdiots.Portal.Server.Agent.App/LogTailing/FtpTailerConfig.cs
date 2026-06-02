namespace XtremeIdiots.Portal.Server.Agent.App.LogTailing;

/// <summary>
/// Configuration for connecting to a remote file transport and tailing a log file.
/// </summary>
public sealed record FileTransportTailerConfig
{
    /// <summary>
    /// The transport type (ftp or sftp).
    /// </summary>
    public required string TransportType { get; init; }

    /// <summary>
    /// The remote server hostname or IP address.
    /// </summary>
    public required string Hostname { get; init; }

    /// <summary>
    /// The remote server port.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// The username for authentication.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// The password for authentication.
    /// </summary>
    public required string Password { get; init; }

    /// <summary>
    /// The expected host key fingerprint for SFTP transport.
    /// </summary>
    public string? HostKeyFingerprint { get; init; }

    /// <summary>
    /// The remote path to the log file to tail.
    /// </summary>
    public required string FilePath { get; init; }
}

/// <summary>
/// Backwards-compatible FTP config shape retained for tests and compatibility call sites.
/// </summary>
public sealed record FtpTailerConfig
{
    public required string Hostname { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string FilePath { get; init; }

    public FileTransportTailerConfig ToTransportConfig() => new()
    {
        TransportType = "ftp",
        Hostname = Hostname,
        Port = Port,
        Username = Username,
        Password = Password,
        FilePath = FilePath
    };
}
