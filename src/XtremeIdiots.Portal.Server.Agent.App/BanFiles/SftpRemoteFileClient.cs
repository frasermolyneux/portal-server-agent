using System.Text;

using Renci.SshNet;
using Renci.SshNet.Common;

using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.FileTransport;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public sealed class SftpRemoteFileClient : IRemoteFileClient
{
    private readonly ISftpClient _client;
    private readonly SftpAuthentication _authentication;
    private readonly string _expectedHostKey;

    private bool _hostKeyValidated;
    private bool _disposed;
    private string? _actualHostKey;

    public SftpRemoteFileClient(ServerContext context)
        : this(context, connectionInfo => new SftpClient(connectionInfo))
    {
    }

    internal SftpRemoteFileClient(ServerContext context, Func<ConnectionInfo, ISftpClient> createClient)
    {
        if (string.IsNullOrWhiteSpace(context.FileTransportHostKeyFingerprint))
        {
            throw new InvalidOperationException($"Missing SFTP host key fingerprint for server '{context.ServerId}'.");
        }

        _expectedHostKey = NormalizeFingerprint(context.FileTransportHostKeyFingerprint);
        _authentication = SftpAuthentication.Create(
            context.EffectiveFileTransportHostname,
            context.EffectiveFileTransportPort,
            context.EffectiveFileTransportUsername,
            context.FileTransportAuthenticationType,
            context.EffectiveFileTransportPassword,
            context.FileTransportPrivateKey,
            context.FileTransportPrivateKeyPassphrase);
        try
        {
            _client = createClient(_authentication.ConnectionInfo);
        }
        catch
        {
            _authentication.Dispose();
            throw;
        }

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
        try
        {
            await Task.Run(() => _client.Connect(), ct).ConfigureAwait(false);

            if (!_hostKeyValidated)
            {
                throw new InvalidOperationException(
                    $"SFTP host key verification failed. Expected '{_expectedHostKey}', actual '{_actualHostKey ?? "unknown"}'.");
            }
        }
        catch
        {
            DisposeResources();
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_client.IsConnected)
        {
            await Task.Run(() => _client.Disconnect(), ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListFilesAsync(string directoryPath, CancellationToken ct = default)
    {
        try
        {
            var entries = await Task.Run(() => _client.ListDirectory(directoryPath).ToList(), ct).ConfigureAwait(false);

            return entries
                .Where(x => !x.IsDirectory && !x.IsSymbolicLink)
                .Select(x => new RemoteFileEntry
                {
                    Path = x.FullName,
                    Name = x.Name,
                    Size = x.Attributes.Size,
                    LastWriteUtc = x.LastWriteTimeUtc
                })
                .ToList();
        }
        catch (SftpPathNotFoundException ex)
        {
            throw new RemoteFileNotFoundException($"Remote directory '{directoryPath}' was not found.", ex);
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

    public async Task<string> ResolveDirectoryPathAsync(string directoryPath, CancellationToken ct = default)
    {
        if (!directoryPath.StartsWith('/'))
        {
            throw new InvalidOperationException("The SFTP ban file directory must be an absolute path.");
        }

        var resolved = "/";
        foreach (var segment in directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new InvalidOperationException("The SFTP ban file directory must not contain traversal segments.");
            }

            var entries = await Task.Run(() => _client.ListDirectory(resolved)
                .Where(entry => entry.IsDirectory && !entry.IsSymbolicLink)
                .Select(entry => entry.Name).ToArray(), ct).ConfigureAwait(false);
            var name = SelectDirectoryName(entries, segment);
            resolved = resolved.TrimEnd('/') + "/" + name;
        }

        return resolved;
    }

    internal static string SelectDirectoryName(IEnumerable<string> directoryNames, string requestedName)
    {
        var matches = directoryNames
            .Where(name => string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var exact = matches.FirstOrDefault(name => string.Equals(name, requestedName, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new RemoteFileNotFoundException(
                $"Ban file directory '{requestedName}' was not found. Verify the game root and active mod."),
            _ => throw new InvalidOperationException(
                $"Ban file directory '{requestedName}' has ambiguous case-insensitive matches. Configure an exact path.")
        };
    }

    public async Task<bool> CreateFileIfMissingAsync(Stream content, string path, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            if (_client.Exists(path))
            {
                return false;
            }

            var stagedPath = path + ".portal-init-" + Guid.NewGuid().ToString("N");
            try
            {
                content.Position = 0;
                _client.UploadFile(content, stagedPath, canOverride: false);
                ct.ThrowIfCancellationRequested();
                if (_client.GetAttributes(stagedPath).Size != content.Length)
                {
                    throw new IOException("Staged ban file size does not match the central source; live file was not published.");
                }

                if (_client.Exists(path))
                {
                    return false;
                }

                try
                {
                    ct.ThrowIfCancellationRequested();
                    // Standard SFTP rename refuses an existing target; POSIX rename replaces it.
                    _client.RenameFile(stagedPath, path, isPosix: false);
                }
                catch (SftpException) when (_client.Exists(path))
                {
                    return false;
                }

                return true;
            }
            finally
            {
                if (_client.Exists(stagedPath))
                {
                    _client.DeleteFile(stagedPath);
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_client.IsConnected)
            {
                await Task.Run(() => _client.Disconnect()).ConfigureAwait(false);
            }
        }
        finally
        {
            DisposeResources();
        }
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

    private void DisposeResources()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _client.Dispose();
        }
        finally
        {
            _authentication.Dispose();
        }
    }
}
