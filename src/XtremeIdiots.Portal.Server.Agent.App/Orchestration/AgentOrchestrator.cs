namespace XtremeIdiots.Portal.Server.Agent.App.Orchestration;

/// <summary>
/// Central orchestrator that fetches bot-enabled servers from the Repository API,
/// spawns/stops GameServerAgent instances, and periodically refreshes configuration.
/// </summary>
public class AgentOrchestrator : BackgroundService
{
    private readonly ILogger<AgentOrchestrator> _logger;

    public AgentOrchestrator(ILogger<AgentOrchestrator> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AgentOrchestrator starting");

        // TODO: Phase B5 — Fetch bot-enabled servers, spawn GameServerAgents, config refresh loop

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }

        _logger.LogInformation("AgentOrchestrator stopping");
    }
}
