using FluentFTP;

using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public sealed class FtpRemoteFileClient : IRemoteFileClient
{
    private readonly AsyncFtpClient _client;

    public FtpRemoteFileClient(ServerContext context)
    {
        _client = new AsyncFtpClient(
            context.EffectiveFileTransportHostname,
            context.EffectiveFileTransportUsername,
            context.EffectiveFileTransportPassword,
            context.EffectiveFileTransportPort);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.Connect(ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client.IsConnected)
        {
            await _client.Disconnect(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(string directoryPath, CancellationToken ct = default)
    {
        try
        {
            var listing = await _client.GetListing(directoryPath, FtpListOption.Auto, ct).ConfigureAwait(false);
            return listing
                .Where(x => x.Type == FtpObjectType.File)
                .Select(x => new RemoteFileEntry
                {
                    Path = x.FullName,
                    Name = x.Name,
                    Size = x.Size,
                    LastWriteUtc = NormalizeUtc(x.Modified)
                })
                .ToList();
        }
        catch (Exception ex) when (IsFileNotFound(ex))
        {
            throw new RemoteFileNotFoundException($"Remote directory '{directoryPath}' was not found.", ex);
        }
    }

    public async Task<long> GetFileSizeAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await _client.GetFileSize(path, -1, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileNotFound(ex))
        {
            throw new RemoteFileNotFoundException($"Remote file '{path}' was not found.", ex);
        }
    }

    public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default)
    {
        try
        {
            return await _client.OpenRead(path, FtpDataType.Binary, offset, false, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileNotFound(ex))
        {
            throw new RemoteFileNotFoundException($"Remote file '{path}' was not found.", ex);
        }
    }

    public async Task UploadAsync(Stream content, string path, CancellationToken ct = default)
    {
        content.Seek(0, SeekOrigin.Begin);
        await _client.UploadStream(content, path, FtpRemoteExists.Overwrite, true, null, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            await _client.Disconnect().ConfigureAwait(false);
        }

        _client.Dispose();
    }

    private static bool IsFileNotFound(Exception ex)
        => ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
