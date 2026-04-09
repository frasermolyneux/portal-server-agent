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

        // Default: config API returns empty for any server
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
            ftpHostname: "ftp.example.com", ftpPort: 21,
            ftpUsername: "user", ftpPassword: "pass",
            liveLogFile: "/logs/games_mp.log",
            hostname: "game.example.com", queryPort: 28960,
            rconPassword: "secret");

        SetupApiSuccess(new[] { dto });

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
        Assert.Equal("/logs/games_mp.log", server.LiveLogFile);
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
    public async Task GetAgentEnabledServersAsync_SkipsServersWithoutFtp()
    {
        // Arrange
        var completeServer = CreateGameServerDto(Guid.NewGuid(), "Complete Server", GameType.CallOfDuty4,
            ftpHostname: "ftp.example.com", ftpPort: 21,
            ftpUsername: "user", ftpPassword: "pass",
            hostname: "game.example.com", queryPort: 28960);

        var missingFtpHostname = CreateGameServerDto(Guid.NewGuid(), "No FTP Host", GameType.CallOfDuty4,
            ftpHostname: null, ftpPort: 21,
            ftpUsername: "user", ftpPassword: "pass",
            hostname: "game.example.com", queryPort: 28960);

        var missingFtpPort = CreateGameServerDto(Guid.NewGuid(), "No FTP Port", GameType.CallOfDuty4,
            ftpHostname: "ftp.example.com", ftpPort: null,
            ftpUsername: "user", ftpPassword: "pass",
            hostname: "game.example.com", queryPort: 28960);

        var missingFtpUsername = CreateGameServerDto(Guid.NewGuid(), "No FTP User", GameType.CallOfDuty4,
            ftpHostname: "ftp.example.com", ftpPort: 21,
            ftpUsername: null, ftpPassword: "pass",
            hostname: "game.example.com", queryPort: 28960);

        var missingFtpPassword = CreateGameServerDto(Guid.NewGuid(), "No FTP Pass", GameType.CallOfDuty4,
            ftpHostname: "ftp.example.com", ftpPort: 21,
            ftpUsername: "user", ftpPassword: null,
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { completeServer, missingFtpHostname, missingFtpPort, missingFtpUsername, missingFtpPassword });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — only the complete server should be returned
        Assert.Single(result);
        Assert.Equal("Complete Server", result[0].Title);
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
    public async Task GetAgentEnabledServersAsync_UsesNewConfigValuesOverDto()
    {
        // Arrange — DTO has old values, config API has new values
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Config Server", GameType.CallOfDuty4,
            ftpHostname: "old-ftp.example.com", ftpPort: 21,
            ftpUsername: "old-user", ftpPassword: "old-pass",
            liveLogFile: "/old/log.log",
            hostname: "game.example.com", queryPort: 28960,
            rconPassword: "old-rcon");

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "new-ftp.example.com", port = 2121, username = "new-user", password = "new-pass" }),
            CreateConfigDto("rcon", new { password = "new-rcon" }),
            CreateConfigDto("agent", new { logFilePath = "/new/log.log" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — new config values should be used
        Assert.Single(result);
        var server = result[0];
        Assert.Equal("new-ftp.example.com", server.FtpHostname);
        Assert.Equal(2121, server.FtpPort);
        Assert.Equal("new-user", server.FtpUsername);
        Assert.Equal("new-pass", server.FtpPassword);
        Assert.Equal("new-rcon", server.RconPassword);
        Assert.Equal("/new/log.log", server.LiveLogFile);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_FallsBackToDtoWhenConfigApiReturnsEmpty()
    {
        // Arrange — config API returns empty (default mock behavior)
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Fallback Server", GameType.CallOfDuty4,
            ftpHostname: "ftp.example.com", ftpPort: 21,
            ftpUsername: "user", ftpPassword: "pass",
            liveLogFile: "/logs/game.log",
            hostname: "game.example.com", queryPort: 28960,
            rconPassword: "rcon-secret");

        SetupApiSuccess(new[] { dto });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — DTO values should be used as fallback
        Assert.Single(result);
        var server = result[0];
        Assert.Equal("ftp.example.com", server.FtpHostname);
        Assert.Equal(21, server.FtpPort);
        Assert.Equal("user", server.FtpUsername);
        Assert.Equal("pass", server.FtpPassword);
        Assert.Equal("rcon-secret", server.RconPassword);
        Assert.Equal("/logs/game.log", server.LiveLogFile);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_FallsBackToDtoWhenConfigApiFails()
    {
        // Arrange — config API throws for this server
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Error Server", GameType.CallOfDuty4,
            ftpHostname: "ftp.example.com", ftpPort: 21,
            ftpUsername: "user", ftpPassword: "pass",
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });

        _mockConfigApi
            .Setup(a => a.GetConfigurations(serverId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Config API unreachable"));

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — should still return the server with DTO fallback values
        Assert.Single(result);
        Assert.Equal("ftp.example.com", result[0].FtpHostname);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_PartialConfigFallsBackPerField()
    {
        // Arrange — config has hostname but missing username; DTO fills the gap
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Partial Config", GameType.CallOfDuty4,
            ftpHostname: "old-ftp.example.com", ftpPort: 21,
            ftpUsername: "dto-user", ftpPassword: "dto-pass",
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { hostname = "new-ftp.example.com" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — hostname from config, rest from DTO
        Assert.Single(result);
        var server = result[0];
        Assert.Equal("new-ftp.example.com", server.FtpHostname);
        Assert.Equal(21, server.FtpPort);
        Assert.Equal("dto-user", server.FtpUsername);
        Assert.Equal("dto-pass", server.FtpPassword);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_MalformedJsonFallsBackToDto()
    {
        // Arrange — config has malformed JSON
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "Bad JSON Server", GameType.CallOfDuty4,
            ftpHostname: "ftp.example.com", ftpPort: 21,
            ftpUsername: "user", ftpPassword: "pass",
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });

        var malformedConfig = new ConfigurationDto();
        SetConfigProperty(malformedConfig, nameof(ConfigurationDto.Namespace), "ftp");
        SetConfigProperty(malformedConfig, nameof(ConfigurationDto.Configuration), "not valid json {{{");

        SetupConfigApi(serverId, new[] { malformedConfig });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — should fall back to DTO values
        Assert.Single(result);
        Assert.Equal("ftp.example.com", result[0].FtpHostname);
    }

    [Fact]
    public async Task GetAgentEnabledServersAsync_SetsBanFileSyncEnabled()
    {
        // Arrange
        var serverId = Guid.NewGuid();
        var dto = CreateGameServerDto(serverId, "BanSync Server", GameType.CallOfDuty4,
            ftpHostname: "ftp.example.com", ftpPort: 21,
            ftpUsername: "user", ftpPassword: "pass",
            hostname: "game.example.com", queryPort: 28960,
            banFileSyncEnabled: false);

        SetupApiSuccess(new[] { dto });

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
            ftpHostname: "ftp.example.com", ftpPort: 21,
            ftpUsername: "user", ftpPassword: "pass",
            hostname: "game.example.com", queryPort: 28960);

        SetupApiSuccess(new[] { dto });
        SetupConfigApi(serverId, new[]
        {
            CreateConfigDto("ftp", new { port = "2222" })
        });

        var provider = CreateProvider();

        // Act
        var result = await provider.GetAgentEnabledServersAsync(CancellationToken.None);

        // Assert — string port should be parsed
        Assert.Single(result);
        Assert.Equal(2222, result[0].FtpPort);
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
        string? ftpHostname = null, int? ftpPort = null,
        string? ftpUsername = null, string? ftpPassword = null,
        string? liveLogFile = null,
        string hostname = "localhost", int queryPort = 28960,
        string? rconPassword = null,
        bool banFileSyncEnabled = true)
    {
        // GameServerDto properties have internal setters, so we use reflection
        var dto = new GameServerDto();
        var type = typeof(GameServerDto);

        SetProperty(type, dto, nameof(GameServerDto.GameServerId), serverId);
        SetProperty(type, dto, nameof(GameServerDto.Title), title);
        SetProperty(type, dto, nameof(GameServerDto.GameType), gameType);
        SetProperty(type, dto, nameof(GameServerDto.Hostname), hostname);
        SetProperty(type, dto, nameof(GameServerDto.QueryPort), queryPort);
        SetProperty(type, dto, nameof(GameServerDto.FtpHostname), ftpHostname);
        SetProperty(type, dto, nameof(GameServerDto.FtpPort), ftpPort);
        SetProperty(type, dto, nameof(GameServerDto.FtpUsername), ftpUsername);
        SetProperty(type, dto, nameof(GameServerDto.FtpPassword), ftpPassword);
        SetProperty(type, dto, nameof(GameServerDto.LiveLogFile), liveLogFile);
        SetProperty(type, dto, nameof(GameServerDto.RconPassword), rconPassword);
        SetProperty(type, dto, nameof(GameServerDto.AgentEnabled), true);
        SetProperty(type, dto, nameof(GameServerDto.BanFileSyncEnabled), banFileSyncEnabled);

        return dto;
    }

    private static void SetProperty(Type type, object obj, string propertyName, object? value) =>
        type.GetProperty(propertyName)!.SetValue(obj, value);
}
