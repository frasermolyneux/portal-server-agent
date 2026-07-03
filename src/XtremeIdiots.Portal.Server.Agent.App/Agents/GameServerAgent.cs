using System.Reflection;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Manages a single game server connection. Tails the log file, parses events,
/// publishes to Service Bus, and periodically saves the tailing offset.
/// </summary>
public sealed class GameServerAgent
{
    internal static readonly TimeSpan StartupJitterMax = TimeSpan.FromSeconds(60);

    private readonly ServerContext _context;
    private readonly ILogTailer _tailer;
    private readonly ILogParser _parser;
    private readonly IEventPublisher _publisher;
    private readonly IOffsetStore _offsetStore;
    private readonly IServerLock _serverLock;
    private readonly IServerSyncService _syncService;
    private readonly IRconBroadcastService _broadcastService;
    private readonly ICod4xCvarProbe _cvarProbe;
    private readonly ICoD4xPluginLifecycleService _coD4xPluginLifecycleService;
    private readonly IBanFileWatcher _banFileWatcher;
    private readonly ILogger _logger;
    private readonly Random _startupJitterRandom;

    private long _sequenceId;
    private DateTime _lastOffsetSave = DateTime.MinValue;
    private DateTime _lastStatusPublish = DateTime.MinValue;
    private DateTime _lastLeaseRenew = DateTime.MinValue;
    private DateTime _lastRconSync = DateTime.MinValue;
    private DateTime _lastPluginLifecycleCheck = DateTime.MinValue;
    private DateTime _lastBanFileCheck = DateTime.MinValue;
    private DateTime _lastBroadcastAt = DateTime.MinValue;
    private int _nextBroadcastIndex;

    internal static readonly TimeSpan OffsetSaveInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StatusPublishInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan RconSyncInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PluginLifecycleCheckInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan BanFileCheckInterval = TimeSpan.FromSeconds(ServerContext.DefaultBanFileCheckIntervalSeconds);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public GameServerAgent(
        ServerContext context,
        ILogTailer tailer,
        ILogParser parser,
        IEventPublisher publisher,
        IOffsetStore offsetStore,
        IServerLock serverLock,
        IServerSyncService syncService,
        IRconBroadcastService broadcastService,
        ICod4xCvarProbe cvarProbe,
        ICoD4xPluginLifecycleService coD4xPluginLifecycleService,
        IBanFileWatcher banFileWatcher,
        ILogger logger)
        : this(
            context,
            tailer,
            parser,
            publisher,
            offsetStore,
            serverLock,
            syncService,
            broadcastService,
            cvarProbe,
            coD4xPluginLifecycleService,
            banFileWatcher,
            logger,
            new Random())
    {
    }

