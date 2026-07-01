using System.Collections.Concurrent;
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
                settings.OperationRequest = null;

                _ = await TryPersistSettingsAsync(repositoryApiClient, context.ServerId, settings, ct).ConfigureAwait(false);
                return;
            }

            ApplySuccessfulStateMutation(settings.RuntimeState, request, actionResult.EffectiveVersion);
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
            return ActionResult.Failure("Install operation targetVersion is invalid.");
        }

        if (string.Equals(runtimeState.CurrentVersion?.Trim(), targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            var alreadyLoadedResult = await VerifyPluginHealthAsync(context, coD4xRconApi, targetVersion, ct).ConfigureAwait(false);
            if (alreadyLoadedResult.IsSuccess)
            {
                return ActionResult.Success(targetVersion);
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
            return uploadResult;
        }

        var loadResult = await coD4xRconApi.LoadPlugin(
            context.ServerId,
            new CoD4xPluginRequestDto { PluginName = PluginName },
            ct).ConfigureAwait(false);

        if (!loadResult.IsSuccess)
        {
            return ActionResult.Failure(BuildApiFailureMessage("loadplugin", loadResult));
        }

        var healthResult = await VerifyPluginHealthAsync(context, coD4xRconApi, targetVersion, ct).ConfigureAwait(false);
        return healthResult.IsSuccess
            ? ActionResult.Success(targetVersion)
            : healthResult;
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
            return ActionResult.Failure("Rollback requested but no previous known-good version is available.");
        }

        if (rollbackVersion.Length > Cod4xPluginSettingsConstants.MaxVersionLength || !IsValidVersionToken(rollbackVersion))
        {
            return ActionResult.Failure("Rollback requested with an invalid previous known-good version.");
        }

        await TryBestEffortUnloadAsync(context, coD4xRconApi, ct).ConfigureAwait(false);

        var uploadResult = await UploadPluginBinaryAsync(context, settings, request, rollbackVersion, ct).ConfigureAwait(false);
        if (!uploadResult.IsSuccess)
        {
            return uploadResult;
        }

        var loadResult = await coD4xRconApi.LoadPlugin(
            context.ServerId,
            new CoD4xPluginRequestDto { PluginName = PluginName },
            ct).ConfigureAwait(false);

        if (!loadResult.IsSuccess)
        {
            return ActionResult.Failure(BuildApiFailureMessage("loadplugin (rollback)", loadResult));
        }

        var healthResult = await VerifyPluginHealthAsync(context, coD4xRconApi, rollbackVersion, ct).ConfigureAwait(false);
        return healthResult.IsSuccess
            ? ActionResult.Success(rollbackVersion)
            : healthResult;
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
            ? ActionResult.Success(null)
            : ActionResult.Failure(BuildApiFailureMessage("unloadplugin", unloadResult));
    }

    private async Task<ActionResult> VerifyPluginHealthAsync(
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
            return ActionResult.Failure(BuildApiFailureMessage("plugin info", pluginInfoResult));
        }

        var pluginInfo = pluginInfoResult.Result?.Data;
        if (string.IsNullOrWhiteSpace(pluginInfo))
        {
            return ActionResult.Failure("Plugin health check did not return plugin info output.");
        }

        if (!pluginInfo.Contains(expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return ActionResult.Failure($"Plugin health check did not report expected version '{expectedVersion}'.");
        }

        return ActionResult.Success(expectedVersion);
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

    private sealed record ActionResult(bool IsSuccess, string? EffectiveVersion, string? ErrorMessage)
    {
        public static ActionResult Success(string? effectiveVersion)
            => new(true, effectiveVersion, null);

        public static ActionResult Failure(string errorMessage)
            => new(false, null, errorMessage);
    }
}
