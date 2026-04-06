namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Monitors a game server's ban file for new untagged entries.
/// </summary>
public interface IBanFileWatcher
{
    /// <summary>
    /// Check the ban file(s) for changes. Downloads and parses if size changed.
    /// Returns new untagged ban entries, or empty if no changes.
    /// </summary>
    Task<BanFileCheckResult> CheckAsync(Agents.ServerContext context, CancellationToken ct = default);

    /// <summary>
    /// Updates the BanFileMonitor(s) with new file sizes after successful event publish.
    /// Call this only after the event has been successfully published to prevent ban loss.
    /// </summary>
    Task AcknowledgeAsync(IReadOnlyList<MonitorUpdate> updates, CancellationToken ct = default);
}

/// <summary>
/// Result of a ban file check, including new bans and metadata needed
/// to update the BanFileMonitor after successful publish.
/// </summary>
public sealed record BanFileCheckResult
{
    public static readonly BanFileCheckResult Empty = new()
    {
        NewBans = [],
        MonitorUpdates = []
    };

    public required IReadOnlyList<DetectedBanEntry> NewBans { get; init; }
    public required IReadOnlyList<MonitorUpdate> MonitorUpdates { get; init; }
}

/// <summary>
/// Captures the new file size for a monitor so it can be persisted after publish.
/// </summary>
public sealed record MonitorUpdate
{
    public required Guid BanFileMonitorId { get; init; }
    public required long NewFileSize { get; init; }
}

/// <summary>
/// A single ban entry parsed from the ban file.
/// </summary>
public sealed record DetectedBanEntry
{
    public required string PlayerGuid { get; init; }
    public required string PlayerName { get; init; }
}
