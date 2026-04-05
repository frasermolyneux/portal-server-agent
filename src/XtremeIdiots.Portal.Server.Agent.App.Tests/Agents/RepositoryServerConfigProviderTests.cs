using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
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
    private readonly ILogger<RepositoryServerConfigProvider> _logger = NullLogger<RepositoryServerConfigProvider>.Instance;

    public RepositoryServerConfigProviderTests()
    {
        _mockClient.Setup(c => c.GameServers).Returns(_mockVersioned.Object);
        _mockVersioned.Setup(v => v.V1).Returns(_mockGameServersApi.Object);
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

    private void SetupApiSuccess(GameServerDto[] dtos)
    {
        var collection = new CollectionModel<GameServerDto>(dtos);
        var apiResponse = new ApiResponse<CollectionModel<GameServerDto>>(collection);
        var apiResult = new ApiResult<CollectionModel<GameServerDto>>(HttpStatusCode.OK, apiResponse);

        _mockGameServersApi
            .Setup(a => a.GetGameServers(null, null, GameServerFilter.AgentEnabled, 0, 50, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);
    }

    private static GameServerDto CreateGameServerDto(
        Guid serverId, string title, GameType gameType,
        string? ftpHostname = null, int? ftpPort = null,
        string? ftpUsername = null, string? ftpPassword = null,
        string? liveLogFile = null,
        string hostname = "localhost", int queryPort = 28960,
        string? rconPassword = null)
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

        return dto;
    }

    private static void SetProperty(Type type, object obj, string propertyName, object? value) =>
        type.GetProperty(propertyName)!.SetValue(obj, value);
}
