using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Manages a single game server connection. Tails the log file, parses events,
/// publishes to Service Bus, and periodically saves the FTP offset.
/// </summary>
public sealed class GameServerAgent
{
    private readonly ServerContext _context;
    private readonly ILogTailer _tailer;
    private readonly ILogParser _parser;
    private readonly IEventPublisher _publisher;
    private readonly IOffsetStore _offsetStore;
    private readonly IServerLock _serverLock;
    private readonly IServerSyncService _syncService;
    private readonly ILogger _logger;

    private long _sequenceId;
    private DateTime _lastOffsetSave = DateTime.MinValue;
    private DateTime _lastStatusPublish = DateTime.MinValue;
    private DateTime _lastLeaseRenew = DateTime.MinValue;
    private DateTime _lastRconSync = DateTime.MinValue;

    internal static readonly TimeSpan OffsetSaveInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StatusPublishInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan RconSyncInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public GameServerAgent(
        ServerContext context,
        ILogTailer tailer,
        ILogParser parser,
        IEventPublisher publisher,
        IOffsetStore offsetStore,
        IServerLock serverLock,
        IServerSyncService syncService,
        ILogger logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _tailer = tailer ?? throw new ArgumentNullException(nameof(tailer));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _offsetStore = offsetStore ?? throw new ArgumentNullException(nameof(offsetStore));
        _serverLock = serverLock ?? throw new ArgumentNullException(nameof(serverLock));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("[{GameType}:{Title}] Agent starting for server {ServerId}",
            _context.GameType, _context.Title, _context.ServerId);

        // Acquire distributed lock before connecting
        if (!await _serverLock.TryAcquireAsync(_context.ServerId, ct))
        {
            _logger.LogWarning("[{GameType}:{Title}] Could not acquire lock for server {ServerId} — another instance holds it",
                _context.GameType, _context.Title, _context.ServerId);
            return;
        }

        try
        {
            // 1. Load saved offset
            var savedOffset = await _offsetStore.GetOffsetAsync(_context.ServerId, ct);
            long? startOffset = savedOffset is not null && savedOffset.FilePath == _context.LiveLogFile
                ? savedOffset.Offset
                : null;

            if (startOffset.HasValue)
                _logger.LogInformation("[{Title}] Resuming from offset {Offset}", _context.Title, startOffset.Value);

            // 2. Connect FTP tailer
            var ftpConfig = new FtpTailerConfig
            {
                Hostname = _context.FtpHostname,
                Port = _context.FtpPort,
                Username = _context.FtpUsername,
                Password = _context.FtpPassword,
                FilePath = _context.LiveLogFile ?? throw new InvalidOperationException("LiveLogFile not set")
            };

            await _tailer.ConnectAsync(ftpConfig, startOffset, ct);

            // 3. Publish server connected event
            await _publisher.PublishServerConnectedAsync(_context.ServerId, _context.GameType, NextSequenceId(), ct);

            // 4. Initial RCON sync — populate slot map with current players
            await _syncService.SyncAsync(_context.ServerId, _parser, ct);
            _lastRconSync = DateTime.UtcNow;

            // 5. Main loop
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var lines = await _tailer.PollAsync(ct);

                    foreach (var line in lines)
                    {
                        var gameEvent = _parser.ParseLine(line);
                        if (gameEvent is not null)
                        {
                            await _publisher.PublishAsync(gameEvent, _context.ServerId, _context.GameType, NextSequenceId(), ct);
                        }
                    }

                    // Periodic lease renewal (every 15s)
                    if (DateTime.UtcNow - _lastLeaseRenew > LeaseRenewInterval)
                    {
                        if (!await _serverLock.RenewAsync(_context.ServerId, ct))
                        {
                            _logger.LogWarning("[{Title}] Lost lease — stopping agent", _context.Title);
                            break;
                        }
                        _lastLeaseRenew = DateTime.UtcNow;
                    }

                    // Periodic offset save (every 30s)
                    if (DateTime.UtcNow - _lastOffsetSave > OffsetSaveInterval)
                    {
                        await SaveOffsetAsync(ct);
                    }

                    // Periodic status publish (every 60s)
                    if (DateTime.UtcNow - _lastStatusPublish > StatusPublishInterval)
                    {
                        await PublishStatusAsync(ct);
                    }

                    // Periodic RCON sync (every 5 minutes)
                    if (DateTime.UtcNow - _lastRconSync > RconSyncInterval)
                    {
                        await _syncService.SyncAsync(_context.ServerId, _parser, ct);
                        _lastRconSync = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Title}] Error in main loop, continuing", _context.Title);
                }

                await Task.Delay(PollInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Graceful shutdown
        }
        finally
        {
            // Save offset on shutdown with a 5-second timeout
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SaveOffsetAsync(shutdownCts.Token);
            await _tailer.DisposeAsync();

            // Release the distributed lock
            await _serverLock.ReleaseAsync(_context.ServerId, CancellationToken.None);

            _logger.LogInformation("[{Title}] Agent stopped for server {ServerId}", _context.Title, _context.ServerId);
        }
    }

    private long NextSequenceId() => Interlocked.Increment(ref _sequenceId);

    private async Task SaveOffsetAsync(CancellationToken ct)
    {
        if (_tailer.CurrentFilePath is not null)
        {
            await _offsetStore.SaveOffsetAsync(_context.ServerId, _tailer.CurrentOffset, _tailer.CurrentFilePath, ct);
            _lastOffsetSave = DateTime.UtcNow;
        }
    }

    private async Task PublishStatusAsync(CancellationToken ct)
    {
        if (_parser.CurrentMap is not null)
        {
            await _publisher.PublishServerStatusAsync(
                _context.ServerId, _context.GameType, NextSequenceId(),
                _parser.CurrentMap, _parser.CurrentMap, // gameName TBD
                _parser.ConnectedPlayers,
                _parser.ServerTitle, _parser.ServerMod, _parser.MaxPlayers,
                ct);
            _lastStatusPublish = DateTime.UtcNow;
        }
    }
}
