using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public interface IRemoteOpsSessionCoordinator
{
    Task<T> ExecuteAsync<T>(ServerContext context, Func<IRemoteFileClient, CancellationToken, Task<T>> operation, CancellationToken ct = default);
    Task ExecuteAsync(ServerContext context, Func<IRemoteFileClient, CancellationToken, Task> operation, CancellationToken ct = default);
    Task CloseServerSessionAsync(Guid serverId, CancellationToken ct = default);
}
