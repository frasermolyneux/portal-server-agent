using System.Text;

using Microsoft.Extensions.Logging;

using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace XtremeIdiots.Portal.Server.Agent.App.LogTailing;

/// <summary>
/// Tails a remote log file over SFTP using offset tracking and reconnect logic.
/// </summary>
public sealed class SftpLogTailer : ILogTailer
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 30, 60];

    internal const int MaxBytesPerPoll = 1024 * 1024; // 1 MB

    internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RotationCheckInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<SftpLogTailer> _logger;
    private SftpClient? _client;
    private SftpFileStream? _logStream;
    private FileTransportTailerConfig? _config;
    private long _lastFileSize;
    private string _partialLine = string.Empty;
    private int _reconnectAttempts;
    private DateTime _rotationCheckAt = DateTime.MinValue;

    private bool _hostKeyValidated;
    private string? _actualHostKeyFingerprint;

    public SftpLogTailer(ILogger<SftpLogTailer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConnected => _client?.IsConnected == true;

    public long CurrentOffset => _lastFileSize;

    public string? CurrentFilePath => _config?.FilePath;

    public async Task ConnectAsync(FileTransportTailerConfig config, long? startOffset = null, CancellationToken ct = default)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        if (!string.Equals(config.TransportType, "sftp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SftpLogTailer does not support transport type '{config.TransportType}'.");
        }

        if (string.IsNullOrWhiteSpace(config.HostKeyFingerprint))
        {
            throw new InvalidOperationException(
                $"Missing SFTP host key fingerprint for '{config.Hostname}:{config.Port}'.");
        }

        _logger.LogInformation("Connecting to SFTP server {Hostname}:{Port} for file {FilePath}",
            config.Hostname, config.Port, config.FilePath);

        await EstablishConnectionAsync(ct).ConfigureAwait(false);

        var currentFileSize = await GetFileSizeAsync(config.FilePath, ct).ConfigureAwait(false);

        if (startOffset.HasValue && startOffset.Value <= currentFileSize)
        {
            _lastFileSize = startOffset.Value;
            _logger.LogInformation("Resuming from saved offset {Offset} for {FilePath} (file size: {FileSize})",
                _lastFileSize, config.FilePath, currentFileSize);
        }
        else
        {
            _lastFileSize = currentFileSize;
            _logger.LogInformation("Connected and tailing from end of file at offset {Offset} for {FilePath}",
                _lastFileSize, config.FilePath);
        }

        _partialLine = string.Empty;
        _reconnectAttempts = 0;

        await OpenLogStreamAsync(config.FilePath, ct).ConfigureAwait(false);
        _rotationCheckAt = DateTime.UtcNow + RotationCheckInterval;
    }

    public async Task<IReadOnlyList<string>> PollAsync(CancellationToken ct = default)
    {
        if (_config is null)
        {
            throw new InvalidOperationException("ConnectAsync must be called before PollAsync.");
        }

        try
        {
            if (!IsConnected)
            {
                await ReconnectAsync(ct).ConfigureAwait(false);
            }

            // Infrequent rotation check - avoids SSH_FXP_STAT on every poll.
            long? statSize = null;
            if (DateTime.UtcNow >= _rotationCheckAt)
            {
                statSize = await GetFileSizeAsync(_config.FilePath, ct).ConfigureAwait(false);
                _rotationCheckAt = DateTime.UtcNow + RotationCheckInterval;

                if (statSize < _lastFileSize)
                {
                    _logger.LogWarning(
                        "Log rotation detected for {FilePath}: size went from {OldSize} to {NewSize}, resetting offset",
                        _config.FilePath, _lastFileSize, statSize);

                    _lastFileSize = 0;
                    _partialLine = string.Empty;
                    statSize = null;

                    _logStream?.Dispose();
                    _logStream = null;
                    await OpenLogStreamAsync(_config.FilePath, ct).ConfigureAwait(false);
                }
            }

            // Read from the persistent handle - SSH_FXP_READ returns 0 bytes at EOF.
            using var memoryStream = new MemoryStream();
            var buffer = new byte[8192];
            long totalRead = 0;

            while (totalRead < MaxBytesPerPoll)
            {
                var toRead = (int)Math.Min(buffer.Length, MaxBytesPerPoll - totalRead);
                var bytesRead = await _logStream!.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }

                memoryStream.Write(buffer, 0, bytesRead);
                totalRead += bytesRead;
            }

            // Connection is healthy regardless of whether new data arrived.
            _reconnectAttempts = 0;

            // Guard against rename-rotation: new file may have grown to >= old size
            // before the shrink check fired. If a stat was taken this cycle and shows
            // the file is larger than our offset but the stream returned no bytes,
            // reopen the handle so we start reading from the new file.
            if (totalRead == 0 && statSize.HasValue && statSize.Value > _lastFileSize)
            {
                _logger.LogWarning(
                    "Rename-rotation suspected for {FilePath}: stat shows {StatSize} bytes but stream returned 0; reopening handle",
                    _config.FilePath, statSize.Value);

                _logStream?.Dispose();
                _logStream = null;
                await OpenLogStreamAsync(_config.FilePath, ct).ConfigureAwait(false);
            }

            if (totalRead == 0)
            {
                return Array.Empty<string>();
            }

            _lastFileSize += totalRead;

            var lines = FtpLogTailer.SplitIntoLines(memoryStream.ToArray(), ref _partialLine);

            _logger.LogDebug("Poll returned {LineCount} lines from {FilePath} ({Bytes} bytes)",
                lines.Count, _config.FilePath, totalRead);

            return lines;
        }
        catch (SftpPathNotFoundException ex)
        {
            _logger.LogError(ex, "SFTP path not found while polling {FilePath}, will attempt reconnect", _config.FilePath);
            _logStream?.Dispose();
            _logStream = null;
            await ReconnectAsync(ct).ConfigureAwait(false);
            return Array.Empty<string>();
        }
        catch (SshException ex)
        {
            _logger.LogError(ex, "SFTP error while polling {FilePath}, will attempt reconnect", _config.FilePath);
            _logStream?.Dispose();
            _logStream = null;
            await ReconnectAsync(ct).ConfigureAwait(false);
            return Array.Empty<string>();
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error while polling {FilePath}, will attempt reconnect", _config.FilePath);
            _logStream?.Dispose();
            _logStream = null;
            await ReconnectAsync(ct).ConfigureAwait(false);
            return Array.Empty<string>();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_logStream is not null)
        {
            _logStream.Dispose();
            _logStream = null;
        }

        if (_client is not null)
        {
            _logger.LogInformation("Disposing SFTP log tailer");

            if (_client.IsConnected)
            {
                await Task.Run(() => _client.Disconnect()).ConfigureAwait(false);
            }

            _client.Dispose();
            _client = null;
        }
    }

    private async Task EstablishConnectionAsync(CancellationToken ct)
    {
        _logStream?.Dispose();
        _logStream = null;
        _client?.Dispose();

        var expectedFingerprint = NormalizeFingerprint(_config!.HostKeyFingerprint!);
        _hostKeyValidated = false;
        _actualHostKeyFingerprint = null;

        _client = new SftpClient(_config.Hostname, _config.Port, _config.Username, _config.Password);
        _client.KeepAliveInterval = KeepAliveInterval;
        _client.HostKeyReceived += (_, args) =>
        {
            var actual = Convert.ToHexString(args.FingerPrint);
            _actualHostKeyFingerprint = actual;
            _hostKeyValidated = string.Equals(actual, expectedFingerprint, StringComparison.OrdinalIgnoreCase);
            args.CanTrust = _hostKeyValidated;
        };

        await Task.Run(() => _client.Connect(), ct).ConfigureAwait(false);

        if (!_hostKeyValidated)
        {
            _client.Dispose();
            _client = null;
            throw new InvalidOperationException(
                $"SFTP host key verification failed for '{_config.Hostname}:{_config.Port}'. " +
                $"Expected '{expectedFingerprint}', actual '{_actualHostKeyFingerprint ?? "unknown"}'.");
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        var backoffIndex = Math.Min(_reconnectAttempts, BackoffSeconds.Length - 1);
        var delay = TimeSpan.FromSeconds(BackoffSeconds[backoffIndex]);
        _reconnectAttempts++;

        _logger.LogWarning("Reconnecting to SFTP server (attempt {Attempt}, backoff {BackoffSeconds}s)",
            _reconnectAttempts, delay.TotalSeconds);

        await Task.Delay(delay, ct).ConfigureAwait(false);
        await EstablishConnectionAsync(ct).ConfigureAwait(false);
        await OpenLogStreamAsync(_config!.FilePath, ct).ConfigureAwait(false);
        _rotationCheckAt = DateTime.UtcNow + RotationCheckInterval;

        _logger.LogInformation("Reconnected to SFTP server, resuming from offset {Offset}", _lastFileSize);
    }

    private async Task<long> GetFileSizeAsync(string path, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var attributes = _client!.GetAttributes(path);
            return attributes.Size;
        }, ct).ConfigureAwait(false);
    }

    private async Task OpenLogStreamAsync(string path, CancellationToken ct)
    {
        _logStream = await Task.Run(() => _client!.OpenRead(path), ct).ConfigureAwait(false);

        if (_lastFileSize > 0)
        {
            _logStream.Seek(_lastFileSize, SeekOrigin.Begin);
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
}
