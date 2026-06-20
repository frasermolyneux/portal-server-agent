using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Agent;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.BanFiles;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Broadcasts;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Shared;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Fetches agent-enabled game servers from the Portal Repository API.
/// Reads configuration exclusively from the per-server config namespace API.
/// </summary>
public sealed class RepositoryServerConfigProvider : IServerConfigProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly IRepositoryApiClient _repositoryClient;
    private readonly ILogger<RepositoryServerConfigProvider> _logger;

    public RepositoryServerConfigProvider(
        IRepositoryApiClient repositoryClient,
        ILogger<RepositoryServerConfigProvider> logger)
    {
        _repositoryClient = repositoryClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ServerContext>> GetAgentEnabledServersAsync(CancellationToken ct)
    {
        try
        {
            var globalAgentNamePrefix = await FetchGlobalAgentNamePrefixAsync(ct);

            var result = await _repositoryClient.GameServers.V1.GetGameServers(
                gameTypes: null,
                gameServerIds: null,
                filter: GameServerFilter.AgentEnabled,
                skipEntries: 0,
                takeEntries: 50,
                order: null,
                cancellationToken: ct);

            if (!result.IsSuccess || result.Result?.Data?.Items is null)
            {
                _logger.LogError("Failed to retrieve agent-enabled servers from Repository API");
                return Array.Empty<ServerContext>();
            }

            var servers = new List<ServerContext>();

            foreach (var dto in result.Result.Data.Items)
            {
                var configs = await FetchConfigurationsAsync(dto.GameServerId, dto.Title, ct);

                var transportMetadata = ResolveTransportType(dto);
                if (transportMetadata.IsPresent && !transportMetadata.IsValid)
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — invalid FileTransportType metadata value '{Value}'",
                        dto.GameServerId,
                        dto.Title,
                        transportMetadata.RawValue);
                    continue;
                }

                var resolvedTransportType = transportMetadata.ResolvedType;
                var resolvedTransportEnabled = dto.FileTransportEnabled;
                var transportNamespace = resolvedTransportType;

                if (!TryGetStringValue(configs, transportNamespace, "hostname", out var transportHostname) ||
                    !TryGetStringValue(configs, transportNamespace, "username", out var transportUsername) ||
                    !TryGetStringValue(configs, transportNamespace, "password", out var transportPassword) ||
                    !TryGetIntValue(configs, transportNamespace, "port", out var transportPort))
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — missing {TransportType} configuration in config namespace '{Namespace}'",
                        dto.GameServerId, dto.Title, resolvedTransportType, transportNamespace);
                    continue;
                }

                string? sftpHostKeyFingerprint = null;
                if (string.Equals(resolvedTransportType, FileTransportTypes.Sftp, StringComparison.OrdinalIgnoreCase))
                {
                    _ = TryGetStringValue(configs, transportNamespace, "hostKeyFingerprint", out sftpHostKeyFingerprint);
                }

                if (!TryGetStringValue(configs, "rcon", "password", out var rconPassword))
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — missing RCON configuration in config namespace",
                        dto.GameServerId, dto.Title);
                    continue;
                }

                var agentSettings = ParseAgentSettings(configs);
                if (agentSettings is null || string.IsNullOrWhiteSpace(agentSettings.LogFilePath))
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — missing agent configuration in config namespace",
                        dto.GameServerId, dto.Title);
                    continue;
                }

                var broadcasts = ParseBroadcastSettings(configs);
                var banFileSettings = ParseBanFileSettings(configs);
                var screenshots = ParseScreenshotSettings(configs);
                var agentNamePrefix = globalAgentNamePrefix;
                if (!string.IsNullOrWhiteSpace(agentSettings.AgentName))
                {
                    agentNamePrefix = agentSettings.AgentName;
                }

                var banFileCheckIntervalSeconds =
                    banFileSettings?.CheckIntervalSeconds is > 0
                        ? banFileSettings.CheckIntervalSeconds.Value
                        : ServerContext.DefaultBanFileCheckIntervalSeconds;

                var banFileRootPath = string.IsNullOrWhiteSpace(dto.BanFileRootPath) ? "/" : dto.BanFileRootPath;

                // Include DTO-level toggles that affect the agent loop in the hash so the
                // orchestrator restarts the agent when an admin changes them. ComputeConfigHash
                // by itself only sees the per-server config namespaces.
                var configHashInputs = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dto.FileTransportEnabled"] = resolvedTransportEnabled.ToString(),
                    ["dto.FileTransportType"] = resolvedTransportType,
                    ["dto.RconEnabled"] = dto.RconEnabled.ToString(),
                    ["dto.BanFileSyncEnabled"] = dto.BanFileSyncEnabled.ToString(),
                    ["dto.BanFileRootPath"] = banFileRootPath,
                };
                configHashInputs[$"{transportNamespace}.hostname"] = transportHostname;
                configHashInputs[$"{transportNamespace}.port"] = transportPort.ToString();
                configHashInputs[$"{transportNamespace}.username"] = transportUsername;
                configHashInputs[$"{transportNamespace}.password"] = transportPassword;
                if (!string.IsNullOrWhiteSpace(sftpHostKeyFingerprint))
                {
                    configHashInputs[$"{transportNamespace}.hostKeyFingerprint"] = sftpHostKeyFingerprint;
                }
                configHashInputs["agent.agentNamePrefix"] = agentNamePrefix;
                AppendBroadcastHashFields(configHashInputs, broadcasts);
                AppendScreenshotHashFields(configHashInputs, screenshots);
                var configHash = ComputeConfigHash(configs, configHashInputs);

                servers.Add(new ServerContext
                {
                    ServerId = dto.GameServerId,
                    GameType = dto.GameType.ToString(),
                    Title = dto.Title,
                    FtpHostname = transportHostname,
                    FtpPort = transportPort,
                    FtpUsername = transportUsername,
                    FtpPassword = transportPassword,
                    FileTransportEnabled = resolvedTransportEnabled,
                    FileTransportType = resolvedTransportType,
                    FileTransportHostname = transportHostname,
                    FileTransportPort = transportPort,
                    FileTransportUsername = transportUsername,
                    FileTransportPassword = transportPassword,
                    FileTransportHostKeyFingerprint = sftpHostKeyFingerprint,
                    LogFilePath = agentSettings.LogFilePath,
                    Hostname = dto.Hostname,
                    QueryPort = dto.QueryPort,
                    RconPassword = rconPassword,
                    FtpEnabled = dto.FtpEnabled,
                    RconEnabled = dto.RconEnabled,
                    BanFileSyncEnabled = dto.BanFileSyncEnabled,
                    BanFileRootPath = banFileRootPath,
                    BanFileCheckIntervalSeconds = banFileCheckIntervalSeconds,
                    AgentNamePrefix = agentNamePrefix,
                    Broadcasts = broadcasts,
                    Screenshots = screenshots,
                    ConfigHash = configHash
                });
            }

            _logger.LogInformation("Loaded {Count} agent-enabled servers from Repository API", servers.Count);
            return servers;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error fetching servers from Repository API");
            return Array.Empty<ServerContext>();
        }
    }

    private async Task<string> FetchGlobalAgentNamePrefixAsync(CancellationToken ct)
    {
        try
        {
            var result = await _repositoryClient.GlobalConfigurations.V1.GetConfigurations(ct);
            if (!result.IsSuccess || result.Result?.Data?.Items is null)
            {
                return ServerContext.DefaultAgentNamePrefix;
            }

            var agentConfig = result.Result.Data.Items
                .FirstOrDefault(x => string.Equals(x.Namespace, AgentSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));

            if (agentConfig is null || string.IsNullOrWhiteSpace(agentConfig.Configuration))
            {
                return ServerContext.DefaultAgentNamePrefix;
            }

            var properties = ParseNamespaceProperties(agentConfig.Configuration);
            var parsed = ParseAgentSettings(properties);
            if (!string.IsNullOrWhiteSpace(parsed?.AgentName))
            {
                return parsed.AgentName;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch global agent prefix, using default");
        }

        return ServerContext.DefaultAgentNamePrefix;
    }

    private static TransportTypeResolution ResolveTransportType(GameServerDto dto)
    {
        var value = dto.FileTransportType;
        if (value == FileTransportType.Unknown)
        {
            return new TransportTypeResolution
            {
                IsPresent = false,
                IsValid = true,
                ResolvedType = FileTransportTypes.Ftp
            };
        }

        var rawValue = value.ToString();
        if (FileTransportTypes.TryNormalize(rawValue, out var normalized))
        {
            return new TransportTypeResolution
            {
                IsPresent = true,
                IsValid = true,
                RawValue = rawValue,
                ResolvedType = normalized
            };
        }

        return new TransportTypeResolution
        {
            IsPresent = true,
            IsValid = false,
            RawValue = rawValue,
            ResolvedType = FileTransportTypes.Ftp
        };
    }

    private sealed record TransportTypeResolution
    {
        public required bool IsPresent { get; init; }
        public required bool IsValid { get; init; }
        public string? RawValue { get; init; }
        public required string ResolvedType { get; init; }
    }

    /// <summary>
    /// Fetches all configuration namespaces for a server, returning a case-insensitive lookup.
    /// Returns an empty dictionary on failure so the server will be skipped.
    /// </summary>
    private async Task<Dictionary<string, Dictionary<string, JsonElement>>> FetchConfigurationsAsync(
        Guid serverId, string title, CancellationToken ct)
    {
        try
        {
            var configResult = await _repositoryClient.GameServerConfigurations.V1
                .GetConfigurations(serverId, ct);

            if (!configResult.IsSuccess || configResult.Result?.Data?.Items is null)
            {
                _logger.LogWarning("No configurations found for server {ServerId} ({Title})",
                    serverId, title);
                return new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
            }

            return ParseConfigurations(configResult.Result.Data.Items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch configurations for server {ServerId} ({Title})",
                serverId, title);
            return new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Parses configuration DTOs into a namespace → (key → value) lookup with case-insensitive keys.
    /// </summary>
    private static Dictionary<string, Dictionary<string, JsonElement>> ParseConfigurations(
        IEnumerable<ConfigurationDto> configs)
    {
        var result = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in configs)
        {
            if (string.IsNullOrWhiteSpace(config.Namespace) || string.IsNullOrWhiteSpace(config.Configuration))
                continue;

            try
            {
                var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(config.Configuration);
                if (properties is not null)
                {
                    result[config.Namespace] = new Dictionary<string, JsonElement>(properties, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (JsonException)
            {
                // Malformed JSON — skip this namespace
            }
        }

        return result;
    }

    private static Dictionary<string, JsonElement>? ParseNamespaceProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetStringValue(
        Dictionary<string, Dictionary<string, JsonElement>> configs,
        string ns, string key, out string value)
    {
        value = string.Empty;
        if (configs.TryGetValue(ns, out var nsConfig) &&
            nsConfig.TryGetValue(key, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                value = str;
                return true;
            }
        }
        return false;
    }

    private static bool TryGetIntValue(
        Dictionary<string, Dictionary<string, JsonElement>> configs,
        string ns, string key, out int value)
    {
        value = 0;
        if (configs.TryGetValue(ns, out var nsConfig) &&
            nsConfig.TryGetValue(key, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
                return true;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out value))
                return true;
        }
        return false;
    }

    private static bool TryGetStringValue(
        Dictionary<string, JsonElement> namespaceConfig,
        string key,
        out string value)
    {
        value = string.Empty;
        if (namespaceConfig.TryGetValue(key, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (!string.IsNullOrWhiteSpace(str))
            {
                value = str;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetIntValue(
        Dictionary<string, JsonElement> namespaceConfig,
        string key,
        out int value)
    {
        value = 0;
        if (!namespaceConfig.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetBoolValue(
        Dictionary<string, JsonElement> namespaceConfig,
        string key,
        out bool value)
    {
        value = false;
        if (!namespaceConfig.TryGetValue(key, out var element))
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private static AgentSettingsDocument? ParseAgentSettings(
        Dictionary<string, Dictionary<string, JsonElement>> configs)
    {
        return configs.TryGetValue(AgentSettingsConstants.Namespace, out var namespaceConfig)
            ? ParseAgentSettings(namespaceConfig)
            : null;
    }

    private static AgentSettingsDocument? ParseAgentSettings(
        Dictionary<string, JsonElement>? namespaceConfig)
    {
        if (namespaceConfig is null)
        {
            return null;
        }

        var document = DeserializeDocument<AgentSettingsDocument>(namespaceConfig);
        if (document is null)
        {
            return null;
        }

        if (!SchemaVersionSupport.IsSupported(document.SchemaVersion))
        {
            return null;
        }

        _ = new AgentSettingsValidator().Validate(document);

        return document;
    }

    private static BanFileSettingsDocument? ParseBanFileSettings(
        Dictionary<string, Dictionary<string, JsonElement>> configs)
    {
        if (!configs.TryGetValue(BanFileSettingsConstants.Namespace, out var namespaceConfig))
        {
            return null;
        }

        var document = DeserializeDocument<BanFileSettingsDocument>(namespaceConfig);
        if (document is null)
        {
            return null;
        }

        if (!SchemaVersionSupport.IsSupported(document.SchemaVersion))
        {
            return null;
        }

        _ = new BanFileSettingsValidator().Validate(document);

        return document;
    }

    private static BroadcastSettings ParseBroadcastSettings(
        Dictionary<string, Dictionary<string, JsonElement>> configs)
    {
        if (!configs.TryGetValue(BroadcastSettingsConstants.Namespace, out var namespaceConfig))
        {
            return new BroadcastSettings();
        }

        var enabled = false;
        _ = TryGetBoolValue(namespaceConfig, "enabled", out enabled);

        if (TryGetIntValue(namespaceConfig, "schemaVersion", out var schemaVersion) &&
            !SchemaVersionSupport.IsSupported(schemaVersion))
        {
            return new BroadcastSettings();
        }

        var intervalSeconds = ServerContext.DefaultBroadcastIntervalSeconds;
        if (TryGetIntValue(namespaceConfig, "intervalSeconds", out var configuredInterval) && configuredInterval > 0)
        {
            intervalSeconds = configuredInterval;
        }

        var messages = new List<BroadcastMessage>();
        if (namespaceConfig.TryGetValue("messages", out var messagesElement) &&
            messagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var messageElement in messagesElement.EnumerateArray())
            {
                if (messageElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!messageElement.TryGetProperty("message", out var textElement) ||
                    textElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var messageText = textElement.GetString();
                if (string.IsNullOrWhiteSpace(messageText))
                {
                    continue;
                }

                var messageEnabled = true;
                if (messageElement.TryGetProperty("enabled", out var enabledElement))
                {
                    if (enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        messageEnabled = enabledElement.GetBoolean();
                    }
                    else
                    {
                        continue;
                    }
                }

                messages.Add(new BroadcastMessage
                {
                    Message = messageText,
                    Enabled = messageEnabled
                });
            }
        }

        return new BroadcastSettings
        {
            Enabled = enabled,
            IntervalSeconds = intervalSeconds,
            Messages = messages.ToArray()
        };
    }

    private static T? DeserializeDocument<T>(Dictionary<string, JsonElement> namespaceConfig)
    {
        try
        {
            var json = JsonSerializer.Serialize(namespaceConfig);
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool TryGetBoolValue(
        Dictionary<string, Dictionary<string, JsonElement>> configs,
        string ns, string key, out bool value)
    {
        value = false;
        if (configs.TryGetValue(ns, out var nsConfig) &&
            nsConfig.TryGetValue(key, out var element))
        {
            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = element.GetBoolean();
                return true;
            }

            if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out value))
            {
                return true;
            }
        }

        return false;
    }

    private static ScreenshotSettings ParseScreenshotSettings(
        Dictionary<string, Dictionary<string, JsonElement>> configs)
    {
        if (!configs.TryGetValue("screenshots", out _))
        {
            return new ScreenshotSettings();
        }

        _ = TryGetBoolValue(configs, "screenshots", "enabled", out var enabled);

        var directoryPath = string.Empty;
        _ = TryGetStringValue(configs, "screenshots", "directoryPath", out directoryPath);

        var filePattern = ServerContext.DefaultScreenshotFilePattern;
        if (TryGetStringValue(configs, "screenshots", "filePattern", out var configuredPattern) &&
            !string.IsNullOrWhiteSpace(configuredPattern))
        {
            filePattern = configuredPattern.Trim();
        }

        var pollIntervalSeconds = ServerContext.DefaultScreenshotPollIntervalSeconds;
        if (TryGetIntValue(configs, "screenshots", "pollIntervalSeconds", out var configuredInterval))
        {
            pollIntervalSeconds = Math.Clamp(
                configuredInterval,
                ServerContext.MinScreenshotPollIntervalSeconds,
                ServerContext.MaxScreenshotPollIntervalSeconds);
        }

        return new ScreenshotSettings
        {
            Enabled = enabled,
            DirectoryPath = directoryPath,
            FilePattern = filePattern,
            PollIntervalSeconds = pollIntervalSeconds
        };
    }

    private static void AppendBroadcastHashFields(
        SortedDictionary<string, string>? configHashInputs,
        BroadcastSettings broadcasts)
    {
        if (configHashInputs is null)
        {
            return;
        }

        configHashInputs["broadcasts.enabled"] = broadcasts.Enabled.ToString();
        configHashInputs["broadcasts.intervalSeconds"] = broadcasts.IntervalSeconds.ToString();

        for (var index = 0; index < broadcasts.Messages.Count; index++)
        {
            var message = broadcasts.Messages[index];
            configHashInputs[$"broadcasts.messages[{index}].message"] = message.Message;
            configHashInputs[$"broadcasts.messages[{index}].enabled"] = message.Enabled.ToString();
        }
    }

    private static void AppendScreenshotHashFields(
        SortedDictionary<string, string>? configHashInputs,
        ScreenshotSettings screenshots)
    {
        if (configHashInputs is null)
        {
            return;
        }

        configHashInputs["screenshots.enabled"] = screenshots.Enabled.ToString();
        configHashInputs["screenshots.directoryPath"] = screenshots.DirectoryPath;
        configHashInputs["screenshots.filePattern"] = screenshots.FilePattern;
        configHashInputs["screenshots.pollIntervalSeconds"] = screenshots.PollIntervalSeconds.ToString();
    }

    /// <summary>
    /// Computes a deterministic SHA256 hash of all configuration namespaces plus the
    /// supplied <paramref name="extraFields"/>. Keys are sorted to ensure consistent
    /// ordering regardless of API response order.
    /// </summary>
    internal static string ComputeConfigHash(
        Dictionary<string, Dictionary<string, JsonElement>> configs,
        SortedDictionary<string, string>? extraFields = null)
    {
        var parts = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (ns, properties) in configs)
        {
            foreach (var (key, value) in properties)
            {
                parts[$"{ns}.{key}"] = value.ToString();
            }
        }

        if (extraFields is not null)
        {
            foreach (var (key, value) in extraFields)
            {
                parts[key] = value;
            }
        }

        var combined = string.Join("|", parts.Select(kv => $"{kv.Key}={kv.Value}"));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes);
    }
}
