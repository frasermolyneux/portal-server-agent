using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class ServerSyncServiceTests
{
    private readonly Mock<IServersApiClient> _mockServersApiClient = new();
    private readonly Mock<IVersionedCoD4xRconApi> _mockVersionedCoD4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> _mockCoD4xRconApi = new();
    private readonly Mock<IVersionedCod2RconApi> _mockVersionedCod2RconApi = new();
    private readonly Mock<ICod2RconApi> _mockCod2RconApi = new();
    private readonly Mock<IVersionedCod4RconApi> _mockVersionedCod4RconApi = new();
    private readonly Mock<ICod4RconApi> _mockCod4RconApi = new();
    private readonly Mock<IVersionedCod5RconApi> _mockVersionedCod5RconApi = new();
    private readonly Mock<ICod5RconApi> _mockCod5RconApi = new();
    private readonly Mock<IQueryApi> _mockQueryApi = new();
    private readonly Mock<ICoD4xBanReconciliationService> _mockCoD4xReconciliationService = new();
    private readonly Mock<ICoD4xAdminReconciliationService> _mockCoD4xAdminReconciliationService = new();
    private readonly Mock<ICoD4xCommandPowerReconciliationService> _mockCoD4xCommandPowerReconciliationService = new();
    private readonly Mock<ILogParser> _mockParser = new();
    private readonly ILogger<ServerSyncService> _logger = NullLogger<ServerSyncService>.Instance;
    private readonly Guid _serverId = Guid.NewGuid();

    private ServerSyncService CreateService(
        bool includeCoD4xReconciliationService = true,
        bool includeCoD4xAdminReconciliationService = true,
        bool includeCoD4xCommandPowerReconciliationService = true)
    {
        _mockVersionedCoD4xRconApi.Setup(x => x.V1).Returns(_mockCoD4xRconApi.Object);
        _mockVersionedCod2RconApi.Setup(x => x.V1).Returns(_mockCod2RconApi.Object);
        _mockVersionedCod4RconApi.Setup(x => x.V1).Returns(_mockCod4RconApi.Object);
        _mockVersionedCod5RconApi.Setup(x => x.V1).Returns(_mockCod5RconApi.Object);
        _mockServersApiClient.Setup(x => x.CoD4xRcon).Returns(_mockVersionedCoD4xRconApi.Object);
        _mockServersApiClient.Setup(x => x.Cod2Rcon).Returns(_mockVersionedCod2RconApi.Object);
        _mockServersApiClient.Setup(x => x.Cod4Rcon).Returns(_mockVersionedCod4RconApi.Object);
        _mockServersApiClient.Setup(x => x.Cod5Rcon).Returns(_mockVersionedCod5RconApi.Object);

        var services = new ServiceCollection();
        services.AddSingleton(_mockServersApiClient.Object);
        services.AddSingleton(_mockQueryApi.Object);

        if (includeCoD4xReconciliationService)
        {
            services.AddScoped(_ => _mockCoD4xReconciliationService.Object);
        }

        if (includeCoD4xAdminReconciliationService)
        {
            services.AddScoped(_ => _mockCoD4xAdminReconciliationService.Object);
        }

        if (includeCoD4xCommandPowerReconciliationService)
        {
            services.AddScoped(_ => _mockCoD4xCommandPowerReconciliationService.Object);
        }

        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new ServerSyncService(scopeFactory, _logger);
    }

    [Fact]
    public async Task SyncAsync_AddsNewPlayers()
    {
        var rconPlayers = new List<CoD4xStatusPlayerDto>
        {
            new() { Num = 0, PlayerIdentifier = "guid-a", Name = "Alice", IpAddress = "1.2.3.4", Ping = 30, Rate = 25000 },
            new() { Num = 1, PlayerIdentifier = "guid-b", Name = "Bob", IpAddress = "5.6.7.8", Ping = 50, Rate = 20000 }
        };

        var statusDto = new CoD4xStatusResponseDto { Players = rconPlayers };
        var apiResult = new ApiResult<CoD4xStatusResponseDto>(
            HttpStatusCode.OK,
            new ApiResponse<CoD4xStatusResponseDto>(statusDto));

        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>())).ReturnsAsync(apiResult);

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        _mockParser.Verify(p => p.SetPlayer(0, It.Is<PlayerInfo>(pi =>
            pi.Guid == "guid-a" && pi.Name == "Alice" && pi.IpAddress == "1.2.3.4" && pi.Ping == 30 && pi.Rate == 25000)), Times.Once);
        _mockParser.Verify(p => p.SetPlayer(1, It.Is<PlayerInfo>(pi =>
            pi.Guid == "guid-b" && pi.Name == "Bob" && pi.IpAddress == "5.6.7.8" && pi.Ping == 50 && pi.Rate == 20000)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_RemovesStalePlayers()
    {
        var rconPlayers = new List<CoD4xStatusPlayerDto>
        {
            new() { Num = 0, PlayerIdentifier = "guid-a", Name = "Alice" }
        };

        var statusDto = new CoD4xStatusResponseDto { Players = rconPlayers };
        var apiResult = new ApiResult<CoD4xStatusResponseDto>(
            HttpStatusCode.OK,
            new ApiResponse<CoD4xStatusResponseDto>(statusDto));

        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>())).ReturnsAsync(apiResult);

        var existingPlayers = new Dictionary<int, PlayerInfo>
        {
            [0] = new() { Guid = "guid-a", Name = "Alice", SlotId = 0, ConnectedAt = DateTime.UtcNow },
            [1] = new() { Guid = "guid-b", Name = "Bob", SlotId = 1, ConnectedAt = DateTime.UtcNow }
        };
        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(existingPlayers);

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        _mockParser.Verify(p => p.RemovePlayer(1), Times.Once);
        _mockParser.Verify(p => p.SetPlayer(0, It.IsAny<PlayerInfo>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenRconFails_DoesNotThrow()
    {
        var apiResult = new ApiResult<CoD4xStatusResponseDto>(
            HttpStatusCode.InternalServerError,
            new ApiResponse<CoD4xStatusResponseDto>(new ApiError("SERVER_ERROR", "Internal error")));

        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>())).ReturnsAsync(apiResult);

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        _mockParser.Verify(p => p.SetPlayer(It.IsAny<int>(), It.IsAny<PlayerInfo>()), Times.Never);
        _mockParser.Verify(p => p.RemovePlayer(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenExceptionThrown_DoesNotThrow()
    {
        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("Network error"));

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);
    }

    [Fact]
    public async Task SyncAsync_UpdatesPingAndRateOnExistingPlayers()
    {
        var rconPlayers = new List<CoD4xStatusPlayerDto>
        {
            new() { Num = 0, PlayerIdentifier = "guid-a", Name = "Alice", Ping = 45, Rate = 25000 },
            new() { Num = 1, PlayerIdentifier = "guid-b", Name = "Bob", Ping = 60, Rate = 20000 }
        };

        var statusDto = new CoD4xStatusResponseDto { Players = rconPlayers };
        var apiResult = new ApiResult<CoD4xStatusResponseDto>(
            HttpStatusCode.OK,
            new ApiResponse<CoD4xStatusResponseDto>(statusDto));

        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>())).ReturnsAsync(apiResult);

        var player0 = new PlayerInfo { Guid = "guid-a", Name = "Alice", SlotId = 0, ConnectedAt = DateTime.UtcNow };
        var player1 = new PlayerInfo { Guid = "guid-b", Name = "Bob", SlotId = 1, ConnectedAt = DateTime.UtcNow };
        var existingPlayers = new Dictionary<int, PlayerInfo>
        {
            [0] = player0,
            [1] = player1
        };
        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(existingPlayers);

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        Assert.Equal(45, player0.Ping);
        Assert.Equal(25000, player0.Rate);
        Assert.Equal(60, player1.Ping);
        Assert.Equal(20000, player1.Rate);
        _mockParser.Verify(p => p.SetPlayer(It.IsAny<int>(), It.IsAny<PlayerInfo>()), Times.Never);
        _mockParser.Verify(p => p.RemovePlayer(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_HandlesEmptyGuidAndName()
    {
        var rconPlayers = new List<CoD4xStatusPlayerDto>
        {
            new() { Num = 0, PlayerIdentifier = string.Empty, Name = string.Empty }
        };

        var statusDto = new CoD4xStatusResponseDto { Players = rconPlayers };
        var apiResult = new ApiResult<CoD4xStatusResponseDto>(
            HttpStatusCode.OK,
            new ApiResponse<CoD4xStatusResponseDto>(statusDto));

        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>())).ReturnsAsync(apiResult);

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        _mockParser.Verify(p => p.SetPlayer(0, It.Is<PlayerInfo>(pi =>
            pi.Guid == string.Empty && pi.Name == string.Empty)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_QuerySetsServerInfo()
    {
        var rconStatus = new CoD4xStatusResponseDto { Players = [] };
        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        var queryStatus = new ServerQueryStatusResponseDto
        {
            ServerName = "My Server",
            Mod = "mods/mymod",
            MaxPlayers = 32,
            Players = []
        };
        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerQueryStatusResponseDto>(queryStatus)));

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        _mockParser.Verify(p => p.SetServerInfo("My Server", "mods/mymod", 32), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_QueryMergesScoreByName()
    {
        var rconPlayers = new List<CoD4xStatusPlayerDto>
        {
            new() { Num = 0, PlayerIdentifier = "guid-a", Name = "Alice", Ping = 30, Rate = 25000 }
        };
        var rconStatus = new CoD4xStatusResponseDto { Players = rconPlayers };
        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(rconStatus)));

        var player0 = new PlayerInfo { Guid = "guid-a", Name = "Alice", SlotId = 0, ConnectedAt = DateTime.UtcNow };
        var existingPlayers = new Dictionary<int, PlayerInfo> { [0] = player0 };
        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(existingPlayers);

        var queryStatus = new ServerQueryStatusResponseDto
        {
            ServerName = "Server",
            Players = [new ServerQueryPlayerDto { Name = "Alice", Score = 15 }]
        };
        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerQueryStatusResponseDto>(queryStatus)));

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        Assert.Equal(15, player0.Score);
    }

    [Fact]
    public async Task SyncAsync_WhenQueryFails_DoesNotThrow()
    {
        var rconStatus = new CoD4xStatusResponseDto { Players = [] };
        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ThrowsAsync(new HttpRequestException("Query unavailable"));

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        _mockParser.Verify(p => p.SetServerInfo(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_QuerySetsCurrentMap()
    {
        var rconStatus = new CoD4xStatusResponseDto { Players = [] };
        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        var queryStatus = new ServerQueryStatusResponseDto
        {
            ServerName = "My Server",
            Map = "mp_crash",
            Mod = "mods/mymod",
            MaxPlayers = 32,
            Players = []
        };
        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerQueryStatusResponseDto>(queryStatus)));

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        _mockParser.Verify(p => p.SetCurrentMap("mp_crash"), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_QueryWithNullMap_DoesNotSetCurrentMap()
    {
        var rconStatus = new CoD4xStatusResponseDto { Players = [] };
        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<CoD4xStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        var queryStatus = new ServerQueryStatusResponseDto
        {
            ServerName = "My Server",
            Map = null,
            Players = []
        };
        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK, new ApiResponse<ServerQueryStatusResponseDto>(queryStatus)));

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object);

        _mockParser.Verify(p => p.SetCurrentMap(null), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_CallsCoD4xReconciliationService_WhenRegistered()
    {
        var rconStatus = new CoD4xStatusResponseDto { Players = [] };
        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(new Dictionary<int, PlayerInfo>());

        var service = CreateService(includeCoD4xReconciliationService: true);

        await service.SyncAsync(_serverId, _mockParser.Object, "CallOfDuty4x");

        _mockCoD4xReconciliationService.Verify(x => x.ReconcileAsync(
            _serverId,
            "CallOfDuty4x",
            It.IsAny<CancellationToken>()), Times.Once);

        _mockCoD4xAdminReconciliationService.Verify(x => x.ReconcileAsync(
            _serverId,
            "CallOfDuty4x",
            It.IsAny<CancellationToken>()), Times.Once);

        _mockCoD4xCommandPowerReconciliationService.Verify(x => x.ReconcileAsync(
            _serverId,
            "CallOfDuty4x",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_DoesNotThrow_WhenCoD4xReconciliationServiceIsNotRegistered()
    {
        var rconStatus = new CoD4xStatusResponseDto { Players = [] };
        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(new Dictionary<int, PlayerInfo>());

        var service = CreateService(
            includeCoD4xReconciliationService: false,
            includeCoD4xAdminReconciliationService: false,
            includeCoD4xCommandPowerReconciliationService: false);

        await service.SyncAsync(_serverId, _mockParser.Object, "CallOfDuty4x");
    }

    [Fact]
    public async Task SyncAsync_CallsAdminReconciliation_WhenBanReconciliationServiceIsNotRegistered()
    {
        var rconStatus = new CoD4xStatusResponseDto { Players = [] };
        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(rconStatus)));

        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(new Dictionary<int, PlayerInfo>());

        var service = CreateService(includeCoD4xReconciliationService: false, includeCoD4xAdminReconciliationService: true);

        await service.SyncAsync(_serverId, _mockParser.Object, "CallOfDuty4x");

        _mockCoD4xReconciliationService.Verify(x => x.ReconcileAsync(
            It.IsAny<Guid>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _mockCoD4xAdminReconciliationService.Verify(x => x.ReconcileAsync(
            _serverId,
            "CallOfDuty4x",
            It.IsAny<CancellationToken>()), Times.Once);

        _mockCoD4xCommandPowerReconciliationService.Verify(x => x.ReconcileAsync(
            _serverId,
            "CallOfDuty4x",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenGameTypeIsCod4_UsesCod4RconAndRunsQuerySync()
    {
        var cod4Status = new RconStatusResponseDto
        {
            Players = [new RconStatusPlayerDto { Num = 3, Guid = "guid-cod4", Name = "LegacyPlayer", IpAddress = "10.0.0.4", Ping = 44, Rate = 18000 }]
        };
        _mockCod4RconApi.Setup(x => x.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<RconStatusResponseDto>(cod4Status)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        var queryStatus = new ServerQueryStatusResponseDto
        {
            ServerName = "Legacy Server",
            Mod = "mods/legacy",
            MaxPlayers = 24,
            Map = "mp_backlot",
            Players = []
        };

        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<ServerQueryStatusResponseDto>(queryStatus)));

        var service = CreateService(includeCoD4xReconciliationService: true);

        await service.SyncAsync(_serverId, _mockParser.Object, "CallOfDuty4");

        _mockCoD4xRconApi.Verify(x => x.Status(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod4RconApi.Verify(x => x.Status(_serverId, It.IsAny<CancellationToken>()), Times.Once);
        _mockQueryApi.Verify(x => x.GetServerStatus(_serverId), Times.Once);
        _mockParser.Verify(x => x.SetPlayer(3, It.Is<PlayerInfo>(p =>
            p.Guid == "guid-cod4" &&
            p.Name == "LegacyPlayer" &&
            p.IpAddress == "10.0.0.4" &&
            p.Ping == 44 &&
            p.Rate == 18000)), Times.Once);
        _mockParser.Verify(x => x.SetServerInfo("Legacy Server", "mods/legacy", 24), Times.Once);
        _mockParser.Verify(x => x.SetCurrentMap("mp_backlot"), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenGameTypeIsCod5_DoesNotInvokeCoD4xReconciliation()
    {
        var cod5Status = new RconStatusResponseDto
        {
            Players = [new RconStatusPlayerDto { Num = 7, Guid = "guid-cod5", Name = "Cod5Player", IpAddress = "10.0.0.5", Ping = 55, Rate = 19000 }]
        };
        _mockCod5RconApi.Setup(x => x.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<RconStatusResponseDto>(cod5Status)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<ServerQueryStatusResponseDto>(new ServerQueryStatusResponseDto { Players = [] })));

        var service = CreateService(includeCoD4xReconciliationService: true);

        await service.SyncAsync(_serverId, _mockParser.Object, "CallOfDuty5");

        _mockCod5RconApi.Verify(x => x.Status(_serverId, It.IsAny<CancellationToken>()), Times.Once);
        _mockCoD4xReconciliationService.Verify(x => x.ReconcileAsync(
            It.IsAny<Guid>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _mockCoD4xAdminReconciliationService.Verify(x => x.ReconcileAsync(
            It.IsAny<Guid>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _mockCoD4xCommandPowerReconciliationService.Verify(x => x.ReconcileAsync(
            It.IsAny<Guid>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_WhenGameTypeIsCod2_UsesCod2RconStatus()
    {
        var cod2Status = new RconStatusResponseDto
        {
            Players = [new RconStatusPlayerDto { Num = 2, Guid = "guid-cod2", Name = "Cod2Player", IpAddress = "10.0.0.2", Ping = 32, Rate = 16000 }]
        };

        _mockCod2RconApi.Setup(x => x.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<RconStatusResponseDto>(cod2Status)));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<ServerQueryStatusResponseDto>(new ServerQueryStatusResponseDto { Players = [] })));

        var service = CreateService(includeCoD4xReconciliationService: true);

        await service.SyncAsync(_serverId, _mockParser.Object, "CallOfDuty2");

        _mockCod2RconApi.Verify(x => x.Status(_serverId, It.IsAny<CancellationToken>()), Times.Once);
        _mockParser.Verify(x => x.SetPlayer(2, It.Is<PlayerInfo>(p =>
            p.Guid == "guid-cod2" &&
            p.Name == "Cod2Player" &&
            p.IpAddress == "10.0.0.2" &&
            p.Ping == 32 &&
            p.Rate == 16000)), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenCod4RconFails_StillRunsQuerySync()
    {
        _mockCod4RconApi.Setup(x => x.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<RconStatusResponseDto>(
                HttpStatusCode.InternalServerError,
                new ApiResponse<RconStatusResponseDto>(new ApiError("SERVER_ERROR", "status failed"))));

        _mockParser.SetupGet(p => p.ConnectedPlayers)
            .Returns(new Dictionary<int, PlayerInfo>());

        _mockQueryApi.Setup(q => q.GetServerStatus(_serverId))
            .ReturnsAsync(new ApiResult<ServerQueryStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<ServerQueryStatusResponseDto>(new ServerQueryStatusResponseDto
                {
                    ServerName = "Query Fallback Server",
                    Mod = "mods/fallback",
                    MaxPlayers = 12,
                    Map = "mp_crash",
                    Players = []
                })));

        var service = CreateService();

        await service.SyncAsync(_serverId, _mockParser.Object, "CallOfDuty4");

        _mockQueryApi.Verify(x => x.GetServerStatus(_serverId), Times.Once);
        _mockParser.Verify(x => x.SetServerInfo("Query Fallback Server", "mods/fallback", 12), Times.Once);
        _mockParser.Verify(x => x.SetCurrentMap("mp_crash"), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_WhenExistingGuidMissing_UsesRconGuidForIpResolvedEvent()
    {
        var rconPlayers = new List<CoD4xStatusPlayerDto>
        {
            new() { Num = 0, PlayerIdentifier = "guid-from-rcon", Name = "Alice", IpAddress = "9.9.9.9", Ping = 30, Rate = 25000 }
        };

        _mockCoD4xRconApi.Setup(r => r.Status(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xStatusResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xStatusResponseDto>(new CoD4xStatusResponseDto { Players = rconPlayers })));

        var existingPlayers = new Dictionary<int, PlayerInfo>
        {
            [0] = new()
            {
                Guid = string.Empty,
                Name = "Alice",
                SlotId = 0,
                ConnectedAt = DateTime.UtcNow,
                IpAddress = "1.1.1.1"
            }
        };

        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(existingPlayers);

        var service = CreateService();

        var ipEvents = await service.SyncAsync(_serverId, _mockParser.Object, "CallOfDuty4x");

        var ipEvent = Assert.Single(ipEvents);
        Assert.Equal("guid-from-rcon", ipEvent.PlayerGuid);
        Assert.Equal("9.9.9.9", ipEvent.IpAddress);
        Assert.Equal(string.Empty, existingPlayers[0].Guid);
    }
}
