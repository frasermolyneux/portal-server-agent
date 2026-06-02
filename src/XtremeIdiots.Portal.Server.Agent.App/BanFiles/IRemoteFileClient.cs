namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public interface IRemoteFileClient : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(string directoryPath, CancellationToken ct = default);
    Task<long> GetFileSizeAsync(string path, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default);
    Task UploadAsync(Stream content, string path, CancellationToken ct = default);
}
