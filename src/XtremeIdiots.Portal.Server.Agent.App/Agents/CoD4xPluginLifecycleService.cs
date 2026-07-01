using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public sealed class CoD4xPluginLifecycleService : ICoD4xPluginLifecycleService
{
    private const string CoD4xGameType = "CallOfDuty4x";
    private const string PluginName = "portal-cod4x-plugin";
    private const string PluginArtifactRootEnvironmentVariable = "PORTAL_COD4X_PLUGIN_ARTIFACT_ROOT";
    private static readonly string DefaultPluginArtifactRoot = Path.Combine(Path.GetTempPath(), "portal-cod4x-plugin-artifacts");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRemoteOpsSessionCoordinator _remoteOpsSessionCoordinator;
    private readonly ILogger<CoD4xPluginLifecycleService> _logger;
    private readonly ConcurrentDictionary<Guid, string> _inFlightOperations = new();

    private const string ExtensionKeyHealthReport = "healthReport";
    private const string ExtensionKeyRollout = "rollout";
    private const string ExtensionKeyHealthReportChannel = "healthReportChannel";
    private const string ExtensionKeyRolloutStage = "rolloutStage";
    private const string ExtensionKeyRolloutApproved = "rolloutApproved";
    private const string ExtensionKeyRolloutCanaryHealthy = "canaryHealthy";
    private const string ExtensionKeyRolloutSoakUntilUtc = "rolloutSoakUntilUtc";
    private const string ExtensionKeyRolloutGatePassed = "rolloutGatePassed";
    private const string ExtensionKeyRolloutEvaluation = "rolloutEvaluation";

    public CoD4xPluginLifecycleService(
        IServiceScopeFactory scopeFactory,
        IRemoteOpsSessionCoordinator remoteOpsSessionCoordinator,
        ILogger<CoD4xPluginLifecycleService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _remoteOpsSessionCoordinator = remoteOpsSessionCoordinator ?? throw new ArgumentNullException(nameof(remoteOpsSessionCoordinator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(ServerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(context.GameType, CoD4xGameType, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var repositoryApiClient = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();
        var serversApiClient = scope.ServiceProvider.GetRequiredService<IServersApiClient>();

        var configurationsResult = await repositoryApiClient.GameServerConfigurations.V1
            .GetConfigurations(context.ServerId, ct)
            .ConfigureAwait(false);

        if (!configurationsResult.IsSuccess || configurationsResult.Result?.Data?.Items is null)
        {
            _logger.LogWarning(
                "[{Title}] Skipping CoD4x plugin lifecycle check - failed to load game server configuration",
                context.Title);
            return;
        }

        var cod4xPluginConfiguration = configurationsResult.Result.Data.Items.FirstOrDefault(static config =>
            string.Equals(config.Namespace, Cod4xPluginSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));

        if (cod4xPluginConfiguration is null || string.IsNullOrWhiteSpace(cod4xPluginConfiguration.Configuration))
        {
            return;
        }

        if (!TryDeserializeSettings(cod4xPluginConfiguration.Configuration, out var settings) || settings is null)
        {
            _logger.LogWarning(
                "[{Title}] Skipping CoD4x plugin lifecycle check - malformed cod4xPlugin configuration payload",
                context.Title);
            return;
        }

        settings.SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion;

        if (!TryNormalizeAndValidateRequest(settings.OperationRequest, out var request, out var validationError))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(validationError))
        {
            if (request is null)
            {
                return;
            }

            settings.RuntimeState ??= new Cod4xPluginRuntimeState();
            settings.RuntimeState.LastOperationId = Truncate(request.OperationId, Cod4xPluginSettingsConstants.MaxOperationIdLength);
            settings.RuntimeState.LastOperationStatus = request.Action == Cod4xPluginOperationAction.Rollback
                ? Cod4xPluginOperationStatus.RollbackFailed
                : Cod4xPluginOperationStatus.Failed;
            settings.RuntimeState.LastOperationUtc = DateTimeOffset.UtcNow;
            settings.RuntimeState.LastError = Truncate(validationError, Cod4xPluginSettingsConstants.MaxLastErrorLength);
            settings.OperationRequest = null;

            _ = await TryPersistSettingsAsync(repositoryApiClient, context.ServerId, settings, ct).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.OperationId))
        {
            return;
        }

        if (IsTerminalDuplicateRequest(settings.RuntimeState, request))
        {
            settings.OperationRequest = null;
            _ = await TryPersistSettingsAsync(repositoryApiClient, context.ServerId, settings, ct).ConfigureAwait(false);
            return;
        }

        if (!TryBeginOperation(context.ServerId, request.OperationId))
        {
            return;
        }

        try
        {
            settings.OperationRequest = request;
            settings.RuntimeState ??= new Cod4xPluginRuntimeState();
            settings.RuntimeState.LastOperationId = request.OperationId;
            settings.RuntimeState.LastOperationStatus = request.Action == Cod4xPluginOperationAction.Rollback
                ? Cod4xPluginOperationStatus.RollbackStarted
                : Cod4xPluginOperationStatus.Running;
            settings.RuntimeState.LastOperationUtc = DateTimeOffset.UtcNow;
            settings.RuntimeState.LastError = null;

            if (!await TryPersistSettingsAsync(repositoryApiClient, context.ServerId, settings, ct).ConfigureAwait(false))
            {
                return;
            }

            ActionResult actionResult;
            try
            {
                actionResult = await ExecuteActionAsync(
                        context,
                        serversApiClient.CoD4xRcon.V1,
                        settings,
                        settings.RuntimeState,
                        request,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "[{Title}] CoD4x plugin lifecycle action {Action} failed with an unexpected exception",
                    context.Title,
                    request.Action);

                actionResult = ActionResult.Failure("Unexpected CoD4x plugin lifecycle exception.");
            }

            if (!actionResult.IsSuccess)
            {
                settings.RuntimeState.LastOperationStatus = request.Action == Cod4xPluginOperationAction.Rollback
                    ? Cod4xPluginOperationStatus.RollbackFailed
                    : Cod4xPluginOperationStatus.Failed;
                settings.RuntimeState.LastOperationUtc = DateTimeOffset.UtcNow;
                settings.RuntimeState.LastError = Truncate(actionResult.ErrorMessage, Cod4xPluginSettingsConstants.MaxLastErrorLength);
                ApplyOperationTelemetry(settings, request, settings.RuntimeState, actionResult, settings.RuntimeState.LastOperationStatus);
                settings.OperationRequest = null;

                _ = await TryPersistSettingsAsync(repositoryApiClient, context.ServerId, settings, ct).ConfigureAwait(false);
                return;
            }

            ApplySuccessfulStateMutation(settings.RuntimeState, request, actionResult.EffectiveVersion);
            ApplyOperationTelemetry(settings, request, settings.RuntimeState, actionResult, settings.RuntimeState.LastOperationStatus);
            settings.OperationRequest = null;

            _ = await TryPersistSettingsAsync(repositoryApiClient, context.ServerId, settings, ct).ConfigureAwait(false);
        }
        finally
        {
            CompleteOperation(context.ServerId, request.OperationId);
        }
    }

    private async Task<ActionResult> ExecuteActionAsync(
        ServerContext context,
        ICoD4xRconApi coD4xRconApi,
        Cod4xPluginSettingsDocument settings,
        Cod4xPluginRuntimeState runtimeState,
        Cod4xPluginOperationRequest request,
        CancellationToken ct)
    {
        return request.Action switch
        {
            Cod4xPluginOperationAction.Install => await ExecuteInstallAsync(context, coD4xRconApi, settings, runtimeState, request, ct).ConfigureAwait(false),
            Cod4xPluginOperationAction.Rollback => await ExecuteRollbackAsync(context, coD4xRconApi, settings, runtimeState, request, ct).ConfigureAwait(false),
            Cod4xPluginOperationAction.Unload => await ExecuteUnloadAsync(context, coD4xRconApi, ct).ConfigureAwait(false),
            _ => ActionResult.Failure($"Unsupported CoD4x plugin lifecycle action '{request.Action}'.")
        };
    }

    private async Task<ActionResult> ExecuteInstallAsync(
        ServerContext context,
        ICoD4xRconApi coD4xRconApi,
        Cod4xPluginSettingsDocument settings,
        Cod4xPluginRuntimeState runtimeState,
        Cod4xPluginOperationRequest request,
        CancellationToken ct)
    {
        var targetVersion = request.TargetVersion?.Trim();
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return ActionResult.Failure("Install operation requires targetVersion.");
        }

        if (targetVersion.Length > Cod4xPluginSettingsConstants.MaxVersionLength || !IsValidVersionToken(targetVersion))
        {
            return ActionResult.Failure(
                "Install operation targetVersion is invalid.",
                healthStatus: "unhealthy");
        }

        if (!TryEvaluateRolloutDecision(request, out var rolloutDecision, out var rolloutError))
        {
            return ActionResult.Failure(
                rolloutError,
                healthStatus: "rollout-blocked",
                rolloutStage: rolloutDecision.Stage,
                rolloutGatePassed: false);
        }

        if (string.Equals(runtimeState.CurrentVersion?.Trim(), targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            var alreadyLoadedResult = await VerifyPluginHealthAsync(context, coD4xRconApi, targetVersion, ct).ConfigureAwait(false);
            if (alreadyLoadedResult.IsSuccess)
            {
                return ActionResult.Success(
                    targetVersion,
                    healthStatus: "healthy",
                    healthDetails: alreadyLoadedResult.Details,
                    reportedVersion: targetVersion,
                    rolloutStage: rolloutDecision.Stage,
                    rolloutGatePassed: rolloutDecision.GatePassed);
            }

            _logger.LogInformation(
                "[{Title}] CoD4x plugin target version {TargetVersion} is already marked current but failed health check; attempting redeploy",
                context.Title,
                targetVersion);
        }

        await TryBestEffortUnloadAsync(context, coD4xRconApi, ct).ConfigureAwait(false);

        var uploadResult = await UploadPluginBinaryAsync(context, settings, request, targetVersion, ct).ConfigureAwait(false);
        if (!uploadResult.IsSuccess)
        {
            uploadResult.RolloutStage = rolloutDecision.Stage;
            uploadResult.RolloutGatePassed = rolloutDecision.GatePassed;
            return uploadResult;
        }

        var loadResult = await coD4xRconApi.LoadPlugin(
            context.ServerId,
            new CoD4xPluginRequestDto { PluginName = PluginName },
            ct).ConfigureAwait(false);

        if (!loadResult.IsSuccess)
        {
            return ActionResult.Failure(
                BuildApiFailureMessage("loadplugin", loadResult),
                healthStatus: "unhealthy",
                rolloutStage: rolloutDecision.Stage,
                rolloutGatePassed: rolloutDecision.GatePassed);
        }

        var healthResult = await VerifyPluginHealthAsync(context, coD4xRconApi, targetVersion, ct).ConfigureAwait(false);
        return healthResult.IsSuccess
            ? ActionResult.Success(
                targetVersion,
                healthStatus: "healthy",
                healthDetails: healthResult.Details,
                reportedVersion: targetVersion,
                rolloutStage: rolloutDecision.Stage,
                rolloutGatePassed: rolloutDecision.GatePassed)
            : ActionResult.Failure(
                healthResult.ErrorMessage,
                healthStatus: "unhealthy",
                healthDetails: healthResult.Details,
                reportedVersion: targetVersion,
                rolloutStage: rolloutDecision.Stage,
                rolloutGatePassed: rolloutDecision.GatePassed);
    }

    private async Task<ActionResult> ExecuteRollbackAsync(
        ServerContext context,
        ICoD4xRconApi coD4xRconApi,
        Cod4xPluginSettingsDocument settings,
        Cod4xPluginRuntimeState runtimeState,
        Cod4xPluginOperationRequest request,
        CancellationToken ct)
    {
        var rollbackVersion = runtimeState.PreviousKnownGoodVersion?.Trim();
        if (string.IsNullOrWhiteSpace(rollbackVersion))
        {
            return ActionResult.Failure(
                "Rollback requested but no previous known-good version is available.",
                healthStatus: "unhealthy");
        }

        if (rollbackVersion.Length > Cod4xPluginSettingsConstants.MaxVersionLength || !IsValidVersionToken(rollbackVersion))
        {
            return ActionResult.Failure(
                "Rollback requested with an invalid previous known-good version.",
                healthStatus: "unhealthy");
        }

        if (!TryEvaluateRolloutDecision(request, out var rolloutDecision, out var rolloutError))
        {
            return ActionResult.Failure(
                rolloutError,
                healthStatus: "rollout-blocked",
                rolloutStage: rolloutDecision.Stage,
                rolloutGatePassed: false);
        }

        await TryBestEffortUnloadAsync(context, coD4xRconApi, ct).ConfigureAwait(false);

        var uploadResult = await UploadPluginBinaryAsync(context, settings, request, rollbackVersion, ct).ConfigureAwait(false);
        if (!uploadResult.IsSuccess)
        {
            uploadResult.RolloutStage = rolloutDecision.Stage;
            uploadResult.RolloutGatePassed = rolloutDecision.GatePassed;
            return uploadResult;
        }

        var loadResult = await coD4xRconApi.LoadPlugin(
            context.ServerId,
            new CoD4xPluginRequestDto { PluginName = PluginName },
            ct).ConfigureAwait(false);

        if (!loadResult.IsSuccess)
        {
            return ActionResult.Failure(
                BuildApiFailureMessage("loadplugin (rollback)", loadResult),
                healthStatus: "unhealthy",
                rolloutStage: rolloutDecision.Stage,
                rolloutGatePassed: rolloutDecision.GatePassed);
        }

        var healthResult = await VerifyPluginHealthAsync(context, coD4xRconApi, rollbackVersion, ct).ConfigureAwait(false);
        return healthResult.IsSuccess
            ? ActionResult.Success(
                rollbackVersion,
                healthStatus: "healthy",
                healthDetails: healthResult.Details,
                reportedVersion: rollbackVersion,
                rolloutStage: rolloutDecision.Stage,
                rolloutGatePassed: rolloutDecision.GatePassed)
            : ActionResult.Failure(
                healthResult.ErrorMessage,
                healthStatus: "unhealthy",
                healthDetails: healthResult.Details,
                reportedVersion: rollbackVersion,
                rolloutStage: rolloutDecision.Stage,
                rolloutGatePassed: rolloutDecision.GatePassed);
    }

    private async Task<ActionResult> ExecuteUnloadAsync(
        ServerContext context,
        ICoD4xRconApi coD4xRconApi,
        CancellationToken ct)
    {
        var unloadResult = await coD4xRconApi.UnloadPlugin(
            context.ServerId,
            new CoD4xPluginRequestDto { PluginName = PluginName },
            ct).ConfigureAwait(false);

        return unloadResult.IsSuccess
            ? ActionResult.Success(null, healthStatus: "unloaded")
            : ActionResult.Failure(BuildApiFailureMessage("unloadplugin", unloadResult), healthStatus: "unhealthy");
    }

    private async Task<PluginHealthResult> VerifyPluginHealthAsync(
        ServerContext context,
        ICoD4xRconApi coD4xRconApi,
        string expectedVersion,
        CancellationToken ct)
    {
        var pluginInfoResult = await coD4xRconApi.PluginInfo(
            context.ServerId,
            new CoD4xPluginRequestDto { PluginName = PluginName },
            ct).ConfigureAwait(false);

        if (!pluginInfoResult.IsSuccess)
        {
            return PluginHealthResult.Failure(BuildApiFailureMessage("plugin info", pluginInfoResult));
        }

        var pluginInfo = pluginInfoResult.Result?.Data;
        if (string.IsNullOrWhiteSpace(pluginInfo))
        {
            return PluginHealthResult.Failure("Plugin health check did not return plugin info output.");
        }

        if (!pluginInfo.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return PluginHealthResult.Failure(
                $"Plugin health check did not report expected version '{expectedVersion}'.",
                details: Truncate(pluginInfo, Cod4xPluginSettingsConstants.MaxLastErrorLength));
        }

        return PluginHealthResult.Success(
            details: Truncate(pluginInfo, Cod4xPluginSettingsConstants.MaxLastErrorLength));
    }

    private async Task TryBestEffortUnloadAsync(
        ServerContext context,
        ICoD4xRconApi coD4xRconApi,
        CancellationToken ct)
    {
        var unloadResult = await coD4xRconApi.UnloadPlugin(
            context.ServerId,
            new CoD4xPluginRequestDto { PluginName = PluginName },
            ct).ConfigureAwait(false);

        if (!unloadResult.IsSuccess)
        {
            _logger.LogDebug(
                "[{Title}] Best-effort unload before install/rollback returned status {StatusCode}",
                context.Title,
                unloadResult.StatusCode);
        }
    }

    private async Task<ActionResult> UploadPluginBinaryAsync(
        ServerContext context,
        Cod4xPluginSettingsDocument settings,
        Cod4xPluginOperationRequest request,
        string version,
        CancellationToken ct)
    {
        if (!TryResolveArtifactPath(request, out var artifactPath, out var artifactPathError))
        {
            return ActionResult.Failure(artifactPathError);
        }

        if (!File.Exists(artifactPath))
        {
            return ActionResult.Failure($"Plugin artifact path '{artifactPath}' does not exist.");
        }

        if (!TryNormalizePluginRootDirectory(settings.PluginRootDirectory, out var rootDirectory, out var rootDirectoryError))
        {
            return ActionResult.Failure(rootDirectoryError);
        }

        if (!TryResolvePluginBinaryExtension(artifactPath, out var extension, out var extensionError))
        {
            return ActionResult.Failure(extensionError);
        }

        var remotePluginPath = BuildRemotePluginPath(rootDirectory, extension);

        try
        {
            await _remoteOpsSessionCoordinator.ExecuteAsync(
                context,
                async (remoteFileClient, token) =>
                {
                    await using var artifactStream = File.OpenRead(artifactPath);
                    await remoteFileClient.UploadAsync(artifactStream, remotePluginPath, token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            _logger.LogInformation(
                "[{Title}] Uploaded CoD4x plugin version {Version} from {ArtifactPath} to {RemotePluginPath}",
                context.Title,
                version,
                artifactPath,
                remotePluginPath);

            return ActionResult.Success(version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "[{Title}] Failed to upload CoD4x plugin version {Version} from {ArtifactPath} to {RemotePluginPath}",
                context.Title,
                version,
                artifactPath,
                remotePluginPath);

            return ActionResult.Failure($"Plugin artifact upload failed for version '{version}'.");
        }
    }

    private static void ApplySuccessfulStateMutation(
        Cod4xPluginRuntimeState runtimeState,
        Cod4xPluginOperationRequest request,
        string? effectiveVersion)
    {
        runtimeState.LastOperationId = request.OperationId;
        runtimeState.LastOperationStatus = request.Action == Cod4xPluginOperationAction.Rollback
            ? Cod4xPluginOperationStatus.RollbackSucceeded
            : Cod4xPluginOperationStatus.Succeeded;
        runtimeState.LastOperationUtc = DateTimeOffset.UtcNow;
        runtimeState.LastError = null;

        if (request.Action == Cod4xPluginOperationAction.Unload)
        {
            runtimeState.CurrentVersion = null;
            return;
        }

        var previousCurrentVersion = runtimeState.CurrentVersion?.Trim();
        if (request.Action == Cod4xPluginOperationAction.Install
            && !string.IsNullOrWhiteSpace(previousCurrentVersion)
            && !string.Equals(previousCurrentVersion, effectiveVersion, StringComparison.OrdinalIgnoreCase))
        {
            runtimeState.PreviousKnownGoodVersion = previousCurrentVersion;
        }

        runtimeState.CurrentVersion = effectiveVersion;
    }

    private async Task<bool> TryPersistSettingsAsync(
        IRepositoryApiClient repositoryApiClient,
        Guid serverId,
        Cod4xPluginSettingsDocument settings,
        CancellationToken ct)
    {
        try
        {
            settings.SchemaVersion = Cod4xPluginSettingsConstants.SchemaVersion;

            var payload = JsonSerializer.Serialize(settings, JsonOptions);
            var upsertResult = await repositoryApiClient.GameServerConfigurations.V1.UpsertConfiguration(
                    serverId,
                    Cod4xPluginSettingsConstants.Namespace,
                    new UpsertConfigurationDto { Configuration = payload },
                    ct)
                .ConfigureAwait(false);

            if (!upsertResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Failed to persist CoD4x plugin lifecycle state for server {ServerId}: status {StatusCode}",
                    serverId,
                    upsertResult.StatusCode);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Exception persisting CoD4x plugin lifecycle state for server {ServerId}", serverId);
            return false;
        }
    }

    private bool TryBeginOperation(Guid serverId, string operationId)
    {
        if (_inFlightOperations.TryGetValue(serverId, out var existingOperationId))
        {
            if (string.Equals(existingOperationId, operationId, StringComparison.Ordinal))
            {
                return false;
            }

            _logger.LogDebug(
                "Skipping CoD4x lifecycle operation {OperationId} for server {ServerId} because operation {ExistingOperationId} is still in-flight",
                operationId,
                serverId,
                existingOperationId);
            return false;
        }

        return _inFlightOperations.TryAdd(serverId, operationId);
    }

    private void CompleteOperation(Guid serverId, string operationId)
    {
        if (_inFlightOperations.TryGetValue(serverId, out var existingOperationId)
            && string.Equals(existingOperationId, operationId, StringComparison.Ordinal))
        {
            _inFlightOperations.TryRemove(serverId, out _);
        }
    }

    private static bool IsTerminalDuplicateRequest(
        Cod4xPluginRuntimeState? runtimeState,
        Cod4xPluginOperationRequest request)
    {
        if (runtimeState is null || string.IsNullOrWhiteSpace(runtimeState.LastOperationId))
        {
            return false;
        }

        if (!string.Equals(runtimeState.LastOperationId, request.OperationId, StringComparison.Ordinal))
        {
            return false;
        }

        return runtimeState.LastOperationStatus is Cod4xPluginOperationStatus.Succeeded
            or Cod4xPluginOperationStatus.Failed
            or Cod4xPluginOperationStatus.RollbackSucceeded
            or Cod4xPluginOperationStatus.RollbackFailed;
    }

    private static bool TryNormalizeAndValidateRequest(
        Cod4xPluginOperationRequest? request,
        out Cod4xPluginOperationRequest? normalized,
        out string? error)
    {
        normalized = null;
        error = null;

        if (request is null)
        {
            return false;
        }

        var operationId = request.OperationId?.Trim();
        var targetVersion = request.TargetVersion?.Trim();
        var requestedBy = request.RequestedBy?.Trim();

        request.OperationId = operationId;
        request.TargetVersion = targetVersion;
        request.RequestedBy = requestedBy;
        normalized = request;

        if (string.IsNullOrWhiteSpace(operationId))
        {
            error = "Operation request is missing operationId.";
            return true;
        }

        if (operationId.Length > Cod4xPluginSettingsConstants.MaxOperationIdLength)
        {
            error = "Operation request operationId exceeds the maximum length.";
            return true;
        }

        if (request.Action is Cod4xPluginOperationAction.Unknown)
        {
            error = "Operation request action is invalid.";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(requestedBy)
            && requestedBy.Length > Cod4xPluginSettingsConstants.MaxRequestedByLength)
        {
            error = "Operation request requestedBy exceeds the maximum length.";
            return true;
        }

        return true;
    }

    private static bool TryResolveArtifactPath(
        Cod4xPluginOperationRequest request,
        out string artifactPath,
        out string error)
    {
        artifactPath = string.Empty;
        error = string.Empty;

        if (!TryGetExtensionDataString(request.ExtensionData, "artifactPath", out var rawArtifactPath)
            && !TryGetExtensionDataString(request.ExtensionData, "artifactLocalPath", out rawArtifactPath))
        {
            error = "Operation request is missing artifactPath in extension data.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawArtifactPath))
        {
            error = "Operation request artifactPath is empty.";
            return false;
        }

        if (!TryGetCanonicalPath(rawArtifactPath.Trim(), out artifactPath, out error))
        {
            return false;
        }

        if (!TryResolveTrustedArtifactRoot(out var trustedRootPath, out error))
        {
            return false;
        }

        if (!IsPathUnderRoot(artifactPath, trustedRootPath))
        {
            error = $"Operation request artifactPath '{artifactPath}' is outside trusted plugin artifact root '{trustedRootPath}'.";
            return false;
        }

        return true;
    }

    private static bool TryGetExtensionDataString(
        IReadOnlyDictionary<string, JsonElement>? extensionData,
        string key,
        out string? value)
    {
        value = null;

        if (extensionData is null || extensionData.Count == 0)
        {
            return false;
        }

        var match = extensionData.FirstOrDefault(pair =>
            string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(match.Key))
        {
            return false;
        }

        if (match.Value.ValueKind == JsonValueKind.String)
        {
            value = match.Value.GetString();
            return true;
        }

        if (match.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            value = match.Value.ToString();
            return true;
        }

        return false;
    }

    private static bool TryNormalizePluginRootDirectory(string? pluginRootDirectory, out string normalizedRootDirectory, out string error)
    {
        normalizedRootDirectory = string.Empty;
        error = string.Empty;

        var normalized = string.IsNullOrWhiteSpace(pluginRootDirectory)
            ? "/plugins"
            : pluginRootDirectory.Trim();

        normalized = normalized.Replace('\\', '/');
        if (!normalized.StartsWith('/'))
        {
            normalized = $"/{normalized}";
        }

        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(static segment => segment.Trim())
            .ToArray();

        if (segments.Any(static segment => segment is "." or ".."))
        {
            error = "cod4xPlugin.pluginRootDirectory contains invalid traversal segments.";
            return false;
        }

        if (segments.Any(static segment => segment.Contains(':')))
        {
            error = "cod4xPlugin.pluginRootDirectory contains invalid path characters.";
            return false;
        }

        normalizedRootDirectory = segments.Length == 0
            ? "/"
            : $"/{string.Join('/', segments)}";

        return true;
    }

    private static bool TryResolvePluginBinaryExtension(string artifactPath, out string extension, out string error)
    {
        extension = string.Empty;
        error = string.Empty;

        var rawExtension = Path.GetExtension(artifactPath)?.Trim();
        if (string.IsNullOrWhiteSpace(rawExtension))
        {
            error = "Plugin artifact path is missing a file extension. Expected .so or .dll.";
            return false;
        }

        if (!string.Equals(rawExtension, ".so", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rawExtension, ".dll", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Plugin artifact extension '{rawExtension}' is not supported. Expected .so or .dll.";
            return false;
        }

        extension = rawExtension.ToLowerInvariant();
        return true;
    }

    private static bool TryResolveTrustedArtifactRoot(out string trustedRootPath, out string error)
    {
        trustedRootPath = string.Empty;
        error = string.Empty;

        var configuredRoot = Environment.GetEnvironmentVariable(PluginArtifactRootEnvironmentVariable);
        var candidateRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? DefaultPluginArtifactRoot
            : configuredRoot;

        if (!TryGetCanonicalPath(candidateRoot, out trustedRootPath, out error))
        {
            error = $"Configured trusted plugin artifact root is invalid: {error}";
            return false;
        }

        return true;
    }

    private static bool TryGetCanonicalPath(string rawPath, out string canonicalPath, out string error)
    {
        canonicalPath = string.Empty;
        error = string.Empty;

        try
        {
            canonicalPath = Path.GetFullPath(rawPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsPathUnderRoot(string fullPath, string rootPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, fullPath);
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        if (string.Equals(relativePath, "..", StringComparison.Ordinal))
        {
            return false;
        }

        var parentPrefix = $"..{Path.DirectorySeparatorChar}";
        var altParentPrefix = $"..{Path.AltDirectorySeparatorChar}";
        return !relativePath.StartsWith(parentPrefix, StringComparison.Ordinal)
               && !relativePath.StartsWith(altParentPrefix, StringComparison.Ordinal);
    }

    private static string BuildRemotePluginPath(string rootDirectory, string extension)
    {
        var normalizedExtension = string.IsNullOrWhiteSpace(extension)
            ? ".so"
            : extension;

        return rootDirectory == "/"
            ? $"/{PluginName}{normalizedExtension}"
            : $"{rootDirectory}/{PluginName}{normalizedExtension}";
    }

    private static string BuildApiFailureMessage<T>(string operation, ApiResult<T> result)
    {
        var error = result.Result?.Errors?.FirstOrDefault();
        var errorCode = error?.Code;
        var errorMessage = error?.Message;
        return string.IsNullOrWhiteSpace(errorCode) && string.IsNullOrWhiteSpace(errorMessage)
            ? $"{operation} returned status {result.StatusCode}."
            : $"{operation} returned status {result.StatusCode} ({errorCode}: {errorMessage}).";
    }

    private void ApplyOperationTelemetry(
        Cod4xPluginSettingsDocument settings,
        Cod4xPluginOperationRequest request,
        Cod4xPluginRuntimeState runtimeState,
        ActionResult actionResult,
        Cod4xPluginOperationStatus operationStatus)
    {
        var report = new HealthReportPayload
        {
            OperationId = request.OperationId,
            Action = request.Action.ToString(),
            Status = operationStatus.ToString(),
            HealthStatus = actionResult.HealthStatus,
            ReportedVersion = actionResult.ReportedVersion,
            RolloutStage = actionResult.RolloutStage,
            RolloutGatePassed = actionResult.RolloutGatePassed,
            LastUpdatedUtc = runtimeState.LastOperationUtc,
            LastError = runtimeState.LastError,
            Details = actionResult.HealthDetails
        };

        var reportElement = JsonSerializer.SerializeToElement(report, JsonOptions);

        runtimeState.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        runtimeState.ExtensionData[ExtensionKeyHealthReport] = reportElement;
        if (actionResult.RolloutGatePassed.HasValue)
        {
            runtimeState.ExtensionData[ExtensionKeyRolloutGatePassed] = JsonSerializer.SerializeToElement(actionResult.RolloutGatePassed.Value, JsonOptions);
        }

        settings.ExtensionData ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        settings.ExtensionData[ExtensionKeyHealthReport] = reportElement;

        var rolloutPayload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (request.ExtensionData is not null)
        {
            if (TryGetRolloutObject(request.ExtensionData) is JsonElement existingRollout)
            {
                foreach (var property in existingRollout.EnumerateObject())
                {
                    rolloutPayload[property.Name] = property.Value.Clone();
                }
            }

            CopyRolloutDirective(request.ExtensionData, rolloutPayload, ExtensionKeyRolloutStage);
            CopyRolloutDirective(request.ExtensionData, rolloutPayload, ExtensionKeyRolloutApproved);
            CopyRolloutDirective(request.ExtensionData, rolloutPayload, ExtensionKeyRolloutCanaryHealthy);
            CopyRolloutDirective(request.ExtensionData, rolloutPayload, ExtensionKeyRolloutSoakUntilUtc);
        }

        if (!rolloutPayload.ContainsKey(ExtensionKeyRolloutStage) && !string.IsNullOrWhiteSpace(actionResult.RolloutStage))
        {
            rolloutPayload[ExtensionKeyRolloutStage] = JsonSerializer.SerializeToElement(actionResult.RolloutStage, JsonOptions);
        }

        if (rolloutPayload.Count > 0)
        {
            settings.ExtensionData[ExtensionKeyRollout] = JsonSerializer.SerializeToElement(rolloutPayload, JsonOptions);
        }

        var rolloutEvaluation = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            [ExtensionKeyRolloutGatePassed] = actionResult.RolloutGatePassed,
            ["evaluatedAtUtc"] = runtimeState.LastOperationUtc,
            ["healthStatus"] = actionResult.HealthStatus
        };

        settings.ExtensionData[ExtensionKeyRolloutEvaluation] = JsonSerializer.SerializeToElement(rolloutEvaluation, JsonOptions);

        if (TryGetHealthReportChannel(request, out var channel))
        {
            settings.ExtensionData[ExtensionKeyHealthReportChannel] = JsonSerializer.SerializeToElement(channel, JsonOptions);
        }
    }

    private static bool TryEvaluateRolloutDecision(
        Cod4xPluginOperationRequest request,
        out RolloutDecision decision,
        out string error)
    {
        decision = RolloutDecision.Default;
        error = string.Empty;

        var extensionData = request.ExtensionData;
        if (extensionData is null || extensionData.Count == 0)
        {
            return true;
        }

        var rolloutObject = TryGetRolloutObject(extensionData);

        var stage = TryGetString(extensionData, rolloutObject, ExtensionKeyRolloutStage);
        if (!string.IsNullOrWhiteSpace(stage))
        {
            decision.Stage = Truncate(stage, Cod4xPluginSettingsConstants.MaxVersionLength) ?? decision.Stage;
        }

        if (TryGetRolloutElement(extensionData, rolloutObject, ExtensionKeyRolloutApproved, out var approvedElement))
        {
            if (!TryParseRolloutBoolean(approvedElement, out var approved))
            {
                decision.GatePassed = false;
                error = "Rollout approval gate value is invalid. Expected a boolean.";
                return false;
            }

            if (!approved)
            {
                decision.GatePassed = false;
                error = "Rollout promotion is not approved for this operation request.";
                return false;
            }
        }

        if (TryGetRolloutElement(extensionData, rolloutObject, ExtensionKeyRolloutCanaryHealthy, out var canaryHealthyElement))
        {
            if (!TryParseRolloutBoolean(canaryHealthyElement, out var canaryHealthy))
            {
                decision.GatePassed = false;
                error = "Rollout canary health gate value is invalid. Expected a boolean.";
                return false;
            }

            if (!canaryHealthy)
            {
                decision.GatePassed = false;
                error = "Rollout canary health gate failed for this operation request.";
                return false;
            }
        }

        if (TryGetRolloutElement(extensionData, rolloutObject, ExtensionKeyRolloutSoakUntilUtc, out var soakUntilElement))
        {
            if (!TryParseUtcDateTimeOffset(soakUntilElement, out var soakUntilUtc))
            {
                decision.GatePassed = false;
                error = "Rollout soak-until value is invalid. Expected a UTC timestamp string.";
                return false;
            }

            if (DateTimeOffset.UtcNow < soakUntilUtc)
            {
                decision.GatePassed = false;
                error = $"Rollout soak period has not elapsed. Earliest promotion time is {soakUntilUtc:O}.";
                return false;
            }
        }

        decision.GatePassed = true;
        return true;
    }

    private static bool TryGetHealthReportChannel(Cod4xPluginOperationRequest request, out string channel)
    {
        channel = string.Empty;
        var extensionData = request.ExtensionData;
        if (extensionData is null)
        {
            return false;
        }

        var rolloutObject = TryGetRolloutObject(extensionData);
        var value = TryGetString(extensionData, rolloutObject, ExtensionKeyHealthReportChannel);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        channel = value.Trim();
        return channel.Length > 0;
    }

    private static JsonElement? TryGetRolloutObject(IReadOnlyDictionary<string, JsonElement> extensionData)
    {
        var rolloutPair = extensionData.FirstOrDefault(static pair =>
            string.Equals(pair.Key, ExtensionKeyRollout, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(rolloutPair.Key))
        {
            return null;
        }

        return rolloutPair.Value.ValueKind == JsonValueKind.Object
            ? rolloutPair.Value
            : null;
    }

    private static bool TryGetRolloutElement(
        IReadOnlyDictionary<string, JsonElement> extensionData,
        JsonElement? rolloutObject,
        string key,
        out JsonElement value)
    {
        if (TryGetElementFromDictionary(extensionData, key, out value))
        {
            return true;
        }

        return rolloutObject is not null
               && TryGetPropertyCaseInsensitive(rolloutObject.Value, key, out value);
    }

    private static bool TryGetElementFromDictionary(
        IReadOnlyDictionary<string, JsonElement> source,
        string key,
        out JsonElement value)
    {
        var match = source.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(match.Key))
        {
            value = default;
            return false;
        }

        value = match.Value;
        return true;
    }

    private static void CopyRolloutDirective(
        IReadOnlyDictionary<string, JsonElement> extensionData,
        IDictionary<string, JsonElement> destination,
        string key)
    {
        if (TryGetElementFromDictionary(extensionData, key, out var element))
        {
            destination[key] = element.Clone();
        }
    }

    private static string? TryGetString(IReadOnlyDictionary<string, JsonElement> extensionData, JsonElement? nestedObject, string key)
    {
        var direct = TryGetStringFromDictionary(extensionData, key);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        return nestedObject is null
            ? null
            : TryGetStringFromObject(nestedObject.Value, key);
    }

    private static string? TryGetStringFromDictionary(IReadOnlyDictionary<string, JsonElement> extensionData, string key)
    {
        var match = extensionData.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(match.Key))
        {
            return null;
        }

        return TryGetElementAsString(match.Value);
    }

    private static string? TryGetStringFromObject(JsonElement element, string key)
    {
        if (!TryGetPropertyCaseInsensitive(element, key, out var value))
        {
            return null;
        }

        return TryGetElementAsString(value);
    }

    private static string? TryGetElementAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static bool TryParseRolloutBoolean(JsonElement element, out bool value)
    {
        value = false;

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            return !string.IsNullOrWhiteSpace(raw) && bool.TryParse(raw, out value);
        }

        return false;
    }

    private static bool TryParseUtcDateTimeOffset(JsonElement element, out DateTimeOffset value)
    {
        value = default;

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var hasUtcDesignator = raw.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            || raw.EndsWith("+00:00", StringComparison.Ordinal)
            || raw.EndsWith("-00:00", StringComparison.Ordinal);

        if (!hasUtcDesignator)
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        if (parsed.Offset != TimeSpan.Zero)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement element, string key, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }

    private static bool TryDeserializeSettings(string configuration, out Cod4xPluginSettingsDocument? settings)
    {
        settings = null;

        try
        {
            settings = JsonSerializer.Deserialize<Cod4xPluginSettingsDocument>(configuration, JsonOptions);
            return settings is not null;
        }
        catch (JsonException)
        {
            try
            {
                var normalized = NormalizeBooleanStrings(configuration);
                settings = JsonSerializer.Deserialize<Cod4xPluginSettingsDocument>(normalized, JsonOptions);
                return settings is not null;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    private static string NormalizeBooleanStrings(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is null)
        {
            return json;
        }

        NormalizeBooleanNodes(node);
        return node.ToJsonString();
    }

    private static void NormalizeBooleanNodes(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            var keys = jsonObject.Select(static x => x.Key).ToArray();
            foreach (var key in keys)
            {
                var child = jsonObject[key];
                if (child is null)
                {
                    continue;
                }

                if (child is JsonValue jsonValue
                    && jsonValue.TryGetValue<string>(out var stringValue)
                    && bool.TryParse(stringValue, out var parsedBool))
                {
                    jsonObject[key] = parsedBool;
                    continue;
                }

                NormalizeBooleanNodes(child);
            }

            return;
        }

        if (node is not JsonArray jsonArray)
        {
            return;
        }

        for (var i = 0; i < jsonArray.Count; i++)
        {
            var child = jsonArray[i];
            if (child is null)
            {
                continue;
            }

            if (child is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out var stringValue)
                && bool.TryParse(stringValue, out var parsedBool))
            {
                jsonArray[i] = parsedBool;
                continue;
            }

            NormalizeBooleanNodes(child);
        }
    }

    private static bool IsValidVersionToken(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || !char.IsLetterOrDigit(version[0]))
        {
            return false;
        }

        for (var i = 1; i < version.Length; i++)
        {
            var current = version[i];
            if (!char.IsLetterOrDigit(current) && current is not '.' and not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private sealed class ActionResult
    {
        public static ActionResult Success(
            string? effectiveVersion,
            string? healthStatus = null,
            string? healthDetails = null,
            string? reportedVersion = null,
            string? rolloutStage = null,
            bool? rolloutGatePassed = null)
            => new(
                isSuccess: true,
                effectiveVersion: effectiveVersion,
                errorMessage: null,
                healthStatus: healthStatus,
                healthDetails: healthDetails,
                reportedVersion: reportedVersion,
                rolloutStage: rolloutStage,
                rolloutGatePassed: rolloutGatePassed);

        public static ActionResult Failure(
            string? errorMessage,
            string? healthStatus = null,
            string? healthDetails = null,
            string? reportedVersion = null,
            string? rolloutStage = null,
            bool? rolloutGatePassed = null)
            => new(
                isSuccess: false,
                effectiveVersion: null,
                errorMessage: errorMessage,
                healthStatus: healthStatus,
                healthDetails: healthDetails,
                reportedVersion: reportedVersion,
                rolloutStage: rolloutStage,
                rolloutGatePassed: rolloutGatePassed);

        private ActionResult(
            bool isSuccess,
            string? effectiveVersion,
            string? errorMessage,
            string? healthStatus,
            string? healthDetails,
            string? reportedVersion,
            string? rolloutStage,
            bool? rolloutGatePassed)
        {
            IsSuccess = isSuccess;
            EffectiveVersion = effectiveVersion;
            ErrorMessage = errorMessage;
            HealthStatus = healthStatus;
            HealthDetails = healthDetails;
            ReportedVersion = reportedVersion;
            RolloutStage = rolloutStage;
            RolloutGatePassed = rolloutGatePassed;
        }

        public bool IsSuccess { get; }

        public string? EffectiveVersion { get; }

        public string? ErrorMessage { get; }

        public string? HealthStatus { get; }

        public string? HealthDetails { get; }

        public string? ReportedVersion { get; }

        public string? RolloutStage { get; set; }

        public bool? RolloutGatePassed { get; set; }
    }

    private sealed class PluginHealthResult
    {
        public static PluginHealthResult Success(string? details = null) => new(true, null, details);

        public static PluginHealthResult Failure(string? errorMessage, string? details = null) => new(false, errorMessage, details);

        private PluginHealthResult(bool isSuccess, string? errorMessage, string? details)
        {
            IsSuccess = isSuccess;
            ErrorMessage = errorMessage;
            Details = details;
        }

        public bool IsSuccess { get; }

        public string? ErrorMessage { get; }

        public string? Details { get; }
    }

    private sealed class RolloutDecision
    {
        public static RolloutDecision Default => new()
        {
            Stage = "direct",
            GatePassed = true
        };

        public string Stage { get; set; } = "direct";

        public bool GatePassed { get; set; }
    }

    private sealed class HealthReportPayload
    {
        public string? OperationId { get; set; }

        public string? Action { get; set; }

        public string? Status { get; set; }

        public string? HealthStatus { get; set; }

        public string? ReportedVersion { get; set; }

        public string? RolloutStage { get; set; }

        public bool? RolloutGatePassed { get; set; }

        public DateTimeOffset? LastUpdatedUtc { get; set; }

        public string? LastError { get; set; }

        public string? Details { get; set; }
    }
}
