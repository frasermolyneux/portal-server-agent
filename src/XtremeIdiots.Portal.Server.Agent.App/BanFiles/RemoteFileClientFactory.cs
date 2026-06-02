using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public sealed class RemoteFileClientFactory : IRemoteFileClientFactory
{
    public IRemoteFileClient Create(ServerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return string.Equals(context.EffectiveFileTransportType, FileTransportTypes.Sftp, StringComparison.OrdinalIgnoreCase)
            ? new SftpRemoteFileClient(context)
            : new FtpRemoteFileClient(context);
    }
}
