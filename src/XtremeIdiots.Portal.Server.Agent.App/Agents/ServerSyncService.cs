using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
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
    private static readonly TimeSpan RosterCaptureTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ServerSyncService> _logger;

    public ServerSyncService(IServiceScopeFactory scopeFactory, ILogger<ServerSyncService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(Guid serverId, ILogParser parser, CancellationToken ct = default)
        => SyncAsync(serverId, parser, null, ct);

    public Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(Guid serverId, ILogParser parser, string? gameType, CancellationToken ct = default)
        => SyncAsync(serverId, parser, gameType, isCod4xPluginSourceEnabled: false, ct);

    public async Task<IReadOnlyList<PlayerIpResolvedEvent>> SyncAsync(
        Guid serverId,
        ILogParser parser,
        string? gameType,
        bool isCod4xPluginSourceEnabled,
        CancellationToken ct = default)
        => (await SyncWithRosterAsync(serverId, parser, gameType, isCod4xPluginSourceEnabled, ct).ConfigureAwait(false)).IpResolvedEvents;

    public Task<ServerSyncResult> SyncWithRosterAsync(
        Guid serverId,
        ILogParser parser,
        string? gameType,
        bool isCod4xPluginSourceEnabled,
        CancellationToken ct = default)
        => SyncCoreAsync(serverId, parser, gameType, isCod4xPluginSourceEnabled, null, ct);

    public Task<ServerSyncResult> SyncWithRosterCallbackAsync(
        Guid serverId,
        ILogParser parser,
        string? gameType,
        bool isCod4xPluginSourceEnabled,
        Func<ServerRosterSnapshot?, CancellationToken, Task<bool>> onRoster,
        CancellationToken ct = default)
        => SyncCoreAsync(serverId, parser, gameType, isCod4xPluginSourceEnabled,
            onRoster ?? throw new ArgumentNullException(nameof(onRoster)), ct);

    public async Task<bool> CaptureRosterAsync(
        Guid serverId,
        string gameType,
        Func<ServerRosterSnapshot?, CancellationToken, Task<bool>> onRoster,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(onRoster);
        ServerRosterSnapshot? roster = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<IServersApiClient>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RosterCaptureTimeout);
            var started = Stopwatch.GetTimestamp();
            var (_, _, observed) = await TryGetRconPlayersAsync(client, serverId, gameType, timeout.Token).ConfigureAwait(false);
            roster = observed is null ? null : observed with { CaptureDuration = Stopwatch.GetElapsedTime(started) };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playtime roster status failed for server {ServerId} game {GameType}", serverId, gameType);
        }

        var recorded = await NotifyRosterAsync(onRoster, roster, serverId, ct).ConfigureAwait(false);
        return recorded && roster is { State: RconRosterState.Complete or RconRosterState.Partial }
            && roster.CaptureDuration <= RosterCaptureTimeout;
    }

    private async Task<ServerSyncResult> SyncCoreAsync(
        Guid serverId,
        ILogParser parser,
        string? gameType,
        bool isCod4xPluginSourceEnabled,
        Func<ServerRosterSnapshot?, CancellationToken, Task<bool>>? onRoster,
        CancellationToken ct)
    {
        var ipResolvedEvents = new List<PlayerIpResolvedEvent>();
        ServerRosterSnapshot? roster = null;
        var rosterNotified = false;
        var rosterRecorded = false;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var serversApiClient = scope.ServiceProvider.GetRequiredService<IServersApiClient>();

            var started = Stopwatch.GetTimestamp();
            var (isSupportedGameType, rconPlayers, observedRoster) = await TryGetRconPlayersAsync(serversApiClient, serverId, gameType, ct).ConfigureAwait(false);
            roster = observedRoster is null ? null : observedRoster with { CaptureDuration = Stopwatch.GetElapsedTime(started) };

            if (onRoster is not null)
            {
                rosterNotified = true;
                rosterRecorded = await NotifyRosterAsync(onRoster, roster, serverId, ct).ConfigureAwait(false);
            }

            if (!isSupportedGameType)
            {
                _logger.LogDebug(
                    "Skipping RCON player sync for server {ServerId} because game type {GameType} is not supported",
                    serverId,
                    gameType);

                await SyncQueryAsync(scope, serverId, parser, ct).ConfigureAwait(false);
                return new ServerSyncResult(ipResolvedEvents, roster, rosterRecorded);
            }

            if (rconPlayers is null)
            {
                await SyncQueryAsync(scope, serverId, parser, ct).ConfigureAwait(false);

                // The CoD4x reconciles issue their own independent RCON commands (dumpbanlist,
                // admin/command-power) and do not depend on the player-status result. Run them even
                // when the status fetch failed or returned no players, so a flaky status call never
                // starves server -> portal ban import.
                await RunCoD4xReconciliationAsync(scope, serverId, gameType, isCod4xPluginSourceEnabled, ct).ConfigureAwait(false);

                return new ServerSyncResult(ipResolvedEvents, roster, rosterRecorded);
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
            await SyncQueryAsync(scope, serverId, parser, ct).ConfigureAwait(false);

            await RunCoD4xReconciliationAsync(scope, serverId, gameType, isCod4xPluginSourceEnabled, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RCON sync failed for server {ServerId}", serverId);
            if (onRoster is not null && !rosterNotified)
            {
                await NotifyRosterAsync(onRoster, null, serverId, ct).ConfigureAwait(false);
            }
        }

        return new ServerSyncResult(ipResolvedEvents, roster, rosterRecorded);
    }

    private async Task<bool> NotifyRosterAsync(
        Func<ServerRosterSnapshot?, CancellationToken, Task<bool>> onRoster,
        ServerRosterSnapshot? roster,
        Guid serverId,
        CancellationToken ct)
    {
        try
        {
            return await onRoster(roster, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Playtime roster recording failed for server {ServerId}; legacy synchronization continues", serverId);
            return false;
        }
    }

    /// <summary>
    /// Runs the CoD4x ban, admin, and command-power reconciles. These issue their own independent
    /// RCON commands (e.g. <c>dumpbanlist</c>) and do not depend on the player-status sync, so they
    /// are invoked on every sync path — including when the status fetch failed or returned no
    /// players — to guarantee server-side bans are imported into the portal. No-op for non-CoD4x
    /// game types.
    /// </summary>
    private async Task RunCoD4xReconciliationAsync(
        IServiceScope scope,
        Guid serverId,
        string? gameType,
        bool isCod4xPluginSourceEnabled,
        CancellationToken ct)
    {
        if (!IsCoD4xGameType(gameType))
        {
            return;
        }

        var reconciliationService = scope.ServiceProvider.GetService<ICoD4xBanReconciliationService>();
        if (reconciliationService is not null)
        {
            await reconciliationService
                .ReconcileAsync(serverId, gameType, isCod4xPluginSourceEnabled, ct)
                .ConfigureAwait(false);
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

    private async Task<(bool IsSupportedGameType, IReadOnlyList<RconPlayerSnapshot>? Players, ServerRosterSnapshot? Roster)> TryGetRconPlayersAsync(
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
                return (true, null, null);
            }

            var players = result.Result.Data.Players.Select(player => new RconPlayerSnapshot(
                player.Num,
                player.PlayerIdentifier,
                player.Name,
                player.IpAddress,
                player.Ping ?? 0,
                player.Rate)).ToList();
            return (true, players, CreateRosterSnapshot(serverId, result.Result.Data.Roster, players));
        }

        if (string.Equals(gameType, CallOfDuty2GameType, StringComparison.OrdinalIgnoreCase))
        {
            var result = await serversApiClient.Cod2Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Players is null)
            {
                _logger.LogWarning("RCON sync failed for server {ServerId}: CoD2 status returned non-success or no data", serverId);
                return (true, null, null);
            }

            var players = result.Result.Data.Players.Select(player => new RconPlayerSnapshot(
                player.Num,
                player.Guid,
                player.Name,
                player.IpAddress,
                player.Ping,
                player.Rate)).ToList();
            return (true, players, CreateRosterSnapshot(serverId, result.Result.Data.Roster, players));
        }

        if (string.Equals(gameType, CallOfDuty4GameType, StringComparison.OrdinalIgnoreCase))
        {
            var result = await serversApiClient.Cod4Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Players is null)
            {
                _logger.LogWarning("RCON sync failed for server {ServerId}: CoD4 status returned non-success or no data", serverId);
                return (true, null, null);
            }

            var players = result.Result.Data.Players.Select(player => new RconPlayerSnapshot(
                player.Num,
                player.Guid,
                player.Name,
                player.IpAddress,
                player.Ping,
                player.Rate)).ToList();
            return (true, players, CreateRosterSnapshot(serverId, result.Result.Data.Roster, players));
        }

        if (string.Equals(gameType, CallOfDuty5GameType, StringComparison.OrdinalIgnoreCase))
        {
            var result = await serversApiClient.Cod5Rcon.V1.Status(serverId, ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Players is null)
            {
                _logger.LogWarning("RCON sync failed for server {ServerId}: CoD5 status returned non-success or no data", serverId);
                return (true, null, null);
            }

            var players = result.Result.Data.Players.Select(player => new RconPlayerSnapshot(
                player.Num,
                player.Guid,
                player.Name,
                player.IpAddress,
                player.Ping,
                player.Rate)).ToList();
            return (true, players, CreateRosterSnapshot(serverId, result.Result.Data.Roster, players));
        }

        return (false, null, null);
    }

    private ServerRosterSnapshot? CreateRosterSnapshot(
        Guid serverId,
        RconRosterObservationDto? roster,
        IReadOnlyList<RconPlayerSnapshot> players)
    {
        if (roster is null)
        {
            return null;
        }

        if (!Enum.IsDefined(roster.State) || roster.Players is null || roster.UnparsedRowCount < 0 ||
            roster.MissingIdentityCount < 0 || roster.KnownBotCount < 0 ||
            roster.Players.Any(p => p is null || string.IsNullOrWhiteSpace(p.PlayerIdentifier) || p.Slot < 0))
        {
            _logger.LogWarning("RCON roster for server {ServerId} is invalid; ignoring roster metadata", serverId);
            return null;
        }

        var observedPlayers = (roster.State is RconRosterState.Partial or RconRosterState.Complete
            ? roster.Players
            : []).Select(player => new ServerRosterPlayer(
            player.Slot,
            player.PlayerIdentifier,
            players.FirstOrDefault(p => p.Num == player.Slot &&
                string.Equals(p.Guid, player.PlayerIdentifier, StringComparison.OrdinalIgnoreCase))?.Name,
            null)).ToArray();

        return new ServerRosterSnapshot(DateTime.UtcNow, roster.State, observedPlayers,
            roster.UnparsedRowCount, roster.MissingIdentityCount, roster.KnownBotCount);
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
