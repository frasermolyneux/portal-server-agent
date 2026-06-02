namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public sealed record RemoteFileEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required DateTime LastWriteUtc { get; init; }
}
