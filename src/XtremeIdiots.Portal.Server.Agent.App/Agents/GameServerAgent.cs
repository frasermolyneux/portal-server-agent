using System.Reflection;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Playtime;
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
    private readonly Action? _reportHeartbeat;
    private readonly ConnectionTracker _connectionTracker = new();
    private readonly Dictionary<int, (PlayerInfo Player, PlayerIpResolvedEvent Event)> _pendingIpObservations = new();
    private readonly Guid _producerInstanceId = Guid.NewGuid();
    private readonly DateTimeOffset _producerStartedAtUtc = DateTimeOffset.UtcNow;
    private readonly SiegeRefreshSchedule _siegeRefresh = new();
    private PlaytimeSourceTracker? _playtimeSourceTracker;
    private readonly PlaytimeProducer? _playtimeProducer;

    private long _sequenceId;
    private DateTime _lastOffsetSave = DateTime.MinValue;
    private DateTime _lastStatusPublish = DateTime.MinValue;
    private DateTime _lastLeaseRenew = DateTime.MinValue;
    private DateTime _lastRconSync = DateTime.MinValue;
    private DateTime _lastPluginLifecycleCheck = DateTime.MinValue;
    private DateTime _lastBanFileCheck = DateTime.MinValue;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private DateTime _lastBroadcastAt = DateTime.MinValue;
    private DateTime _lastPlaytimeFlush = DateTime.MinValue;
    private DateTime _nextPlaytimeRosterSync = DateTime.MaxValue;
    private int _nextBroadcastIndex;
    private bool _identityBaselinePending = true;
    private bool _connectionStatusPending;
    private DateTime _lastIdentityBaselineAttempt = DateTime.MinValue;
    private long _tailContinuityVersion;

    internal static readonly TimeSpan OffsetSaveInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan StatusPublishInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan LeaseRenewInterval = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan RconSyncInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PlaytimeRosterSyncInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan PluginLifecycleCheckInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan BanFileCheckInterval = TimeSpan.FromSeconds(ServerContext.DefaultBanFileCheckIntervalSeconds);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan IdentityBaselineMinimumInterval = TimeSpan.FromSeconds(30);

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
        ILogger logger,
        Action? reportHeartbeat = null)
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
            Random.Shared,
            reportHeartbeat)
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
        Random startupJitterRandom,
        Action? reportHeartbeat = null,
        PlaytimeSourceTracker? playtimeSourceTracker = null,
        PlaytimeProducer? playtimeProducer = null)
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
        _reportHeartbeat = reportHeartbeat;
        _playtimeProducer = playtimeProducer;
        _playtimeSourceTracker = playtimeSourceTracker ?? playtimeProducer?.SourceTracker;
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
                AuthenticationType = _context.FileTransportAuthenticationType,
                PrivateKey = _context.FileTransportPrivateKey,
                PrivateKeyPassphrase = _context.FileTransportPrivateKeyPassphrase,
                FilePath = _context.LogFilePath ?? throw new InvalidOperationException("LogFilePath not set")
            };

            await _tailer.ConnectAsync(tailerConfig, startOffset, ct);
            _tailContinuityVersion = _tailer.ContinuityVersion;
            await TrackPlaytimeSourceAsync(initialize: true, ct);

            // 2a. Broadcast startup status once after successful connection.
            await SendStartupOnlineBroadcastAsync(ct);

            // 3. Publish server connected event unless CoD4x plugin source mode owns event emission.
            if (!IsCod4xPluginSourceEnabledForCurrentServer())
            {
                await _publisher.PublishServerConnectedAsync(_context.ServerId, _context.GameType, NextSequenceId(), ct);
            }

            // 4. Initial RCON sync — populate slot map with current players
            var rosterCaptured = await SyncPlayersAsync(ct);
            _lastRconSync = DateTime.UtcNow;
            SchedulePlaytimeRosterSync(_lastRconSync, rosterCaptured);

            // 4a. One-shot CoD4x cvar probe (no-op for other game types). Self-gates
            // so it runs at most once per agent lifecycle per server.
            await _cvarProbe.ProbeAsync(_context, ct);

            await CheckCod4xPluginLifecycleAsync(ct);

            // 5. Main loop
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var trackPoll = await FlushPlaytimeSourcePendingAsync(ct);
                    IReadOnlyList<string> lines;
                    try
                    {
                        lines = await _tailer.PollAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        ResetConnectionBaseline();
                        await TrackPlaytimePollFailureAsync(ct);
                        throw;
                    }
                    if (_tailContinuityVersion != _tailer.ContinuityVersion)
                    {
                        _tailContinuityVersion = _tailer.ContinuityVersion;
                        ResetConnectionBaseline();
                    }
                    if (trackPoll)
                    {
                        await TrackPlaytimeSourceAsync(initialize: false, ct);
                    }

                    foreach (var line in lines)
                    {
                        var playerStateVersion = _parser.PlayerStateVersion;
                        var gameEvent = _parser.ParseLine(line);
                        if (_parser.PlayerStateVersion != playerStateVersion)
                        {
                            ResetConnectionBaseline(preservePossibleCarryovers: true);
                        }
                        if (gameEvent is not null)
                        {
                            if (!ShouldPublishParsedEvent(gameEvent))
                            {
                                continue;
                            }

                            var correlated = _connectionTracker.Observe(gameEvent) with
                            {
                                ProducerInstanceId = _producerInstanceId,
                                ProducerStartedAtUtc = _producerStartedAtUtc
                            };
                            if (correlated is PlayerDisconnectedEvent disconnected)
                            {
                                _pendingIpObservations.Remove(disconnected.SlotId);
                            }
                            else if (correlated is MapChangeEvent)
                            {
                                _pendingIpObservations.Clear();
                            }
                            if (correlated is PlayerConnectedEvent)
                            {
                                _siegeRefresh.ObserveJoin(DateTimeOffset.UtcNow);
                            }
                            if (gameEvent is PlayerConnectedEvent { IpAddress.Length: > 0 } original &&
                                correlated is PlayerConnectedEvent { IpAddress.Length: 0 } &&
                                _parser.ConnectedPlayers.TryGetValue(original.SlotId, out var player) &&
                                string.Equals(player.Guid, original.PlayerGuid, StringComparison.OrdinalIgnoreCase))
                            {
                                player.IpAddress = null;
                            }
                            try
                            {
                                await _publisher.PublishAsync(correlated, _context.ServerId, _context.GameType, NextSequenceId(), ct);
                                _connectionStatusPending |= correlated is PlayerConnectedEvent { ConnectionId: not null };
                            }
                            catch
                            {
                                ResetConnectionBaseline();
                                throw;
                            }
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

                    if (_tailer.LastPollReachedEnd && _identityBaselinePending &&
                        !IsCod4xPluginSourceEnabledForCurrentServer() &&
                        DateTime.UtcNow - _lastIdentityBaselineAttempt >= IdentityBaselineMinimumInterval)
                    {
                        await RefreshConnectionBaselineAsync(ct);
                    }

                    if (!IsCod4xPluginSourceEnabledForCurrentServer())
                    {
                        await RefreshSiegeIfDueAsync(ct);
                    }

                    if (_reportHeartbeat is not null && DateTime.UtcNow - _lastHeartbeat > StatusPublishInterval)
                    {
                        _reportHeartbeat();
                        _lastHeartbeat = DateTime.UtcNow;
                    }

                    // Periodic offset save (every 30s)
                    if (DateTime.UtcNow - _lastOffsetSave > OffsetSaveInterval)
                    {
                        await SaveOffsetAsync(ct);
                    }

                    // Periodic status publish (every 60s)
                    if (_connectionStatusPending || DateTime.UtcNow - _lastStatusPublish > StatusPublishInterval)
                    {
                        await PublishStatusAsync(ct);
                    }

                    // Periodic RCON sync (every 5 minutes)
                    if (DateTime.UtcNow - _lastRconSync > RconSyncInterval)
                    {
                        rosterCaptured = await SyncPlayersAsync(ct);
                        if (!_connectionTracker.HasCompleteBaseline)
                        {
                            _identityBaselinePending = true;
                        }
                        _lastRconSync = DateTime.UtcNow;
                        SchedulePlaytimeRosterSync(_lastRconSync, rosterCaptured);
                    }

                    await RefreshPlaytimeRosterIfDueAsync(DateTime.UtcNow, ct);

                    if (_playtimeProducer is not null && DateTime.UtcNow - _lastPlaytimeFlush > TimeSpan.FromSeconds(5))
                    {
                        await FlushPlaytimeAsync(ct);
                        _lastPlaytimeFlush = DateTime.UtcNow;
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

            if (_playtimeProducer?.HasUnpersistedObservation == true)
            {
                try
                {
                    await FlushPlaytimeAsync(shutdownCts.Token);
                }
                catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
                {
                    _logger.LogWarning("[{Title}] Timed out persisting pending playtime data", _context.Title);
                }
                if (_playtimeProducer.HasUnpersistedObservation)
                {
                    _logger.LogWarning("[{Title}] Unpersisted playtime data was lost during shutdown", _context.Title);
                }
            }

            // Release the distributed lock
            await _serverLock.ReleaseAsync(_context.ServerId, CancellationToken.None);

            _logger.LogInformation("[{Title}] Agent stopped for server {ServerId}", _context.Title, _context.ServerId);
        }
    }

    private async Task TrackPlaytimeSourceAsync(bool initialize, CancellationToken ct)
    {
        if (_playtimeSourceTracker is null)
        {
            return;
        }

        try
        {
            if (initialize)
            {
                await _playtimeSourceTracker.InitializeAsync(_tailer, ct);
            }
            else
            {
                await _playtimeSourceTracker.ObservePollAsync(_tailer, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (initialize)
            {
                _logger.LogWarning(ex, "[{Title}] Playtime source initialization failed; tracking is disabled for this agent run", _context.Title);
                _playtimeSourceTracker = null;
                return;
            }
            _logger.LogWarning(ex, "[{Title}] Playtime source batch could not be persisted; it will be retried", _context.Title);
        }
    }

    private async Task<bool> FlushPlaytimeSourcePendingAsync(CancellationToken ct)
    {
        if (_playtimeSourceTracker is null)
        {
            return false;
        }
        try
        {
            await _playtimeSourceTracker.FlushPendingAsync(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _playtimeSourceTracker.MarkSkippedPoll();
            _logger.LogWarning(ex, "[{Title}] Pending playtime source batch could not be persisted; skipping playtime for this poll", _context.Title);
            return false;
        }
    }

    private async Task<bool> SyncPlayersAsync(CancellationToken ct)
    {
        if (_playtimeProducer is null)
        {
            var sync = await _syncService.SyncWithRosterAsync(_context.ServerId, _parser, _context.GameType,
                _context.IsCod4xPluginSourceEnabled, ct);
            _connectionTracker.ObserveRoster(sync.Roster, establishBaseline: false);
            await PublishIpResolvedEventsAsync(sync.IpResolvedEvents, ct);
            return false;
        }

        var result = await _syncService.SyncWithRosterCallbackAsync(_context.ServerId, _parser, _context.GameType,
            _context.IsCod4xPluginSourceEnabled, _playtimeProducer.CaptureRosterAsync, ct);
        _connectionTracker.ObserveRoster(result.Roster, establishBaseline: false);
        await PublishIpResolvedEventsAsync(result.IpResolvedEvents, ct);
        return result.RosterRecorded
            && result.Roster is { State: RconRosterState.Complete or RconRosterState.Partial, CaptureDuration.TotalSeconds: <= 10 };
    }

    private void ResetConnectionBaseline(bool preservePossibleCarryovers = true)
    {
        _connectionTracker.Reset(preservePossibleCarryovers);
        _pendingIpObservations.Clear();
        _identityBaselinePending = true;
    }

    private async Task RefreshConnectionBaselineAsync(CancellationToken ct)
    {
        _lastIdentityBaselineAttempt = DateTime.UtcNow;
        _identityBaselinePending = false;
        _siegeRefresh.RecordRosterAttempt(DateTimeOffset.UtcNow);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await _syncService.SyncIdentityAsync(_context.ServerId, _parser, _context.GameType, timeout.Token);
            _connectionTracker.ObserveRoster(result.Roster);
            if (!_connectionTracker.HasCompleteBaseline)
            {
                _logger.LogWarning("[{Title}] Connection identity baseline unavailable; admission checks must allow unknown connections", _context.Title);
            }
            await PublishIpResolvedEventsAsync(result.IpResolvedEvents, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("[{Title}] Connection identity baseline timed out; admission checks must allow unknown connections", _context.Title);
        }
    }

    internal async Task RefreshSiegeIfDueAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_siegeRefresh.StateCheckDue(now))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            DateTimeOffset? expires = null;
            try
            {
                expires = await _syncService.GetActiveSiegeExpiryAsync(_context.ServerId, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("[{Title}] Current Siege session lookup timed out", _context.Title);
            }
            now = DateTimeOffset.UtcNow;
            _siegeRefresh.RecordState(now, expires);
        }

        if (_tailer.LastPollReachedEnd && _siegeRefresh.RosterCheckDue(DateTimeOffset.UtcNow))
        {
            await RefreshConnectionBaselineAsync(ct);
        }
    }

    internal async Task RefreshPlaytimeRosterIfDueAsync(DateTime now, CancellationToken ct)
    {
        if (_playtimeProducer is null || now < _nextPlaytimeRosterSync)
        {
            return;
        }

        var captured = await _syncService.CaptureRosterAsync(_context.ServerId, _context.GameType,
            _playtimeProducer.CaptureRosterAsync, ct);
        _nextPlaytimeRosterSync = DateTime.UtcNow + (captured ? PlaytimeRosterSyncInterval : RconSyncInterval);
    }

    internal void SchedulePlaytimeRosterSync(DateTime now, bool captured)
        => _nextPlaytimeRosterSync = now + (captured ? PlaytimeRosterSyncInterval : RconSyncInterval);

    private async Task FlushPlaytimeAsync(CancellationToken ct)
    {
        try
        {
            await _playtimeProducer!.FlushAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Title}] Playtime dispatch failed; pending observations will be retried", _context.Title);
        }
    }

    private async Task TrackPlaytimePollFailureAsync(CancellationToken ct)
    {
        if (_playtimeSourceTracker is null)
        {
            return;
        }

        try
        {
            await _playtimeSourceTracker.ObservePollFailureAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Title}] Playtime source tracking failed; tracking is disabled for this agent run", _context.Title);
            _playtimeSourceTracker = null;
        }
    }

    private static readonly HashSet<string> BroadcastSupportedGameTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CallOfDuty2",
        "CallOfDuty4",
        "CallOfDuty4x",
        "CallOfDuty5"
    };

    private bool IsBroadcastSupportedForCurrentServer()
        => BroadcastSupportedGameTypes.Contains(_context.GameType);

    private async Task SendScheduledBroadcastAsync(CancellationToken ct)
    {
        if (!IsBroadcastSupportedForCurrentServer())
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
            result = await _broadcastService.SayAsync(_context.ServerId, _context.GameType, message, ct);
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
        if (!IsBroadcastSupportedForCurrentServer())
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
            var result = await _broadcastService.SayAsync(_context.ServerId, _context.GameType, message, ct);
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
                new AgentConnectionSnapshot(_producerInstanceId, _producerStartedAtUtc,
                    _parser.ConnectedPlayers.Values
                        .Select(p => (p.SlotId, Id: _connectionTracker.GetConnectionId(p.SlotId, p.Guid)))
                        .Where(p => p.Id.HasValue)
                        .ToDictionary(p => p.SlotId, p => p.Id!.Value)),
                ct);
            _lastStatusPublish = DateTime.UtcNow;
            _connectionStatusPending = false;
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

    internal async Task PublishIpResolvedEventsAsync(IReadOnlyList<PlayerIpResolvedEvent>? events, CancellationToken ct)
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
            var correlated = (PlayerIpResolvedEvent)_connectionTracker.Observe(evt) with
            {
                ProducerInstanceId = _producerInstanceId,
                ProducerStartedAtUtc = _producerStartedAtUtc
            };
            PlayerInfo? currentPlayer = null;
            if (evt.SlotId is { } currentSlot &&
                _parser.ConnectedPlayers.TryGetValue(currentSlot, out var player) &&
                string.Equals(player.Guid, evt.PlayerGuid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(player.IpAddress, evt.IpAddress, StringComparison.Ordinal))
            {
                currentPlayer = player;
                if (_pendingIpObservations.TryGetValue(currentSlot, out var pending) &&
                    ReferenceEquals(pending.Player, player) &&
                    pending.Event.ConnectionId == correlated.ConnectionId &&
                    string.Equals(pending.Event.IpAddress, correlated.IpAddress, StringComparison.Ordinal))
                {
                    correlated = correlated with
                    {
                        ObservationId = pending.Event.ObservationId,
                        Timestamp = pending.Event.Timestamp
                    };
                }
            }
            try
            {
                await _publisher.PublishAsync(correlated, _context.ServerId, _context.GameType, NextSequenceId(), ct);
                if (evt.SlotId is { } publishedSlot)
                {
                    _pendingIpObservations.Remove(publishedSlot);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Title}] Failed to publish PlayerIpResolved for {PlayerGuid}",
                    _context.Title, evt.PlayerGuid);
                if (evt.SlotId is { } slot && currentPlayer is not null)
                {
                    _pendingIpObservations[slot] = (currentPlayer, correlated);
                    currentPlayer.IpAddress = null;
                }
            }
        }
    }

    private bool ShouldPublishParsedEvent(GameEvent gameEvent)
    {
        _ = gameEvent;

        if (!IsCod4xPluginSourceEnabledForCurrentServer())
        {
            return true;
        }

        // Plugin-enabled CoD4x servers become the source of truth for parsed game
        // events, so the log-tail path should not emit duplicates.
        return false;
    }

    private bool IsCod4xPluginSourceEnabledForCurrentServer() =>
        _context.IsCod4xPluginSourceEnabled &&
        string.Equals(_context.GameType, Cod4xCvarProbe.Cod4xGameType, StringComparison.OrdinalIgnoreCase);
}
