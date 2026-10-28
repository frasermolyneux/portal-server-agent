using Moq;

using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Orchestration;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Orchestration;

public class AgentOrchestratorRefreshIntegrationTests
{
    [Fact]
    public async Task RefreshAgents_CancellationCallbackFailurePropagatesBeforeSessionClosure()
    {
        var server = new ServerContext
        {
            ServerId = Guid.NewGuid(),
            GameType = "CallOfDuty4",
            Title = "Removed Server",
            FtpHostname = "ftp.example.com",
            FtpPort = 21,
            FtpUsername = "user",
            FtpPassword = "pass",
            LogFilePath = "/logs/games_mp.log",
            Hostname = "game.example.com",
            QueryPort = 28960,
            RconPassword = "secret",
            FileTransportEnabled = true,
            FileTransportType = FileTransportTypes.Ftp,
            FileTransportHostname = "ftp.example.com",
            FileTransportPort = 21,
            FileTransportUsername = "user",
            FileTransportPassword = "pass",
            FtpEnabled = true,
            RconEnabled = true,
            BanFileSyncEnabled = true,
            BanFileRootPath = "/",
            ConfigHash = "hash-removed"
        };
        var configProvider = new Mock<IServerConfigProvider>();
        _ = configProvider.SetupSequence(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([server])
            .ReturnsAsync([]);

        var tailerFactory = new Mock<ILogTailerFactory>();
        var tailer = new Mock<ILogTailer>();
        _ = tailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _ = tailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(10, ct);
                return [];
            });
        _ = tailerFactory.Setup(f => f.Create(It.IsAny<ServerContext>())).Returns(tailer.Object);

        var parserFactory = new Mock<ILogParserFactory>();
        _ = parserFactory.Setup(f => f.Create(It.IsAny<string>())).Returns(new Mock<ILogParser>().Object);
        var offsetStore = new Mock<IOffsetStore>();
        _ = offsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        var serverLock = new Mock<IServerLock>();
        _ = serverLock.Setup(l => l.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _ = serverLock.Setup(l => l.RenewAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var sessionCoordinator = new Mock<IRemoteOpsSessionCoordinator>();
        var orchestrator = new AgentOrchestrator(
            configProvider.Object,
            tailerFactory.Object,
            parserFactory.Object,
            new Mock<IEventPublisher>().Object,
            offsetStore.Object,
            serverLock.Object,
            new Mock<IServerSyncService>().Object,
            new Mock<IRconBroadcastService>().Object,
            new Mock<ICod4xCvarProbe>().Object,
            new Mock<ICoD4xPluginLifecycleService>().Object,
            new Mock<IBanFileWatcher>().Object,
            sessionCoordinator.Object,
            NullLoggerFactory.Instance,
            NullLogger<AgentOrchestrator>.Instance,
            new TelemetryClient(new TelemetryConfiguration { DisableTelemetry = true }));

        await orchestrator.RefreshAgentsAsync(CancellationToken.None);
        var cancellationSource = GetAgentCancellationSource(orchestrator, server.ServerId);
        _ = cancellationSource.Token.Register(() => throw new InvalidOperationException("Cancellation callback failed."));

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => orchestrator.RefreshAgentsAsync(CancellationToken.None));

        Assert.True(cancellationSource.IsCancellationRequested);
        var callbackException = Assert.IsType<InvalidOperationException>(Assert.Single(exception.InnerExceptions));
        Assert.Equal("Cancellation callback failed.", callbackException.Message);
        Assert.Same(cancellationSource, GetAgentCancellationSource(orchestrator, server.ServerId));
        sessionCoordinator.Verify(
            c => c.CloseServerSessionAsync(server.ServerId, It.IsAny<CancellationToken>()),
            Times.Never);

        await orchestrator.StopAsync(CancellationToken.None);
    }

    private static CancellationTokenSource GetAgentCancellationSource(AgentOrchestrator orchestrator, Guid serverId)
    {
        var agents = typeof(AgentOrchestrator)
            .GetField("_agents", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(orchestrator)!;
        var arguments = new object?[] { serverId, null };
        Assert.True((bool)agents.GetType().GetMethod("TryGetValue")!.Invoke(agents, arguments)!);
        return (CancellationTokenSource)arguments[1]!.GetType().GetProperty("Cts")!.GetValue(arguments[1])!;
    }
}
