using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Observability;
using XtremeIdiots.Portal.Server.Agent.App.Orchestration;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Observability;

public class AgentHealthCheckTests
{
    private static AgentOrchestrator CreateOrchestrator() =>
        new(
            new Mock<IServerConfigProvider>().Object,
            new Mock<ILogTailerFactory>().Object,
            new Mock<ILogParserFactory>().Object,
            new Mock<IEventPublisher>().Object,
            new Mock<IOffsetStore>().Object,
            new Mock<IServerLock>().Object,
            NullLoggerFactory.Instance,
            NullLogger<AgentOrchestrator>.Instance);

    [Fact]
    public async Task CheckHealthAsync_WhenOrchestratorNotRunning_ReturnsUnhealthy()
    {
        // Arrange — orchestrator has not been started, so IsRunning is false
        var orchestrator = CreateOrchestrator();
        var healthCheck = new AgentHealthCheck(orchestrator);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("agent-status", healthCheck, null, null)
            });

        // Assert
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not running", result.Description);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenOrchestratorRunningWithNoAgents_ReturnsHealthy()
    {
        // Arrange — start the orchestrator to set IsRunning = true, with no servers configured
        var mockConfigProvider = new Mock<IServerConfigProvider>();
        mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ServerContext>());

        var orchestrator = new AgentOrchestrator(
            mockConfigProvider.Object,
            new Mock<ILogTailerFactory>().Object,
            new Mock<ILogParserFactory>().Object,
            new Mock<IEventPublisher>().Object,
            new Mock<IOffsetStore>().Object,
            new Mock<IServerLock>().Object,
            NullLoggerFactory.Instance,
            NullLogger<AgentOrchestrator>.Instance);

        using var cts = new CancellationTokenSource();
        await orchestrator.StartAsync(cts.Token);

        // Give the orchestrator a moment to start executing
        await Task.Delay(100);

        var healthCheck = new AgentHealthCheck(orchestrator);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("agent-status", healthCheck, null, null)
            });

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("0 active agent(s)", result.Description);

        // Cleanup
        cts.Cancel();
        await orchestrator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CheckHealthAsync_WhenOrchestratorRunningWithAgents_ReturnsHealthy()
    {
        // Arrange
        var servers = new[]
        {
            new ServerContext
            {
                ServerId = Guid.NewGuid(),
                GameType = "CallOfDuty4",
                Title = "Test Server",
                FtpHostname = "ftp.example.com",
                FtpPort = 21,
                FtpUsername = "user",
                FtpPassword = "pass",
                LiveLogFile = "/logs/games_mp.log",
                Hostname = "game.example.com",
                QueryPort = 28960,
                RconPassword = "secret"
            }
        };

        var mockConfigProvider = new Mock<IServerConfigProvider>();
        mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(servers);

        var mockTailer = new Mock<ILogTailer>();
        mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(100, ct);
                return (IReadOnlyList<string>)Array.Empty<string>();
            });

        var mockTailerFactory = new Mock<ILogTailerFactory>();
        mockTailerFactory.Setup(f => f.Create()).Returns(mockTailer.Object);

        var mockParserFactory = new Mock<ILogParserFactory>();
        mockParserFactory.Setup(f => f.Create(It.IsAny<string>())).Returns(new Mock<ILogParser>().Object);

        var mockOffsetStore = new Mock<IOffsetStore>();
        mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        var mockServerLock = new Mock<IServerLock>();
        mockServerLock.Setup(l => l.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mockServerLock.Setup(l => l.RenewAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var orchestrator = new AgentOrchestrator(
            mockConfigProvider.Object,
            mockTailerFactory.Object,
            mockParserFactory.Object,
            new Mock<IEventPublisher>().Object,
            mockOffsetStore.Object,
            mockServerLock.Object,
            NullLoggerFactory.Instance,
            NullLogger<AgentOrchestrator>.Instance);

        using var cts = new CancellationTokenSource();
        await orchestrator.StartAsync(cts.Token);

        // Give orchestrator time to start and spawn agents
        await Task.Delay(200);

        var healthCheck = new AgentHealthCheck(orchestrator);

        // Act
        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("agent-status", healthCheck, null, null)
            });

        // Assert
        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("active agent", result.Description);

        // Cleanup
        cts.Cancel();
        await orchestrator.StopAsync(CancellationToken.None);
    }
}
