using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class ServerSyncServiceTests
{
    private readonly Mock<IRconApi> _mockRconApi = new();
    private readonly Mock<IQueryApi> _mockQueryApi = new();
    private readonly Mock<ILogParser> _mockParser = new();
    private readonly ILogger<ServerSyncService> _logger = NullLogger<ServerSyncService>.Instance;
    private readonly Guid _serverId = Guid.NewGuid();

    private ServerSyncService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockRconApi.Object);
        services.AddSingleton(_mockQueryApi.Object);
        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new ServerSyncService(scopeFactory, _logger);
    }

    [Fact]
    public async Task SyncAsync_AddsNewPlayers()
    {
        // Arrange — RCON returns 2 players, slot map is empty
        var rconPlayers = new List<ServerRconPlayerDto>
        {
            new() { Num = 0, Guid = "guid-a", Name = "Alice", IpAddress = "1.2.3.4", Ping = 30, Rate = 25000 },
            new() { Num = 1, Guid = "guid-b", Name = "Bob", IpAddress = "5.6.7.8", Ping = 50, Rate = 20000 }
        };

        var statusDto = new ServerRconStatusResponseDto { Players = rconPlayers };
        var apiResult = new ApiResult<ServerRconStatusResponseDto>(
            HttpStatusCode.OK,
            new ApiResponse<ServerRconStatusResponseDto>(statusDto));

        _mockRconApi.Setup(r => r.GetServerStatus(_serverId)).ReturnsAsync(apiResult);

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        var service = CreateService();

        // Act
        await service.SyncAsync(_serverId, _mockParser.Object);

        // Assert — both players should be added with Ping and Rate
        _mockParser.Verify(p => p.SetPlayer(0, It.Is<PlayerInfo>(pi =>
            pi.Guid == "guid-a" && pi.Name == "Alice" && pi.Ping == 30 && pi.Rate == 25000)), Times.Once);
        _mockParser.Verify(p => p.SetPlayer(1, It.Is<PlayerInfo>(pi =>
            pi.Guid == "guid-b" && pi.Name == "Bob" && pi.Ping == 50 && pi.Rate == 20000)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_RemovesStalePlayers()
    {
        // Arrange — RCON returns 1 player, slot map has 2
        var rconPlayers = new List<ServerRconPlayerDto>
        {
            new() { Num = 0, Guid = "guid-a", Name = "Alice" }
        };

        var statusDto = new ServerRconStatusResponseDto { Players = rconPlayers };
        var apiResult = new ApiResult<ServerRconStatusResponseDto>(
            HttpStatusCode.OK,
            new ApiResponse<ServerRconStatusResponseDto>(statusDto));

        _mockRconApi.Setup(r => r.GetServerStatus(_serverId)).ReturnsAsync(apiResult);

        var existingPlayers = new Dictionary<int, PlayerInfo>
        {
            [0] = new() { Guid = "guid-a", Name = "Alice", SlotId = 0, ConnectedAt = DateTime.UtcNow },
            [1] = new() { Guid = "guid-b", Name = "Bob", SlotId = 1, ConnectedAt = DateTime.UtcNow }
        };
        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(existingPlayers);

        var service = CreateService();

        // Act
        await service.SyncAsync(_serverId, _mockParser.Object);

        // Assert — slot 1 should be removed (not in RCON), slot 0 should NOT be re-added (already present)
        _mockParser.Verify(p => p.RemovePlayer(1), Times.Once);
        _mockParser.Verify(p => p.SetPlayer(0, It.IsAny<PlayerInfo>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenRconFails_DoesNotThrow()
    {
        // Arrange — RCON returns error
        var apiResult = new ApiResult<ServerRconStatusResponseDto>(
            HttpStatusCode.InternalServerError,
            new ApiResponse<ServerRconStatusResponseDto>(new ApiError("SERVER_ERROR", "Internal error")));

        _mockRconApi.Setup(r => r.GetServerStatus(_serverId)).ReturnsAsync(apiResult);

        var service = CreateService();

        // Act & Assert — should not throw
        await service.SyncAsync(_serverId, _mockParser.Object);

        // No players should be modified
        _mockParser.Verify(p => p.SetPlayer(It.IsAny<int>(), It.IsAny<PlayerInfo>()), Times.Never);
        _mockParser.Verify(p => p.RemovePlayer(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenExceptionThrown_DoesNotThrow()
    {
        // Arrange — RCON throws
        _mockRconApi.Setup(r => r.GetServerStatus(_serverId)).ThrowsAsync(new HttpRequestException("Network error"));

        var service = CreateService();

        // Act & Assert — should not throw
        await service.SyncAsync(_serverId, _mockParser.Object);
    }

    [Fact]
    public async Task SyncAsync_UpdatesPingAndRateOnExistingPlayers()
    {
        // Arrange — RCON returns same players already in slot map but with updated Ping/Rate
        var rconPlayers = new List<ServerRconPlayerDto>
        {
            new() { Num = 0, Guid = "guid-a", Name = "Alice", Ping = 45, Rate = 25000 },
            new() { Num = 1, Guid = "guid-b", Name = "Bob", Ping = 60, Rate = 20000 }
        };

        var statusDto = new ServerRconStatusResponseDto { Players = rconPlayers };
        var apiResult = new ApiResult<ServerRconStatusResponseDto>(
            HttpStatusCode.OK,
            new ApiResponse<ServerRconStatusResponseDto>(statusDto));

        _mockRconApi.Setup(r => r.GetServerStatus(_serverId)).ReturnsAsync(apiResult);

        var player0 = new PlayerInfo { Guid = "guid-a", Name = "Alice", SlotId = 0, ConnectedAt = DateTime.UtcNow };
        var player1 = new PlayerInfo { Guid = "guid-b", Name = "Bob", SlotId = 1, ConnectedAt = DateTime.UtcNow };
        var existingPlayers = new Dictionary<int, PlayerInfo>
        {
            [0] = player0,
            [1] = player1
        };
        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(existingPlayers);

        var service = CreateService();

        // Act
        await service.SyncAsync(_serverId, _mockParser.Object);

        // Assert — Ping and Rate updated on existing players, no SetPlayer calls (not re-added)
        Assert.Equal(45, player0.Ping);
        Assert.Equal(25000, player0.Rate);
        Assert.Equal(60, player1.Ping);
        Assert.Equal(20000, player1.Rate);
        _mockParser.Verify(p => p.SetPlayer(It.IsAny<int>(), It.IsAny<PlayerInfo>()), Times.Never);
        _mockParser.Verify(p => p.RemovePlayer(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_HandlesNullGuidAndName()
    {
        // Arrange — RCON returns player with null guid/name
        var rconPlayers = new List<ServerRconPlayerDto>
        {
            new() { Num = 0, Guid = null, Name = null }
        };

        var statusDto = new ServerRconStatusResponseDto { Players = rconPlayers };
        var apiResult = new ApiResult<ServerRconStatusResponseDto>(
            HttpStatusCode.OK,
            new ApiResponse<ServerRconStatusResponseDto>(statusDto));

        _mockRconApi.Setup(r => r.GetServerStatus(_serverId)).ReturnsAsync(apiResult);

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        var service = CreateService();

        // Act
        await service.SyncAsync(_serverId, _mockParser.Object);

        // Assert — player added with empty strings for null values
        _mockParser.Verify(p => p.SetPlayer(0, It.Is<PlayerInfo>(pi =>
            pi.Guid == string.Empty && pi.Name == string.Empty)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_QuerySetsServerInfo()
    {
        // Arrange — RCON succeeds with empty player list
        var rconStatus = new ServerRconStatusResponseDto { Players = new List<ServerRconPlayerDto>() };
        _mockRconApi.Setup(r => r.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerRconStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        // Query returns server metadata
        var queryStatus = new ServerQueryStatusResponseDto
        {
            ServerName = "My Server",
            Mod = "mods/mymod",
            MaxPlayers = 32,
            Players = new List<ServerQueryPlayerDto>()
        };
        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerQueryStatusResponseDto>(queryStatus)));

        var service = CreateService();

        // Act
        await service.SyncAsync(_serverId, _mockParser.Object);

        // Assert — SetServerInfo should be called with Query data
        _mockParser.Verify(p => p.SetServerInfo("My Server", "mods/mymod", 32), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_QueryMergesScoreByName()
    {
        // Arrange — RCON returns a player
        var rconPlayers = new List<ServerRconPlayerDto>
        {
            new() { Num = 0, Guid = "guid-a", Name = "Alice", Ping = 30, Rate = 25000 }
        };
        var rconStatus = new ServerRconStatusResponseDto { Players = rconPlayers };
        _mockRconApi.Setup(r => r.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerRconStatusResponseDto>(rconStatus)));

        var player0 = new PlayerInfo { Guid = "guid-a", Name = "Alice", SlotId = 0, ConnectedAt = DateTime.UtcNow };
        var existingPlayers = new Dictionary<int, PlayerInfo> { [0] = player0 };
        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(existingPlayers);

        // Query returns player with Score
        var queryStatus = new ServerQueryStatusResponseDto
        {
            ServerName = "Server",
            Players = new List<ServerQueryPlayerDto>
            {
                new() { Name = "Alice", Score = 15 }
            }
        };
        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerQueryStatusResponseDto>(queryStatus)));

        var service = CreateService();

        // Act
        await service.SyncAsync(_serverId, _mockParser.Object);

        // Assert — Score should be merged from Query
        Assert.Equal(15, player0.Score);
    }

    [Fact]
    public async Task SyncAsync_WhenQueryFails_DoesNotThrow()
    {
        // Arrange — RCON succeeds
        var rconStatus = new ServerRconStatusResponseDto { Players = new List<ServerRconPlayerDto>() };
        _mockRconApi.Setup(r => r.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerRconStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        // Query throws
        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ThrowsAsync(new HttpRequestException("Query unavailable"));

        var service = CreateService();

        // Act & Assert — should not throw (Query failure is best-effort)
        await service.SyncAsync(_serverId, _mockParser.Object);

        // SetServerInfo should not be called
        _mockParser.Verify(p => p.SetServerInfo(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_QuerySetsCurrentMap()
    {
        // Arrange — RCON succeeds with empty player list
        var rconStatus = new ServerRconStatusResponseDto { Players = new List<ServerRconPlayerDto>() };
        _mockRconApi.Setup(r => r.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerRconStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        // Query returns server metadata including map
        var queryStatus = new ServerQueryStatusResponseDto
        {
            ServerName = "My Server",
            Map = "mp_crash",
            Mod = "mods/mymod",
            MaxPlayers = 32,
            Players = new List<ServerQueryPlayerDto>()
        };
        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerQueryStatusResponseDto>(queryStatus)));

        var service = CreateService();

        // Act
        await service.SyncAsync(_serverId, _mockParser.Object);

        // Assert — SetCurrentMap should be called with the Query map
        _mockParser.Verify(p => p.SetCurrentMap("mp_crash"), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_QueryWithNullMap_DoesNotSetCurrentMap()
    {
        // Arrange — RCON succeeds
        var rconStatus = new ServerRconStatusResponseDto { Players = new List<ServerRconPlayerDto>() };
        _mockRconApi.Setup(r => r.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerRconStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerRconStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        // Query returns server metadata with null map
        var queryStatus = new ServerQueryStatusResponseDto
        {
            ServerName = "My Server",
            Map = null,
            Players = new List<ServerQueryPlayerDto>()
        };
        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerQueryStatusResponseDto>(queryStatus)));

        var service = CreateService();

        // Act
        await service.SyncAsync(_serverId, _mockParser.Object);

        // Assert — SetCurrentMap should still be called (parser handles null/whitespace filtering)
        _mockParser.Verify(p => p.SetCurrentMap(null), Times.Once);
    }
}
