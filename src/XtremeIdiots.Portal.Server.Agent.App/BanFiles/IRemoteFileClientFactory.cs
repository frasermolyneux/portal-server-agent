using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public interface IRemoteFileClientFactory
{
    IRemoteFileClient Create(ServerContext context);
}
