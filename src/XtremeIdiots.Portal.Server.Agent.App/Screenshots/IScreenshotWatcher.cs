using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Screenshots;

public interface IScreenshotWatcher
{
    Task CheckAsync(ServerContext context, CancellationToken ct = default);
}
