using Moq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Orchestration;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Orchestration;

public class AgentOrchestratorTests
{
    private readonly Mock<IServerConfigProvider> _mockConfigProvider = new();
    private readonly Mock<ILogTailerFactory> _mockTailerFactory = new();
    private readonly Mock<ILogParserFactory> _mockParserFactory = new();
    private readonly Mock<IEventPublisher> _mockPublisher = new();
    private readonly Mock<IOffsetStore> _mockOffsetStore = new();
    private readonly Mock<IServerLock> _mockServerLock = new();
    private readonly Mock<IServerSyncService> _mockSyncService = new();
    private readonly Mock<IRconBroadcastService> _mockBroadcastService = new();
    private readonly Mock<ICod4xCvarProbe> _mockCvarProbe = new();
    private readonly Mock<IBanFileWatcher> _mockBanFileWatcher = new();
    private readonly Mock<IRemoteOpsSessionCoordinator> _mockOpsSessionCoordinator = new();
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ILogger<AgentOrchestrator> _logger = NullLogger<AgentOrchestrator>.Instance;

    public AgentOrchestratorTests()
    {
        _mockServerLock.Setup(l => l.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockServerLock.Setup(l => l.RenewAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    private AgentOrchestrator CreateOrchestrator() =>
        new(_mockConfigProvider.Object, _mockTailerFactory.Object, _mockParserFactory.Object,
            _mockPublisher.Object, _mockOffsetStore.Object, _mockServerLock.Object,
            _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockBanFileWatcher.Object, _mockOpsSessionCoordinator.Object, _loggerFactory, _logger);

    [Fact]
    public async Task RefreshAgents_WithNoServers_StartsNoAgents()
    {
        // Arrange
        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ServerContext>());

        var orchestrator = CreateOrchestrator();

        // Act
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_WithServers_StartsAgents()
    {
        // Arrange
        var servers = new[]
        {
            CreateTestServerContext("Server 1"),
            CreateTestServerContext("Server 2")
        };

        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(servers);

        SetupFactoryMocks();

        var orchestrator = CreateOrchestrator();

        // Act
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(2, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_SkipsServersWithoutFileTransportConfig()
    {
        // Arrange
        var serverWithTransport = CreateTestServerContext("With File Transport");
        var serverWithoutTransport = new ServerContext
        {
            ServerId = Guid.NewGuid(),
            GameType = "CallOfDuty4",
            Title = "No File Transport",
            FtpHostname = "",
            FtpPort = 21,
            FtpUsername = "",
            FtpPassword = "",
            LogFilePath = "/logs/test.log",
            Hostname = "game.example.com",
            QueryPort = 28960,
            RconPassword = null,
            FileTransportEnabled = true,
            FileTransportType = FileTransportTypes.Ftp,
            FileTransportHostname = "",
            FileTransportPort = 21,
            FileTransportUsername = "",
            FileTransportPassword = "",
            FtpEnabled = true,
            RconEnabled = true,
            BanFileSyncEnabled = true,
            BanFileRootPath = "/",
            ConfigHash = "hash-no-ftp"
        };

        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { serverWithTransport, serverWithoutTransport });

        SetupFactoryMocks();

        var orchestrator = CreateOrchestrator();

        // Act
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert — only the server with file transport settings should have an agent
        Assert.Equal(1, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_SkipsServersWithoutLogFilePath()
    {
        // Arrange
        var server = new ServerContext
        {
            ServerId = Guid.NewGuid(),
            GameType = "CallOfDuty4",
            Title = "No Log File",
            FtpHostname = "ftp.example.com",
            FtpPort = 21,
            FtpUsername = "user",
            FtpPassword = "pass",
            LogFilePath = null,
            Hostname = "game.example.com",
            QueryPort = 28960,
            RconPassword = null,
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
            ConfigHash = "hash-no-log"
        };

        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { server });

        var orchestrator = CreateOrchestrator();

        // Act
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_AgentEnabledButFileTransportDisabled_SkipsServer()
    {
        // Arrange
        var server = new ServerContext
        {
            ServerId = Guid.NewGuid(),
            GameType = "CallOfDuty4",
            Title = "File Transport Disabled",
            FtpHostname = "ftp.example.com",
            FtpPort = 21,
            FtpUsername = "user",
            FtpPassword = "pass",
            LogFilePath = "/logs/games_mp.log",
            Hostname = "game.example.com",
            QueryPort = 28960,
            RconPassword = "secret",
            FileTransportEnabled = false,
            FileTransportType = FileTransportTypes.Ftp,
            FileTransportHostname = "ftp.example.com",
            FileTransportPort = 21,
            FileTransportUsername = "user",
            FileTransportPassword = "pass",
            FtpEnabled = false,
            RconEnabled = true,
            BanFileSyncEnabled = true,
            BanFileRootPath = "/",
            ConfigHash = "hash-ftp-disabled"
        };

        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { server });

        var orchestrator = CreateOrchestrator();

        // Act
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_SftpWithoutHostKeyFingerprint_SkipsServer()
    {
        var server = CreateTestServerContext("SFTP Missing Fingerprint") with
        {
            FileTransportEnabled = true,
            FileTransportType = "sftp",
            FileTransportHostname = "sftp.example.com",
            FileTransportPort = 22,
            FileTransportUsername = "sftp-user",
            FileTransportPassword = "sftp-pass",
            FileTransportHostKeyFingerprint = null
        };

        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([server]);

        var orchestrator = CreateOrchestrator();

        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        Assert.Equal(0, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_DoesNotDuplicateExistingAgents()
    {
        // Arrange
        var server = CreateTestServerContext("Server 1");

        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { server });

        SetupFactoryMocks();

        var orchestrator = CreateOrchestrator();

        // Act — refresh twice with the same server
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert — still only one agent
        Assert.Equal(1, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_StopsAgentWhenServerRemoved()
    {
        // Arrange
        var server = CreateTestServerContext("Server 1");

        _mockConfigProvider.SetupSequence(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { server })
            .ReturnsAsync(Array.Empty<ServerContext>());

        SetupFactoryMocks();

        var orchestrator = CreateOrchestrator();

        // Act — first refresh starts the agent
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);
        Assert.Equal(1, orchestrator.ActiveAgentCount);

        // Allow the agent task to start
        await Task.Delay(50);

        // Second refresh removes it
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_RestartsAgentWhenConfigHashChanges()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var serverV1 = CreateTestServerContext("Server 1", serverId) with { ConfigHash = "hash-v1" };
        var serverV2 = CreateTestServerContext("Server 1", serverId) with { ConfigHash = "hash-v2" };

        _mockConfigProvider.SetupSequence(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { serverV1 })
            .ReturnsAsync(new[] { serverV2 });

        SetupFactoryMocks();

        var orchestrator = CreateOrchestrator();

        // Act — first refresh starts the agent
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);
        Assert.Equal(1, orchestrator.ActiveAgentCount);

        // Allow the agent task to start
        await Task.Delay(50);

        // Second refresh detects config change and restarts
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert — still one agent running (old stopped, new started)
        Assert.Equal(1, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_DoesNotRestartAgentWhenConfigHashUnchanged()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var server = CreateTestServerContext("Server 1", serverId);

        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { server });

        SetupFactoryMocks();

        var orchestrator = CreateOrchestrator();

        // Act — refresh twice with same config hash
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert — tailer factory should only be called once (no restart)
        _mockTailerFactory.Verify(f => f.Create(It.IsAny<ServerContext>()), Times.Once);
    }

    [Fact]
    public async Task StopAsync_CancelsAllAgents()
    {
        // Arrange
        var server = CreateTestServerContext("Server 1");

        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { server });

        SetupFactoryMocks();

        var orchestrator = CreateOrchestrator();
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Act
        await orchestrator.StopAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, orchestrator.ActiveAgentCount);
    }

    private static ServerContext CreateTestServerContext(string title) => CreateTestServerContext(title, Guid.NewGuid());

    private static ServerContext CreateTestServerContext(string title, Guid serverId) => new()
    {
        ServerId = serverId,
        GameType = "CallOfDuty4",
        Title = title,
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
        ConfigHash = $"hash-{title}"
    };

    private void SetupFactoryMocks()
    {
        var mockTailer = new Mock<ILogTailer>();
        mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                // Simulate polling delay so agent doesn't spin
                await Task.Delay(100, ct);
                return (IReadOnlyList<string>)Array.Empty<string>();
            });

        _mockTailerFactory.Setup(f => f.Create(It.IsAny<ServerContext>())).Returns(mockTailer.Object);
        _mockParserFactory.Setup(f => f.Create(It.IsAny<string>())).Returns(new Mock<ILogParser>().Object);
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
    }
}
