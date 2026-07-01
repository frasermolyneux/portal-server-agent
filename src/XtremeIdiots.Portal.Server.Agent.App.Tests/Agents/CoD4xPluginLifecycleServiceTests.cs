using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class CoD4xPluginLifecycleServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Mock<IRepositoryApiClient> _mockRepositoryApiClient = new();
    private readonly Mock<IVersionedGameServerConfigurationsApi> _mockVersionedGameServerConfigurationsApi = new();
    private readonly Mock<IGameServerConfigurationsApi> _mockGameServerConfigurationsApi = new();
    private readonly Mock<IServersApiClient> _mockServersApiClient = new();
    private readonly Mock<IVersionedCoD4xRconApi> _mockVersionedCoD4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> _mockCoD4xRconApi = new();
    private readonly TestLogger<CoD4xPluginLifecycleService> _logger = new();
    private readonly Guid _serverId = Guid.NewGuid();

    public CoD4xPluginLifecycleServiceTests()
    {
        _mockRepositoryApiClient.Setup(x => x.GameServerConfigurations)
            .Returns(_mockVersionedGameServerConfigurationsApi.Object);
        _mockVersionedGameServerConfigurationsApi.Setup(x => x.V1)
            .Returns(_mockGameServerConfigurationsApi.Object);

        _mockServersApiClient.Setup(x => x.CoD4xRcon)
            .Returns(_mockVersionedCoD4xRconApi.Object);
        _mockVersionedCoD4xRconApi.Setup(x => x.V1)
            .Returns(_mockCoD4xRconApi.Object);

        _mockGameServerConfigurationsApi.Setup(x => x.UpsertConfiguration(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<UpsertConfigurationDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        _mockCoD4xRconApi.Setup(x => x.UnloadPlugin(
                It.IsAny<Guid>(),
                It.IsAny<CoD4xPluginRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("unloaded"));

        _mockCoD4xRconApi.Setup(x => x.LoadPlugin(
                It.IsAny<Guid>(),
                It.IsAny<CoD4xPluginRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("loaded"));

        _mockCoD4xRconApi.Setup(x => x.PluginInfo(
                It.IsAny<Guid>(),
                It.IsAny<CoD4xPluginRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult("portal-cod4x-plugin version 1.2.3"));
    }

    [Fact]
    public async Task ExecuteAsync_NonCod4xGameType_DoesNothing()
    {
        var service = CreateService();
        var context = CreateContext("CallOfDuty4");

        await service.ExecuteAsync(context, CancellationToken.None);

        _mockGameServerConfigurationsApi.Verify(x => x.GetConfigurations(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_NoOperationRequest_DoesNotPersistOrInvokeRcon()
    {
        var settings = new Cod4xPluginSettingsDocument
        {
            SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion,
            Enabled = true,
            RuntimeState = new Cod4xPluginRuntimeState
            {
                CurrentVersion = "1.0.0"
            }
        };

        SetupConfiguration(settings);
        var service = CreateService();

        await service.ExecuteAsync(CreateContext(), CancellationToken.None);

        _mockGameServerConfigurationsApi.Verify(x => x.UpsertConfiguration(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<UpsertConfigurationDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidOperationRequest_PersistsFailureAndClearsRequest()
    {
        var settings = new Cod4xPluginSettingsDocument
        {
            SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion,
            Enabled = true,
            RuntimeState = new Cod4xPluginRuntimeState
            {
                CurrentVersion = "1.0.0"
            },
            OperationRequest = new Cod4xPluginOperationRequest
            {
                OperationId = "   ",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester"
            }
        };

        SetupConfiguration(settings);

        UpsertConfigurationDto? upsertPayload = null;
        _mockGameServerConfigurationsApi.Setup(x => x.UpsertConfiguration(
                _serverId,
                Cod4xPluginSettingsConstants.Namespace,
                It.IsAny<UpsertConfigurationDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, UpsertConfigurationDto, CancellationToken>((_, _, dto, _) => upsertPayload = dto)
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        var service = CreateService();
        await service.ExecuteAsync(CreateContext(), CancellationToken.None);

        Assert.NotNull(upsertPayload);
        var persisted = DeserializeSettings(upsertPayload.Configuration);

        Assert.NotNull(persisted.RuntimeState);
        Assert.Equal(Cod4xPluginOperationStatus.Failed, persisted.RuntimeState!.LastOperationStatus);
        Assert.Null(persisted.OperationRequest);
        Assert.Contains("missing operationId", persisted.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);

        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_OverLengthOperationId_PersistsSanitizedFailureAndClearsRequest()
    {
        var overLengthOperationId = new string('x', Cod4xPluginSettingsConstants.MaxOperationIdLength + 16);
        var settings = new Cod4xPluginSettingsDocument
        {
            SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion,
            Enabled = true,
            RuntimeState = new Cod4xPluginRuntimeState
            {
                CurrentVersion = "1.0.0"
            },
            OperationRequest = new Cod4xPluginOperationRequest
            {
                OperationId = overLengthOperationId,
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester"
            }
        };

        SetupConfiguration(settings);

        UpsertConfigurationDto? upsertPayload = null;
        _mockGameServerConfigurationsApi.Setup(x => x.UpsertConfiguration(
                _serverId,
                Cod4xPluginSettingsConstants.Namespace,
                It.IsAny<UpsertConfigurationDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, UpsertConfigurationDto, CancellationToken>((_, _, dto, _) => upsertPayload = dto)
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        var service = CreateService();
        await service.ExecuteAsync(CreateContext(), CancellationToken.None);

        Assert.NotNull(upsertPayload);
        var persisted = DeserializeSettings(upsertPayload.Configuration);

        Assert.NotNull(persisted.RuntimeState);
        Assert.Equal(Cod4xPluginOperationStatus.Failed, persisted.RuntimeState!.LastOperationStatus);
        Assert.Equal(Cod4xPluginSettingsConstants.MaxOperationIdLength, persisted.RuntimeState.LastOperationId?.Length);
        Assert.Null(persisted.OperationRequest);
        Assert.Contains("operationId exceeds", persisted.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);

        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_SetsSucceededStateAndUpdatesVersions()
    {
        var settings = new Cod4xPluginSettingsDocument
        {
            SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion,
            Enabled = true,
            RuntimeState = new Cod4xPluginRuntimeState
            {
                CurrentVersion = "1.0.0"
            },
            OperationRequest = new Cod4xPluginOperationRequest
            {
                OperationId = "op-install-1",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester"
            }
        };

        SetupConfiguration(settings);

        var persistedPayloads = new List<UpsertConfigurationDto>();
        _mockGameServerConfigurationsApi.Setup(x => x.UpsertConfiguration(
                _serverId,
                Cod4xPluginSettingsConstants.Namespace,
                It.IsAny<UpsertConfigurationDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, UpsertConfigurationDto, CancellationToken>((_, _, dto, _) => persistedPayloads.Add(dto))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        var service = CreateService();
        await service.ExecuteAsync(CreateContext(), CancellationToken.None);

        Assert.True(persistedPayloads.Count >= 2);

        var final = DeserializeSettings(persistedPayloads[^1].Configuration);
        Assert.NotNull(final.RuntimeState);
        Assert.Equal(Cod4xPluginOperationStatus.Succeeded, final.RuntimeState!.LastOperationStatus);
        Assert.Equal("1.2.3", final.RuntimeState.CurrentVersion);
        Assert.Equal("1.0.0", final.RuntimeState.PreviousKnownGoodVersion);
        Assert.Null(final.OperationRequest);
        Assert.Null(final.RuntimeState.LastError);

        _mockCoD4xRconApi.Verify(x => x.UnloadPlugin(_serverId, It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(_serverId, It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockCoD4xRconApi.Verify(x => x.PluginInfo(_serverId, It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RollbackWithoutKnownGoodVersion_SetsRollbackFailed()
    {
        var settings = new Cod4xPluginSettingsDocument
        {
            SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion,
            Enabled = true,
            RuntimeState = new Cod4xPluginRuntimeState
            {
                CurrentVersion = "1.2.3",
                PreviousKnownGoodVersion = null
            },
            OperationRequest = new Cod4xPluginOperationRequest
            {
                OperationId = "op-rollback-1",
                Action = Cod4xPluginOperationAction.Rollback,
                RequestedBy = "tester"
            }
        };

        SetupConfiguration(settings);

        var persistedPayloads = new List<UpsertConfigurationDto>();
        _mockGameServerConfigurationsApi.Setup(x => x.UpsertConfiguration(
                _serverId,
                Cod4xPluginSettingsConstants.Namespace,
                It.IsAny<UpsertConfigurationDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, UpsertConfigurationDto, CancellationToken>((_, _, dto, _) => persistedPayloads.Add(dto))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        var service = CreateService();
        await service.ExecuteAsync(CreateContext(), CancellationToken.None);

        Assert.True(persistedPayloads.Count >= 2);
        var final = DeserializeSettings(persistedPayloads[^1].Configuration);

        Assert.NotNull(final.RuntimeState);
        Assert.Equal(Cod4xPluginOperationStatus.RollbackFailed, final.RuntimeState!.LastOperationStatus);
        Assert.Null(final.OperationRequest);

        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InstallWithEmptyPluginInfo_SetsFailedState()
    {
        var settings = new Cod4xPluginSettingsDocument
        {
            SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion,
            Enabled = true,
            RuntimeState = new Cod4xPluginRuntimeState
            {
                CurrentVersion = "1.0.0"
            },
            OperationRequest = new Cod4xPluginOperationRequest
            {
                OperationId = "op-install-empty-plugin-info",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester"
            }
        };

        SetupConfiguration(settings);

        _mockCoD4xRconApi.Setup(x => x.PluginInfo(
                _serverId,
                It.IsAny<CoD4xPluginRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(SuccessResult(string.Empty));

        var persistedPayloads = new List<UpsertConfigurationDto>();
        _mockGameServerConfigurationsApi.Setup(x => x.UpsertConfiguration(
                _serverId,
                Cod4xPluginSettingsConstants.Namespace,
                It.IsAny<UpsertConfigurationDto>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, string, UpsertConfigurationDto, CancellationToken>((_, _, dto, _) => persistedPayloads.Add(dto))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        var service = CreateService();
        await service.ExecuteAsync(CreateContext(), CancellationToken.None);

        Assert.True(persistedPayloads.Count >= 2);
        var final = DeserializeSettings(persistedPayloads[^1].Configuration);

        Assert.NotNull(final.RuntimeState);
        Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
        Assert.Equal("1.0.0", final.RuntimeState.CurrentVersion);
        Assert.Null(final.OperationRequest);
        Assert.Contains("did not return plugin info output", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);
    }

    private CoD4xPluginLifecycleService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockRepositoryApiClient.Object);
        services.AddSingleton(_mockServersApiClient.Object);
        services.AddSingleton<ILogger<CoD4xPluginLifecycleService>>(_logger);

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new CoD4xPluginLifecycleService(scopeFactory, _logger);
    }

    private void SetupConfiguration(Cod4xPluginSettingsDocument settings)
    {
        var configuration = JsonSerializer.Serialize(settings, JsonOptions);
        var dto = new ConfigurationDto();
        typeof(ConfigurationDto).GetProperty(nameof(ConfigurationDto.Namespace))!.SetValue(dto, Cod4xPluginSettingsConstants.Namespace);
        typeof(ConfigurationDto).GetProperty(nameof(ConfigurationDto.Configuration))!.SetValue(dto, configuration);

        var result = new ApiResult<CollectionModel<ConfigurationDto>>(
            HttpStatusCode.OK,
            new ApiResponse<CollectionModel<ConfigurationDto>>(new CollectionModel<ConfigurationDto>(new[] { dto })));

        _mockGameServerConfigurationsApi.Setup(x => x.GetConfigurations(_serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    private ServerContext CreateContext(string gameType = "CallOfDuty4x")
    {
        return new ServerContext
        {
            ServerId = _serverId,
            GameType = gameType,
            Title = "Test Server",
            FtpHostname = "ftp.example.com",
            FtpPort = 21,
            FtpUsername = "user",
            FtpPassword = "pass",
            LogFilePath = "/logs/games_mp.log",
            Hostname = "game.example.com",
            QueryPort = 28960,
            RconPassword = "secret",
            FtpEnabled = true,
            RconEnabled = true,
            BanFileSyncEnabled = false,
            BanFileRootPath = "/",
            ConfigHash = "test-hash"
        };
    }

    private static ApiResult<string> SuccessResult(string value)
    {
        return new ApiResult<string>(
            HttpStatusCode.OK,
            new ApiResponse<string>(value));
    }

    private static Cod4xPluginSettingsDocument DeserializeSettings(string payload)
    {
        var result = JsonSerializer.Deserialize<Cod4xPluginSettingsDocument>(payload, JsonOptions);
        Assert.NotNull(result);
        return result;
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
