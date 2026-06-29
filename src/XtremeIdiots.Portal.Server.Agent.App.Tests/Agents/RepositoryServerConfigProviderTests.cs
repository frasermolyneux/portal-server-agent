using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using RepositoryGameServerFilter = XtremeIdiots.Portal.Repository.Abstractions.Constants.V1.GameServerFilter;
using RepositoryGameType = XtremeIdiots.Portal.Repository.Abstractions.Constants.V1.GameType;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class RepositoryServerConfigProviderTests
{
    private readonly Mock<IRepositoryApiClient> _mockClient = new();
    private readonly Mock<IVersionedGameServersApi> _mockVersioned = new();
    private readonly Mock<IGameServersApi> _mockGameServersApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _mockVersionedConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _mockConfigApi = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _mockVersionedGlobalConfigs = new();
    private readonly Mock<IGlobalConfigurationsApi> _mockGlobalConfigApi = new();
    private readonly ILogger<RepositoryServerConfigProvider> _logger = NullLogger<RepositoryServerConfigProvider>.Instance;

    public RepositoryServerConfigProviderTests()
    {
        _mockClient.Setup(c => c.GameServers).Returns(_mockVersioned.Object);
        _mockVersioned.Setup(v => v.V1).Returns(_mockGameServersApi.Object);
        _mockClient.Setup(c => c.GameServerConfigurations).Returns(_mockVersionedConfigs.Object);
        _mockVersionedConfigs.Setup(v => v.V1).Returns(_mockConfigApi.Object);
        _mockClient.Setup(c => c.GlobalConfigurations).Returns(_mockVersionedGlobalConfigs.Object);
        _mockVersionedGlobalConfigs.Setup(v => v.V1).Returns(_mockGlobalConfigApi.Object);

        // Default: config API returns empty for any server (server will be skipped)
        _mockConfigApi
            .Setup(a => a.GetConfigurations(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ConfigurationDto>>(
                    new CollectionModel<ConfigurationDto>(Array.Empty<ConfigurationDto>()))));

        _mockGlobalConfigApi
            .Setup(a => a.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<ConfigurationDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<ConfigurationDto>>(
                    new CollectionModel<ConfigurationDto>(Array.Empty<ConfigurationDto>()))));
    }

    private RepositoryServerConfigProvider CreateProvider() =>
        new(_mockClient.Object, _logger);

    [Fact]
    public async Task GetAgentEnabledServersAsync_ReturnsMappedServers()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Test Server", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert
        Assert.Single(result);
        var server = result[0];
        Assert.Equal(serverId, server.ServerId);
        Assert.Equal("CallOfDuty4", server.GameType);
        Assert.Equal("Test Server", server.Title);
        Assert.Equal("ftp.example.com", server.FtpHostname);
        Assert.Equal(21, server.FtpPort);
        Assert.Equal("user", server.FtpUsername);
        Assert.Equal("pass", server.FtpPassword);
        Assert.Equal("/logs/games_mp.log", server.LogFilePath);
        Assert.Equal("game.example.com", server.Hostname);
        Assert.Equal(28960, server.QueryPort);
        Assert.Equal("secret", server.RconPassword);
        Assert.True(server.BanFileSyncEnabled);
        Assert.Equal(ServerContext.DefaultAgentNamePrefix, server.AgentNamePrefix);
        Assert.False(server.Broadcasts.Enabled);
        Assert.Equal(ServerContext.DefaultBroadcastIntervalSeconds, server.Broadcasts.IntervalSeconds);
        Assert.Empty(server.Broadcasts.Messages);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesSftpNamespaceWhenTransportTypeIsSftp()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(
            serverId,
            "SFTP Server",
            RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com",
            queryPort: 28960,
            fileTransportType: "sftp",
            fileTransportEnabled: true);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("sftp", new { hostname = "sftp.example.com", port = 2222, username = "sftp-user", password = "sftp-pass", hostKeyFingerprint = "aa:bb:cc" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        var server = Assert.Single(result);
        Assert.Equal("sftp", server.EffectiveFileTransportType);
        Assert.True(server.EffectiveFileTransportEnabled);
        Assert.Equal("sftp.example.com", server.EffectiveFileTransportHostname);
        Assert.Equal(2222, server.EffectiveFileTransportPort);
        Assert.Equal("sftp-user", server.EffectiveFileTransportUsername);
        Assert.Equal("sftp-pass", server.EffectiveFileTransportPassword);
        Assert.Equal("aa:bb:cc", server.FileTransportHostKeyFingerprint);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SkipsServerWhenSelectedTransportNamespaceMissing()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(
            serverId,
            "SFTP Namespace Missing",
            RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com",
            queryPort: 28960,
            fileTransportType: "sftp",
            fileTransportEnabled: true);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SkipsServerWhenTransportMetadataInvalid()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(
            serverId,
            "Invalid transport metadata",
            RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com",
            queryPort: 28960,
            fileTransportType: "smtp",
            fileTransportEnabled: true);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesGlobalAgentNamePrefix_WhenServerOverrideMissing()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Global Prefix Server", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupGlobalConfigApi(new[]
        {
            CreateConfigDto("agent", new { agentName = "^2[Global Prefix]^7" })
        });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Equal("^2[Global Prefix]^7", Assert.Single(result).AgentNamePrefix);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesServerAgentNamePrefixOverride_WhenPresent()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Server Prefix Server", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupGlobalConfigApi(new[]
        {
            CreateConfigDto("agent", new { agentName = "^2[Global Prefix]^7" })
        });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log", agentName = "^1[Server Override]^7" })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Equal("^1[Server Override]^7", Assert.Single(result).AgentNamePrefix);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesDefaultAgentNamePrefix_WhenGlobalPrefixWhitespace()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Whitespace Prefix Server", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupGlobalConfigApi(new[]
        {
            CreateConfigDto("agent", new { agentName = "   " })
        });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Equal(ServerContext.DefaultAgentNamePrefix, Assert.Single(result).AgentNamePrefix);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_ParsesBroadcastSettings()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Broadcast Server", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" }),
            CreateConfigDto("broadcasts", new
            {
                enabled = true,
                intervalSeconds = 120,
                messages = new object[]
                {
                    new { message = "Message A", enabled = true },
                    new { message = "Message B", enabled = false }
                }
            })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        var server = Assert.Single(result);
        Assert.True(server.Broadcasts.Enabled);
        Assert.Equal(120, server.Broadcasts.IntervalSeconds);
        Assert.Equal(2, server.Broadcasts.Messages.Count);
        Assert.Equal("Message A", server.Broadcasts.Messages[0].Message);
        Assert.True(server.Broadcasts.Messages[0].Enabled);
        Assert.Equal("Message B", server.Broadcasts.Messages[1].Message);
        Assert.False(server.Broadcasts.Messages[1].Enabled);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_BroadcastMessageEnabledDefaultsToTrue_WhenMissing()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Broadcast Missing Enabled", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" }),
            CreateConfigDto("broadcasts", new
            {
                enabled = true,
                intervalSeconds = 120,
                messages = new object[]
                {
                    new { message = "Message A" }
                }
            })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        var server = Assert.Single(result);
        Assert.True(Assert.Single(server.Broadcasts.Messages).Enabled);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_InvalidBroadcastSettings_DefaultsSafely()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Broadcast Defaults", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" }),
            CreateConfigDto("broadcasts", new
            {
                enabled = "invalid",
                intervalSeconds = "bad",
                messages = new object[]
                {
                    new { message = "Message A", enabled = "bad" },
                    new { message = "", enabled = true },
                    new { enabled = true }
                }
            })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        var server = Assert.Single(result);
        Assert.False(server.Broadcasts.Enabled);
        Assert.Equal(ServerContext.DefaultBroadcastIntervalSeconds, server.Broadcasts.IntervalSeconds);
        Assert.Empty(server.Broadcasts.Messages);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_BroadcastChanges_ChangeConfigHash()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Broadcast Hash", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });

        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" }),
            CreateConfigDto("broadcasts", new
            {
                enabled = true,
                intervalSeconds = 60,
                messages = new object[] { new { message = "Message A", enabled = true } }
            })
        });

        var provider = CreateProvider();
        var first = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/games_mp.log" }),
            CreateConfigDto("broadcasts", new
            {
                enabled = true,
                intervalSeconds = 60,
                messages = new object[] { new { message = "Message B", enabled = true } }
            })
        });

        var second = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.NotEqual(first.Single().ConfigHash, second.Single().ConfigHash);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_WhenApiFails_ReturnsEmptyList()
    {
        // Arrange
        var apiResult = new ApiResult<CollectionModel<GameServerDto>>(HttpStatusCode.InternalServerError);

        _mockGameServersApi
            .Setup(a => a.GetGameServers(null, null, RepositoryGameServerFilter.AgentEnabled, 0, 50, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SkipsServersWithMissingFileTransportConfig()
    {
        // Arrange — config has rcon + agent but no file transport namespace
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "No File Transport Config", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — server should be skipped
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SkipsServersWithMissingRconConfig()
    {
        // Arrange — config has file transport + agent but no rcon namespace
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "No RCON Config", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — server should be skipped
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SkipsServersWithMissingAgentConfig()
    {
        // Arrange — config has file transport + rcon but no agent namespace
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "No Agent Config", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — server should be skipped
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_WhenExceptionThrown_ReturnsEmptyList()
    {
        // Arrange
        _mockGameServersApi
            .Setup(a => a.GetGameServers(null, null, RepositoryGameServerFilter.AgentEnabled, 0, 50, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_WhenConfigApiReturnsEmpty_SkipsServer()
    {
        // Arrange — config API returns empty (default mock behavior), server should be skipped
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Empty Config Server", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — no configs means required namespaces are missing, server is skipped
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_WhenConfigApiFails_SkipsServer()
    {
        // Arrange — config API throws for this server
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Error Server", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });

        _mockConfigApi
            .Setup(a => a.GetConfigurations(serverId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Config API unreachable"));

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — config API failure means required namespaces are missing, server is skipped
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_MalformedFileTransportJsonSkipsServer()
    {
        // Arrange — file transport config has malformed JSON, so the namespace is missing
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Bad JSON Server", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });

        var malformedConfig = new ConfigurationDto();
        SetConfigProperty(malformedConfig, nameof(ConfigurationDto.Namespace), "ftp");
        SetConfigProperty(malformedConfig, nameof(ConfigurationDto.Configuration), "not valid json {{{");

        SetupConfigApi(serverId, new[]
        {
            malformedConfig,
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — malformed file transport config means server is skipped
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SetsBanFileSyncEnabled()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "BanSync Server", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960,
            banFileSyncEnabled: false);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.False(result[0].BanFileSyncEnabled);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_FtpPortFromConfigAsString()
    {
        // Arrange — config has port as a string value
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "String Port", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = "2222", username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — string port should be parsed
        Assert.Single(result);
        Assert.Equal(2222, result[0].FtpPort);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SkipsServersWithIncompleteFileTransportConfig()
    {
        // Arrange — file transport namespace exists but is missing required keys
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Incomplete File Transport", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — incomplete file transport config means server is skipped
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesFixture_AgentBroadcastBanfileInputs()
    {
        var fixture = LoadFixture("agent-broadcast-banfile-happy-path.json");
        var dto = CreateGameServerDto(
            fixture.Server.Id,
            fixture.Server.Title,
            Enum.Parse<RepositoryGameType>(fixture.Server.GameType, ignoreCase: true),
            hostname: fixture.Server.Hostname,
            queryPort: fixture.Server.QueryPort,
            banFileSyncEnabled: fixture.Server.BanFileSyncEnabled,
            ftpEnabled: fixture.Server.FtpEnabled,
            rconEnabled: fixture.Server.RconEnabled,
            fileTransportType: fixture.Server.FileTransportType,
            fileTransportEnabled: fixture.Server.FileTransportEnabled,
            banFileRootPath: fixture.Server.BanFileRootPath);

        SetupApiSuccess([dto]);
        SetupGlobalConfigApi(fixture.GlobalConfigurations.Select(ToConfigurationDto).ToArray());
        SetupConfigApi(fixture.Server.Id, fixture.Configurations.Select(ToConfigurationDto).ToArray());

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        var server = Assert.Single(result);
        Assert.Equal(fixture.Server.BanFileRootPath, server.BanFileRootPath);
        Assert.True(server.BanFileSyncEnabled);
        Assert.Equal("^6[Fixture Global]^7", server.AgentNamePrefix);
        Assert.Equal("/var/log/game_mp.log", server.LogFilePath);
        Assert.True(server.Broadcasts.Enabled);
        Assert.Equal(180, server.Broadcasts.IntervalSeconds);
        Assert.Equal(2, server.Broadcasts.Messages.Count);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_FixtureLock_BroadcastMessageStringEnabledParsesMessage()
    {
        var fixture = LoadFixture("agent-broadcast-message-enabled-string-gap.json");
        var dto = CreateGameServerDto(
            fixture.Server.Id,
            fixture.Server.Title,
            Enum.Parse<RepositoryGameType>(fixture.Server.GameType, ignoreCase: true),
            hostname: fixture.Server.Hostname,
            queryPort: fixture.Server.QueryPort,
            banFileSyncEnabled: fixture.Server.BanFileSyncEnabled,
            ftpEnabled: fixture.Server.FtpEnabled,
            rconEnabled: fixture.Server.RconEnabled,
            fileTransportType: fixture.Server.FileTransportType,
            fileTransportEnabled: fixture.Server.FileTransportEnabled,
            banFileRootPath: fixture.Server.BanFileRootPath);

        SetupApiSuccess([dto]);
        SetupConfigApi(fixture.Server.Id, fixture.Configurations.Select(ToConfigurationDto).ToArray());

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        var server = Assert.Single(result);
        Assert.True(server.Broadcasts.Enabled);
        Assert.Equal(120, server.Broadcasts.IntervalSeconds);
        var message = Assert.Single(server.Broadcasts.Messages);
        Assert.Equal("This message uses a string bool", message.Message);
        Assert.True(message.Enabled);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_FixtureLock_EmptyBanFileRootPathFallsBackToSlash()
    {
        var fixture = LoadFixture("agent-empty-banfile-rootpath.json");
        var dto = CreateGameServerDto(
            fixture.Server.Id,
            fixture.Server.Title,
            Enum.Parse<RepositoryGameType>(fixture.Server.GameType, ignoreCase: true),
            hostname: fixture.Server.Hostname,
            queryPort: fixture.Server.QueryPort,
            banFileSyncEnabled: fixture.Server.BanFileSyncEnabled,
            ftpEnabled: fixture.Server.FtpEnabled,
            rconEnabled: fixture.Server.RconEnabled,
            fileTransportType: fixture.Server.FileTransportType,
            fileTransportEnabled: fixture.Server.FileTransportEnabled,
            banFileRootPath: fixture.Server.BanFileRootPath);

        SetupApiSuccess([dto]);
        SetupConfigApi(fixture.Server.Id, fixture.Configurations.Select(ToConfigurationDto).ToArray());

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Equal("/", Assert.Single(result).BanFileRootPath);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_FixtureLock_MissingAgentLogFilePathSkipsServer()
    {
        var fixture = LoadFixture("agent-missing-logfilepath-gap.json");
        var dto = CreateGameServerDto(
            fixture.Server.Id,
            fixture.Server.Title,
            Enum.Parse<RepositoryGameType>(fixture.Server.GameType, ignoreCase: true),
            hostname: fixture.Server.Hostname,
            queryPort: fixture.Server.QueryPort,
            banFileSyncEnabled: fixture.Server.BanFileSyncEnabled,
            ftpEnabled: fixture.Server.FtpEnabled,
            rconEnabled: fixture.Server.RconEnabled,
            fileTransportType: fixture.Server.FileTransportType,
            fileTransportEnabled: fixture.Server.FileTransportEnabled,
            banFileRootPath: fixture.Server.BanFileRootPath);

        SetupApiSuccess([dto]);
        SetupConfigApi(fixture.Server.Id, fixture.Configurations.Select(ToConfigurationDto).ToArray());

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Baseline lock for current behavior gap: no fallback when agent.logFilePath is absent.
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SkipsServer_WhenAgentSchemaVersionUnsupported()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Unsupported Agent Schema", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { schemaVersion = 99, logFilePath = "/logs/game.log", agentName = "^2[Unsupported]^7" })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesBroadcastDefaults_WhenBroadcastSchemaVersionUnsupported()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Unsupported Broadcast Schema", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" }),
            CreateConfigDto("broadcasts", new
            {
                schemaVersion = 99,
                enabled = true,
                intervalSeconds = 15,
                messages = new object[] { new { message = "Should be ignored", enabled = true } }
            })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        var server = Assert.Single(result);
        Assert.False(server.Broadcasts.Enabled);
        Assert.Equal(ServerContext.DefaultBroadcastIntervalSeconds, server.Broadcasts.IntervalSeconds);
        Assert.Empty(server.Broadcasts.Messages);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesBroadcastDefaults_WhenBroadcastSchemaVersionIsInvalidShape()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Invalid Broadcast Schema Shape", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" }),
            CreateConfigDto("broadcasts", new
            {
                schemaVersion = new { value = 1 },
                enabled = true,
                intervalSeconds = 15,
                messages = new object[] { new { message = "Should be ignored", enabled = true } }
            })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        var server = Assert.Single(result);
        Assert.False(server.Broadcasts.Enabled);
        Assert.Equal(ServerContext.DefaultBroadcastIntervalSeconds, server.Broadcasts.IntervalSeconds);
        Assert.Empty(server.Broadcasts.Messages);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesTypedBanFileInterval_WhenProvided()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Typed Banfile Interval", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" }),
            CreateConfigDto("banfiles", new { checkIntervalSeconds = 15 })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Equal(ServerContext.MinBanFileCheckIntervalSeconds, Assert.Single(result).BanFileCheckIntervalSeconds);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesTypedBanFileInterval_WhenNumericFieldsAreStrings()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Typed Banfile Interval String Numbers", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" }),
            CreateConfigDto("banfiles", new { schemaVersion = "1", checkIntervalSeconds = "15" })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Equal(ServerContext.MinBanFileCheckIntervalSeconds, Assert.Single(result).BanFileCheckIntervalSeconds);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_DoesNotSkipServer_WhenOptionalAgentFieldTypeIsInvalid()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Agent Optional Type Mismatch", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log", agentName = 123 })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        var server = Assert.Single(result);
        Assert.Equal("/logs/game.log", server.LogFilePath);
        Assert.Equal(ServerContext.DefaultAgentNamePrefix, server.AgentNamePrefix);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_UsesDefaultBanFileInterval_WhenBanFileSchemaVersionUnsupported()
    {
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Unsupported Banfile Schema", RepositoryGameType.CallOfDuty4,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess([dto]);
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "ftp.example.com", port = 21, username = "user", password = "pass" }),
            CreateConfigDto("rcon", new { password = "secret" }),
            CreateConfigDto("agent", new { logFilePath = "/logs/game.log" }),
            CreateConfigDto("banfiles", new { schemaVersion = 99, checkIntervalSeconds = 15 })
        });

        var provider = CreateProvider();

        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        Assert.Equal(ServerContext.DefaultBanFileCheckIntervalSeconds, Assert.Single(result).BanFileCheckIntervalSeconds);
    }

    private static ServerConfigFixture LoadFixture(string fixtureFileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Agents", "Fixtures", fixtureFileName);
        var json = File.ReadAllText(fixturePath);
        var fixture = JsonSerializer.Deserialize<ServerConfigFixture>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return fixture ?? throw new InvalidOperationException($"Failed to deserialize fixture '{fixtureFileName}'.");
    }

    private static ConfigurationDto ToConfigurationDto(FixtureConfiguration configuration) =>
        CreateConfigDto(configuration.Namespace, configuration.Configuration);

    private void SetupApiSuccess(GameServerDto[] dtos)
    {
        var collection = new CollectionModel<GameServerDto>(dtos);
        var apiResponse = new ApiResponse<CollectionModel<GameServerDto>>(collection);
        var apiResult = new ApiResult<CollectionModel<GameServerDto>>(HttpStatusCode.OK, apiResponse);

        _mockGameServersApi
            .Setup(a => a.GetGameServers(null, null, RepositoryGameServerFilter.AgentEnabled, 0, 50, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);
    }

    private void SetupConfigApi(Guid serverId, ConfigurationDto[] configs)
    {
        var collection = new CollectionModel<ConfigurationDto>(configs);
        var apiResponse = new ApiResponse<CollectionModel<ConfigurationDto>>(collection);
        var apiResult = new ApiResult<CollectionModel<ConfigurationDto>>(HttpStatusCode.OK, apiResponse);

        _mockConfigApi
            .Setup(a => a.GetConfigurations(serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);
    }

    private void SetupGlobalConfigApi(ConfigurationDto[] configs)
    {
        var collection = new CollectionModel<ConfigurationDto>(configs);
        var apiResponse = new ApiResponse<CollectionModel<ConfigurationDto>>(collection);
        var apiResult = new ApiResult<CollectionModel<ConfigurationDto>>(HttpStatusCode.OK, apiResponse);

        _mockGlobalConfigApi
            .Setup(a => a.GetConfigurations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);
    }

    private static ConfigurationDto CreateConfigDto(string ns, object configObj)
    {
        var dto = new ConfigurationDto();
        SetConfigProperty(dto, nameof(ConfigurationDto.Namespace), ns);
        SetConfigProperty(dto, nameof(ConfigurationDto.Configuration), JsonSerializer.Serialize(configObj));
        return dto;
    }

    private static ConfigurationDto CreateConfigDto(string ns, JsonElement configJson)
    {
        var dto = new ConfigurationDto();
        SetConfigProperty(dto, nameof(ConfigurationDto.Namespace), ns);
        SetConfigProperty(dto, nameof(ConfigurationDto.Configuration), configJson.GetRawText());
        return dto;
    }

    private static void SetConfigProperty(ConfigurationDto dto, string propertyName, object? value) =>
        typeof(ConfigurationDto).GetProperty(propertyName)!.SetValue(dto, value);

    private static GameServerDto CreateGameServerDto(
        Guid serverId, string title, RepositoryGameType gameType,
        string hostname = "localhost", int queryPort = 28960,
        bool banFileSyncEnabled = true,
        bool ftpEnabled = true,
        bool rconEnabled = true,
        string? fileTransportType = null,
        bool? fileTransportEnabled = null,
        string? banFileRootPath = null)
    {
        var dto = new GameServerDto();
        var type = typeof(GameServerDto);

        SetProperty(type, dto, nameof(GameServerDto.GameServerId), serverId);
        SetProperty(type, dto, nameof(GameServerDto.Title), title);
        SetProperty(type, dto, nameof(GameServerDto.GameType), gameType);
        SetProperty(type, dto, nameof(GameServerDto.Hostname), hostname);
        SetProperty(type, dto, nameof(GameServerDto.QueryPort), queryPort);
        SetProperty(type, dto, nameof(GameServerDto.AgentEnabled), true);
        SetProperty(type, dto, nameof(GameServerDto.BanFileSyncEnabled), banFileSyncEnabled);
        SetProperty(type, dto, nameof(GameServerDto.FtpEnabled), ftpEnabled);
        SetProperty(type, dto, nameof(GameServerDto.RconEnabled), rconEnabled);
        SetOptionalProperty(type, dto, "FileTransportType", fileTransportType);
        SetOptionalProperty(type, dto, "FileTransportEnabled", fileTransportEnabled);
        SetOptionalProperty(type, dto, "BanFileRootPath", banFileRootPath);

        return dto;
    }

    private static void SetProperty(Type type, object obj, string propertyName, object? value) =>
        type.GetProperty(propertyName)!.SetValue(obj, value);

    private static void SetOptionalProperty(Type type, object obj, string propertyName, object? value)
    {
        var property = type.GetProperty(propertyName);
        if (property is not null)
        {
            if (value is string text && property.PropertyType.IsEnum)
            {
                if (Enum.TryParse(property.PropertyType, text, ignoreCase: true, out var parsed))
                {
                    property.SetValue(obj, parsed);
                    return;
                }

                var unsupported = Enum.ToObject(property.PropertyType, 999);
                property.SetValue(obj, unsupported);
                return;
            }

            property.SetValue(obj, value);
        }
    }

    private sealed class ServerConfigFixture
    {
        public required FixtureServer Server { get; set; }
        public required List<FixtureConfiguration> Configurations { get; set; }
        public List<FixtureConfiguration> GlobalConfigurations { get; set; } = [];
    }

    private sealed class FixtureServer
    {
        public required Guid Id { get; set; }
        public required string Title { get; set; }
        public required string GameType { get; set; }
        public required string Hostname { get; set; }
        public required int QueryPort { get; set; }
        public bool BanFileSyncEnabled { get; set; }
        public bool FtpEnabled { get; set; }
        public bool RconEnabled { get; set; }
        public string? FileTransportType { get; set; }
        public bool? FileTransportEnabled { get; set; }
        public string? BanFileRootPath { get; set; }
    }

    private sealed class FixtureConfiguration
    {
        public required string Namespace { get; set; }
        public required JsonElement Configuration { get; set; }
    }
}
