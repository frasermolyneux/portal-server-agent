namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Monitors a game server's ban file for new untagged entries and propagates the
/// central regenerated blob back to the server. Owns the per-server
/// <c>BanFileMonitor</c> status row — upserts it directly, no manual creation by
/// admins.
/// </summary>
public interface IBanFileWatcher
{
    /// <summary>
    /// Resolve the current ban file path (per game type + live mod), check for new
    /// untagged bans, push the central blob if its ETag has changed, and upsert the
    /// <c>BanFileMonitor</c> status row with the check + push + count results.
    ///
    /// Returns any new bans for the agent to publish via Service Bus. The agent
    /// then calls <see cref="AcknowledgeImportAsync"/> after a successful publish
    /// so the import-status fields are persisted only after the events are durable.
    /// </summary>
    Task<BanFileCheckResult> CheckAsync(
        Agents.ServerContext context,
        string? liveMod,
        CancellationToken ct = default);

    /// <summary>
    /// Persists the import-status fields (LastImportUtc, LastImportBanCount,
    /// LastImportSampleNames) after the agent has successfully published the
    /// detected bans. Skipped fields are left untouched on the row.
    /// </summary>
    Task AcknowledgeImportAsync(
        Guid serverId,
        ImportAcknowledgment acknowledgment,
        CancellationToken ct = default);
}

/// <summary>
/// Result of a single ban file check. <see cref="NewBans"/> is the only payload
/// the agent acts on; <see cref="ImportAcknowledgment"/> is opaque and round-tripped
/// back to <see cref="IBanFileWatcher.AcknowledgeImportAsync"/> after publish.
/// </summary>
public sealed record BanFileCheckResult
{
    public static readonly BanFileCheckResult Empty = new()
    {
        NewBans = [],
        ImportAcknowledgment = null
    };

    public required IReadOnlyList<DetectedBanEntry> NewBans { get; init; }

    /// <summary>
    /// Non-null when bans were detected this cycle. Pass back to
    /// <see cref="IBanFileWatcher.AcknowledgeImportAsync"/> after publish.
    /// </summary>
    public required ImportAcknowledgment? ImportAcknowledgment { get; init; }
}

/// <summary>
/// Captures the import metadata that should be written to the status row after a
/// successful Service Bus publish. Held by the agent between the publish call and
/// the acknowledge call; the watcher does not store it internally.
/// </summary>
public sealed record ImportAcknowledgment
{
    public required DateTime ImportUtc { get; init; }
    public required int BanCount { get; init; }
    public required string SampleNamesJson { get; init; }
}

/// <summary>
/// A single ban entry parsed from the ban file.
/// </summary>
public sealed record DetectedBanEntry
{
    public required string PlayerGuid { get; init; }
    public required string PlayerName { get; init; }
}

