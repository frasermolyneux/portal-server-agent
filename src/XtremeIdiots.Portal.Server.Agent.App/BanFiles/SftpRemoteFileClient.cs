using System.Text;

using Renci.SshNet;
using Renci.SshNet.Common;

using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public sealed class SftpRemoteFileClient : IRemoteFileClient
{
    private readonly SftpClient _client;
    private readonly string _expectedHostKey;

    private bool _hostKeyValidated;
    private string? _actualHostKey;

    public SftpRemoteFileClient(ServerContext context)
    {
        if (string.IsNullOrWhiteSpace(context.FileTransportHostKeyFingerprint))
        {
            throw new InvalidOperationException($"Missing SFTP host key fingerprint for server '{context.ServerId}'.");
        }

        _expectedHostKey = NormalizeFingerprint(context.FileTransportHostKeyFingerprint);
        _client = new SftpClient(
            context.EffectiveFileTransportHostname,
            context.EffectiveFileTransportPort,
            context.EffectiveFileTransportUsername,
            context.EffectiveFileTransportPassword);

        _client.HostKeyReceived += (_, args) =>
        {
            _actualHostKey = Convert.ToHexString(args.FingerPrint);
            _hostKeyValidated = string.Equals(_actualHostKey, _expectedHostKey, StringComparison.OrdinalIgnoreCase);
            args.CanTrust = _hostKeyValidated;
        };
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _hostKeyValidated = false;
        _actualHostKey = null;
        await Task.Run(() => _client.Connect(), ct).ConfigureAwait(false);

        if (!_hostKeyValidated)
        {
            throw new InvalidOperationException(
                $"SFTP host key verification failed. Expected '{_expectedHostKey}', actual '{_actualHostKey ?? "unknown"}'.");
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client.IsConnected)
        {
            await Task.Run(() => _client.Disconnect(), ct).ConfigureAwait(false);
        }
    }

    public async Task<long> GetFileSizeAsync(string path, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => _client.GetAttributes(path).Size, ct).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new RemoteFileNotFoundException($"Remote file '{path}' was not found.", ex);
        }
    }

    public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct = default)
    {
        Stream stream;
        try
        {
            stream = await Task.Run(() => _client.OpenRead(path), ct).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new RemoteFileNotFoundException($"Remote file '{path}' was not found.", ex);
        }

        stream.Seek(offset, SeekOrigin.Begin);
        return stream;
    }

    public async Task UploadAsync(Stream content, string path, CancellationToken ct = default)
    {
        content.Seek(0, SeekOrigin.Begin);
        await Task.Run(() => _client.UploadFile(content, path, true), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            await Task.Run(() => _client.Disconnect()).ConfigureAwait(false);
        }

        _client.Dispose();
    }

    private static string NormalizeFingerprint(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }
}
