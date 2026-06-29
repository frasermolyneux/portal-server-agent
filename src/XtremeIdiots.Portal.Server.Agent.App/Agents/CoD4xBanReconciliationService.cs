using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public sealed class CoD4xBanReconciliationService(
    IRepositoryApiClient repositoryApiClient,
    ICoD4xRconApi coD4xRconApi,
    ILogger<CoD4xBanReconciliationService> logger) : ICoD4xBanReconciliationService
{
    private const int ActiveBanPageSize = 200;

    public async Task ReconcileAsync(Guid serverId, string? gameType, CancellationToken ct = default)
    {
        if (!IsCoD4x(gameType))
        {
            return;
        }

        try
        {
            var dumpBanListResult = await coD4xRconApi.DumpBanList(serverId, ct).ConfigureAwait(false);
            if (!dumpBanListResult.IsSuccess || dumpBanListResult.Result?.Data is null)
            {
                logger.LogWarning("CoD4x dumpbanlist failed for {ServerId}: status {StatusCode}", serverId, dumpBanListResult.StatusCode);
                return;
            }

            var serverBansByIdentifier = dumpBanListResult.Result.Data.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.PlayerIdentifier))
                .GroupBy(e => e.PlayerIdentifier.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var portalActiveBansByIdentifier = await GetPortalActiveBansByIdentifierAsync(ct).ConfigureAwait(false);

            await ImportServerOnlyBansAsync(serverId, serverBansByIdentifier, portalActiveBansByIdentifier, ct).ConfigureAwait(false);
            await ReapplyPortalOnlyBansAsync(serverId, serverBansByIdentifier, portalActiveBansByIdentifier, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CoD4x ban reconciliation failed for server {ServerId}", serverId);
        }
    }

    private static bool IsCoD4x(string? gameType)
        => string.Equals(gameType, nameof(GameType.CallOfDuty4x), StringComparison.OrdinalIgnoreCase);

    private async Task<Dictionary<string, PortalBanExpectation>> GetPortalActiveBansByIdentifierAsync(CancellationToken ct)
    {
        var expectations = new Dictionary<string, PortalBanExpectation>(StringComparer.OrdinalIgnoreCase);
        var skip = 0;

        while (true)
        {
            var activeBansResult = await repositoryApiClient.AdminActions.V1
                .GetAdminActions(
                    GameType.CallOfDuty4x,
                    null,
                    null,
                    AdminActionFilter.ActiveBans,
                    skip,
                    ActiveBanPageSize,
                    null,
                    ct)
                .ConfigureAwait(false);

            if (!activeBansResult.IsSuccess || activeBansResult.Result?.Data?.Items is null)
            {
                logger.LogWarning("Failed to fetch active CoD4x bans from repository while reconciling");
                break;
            }

            var pageItems = activeBansResult.Result.Data.Items;
            if (!pageItems.Any())
            {
                break;
            }

            foreach (var action in pageItems)
            {
                var playerIdentifier = action.Player?.Guid?.Trim();
                if (string.IsNullOrWhiteSpace(playerIdentifier))
                {
                    continue;
                }

                if (action.Type == AdminActionType.Ban)
                {
                    expectations[playerIdentifier] = PortalBanExpectation.Permanent(playerIdentifier);
                    continue;
                }

                if (action.Type != AdminActionType.TempBan || !action.Expires.HasValue)
                {
                    continue;
                }

                var remaining = action.Expires.Value - DateTime.UtcNow;
                var remainingMinutes = (int)Math.Ceiling(remaining.TotalMinutes);
                if (remainingMinutes <= 0)
                {
                    continue;
                }

                if (expectations.TryGetValue(playerIdentifier, out var existing) && !existing.IsTemporary)
                {
                    continue;
                }

                if (expectations.TryGetValue(playerIdentifier, out existing) && existing.IsTemporary)
                {
                    expectations[playerIdentifier] = PortalBanExpectation.Temporary(playerIdentifier, Math.Max(existing.DurationMinutes ?? 0, remainingMinutes));
                    continue;
                }

                expectations[playerIdentifier] = PortalBanExpectation.Temporary(playerIdentifier, remainingMinutes);
            }

            if (pageItems.Count() < ActiveBanPageSize)
            {
                break;
            }

            skip += pageItems.Count();
        }

        return expectations;
    }

    private async Task ImportServerOnlyBansAsync(
        Guid serverId,
        IReadOnlyDictionary<string, CoD4xBanEntryDto> serverBansByIdentifier,
        IReadOnlyDictionary<string, PortalBanExpectation> portalActiveBansByIdentifier,
        CancellationToken ct)
    {
        var importedCount = 0;

        foreach (var serverBan in serverBansByIdentifier)
        {
            if (portalActiveBansByIdentifier.ContainsKey(serverBan.Key))
            {
                continue;
            }

            var playerIdentifier = serverBan.Key;
            var playerName = string.IsNullOrWhiteSpace(serverBan.Value.Nick)
                ? playerIdentifier
                : serverBan.Value.Nick.Trim();

            var playerId = await GetOrCreatePlayerIdAsync(playerIdentifier, playerName, ct).ConfigureAwait(false);
            if (!playerId.HasValue)
            {
                continue;
            }

            var hasActiveBan = await HasActiveBanAsync(playerId.Value, ct).ConfigureAwait(false);
            if (!hasActiveBan.HasValue)
            {
                logger.LogWarning(
                    "Skipping CoD4x ban import for {PlayerIdentifier}: could not determine existing active-ban state",
                    playerIdentifier);
                continue;
            }

            if (hasActiveBan.Value)
            {
                continue;
            }

            var (actionType, expires) = ResolveAdminActionFromDumpBanEntry(serverBan.Value);
            var createDto = new CreateAdminActionDto(playerId.Value, actionType, "Imported from server RCON dumpbanlist")
            {
                Expires = expires
            };

            var createResult = await repositoryApiClient.AdminActions.V1
                .CreateAdminAction(createDto, ct)
                .ConfigureAwait(false);

            if (!createResult.IsSuccess)
            {
                logger.LogWarning("Failed to import CoD4x server ban for {PlayerIdentifier}: status {StatusCode}",
                    playerIdentifier,
                    createResult.StatusCode);
                continue;
            }

            importedCount++;
        }

        if (importedCount > 0)
        {
            logger.LogInformation("Imported {ImportedCount} CoD4x server-only bans into portal for server {ServerId}", importedCount, serverId);
        }
    }

    private async Task ReapplyPortalOnlyBansAsync(
        Guid serverId,
        IReadOnlyDictionary<string, CoD4xBanEntryDto> serverBansByIdentifier,
        IReadOnlyDictionary<string, PortalBanExpectation> portalActiveBansByIdentifier,
        CancellationToken ct)
    {
        var reapplied = 0;

        foreach (var portalBan in portalActiveBansByIdentifier.Values)
        {
            if (serverBansByIdentifier.ContainsKey(portalBan.PlayerIdentifier))
            {
                continue;
            }

            ApiResult<CoD4xBanCommandResponseDto> result;

            if (portalBan.IsTemporary)
            {
                var durationMinutes = Math.Max(portalBan.DurationMinutes ?? 1, 1);
                result = await coD4xRconApi.TempBanPlayerByPlayerIdentifier(
                    serverId,
                    new CoD4xTempBanRequestDto
                    {
                        PlayerIdentifier = portalBan.PlayerIdentifier,
                        DurationMinutes = durationMinutes
                    },
                    ct).ConfigureAwait(false);
            }
            else
            {
                result = await coD4xRconApi.BanPlayerByPlayerIdentifier(
                    serverId,
                    new CoD4xPermBanRequestDto
                    {
                        PlayerIdentifier = portalBan.PlayerIdentifier
                    },
                    ct).ConfigureAwait(false);
            }

            if (!result.IsSuccess || result.Result?.Data?.IsSuccess != true)
            {
                logger.LogWarning(
                    "Failed to reapply CoD4x {BanType} for {PlayerIdentifier} on server {ServerId}. Status={StatusCode}, Error={Error}",
                    portalBan.IsTemporary ? "temp ban" : "ban",
                    portalBan.PlayerIdentifier,
                    serverId,
                    result.StatusCode,
                    result.Result?.Data?.ErrorMessage ?? "Unknown");
                continue;
            }

            reapplied++;
        }

        if (reapplied > 0)
        {
            logger.LogInformation("Reapplied {ReappliedCount} CoD4x portal-active bans missing from server {ServerId}", reapplied, serverId);
        }
    }

    private async Task<Guid?> GetOrCreatePlayerIdAsync(
        string playerIdentifier,
        string playerName,
        CancellationToken ct)
    {
        var headResult = await repositoryApiClient.Players.V1
            .HeadPlayerByGameType(GameType.CallOfDuty4x, playerIdentifier)
            .ConfigureAwait(false);

        if (headResult.IsNotFound)
        {
            var createResult = await repositoryApiClient.Players.V1
                .CreatePlayer(new CreatePlayerDto(playerName, playerIdentifier, GameType.CallOfDuty4x))
                .ConfigureAwait(false);

            if (!createResult.IsSuccess && !createResult.IsConflict)
            {
                logger.LogWarning("Failed to create player during CoD4x ban import for {PlayerIdentifier}: status {StatusCode}",
                    playerIdentifier,
                    createResult.StatusCode);
                return null;
            }
        }

        var playerResult = await repositoryApiClient.Players.V1
            .GetPlayerByGameType(GameType.CallOfDuty4x, playerIdentifier, PlayerEntityOptions.None)
            .ConfigureAwait(false);

        if (!playerResult.IsSuccess || playerResult.Result?.Data is null)
        {
            logger.LogWarning("Could not resolve player during CoD4x ban import for {PlayerIdentifier}", playerIdentifier);
            return null;
        }

        return playerResult.Result.Data.PlayerId;
    }

    private async Task<bool?> HasActiveBanAsync(Guid playerId, CancellationToken ct)
    {
        var activeBansResult = await repositoryApiClient.AdminActions.V1
            .GetAdminActions(GameType.CallOfDuty4x, playerId, null, AdminActionFilter.ActiveBans, 0, 1, null, ct)
            .ConfigureAwait(false);

        if (!activeBansResult.IsSuccess)
        {
            return null;
        }

        return activeBansResult.Result?.Data?.Items?.Any() == true;
    }

    private static (AdminActionType ActionType, DateTime? Expires) ResolveAdminActionFromDumpBanEntry(CoD4xBanEntryDto entry)
    {
        if (string.Equals(entry.Expire, "Never", StringComparison.OrdinalIgnoreCase))
        {
            return (AdminActionType.Ban, null);
        }

        if (DateTime.TryParse(
            entry.Expire,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var expiresUtc))
        {
            return (AdminActionType.TempBan, expiresUtc);
        }

        return (AdminActionType.Ban, null);
    }

    private sealed record PortalBanExpectation(string PlayerIdentifier, bool IsTemporary, int? DurationMinutes)
    {
        public static PortalBanExpectation Permanent(string playerIdentifier) => new(playerIdentifier, false, null);

        public static PortalBanExpectation Temporary(string playerIdentifier, int durationMinutes)
            => new(playerIdentifier, true, durationMinutes);
    }
}
