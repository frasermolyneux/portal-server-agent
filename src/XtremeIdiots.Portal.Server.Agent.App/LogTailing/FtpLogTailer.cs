using System.Text;

using FluentFTP;
using FluentFTP.Exceptions;

using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Agent.App.LogTailing;

/// <summary>
/// Tails a remote log file over FTP using persistent connections and offset tracking.
/// Downloads only new bytes since the last poll, handles log rotation and partial lines.
/// </summary>
public sealed class FtpLogTailer : ILogTailer
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 16, 30, 60];

    private readonly ILogger<FtpLogTailer> _logger;
    private AsyncFtpClient? _client;
    private FtpTailerConfig? _config;
    private long _lastFileSize;
    private string _partialLine = string.Empty;
    private int _reconnectAttempts;

    /// <summary>
    /// Creates a new <see cref="FtpLogTailer"/> instance.
    /// </summary>
    /// <param name="logger">Logger for structured diagnostics.</param>
    public FtpLogTailer(ILogger<FtpLogTailer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsConnected => _client?.IsConnected == true;

    /// <inheritdoc />
    public long CurrentOffset => _lastFileSize;

    /// <inheritdoc />
    public string? CurrentFilePath => _config?.FilePath;

    /// <inheritdoc />
    public async Task ConnectAsync(FtpTailerConfig config, long? startOffset = null, CancellationToken ct = default)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        _logger.LogInformation("Connecting to FTP server {Hostname}:{Port} for file {FilePath}",
            config.Hostname, config.Port, config.FilePath);

        await EstablishConnectionAsync(ct);

        var currentFileSize = await _client!.GetFileSize(config.FilePath, -1, ct);

        if (startOffset.HasValue && startOffset.Value <= currentFileSize)
        {
            _lastFileSize = startOffset.Value;
            _logger.LogInformation("Resuming from saved offset {Offset} for {FilePath} (file size: {FileSize})",
                _lastFileSize, config.FilePath, currentFileSize);
        }
        else
        {
            // Start tailing from end of file so we only get new data
            _lastFileSize = currentFileSize;
            _logger.LogInformation("Connected and tailing from end of file at offset {Offset} for {FilePath}",
                _lastFileSize, config.FilePath);
        }

        _partialLine = string.Empty;
        _reconnectAttempts = 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> PollAsync(CancellationToken ct = default)
    {
        if (_config is null)
            throw new InvalidOperationException("ConnectAsync must be called before PollAsync.");

        try
        {
            if (!IsConnected)
            {
                await ReconnectAsync(ct);
            }

            var currentSize = await _client!.GetFileSize(_config.FilePath, -1, ct);

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

            var bytesToRead = currentSize - _lastFileSize;

            _logger.LogDebug("Reading {ByteCount} new bytes from {FilePath} at offset {Offset}",
                bytesToRead, _config.FilePath, _lastFileSize);

            var data = await DownloadBytesFromOffsetAsync(_config.FilePath, _lastFileSize, bytesToRead, ct);
            _lastFileSize = currentSize;
            _reconnectAttempts = 0;

            var lines = SplitIntoLines(data, ref _partialLine);

            _logger.LogDebug("Poll returned {LineCount} lines from {FilePath}", lines.Count, _config.FilePath);

            return lines;
        }
        catch (FtpException ex)
        {
            _logger.LogError(ex, "FTP error while polling {FilePath}, will attempt reconnect", _config.FilePath);
            await ReconnectAsync(ct);
            return Array.Empty<string>();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            _logger.LogInformation("Disposing FTP log tailer");

            if (_client.IsConnected)
            {
                await _client.Disconnect();
            }

            _client.Dispose();
            _client = null;
        }
    }

    /// <summary>
    /// Splits raw bytes into complete lines, buffering any trailing partial line.
    /// </summary>
    /// <param name="data">The raw bytes read from the log file.</param>
    /// <param name="partialLine">
    /// On entry, any leftover text from the previous poll.
    /// On exit, updated with any incomplete trailing line from this chunk.
    /// </param>
    /// <returns>A list of complete lines (without line endings).</returns>
    internal static IReadOnlyList<string> SplitIntoLines(byte[] data, ref string partialLine)
    {
        if (data.Length == 0)
            return Array.Empty<string>();

        var text = partialLine + Encoding.UTF8.GetString(data);
        var lines = new List<string>();

        var startIndex = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var end = i > 0 && text[i - 1] == '\r' ? i - 1 : i;
                var line = text[startIndex..end];

                if (line.Length > 0)
                {
                    lines.Add(line);
                }

                startIndex = i + 1;
            }
        }

        // Whatever remains after the last newline is a partial line
        partialLine = startIndex < text.Length ? text[startIndex..] : string.Empty;

        return lines;
    }

    private async Task EstablishConnectionAsync(CancellationToken ct)
    {
        _client?.Dispose();
        _client = new AsyncFtpClient(_config!.Hostname, _config.Username, _config.Password, _config.Port);
        await _client.Connect(ct);
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        var backoffIndex = Math.Min(_reconnectAttempts, BackoffSeconds.Length - 1);
        var delay = TimeSpan.FromSeconds(BackoffSeconds[backoffIndex]);
        _reconnectAttempts++;

        _logger.LogWarning("Reconnecting to FTP server (attempt {Attempt}, backoff {BackoffSeconds}s)",
            _reconnectAttempts, delay.TotalSeconds);

        await Task.Delay(delay, ct);
        await EstablishConnectionAsync(ct);

        _logger.LogInformation("Reconnected to FTP server, resuming from offset {Offset}", _lastFileSize);
    }

    private async Task<byte[]> DownloadBytesFromOffsetAsync(string path, long offset, long length, CancellationToken ct)
    {
        await using var stream = await _client!.OpenRead(path, FtpDataType.Binary, offset, false, ct);

        using var memoryStream = new MemoryStream();
        var buffer = new byte[8192];
        long totalRead = 0;

        while (totalRead < length)
        {
            var toRead = (int)Math.Min(buffer.Length, length - totalRead);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, toRead), ct);

            if (bytesRead == 0)
                break;

            memoryStream.Write(buffer, 0, bytesRead);
            totalRead += bytesRead;
        }

        return memoryStream.ToArray();
    }
}
