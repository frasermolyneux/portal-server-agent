using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public sealed class CoD4xAdminReconciliationService(
    IRepositoryApiClient repositoryApiClient,
    ICoD4xRconApi coD4xRconApi,
    ILogger<CoD4xAdminReconciliationService> logger) : ICoD4xAdminReconciliationService
{
    public async Task ReconcileAsync(Guid serverId, string? gameType, CancellationToken ct = default)
    {
        if (!IsCoD4x(gameType))
        {
            return;
        }

        try
        {
            var desiredBuild = await BuildDesiredAdminsBySteamIdAsync(serverId, ct).ConfigureAwait(false);
            if (!desiredBuild.Success)
            {
                return;
            }

            if (!desiredBuild.Enabled)
            {
                logger.LogDebug("CoD4x admin reconciliation skipped for {ServerId}: roster disabled", serverId);
                return;
            }

            var adminListResult = await coD4xRconApi.AdminListAdmins(serverId, ct).ConfigureAwait(false);
            if (!adminListResult.IsSuccess || adminListResult.Result?.Data is null)
            {
                logger.LogWarning(
                    "CoD4x adminlistadmins failed for {ServerId}: status {StatusCode}",
                    serverId,
                    adminListResult.StatusCode);
                return;
            }

            var currentParseResult = ParseCurrentAdminsBySteamId(adminListResult.Result.Data);
            var currentAdminsBySteamId = currentParseResult.AdminsBySteamId;
            if (currentParseResult.UnmatchedLineCount > 0)
            {
                logger.LogWarning(
                    "CoD4x adminlistadmins returned {UnmatchedLineCount} unparseable lines for {ServerId}; removal pass will be skipped for safety",
                    currentParseResult.UnmatchedLineCount,
                    serverId);
            }

            var addedCount = 0;
            var updatedCount = 0;
            var removedCount = 0;

            foreach (var desiredAdmin in desiredBuild.AdminsBySteamId)
            {
                if (!currentAdminsBySteamId.TryGetValue(desiredAdmin.Key, out var currentPower))
                {
                    if (await TryAddOrUpdateAdminAsync(serverId, desiredAdmin.Key, desiredAdmin.Value, ct).ConfigureAwait(false))
                    {
                        addedCount++;
                    }

                    continue;
                }

                if (currentPower == desiredAdmin.Value)
                {
                    continue;
                }

                if (await TryAddOrUpdateAdminAsync(serverId, desiredAdmin.Key, desiredAdmin.Value, ct).ConfigureAwait(false))
                {
                    updatedCount++;
                }
            }

            if (!desiredBuild.HadLookupFailures && currentParseResult.UnmatchedLineCount == 0)
            {
                foreach (var currentAdmin in currentAdminsBySteamId)
                {
                    if (desiredBuild.AdminsBySteamId.ContainsKey(currentAdmin.Key))
                    {
                        continue;
                    }

                    if (await TryRemoveAdminAsync(serverId, currentAdmin.Key, ct).ConfigureAwait(false))
                    {
                        removedCount++;
                    }
                }
            }
            else if (desiredBuild.HadLookupFailures)
            {
                logger.LogWarning(
                    "CoD4x admin reconciliation skipped removal pass for {ServerId}: one or more desired players could not be resolved",
                    serverId);
            }

            logger.LogInformation(
                "CoD4x admin reconciliation for {ServerId}: desired={DesiredCount}, current={CurrentCount}, added={AddedCount}, updated={UpdatedCount}, removed={RemovedCount}, skippedWithoutSteamId={SkippedWithoutSteamId}",
                serverId,
                desiredBuild.AdminsBySteamId.Count,
                currentAdminsBySteamId.Count,
                addedCount,
                updatedCount,
                removedCount,
                desiredBuild.SkippedWithoutSteamIdCount);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "CoD4x admin reconciliation failed for server {ServerId}", serverId);
        }
    }

    private static bool IsCoD4x(string? gameType)
        => string.Equals(gameType, nameof(GameType.CallOfDuty4x), StringComparison.OrdinalIgnoreCase);

    private async Task<DesiredAdminBuildResult> BuildDesiredAdminsBySteamIdAsync(Guid serverId, CancellationToken ct)
    {
        var rosterResult = await repositoryApiClient.ConnectedPlayers.V1
            .GetCod4xAdminRoster(serverId, ct)
            .ConfigureAwait(false);

        if (!rosterResult.IsSuccess || rosterResult.Result?.Data is null)
        {
            logger.LogWarning(
                "Failed to retrieve CoD4x admin roster for {ServerId}: status {StatusCode}",
                serverId,
                rosterResult.StatusCode);
            return new DesiredAdminBuildResult(false, false, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), 0, false);
        }

        var roster = rosterResult.Result.Data;
        var desiredAdminsBySteamId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var skippedWithoutSteamIdCount = 0;
        var lookupFailureCount = 0;

        if (!roster.Enabled)
        {
            return new DesiredAdminBuildResult(true, false, desiredAdminsBySteamId, skippedWithoutSteamIdCount, false);
        }

        foreach (var entry in roster.Entries)
        {
            var playerGuid = entry.PlayerGuid?.Trim();
            if (string.IsNullOrWhiteSpace(playerGuid))
            {
                continue;
            }

            var playerResult = await repositoryApiClient.Players.V1
                .GetPlayerByGameType(GameType.CallOfDuty4x, playerGuid, PlayerEntityOptions.None)
                .ConfigureAwait(false);

            if (!playerResult.IsSuccess || playerResult.Result?.Data is null)
            {
                logger.LogWarning(
                    "Skipping desired CoD4x admin for player guid {PlayerGuid}: failed to resolve player record (status {StatusCode})",
                    playerGuid,
                    playerResult.StatusCode);
                lookupFailureCount++;
                continue;
            }

            var steamId = playerResult.Result.Data.SteamId?.Trim();
            if (string.IsNullOrWhiteSpace(steamId))
            {
                skippedWithoutSteamIdCount++;
                continue;
            }

            var desiredPower = Math.Clamp(entry.Power, 1, 100);
            desiredAdminsBySteamId[steamId] = desiredAdminsBySteamId.TryGetValue(steamId, out var existingPower)
                ? Math.Max(existingPower, desiredPower)
                : desiredPower;
        }

        return new DesiredAdminBuildResult(
            true,
            true,
            desiredAdminsBySteamId,
            skippedWithoutSteamIdCount,
            lookupFailureCount > 0);
    }

    private static CurrentAdminParseResult ParseCurrentAdminsBySteamId(string rawAdminListResponse)
    {
        var currentAdminsBySteamId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unmatchedLineCount = 0;

        var lines = rawAdminListResponse.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (!TryParseAdminListLine(line, out var steamId, out var power))
            {
                unmatchedLineCount++;
                continue;
            }

            currentAdminsBySteamId[steamId] = Math.Clamp(power, 1, 100);
        }

        return new CurrentAdminParseResult(currentAdminsBySteamId, unmatchedLineCount);
    }

    private static bool TryParseAdminListLine(string line, out string steamId, out int power)
    {
        steamId = string.Empty;
        power = 0;

        var powerIndex = line.IndexOf("Power:", StringComparison.OrdinalIgnoreCase);
        var steamIdIndex = line.IndexOf("SteamId:", StringComparison.OrdinalIgnoreCase);
        if (powerIndex < 0 || steamIdIndex < 0)
        {
            return false;
        }

        var powerSegmentStart = powerIndex + "Power:".Length;
        var powerSegmentLength = steamIdIndex - powerSegmentStart;
        if (powerSegmentLength <= 0)
        {
            return false;
        }

        var powerValue = line.Substring(powerSegmentStart, powerSegmentLength).Trim().TrimEnd(',');
        if (!int.TryParse(powerValue, out power))
        {
            return false;
        }

        var steamSegmentStart = steamIdIndex + "SteamId:".Length;
        if (steamSegmentStart >= line.Length)
        {
            return false;
        }

        steamId = line[steamSegmentStart..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(steamId);
    }

    private async Task<bool> TryAddOrUpdateAdminAsync(Guid serverId, string steamId, int power, CancellationToken ct)
    {
        var response = await coD4xRconApi.AdminAddAdmin(
            serverId,
            new CoD4xAdminAddAdminRequestDto
            {
                User = steamId,
                Power = Math.Clamp(power, 1, 100)
            },
            ct).ConfigureAwait(false);

        if (response.IsSuccess)
        {
            return true;
        }

        logger.LogWarning(
            "AdminAddAdmin failed for server {ServerId} and steamId {SteamId}: status {StatusCode}",
            serverId,
            steamId,
            response.StatusCode);

        return false;
    }

    private async Task<bool> TryRemoveAdminAsync(Guid serverId, string steamId, CancellationToken ct)
    {
        var response = await coD4xRconApi.AdminRemoveAdmin(
            serverId,
            new CoD4xAdminUserRequestDto
            {
                User = steamId
            },
            ct).ConfigureAwait(false);

        if (response.IsSuccess)
        {
            return true;
        }

        logger.LogWarning(
            "AdminRemoveAdmin failed for server {ServerId} and steamId {SteamId}: status {StatusCode}",
            serverId,
            steamId,
            response.StatusCode);

        return false;
    }

    private sealed record DesiredAdminBuildResult(
        bool Success,
        bool Enabled,
        Dictionary<string, int> AdminsBySteamId,
        int SkippedWithoutSteamIdCount,
        bool HadLookupFailures);

    private sealed record CurrentAdminParseResult(
        Dictionary<string, int> AdminsBySteamId,
        int UnmatchedLineCount);
}
