using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Queries game servers via RCON to populate/reconcile the parser's player slot map.
/// </summary>
public interface IServerSyncService
{
    /// <summary>
    /// Query the server via RCON and merge the player list into the parser's slot map.
    /// Returns any PlayerIpResolved events for players whose IP was first discovered.
    /// Best-effort — failures are logged; caller cancellation propagates.
    /// </summary>
    Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(Guid serverId, ILogParser parser, CancellationToken ct = default);

    /// <summary>
    /// Query the server via RCON and merge the player list into the parser's slot map.
    /// When <paramref name="gameType"/> is CoD4x, performs ban reconciliation so
    /// portal-active bans are reapplied server-side and server-only bans are imported
    /// into the portal as admin actions.
    /// Best-effort — failures are logged; caller cancellation propagates.
    /// </summary>
    Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(Guid serverId, ILogParser parser, string? gameType, CancellationToken ct = default);

    /// <summary>
    /// Query the server via RCON and merge the player list into the parser's slot map.
    /// When <paramref name="gameType"/> is CoD4x, performs ban/admin/command-power
    /// reconciliation. The <paramref name="isCod4xPluginSourceEnabled"/> flag controls
    /// whether agent-side CoD4x ban reapply operations are skipped in favor of plugin
    /// enforcement while preserving server-only import behavior.
    /// Best-effort — failures are logged; caller cancellation propagates.
    /// </summary>
    Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(
        Guid serverId,
        ILogParser parser,
        string? gameType,
        bool isCod4xPluginSourceEnabled,
        CancellationToken ct = default);

    /// <summary>
    /// Performs the same sync and returns the roster observed by that RCON call, when available.
    /// Observation time is the agent's response time, not the players' join time.
    /// </summary>
    Task<ServerSyncResult> SyncWithRosterAsync(
        Guid serverId,
        ILogParser parser,
        string? gameType,
        bool isCod4xPluginSourceEnabled,
        CancellationToken ct = default);

    Task<ServerSyncResult> SyncWithRosterCallbackAsync(
        Guid serverId,
        ILogParser parser,
        string? gameType,
        bool isCod4xPluginSourceEnabled,
        Func<ServerRosterSnapshot?, CancellationToken, Task<bool>> onRoster,
        CancellationToken ct = default);

    /// <summary>
    /// Refreshes player identity and IPs from RCON without query or reconciliation.
    /// </summary>
    Task<ServerSyncResult> SyncIdentityAsync(
        Guid serverId, ILogParser parser, string? gameType, CancellationToken ct = default);

    /// <summary>Returns the expiry of a confirmed current Siege or Dry Run session.</summary>
    Task<DateTimeOffset?> GetActiveSiegeExpiryAsync(Guid serverId, CancellationToken ct = default);

    /// <summary>
    /// Captures only an RCON roster without changing the parser or running query reconciliation.
    /// Returns whether a fresh partial or complete response was captured.
    /// </summary>
    Task<bool> CaptureRosterAsync(
        Guid serverId,
        string gameType,
        Func<ServerRosterSnapshot?, CancellationToken, Task<bool>> onRoster,
        CancellationToken ct = default);
}

public sealed record ServerSyncResult(
    IReadOnlyList<PlayerIpResolvedEvent> IpResolvedEvents,
    ServerRosterSnapshot? Roster,
    bool RosterRecorded = false);

public sealed record ServerRosterSnapshot(
    DateTime ObservedUtc,
    RconRosterState State,
    IReadOnlyList<ServerRosterPlayer> Players,
    int UnparsedRowCount,
    int MissingIdentityCount,
    int KnownBotCount,
    TimeSpan CaptureDuration = default);

public sealed record ServerRosterPlayer(int SlotId, string PlayerGuid, string? Username, bool? IsBot);
