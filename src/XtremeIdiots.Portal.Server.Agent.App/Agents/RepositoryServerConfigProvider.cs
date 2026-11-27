using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Agent;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.BanFiles;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Broadcasts;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xPlugin;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Cod4xCommands;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.FileTransport;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.Shared;
using RepositoryFileTransportType = XtremeIdiots.Portal.Repository.Abstractions.Constants.V1.FileTransportType;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Fetches agent-enabled game servers from the Portal Repository API.
/// Resolves server configuration from per-server namespaces and supported global defaults.
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
    private readonly TimeProvider _timeProvider;
    private CommandSnapshotBatch? _commandSnapshots;
    public AgentInventory? Inventory { get; private set; }

    private sealed record CommandSnapshotBatch(
        DateTimeOffset StartedAt,
        ConfigurationSnapshotValue Global,
        IReadOnlyDictionary<Guid, ConfigurationSnapshotValue> Servers);

    public bool TryGetCommandSettings(Guid serverId, out CommandSettingsSnapshot snapshot)
    {
        var batch = Volatile.Read(ref _commandSnapshots);
        var age = batch is null ? TimeSpan.MaxValue : _timeProvider.GetUtcNow() - batch.StartedAt;
        if (batch is not null
            && age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(1)
            && batch.Servers.TryGetValue(serverId, out var server))
        {
            snapshot = new(batch.Global, server);
            return true;
        }

        snapshot = default;
        return false;
    }

    public RepositoryServerConfigProvider(
        IRepositoryApiClient repositoryClient,
        ILogger<RepositoryServerConfigProvider> logger,
        TimeProvider? timeProvider = null)
    {
        _repositoryClient = repositoryClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ServerContext>> GetAgentEnabledServersAsync(CancellationToken ct)
    {
        Volatile.Write(ref _commandSnapshots, null);
        var startedAt = _timeProvider.GetUtcNow();
        try
        {
            var globalConfigurations = await FetchGlobalConfigurationsAsync(ct);
            var globalAgentNamePrefix = ResolveGlobalAgentNamePrefix(globalConfigurations ?? []);
            var globalCod4xPluginSourceEnabled = ResolveGlobalCod4xPluginEnabled(globalConfigurations ?? []);
            var globalBroadcasts = globalConfigurations is null ? null : ParseGlobalBroadcastSettings(globalConfigurations);
            var commandSnapshots = new Dictionary<Guid, ConfigurationSnapshotValue>();

            var inventory = new List<GameServerDto>();
            var identities = new HashSet<Guid>();
            while (true)
            {
                var result = await _repositoryClient.GameServers.V1.GetGameServers(
                    gameTypes: null, gameServerIds: null, filter: null,
                    skipEntries: inventory.Count, takeEntries: 50, order: null, cancellationToken: ct);
                if (!result.IsSuccess || result.Result?.Data?.Items is null)
                {
                    throw new HttpRequestException("Failed to retrieve the complete configured server inventory.");
                }

                var page = result.Result.Data.Items.ToArray();
                if (page.Length > 50 || page.Any(server => server.GameServerId == Guid.Empty || !identities.Add(server.GameServerId)))
                {
                    throw new InvalidDataException("Configured server inventory contains invalid or repeated identities.");
                }
                inventory.AddRange(page);
                if (page.Length < 50)
                {
                    break;
                }
            }
            var servers = new List<ServerContext>();

            foreach (var dto in inventory.Where(server => server.AgentEnabled))
            {
                var (configs, commandSettings, serverBroadcasts) = await FetchConfigurationsAsync(dto.GameServerId, dto.Title, ct);
                commandSnapshots.Add(dto.GameServerId, commandSettings);

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
                    !TryGetIntValue(configs, transportNamespace, "port", out var transportPort))
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — missing {TransportType} configuration in config namespace '{Namespace}'",
                        dto.GameServerId, dto.Title, resolvedTransportType, transportNamespace);
                    continue;
                }

                var transportPassword = string.Empty;
                string? sftpHostKeyFingerprint = null;
                var sftpAuthenticationType = SftpAuthenticationType.Password;
                string? sftpPrivateKey = null;
                string? sftpPrivateKeyPassphrase = null;
                if (string.Equals(resolvedTransportType, FileTransportTypes.Sftp, StringComparison.OrdinalIgnoreCase))
                {
                    _ = TryGetStringValue(configs, transportNamespace, "hostKeyFingerprint", out sftpHostKeyFingerprint);

                    if (TryGetStringValue(configs, transportNamespace, "authenticationType", out var authenticationTypeValue)
                        && (!Enum.TryParse(authenticationTypeValue, ignoreCase: true, out sftpAuthenticationType)
                            || !Enum.IsDefined(sftpAuthenticationType)))
                    {
                        _logger.LogWarning(
                            "Skipping server {ServerId} ({Title}) — invalid SFTP authentication type '{AuthenticationType}'",
                            dto.GameServerId, dto.Title, authenticationTypeValue);
                        continue;
                    }

                    if (sftpAuthenticationType == SftpAuthenticationType.PrivateKey)
                    {
                        if (!TryGetStringValue(configs, transportNamespace, "privateKey", out sftpPrivateKey))
                        {
                            _logger.LogWarning(
                                "Skipping server {ServerId} ({Title}) — missing SFTP private key",
                                dto.GameServerId, dto.Title);
                            continue;
                        }

                        _ = TryGetStringValue(configs, transportNamespace, "privateKeyPassphrase", out sftpPrivateKeyPassphrase);
                    }
                    else if (!TryGetStringValue(configs, transportNamespace, "password", out transportPassword))
                    {
                        _logger.LogWarning(
                            "Skipping server {ServerId} ({Title}) — missing SFTP password",
                            dto.GameServerId, dto.Title);
                        continue;
                    }
                }
                else if (!TryGetStringValue(configs, transportNamespace, "password", out transportPassword))
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — missing FTP password",
                        dto.GameServerId, dto.Title);
                    continue;
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

                var broadcasts = ResolveBroadcastSettings(serverBroadcasts, globalBroadcasts, dto.GameServerId);
                var banFileSettings = ParseBanFileSettings(configs);
                var isCod4xServer = string.Equals(dto.GameType.ToString(), "CallOfDuty4x", StringComparison.OrdinalIgnoreCase);
                var cod4xPluginSourceEnabled = isCod4xServer && ResolveCod4xPluginSourceEnabled(configs, globalCod4xPluginSourceEnabled);
                var agentNamePrefix = globalAgentNamePrefix;
                if (!string.IsNullOrWhiteSpace(agentSettings.AgentName))
                {
                    agentNamePrefix = agentSettings.AgentName;
                }

                var banFileCheckIntervalSeconds =
                    banFileSettings?.CheckIntervalSeconds is > 0
                        ? Math.Clamp(
                            banFileSettings.CheckIntervalSeconds.Value,
                            ServerContext.MinBanFileCheckIntervalSeconds,
                            ServerContext.MaxBanFileCheckIntervalSeconds)
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
                configHashInputs[$"{transportNamespace}.authenticationType"] = sftpAuthenticationType.ToString();
                if (!string.IsNullOrWhiteSpace(sftpPrivateKey))
                {
                    configHashInputs[$"{transportNamespace}.privateKey"] = sftpPrivateKey;
                }
                if (!string.IsNullOrWhiteSpace(sftpPrivateKeyPassphrase))
                {
                    configHashInputs[$"{transportNamespace}.privateKeyPassphrase"] = sftpPrivateKeyPassphrase;
                }
                if (!string.IsNullOrWhiteSpace(sftpHostKeyFingerprint))
                {
                    configHashInputs[$"{transportNamespace}.hostKeyFingerprint"] = sftpHostKeyFingerprint;
                }
                configHashInputs["agent.agentNamePrefix"] = agentNamePrefix;
                if (isCod4xServer)
                {
                    configHashInputs["settings.cod4xPluginSourceEnabled"] = cod4xPluginSourceEnabled.ToString();
                }
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
                    FileTransportAuthenticationType = sftpAuthenticationType,
                    FileTransportPrivateKey = sftpPrivateKey,
                    FileTransportPrivateKeyPassphrase = sftpPrivateKeyPassphrase,
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
                    IsCod4xPluginSourceEnabled = cod4xPluginSourceEnabled,
                    Broadcasts = broadcasts,
                    ConfigHash = configHash
                });
            }

            _logger.LogInformation("Loaded {Count} agent-enabled servers from Repository API", servers.Count);
            Inventory = new(DateTimeOffset.UtcNow, inventory.Select(server =>
                new ExpectedAgent(server.GameServerId, server.AgentEnabled, server.BanFileSyncEnabled)).ToArray());
            if (globalConfigurations is not null)
            {
                Volatile.Write(ref _commandSnapshots, new CommandSnapshotBatch(
                    startedAt, GetNamespace(globalConfigurations, Cod4xCommandSettingsConstants.Namespace), commandSnapshots));
            }
            return servers;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error fetching servers from Repository API");
            throw;
        }
    }

    private async Task<IReadOnlyList<ConfigurationDto>?> FetchGlobalConfigurationsAsync(CancellationToken ct)
    {
        try
        {
            var result = await _repositoryClient.GlobalConfigurations.V1.GetConfigurations(ct);
            if (!result.IsSuccess || result.Result?.Data?.Items is null)
            {
                _logger.LogWarning("Failed to fetch global configurations, using defaults");
                return null;
            }

            return result.Result.Data.Items.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch global configurations, using defaults");
            return null;
        }
    }

    private static string ResolveGlobalAgentNamePrefix(IReadOnlyList<ConfigurationDto> configurations)
    {
        var agentConfig = configurations
            .FirstOrDefault(x => string.Equals(x.Namespace, AgentSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));

        if (agentConfig is not null && !string.IsNullOrWhiteSpace(agentConfig.Configuration))
        {
            var parsed = ParseAgentSettings(ParseNamespaceProperties(agentConfig.Configuration));
            if (!string.IsNullOrWhiteSpace(parsed?.AgentName))
            {
                return parsed.AgentName;
            }
        }
        return ServerContext.DefaultAgentNamePrefix;
    }

    private static bool ResolveGlobalCod4xPluginEnabled(IReadOnlyList<ConfigurationDto> configurations)
    {
        var pluginConfig = configurations
            .FirstOrDefault(x => string.Equals(x.Namespace, Cod4xPluginSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase));

        if (pluginConfig is not null && !string.IsNullOrWhiteSpace(pluginConfig.Configuration))
        {
            var parsed = ParseCod4xPluginSettings(ParseNamespaceProperties(pluginConfig.Configuration));
            if (parsed?.Enabled is bool enabled)
            {
                return enabled;
            }
        }
        return false;
    }

    private static TransportTypeResolution ResolveTransportType(GameServerDto dto)
    {
        var value = dto.FileTransportType;
        if (value == RepositoryFileTransportType.Unknown)
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
    /// </summary>
    private async Task<(Dictionary<string, Dictionary<string, JsonElement>> Configs,
        ConfigurationSnapshotValue Commands, ConfigurationSnapshotValue Broadcasts)> FetchConfigurationsAsync(
        Guid serverId, string title, CancellationToken ct)
    {
        try
        {
            var configResult = await _repositoryClient.GameServerConfigurations.V1
                .GetConfigurations(serverId, ct);

            if (!configResult.IsSuccess || configResult.Result?.Data?.Items is null)
            {
                throw new HttpRequestException("Failed to retrieve server configuration.");
            }

            var items = configResult.Result.Data.Items.ToArray();
            return (ParseConfigurations(items), GetNamespace(items, Cod4xCommandSettingsConstants.Namespace),
                GetNamespace(items, BroadcastSettingsConstants.Namespace));
        }

        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch configurations for server {ServerId} ({Title})",
                serverId, title);
            throw;
        }
    }

    private static ConfigurationSnapshotValue GetNamespace(IEnumerable<ConfigurationDto> configurations, string namespaceName)
    {
        var config = configurations.FirstOrDefault(x =>
            string.Equals(x.Namespace, namespaceName, StringComparison.OrdinalIgnoreCase));
        return config is null ? new(false, null) : new(true, config.Configuration);
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
            if (string.IsNullOrWhiteSpace(config.Namespace) || string.IsNullOrWhiteSpace(config.Configuration)
                || string.Equals(config.Namespace, BroadcastSettingsConstants.Namespace, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out value))
            {
                return true;
            }
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

        var document = DeserializeDocument<AgentSettingsDocument>(namespaceConfig)
            ?? TryBuildAgentSettingsFallback(namespaceConfig);

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

    private static Cod4xPluginSettingsDocument? ParseCod4xPluginSettings(
        Dictionary<string, JsonElement>? namespaceConfig)
    {
        if (namespaceConfig is null)
        {
            return null;
        }

        var document = DeserializeDocument<Cod4xPluginSettingsDocument>(namespaceConfig);
        if (document is null)
        {
            return null;
        }

        if (!SchemaVersionSupport.IsSupported(document.SchemaVersion))
        {
            return null;
        }

        var validation = new Cod4xPluginSettingsValidator().Validate(document);
        if (!validation.IsValid)
        {
            return null;
        }

        return document;
    }

    private static bool ResolveCod4xPluginSourceEnabled(
        Dictionary<string, Dictionary<string, JsonElement>> configs,
        bool globalCod4xPluginSourceEnabled)
    {
        if (!configs.TryGetValue(Cod4xPluginSettingsConstants.Namespace, out var namespaceConfig))
        {
            return globalCod4xPluginSourceEnabled;
        }

        var document = ParseCod4xPluginSettings(namespaceConfig);
        if (document?.Enabled is bool enabled)
        {
            return enabled;
        }

        return globalCod4xPluginSourceEnabled;
    }

    private BroadcastSettingsDocument ParseGlobalBroadcastSettings(IReadOnlyList<ConfigurationDto> configurations)
    {
        var document = GetNamespace(configurations, BroadcastSettingsConstants.Namespace);
        if (document.Exists && ParseBroadcastProperties(document) is { } config
            && HasSupportedBroadcastSchema(config)
            && TryGetBroadcastMessagesMode(config, out var mode)
            && HasValidExplicitBroadcastMessages(config))
        {
            return ParseBroadcastSettings(config, mode);
        }

        if (document.Exists)
        {
            _logger.LogWarning("Global broadcast configuration is malformed or has an unsupported schema; broadcasts default to off");
        }
        return DisabledBroadcastDocument();
    }

    private BroadcastSettings ResolveBroadcastSettings(
        ConfigurationSnapshotValue document, BroadcastSettingsDocument? global, Guid serverId)
    {
        BroadcastSettingsDocument? server = null;
        if (document.Exists)
        {
            var config = ParseBroadcastProperties(document);
            if (config is null || !HasSupportedBroadcastSchema(config))
            {
                _logger.LogWarning("Broadcast configuration for server {ServerId} is malformed or has an unsupported schema; broadcasts are disabled", serverId);
                server = DisabledBroadcastDocument();
            }
            else if (!TryGetBroadcastMessagesMode(config, out var mode)
                || !HasValidExplicitBroadcastMessages(config))
            {
                _logger.LogWarning("Broadcast configuration for server {ServerId} has an invalid messagesMode or messages list; broadcasts are disabled", serverId);
                server = DisabledBroadcastDocument();
            }
            else
            {
                server = ParseBroadcastSettings(config, mode);
            }
        }

        if (global is null && (server is null || server.Enabled is null || server.IntervalSeconds is null
            || server.MessagesMode == BroadcastMessagesMode.Inherit))
        {
            throw new InvalidOperationException("Global broadcast configuration is unavailable for an inheriting server.");
        }
        var effective = BroadcastSettingsResolver.Resolve(global, server, BroadcastSettingsResolutionMode.GlobalInheritance);
        return new BroadcastSettings
        {
            Enabled = effective.Enabled,
            IntervalSeconds = effective.IntervalSeconds,
            Messages = effective.Messages.Select(message => new BroadcastMessage
            {
                Message = message.Message!,
                Enabled = message.Enabled
            }).ToArray()
        };
    }

    private static BroadcastSettingsDocument DisabledBroadcastDocument() => new()
    {
        Enabled = false,
        IntervalSeconds = BroadcastSettingsConstants.DefaultIntervalSeconds
    };

    private static Dictionary<string, JsonElement>? ParseBroadcastProperties(ConfigurationSnapshotValue document)
    {
        if (ParseNamespaceProperties(document.Configuration) is not { } properties)
        {
            return null;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in properties)
        {
            if (!result.TryAdd(key, value))
            {
                return null;
            }
        }
        return result;
    }

    private static bool IsMissingOrNull(Dictionary<string, JsonElement> config, string key) =>
        !config.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null;

    private static bool TryGetBroadcastMessagesMode(Dictionary<string, JsonElement> config, out BroadcastMessagesMode? mode)
    {
        mode = null;
        if (!config.TryGetValue("messagesMode", out var value))
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        mode = value.GetString() switch
        {
            "Inherit" => BroadcastMessagesMode.Inherit,
            "Replace" => BroadcastMessagesMode.Replace,
            _ => null
        };
        return mode is not null;
    }

    private static bool HasValidExplicitBroadcastMessages(Dictionary<string, JsonElement> config)
    {
        if (!config.ContainsKey("messagesMode"))
        {
            return true;
        }

        if (config.TryGetValue("messages", out var messages)
            && messages.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (messages.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in messages.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("message", out var textElement)
                || textElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(textElement.GetString()))
            {
                return false;
            }
            if (!item.TryGetProperty("id", out var idElement))
            {
                continue;
            }

            var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)
                || id.Length > BroadcastSettingsConstants.MaximumMessageIdLength
                || !string.Equals(id, id.Trim(), StringComparison.Ordinal)
                || !ids.Add(id))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSupportedBroadcastSchema(Dictionary<string, JsonElement> config) =>
        !config.ContainsKey("schemaVersion")
        || TryGetIntValue(config, "schemaVersion", out var version) && SchemaVersionSupport.IsSupported(version);

    private static BroadcastSettingsDocument ParseBroadcastSettings(
        Dictionary<string, JsonElement> namespaceConfig, BroadcastMessagesMode? messagesMode)
    {
        bool? enabled = IsMissingOrNull(namespaceConfig, "enabled")
            ? null
            : TryGetBoolValue(namespaceConfig, "enabled", out var configuredEnabled) && configuredEnabled;

        int? intervalSeconds = IsMissingOrNull(namespaceConfig, "intervalSeconds")
            ? null
            : BroadcastSettingsConstants.DefaultIntervalSeconds;
        if (TryGetIntValue(namespaceConfig, "intervalSeconds", out var configuredInterval) && configuredInterval > 0)
        {
            intervalSeconds = configuredInterval;
        }

        var messages = new List<BroadcastSettingsMessage?>();
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
                    else if (enabledElement.ValueKind == JsonValueKind.String &&
                             bool.TryParse(enabledElement.GetString(), out var parsedEnabled))
                    {
                        messageEnabled = parsedEnabled;
                    }
                    else
                    {
                        continue;
                    }
                }

                messages.Add(new BroadcastSettingsMessage
                {
                    Id = messagesMode is not null && messageElement.TryGetProperty("id", out var id)
                        ? id.GetString()
                        : null,
                    Message = messageText,
                    Enabled = messageEnabled
                });
            }
        }

        return new BroadcastSettingsDocument
        {
            Enabled = enabled,
            IntervalSeconds = intervalSeconds,
            MessagesMode = messagesMode,
            Messages = messages
        };
    }

    private static T? DeserializeDocument<T>(Dictionary<string, JsonElement> namespaceConfig)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(namespaceConfig, JsonOptions);
            return element.Deserialize<T>(JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (NotSupportedException)
        {
            return default;
        }
    }

    private static AgentSettingsDocument? TryBuildAgentSettingsFallback(
        Dictionary<string, JsonElement> namespaceConfig)
    {
        if (!TryGetStringValue(namespaceConfig, "logFilePath", out var logFilePath))
        {
            return null;
        }

        var fallback = new AgentSettingsDocument
        {
            LogFilePath = logFilePath
        };

        if (TryGetIntValue(namespaceConfig, "schemaVersion", out var schemaVersion))
        {
            fallback.SchemaVersion = schemaVersion;
        }

        if (TryGetStringValue(namespaceConfig, "agentName", out var agentName))
        {
            fallback.AgentName = agentName;
        }

        return fallback;
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
