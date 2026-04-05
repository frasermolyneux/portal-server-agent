using Moq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using XtremeIdiots.Portal.Server.Agent.App.Agents;
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
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private readonly ILogger<AgentOrchestrator> _logger = NullLogger<AgentOrchestrator>.Instance;

    private AgentOrchestrator CreateOrchestrator() =>
        new(_mockConfigProvider.Object, _mockTailerFactory.Object, _mockParserFactory.Object,
            _mockPublisher.Object, _mockOffsetStore.Object, _loggerFactory, _logger);

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
    public async Task RefreshAgents_SkipsServersWithoutFtpConfig()
    {
        // Arrange
        var serverWithFtp = CreateTestServerContext("With FTP");
        var serverWithoutFtp = new ServerContext
        {
            ServerId = Guid.NewGuid(),
            GameType = "CallOfDuty4",
            Title = "No FTP",
            FtpHostname = "",
            FtpPort = 21,
            FtpUsername = "",
            FtpPassword = "",
            LiveLogFile = "/logs/test.log",
            Hostname = "game.example.com",
            QueryPort = 28960,
            RconPassword = null
        };

        _mockConfigProvider.Setup(c => c.GetAgentEnabledServersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { serverWithFtp, serverWithoutFtp });

        SetupFactoryMocks();

        var orchestrator = CreateOrchestrator();

        // Act
        await orchestrator.RefreshAgentsAsync(CancellationToken.None);

        // Assert — only the server with FTP should have an agent
        Assert.Equal(1, orchestrator.ActiveAgentCount);
    }

    [Fact]
    public async Task RefreshAgents_SkipsServersWithoutLiveLogFile()
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
            LiveLogFile = null,
            Hostname = "game.example.com",
            QueryPort = 28960,
            RconPassword = null
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

    private static ServerContext CreateTestServerContext(string title) => new()
    {
        ServerId = Guid.NewGuid(),
        GameType = "CallOfDuty4",
        Title = title,
        FtpHostname = "ftp.example.com",
        FtpPort = 21,
        FtpUsername = "user",
        FtpPassword = "pass",
        LiveLogFile = "/logs/games_mp.log",
        Hostname = "game.example.com",
        QueryPort = 28960,
        RconPassword = "secret"
    };

    private void SetupFactoryMocks()
    {
        var mockTailer = new Mock<ILogTailer>();
        mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                // Simulate polling delay so agent doesn't spin
                await Task.Delay(100, ct);
                return (IReadOnlyList<string>)Array.Empty<string>();
            });

        _mockTailerFactory.Setup(f => f.Create()).Returns(mockTailer.Object);
        _mockParserFactory.Setup(f => f.Create(It.IsAny<string>())).Returns(new Mock<ILogParser>().Object);
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
    }
}
