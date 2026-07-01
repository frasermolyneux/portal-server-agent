namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Executes pending CoD4x plugin lifecycle operations queued in the
/// <c>cod4xPlugin</c> game-server configuration namespace.
/// </summary>
public interface ICoD4xPluginLifecycleService
{
    Task ExecuteAsync(ServerContext context, CancellationToken ct = default);
}
