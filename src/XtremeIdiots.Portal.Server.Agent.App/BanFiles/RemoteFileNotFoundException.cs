namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public sealed class RemoteFileNotFoundException : Exception
{
    public RemoteFileNotFoundException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
