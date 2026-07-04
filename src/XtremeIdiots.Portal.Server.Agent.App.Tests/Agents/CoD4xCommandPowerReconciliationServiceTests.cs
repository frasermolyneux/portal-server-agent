using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;
using Newtonsoft.Json;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xCommands;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class CoD4xCommandPowerReconciliationServiceTests
{
    private readonly Mock<IRepositoryApiClient> _mockRepositoryApiClient = new();
    private readonly Mock<IVersionedGlobalConfigurationsApi> _mockVersionedGlobalConfigurationsApi = new();
    private readonly Mock<IGlobalConfigurationsApi> _mockGlobalConfigurationsApi = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _mockVersionedGameServerConfigurationsApi = new();
    private readonly Mock<IGameServerConfigurationsApi> _mockGameServerConfigurationsApi = new();
    private readonly Mock<ICoD4xRconApi> _mockCoD4xRconApi = new();

    private readonly Guid _serverId = Guid.NewGuid();

    private CoD4xCommandPowerReconciliationService CreateService()
    {
        _mockRepositoryApiClient.Setup(x => x.GlobalConfigurations).Returns(_mockVersionedGlobalConfigurationsApi.Object);
        _mockVersionedGlobalConfigurationsApi.Setup(x => x.V1).Returns(_mockGlobalConfigurationsApi.Object);

        _mockRepositoryApiClient.Setup(x => x.GameServerConfigurations).Returns(_mockVersionedGameServerConfigurationsApi.Object);
        _mockVersionedGameServerConfigurationsApi.Setup(x => x.V1).Returns(_mockGameServerConfigurationsApi.Object);

        return new CoD4xCommandPowerReconciliationService(
            _mockRepositoryApiClient.Object,
            _mockCoD4xRconApi.Object,
            NullLogger<CoD4xCommandPowerReconciliationService>.Instance);
    }

    [Fact]
    public async Task ReconcileAsync_UpdatesCommandPower_WhenGlobalEnforcementEnabled()
    {
        // Arrange
        SetupGlobalSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = true,
            commands = new
            {
                kick = new { minPower = 70 }
            }
        });

        SetupServerSettingsNotFound();

        _mockCoD4xRconApi
            .Setup(x => x.AdminListCommands(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("kick                    35\nAdminListCommands       95")));

        _mockCoD4xRconApi
            .Setup(x => x.AdminChangeCommandPower(_serverId, It.IsAny<CoD4xAdminChangeCommandPowerRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("changed required power of cmd: kick to new power: 70")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminChangeCommandPower(
            _serverId,
            It.Is<CoD4xAdminChangeCommandPowerRequestDto>(r => r.Command == "kick" && r.MinPower == 70),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_EnforcesWhenServerOverrideEnabledEvenIfGlobalDisabled()
    {
        // Arrange
        SetupGlobalSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = false,
            commands = new { }
        });

        SetupServerSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = true,
            commands = new
            {
                AdminListCommands = new { minPower = 65 }
            }
        });

        _mockCoD4xRconApi
            .Setup(x => x.AdminListCommands(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("AdminListCommands       95")));

        _mockCoD4xRconApi
            .Setup(x => x.AdminChangeCommandPower(_serverId, It.IsAny<CoD4xAdminChangeCommandPowerRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("changed required power of cmd: AdminListCommands to new power: 65")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminChangeCommandPower(
            _serverId,
            It.Is<CoD4xAdminChangeCommandPowerRequestDto>(r => r.Command == "AdminListCommands" && r.MinPower == 65),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_DoesNotApplyServerOverrides_WhenServerScopeDisabled()
    {
        // Arrange
        SetupGlobalSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = true,
            commands = new
            {
                kick = new { minPower = 70 }
            }
        });

        SetupServerSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = false,
            commands = new
            {
                kick = new { minPower = 50 }
            }
        });

        _mockCoD4xRconApi
            .Setup(x => x.AdminListCommands(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("kick                    35")));

        _mockCoD4xRconApi
            .Setup(x => x.AdminChangeCommandPower(_serverId, It.IsAny<CoD4xAdminChangeCommandPowerRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("changed required power of cmd: kick to new power: 70")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminChangeCommandPower(
            _serverId,
            It.Is<CoD4xAdminChangeCommandPowerRequestDto>(r => r.Command == "kick" && r.MinPower == 70),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsWhenBothGlobalAndServerEnforcementDisabled()
    {
        // Arrange
        SetupGlobalSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = false,
            commands = new { }
        });

        SetupServerSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = false,
            commands = new { }
        });

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminListCommands(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.AdminChangeCommandPower(
            It.IsAny<Guid>(),
            It.IsAny<CoD4xAdminChangeCommandPowerRequestDto>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_ResolvesAliasAndSetsCanonicalCommandPower()
    {
        // Arrange
        SetupGlobalSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = true,
            commands = new { }
        });

        SetupServerSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = true,
            commands = new
            {
                cmdpowerlist = new { minPower = 65 }
            }
        });

        _mockCoD4xRconApi
            .Setup(x => x.AdminListCommands(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("AdminListCommands       95")));

        _mockCoD4xRconApi
            .Setup(x => x.AdminChangeCommandPower(_serverId, It.IsAny<CoD4xAdminChangeCommandPowerRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("changed required power of cmd: AdminListCommands to new power: 65")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminChangeCommandPower(
            _serverId,
            It.Is<CoD4xAdminChangeCommandPowerRequestDto>(r => r.Command == "AdminListCommands" && r.MinPower == 65),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_SetsPowerTo100_WhenCommandIsDisabledBySettings()
    {
        // Arrange
        SetupGlobalSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = true,
            commands = new
            {
                kick = new { enabled = false }
            }
        });

        SetupServerSettingsNotFound();

        _mockCoD4xRconApi
            .Setup(x => x.AdminListCommands(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("kick                    35")));

        _mockCoD4xRconApi
            .Setup(x => x.AdminChangeCommandPower(_serverId, It.IsAny<CoD4xAdminChangeCommandPowerRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("changed required power of cmd: kick to new power: 100")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminChangeCommandPower(
            _serverId,
            It.Is<CoD4xAdminChangeCommandPowerRequestDto>(r => r.Command == "kick" && r.MinPower == 100),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_UsesServerOverride_WhenGlobalFetchFailsAndServerEnabled()
    {
        // Arrange
        _mockGlobalConfigurationsApi
            .Setup(x => x.GetConfiguration(Cod4xCommandSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigurationDto>(
                HttpStatusCode.InternalServerError,
                new ApiResponse<ConfigurationDto>(new ApiError("SERVER_ERROR", "global failed"))));

        SetupServerSettings(new
        {
            schemaVersion = Cod4xCommandSettingsConstants.SchemaVersion,
            enabled = true,
            commands = new
            {
                kick = new { minPower = 80 }
            }
        });

        _mockCoD4xRconApi
            .Setup(x => x.AdminListCommands(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("kick                    35")));

        _mockCoD4xRconApi
            .Setup(x => x.AdminChangeCommandPower(_serverId, It.IsAny<CoD4xAdminChangeCommandPowerRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                HttpStatusCode.OK,
                new ApiResponse<string>("changed required power of cmd: kick to new power: 80")));

        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4x));

        // Assert
        _mockCoD4xRconApi.Verify(x => x.AdminListCommands(_serverId, It.IsAny<CancellationToken>()), Times.Once);
        _mockCoD4xRconApi.Verify(x => x.AdminChangeCommandPower(
            _serverId,
            It.Is<CoD4xAdminChangeCommandPowerRequestDto>(r => r.Command == "kick" && r.MinPower == 80),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsWhenGameTypeIsNotCoD4x()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.ReconcileAsync(_serverId, nameof(GameType.CallOfDuty4));

        // Assert
        _mockGlobalConfigurationsApi.Verify(x => x.GetConfiguration(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.AdminListCommands(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private void SetupGlobalSettings(object settings)
    {
        var dto = CreateConfigurationDto(Cod4xCommandSettingsConstants.Namespace, JsonConvert.SerializeObject(settings));

        _mockGlobalConfigurationsApi
            .Setup(x => x.GetConfiguration(Cod4xCommandSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigurationDto>(
                HttpStatusCode.OK,
                new ApiResponse<ConfigurationDto>(dto)));
    }

    private void SetupServerSettings(object settings)
    {
        var dto = CreateConfigurationDto(Cod4xCommandSettingsConstants.Namespace, JsonConvert.SerializeObject(settings));

        _mockGameServerConfigurationsApi
            .Setup(x => x.GetConfiguration(_serverId, Cod4xCommandSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigurationDto>(
                HttpStatusCode.OK,
                new ApiResponse<ConfigurationDto>(dto)));
    }

    private void SetupServerSettingsNotFound()
    {
        _mockGameServerConfigurationsApi
            .Setup(x => x.GetConfiguration(_serverId, Cod4xCommandSettingsConstants.Namespace, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigurationDto>(
                HttpStatusCode.NotFound,
                new ApiResponse<ConfigurationDto>(new ApiError("NOT_FOUND", "Not found"))));
    }

    private static ConfigurationDto CreateConfigurationDto(string ns, string configuration)
    {
        var dto = new
        {
            Namespace = ns,
            Configuration = configuration,
            LastModifiedUtc = DateTime.UtcNow
        };

        return JsonConvert.DeserializeObject<ConfigurationDto>(JsonConvert.SerializeObject(dto))!;
    }
}
