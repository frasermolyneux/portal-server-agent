namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Reconciles transient CoD4x admin powers between the desired roster from the repository API
/// and the live in-game admin list from RCON.
/// </summary>
public interface ICoD4xAdminReconciliationService
{
    /// <summary>
    /// Best-effort reconciliation. Failures are logged and do not throw to callers.
    /// </summary>
    Task ReconcileAsync(Guid serverId, string? gameType, CancellationToken ct = default);
}
