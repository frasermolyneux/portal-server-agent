using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Queries game servers via the Servers Integration API (RCON and Query) to populate/reconcile
/// the parser's player slot map and server metadata. Creates a DI scope per call to resolve
/// the scoped API clients.
/// </summary>
public sealed class ServerSyncService : IServerSyncService
{
    private const string CallOfDuty2GameType = "CallOfDuty2";
    private const string CallOfDuty4GameType = "CallOfDuty4";
    private const string CallOfDuty5GameType = "CallOfDuty5";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServerSyncService> _logger;

    public ServerSyncService(IServiceScopeFactory scopeFactory, ILogger<ServerSyncService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(Guid serverId, ILogParser parser, CancellationToken ct = default)
        => SyncAsync(serverId, parser, null, ct);

    public async Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(Guid serverId, ILogParser parser, string? gameType, CancellationToken ct = default)
    {
        var ipResolvedEvents = new List<PlayerIpResolvedEvent>();

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var serversApiClient = scope.ServiceProvider.GetRequiredService<IServersApiClient>();

            var (isSupportedGameType, rconPlayers) = await TryGetRconPlayersAsync(serversApiClient, serverId, gameType, ct).ConfigureAwait(false);

            if (!isSupportedGameType)
            {
                _logger.LogDebug(
                    "Skipping RCON player sync for server {ServerId} because game type {GameType} is not supported",
                    serverId,
                    gameType);

                await SyncQueryAsync(scope, serverId, parser, ct).ConfigureAwait(false);
                return ipResolvedEvents;
            }

            if (rconPlayers is null)
            {
                await SyncQueryAsync(scope, serverId, parser, ct).ConfigureAwait(false);
                return ipResolvedEvents;
            }

            var now = DateTime.UtcNow;
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

                    // Emit event when RCON provides an IP that differs from what we had
                    // (includes null→value first discovery and value→value IP changes)
                    if (!string.IsNullOrWhiteSpace(rconPlayer.IpAddress) &&
                        existing.IpAddress != rconPlayer.IpAddress)
                    {
                        existing.IpAddress = rconPlayer.IpAddress;

                        var resolvedPlayerGuid = string.IsNullOrWhiteSpace(existing.Guid)
                            ? rconPlayer.Guid
                            : existing.Guid;

                        if (!string.IsNullOrWhiteSpace(resolvedPlayerGuid))
                        {
                            ipResolvedEvents.Add(new PlayerIpResolvedEvent
                            {
                                Timestamp = now,
                                PlayerGuid = resolvedPlayerGuid,
                                IpAddress = rconPlayer.IpAddress
                            });
                        }
                    }

                    updated++;
                }
                else
                {
                    parser.SetPlayer(slotId, new PlayerInfo
                    {
                        Guid = rconPlayer.Guid,
                        Name = rconPlayer.Name,
                        SlotId = slotId,
                        IpAddress = rconPlayer.IpAddress,
                        ConnectedAt = now,
                        Ping = rconPlayer.Ping,
                        Rate = rconPlayer.Rate
                    });
                    added++;

                    // New player discovered via RCON with IP — emit resolved event
                    if (!string.IsNullOrWhiteSpace(rconPlayer.IpAddress) &&
                        !string.IsNullOrWhiteSpace(rconPlayer.Guid))
                    {
                        ipResolvedEvents.Add(new PlayerIpResolvedEvent
                        {
                            Timestamp = now,
                            PlayerGuid = rconPlayer.Guid,
                            IpAddress = rconPlayer.IpAddress
                        });
                    }
                }
            }

            // Remove players from slot map that RCON says are gone
            var rconSlots = rconPlayers.Select(p => p.Num).ToHashSet();
            var staleSlots = existingPlayers.Keys.Where(k => !rconSlots.Contains(k)).ToList();
            foreach (var staleSlot in staleSlots)
            {
                parser.RemovePlayer(staleSlot);
            }

            _logger.LogInformation("RCON sync for {ServerId}: added {Added}, updated {Updated}, removed {Removed}, ip-resolved {IpResolved} players",
                serverId, added, updated, staleSlots.Count, ipResolvedEvents.Count);

