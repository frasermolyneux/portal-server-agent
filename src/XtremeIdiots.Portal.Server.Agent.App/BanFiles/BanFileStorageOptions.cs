namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Configuration for the central ban file storage account (the same storage
/// portal-sync writes to via its <c>GenerateLatestBansFile</c> timer trigger).
/// The agent reads from it to push regenerated ban lists to game servers.
/// </summary>
public sealed class BanFileStorageOptions
{
    public const string SectionName = "BanFileStorage";

    /// <summary>
    /// Primary blob endpoint of the storage account hosting the regenerated
    /// per-game ban files (e.g. <c>https://saadXXXX.blob.core.windows.net</c>).
    /// </summary>
    public string? BlobEndpoint { get; set; }

    /// <summary>
    /// Container holding the <c>{GameType}-bans.txt</c> blobs. Defaults to <c>ban-files</c>
    /// to match the portal-sync convention.
    /// </summary>
    public string ContainerName { get; set; } = "ban-files";
}