    internal GameServerAgent(
        ServerContext context,
        ILogTailer tailer,
        ILogParser parser,
        IEventPublisher publisher,
        IOffsetStore offsetStore,
        IServerLock serverLock,
        IServerSyncService syncService,
        IRconBroadcastService broadcastService,
        ICod4xCvarProbe cvarProbe,
        ICoD4xPluginLifecycleService coD4xPluginLifecycleService,
        IBanFileWatcher banFileWatcher,
        ILogger logger,
        Random startupJitterRandom)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _tailer = tailer ?? throw new ArgumentNullException(nameof(tailer));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _offsetStore = offsetStore ?? throw new ArgumentNullException(nameof(offsetStore));
        _serverLock = serverLock ?? throw new ArgumentNullException(nameof(serverLock));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _broadcastService = broadcastService ?? throw new ArgumentNullException(nameof(broadcastService));
        _cvarProbe = cvarProbe ?? throw new ArgumentNullException(nameof(cvarProbe));
        _coD4xPluginLifecycleService = coD4xPluginLifecycleService ?? throw new ArgumentNullException(nameof(coD4xPluginLifecycleService));
        _banFileWatcher = banFileWatcher ?? throw new ArgumentNullException(nameof(banFileWatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startupJitterRandom = startupJitterRandom ?? throw new ArgumentNullException(nameof(startupJitterRandom));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await ApplyStartupJitterAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("[{Title}] Startup jitter cancelled before run loop started", _context.Title);
            return;
        }

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
            long? startOffset = savedOffset is not null && savedOffset.FilePath == _context.LogFilePath
                ? savedOffset.Offset
                : null;

            if (startOffset.HasValue)
            {
                _logger.LogInformation("[{Title}] Resuming from offset {Offset}", _context.Title, startOffset.Value);
            }

            // 2. Connect file transport tailer
            var tailerConfig = new FileTransportTailerConfig
            {
                TransportType = _context.EffectiveFileTransportType,
                Hostname = _context.EffectiveFileTransportHostname,
                Port = _context.EffectiveFileTransportPort,
                Username = _context.EffectiveFileTransportUsername,
                Password = _context.EffectiveFileTransportPassword,
                HostKeyFingerprint = _context.FileTransportHostKeyFingerprint,
                FilePath = _context.LogFilePath ?? throw new InvalidOperationException("LogFilePath not set")
            };

            await _tailer.ConnectAsync(tailerConfig, startOffset, ct);

            // 2a. Broadcast startup status once after successful connection.
            await SendStartupOnlineBroadcastAsync(ct);

            // 3. Publish server connected event
            await _publisher.PublishServerConnectedAsync(_context.ServerId, _context.GameType, NextSequenceId(), ct);

            // 4. Initial RCON sync — populate slot map with current players
            var initialIpEvents = await _syncService.SyncAsync(_context.ServerId, _parser, _context.GameType, ct);
            await PublishIpResolvedEventsAsync(initialIpEvents, ct);
            _lastRconSync = DateTime.UtcNow;

            // 4a. One-shot CoD4x cvar probe (no-op for other game types). Self-gates
            // so it runs at most once per agent lifecycle per server.
            await _cvarProbe.ProbeAsync(_context, ct);

            await CheckCod4xPluginLifecycleAsync(ct);

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
                            if (!ShouldPublishParsedEvent(gameEvent))
                            {
                                continue;
                            }

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
                        var ipEvents = await _syncService.SyncAsync(_context.ServerId, _parser, _context.GameType, ct);
                        await PublishIpResolvedEventsAsync(ipEvents, ct);
                        _lastRconSync = DateTime.UtcNow;
                    }

                    if (DateTime.UtcNow - _lastPluginLifecycleCheck > PluginLifecycleCheckInterval)
                    {
                        await CheckCod4xPluginLifecycleAsync(ct);
                    }

                    // Periodic ban file check (defaults to 60 seconds, overridable via typed settings)
                    var banFileCheckInterval = TimeSpan.FromSeconds(Math.Max(_context.BanFileCheckIntervalSeconds, 1));
                    if (_context.BanFileSyncEnabled && DateTime.UtcNow - _lastBanFileCheck > banFileCheckInterval)
                    {
                        await CheckBanFileAsync(ct);
                    }

