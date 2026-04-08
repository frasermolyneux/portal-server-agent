using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Queries game servers via the Servers Integration API (RCON and Query) to populate/reconcile
/// the parser's player slot map and server metadata. Creates a DI scope per call to resolve
/// the scoped API clients.
/// </summary>
public sealed class ServerSyncService : IServerSyncService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServerSyncService> _logger;

    public ServerSyncService(IServiceScopeFactory scopeFactory, ILogger<ServerSyncService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SyncAsync(Guid serverId, ILogParser parser, CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var rconApi = scope.ServiceProvider.GetRequiredService<IRconApi>();

            var result = await rconApi.GetServerStatus(serverId);

            if (!result.IsSuccess || result.Result?.Data?.Players is null)
            {
                _logger.LogWarning("RCON sync failed for server {ServerId}: API returned non-success or no data", serverId);
                return;
            }

            var rconPlayers = result.Result.Data.Players;
            var existingPlayers = parser.ConnectedPlayers;
            var added = 0;
            var updated = 0;

            foreach (var rconPlayer in rconPlayers)
            {
                var slotId = rconPlayer.Num;

                if (existingPlayers.TryGetValue(slotId, out var existing))
                {
                    // Update mutable RCON fields on existing player
                    existing.Ping = rconPlayer.Ping;
                    existing.Rate = rconPlayer.Rate;
                    if (!string.IsNullOrWhiteSpace(rconPlayer.IpAddress))
                        existing.IpAddress = rconPlayer.IpAddress;
                    updated++;
                }
                else
                {
                    parser.SetPlayer(slotId, new PlayerInfo
                    {
                        Guid = rconPlayer.Guid ?? string.Empty,
                        Name = rconPlayer.Name ?? string.Empty,
                        SlotId = slotId,
                        IpAddress = rconPlayer.IpAddress,
                        ConnectedAt = DateTime.UtcNow,
                        Ping = rconPlayer.Ping,
                        Rate = rconPlayer.Rate
                    });
                    added++;
                }
            }

            // Remove players from slot map that RCON says are gone
            var rconSlots = rconPlayers.Select(p => p.Num).ToHashSet();
            var staleSlots = existingPlayers.Keys.Where(k => !rconSlots.Contains(k)).ToList();
            foreach (var staleSlot in staleSlots)
            {
                parser.RemovePlayer(staleSlot);
            }

            _logger.LogInformation("RCON sync for {ServerId}: added {Added}, updated {Updated}, removed {Removed} players",
                serverId, added, updated, staleSlots.Count);

            // Query protocol for server metadata and player scores
            await SyncQueryAsync(scope, serverId, parser, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RCON sync failed for server {ServerId}", serverId);
        }
    }

    private async Task SyncQueryAsync(IServiceScope scope, Guid serverId, ILogParser parser, CancellationToken ct)
    {
        try
        {
            var queryApi = scope.ServiceProvider.GetRequiredService<IQueryApi>();
            var queryResult = await queryApi.GetServerStatus(serverId);

            if (!queryResult.IsSuccess || queryResult.Result?.Data is null)
            {
                _logger.LogDebug("Query sync skipped for server {ServerId}: API returned non-success or no data", serverId);
                return;
            }

            var query = queryResult.Result.Data;
            parser.SetServerInfo(query.ServerName, query.Mod, query.MaxPlayers);
            parser.SetCurrentMap(query.Map);

            // Merge Score from Query players (Query has Score, RCON doesn't)
            foreach (var queryPlayer in query.Players)
            {
                if (queryPlayer.Name is null) continue;

                // Match by name (Query doesn't have GUID)
                var slotEntry = parser.ConnectedPlayers.Values
                    .FirstOrDefault(p => p.Name == queryPlayer.Name);
                if (slotEntry is not null)
                {
                    slotEntry.Score = queryPlayer.Score;
                }
            }

            _logger.LogDebug("Query sync for {ServerId}: ServerName={ServerName}, Mod={Mod}, MaxPlayers={MaxPlayers}",
                serverId, query.ServerName, query.Mod, query.MaxPlayers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Query sync failed for server {ServerId}", serverId);
        }
    }
}
