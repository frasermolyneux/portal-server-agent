using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xCommands;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Shared;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public sealed class CoD4xCommandPowerReconciliationService(
    IRepositoryApiClient repositoryApiClient,
    ICoD4xRconApi coD4xRconApi,
    ILogger<CoD4xCommandPowerReconciliationService> logger) : ICoD4xCommandPowerReconciliationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly Regex ColorCodeRegex = new(@"\^[0-9]", RegexOptions.Compiled);
    private static readonly Regex CommandPowerLineRegex = new(
        @"^(?<command>[A-Za-z0-9_]+)\s+(?<power>\d{1,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task ReconcileAsync(Guid serverId, string? gameType, CancellationToken ct = default)
    {
        if (!IsCoD4x(gameType))
        {
            return;
        }

        try
        {
            var desiredBuild = await BuildDesiredCommandPowersAsync(serverId, ct).ConfigureAwait(false);
            if (!desiredBuild.Success)
            {
                return;
            }

            if (!desiredBuild.EnforcementEnabled)
            {
                logger.LogDebug(
                    "CoD4x command-power reconciliation skipped for {ServerId}: enforcement disabled",
                    serverId);
                return;
            }

            var commandListResult = await coD4xRconApi.AdminListCommands(serverId, ct).ConfigureAwait(false);
            if (!commandListResult.IsSuccess || commandListResult.Result?.Data is null)
            {
                logger.LogWarning(
                    "CoD4x AdminListCommands failed for {ServerId}: status {StatusCode}",
                    serverId,
                    commandListResult.StatusCode);
                return;
            }

            var currentPowers = ParseCurrentCommandPowers(commandListResult.Result.Data);

            var changedCount = 0;
            var skippedAlreadyHiddenCount = 0;

            foreach (var desired in desiredBuild.DesiredPowersByCommand.OrderBy(static x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!currentPowers.TryGetValue(desired.Key, out var currentPower))
                {
                    // Commands at power 100 are hidden by AdminListCommands output.
                    if (desired.Value >= Cod4xCommandSettingsConstants.MaxPower)
                    {
                        skippedAlreadyHiddenCount++;
                        continue;
                    }

                    if (await TrySetCommandPowerAsync(serverId, desired.Key, desired.Value, ct).ConfigureAwait(false))
                    {
                        changedCount++;
                    }

                    continue;
                }

                if (currentPower == desired.Value)
                {
                    continue;
                }

                if (await TrySetCommandPowerAsync(serverId, desired.Key, desired.Value, ct).ConfigureAwait(false))
                {
                    changedCount++;
                }
            }

            logger.LogInformation(
                "CoD4x command-power reconciliation for {ServerId}: desired={DesiredCount}, listed={ListedCount}, changed={ChangedCount}, ignoredUnknown={IgnoredUnknownCount}, skippedAlreadyHidden={SkippedAlreadyHiddenCount}",
                serverId,
                desiredBuild.DesiredPowersByCommand.Count,
                currentPowers.Count,
                changedCount,
                desiredBuild.IgnoredUnknownCommandCount,
                skippedAlreadyHiddenCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CoD4x command-power reconciliation failed for server {ServerId}", serverId);
        }
    }

    private static bool IsCoD4x(string? gameType)
        => string.Equals(gameType, nameof(GameType.CallOfDuty4x), StringComparison.OrdinalIgnoreCase);

    private async Task<DesiredCommandPowerBuildResult> BuildDesiredCommandPowersAsync(Guid serverId, CancellationToken ct)
    {
        var globalFetch = await TryFetchCommandSettingsDocumentAsync(
            () => repositoryApiClient.GlobalConfigurations.V1.GetConfiguration(Cod4xCommandSettingsConstants.Namespace, ct),
            serverId,
            "global").ConfigureAwait(false);

        var serverFetch = await TryFetchCommandSettingsDocumentAsync(
            () => repositoryApiClient.GameServerConfigurations.V1.GetConfiguration(serverId, Cod4xCommandSettingsConstants.Namespace, ct),
            serverId,
            "server").ConfigureAwait(false);

        if (!globalFetch.Success && !serverFetch.Success)
        {
            return DesiredCommandPowerBuildResult.Failed();
        }

        var globalEnabled = globalFetch.Success && globalFetch.Document?.Enabled == true;
        var serverEnabled = serverFetch.Success && serverFetch.Document?.Enabled == true;

        // Required behavior: enforce when global is enabled OR server override is enabled.
        var enforcementEnabled = globalEnabled || serverEnabled;

        if (!enforcementEnabled)
        {
            return DesiredCommandPowerBuildResult.Skipped();
        }

        var rulesByCommand = Cod4xCommandSettingsConstants.BuiltInCommandMinPowerDefaults
            .ToDictionary(
                static x => ResolveCanonicalCommand(x.Key),
                static x => new CommandPowerRule(true, Math.Clamp(x.Value, Cod4xCommandSettingsConstants.MinPower, Cod4xCommandSettingsConstants.MaxPower)),
                StringComparer.OrdinalIgnoreCase);

        var ignoredUnknownCommandCount = 0;

        if (globalEnabled)
        {
            ApplyOverrides(globalFetch.Document, rulesByCommand, ref ignoredUnknownCommandCount);
        }

        if (serverEnabled)
        {
            ApplyOverrides(serverFetch.Document, rulesByCommand, ref ignoredUnknownCommandCount);
        }

        var desiredPowersByCommand = rulesByCommand.ToDictionary(
            static x => x.Key,
            static x => x.Value.Enabled
                ? Math.Clamp(x.Value.MinPower, Cod4xCommandSettingsConstants.MinPower, Cod4xCommandSettingsConstants.MaxPower)
                : Cod4xCommandSettingsConstants.MaxPower,
            StringComparer.OrdinalIgnoreCase);

        return DesiredCommandPowerBuildResult.Enabled(desiredPowersByCommand, ignoredUnknownCommandCount);
    }

    private async Task<SettingsFetchResult> TryFetchCommandSettingsDocumentAsync(
        Func<Task<ApiResult<ConfigurationDto>>> fetch,
        Guid serverId,
        string scope)
    {
        ApiResult<ConfigurationDto> result;
        try
        {
            result = await fetch().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to fetch {Scope} cod4xCommands settings for {ServerId}",
                scope,
                serverId);
            return SettingsFetchResult.Failed();
        }

        if (result.IsNotFound)
        {
            return SettingsFetchResult.NotConfigured();
        }

        if (!result.IsSuccess || result.Result?.Data is null)
        {
            logger.LogWarning(
                "Failed to fetch {Scope} cod4xCommands settings for {ServerId}: status {StatusCode}",
                scope,
                serverId,
                result.StatusCode);
            return SettingsFetchResult.Failed();
        }

        var document = ParseSettingsDocument(result.Result.Data.Configuration);
        if (document is null)
        {
            logger.LogWarning(
                "Ignoring {Scope} cod4xCommands settings for {ServerId}: invalid or unsupported payload",
                scope,
                serverId);
            return SettingsFetchResult.Failed();
        }

        return SettingsFetchResult.Configured(document);
    }

    private static Cod4xCommandSettingsDocument? ParseSettingsDocument(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<Cod4xCommandSettingsDocument>(configuration, JsonOptions);
            if (document is null)
            {
                return null;
            }

            if (!SchemaVersionSupport.IsSupported(document.SchemaVersion))
            {
                return null;
            }

            var validation = new Cod4xCommandSettingsValidator().Validate(document);
            return validation.IsValid ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ApplyOverrides(
        Cod4xCommandSettingsDocument? document,
        Dictionary<string, CommandPowerRule> rulesByCommand,
        ref int ignoredUnknownCommandCount)
    {
        if (document?.Commands is null)
        {
            return;
        }

        foreach (var commandOverride in document.Commands)
        {
            var canonicalCommand = ResolveCanonicalCommand(commandOverride.Key);
            if (!rulesByCommand.TryGetValue(canonicalCommand, out var currentRule))
            {
                ignoredUnknownCommandCount++;
                continue;
            }

            var overrideEntry = commandOverride.Value;
            if (overrideEntry?.Enabled is bool enabled)
            {
                currentRule = currentRule with { Enabled = enabled };
            }

            if (overrideEntry?.MinPower is int minPower)
            {
                currentRule = currentRule with
                {
                    MinPower = Math.Clamp(minPower, Cod4xCommandSettingsConstants.MinPower, Cod4xCommandSettingsConstants.MaxPower)
                };
            }

            rulesByCommand[canonicalCommand] = currentRule;
        }
    }

    private static string ResolveCanonicalCommand(string command)
    {
        var trimmed = command.Trim();
        if (Cod4xCommandSettingsConstants.BuiltInCommandAliases.TryGetValue(trimmed, out var canonical))
        {
            return canonical;
        }

        return trimmed;
    }

    private static Dictionary<string, int> ParseCurrentCommandPowers(string rawCommandList)
    {
        var powersByCommand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lines = rawCommandList.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var rawLine in lines)
        {
            var line = ColorCodeRegex.Replace(rawLine, string.Empty).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var match = CommandPowerLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var command = ResolveCanonicalCommand(match.Groups["command"].Value);
            if (!int.TryParse(match.Groups["power"].Value, out var power))
            {
                continue;
            }

            powersByCommand[command] = Math.Clamp(
                power,
                Cod4xCommandSettingsConstants.MinPower,
                Cod4xCommandSettingsConstants.MaxPower);
        }

        return powersByCommand;
    }

    private async Task<bool> TrySetCommandPowerAsync(
        Guid serverId,
        string command,
        int minPower,
        CancellationToken ct)
    {
        var response = await coD4xRconApi.AdminChangeCommandPower(
            serverId,
            new CoD4xAdminChangeCommandPowerRequestDto
            {
                Command = command,
                MinPower = minPower
            },
            ct).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            logger.LogWarning(
                "AdminChangeCommandPower failed for {ServerId} command {Command}: status {StatusCode}",
                serverId,
                command,
                response.StatusCode);
            return false;
        }

        var commandOutput = response.Result?.Data;
        if (!string.IsNullOrWhiteSpace(commandOutput) &&
            commandOutput.Contains("Failed to change power of cmd", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "AdminChangeCommandPower command output indicates failure for {ServerId} command {Command}: {Output}",
                serverId,
                command,
                commandOutput);
            return false;
        }

        return true;
    }

    private sealed record CommandPowerRule(bool Enabled, int MinPower);

    private sealed record SettingsFetchResult(bool Success, Cod4xCommandSettingsDocument? Document)
    {
        public static SettingsFetchResult Configured(Cod4xCommandSettingsDocument document) => new(true, document);

        public static SettingsFetchResult NotConfigured() => new(true, null);

        public static SettingsFetchResult Failed() => new(false, null);
    }

    private sealed record DesiredCommandPowerBuildResult(
        bool Success,
        bool EnforcementEnabled,
        Dictionary<string, int> DesiredPowersByCommand,
        int IgnoredUnknownCommandCount)
    {
        public static DesiredCommandPowerBuildResult Enabled(Dictionary<string, int> desiredPowersByCommand, int ignoredUnknownCommandCount)
            => new(true, true, desiredPowersByCommand, ignoredUnknownCommandCount);

        public static DesiredCommandPowerBuildResult Skipped()
            => new(true, false, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 0);

        public static DesiredCommandPowerBuildResult Failed()
            => new(false, false, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 0);
    }
}
