using System.Collections.Concurrent;

using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Orchestration;

/// <summary>
/// Central orchestrator that fetches bot-enabled servers from the Repository API,
/// spawns/stops GameServerAgent instances, and periodically refreshes configuration.
/// </summary>
public class AgentOrchestrator : BackgroundService
{
    private readonly IServerConfigProvider _configProvider;
    private readonly ILogTailerFactory _tailerFactory;
    private readonly ILogParserFactory _parserFactory;
    private readonly IEventPublisher _publisher;
    private readonly IOffsetStore _offsetStore;
    private readonly IServerLock _serverLock;
    private readonly IServerSyncService _syncService;
    private readonly IBanFileWatcher _banFileWatcher;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentOrchestrator> _logger;

    private readonly ConcurrentDictionary<Guid, AgentEntry> _agents = new();

    private record AgentEntry(Task Task, CancellationTokenSource Cts, string ConfigHash);

    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    public AgentOrchestrator(
        IServerConfigProvider configProvider,
        ILogTailerFactory tailerFactory,
        ILogParserFactory parserFactory,
        IEventPublisher publisher,
        IOffsetStore offsetStore,
        IServerLock serverLock,
        IServerSyncService syncService,
        IBanFileWatcher banFileWatcher,
        ILoggerFactory loggerFactory,
        ILogger<AgentOrchestrator> logger)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _tailerFactory = tailerFactory ?? throw new ArgumentNullException(nameof(tailerFactory));
        _parserFactory = parserFactory ?? throw new ArgumentNullException(nameof(parserFactory));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _offsetStore = offsetStore ?? throw new ArgumentNullException(nameof(offsetStore));
        _serverLock = serverLock ?? throw new ArgumentNullException(nameof(serverLock));
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _banFileWatcher = banFileWatcher ?? throw new ArgumentNullException(nameof(banFileWatcher));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Indicates whether the orchestrator's <see cref="ExecuteAsync"/> loop is currently running.
    /// </summary>
    private volatile bool _isRunning;
    public bool IsRunning => _isRunning;

    /// <summary>
    /// The number of currently running agents.
    /// </summary>
    public int ActiveAgentCount => _agents.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentOrchestrator starting");
        _isRunning = true;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshAgentsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error refreshing agents");
                }

                try
                {
                    await Task.Delay(RefreshInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _isRunning = false;
        }

        _logger.LogInformation("AgentOrchestrator stopping");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("AgentOrchestrator stopping, cancelling {Count} agents", _agents.Count);

        foreach (var (_, entry) in _agents)
        {
            entry.Cts.Cancel();
        }

        var allTasks = _agents.Values.Select(a => a.Task).ToArray();

        if (allTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(allTasks).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timed out waiting for agents to stop");
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        foreach (var (_, entry) in _agents)
        {
            entry.Cts.Dispose();
        }

        _agents.Clear();

        await base.StopAsync(cancellationToken);
    }

    internal async Task RefreshAgentsAsync(CancellationToken ct)
    {
        var servers = await _configProvider.GetAgentEnabledServersAsync(ct);
        var serverIds = servers.Select(s => s.ServerId).ToHashSet();

        // Stop agents for removed/disabled servers
        foreach (var (serverId, entry) in _agents)
        {
            if (!serverIds.Contains(serverId))
            {
                _logger.LogInformation("Stopping agent for removed server {ServerId}", serverId);
                entry.Cts.Cancel();
                if (_agents.TryRemove(serverId, out _))
                {
                    entry.Cts.Dispose();
                }
            }
        }

        // Restart agents whose config has changed
        foreach (var server in servers)
        {
            if (_agents.TryGetValue(server.ServerId, out var existing) && !existing.Task.IsCompleted)
            {
                if (existing.ConfigHash != server.ConfigHash)
                {
                    _logger.LogInformation(
                        "Config changed for {Title} ({ServerId}), restarting agent",
                        server.Title, server.ServerId);
                    existing.Cts.Cancel();
                    if (_agents.TryRemove(server.ServerId, out _))
                    {
                        existing.Cts.Dispose();
                    }
                }
            }
        }

        // Remove completed/faulted agent tasks
        foreach (var (serverId, entry) in _agents)
        {
            if (entry.Task.IsCompleted)
            {
                _logger.LogWarning("Agent task for server {ServerId} completed unexpectedly (status: {Status})",
                    serverId, entry.Task.Status);
                if (_agents.TryRemove(serverId, out _))
                {
                    entry.Cts.Dispose();
                }
            }
        }

        // Start agents for new servers (or servers whose agent was just stopped)
        foreach (var server in servers)
        {
            if (_agents.ContainsKey(server.ServerId))
                continue;

            if (!server.FtpEnabled || !server.RconEnabled)
            {
                _logger.LogWarning("Server {Title} ({ServerId}) has FTP or RCON disabled, skipping",
                    server.Title, server.ServerId);
                continue;
            }

            if (string.IsNullOrEmpty(server.FtpHostname) || string.IsNullOrEmpty(server.FtpUsername))
            {
                _logger.LogWarning("Server {Title} ({ServerId}) has no FTP config, skipping",
                    server.Title, server.ServerId);
                continue;
            }

            if (string.IsNullOrEmpty(server.LogFilePath))
            {
                _logger.LogWarning("Server {Title} ({ServerId}) has no LogFilePath configured, skipping",
                    server.Title, server.ServerId);
                continue;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tailer = _tailerFactory.Create();
            var parser = _parserFactory.Create(server.GameType);
            var agentLogger = _loggerFactory.CreateLogger($"GameServerAgent.{server.Title}");

            var agent = new GameServerAgent(server, tailer, parser, _publisher, _offsetStore, _serverLock, _syncService, _banFileWatcher, agentLogger);

            var task = Task.Run(() => agent.RunAsync(cts.Token), cts.Token);
            _agents.TryAdd(server.ServerId, new AgentEntry(task, cts, server.ConfigHash));

            _logger.LogInformation("Started agent for {Title} ({GameType}, {ServerId})",
                server.Title, server.GameType, server.ServerId);
        }

        _logger.LogInformation("Agent refresh complete: {Count} active agents", _agents.Count);
    }
}
