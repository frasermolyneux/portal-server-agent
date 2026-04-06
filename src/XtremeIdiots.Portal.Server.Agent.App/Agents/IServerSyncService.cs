using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Queries game servers via RCON to populate/reconcile the parser's player slot map.
/// </summary>
public interface IServerSyncService
{
    /// <summary>
    /// Query the server via RCON and merge the player list into the parser's slot map.
    /// Best-effort — failures are logged but never throw.
    /// </summary>
    Task SyncAsync(Guid serverId, ILogParser parser, CancellationToken ct = default);
}
