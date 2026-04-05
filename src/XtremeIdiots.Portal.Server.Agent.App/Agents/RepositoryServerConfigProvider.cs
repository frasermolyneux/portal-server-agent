using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Fetches agent-enabled game servers from the Portal Repository API.
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
                if (string.IsNullOrWhiteSpace(dto.FtpHostname) ||
                    string.IsNullOrWhiteSpace(dto.FtpUsername) ||
                    string.IsNullOrWhiteSpace(dto.FtpPassword) ||
                    dto.FtpPort is null)
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
                    FtpHostname = dto.FtpHostname,
                    FtpPort = dto.FtpPort.Value,
                    FtpUsername = dto.FtpUsername,
                    FtpPassword = dto.FtpPassword,
                    LiveLogFile = dto.LiveLogFile,
                    Hostname = dto.Hostname,
                    QueryPort = dto.QueryPort,
                    RconPassword = dto.RconPassword
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
}
