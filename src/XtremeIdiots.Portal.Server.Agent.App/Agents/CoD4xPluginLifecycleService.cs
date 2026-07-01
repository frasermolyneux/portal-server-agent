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
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public sealed class CoD4xPluginLifecycleService : ICoD4xPluginLifecycleService
{
    private const string CoD4xGameType = "CallOfDuty4x";
    private const string PluginName = "portal-cod4x-plugin";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CoD4xPluginLifecycleService> _logger;
    private readonly ConcurrentDictionary<Guid, string> _inFlightOperations = new();

    public CoD4xPluginLifecycleService(
        IServiceScopeFactory scopeFactory,
        ILogger<CoD4xPluginLifecycleService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
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

            var actionResult = await ExecuteActionAsync(
                    context,
                    serversApiClient.CoD4xRcon.V1,
                    settings.RuntimeState,
                    request,
                    ct)
                .ConfigureAwait(false);

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
        Cod4xPluginRuntimeState runtimeState,
        Cod4xPluginOperationRequest request,
        CancellationToken ct)
    {
        return request.Action switch
        {
            Cod4xPluginOperationAction.Install => await ExecuteInstallAsync(context, coD4xRconApi, request, ct).ConfigureAwait(false),
            Cod4xPluginOperationAction.Rollback => await ExecuteRollbackAsync(context, coD4xRconApi, runtimeState, ct).ConfigureAwait(false),
            Cod4xPluginOperationAction.Unload => await ExecuteUnloadAsync(context, coD4xRconApi, ct).ConfigureAwait(false),
            _ => ActionResult.Failure($"Unsupported CoD4x plugin lifecycle action '{request.Action}'.")
        };
    }

    private async Task<ActionResult> ExecuteInstallAsync(
        ServerContext context,
        ICoD4xRconApi coD4xRconApi,
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

        await TryBestEffortUnloadAsync(context, coD4xRconApi, ct).ConfigureAwait(false);

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
        Cod4xPluginRuntimeState runtimeState,
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
