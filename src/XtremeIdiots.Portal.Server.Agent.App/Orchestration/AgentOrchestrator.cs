using System.Collections.Concurrent;

using XtremeIdiots.Portal.Server.Agent.App.Agents;
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
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentOrchestrator> _logger;

    private readonly ConcurrentDictionary<Guid, (Task Task, CancellationTokenSource Cts)> _agents = new();

    internal static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    public AgentOrchestrator(
        IServerConfigProvider configProvider,
        ILogTailerFactory tailerFactory,
        ILogParserFactory parserFactory,
        IEventPublisher publisher,
        IOffsetStore offsetStore,
        IServerLock serverLock,
        IServerSyncService syncService,
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

        foreach (var (_, (_, cts)) in _agents)
        {
            cts.Cancel();
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

        foreach (var (_, (_, cts)) in _agents)
        {
            cts.Dispose();
        }

        _agents.Clear();

        await base.StopAsync(cancellationToken);
    }

    internal async Task RefreshAgentsAsync(CancellationToken ct)
    {
        var servers = await _configProvider.GetAgentEnabledServersAsync(ct);
        var serverIds = servers.Select(s => s.ServerId).ToHashSet();

        // Stop agents for removed/disabled servers
        foreach (var (serverId, (_, cts)) in _agents)
        {
            if (!serverIds.Contains(serverId))
            {
                _logger.LogInformation("Stopping agent for removed server {ServerId}", serverId);
                cts.Cancel();
                if (_agents.TryRemove(serverId, out _))
                {
                    cts.Dispose();
                }
            }
        }

        // Remove completed/faulted agent tasks
        foreach (var (serverId, (task, cts)) in _agents)
        {
            if (task.IsCompleted)
            {
                _logger.LogWarning("Agent task for server {ServerId} completed unexpectedly (status: {Status})",
                    serverId, task.Status);
                if (_agents.TryRemove(serverId, out _))
                {
                    cts.Dispose();
                }
            }
        }

        // Start agents for new servers
        foreach (var server in servers)
        {
            if (_agents.ContainsKey(server.ServerId))
                continue;

            if (string.IsNullOrEmpty(server.FtpHostname) || string.IsNullOrEmpty(server.FtpUsername))
            {
                _logger.LogWarning("Server {Title} ({ServerId}) has no FTP config, skipping",
                    server.Title, server.ServerId);
                continue;
            }

            if (string.IsNullOrEmpty(server.LiveLogFile))
            {
                _logger.LogWarning("Server {Title} ({ServerId}) has no LiveLogFile configured, skipping",
                    server.Title, server.ServerId);
                continue;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tailer = _tailerFactory.Create();
            var parser = _parserFactory.Create(server.GameType);
            var agentLogger = _loggerFactory.CreateLogger($"GameServerAgent.{server.Title}");

            var agent = new GameServerAgent(server, tailer, parser, _publisher, _offsetStore, _serverLock, _syncService, agentLogger);

            var task = Task.Run(() => agent.RunAsync(cts.Token), cts.Token);
            _agents.TryAdd(server.ServerId, (task, cts));

            _logger.LogInformation("Started agent for {Title} ({GameType}, {ServerId})",
                server.Title, server.GameType, server.ServerId);
        }

        _logger.LogInformation("Agent refresh complete: {Count} active agents", _agents.Count);
    }
}
