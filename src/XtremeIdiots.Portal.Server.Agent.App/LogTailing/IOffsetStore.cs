namespace XtremeIdiots.Portal.Server.Agent.App.LogTailing;

/// <summary>
/// Persists and retrieves FTP tailing offsets for resumable log tailing.
/// </summary>
public interface IOffsetStore
{
    /// <summary>
    /// Save the current offset for a server. Non-blocking — failures are logged but don't interrupt tailing.
    /// </summary>
    Task SaveOffsetAsync(Guid serverId, long offset, string filePath, CancellationToken ct = default);

    /// <summary>
    /// Retrieve the last saved offset for a server. Returns null if no offset saved.
    /// </summary>
    Task<SavedOffset?> GetOffsetAsync(Guid serverId, CancellationToken ct = default);
}

/// <summary>
/// Represents a previously saved tailing offset.
/// </summary>
public sealed record SavedOffset
{
    public required long Offset { get; init; }
    public required string FilePath { get; init; }
    public required DateTime SavedAtUtc { get; init; }
}
