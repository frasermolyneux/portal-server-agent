using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class RepositoryServerConfigProviderTests
{
    private readonly Mock<IRepositoryApiClient> _mockClient = new();
    private readonly Mock<IVersionedGameServersApi> _mockVersioned = new();
    private readonly Mock<IGameServersApi> _mockGameServersApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _mockVersionedConfigs = new();
    private readonly Mock<IGameServerConfigurationsApi> _mockConfigApi = new();
    private readonly ILogger<RepositoryServerConfigProvider> _logger = NullLogger<RepositoryServerConfigProvider>.Instance;

    public RepositoryServerConfigProviderTests()
    {
        _mockClient.Setup(c => c.GameServers).Returns(_mockVersioned.Object);
        _mockVersioned.Setup(v => v.V1).Returns(_mockGameServersApi.Object);
        _mockClient.Setup(c => c.GameServerConfigurations).Returns(_mockVersionedConfigs.Object);
        _mockVersionedConfigs.Setup(v => v.V1).Returns(_mockConfigApi.Object);

        // Default: config API returns empty for any server (server will be skipped)
        _mockConfigApi
            .Setup(a => a.GetConfigurations(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
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
        var dto = CreateGameServerDto(serverId, "Test Server", GameType.CallOfDuty4,
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
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_WhenApiFails_ReturnsEmptyList()
    {
        // Arrange
        var apiResult = new ApiResult<CollectionModel<GameServerDto>>(HttpStatusCode.InternalServerError);

        _mockGameServersApi
            .Setup(a => a.GetGameServers(null, null, GameServerFilter.AgentEnabled, 0, 50, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SkipsServersWithMissingFtpConfig()
    {
        // Arrange — config has rcon + agent but no ftp namespace
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "No FTP Config", GameType.CallOfDuty4,
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
        // Arrange — config has ftp + agent but no rcon namespace
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "No RCON Config", GameType.CallOfDuty4,
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
        // Arrange — config has ftp + rcon but no agent namespace
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "No Agent Config", GameType.CallOfDuty4,
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
            .Setup(a => a.GetGameServers(null, null, GameServerFilter.AgentEnabled, 0, 50, null, It.IsAny<CancellationToken>()))
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
        var dto = CreateGameServerDto(serverId, "Empty Config Server", GameType.CallOfDuty4,
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
        var dto = CreateGameServerDto(serverId, "Error Server", GameType.CallOfDuty4,
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
    public async Task GetAgentEnabledServersAsync_MalformedJsonSkipsServer()
    {
        // Arrange — ftp config has malformed JSON, so ftp namespace is missing
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Bad JSON Server", GameType.CallOfDuty4,
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

        // Assert — malformed ftp config means server is skipped
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SetsBanFileSyncEnabled()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "BanSync Server", GameType.CallOfDuty4,
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
        var dto = CreateGameServerDto(serverId, "String Port", GameType.CallOfDuty4,
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
    public async Task GetAgentEnabledServersAsync_SkipsServersWithIncompleteFtpConfig()
    {
        // Arrange — ftp namespace exists but is missing required keys
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Incomplete FTP", GameType.CallOfDuty4,
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

        // Assert — incomplete ftp config means server is skipped
        Assert.Empty(result);
    }

    private void SetupApiSuccess(GameServerDto[] dtos)
    {
        var collection = new CollectionModel<GameServerDto>(dtos);
        var apiResponse = new ApiResponse<CollectionModel<GameServerDto>>(collection);
        var apiResult = new ApiResult<CollectionModel<GameServerDto>>(HttpStatusCode.OK, apiResponse);

        _mockGameServersApi
            .Setup(a => a.GetGameServers(null, null, GameServerFilter.AgentEnabled, 0, 50, null, It.IsAny<CancellationToken>()))
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

    private static ConfigurationDto CreateConfigDto(string ns, object configObj)
    {
        var dto = new ConfigurationDto();
        SetConfigProperty(dto, nameof(ConfigurationDto.Namespace), ns);
        SetConfigProperty(dto, nameof(ConfigurationDto.Configuration), JsonSerializer.Serialize(configObj));
        return dto;
    }

    private static void SetConfigProperty(ConfigurationDto dto, string propertyName, object? value) =>
        typeof(ConfigurationDto).GetProperty(propertyName)!.SetValue(dto, value);

    private static GameServerDto CreateGameServerDto(
        Guid serverId, string title, GameType gameType,
        string hostname = "localhost", int queryPort = 28960,
        bool banFileSyncEnabled = true,
        bool ftpEnabled = true,
        bool rconEnabled = true)
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

        return dto;
    }

    private static void SetProperty(Type type, object obj, string propertyName, object? value) =>
        type.GetProperty(propertyName)!.SetValue(obj, value);
}
