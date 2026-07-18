using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public sealed class CoD4xBanReconciliationService(
    IRepositoryApiClient repositoryApiClient,
    ICoD4xRconApi coD4xRconApi,
    IEventPublisher eventPublisher,
    ILogger<CoD4xBanReconciliationService> logger) : ICoD4xBanReconciliationService
{
    private const int ActiveBanPageSize = 200;
    private const int LegacyCoD4xIdentifierLength = 13;
    private const int CanonicalPuidSearchPrefixLength = 11;
    private const int LegacyCoD4xIdentifierPrefix = 4;
    private const int CanonicalPuidSearchPageSize = 20;
    private const string RconDumpBanListSource = "RconDumpbanlist";
    private const string PortalBanReasonMarker = "[PORTAL-BAN]";
    private const string PortalAutomationReasonMarker = "[PORTAL-AUTOMATION]";

    public async Task ReconcileAsync(Guid serverId, string? gameType, CancellationToken ct = default)
        => await ReconcileAsync(serverId, gameType, isCod4xPluginSourceEnabled: false, ct).ConfigureAwait(false);

    public async Task ReconcileAsync(Guid serverId, string? gameType, bool isCod4xPluginSourceEnabled, CancellationToken ct = default)
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

            await ImportServerOnlyBansAsync(
                serverId,
                serverBansByIdentifier,
                portalActiveBansByIdentifier,
                isCod4xPluginSourceEnabled,
                ct).ConfigureAwait(false);

            if (!isCod4xPluginSourceEnabled)
            {
                await ReapplyPortalOnlyBansAsync(serverId, gameType, serverBansByIdentifier, portalActiveBansByIdentifier, ct).ConfigureAwait(false);
            }
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
        bool isCod4xPluginSourceEnabled,
        CancellationToken ct)
    {
        var importedCount = 0;

        foreach (var serverBan in serverBansByIdentifier)
        {
            if (portalActiveBansByIdentifier.ContainsKey(serverBan.Key))
            {
                continue;
            }

            if (IsPortalAutomationBan(serverBan.Value)
                || (isCod4xPluginSourceEnabled && IsPortalManagedBan(serverBan.Value)))
            {
                continue;
            }

            var playerIdentifier = await ResolveCanonicalPlayerIdentifierAsync(serverBan.Key, ct).ConfigureAwait(false);
            if (playerIdentifier is null)
            {
                continue;
            }

            if (portalActiveBansByIdentifier.ContainsKey(playerIdentifier))
            {
                continue;
            }

            var playerName = string.IsNullOrWhiteSpace(serverBan.Value.Nick)
                ? playerIdentifier
                : serverBan.Value.Nick.Trim();

            var (isTemporary, expires) = ResolveBanDetails(serverBan.Value);
            var reason = string.IsNullOrWhiteSpace(serverBan.Value.Reason)
                ? "Imported from server RCON dumpbanlist"
                : serverBan.Value.Reason.Trim();

            await TryPublishBanAppliedAsync(
                serverId,
                nameof(GameType.CallOfDuty4x),
                playerIdentifier,
                playerName,
                isTemporary,
                expires,
                RconDumpBanListSource,
                reason,
                ct).ConfigureAwait(false);

            importedCount++;
        }

        if (importedCount > 0)
        {
            logger.LogInformation("Imported {ImportedCount} CoD4x server-only bans into portal for server {ServerId}", importedCount, serverId);
        }
    }

    private static bool IsPortalManagedBan(CoD4xBanEntryDto banEntry)
        => !string.IsNullOrWhiteSpace(banEntry.Reason)
            && banEntry.Reason.Contains(PortalBanReasonMarker, StringComparison.OrdinalIgnoreCase);

    private static bool IsPortalAutomationBan(CoD4xBanEntryDto banEntry)
        => !string.IsNullOrWhiteSpace(banEntry.Reason)
            && banEntry.Reason.Contains(PortalAutomationReasonMarker, StringComparison.OrdinalIgnoreCase);

    private async Task ReapplyPortalOnlyBansAsync(
        Guid serverId,
        string? gameType,
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

            try
            {
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
            }
            catch (Exception ex)
            {
                await TryPublishBanSyncFailedAsync(
                    serverId,
                    gameType,
                    "ReapplyPortalBan",
                    ex.Message,
                    portalBan.PlayerIdentifier,
                    null,
                    ct).ConfigureAwait(false);

                logger.LogWarning(
                    ex,
                    "Exception while reapplying CoD4x {BanType} for {PlayerIdentifier} on server {ServerId}",
                    portalBan.IsTemporary ? "temp ban" : "ban",
                    portalBan.PlayerIdentifier,
                    serverId);
                continue;
            }

            if (!result.IsSuccess || result.Result?.Data?.IsSuccess != true)
            {
                var detailedError = result.Result?.Errors?.FirstOrDefault()?.Message;
                var failureReason =
                    !string.IsNullOrWhiteSpace(detailedError)
                        ? detailedError
                        : !string.IsNullOrWhiteSpace(result.Result?.Data?.ErrorMessage)
                            ? result.Result.Data.ErrorMessage
                            : $"Status code {result.StatusCode}";

                await TryPublishBanSyncFailedAsync(
                    serverId,
                    gameType,
                    "ReapplyPortalBan",
                    failureReason,
                    portalBan.PlayerIdentifier,
                    null,
                    ct).ConfigureAwait(false);

                logger.LogWarning(
                    "Failed to reapply CoD4x {BanType} for {PlayerIdentifier} on server {ServerId}. Status={StatusCode}, Error={Error}",
                    portalBan.IsTemporary ? "temp ban" : "ban",
                    portalBan.PlayerIdentifier,
                    serverId,
                    result.StatusCode,
                    result.Result?.Data?.ErrorMessage ?? "Unknown");
                continue;
            }

            await TryPublishBanAppliedAsync(
                serverId,
                gameType,
                portalBan.PlayerIdentifier,
                portalBan.PlayerIdentifier,
                portalBan.IsTemporary,
                portalBan.IsTemporary && portalBan.DurationMinutes.HasValue
                    ? DateTime.UtcNow.AddMinutes(portalBan.DurationMinutes.Value)
                    : null,
                "Portal",
                portalBan.IsTemporary
                    ? "Reconciled missing temporary ban from portal"
                    : "Reconciled missing permanent ban from portal",
                ct).ConfigureAwait(false);

            reapplied++;
        }

        if (reapplied > 0)
        {
            logger.LogInformation("Reapplied {ReappliedCount} CoD4x portal-active bans missing from server {ServerId}", reapplied, serverId);
        }
    }

    private async Task<string?> ResolveCanonicalPlayerIdentifierAsync(string playerIdentifier, CancellationToken ct)
    {
        if (!IsLegacyCoD4xIdentifier(playerIdentifier))
        {
            return playerIdentifier;
        }

        var searchPrefix = GetCanonicalPuidSearchPrefix(playerIdentifier);
        if (searchPrefix is null)
        {
            return null;
        }

        var matchingPlayers = new List<PlayerDto>();
        var skip = 0;

        while (true)
        {
            var playersResult = await repositoryApiClient.Players.V1
                .GetPlayers(
                    GameType.CallOfDuty4x,
                    PlayersFilter.UsernameAndGuid,
                    searchPrefix,
                    skip,
                    CanonicalPuidSearchPageSize,
                    null,
                    PlayerEntityOptions.None)
                .ConfigureAwait(false);

            if (!playersResult.IsSuccess || playersResult.Result?.Data?.Items is null)
            {
                logger.LogWarning("Could not search for canonical PUID during CoD4x ban import for legacy identifier {PlayerIdentifier}", playerIdentifier);
                return null;
            }

            var pageItems = playersResult.Result.Data.Items.ToList();
            matchingPlayers.AddRange(pageItems.Where(player => MatchesLegacyCoD4xIdentifier(player.Guid, playerIdentifier)));

            if (matchingPlayers.Count > 1 || pageItems.Count < CanonicalPuidSearchPageSize)
            {
                break;
            }

            skip += pageItems.Count;
        }

        if (matchingPlayers.Count != 1)
        {
            logger.LogWarning(
                "Skipping CoD4x ban import for legacy identifier {PlayerIdentifier}: found {CandidateCount} matching canonical PUIDs",
                playerIdentifier,
                matchingPlayers.Count);
            return null;
        }

        return matchingPlayers[0].Guid;
    }

    private static bool IsLegacyCoD4xIdentifier(string playerIdentifier)
        => playerIdentifier.Length == LegacyCoD4xIdentifierLength && playerIdentifier.All(char.IsAsciiDigit);

    private static string? GetCanonicalPuidSearchPrefix(string legacyIdentifier)
    {
        if (!System.Numerics.BigInteger.TryParse(legacyIdentifier, out var legacyValue))
        {
            return null;
        }

        var legacyPayloadMask = (System.Numerics.BigInteger.One << 40) - 1;
        var canonicalPuidLowerBound = (legacyValue & legacyPayloadMask) << 24;
        var canonicalPuid = canonicalPuidLowerBound.ToString();

        return canonicalPuid.Length < CanonicalPuidSearchPrefixLength
            ? null
            : canonicalPuid[..CanonicalPuidSearchPrefixLength];
    }

    private static bool MatchesLegacyCoD4xIdentifier(string canonicalPuid, string legacyIdentifier)
    {
        if (!System.Numerics.BigInteger.TryParse(canonicalPuid, out var puid) ||
            !System.Numerics.BigInteger.TryParse(legacyIdentifier, out var legacy))
        {
            return false;
        }

        var legacyPayloadMask = (System.Numerics.BigInteger.One << 40) - 1;
        var derivedLegacy = ((System.Numerics.BigInteger)LegacyCoD4xIdentifierPrefix << 40) | (puid >> 24);
        return derivedLegacy == legacy && (legacy & legacyPayloadMask) == (puid >> 24);
    }

    private static (bool IsTemporary, DateTime? Expires) ResolveBanDetails(CoD4xBanEntryDto entry)
    {
        if (string.Equals(entry.Expire, "Never", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        if (DateTime.TryParse(
            entry.Expire,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var expiresUtc))
        {
            return (true, expiresUtc);
        }

        return (false, null);
    }

    private async Task TryPublishBanAppliedAsync(
        Guid serverId,
        string? gameType,
        string playerGuid,
        string playerName,
        bool isTemporary,
        DateTime? expiresUtc,
        string source,
        string reason,
        CancellationToken ct)
    {
        try
        {
            await eventPublisher.PublishBanAppliedAsync(
                serverId,
                gameType ?? nameof(GameType.CallOfDuty4x),
                DateTime.UtcNow.Ticks,
                playerGuid,
                playerName,
                isTemporary,
                expiresUtc,
                source,
                reason,
                null,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to publish BanAppliedEvent during CoD4x reconciliation for server {ServerId}, player {PlayerGuid}",
                serverId,
                playerGuid);
        }
    }

    private async Task TryPublishBanSyncFailedAsync(
        Guid serverId,
        string? gameType,
        string operation,
        string failureReason,
        string? playerGuid,
        string? playerName,
        CancellationToken ct)
    {
        try
        {
            await eventPublisher.PublishBanSyncFailedAsync(
                serverId,
                gameType ?? nameof(GameType.CallOfDuty4x),
                DateTime.UtcNow.Ticks,
                operation,
                failureReason,
                "Agent",
                playerGuid,
                playerName,
                null,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to publish BanSyncFailedEvent during CoD4x reconciliation for server {ServerId}",
                serverId);
        }
    }

    private sealed record PortalBanExpectation(string PlayerIdentifier, bool IsTemporary, int? DurationMinutes)
    {
        public static PortalBanExpectation Permanent(string playerIdentifier) => new(playerIdentifier, false, null);

        public static PortalBanExpectation Temporary(string playerIdentifier, int durationMinutes)
            => new(playerIdentifier, true, durationMinutes);
    }
}
