namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Reconciles CoD4x command minimum powers between portal settings and live server state.
/// </summary>
public interface ICoD4xCommandPowerReconciliationService
{
    /// <summary>
    /// Best-effort reconciliation. Failures are logged and do not throw to callers.
    /// </summary>
    Task ReconcileAsync(Guid serverId, string? gameType, CancellationToken ct = default);
}
