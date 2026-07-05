namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Reconciles CoD4x active bans between the portal and the live game server via RCON.
/// </summary>
public interface ICoD4xBanReconciliationService
{
    /// <summary>
    /// Imports server-only active bans into the portal and reapplies portal-active bans
    /// that are missing from the server.
    /// Best-effort: failures are logged and do not throw to the caller.
    /// </summary>
    Task ReconcileAsync(Guid serverId, string? gameType, CancellationToken ct = default);

    /// <summary>
    /// Imports server-only active bans into the portal and conditionally reapplies
    /// portal-active bans that are missing from the server.
    /// When <paramref name="isCod4xPluginSourceEnabled"/> is true, reapply actions are
    /// skipped because plugin-side ban enforcement is the source of truth.
    /// Best-effort: failures are logged and do not throw to the caller.
    /// </summary>
    Task ReconcileAsync(Guid serverId, string? gameType, bool isCod4xPluginSourceEnabled, CancellationToken ct = default);
}
