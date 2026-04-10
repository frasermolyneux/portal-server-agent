using System.Text.Json;

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

                if (!TryGetStringValue(configs, "ftp", "hostname", out var ftpHostname) ||
                    !TryGetStringValue(configs, "ftp", "username", out var ftpUsername) ||
                    !TryGetStringValue(configs, "ftp", "password", out var ftpPassword) ||
                    !TryGetIntValue(configs, "ftp", "port", out var ftpPort))
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — missing FTP configuration in config namespace",
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

                if (!TryGetStringValue(configs, "agent", "logFilePath", out var logFilePath))
                {
                    _logger.LogWarning(
                        "Skipping server {ServerId} ({Title}) — missing agent configuration in config namespace",
                        dto.GameServerId, dto.Title);
                    continue;
                }

                servers.Add(new ServerContext
                {
                    ServerId = dto.GameServerId,
                    GameType = dto.GameType.ToString(),
                    Title = dto.Title,
                    FtpHostname = ftpHostname,
                    FtpPort = ftpPort,
                    FtpUsername = ftpUsername,
                    FtpPassword = ftpPassword,
                    LogFilePath = logFilePath,
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
}
