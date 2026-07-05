using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Queries game servers via RCON to populate/reconcile the parser's player slot map.
/// </summary>
public interface IServerSyncService
{
    /// <summary>
    /// Query the server via RCON and merge the player list into the parser's slot map.
    /// Returns any PlayerIpResolved events for players whose IP was first discovered.
    /// Best-effort — failures are logged but never throw.
    /// </summary>
    Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(Guid serverId, ILogParser parser, CancellationToken ct = default);

    /// <summary>
    /// Query the server via RCON and merge the player list into the parser's slot map.
    /// When <paramref name="gameType"/> is CoD4x, performs ban reconciliation so
    /// portal-active bans are reapplied server-side and server-only bans are imported
    /// into the portal as admin actions.
    /// Best-effort — failures are logged but never throw.
    /// </summary>
    Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(Guid serverId, ILogParser parser, string? gameType, CancellationToken ct = default);

    /// <summary>
    /// Query the server via RCON and merge the player list into the parser's slot map.
    /// When <paramref name="gameType"/> is CoD4x, performs ban/admin/command-power
    /// reconciliation. The <paramref name="isCod4xPluginSourceEnabled"/> flag controls
    /// whether agent-side CoD4x ban reapply operations are skipped in favor of plugin
    /// enforcement while preserving server-only import behavior.
    /// Best-effort — failures are logged but never throw.
    /// </summary>
    Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(
        Guid serverId,
        ILogParser parser,
        string? gameType,
        bool isCod4xPluginSourceEnabled,
        CancellationToken ct = default);
}
