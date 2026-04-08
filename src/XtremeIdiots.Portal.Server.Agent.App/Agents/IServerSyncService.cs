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
}
