using System.Diagnostics;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Observability;
using XtremeIdiots.Portal.Server.Agent.App.Orchestration;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Observability;

public class AgentHealthCheckTests
{
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > timeout)
            {
                throw new TimeoutException("Timed out waiting for condition to become true.");
            }

            await Task.Delay(10);
        }
    }

    private static AgentOrchestrator CreateOrchestrator() =>
        new(
            new Mock<IServerConfigProvider>().Object,
            new Mock<ILogTailerFactory>().Object,
            new Mock<ILogParserFactory>().Object,
            new Mock<IEventPublisher>().Object,
            new Mock<IOffsetStore>().Object,
            new Mock<IServerLock>().Object,
            new Mock<IServerSyncService>().Object,
            new Mock<IRconBroadcastService>().Object,
            new Mock<ICod4xCvarProbe>().Object,
            new Mock<ICoD4xPluginLifecycleService>().Object,
            new Mock<IBanFileWatcher>().Object,
            new Mock<IRemoteOpsSessionCoordinator>().Object,
            NullLoggerFactory.Instance,
            NullLogger<AgentOrchestrator>.Instance,
            new Microsoft.ApplicationInsights.TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration { DisableTelemetry = true }));

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

    [Theory]
    [InlineData(0, HealthStatus.Healthy)]
    [InlineData(1, HealthStatus.Unhealthy)]
    [InlineData(-1, HealthStatus.Degraded)]
    public async Task CheckHealthAsync_WhenNoAgentsRun_UsesTheExpectedInventory(int expected, HealthStatus status)
    {
        // Arrange — start the orchestrator to set IsRunning = true, with no servers configured
        var mockConfigProvider = new Mock<IServerConfigProvider>();
        mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ServerContext>());
        mockConfigProvider.SetupGet(provider => provider.Inventory).Returns(expected < 0 ? null :
            new AgentInventory(DateTimeOffset.UtcNow, Enumerable.Range(0, expected)
                .Select(_ => new ExpectedAgent(Guid.NewGuid(), true, true)).ToArray()));

        var orchestrator = new AgentOrchestrator(
            mockConfigProvider.Object,
            new Mock<ILogTailerFactory>().Object,
            new Mock<ILogParserFactory>().Object,
            new Mock<IEventPublisher>().Object,
            new Mock<IOffsetStore>().Object,
            new Mock<IServerLock>().Object,
            new Mock<IServerSyncService>().Object,
            new Mock<IRconBroadcastService>().Object,
            new Mock<ICod4xCvarProbe>().Object,
            new Mock<ICoD4xPluginLifecycleService>().Object,
            new Mock<IBanFileWatcher>().Object,
            new Mock<IRemoteOpsSessionCoordinator>().Object,
            NullLoggerFactory.Instance,
            NullLogger<AgentOrchestrator>.Instance,
            new Microsoft.ApplicationInsights.TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration { DisableTelemetry = true }));

        using var cts = new CancellationTokenSource();
        try
        {
            await orchestrator.StartAsync(cts.Token);

            // Wait until the background ExecuteAsync loop has set IsRunning.
            await WaitForConditionAsync(() => orchestrator.IsRunning, TimeSpan.FromSeconds(5));

            var healthCheck = new AgentHealthCheck(orchestrator);

            // Act
            var result = await healthCheck.CheckHealthAsync(
                new HealthCheckContext
                {
                    Registration = new HealthCheckRegistration("agent-status", healthCheck, null, null)
                });

            // Assert
            Assert.Equal(status, result.Status);
            Assert.Contains(expected < 0 ? "unavailable" : expected == 0 ? "0 active agent(s)" : "No configured agent", result.Description);
        }
        finally
        {
            cts.Cancel();
            await orchestrator.StopAsync(CancellationToken.None);
        }
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
                LogFilePath = "/logs/games_mp.log",
                Hostname = "game.example.com",
                QueryPort = 28960,
                RconPassword = "secret",
                FtpEnabled = true,
                FileTransportEnabled = true,
                RconEnabled = true,
                BanFileSyncEnabled = true,
                BanFileRootPath = "/",
                ConfigHash = "test-hash"
            }
        };

        var mockConfigProvider = new Mock<IServerConfigProvider>();
        mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(servers);
        mockConfigProvider.SetupGet(provider => provider.Inventory).Returns(new AgentInventory(DateTimeOffset.UtcNow,
            servers.Select(server => new ExpectedAgent(server.ServerId, true, true)).ToArray()));

        var mockTailer = new Mock<ILogTailer>();
        mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(100, ct);
                return (IReadOnlyList<string>)Array.Empty<string>();
            });

        var mockTailerFactory = new Mock<ILogTailerFactory>();
        mockTailerFactory.Setup(f => f.Create(It.IsAny<ServerContext>())).Returns(mockTailer.Object);

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
        var mockSyncService = new Mock<IServerSyncService>();
        mockSyncService.Setup(service => service.SyncAsync(It.IsAny<Guid>(), It.IsAny<ILogParser>(),
                It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PlayerIpResolvedEvent>());

        var orchestrator = new AgentOrchestrator(
            mockConfigProvider.Object,
            mockTailerFactory.Object,
            mockParserFactory.Object,
            new Mock<IEventPublisher>().Object,
            mockOffsetStore.Object,
            mockServerLock.Object,
            mockSyncService.Object,
            new Mock<IRconBroadcastService>().Object,
            new Mock<ICod4xCvarProbe>().Object,
            new Mock<ICoD4xPluginLifecycleService>().Object,
            new Mock<IBanFileWatcher>().Object,
            new Mock<IRemoteOpsSessionCoordinator>().Object,
            NullLoggerFactory.Instance,
            NullLogger<AgentOrchestrator>.Instance,
            new Microsoft.ApplicationInsights.TelemetryClient(new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration { DisableTelemetry = true }));

        using var cts = new CancellationTokenSource();
        try
        {
            await orchestrator.StartAsync(cts.Token);

            await WaitForConditionAsync(() => orchestrator.IsRunning && orchestrator.ActiveAgentCount == 1, TimeSpan.FromSeconds(5));

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
        }
        finally
        {
            cts.Cancel();
            await orchestrator.StopAsync(CancellationToken.None);
        }
    }
}
