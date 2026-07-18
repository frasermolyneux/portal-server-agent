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
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class CoD4xBanReconciliationServiceTests
{
    private readonly Mock<ICoD4xRconApi> _mockCoD4xRconApi = new();
    private readonly Mock<IRepositoryApiClient> _mockRepositoryApiClient = new();
    private readonly Mock<IVersionedAdminActionsApi> _mockVersionedAdminActionsApi = new();
    private readonly Mock<IAdminActionsApi> _mockAdminActionsApi = new();
    private readonly Mock<IVersionedPlayersApi> _mockVersionedPlayersApi = new();
    private readonly Mock<IPlayersApi> _mockPlayersApi = new();
    private readonly Mock<IEventPublisher> _mockEventPublisher = new();

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
            _mockEventPublisher.Object,
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

        _mockEventPublisher.Verify(x => x.PublishBanAppliedAsync(
            _serverId,
            It.IsAny<string>(),
            It.IsAny<long>(),
            "cod4x-player-1",
            "cod4x-player-1",
            false,
            null,
            "Portal",
            It.Is<string>(s => s.Contains("Reconciled missing permanent ban", StringComparison.Ordinal)),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_WhenPluginSourceEnabled_DoesNotReapplyPortalBanWhenMissingOnServer()
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
            Guid.Parse("12121212-1212-1212-1212-121212121212"),
            "cod4x-player-plugin-source",
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

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x", isCod4xPluginSourceEnabled: true);

        // Assert
        _mockCoD4xRconApi.Verify(c => c.BanPlayerByPlayerIdentifier(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xPermBanRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _mockCoD4xRconApi.Verify(c => c.TempBanPlayerByPlayerIdentifier(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xTempBanRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_PublishesBanSyncFailed_WhenReapplyFails()
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
            "cod4x-player-2",
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
                HttpStatusCode.InternalServerError,
                new ApiResponse<CoD4xBanCommandResponseDto>(new ApiError("RCON_ERROR", "rcon failed"))));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x");

        // Assert
        _mockEventPublisher.Verify(x => x.PublishBanSyncFailedAsync(
            _serverId,
            It.IsAny<string>(),
            It.IsAny<long>(),
            "ReapplyPortalBan",
            It.Is<string>(s => s.Contains("rcon failed", StringComparison.Ordinal)),
            "Agent",
            "cod4x-player-2",
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotThrow_WhenBanAppliedPublishFails()
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
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            "cod4x-player-7",
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
                    PlayerIdentifier = "cod4x-player-7"
                })));

        _mockEventPublisher.Setup(x => x.PublishBanAppliedAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("publish failed"));

        var service = CreateService();

        // Act
        var ex = await Record.ExceptionAsync(() => service.ReconcileAsync(_serverId, "CallOfDuty4x"));

        // Assert
        Assert.Null(ex);
        _mockCoD4xRconApi.Verify(c => c.BanPlayerByPlayerIdentifier(
            _serverId,
            It.Is<CoD4xPermBanRequestDto>(r => r.PlayerIdentifier == "cod4x-player-7"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotThrow_WhenBanSyncFailedPublishFails()
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
            Guid.Parse("88888888-8888-8888-8888-888888888888"),
            "cod4x-player-8",
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
                HttpStatusCode.InternalServerError,
                new ApiResponse<CoD4xBanCommandResponseDto>(new ApiError("RCON_ERROR", "rcon failed"))));

        _mockEventPublisher.Setup(x => x.PublishBanSyncFailedAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("publish failed"));

        var service = CreateService();

        // Act
        var ex = await Record.ExceptionAsync(() => service.ReconcileAsync(_serverId, "CallOfDuty4x"));

        // Assert
        Assert.Null(ex);
        _mockCoD4xRconApi.Verify(c => c.BanPlayerByPlayerIdentifier(
            _serverId,
            It.Is<CoD4xPermBanRequestDto>(r => r.PlayerIdentifier == "cod4x-player-8"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_WhenRconThrows_ContinuesAndPublishesBanSyncFailed()
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
            Guid.Parse("99999999-9999-9999-9999-999999999999"),
            "cod4x-player-9",
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
            .ThrowsAsync(new InvalidOperationException("rcon exception"));

        var service = CreateService();

        // Act
        var ex = await Record.ExceptionAsync(() => service.ReconcileAsync(_serverId, "CallOfDuty4x"));

        // Assert
        Assert.Null(ex);
        _mockEventPublisher.Verify(x => x.PublishBanSyncFailedAsync(
            _serverId,
            It.IsAny<string>(),
            It.IsAny<long>(),
            "ReapplyPortalBan",
            It.Is<string>(s => s.Contains("rcon exception", StringComparison.Ordinal)),
            "Agent",
            "cod4x-player-9",
            null,
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_PublishesServerOnlyBanForPortalImport()
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

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x");

        // Assert
        _mockEventPublisher.Verify(x => x.PublishBanAppliedAsync(
            _serverId,
            nameof(GameType.CallOfDuty4x),
            It.IsAny<long>(),
            "server-only-player",
            "ServerOnly",
            false,
            null,
            "RconDumpbanlist",
            "Imported from server RCON dumpbanlist",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_WhenPluginSourceEnabled_StillImportsServerOnlyBanWithoutPortalMarker()
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
                            PlayerIdentifier = "plugin-server-only-player",
                            Nick = "PluginServerOnly",
                            Expire = "Never",
                            Reason = "Manual server ban"
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

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x", isCod4xPluginSourceEnabled: true);

        // Assert
        _mockEventPublisher.Verify(x => x.PublishBanAppliedAsync(
            _serverId,
            nameof(GameType.CallOfDuty4x),
            It.IsAny<long>(),
            "plugin-server-only-player",
            "PluginServerOnly",
            false,
            null,
            "RconDumpbanlist",
            "Manual server ban",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_WhenPluginSourceEnabled_SkipsImportForPortalManagedServerBan()
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
                            PlayerIdentifier = "portal-managed-player",
                            Nick = "PortalManaged",
                            Expire = "Never",
                            Reason = "[PORTAL-BAN] synced from portal"
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

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x", isCod4xPluginSourceEnabled: true);

        // Assert
        _mockPlayersApi.Verify(p => p.HeadPlayerByGameType(GameType.CallOfDuty4x, It.IsAny<string>()), Times.Never);
        _mockAdminActionsApi.Verify(a => a.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsImportForPortalAutomationBanWithoutPluginSource()
    {
        _mockCoD4xRconApi.Setup(c => c.DumpBanList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xBanListResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xBanListResponseDto>(new CoD4xBanListResponseDto
                {
                    Entries =
                    [
                        new CoD4xBanEntryDto
                        {
                            PlayerIdentifier = "portal-automation-player",
                            Nick = "PortalAutomation",
                            Expire = "Never",
                            Reason = "[PORTAL-AUTOMATION] VPN:vpn VPN Protection"
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

        var service = CreateService();

        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        _mockPlayersApi.Verify(p => p.HeadPlayerByGameType(GameType.CallOfDuty4x, It.IsAny<string>()), Times.Never);
        _mockAdminActionsApi.Verify(a => a.CreateAdminAction(It.IsAny<CreateAdminActionDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_PublishesRegularPortalBanWithoutPluginSource()
    {
        _mockCoD4xRconApi.Setup(c => c.DumpBanList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xBanListResponseDto>(
                HttpStatusCode.OK,
                new ApiResponse<CoD4xBanListResponseDto>(new CoD4xBanListResponseDto
                {
                    Entries =
                    [
                        new CoD4xBanEntryDto
                        {
                            PlayerIdentifier = "regular-portal-ban-player",
                            Nick = "RegularPortalBan",
                            Expire = "Never",
                            Reason = "[PORTAL-BAN] synced from portal"
                        }
                    ],
                    ActiveBanCount = 1,
                    RawResponse = string.Empty
                })));
        _mockAdminActionsApi.Setup(a => a.GetAdminActions(GameType.CallOfDuty4x, null, null, AdminActionFilter.ActiveBans, 0, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(HttpStatusCode.OK, new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto> { Items = [] })));
        await CreateService().ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        _mockEventPublisher.Verify(x => x.PublishBanAppliedAsync(
            _serverId,
            nameof(GameType.CallOfDuty4x),
            It.IsAny<long>(),
            "regular-portal-ban-player",
            "RegularPortalBan",
            false,
            null,
            "RconDumpbanlist",
            "[PORTAL-BAN] synced from portal",
            null,
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

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x");

        // Assert
        _mockEventPublisher.Verify(x => x.PublishBanAppliedAsync(
            _serverId,
            nameof(GameType.CallOfDuty4x),
            It.IsAny<long>(),
            "server-only-player",
            "ServerOnly",
            true,
            It.Is<DateTime?>(expires => expires.HasValue),
            "RconDumpbanlist",
            "Imported from server RCON dumpbanlist",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_PublishesServerOnlyBanWithoutPlayerLookup()
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

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, "CallOfDuty4x");

        // Assert
        _mockEventPublisher.Verify(x => x.PublishBanAppliedAsync(
            _serverId,
            nameof(GameType.CallOfDuty4x),
            It.IsAny<long>(),
            "server-only-player",
            "ServerOnly",
            false,
            null,
            "RconDumpbanlist",
            It.IsAny<string>(),
            null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_ResolvesLegacyCoD4xIdentifierToCanonicalPuid()
    {
        const string canonicalPuid = "2310346613824768397";
        const string legacyIdentifier = "4535753900329";

        _mockCoD4xRconApi.Setup(c => c.DumpBanList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xBanListResponseDto>(HttpStatusCode.OK, new ApiResponse<CoD4xBanListResponseDto>(new CoD4xBanListResponseDto
            {
                Entries = [new CoD4xBanEntryDto { PlayerIdentifier = legacyIdentifier, Nick = "DigBick_Tony", Expire = "Never", Reason = "VPN Protection: matched rule 'proxycheck-risk-score-dangerous'" }]
            })));
        _mockAdminActionsApi.Setup(a => a.GetAdminActions(GameType.CallOfDuty4x, null, null, AdminActionFilter.ActiveBans, 0, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(HttpStatusCode.OK, new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto> { Items = [] })));
        _mockPlayersApi.Setup(p => p.GetPlayers(GameType.CallOfDuty4x, PlayersFilter.UsernameAndGuid, "23103466138", 0, 20, null, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<CollectionModel<PlayerDto>>(HttpStatusCode.OK, new ApiResponse<CollectionModel<PlayerDto>>(new CollectionModel<PlayerDto> { Items = [CreatePlayerDto(Guid.NewGuid(), canonicalPuid)] })));

        await CreateService().ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x), isCod4xPluginSourceEnabled: true);

        _mockEventPublisher.Verify(x => x.PublishBanAppliedAsync(
            _serverId,
            nameof(GameType.CallOfDuty4x),
            It.IsAny<long>(),
            canonicalPuid,
            "DigBick_Tony",
            false,
            null,
            "RconDumpbanlist",
            "VPN Protection: matched rule 'proxycheck-risk-score-dangerous'",
            null,
            It.IsAny<CancellationToken>()), Times.Once);
        _mockPlayersApi.Verify(p => p.CreatePlayer(It.IsAny<CreatePlayerDto>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsLegacyIdentifierWhenMultipleCanonicalPuidCandidatesExist()
    {
        const string legacyIdentifier = "4535753900329";
        _mockCoD4xRconApi.Setup(c => c.DumpBanList(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CoD4xBanListResponseDto>(HttpStatusCode.OK, new ApiResponse<CoD4xBanListResponseDto>(new CoD4xBanListResponseDto
            {
                Entries = [new CoD4xBanEntryDto { PlayerIdentifier = legacyIdentifier, Nick = "DigBick_Tony", Expire = "Never" }]
            })));
        _mockAdminActionsApi.Setup(a => a.GetAdminActions(GameType.CallOfDuty4x, null, null, AdminActionFilter.ActiveBans, 0, It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(HttpStatusCode.OK, new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto> { Items = [] })));
        _mockPlayersApi.Setup(p => p.GetPlayers(GameType.CallOfDuty4x, PlayersFilter.UsernameAndGuid, "23103466138", 0, 20, null, PlayerEntityOptions.None))
            .ReturnsAsync(new ApiResult<CollectionModel<PlayerDto>>(HttpStatusCode.OK, new ApiResponse<CollectionModel<PlayerDto>>(new CollectionModel<PlayerDto>
            {
                Items = [CreatePlayerDto(Guid.NewGuid(), "2310346613824768397"), CreatePlayerDto(Guid.NewGuid(), "2310346613823897601")]
            })));

        await CreateService().ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x), isCod4xPluginSourceEnabled: true);

        _mockEventPublisher.Verify(x => x.PublishBanAppliedAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<DateTime?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
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
