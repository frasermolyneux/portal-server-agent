using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;
using Newtonsoft.Json;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class CoD4xBanReconciliationServiceTests
{
    private readonly Mock<ICoD4xRconApi> _mockCoD4xRconApi = new();
    private readonly Mock<IRepositoryApiClient> _mockRepositoryApiClient = new();
    private readonly Mock<IVersionedAdminActionsApi> _mockVersionedAdminActionsApi = new();
    private readonly Mock<IAdminActionsApi> _mockAdminActionsApi = new();
    private readonly Mock<IVersionedPlayersApi> _mockVersionedPlayersApi = new();
    private readonly Mock<IPlayersApi> _mockPlayersApi = new();

    private readonly Guid _serverId = Guid.NewGuid();

    private CoD4xBanReconciliationService CreateService()
    {
        _mockRepositoryApiClient.Setup(x => x.AdminActions).Returns(_mockVersionedAdminActionsApi.Object);
        _mockVersionedAdminActionsApi.Setup(x => x.V1).Returns(_mockAdminActionsApi.Object);

        _mockRepositoryApiClient.Setup(x => x.Players).Returns(_mockVersionedPlayersApi.Object);
        _mockVersionedPlayersApi.Setup(x => x.V1).Returns(_mockPlayersApi.Object);

        return new CoD4xBanReconciliationService(
            _mockRepositoryApiClient.Object,
            _mockCoD4xRconApi.Object,
            NullLogger<CoD4xBanReconciliationService>.Instance);
    }

    [Fact]
    public async Task ReconcileAsync_ReappliesPortalBanWhenMissingOnServer()
    {
        // Arrange
        _mockCoD4xRconApi.Setup(c => c.DumpBanList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xBanListResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xBanListResponseDto>(new CoD4xBanListResponseDto
                {
                    Entries = [],
                    ActiveBanCount = 0,
                    RawResponse = string.Empty
                })));

        var activeBan = CreateAdminActionDto(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "cod4x-player-1",
            AdminActionType.Ban,
            null);

        _mockAdminActionsApi.Setup(a => a.GetAdminActions(
                GameType.CallOfDuty4x,
                null,
                null,
                AdminActionFilter.ActiveBans,
                0,
                It.IsAny<int>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto>
                {
                    Items = [activeBan]
                })));

        _mockCoD4xRconApi.Setup(c => c.BanPlayerByPlayerIdentifier(
                _serverId,
                It.IsAny<CoD4xPermBanRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xBanCommandResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xBanCommandResponseDto>(new CoD4xBanCommandResponseDto
                {
                    IsSuccess = true,
                    Outcome = "Success",
                    PlayerIdentifier = "cod4x-player-1"
                })));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x");

        // Assert
        _mockCoD4xRconApi.Verify(c => c.BanPlayerByPlayerIdentifier(
            _serverId,
            It.Is<CoD4xPermBanRequestDto>(r => r.PlayerIdentifier == "cod4x-player-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_ImportsServerOnlyBanIntoPortal()
    {
        // Arrange
        _mockCoD4xRconApi.Setup(c => c.DumpBanList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xBanListResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xBanListResponseDto>(new CoD4xBanListResponseDto
                {
                    Entries =
                    [
                        new CoD4xBanEntryDto
                        {
                            PlayerIdentifier = "server-only-player",
                            Nick = "ServerOnly",
                            Expire = "Never"
                        }
                    ],
                    ActiveBanCount = 1,
                    RawResponse = string.Empty
                })));

        _mockAdminActionsApi.Setup(a => a.GetAdminActions(
                GameType.CallOfDuty4x,
                null,
                null,
                AdminActionFilter.ActiveBans,
                0,
                It.IsAny<int>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto>
                {
                    Items = []
                })));

        _mockPlayersApi.Setup(p => p.HeadPlayerByGameType(GameType.CallOfDuty4x, "server-only-player"))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        _mockPlayersApi.Setup(p => p.GetPlayerByGameType(GameType.CallOfDuty4x, "server-only-player", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.OK,
                new ApiResponse<PlayerDto>(CreatePlayerDto(Guid.Parse("44444444-4444-4444-4444-444444444444"), "server-only-player"))));

        _mockAdminActionsApi.Setup(a => a.GetAdminActions(
                GameType.CallOfDuty4x,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                null,
                AdminActionFilter.ActiveBans,
                0,
                1,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto>
                {
                    Items = []
                })));

        _mockAdminActionsApi.Setup(a => a.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK, new ApiResponse()));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x");

        // Assert
        _mockAdminActionsApi.Verify(a => a.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto =>
                dto.PlayerId == Guid.Parse("44444444-4444-4444-4444-444444444444") &&
                dto.Type == AdminActionType.Ban &&
                dto.Text == "Imported from server RCON dumpbanlist"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_ImportsServerOnlyTimedBanIntoPortalAsTempBan()
    {
        // Arrange
        var expiresAtUtc = DateTime.UtcNow.AddHours(2);
        var expiryText = expiresAtUtc.ToString("yyyy-MM-dd HH:mm:ss");

        _mockCoD4xRconApi.Setup(c => c.DumpBanList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xBanListResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xBanListResponseDto>(new CoD4xBanListResponseDto
                {
                    Entries =
                    [
                        new CoD4xBanEntryDto
                        {
                            PlayerIdentifier = "server-only-player",
                            Nick = "ServerOnly",
                            Expire = expiryText
                        }
                    ],
                    ActiveBanCount = 1,
                    RawResponse = string.Empty
                })));

        _mockAdminActionsApi.Setup(a => a.GetAdminActions(
                GameType.CallOfDuty4x,
                null,
                null,
                AdminActionFilter.ActiveBans,
                0,
                It.IsAny<int>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto>
                {
                    Items = []
                })));

        _mockPlayersApi.Setup(p => p.HeadPlayerByGameType(GameType.CallOfDuty4x, "server-only-player"))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        _mockPlayersApi.Setup(p => p.GetPlayerByGameType(GameType.CallOfDuty4x, "server-only-player", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.OK,
                new ApiResponse<PlayerDto>(CreatePlayerDto(Guid.Parse("44444444-4444-4444-4444-444444444444"), "server-only-player"))));

        _mockAdminActionsApi.Setup(a => a.GetAdminActions(
                GameType.CallOfDuty4x,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                null,
                AdminActionFilter.ActiveBans,
                0,
                1,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto>
                {
                    Items = []
                })));

        _mockAdminActionsApi.Setup(a => a.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK, new ApiResponse()));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x");

        // Assert
        _mockAdminActionsApi.Verify(a => a.CreateAdminAction(
            It.Is<CreateAdminActionDto>(dto =>
                dto.PlayerId == Guid.Parse("44444444-4444-4444-4444-444444444444") &&
                dto.Type == AdminActionType.TempBan &&
                dto.Expires.HasValue &&
                dto.Text == "Imported from server RCON dumpbanlist"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotImportWhenActiveBanLookupFails()
    {
        // Arrange
        _mockCoD4xRconApi.Setup(c => c.DumpBanList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xBanListResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xBanListResponseDto>(new CoD4xBanListResponseDto
                {
                    Entries =
                    [
                        new CoD4xBanEntryDto
                        {
                            PlayerIdentifier = "server-only-player",
                            Nick = "ServerOnly",
                            Expire = "Never"
                        }
                    ],
                    ActiveBanCount = 1,
                    RawResponse = string.Empty
                })));

        _mockAdminActionsApi.Setup(a => a.GetAdminActions(
                GameType.CallOfDuty4x,
                null,
                null,
                AdminActionFilter.ActiveBans,
                0,
                It.IsAny<int>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(
                HttpStatusCode.OK,
                new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto>
                {
                    Items = []
                })));

        _mockPlayersApi.Setup(p => p.HeadPlayerByGameType(GameType.CallOfDuty4x, "server-only-player"))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        _mockPlayersApi.Setup(p => p.GetPlayerByGameType(GameType.CallOfDuty4x, "server-only-player", PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<PlayerDto>(
                HttpStatusCode.OK,
                new ApiResponse<PlayerDto>(CreatePlayerDto(Guid.Parse("44444444-4444-4444-4444-444444444444"), "server-only-player"))));

        _mockAdminActionsApi.Setup(a => a.GetAdminActions(
                GameType.CallOfDuty4x,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                null,
                AdminActionFilter.ActiveBans,
                0,
                1,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(
                HttpStatusCode.InternalServerError,
                new ApiResponse<CollectionModel<AdminActionDto>>(new ApiError("SERVER_ERROR", "lookup failed"))));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x");

        // Assert
        _mockAdminActionsApi.Verify(a => a.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsWhenGameTypeIsNotCoD4x()
    {
        var service = CreateService();

        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4));

        _mockCoD4xRconApi.Verify(x => x.DumpBanList(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static AdminActionDto CreateAdminActionDto(Guid playerId, string playerGuid, AdminActionType type, DateTime? expires)
    {
        var json = JsonConvert.SerializeObject(new
        {
            PlayerId = Guid.NewGuid(),
            Type = type,
            Expires = expires,
            Player = new
            {
                PlayerId = playerId,
                Guid = playerGuid,
                Username = "Player"
            }
        });

        return JsonConvert.DeserializeObject<AdminActionDto>(json)!;
    }

    private static PlayerDto CreatePlayerDto(Guid playerId, string playerGuid)
    {
        var json = JsonConvert.SerializeObject(new
        {
            PlayerId = playerId,
            Guid = playerGuid,
            Username = "Player",
            GameType = GameType.CallOfDuty4x
        });

        return JsonConvert.DeserializeObject<PlayerDto>(json)!;
    }
}
