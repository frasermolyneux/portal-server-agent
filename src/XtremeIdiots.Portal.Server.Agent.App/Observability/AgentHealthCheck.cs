using Microsoft.Extensions.Diagnostics.HealthChecks;

using XtremeIdiots.Portal.Server.Agent.App.Orchestration;

namespace XtremeIdiots.Portal.Server.Agent.App.Observability;

/// <summary>
/// Health check that reports on the status of the agent orchestrator and running game server agents.
/// </summary>
public sealed class AgentHealthCheck : IHealthCheck
{
    private readonly AgentOrchestrator _orchestrator;

    public AgentHealthCheck(AgentOrchestrator orchestrator)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_orchestrator.IsRunning)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Agent orchestrator is not running."));
        }

        var activeCount = _orchestrator.ActiveAgentCount;
        if (_orchestrator.ExpectedAgentCount is null)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Configured agent inventory is unavailable."));
        }

        if (_orchestrator.ExpectedAgentCount > 0 && activeCount == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("No configured agent is running."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Agent orchestrator is running with {activeCount} active agent(s)."));
    }
}
