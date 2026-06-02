using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Fetches agent-enabled game servers from the Portal Repository API.
/// Reads configuration exclusively from the per-server config namespace API.
/// </summary>
public sealed class RepositoryServerConfigProvider : IServerConfigProvider
{
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
                var resolvedTransportEnabled = ResolveFileTransportEnabled(dto);
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

                if (!TryGetStringValue(configs, "agent", "logFilePath", out var logFilePath))
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — missing agent configuration in config namespace",
                        dto.GameServerId, dto.Title);
                    continue;
                }

                var broadcasts = ParseBroadcastSettings(configs);
                var agentNamePrefix = globalAgentNamePrefix;
                if (TryGetStringValue(configs, "agent", "agentName", out var serverAgentName) &&
                    !string.IsNullOrWhiteSpace(serverAgentName))
                {
                    agentNamePrefix = serverAgentName;
                }

                var banFileRootPath = string.IsNullOrWhiteSpace(dto.BanFileRootPath) ? "/" : dto.BanFileRootPath;

                // Include DTO-level toggles that affect the agent loop in the hash so the
                // orchestrator restarts the agent when an admin changes them. ComputeConfigHash
                // by itself only sees the per-server config namespaces.
                var configHashInputs = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dto.FtpEnabled"] = dto.FtpEnabled.ToString(),
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
                    LogFilePath = logFilePath,
                    Hostname = dto.Hostname,
                    QueryPort = dto.QueryPort,
                    RconPassword = rconPassword,
                    FtpEnabled = dto.FtpEnabled,
                    RconEnabled = dto.RconEnabled,
                    BanFileSyncEnabled = dto.BanFileSyncEnabled,
                    BanFileRootPath = banFileRootPath,
                    AgentNamePrefix = agentNamePrefix,
                    Broadcasts = broadcasts,
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
                .FirstOrDefault(x => string.Equals(x.Namespace, "agent", StringComparison.OrdinalIgnoreCase));

            if (agentConfig is null || string.IsNullOrWhiteSpace(agentConfig.Configuration))
            {
                return ServerContext.DefaultAgentNamePrefix;
            }

            try
            {
                var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(agentConfig.Configuration);
                if (properties is not null &&
                    properties.TryGetValue("agentName", out var agentNameElement) &&
                    agentNameElement.ValueKind == JsonValueKind.String)
                {
                    var parsed = agentNameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(parsed))
                    {
                        return parsed;
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid global config payload; fall back to default prefix.
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch global agent prefix, using default");
        }

        return ServerContext.DefaultAgentNamePrefix;
    }

    private static bool ResolveFileTransportEnabled(object dto)
    {
        var fileTransportEnabledProperty = dto.GetType().GetProperty("FileTransportEnabled", BindingFlags.Public | BindingFlags.Instance);
        if (fileTransportEnabledProperty is not null)
        {
            var value = fileTransportEnabledProperty.GetValue(dto);
            if (value is bool enabled)
            {
                return enabled;
            }
        }

        var ftpEnabledProperty = dto.GetType().GetProperty("FtpEnabled", BindingFlags.Public | BindingFlags.Instance);
        if (ftpEnabledProperty?.GetValue(dto) is bool ftpEnabled)
        {
            return ftpEnabled;
        }

        return true;
    }

    private static TransportTypeResolution ResolveTransportType(object dto)
    {
        var fileTransportTypeProperty = dto.GetType().GetProperty("FileTransportType", BindingFlags.Public | BindingFlags.Instance);
        if (fileTransportTypeProperty is null)
        {
            return new TransportTypeResolution
            {
                IsPresent = false,
                IsValid = true,
                ResolvedType = FileTransportTypes.Ftp
            };
        }

        var value = fileTransportTypeProperty.GetValue(dto);
        if (value is null)
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

        // Value-type enum properties can appear as their default (0) even when
        // effectively unset in payloads from older contracts. Treat that as
        // metadata absent so legacy FTP fallback remains deterministic.
        if (fileTransportTypeProperty.PropertyType.IsEnum)
        {
            try
            {
                var numericValue = Convert.ToInt32(value);
                if (numericValue == 0)
                {
                    return new TransportTypeResolution
                    {
                        IsPresent = false,
                        IsValid = true,
                        RawValue = rawValue,
                        ResolvedType = FileTransportTypes.Ftp
                    };
                }
            }
            catch
            {
                // Fall through to invalid metadata handling.
            }
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

    private static BroadcastSettings ParseBroadcastSettings(
        Dictionary<string, Dictionary<string, JsonElement>> configs)
    {
        if (!configs.TryGetValue("broadcasts", out var namespaceConfig))
        {
            return new BroadcastSettings();
        }

        _ = TryGetBoolValue(configs, "broadcasts", "enabled", out var enabled);

        var intervalSeconds = ServerContext.DefaultBroadcastIntervalSeconds;
        if (TryGetIntValue(configs, "broadcasts", "intervalSeconds", out var parsedInterval) && parsedInterval > 0)
        {
            intervalSeconds = parsedInterval;
        }

        IReadOnlyList<BroadcastMessage> messages = Array.Empty<BroadcastMessage>();
        if (namespaceConfig.TryGetValue("messages", out var messagesElement) && messagesElement.ValueKind == JsonValueKind.Array)
        {
            var parsedMessages = new List<BroadcastMessage>();
            foreach (var item in messagesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("message", out var messageElement) ||
                    messageElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var messageText = messageElement.GetString();
                if (string.IsNullOrWhiteSpace(messageText))
                {
                    continue;
                }

                var messageEnabled = !item.TryGetProperty("enabled", out var enabledElement) ||
                                     (enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                      enabledElement.GetBoolean());

                parsedMessages.Add(new BroadcastMessage
                {
                    Message = messageText,
                    Enabled = messageEnabled
                });
            }

            messages = parsedMessages;
        }

        return new BroadcastSettings
        {
            Enabled = enabled,
            IntervalSeconds = intervalSeconds,
            Messages = messages
        };
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
