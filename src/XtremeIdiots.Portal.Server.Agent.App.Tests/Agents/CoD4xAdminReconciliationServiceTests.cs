using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;
using Newtonsoft.Json;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.ConnectedPlayers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class CoD4xAdminReconciliationServiceTests
{
    private readonly Mock<IRepositoryApiClient> _mockRepositoryApiClient = new();
    private readonly Mock<IVersionedConnectedPlayersApi> _mockVersionedConnectedPlayersApi = new();
    private readonly Mock<IConnectedPlayersApi> _mockConnectedPlayersApi = new();
    private readonly Mock<IVersionedPlayersApi> _mockVersionedPlayersApi = new();
    private readonly Mock<IPlayersApi> _mockPlayersApi = new();
    private readonly Mock<ICoD4xRconApi> _mockCoD4xRconApi = new();

    private readonly Guid _serverId = Guid.NewGuid();

    private CoD4xAdminReconciliationService CreateService(ILogger<CoD4xAdminReconciliationService>? logger = null)
    {
        _mockRepositoryApiClient.Setup(x => x.ConnectedPlayers).Returns(_mockVersionedConnectedPlayersApi.Object);
        _mockVersionedConnectedPlayersApi.Setup(x => x.V1).Returns(_mockConnectedPlayersApi.Object);

        _mockRepositoryApiClient.Setup(x => x.Players).Returns(_mockVersionedPlayersApi.Object);
        _mockVersionedPlayersApi.Setup(x => x.V1).Returns(_mockPlayersApi.Object);

        return new CoD4xAdminReconciliationService(
            _mockRepositoryApiClient.Object,
            _mockCoD4xRconApi.Object,
            logger ?? NullLogger<CoD4xAdminReconciliationService>.Instance);
    }

    [Fact]
    public async Task ReconcileAsync_AddsMissingAdmin_FromDesiredRosterSteamId()
    {
        // Arrange
        _mockConnectedPlayersApi
            .Setup(x => x.GetCod4xAdminRoster(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Cod4xAdminRosterDto>(
                HttpStatusCode.OK,
                new ApiResponse<Cod4xAdminRosterDto>(CreateRosterDto(true, ("player-guid-1", 55)))));

        _mockPlayersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid-1", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.OK,
                new ApiResponse<PlayerDto>(CreatePlayerDto("player-guid-1", "steam-1"))));

        _mockCoD4xRconApi
            .Setup(x => x.AdminListAdmins(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("No admins found")));

        _mockCoD4xRconApi
            .Setup(x => x.AdminAddAdmin(_serverId, It.IsAny<CoD4xAdminAddAdminRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("ok")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminAddAdmin(
            _serverId,
            It.Is<CoD4xAdminAddAdminRequestDto>(r => r.User == "steam-1" && r.Power == 55),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xAdminUserRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_UpdatesMismatchedPower_AndRemovesStaleAdmin()
    {
        // Arrange
        _mockConnectedPlayersApi
            .Setup(x => x.GetCod4xAdminRoster(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Cod4xAdminRosterDto>(
                HttpStatusCode.OK,
                new ApiResponse<Cod4xAdminRosterDto>(CreateRosterDto(true, ("player-guid-1", 60)))));

        _mockPlayersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid-1", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.OK,
                new ApiResponse<PlayerDto>(CreatePlayerDto("player-guid-1", "steam-1"))));

        _mockCoD4xRconApi
            .Setup(x => x.AdminListAdmins(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>(
                    "0: Name: Alice, Power: 20, SteamId: steam-1\n1: Name: Bob, Power: 10, SteamId: steam-2")));

        _mockCoD4xRconApi
            .Setup(x => x.AdminAddAdmin(_serverId, It.IsAny<CoD4xAdminAddAdminRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("ok")));

        _mockCoD4xRconApi
            .Setup(x => x.AdminRemoveAdmin(_serverId, It.IsAny<CoD4xAdminUserRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("ok")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminAddAdmin(
            _serverId,
            It.Is<CoD4xAdminAddAdminRequestDto>(r => r.User == "steam-1" && r.Power == 60),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(
            _serverId,
            It.Is<CoD4xAdminUserRequestDto>(r => r.User == "steam-2"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsDesiredEntriesWithoutSteamId()
    {
        // Arrange
        _mockConnectedPlayersApi
            .Setup(x => x.GetCod4xAdminRoster(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Cod4xAdminRosterDto>(
                HttpStatusCode.OK,
                new ApiResponse<Cod4xAdminRosterDto>(CreateRosterDto(true, ("player-guid-1", 40)))));

        _mockPlayersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid-1", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.OK,
                new ApiResponse<PlayerDto>(CreatePlayerDto("player-guid-1", null))));

        _mockCoD4xRconApi
            .Setup(x => x.AdminListAdmins(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("No admins found")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminAddAdmin(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xAdminAddAdminRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xAdminUserRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsWhenRosterDisabled()
    {
        // Arrange
        _mockConnectedPlayersApi
            .Setup(x => x.GetCod4xAdminRoster(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Cod4xAdminRosterDto>(
                HttpStatusCode.OK,
                new ApiResponse<Cod4xAdminRosterDto>(CreateRosterDto(false, ("player-guid-1", 40)))));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminListAdmins(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.AdminAddAdmin(It.IsAny<Guid>(), It.IsAny<CoD4xAdminAddAdminRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(It.IsAny<Guid>(), It.IsAny<CoD4xAdminUserRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotRemoveAdmins_WhenDesiredPlayerLookupFails()
    {
        // Arrange
        _mockConnectedPlayersApi
            .Setup(x => x.GetCod4xAdminRoster(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Cod4xAdminRosterDto>(
                HttpStatusCode.OK,
                new ApiResponse<Cod4xAdminRosterDto>(CreateRosterDto(true, ("player-guid-1", 40)))));

        _mockPlayersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid-1", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.InternalServerError,
                new ApiResponse<PlayerDto>(new ApiError("PLAYER_LOOKUP_FAILED", "lookup failed"))));

        _mockCoD4xRconApi
            .Setup(x => x.AdminListAdmins(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("------- Admins: -------\n0: Name: Alice, Power: 20, SteamId: steam-1\n---------------------------------")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xAdminUserRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _mockCoD4xRconApi.Verify(x => x.AdminAddAdmin(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xAdminAddAdminRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotRemoveAdmins_WhenAdminListContainsUnparseableLines()
    {
        // Arrange
        _mockConnectedPlayersApi
            .Setup(x => x.GetCod4xAdminRoster(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Cod4xAdminRosterDto>(
                HttpStatusCode.OK,
                new ApiResponse<Cod4xAdminRosterDto>(CreateRosterDto(true, ("player-guid-1", 50)))));

        _mockPlayersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid-1", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.OK,
                new ApiResponse<PlayerDto>(CreatePlayerDto("player-guid-1", "steam-1"))));

        _mockCoD4xRconApi
            .Setup(x => x.AdminListAdmins(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>(
                    "0: Name: Alice, Power: 50, SteamId: steam-1\nthis line is not parseable\n1: Name: Bob, Power: 10, SteamId: steam-2")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xAdminUserRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("------- Admins: -------\n1:   Name: Alice, Power: 20, SteamId: steam-1\n2:   Name: Bob, Power: 10, SteamId: steam-2\n---------------------------------")]
    [InlineData("------- Admins: -------\n1: Name: Alice, Power: 20, SteamId: steam-1\n2: Name: Power: 1, SteamId: x , Power: 10, SteamId: steam-2\n---------------------------------")]
    [InlineData("1: Name: Alice, Power: 20, SteamId: steam-1\n2: Name: Bob, Power: 10, SteamId: steam-2")]
    public async Task ReconcileAsync_RemovesStaleAdmins_FromCompleteLists(string adminList)
    {
        _mockConnectedPlayersApi
            .Setup(x => x.GetCod4xAdminRoster(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Cod4xAdminRosterDto>(
                HttpStatusCode.OK, new ApiResponse<Cod4xAdminRosterDto>(CreateRosterDto(true, ("player-guid-1", 20)))));
        _mockPlayersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid-1", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayerDto("player-guid-1", "steam-1"))));
        _mockCoD4xRconApi
            .Setup(x => x.AdminListAdmins(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(HttpStatusCode.OK, new ApiResponse<string>(adminList)));
        _mockCoD4xRconApi
            .Setup(x => x.AdminRemoveAdmin(_serverId, It.IsAny<CoD4xAdminUserRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(HttpStatusCode.OK, new ApiResponse<string>("ok")));

        await CreateService().ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(
            _serverId, It.Is<CoD4xAdminUserRequestDto>(r => r.User == "steam-2"), It.IsAny<CancellationToken>()), Times.Once);
        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(
            It.IsAny<Guid>(), It.IsAny<CoD4xAdminUserRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockCoD4xRconApi.Verify(x => x.AdminAddAdmin(
            It.IsAny<Guid>(), It.IsAny<CoD4xAdminAddAdminRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("------- Admins: -------\n1: Name: Bob, Power: 10, SteamId: steam-2")]
    [InlineData("1: Name: Bob, Power: 10, SteamId: steam-2\n---------------------------------")]
    [InlineData("---------------------------------\n1: Name: Bob, Power: 10, SteamId: steam-2\n------- Admins: -------")]
    [InlineData("------- Admins: -------\n------- Admins: -------\n1: Name: Bob, Power: 10, SteamId: steam-2\n---------------------------------")]
    [InlineData("------- Admins: -------\n1: Name: Bob, Power: 10, SteamId: steam-2\n---------------------------------\nextra")]
    [InlineData("------ Admins: -------\n1: Name: Bob, Power: 10, SteamId: steam-2\n---------------------------------")]
    [InlineData("------- Admins: -------\n1: Name: Bob, Power: 10, SteamId: steam-2\n--------------------------------")]
    [InlineData("------- Admins: -------\n1: Name: Bob, Power: 10, SteamId: steam-2\nunknown line\n---------------------------------")]
    [InlineData("------- Admins: -------\n1: Name: Bob, Power: 10, SteamId: steam-2\n2: Name: Bad, Power: nope, SteamId: steam-3\n---------------------------------")]
    [InlineData("------- Admins: -------\n1: Name: Bob, Power: 10, SteamId: steam-2\njunk Power: 20, SteamId: steam-3\n---------------------------------")]
    public async Task ReconcileAsync_DoesNotRemoveAdmins_FromIncompleteOrMalformedFramedLists(string adminList)
    {
        _mockConnectedPlayersApi
            .Setup(x => x.GetCod4xAdminRoster(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Cod4xAdminRosterDto>(
                HttpStatusCode.OK, new ApiResponse<Cod4xAdminRosterDto>(CreateRosterDto(true))));
        _mockCoD4xRconApi
            .Setup(x => x.AdminListAdmins(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(HttpStatusCode.OK, new ApiResponse<string>(adminList)));

        await CreateService().ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(
            It.IsAny<Guid>(), It.IsAny<CoD4xAdminUserRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_AddsDesiredAdmin_WhenFramedListIsEmpty()
    {
        var logger = new Mock<ILogger<CoD4xAdminReconciliationService>>();
        _mockConnectedPlayersApi
            .Setup(x => x.GetCod4xAdminRoster(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Cod4xAdminRosterDto>(
                HttpStatusCode.OK, new ApiResponse<Cod4xAdminRosterDto>(CreateRosterDto(true, ("player-guid-1", 50)))));
        _mockPlayersApi
            .Setup(x => x.GetPlayerByGameType(GameType.CallOfDuty4x, "player-guid-1", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.OK, new ApiResponse<PlayerDto>(CreatePlayerDto("player-guid-1", "steam-1"))));
        _mockCoD4xRconApi
            .Setup(x => x.AdminListAdmins(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(HttpStatusCode.OK, new ApiResponse<string>("------- Admins: -------\n---------------------------------")));
        _mockCoD4xRconApi
            .Setup(x => x.AdminAddAdmin(_serverId, It.IsAny<CoD4xAdminAddAdminRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(HttpStatusCode.OK, new ApiResponse<string>("ok")));

        await CreateService(logger.Object).ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        _mockCoD4xRconApi.Verify(x => x.AdminAddAdmin(
            _serverId, It.Is<CoD4xAdminAddAdminRequestDto>(r => r.User == "steam-1" && r.Power == 50), It.IsAny<CancellationToken>()), Times.Once);
        _mockCoD4xRconApi.Verify(x => x.AdminRemoveAdmin(
            It.IsAny<Guid>(), It.IsAny<CoD4xAdminUserRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        logger.Verify(x => x.Log(
            LogLevel.Warning, It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(), It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsWhenGameTypeIsNotCoD4x()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4));

        // Assert
        _mockConnectedPlayersApi.Verify(x => x.GetCod4xAdminRoster(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.AdminListAdmins(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Cod4xAdminRosterDto CreateRosterDto(bool enabled, params (string PlayerGuid, int Power)[] entries)
    {
        var roster = new
        {
            Enabled = enabled,
            DefaultPower = 0,
            Entries = entries.Select(e => new
            {
                PlayerGuid = e.PlayerGuid,
                Power = e.Power,
                Tags = Array.Empty<string>()
            }).ToArray()
        };

        return JsonConvert.DeserializeObject<Cod4xAdminRosterDto>(JsonConvert.SerializeObject(roster))!;
    }

    private static PlayerDto CreatePlayerDto(string guid, string? steamId)
    {
        var player = new
        {
            PlayerId = Guid.NewGuid(),
            Guid = guid,
            Username = "Player",
            GameType = GameType.CallOfDuty4x,
            SteamId = steamId
        };

        return JsonConvert.DeserializeObject<PlayerDto>(JsonConvert.SerializeObject(player))!;
    }
}
