using System.Text.Json;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Configurations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Fetches agent-enabled game servers from the Portal Repository API.
/// Reads configuration from the new per-server config system with fallback to legacy DTO columns.
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

                var ftpHostname = ResolveString(configs, "ftp", "hostname", dto.FtpHostname, dto.Title, "FtpHostname");
                var ftpUsername = ResolveString(configs, "ftp", "username", dto.FtpUsername, dto.Title, "FtpUsername");
                var ftpPassword = ResolveString(configs, "ftp", "password", dto.FtpPassword, dto.Title, "FtpPassword");
                var ftpPort = ResolveInt(configs, "ftp", "port", dto.FtpPort, dto.Title, "FtpPort");
                var rconPassword = ResolveString(configs, "rcon", "password", dto.RconPassword, dto.Title, "RconPassword");
                var logFilePath = ResolveString(configs, "agent", "logFilePath", dto.LiveLogFile, dto.Title, "LiveLogFile");

                if (string.IsNullOrWhiteSpace(ftpHostname) ||
                    string.IsNullOrWhiteSpace(ftpUsername) ||
                    string.IsNullOrWhiteSpace(ftpPassword) ||
                    ftpPort is null)
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — missing FTP configuration",
                        dto.GameServerId, dto.Title);
                    continue;
                }

                servers.Add(new ServerContext
                {
                    ServerId = dto.GameServerId,
                    GameType = dto.GameType.ToString(),
                    Title = dto.Title,
                    FtpHostname = ftpHostname,
                    FtpPort = ftpPort.Value,
                    FtpUsername = ftpUsername,
                    FtpPassword = ftpPassword,
                    LiveLogFile = logFilePath,
                    Hostname = dto.Hostname,
                    QueryPort = dto.QueryPort,
                    RconPassword = rconPassword,
                    FtpEnabled = dto.FtpEnabled,
                    RconEnabled = dto.RconEnabled,
                    BanFileSyncEnabled = dto.BanFileSyncEnabled
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

    /// <summary>
    /// Fetches all configuration namespaces for a server, returning a case-insensitive lookup.
    /// Returns an empty dictionary on failure so fallback values are used.
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
                _logger.LogDebug("No configurations found for server {ServerId} ({Title}), using fallback values",
                    serverId, title);
                return new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);
            }

            return ParseConfigurations(configResult.Result.Data.Items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch configurations for server {ServerId} ({Title}), using fallback values",
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
                // Malformed JSON — skip this namespace, fallback values will be used
            }
        }

        return result;
    }

    private string? ResolveString(
        Dictionary<string, Dictionary<string, JsonElement>> configs,
        string ns, string key, string? fallback, string serverTitle, string fieldName)
    {
        if (configs.TryGetValue(ns, out var nsConfig) &&
            nsConfig.TryGetValue(key, out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                _logger.LogDebug("[{Title}] Using config value for {Field} from '{Namespace}.{Key}'",
                    serverTitle, fieldName, ns, key);
                return value;
            }
        }

        _logger.LogDebug("[{Title}] Using fallback value for {Field} (no '{Namespace}.{Key}' config)",
            serverTitle, fieldName, ns, key);
        return fallback;
    }

    private int? ResolveInt(
        Dictionary<string, Dictionary<string, JsonElement>> configs,
        string ns, string key, int? fallback, string serverTitle, string fieldName)
    {
        if (configs.TryGetValue(ns, out var nsConfig) &&
            nsConfig.TryGetValue(key, out var element))
        {
            int? value = element.ValueKind switch
            {
                JsonValueKind.Number => element.TryGetInt32(out var i) ? i : null,
                JsonValueKind.String => int.TryParse(element.GetString(), out var i) ? i : null,
                _ => null
            };

            if (value.HasValue)
            {
                _logger.LogDebug("[{Title}] Using config value for {Field} from '{Namespace}.{Key}'",
                    serverTitle, fieldName, ns, key);
                return value;
            }
        }

        _logger.LogDebug("[{Title}] Using fallback value for {Field} (no '{Namespace}.{Key}' config)",
            serverTitle, fieldName, ns, key);
        return fallback;
    }
}