                    await SendScheduledBroadcastAsync(ct);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Title}] Agent failed during startup or run loop initialization", _context.Title);
            throw;
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

    private async Task SendScheduledBroadcastAsync(CancellationToken ct)
    {
        if (!string.Equals(_context.GameType, Cod4xCvarProbe.Cod4xGameType, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_context.IsCod4xPluginSourceEnabled)
        {
            return;
        }

        var broadcastSettings = _context.Broadcasts;
        if (!broadcastSettings.Enabled)
        {
            return;
        }

        var enabledMessages = broadcastSettings.Messages
            .Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.Message))
            .ToArray();

        if (enabledMessages.Length == 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(broadcastSettings.IntervalSeconds, 1));
        if (DateTime.UtcNow - _lastBroadcastAt <= interval)
        {
            return;
        }

        var index = _nextBroadcastIndex % enabledMessages.Length;
        var messageBody = enabledMessages[index].Message;
        var message = BuildPrefixedMessage(messageBody);

        ApiResult result;

        try
        {
            result = await _broadcastService.SayAsync(_context.ServerId, message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{Title}] Scheduled broadcast send failed", _context.Title);
            _lastBroadcastAt = DateTime.UtcNow;
            return;
        }

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "[{Title}] Scheduled broadcast send failed: status {StatusCode}",
                _context.Title,
                result.StatusCode);
            _lastBroadcastAt = DateTime.UtcNow;
            return;
        }

        _nextBroadcastIndex++;
        _lastBroadcastAt = DateTime.UtcNow;
    }

    private async Task SendStartupOnlineBroadcastAsync(CancellationToken ct)
    {
        if (!string.Equals(_context.GameType, Cod4xCvarProbe.Cod4xGameType, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_context.IsCod4xPluginSourceEnabled)
        {
            return;
        }

        var version = typeof(GameServerAgent).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(GameServerAgent).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        var message = BuildPrefixedMessage($"Agent is now online (version {version})");

        try
        {
            var result = await _broadcastService.SayAsync(_context.ServerId, message, ct);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "[{Title}] Startup online broadcast failed: status {StatusCode}",
                    _context.Title,
                    result.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{Title}] Startup online broadcast threw", _context.Title);
        }
    }

    private string BuildPrefixedMessage(string messageBody)
    {
        var prefix = _context.AgentNamePrefix?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(prefix)
            ? messageBody
            : $"{prefix} {messageBody}";
    }

    private long NextSequenceId() => Interlocked.Increment(ref _sequenceId);

    private async Task ApplyStartupJitterAsync(CancellationToken ct)
    {
        var delayMs = _startupJitterRandom.NextInt64(0, (long)StartupJitterMax.TotalMilliseconds + 1);
        if (delayMs <= 0)
        {
            return;
        }

        var delay = TimeSpan.FromMilliseconds(delayMs);
        _logger.LogDebug("[{Title}] Applying startup jitter delay of {Delay}", _context.Title, delay);
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

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
        if (IsCod4xPluginSourceEnabledForCurrentServer())
        {
            return;
        }

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

    private async Task CheckBanFileAsync(CancellationToken ct)
    {
        try
        {
            var result = await _banFileWatcher.CheckAsync(_context, _parser.ServerMod, ct);
            _lastBanFileCheck = DateTime.UtcNow;

            if (result.NewBans.Count > 0 && result.ImportAcknowledgment is not null)
            {
                // Publish first, then acknowledge — preserves the "no ban loss if publish
                // fails" guarantee. The watcher has already persisted check + push +
                // count fields; only the import-status fields wait on publish success.
                await _publisher.PublishBanDetectedAsync(
                    _context.ServerId, _context.GameType, NextSequenceId(),
                    result.NewBans, ct);

                await _banFileWatcher.AcknowledgeImportAsync(_context.ServerId, result.ImportAcknowledgment, ct);

                _logger.LogInformation("[{Title}] Published {Count} new ban(s) from ban file",
                    _context.Title, result.NewBans.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Title}] Ban file check failed", _context.Title);
        }
    }

    private async Task CheckCod4xPluginLifecycleAsync(CancellationToken ct)
    {
        try
        {
            await _coD4xPluginLifecycleService.ExecuteAsync(_context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Title}] CoD4x plugin lifecycle check failed", _context.Title);
        }
        finally
        {
            _lastPluginLifecycleCheck = DateTime.UtcNow;
        }
    }

    private async Task PublishIpResolvedEventsAsync(IReadOnlyList<PlayerIpResolvedEvent>? events, CancellationToken ct)
    {
        if (IsCod4xPluginSourceEnabledForCurrentServer())
        {
            return;
        }

        if (events is null or { Count: 0 })
        {
            return;
        }

        foreach (var evt in events)
        {
            try
            {
                await _publisher.PublishAsync(evt, _context.ServerId, _context.GameType, NextSequenceId(), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Title}] Failed to publish PlayerIpResolved for {PlayerGuid}",
                    _context.Title, evt.PlayerGuid);
            }
        }
    }

    private bool ShouldPublishParsedEvent(GameEvent gameEvent)
    {
        if (!IsCod4xPluginSourceEnabledForCurrentServer())
        {
            return true;
        }

        // Keep chat flowing so non-plugin-owned portal command paths can still run.
        return gameEvent is ChatMessageEvent;
    }

    private bool IsCod4xPluginSourceEnabledForCurrentServer() =>
        _context.IsCod4xPluginSourceEnabled &&
        string.Equals(_context.GameType, Cod4xCvarProbe.Cod4xGameType, StringComparison.OrdinalIgnoreCase);
}