            // Query protocol for server metadata and player scores
            await SyncQueryAsync(scope, serverId, parser, ct);

            if (IsCoD4xGameType(gameType))
            {
                var reconciliationService = scope.ServiceProvider.GetService<ICoD4xBanReconciliationService>();
                if (reconciliationService is not null)
                {
                    await reconciliationService.ReconcileAsync(serverId, gameType, ct).ConfigureAwait(false);
                }

                var adminReconciliationService = scope.ServiceProvider.GetService<ICoD4xAdminReconciliationService>();
                if (adminReconciliationService is not null)
                {
                    await adminReconciliationService.ReconcileAsync(serverId, gameType, ct).ConfigureAwait(false);
                }

                var commandPowerReconciliationService = scope.ServiceProvider.GetService<ICoD4xCommandPowerReconciliationService>();
                if (commandPowerReconciliationService is not null)
                {
                    await commandPowerReconciliationService.ReconcileAsync(serverId, gameType, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RCON sync failed for server {ServerId}", serverId);
        }

        return ipResolvedEvents;
    }

    private async Task<(bool IsSupportedGameType, IReadOnlyList<RconPlayerSnapshot>? Players)> TryGetRconPlayersAsync(
        IServersApiClient serversApiClient,
        Guid serverId,
        string? gameType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gameType) || IsCoD4xGameType(gameType))
        {
            var result = await serversApiClient.CoD4xRcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Players is null)
            {
                _logger.LogWarning("RCON sync failed for server {ServerId}: CoD4x status returned non-success or no data", serverId);
                return (true, null);
            }

            return (true, result.Result.Data.Players.Select(player => new RconPlayerSnapshot(
                player.Num,
                player.PlayerIdentifier,
                player.Name,
                player.IpAddress,
                player.Ping ?? 0,
                player.Rate)).ToList());
        }

        if (string.Equals(gameType, CallOfDuty2GameType, StringComparison.OrdinalIgnoreCase))
        {
            var result = await serversApiClient.Cod2Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Players is null)
            {
                _logger.LogWarning("RCON sync failed for server {ServerId}: CoD2 status returned non-success or no data", serverId);
                return (true, null);
            }

            return (true, result.Result.Data.Players.Select(player => new RconPlayerSnapshot(
                player.Num,
                player.Guid,
                player.Name,
                player.IpAddress,
                player.Ping,
                player.Rate)).ToList());
        }

        if (string.Equals(gameType, CallOfDuty4GameType, StringComparison.OrdinalIgnoreCase))
        {
            var result = await serversApiClient.Cod4Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Players is null)
            {
                _logger.LogWarning("RCON sync failed for server {ServerId}: CoD4 status returned non-success or no data", serverId);
                return (true, null);
            }

            return (true, result.Result.Data.Players.Select(player => new RconPlayerSnapshot(
                player.Num,
                player.Guid,
                player.Name,
                player.IpAddress,
                player.Ping,
                player.Rate)).ToList());
        }

        if (string.Equals(gameType, CallOfDuty5GameType, StringComparison.OrdinalIgnoreCase))
        {
            var result = await serversApiClient.Cod5Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Players is null)
            {
                _logger.LogWarning("RCON sync failed for server {ServerId}: CoD5 status returned non-success or no data", serverId);
                return (true, null);
            }

            return (true, result.Result.Data.Players.Select(player => new RconPlayerSnapshot(
                player.Num,
                player.Guid,
                player.Name,
                player.IpAddress,
                player.Ping,
                player.Rate)).ToList());
        }

        return (false, null);
    }

    private static bool IsCoD4xGameType(string? gameType)
        => string.Equals(gameType, Cod4xCvarProbe.Cod4xGameType, StringComparison.OrdinalIgnoreCase);

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
                if (queryPlayer.Name is null)
                {
                    continue;
                }

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

    private sealed record RconPlayerSnapshot(
        int Num,
        string Guid,
        string Name,
        string IpAddress,
        int Ping,
        int Rate);
}
