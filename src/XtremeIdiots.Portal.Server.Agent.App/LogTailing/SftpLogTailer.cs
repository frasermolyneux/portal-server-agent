using System.Text;
using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using Renci.SshNet;
using Renci.SshNet.Common;

namespace XtremeIdiots.Portal.Server.Agent.App.LogTailing;

/// <summary>
/// Tails a remote log file over SFTP using offset tracking and reconnect logic.
/// </summary>
public sealed class SftpLogTailer : ILogTailer
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 30, 60];

    internal const int MaxBytesPerPoll = 1024 * 1024; // 1 MB

    private readonly ILogger<SftpLogTailer> _logger;
    private SftpClient? _client;
    private FileTransportTailerConfig? _config;
    private long _lastFileSize;
    private string _partialLine = string.Empty;
    private int _reconnectAttempts;

    private bool _hostKeyValidated;
    private string? _actualHostKeyFingerprint;
    private string _lastResolvedEndpoints = "unknown";

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

            var currentSize = await GetFileSizeAsync(_config.FilePath, ct).ConfigureAwait(false);

            if (currentSize < _lastFileSize)
            {
                _logger.LogWarning("Log rotation detected for {FilePath}: size went from {OldSize} to {NewSize}, resetting offset",
                    _config.FilePath, _lastFileSize, currentSize);
                _lastFileSize = 0;
                _partialLine = string.Empty;
            }

            if (currentSize == _lastFileSize)
            {
                return Array.Empty<string>();
            }

            var bytesAvailable = currentSize - _lastFileSize;
            var bytesToRead = Math.Min(bytesAvailable, MaxBytesPerPoll);

            _logger.LogDebug("Reading {ByteCount} new bytes from {FilePath} at offset {Offset} ({BytesAvailable} bytes available)",
                bytesToRead, _config.FilePath, _lastFileSize, bytesAvailable);

            var data = await DownloadBytesFromOffsetAsync(_config.FilePath, _lastFileSize, bytesToRead, ct).ConfigureAwait(false);
            _lastFileSize += data.Length;
            _reconnectAttempts = 0;

            var lines = FtpLogTailer.SplitIntoLines(data, ref _partialLine);

            _logger.LogDebug("Poll returned {LineCount} lines from {FilePath}", lines.Count, _config.FilePath);

            return lines;
        }
        catch (SftpPathNotFoundException ex)
        {
            _logger.LogError(ex, "SFTP path not found while polling {FilePath}, will attempt reconnect", _config.FilePath);
            await ReconnectAsync(ct).ConfigureAwait(false);
            return Array.Empty<string>();
        }
        catch (SshException ex)
        {
            _logger.LogError(ex, "SFTP error while polling {FilePath}, will attempt reconnect", _config.FilePath);
            await ReconnectAsync(ct).ConfigureAwait(false);
            return Array.Empty<string>();
        }
    }

    public async ValueTask DisposeAsync()
    {
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
        _client?.Dispose();

        var expectedFingerprint = NormalizeFingerprint(_config!.HostKeyFingerprint!);
        _hostKeyValidated = false;
        _actualHostKeyFingerprint = null;

        await CaptureConnectivityDebugAsync(ct).ConfigureAwait(false);

        _client = new SftpClient(_config.Hostname, _config.Port, _config.Username, _config.Password);
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

        _logger.LogInformation("Reconnected to SFTP server, resuming from offset {Offset}", _lastFileSize);
    }

    private async Task CaptureConnectivityDebugAsync(CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(_config!.Hostname, ct).ConfigureAwait(false);
            _lastResolvedEndpoints = addresses.Length > 0
                ? string.Join(", ", addresses.Select(static a => a.ToString()))
                : "no addresses";
        }
        catch (Exception ex)
        {
            _lastResolvedEndpoints = $"dns-resolution-failed: {ex.GetType().Name}";
        }

        _logger.LogDebug(
            "SFTP connect diagnostics for {Hostname}:{Port}: resolved endpoints [{ResolvedEndpoints}]",
            _config!.Hostname,
            _config.Port,
            _lastResolvedEndpoints);

        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        try
        {
            using var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(_config.Hostname, _config.Port, ct).ConfigureAwait(false);

            var localEndPoint = tcpClient.Client.LocalEndPoint?.ToString() ?? "unknown";
            var remoteEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";

            _logger.LogDebug(
                "SFTP TCP probe for {Hostname}:{Port}: local endpoint {LocalEndPoint}, remote endpoint {RemoteEndPoint}",
                _config.Hostname,
                _config.Port,
                localEndPoint,
                remoteEndPoint);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "SFTP TCP probe failed for {Hostname}:{Port} (resolved: [{ResolvedEndpoints}])",
                _config.Hostname,
                _config.Port,
                _lastResolvedEndpoints);
        }
    }

    private async Task<long> GetFileSizeAsync(string path, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var attributes = _client!.GetAttributes(path);
            return attributes.Size;
        }, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> DownloadBytesFromOffsetAsync(string path, long offset, long length, CancellationToken ct)
    {
        await using var stream = await Task.Run(() => _client!.OpenRead(path), ct).ConfigureAwait(false);

        stream.Seek(offset, SeekOrigin.Begin);

        using var memoryStream = new MemoryStream();
        var buffer = new byte[8192];
        long totalRead = 0;

        while (totalRead < length)
        {
            var toRead = (int)Math.Min(buffer.Length, length - totalRead);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                break;
            }

            memoryStream.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;
        }

        return memoryStream.ToArray();
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
