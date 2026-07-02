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
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class CoD4xPluginLifecycleServiceTests : IDisposable
{
    private const string PluginArtifactRootEnvironmentVariable = "PORTAL_COD4X_PLUGIN_ARTIFACT_ROOT";
    private static readonly string TrustedArtifactRoot = Path.Combine(Path.GetTempPath(), "portal-cod4x-plugin-artifacts-test");

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
    private readonly Mock<IRemoteOpsSessionCoordinator> _mockRemoteOpsSessionCoordinator = new();
    private readonly Mock<IRemoteFileClient> _mockRemoteFileClient = new();
    private readonly TestLogger<CoD4xPluginLifecycleService> _logger = new();
    private readonly Guid _serverId = Guid.NewGuid();
    private readonly string? _previousArtifactRoot;

    public CoD4xPluginLifecycleServiceTests()
    {
        _previousArtifactRoot = Environment.GetEnvironmentVariable(PluginArtifactRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(PluginArtifactRootEnvironmentVariable, TrustedArtifactRoot);

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

        _mockRemoteFileClient.Setup(x => x.UploadAsync(
            It.IsAny<Stream>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockRemoteOpsSessionCoordinator.Setup(x => x.ExecuteAsync(
            It.IsAny<ServerContext>(),
            It.IsAny<Func<IRemoteFileClient, CancellationToken, Task>>(),
            It.IsAny<CancellationToken>()))
            .Returns<ServerContext, Func<IRemoteFileClient, CancellationToken, Task>, CancellationToken>((context, operation, cancellationToken) =>
            operation(_mockRemoteFileClient.Object, cancellationToken));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PluginArtifactRootEnvironmentVariable, _previousArtifactRoot);
        GC.SuppressFinalize(this);
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
        var artifactPath = CreateTemporaryArtifact(".so");
        var settings = new Cod4xPluginSettingsDocument
        {
            SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion,
            Enabled = true,
            PluginRootDirectory = "/fs_homepath/plugins",
            RuntimeState = new Cod4xPluginRuntimeState
            {
                CurrentVersion = "1.0.0"
            },
            OperationRequest = new Cod4xPluginOperationRequest
            {
                OperationId = "op-install-1",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["artifactPath"] = ToJsonElement(artifactPath),
                    ["healthReportChannel"] = ToJsonElement("cod4x-lifecycle-health"),
                    ["rollout"] = ToJsonElement(new
                    {
                        rolloutStage = "canary",
                        rolloutApproved = true,
                        canaryHealthy = true
                    })
                }
            }
        };

        try
        {
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
            Assert.NotNull(final.RuntimeState.ExtensionData);
            Assert.True(final.RuntimeState.ExtensionData!.TryGetValue("healthReport", out var runtimeReport));
            Assert.Equal("healthy", runtimeReport.GetProperty("HealthStatus").GetString());
            Assert.Equal("canary", runtimeReport.GetProperty("RolloutStage").GetString());

            Assert.NotNull(final.ExtensionData);
            Assert.True(final.ExtensionData!.TryGetValue("healthReportChannel", out var reportChannel));
            Assert.Equal("cod4x-lifecycle-health", reportChannel.GetString());
            Assert.True(final.ExtensionData.TryGetValue("healthReport", out var documentReport));
            Assert.Equal("Succeeded", documentReport.GetProperty("Status").GetString());
            Assert.True(final.ExtensionData.TryGetValue("rollout", out var rollout));
            Assert.Equal("canary", rollout.GetProperty("rolloutStage").GetString());
            Assert.True(rollout.GetProperty("rolloutApproved").GetBoolean());
            Assert.True(rollout.GetProperty("canaryHealthy").GetBoolean());
            Assert.True(final.ExtensionData.TryGetValue("rolloutEvaluation", out var rolloutEvaluation));
            Assert.True(rolloutEvaluation.GetProperty("rolloutGatePassed").GetBoolean());

            _mockCoD4xRconApi.Verify(x => x.UnloadPlugin(_serverId, It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockCoD4xRconApi.Verify(x => x.LoadPlugin(_serverId, It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockCoD4xRconApi.Verify(x => x.PluginInfo(_serverId, It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockRemoteFileClient.Verify(x => x.UploadAsync(
                It.IsAny<Stream>(),
                "/fs_homepath/plugins/portal-cod4x-plugin.so",
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            TryDeleteFile(artifactPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_AlreadyOnTargetAndHealthy_DoesNotRedeploy()
    {
        var settings = new Cod4xPluginSettingsDocument
        {
            SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion,
            Enabled = true,
            RuntimeState = new Cod4xPluginRuntimeState
            {
                CurrentVersion = "1.2.3"
            },
            OperationRequest = new Cod4xPluginOperationRequest
            {
                OperationId = "op-install-noop-1",
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
        Assert.Null(final.RuntimeState.LastError);
        Assert.Null(final.OperationRequest);

        _mockCoD4xRconApi.Verify(x => x.UnloadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.PluginInfo(_serverId, It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_RolloutGateDenied_SetsFailedStateWithoutDeploy()
    {
        var artifactPath = CreateTemporaryArtifact(".so");
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
                OperationId = "op-install-rollout-denied",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["artifactPath"] = ToJsonElement(artifactPath),
                    ["rolloutApproved"] = ToJsonElement(false),
                    ["rolloutStage"] = ToJsonElement("ring-1")
                }
            }
        };

        try
        {
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
            Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
            Assert.Contains("not approved", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Null(final.OperationRequest);

            _mockCoD4xRconApi.Verify(x => x.UnloadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            TryDeleteFile(artifactPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_MalformedRolloutApproval_FailsClosedWithoutDeploy()
    {
        var artifactPath = CreateTemporaryArtifact(".so");
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
                OperationId = "op-install-rollout-malformed-approval",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["artifactPath"] = ToJsonElement(artifactPath),
                    ["rolloutApproved"] = ToJsonElement("not-a-bool"),
                    ["rolloutStage"] = ToJsonElement("ring-2")
                }
            }
        };

        try
        {
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
            Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
            Assert.Contains("approval gate value is invalid", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Null(final.OperationRequest);

            _mockCoD4xRconApi.Verify(x => x.UnloadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            TryDeleteFile(artifactPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_MalformedRolloutSoakUntil_FailsClosedWithoutDeploy()
    {
        var artifactPath = CreateTemporaryArtifact(".so");
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
                OperationId = "op-install-rollout-malformed-soak",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["artifactPath"] = ToJsonElement(artifactPath),
                    ["rollout"] = ToJsonElement(new
                    {
                        rolloutStage = "ring-3",
                        rolloutApproved = true,
                        canaryHealthy = true,
                        rolloutSoakUntilUtc = "tomorrow"
                    })
                }
            }
        };

        try
        {
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
            Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
            Assert.Contains("soak-until value is invalid", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Null(final.OperationRequest);

            _mockCoD4xRconApi.Verify(x => x.UnloadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            TryDeleteFile(artifactPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_MissingArtifactPath_SetsFailedState()
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
                OperationId = "op-install-missing-artifact",
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
        Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
        Assert.Contains("missing artifactPath", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);

        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_ArtifactPathFileDoesNotExist_SetsFailedState()
    {
        Directory.CreateDirectory(TrustedArtifactRoot);
        var missingArtifactPath = Path.Combine(TrustedArtifactRoot, $"portal-cod4x-plugin-missing-{Guid.NewGuid():N}.so");

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
                OperationId = "op-install-missing-artifact-file",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = CreateExtensionData("artifactPath", missingArtifactPath)
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
        Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
        Assert.Contains("does not exist", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);

        _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_InvalidArtifactStorageAccountMetadata_SetsFailedState()
    {
        Directory.CreateDirectory(TrustedArtifactRoot);
        var missingArtifactPath = Path.Combine(TrustedArtifactRoot, $"portal-cod4x-plugin-missing-{Guid.NewGuid():N}.so");

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
                OperationId = "op-install-invalid-artifact-storage-account",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["artifactPath"] = ToJsonElement(missingArtifactPath),
                    ["artifactStorageAccountName"] = ToJsonElement("Invalid-Name"),
                    ["artifactContainerName"] = ToJsonElement("plugin-artifacts"),
                    ["artifactBlobPath"] = ToJsonElement("releases/1.2.3/linux/x86_64/portal-cod4x-plugin.so")
                }
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
        Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
        Assert.Contains("artifactStorageAccountName is invalid", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);

        _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_InvalidArtifactBlobPathMetadata_SetsFailedState()
    {
        Directory.CreateDirectory(TrustedArtifactRoot);
        var missingArtifactPath = Path.Combine(TrustedArtifactRoot, $"portal-cod4x-plugin-missing-{Guid.NewGuid():N}.so");

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
                OperationId = "op-install-invalid-artifact-blob-path",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["artifactPath"] = ToJsonElement(missingArtifactPath),
                    ["artifactStorageAccountName"] = ToJsonElement("storcod4xartifacts"),
                    ["artifactContainerName"] = ToJsonElement("plugin-artifacts"),
                    ["artifactBlobPath"] = ToJsonElement("../portal-cod4x-plugin.so")
                }
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
        Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
        Assert.Contains("Artifact blob path is invalid", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);

        _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_ArtifactBlobPathMismatchWithArtifactPath_SetsFailedState()
    {
        Directory.CreateDirectory(TrustedArtifactRoot);
        var missingArtifactPath = Path.Combine(TrustedArtifactRoot, $"portal-cod4x-plugin-missing-{Guid.NewGuid():N}.so");

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
                OperationId = "op-install-artifact-blob-mismatch",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                {
                    ["artifactPath"] = ToJsonElement(missingArtifactPath),
                    ["artifactStorageAccountName"] = ToJsonElement("storcod4xartifacts"),
                    ["artifactContainerName"] = ToJsonElement("plugin-artifacts"),
                    ["artifactBlobPath"] = ToJsonElement("releases/9.9.9/linux/x86_64/portal-cod4x-plugin.so")
                }
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
        Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
        Assert.Contains("does not match artifactPath", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);

        _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_ArtifactOutsideTrustedRoot_SetsFailedState()
    {
        var outsideArtifactPath = CreateArtifactOutsideTrustedRoot(".so");
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
                OperationId = "op-install-outside-root",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = CreateExtensionData("artifactPath", outsideArtifactPath)
            }
        };

        try
        {
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
            Assert.Equal(Cod4xPluginOperationStatus.Failed, final.RuntimeState!.LastOperationStatus);
            Assert.Contains("outside trusted plugin artifact root", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);

            _mockRemoteFileClient.Verify(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _mockCoD4xRconApi.Verify(x => x.LoadPlugin(It.IsAny<Guid>(), It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            TryDeleteFile(outsideArtifactPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InstallRequest_UnexpectedActionException_PersistsFailureAndClearsRequest()
    {
        var artifactPath = CreateTemporaryArtifact(".so");
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
                OperationId = "op-install-unexpected-exception",
                Action = Cod4xPluginOperationAction.Install,
                TargetVersion = "1.2.3",
                RequestedBy = "tester",
                ExtensionData = CreateExtensionData("artifactPath", artifactPath)
            }
        };

        try
        {
            SetupConfiguration(settings);

            _mockRemoteOpsSessionCoordinator.Setup(x => x.ExecuteAsync(
                    It.IsAny<ServerContext>(),
                    It.IsAny<Func<IRemoteFileClient, CancellationToken, Task>>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("synthetic failure"));

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
            Assert.Null(final.OperationRequest);
            Assert.Contains("plugin artifact upload failed", final.RuntimeState.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteFile(artifactPath);
        }
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
    public async Task ExecuteAsync_RollbackWithKnownGoodVersion_DeploysAndSetsRollbackSucceeded()
    {
        var artifactPath = CreateTemporaryArtifact(".so");
        var settings = new Cod4xPluginSettingsDocument
        {
            SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion,
            Enabled = true,
            PluginRootDirectory = "/fs_homepath/plugins",
            RuntimeState = new Cod4xPluginRuntimeState
            {
                CurrentVersion = "1.2.3",
                PreviousKnownGoodVersion = "1.0.0"
            },
            OperationRequest = new Cod4xPluginOperationRequest
            {
                OperationId = "op-rollback-success",
                Action = Cod4xPluginOperationAction.Rollback,
                RequestedBy = "tester",
                ExtensionData = CreateExtensionData("artifactPath", artifactPath)
            }
        };

        try
        {
            SetupConfiguration(settings);

            _mockCoD4xRconApi.Setup(x => x.PluginInfo(
                    _serverId,
                    It.IsAny<CoD4xPluginRequestDto>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(SuccessResult("portal-cod4x-plugin version 1.0.0"));

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
            Assert.Equal(Cod4xPluginOperationStatus.RollbackSucceeded, final.RuntimeState!.LastOperationStatus);
            Assert.Equal("1.0.0", final.RuntimeState.CurrentVersion);
            Assert.Null(final.OperationRequest);

            _mockRemoteFileClient.Verify(x => x.UploadAsync(
                It.IsAny<Stream>(),
                "/fs_homepath/plugins/portal-cod4x-plugin.so",
                It.IsAny<CancellationToken>()), Times.Once);
            _mockCoD4xRconApi.Verify(x => x.LoadPlugin(_serverId, It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockCoD4xRconApi.Verify(x => x.PluginInfo(_serverId, It.IsAny<CoD4xPluginRequestDto>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            TryDeleteFile(artifactPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InstallWithEmptyPluginInfo_SetsFailedState()
    {
        var artifactPath = CreateTemporaryArtifact(".so");
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
                RequestedBy = "tester",
                ExtensionData = CreateExtensionData("artifactPath", artifactPath)
            }
        };

        try
        {
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
        finally
        {
            TryDeleteFile(artifactPath);
        }
    }

    private CoD4xPluginLifecycleService CreateService()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_mockRepositoryApiClient.Object);
        services.AddSingleton(_mockServersApiClient.Object);
        services.AddSingleton(_mockRemoteOpsSessionCoordinator.Object);
        services.AddSingleton<ILogger<CoD4xPluginLifecycleService>>(_logger);

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new CoD4xPluginLifecycleService(scopeFactory, _mockRemoteOpsSessionCoordinator.Object, _logger);
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

    private static Dictionary<string, JsonElement> CreateExtensionData(string key, string value)
    {
        return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            [key] = ToJsonElement(value)
        };
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static string CreateTemporaryArtifact(string extension)
    {
        Directory.CreateDirectory(TrustedArtifactRoot);
        var path = Path.Combine(TrustedArtifactRoot, $"portal-cod4x-plugin-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "binary-content");
        return path;
    }

    private static string CreateArtifactOutsideTrustedRoot(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"portal-cod4x-plugin-outside-root-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, "binary-content");
        return path;
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
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
