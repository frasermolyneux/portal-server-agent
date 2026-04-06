using FluentFTP;

using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.BanFileMonitors;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Checks a game server's ban file via FTP. Downloads only new bytes (append-only),
/// parses untagged ban lines, and returns them for publishing.
/// The caller is responsible for updating the BanFileMonitor after successful publish.
/// </summary>
public sealed class BanFileWatcher : IBanFileWatcher
{
    private readonly IRepositoryApiClient _repositoryClient;
    private readonly ILogger<BanFileWatcher> _logger;

    // Tags that indicate the line was written by a known system — skip these
    private static readonly string[] SkipTags = ["[PBBAN]", "[B3BAN]", "[BANSYNC]", "[EXTERNAL]"];

    public BanFileWatcher(IRepositoryApiClient repositoryClient, ILogger<BanFileWatcher> logger)
    {
        _repositoryClient = repositoryClient ?? throw new ArgumentNullException(nameof(repositoryClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BanFileCheckResult> CheckAsync(ServerContext context, CancellationToken ct = default)
    {
        // 1. Get BanFileMonitors for this server
        var monitorsResponse = await _repositoryClient.BanFileMonitors.V1.GetBanFileMonitors(
            gameTypes: null,
            banFileMonitorIds: null,
            gameServerId: context.ServerId,
            skipEntries: 0,
            takeEntries: 50,
            order: null,
            cancellationToken: ct).ConfigureAwait(false);

        if (!monitorsResponse.IsSuccess || monitorsResponse.Result?.Data?.Items is null)
        {
            _logger.LogWarning("[{Title}] Failed to fetch BanFileMonitors for server {ServerId}",
                context.Title, context.ServerId);
            return BanFileCheckResult.Empty;
        }

        var monitors = monitorsResponse.Result.Data.Items.ToList();
        if (monitors.Count == 0)
        {
            return BanFileCheckResult.Empty;
        }

        var allNewBans = new List<DetectedBanEntry>();
        var monitorUpdates = new List<MonitorUpdate>();

        foreach (var monitor in monitors)
        {
            try
            {
                var checkResult = await CheckSingleMonitorAsync(context, monitor, ct).ConfigureAwait(false);
                allNewBans.AddRange(checkResult.Bans);
                if (checkResult.Update is not null)
                {
                    monitorUpdates.Add(checkResult.Update);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Title}] Error checking ban file monitor {MonitorId} ({FilePath})",
                    context.Title, monitor.BanFileMonitorId, monitor.FilePath);
            }
        }

        return new BanFileCheckResult
        {
            NewBans = allNewBans,
            MonitorUpdates = monitorUpdates
        };
    }

    internal async Task<(IReadOnlyList<DetectedBanEntry> Bans, MonitorUpdate? Update)> CheckSingleMonitorAsync(
        ServerContext context, BanFileMonitorDto monitor, CancellationToken ct)
    {
        using var ftp = new AsyncFtpClient(context.FtpHostname, context.FtpUsername, context.FtpPassword, context.FtpPort);
        await ftp.Connect(ct).ConfigureAwait(false);

        try
        {
            var fileSize = await ftp.GetFileSize(monitor.FilePath, -1, ct).ConfigureAwait(false);
            var lastKnownSize = monitor.RemoteFileSize ?? 0;

            // File hasn't changed
            if (fileSize == lastKnownSize)
            {
                return ([], null);
            }

            // File was truncated/rebuilt — reset and process from beginning
            if (fileSize < lastKnownSize)
            {
                _logger.LogInformation("[{Title}] Ban file {FilePath} was truncated ({OldSize} -> {NewSize}), re-processing from start",
                    context.Title, monitor.FilePath, lastKnownSize, fileSize);
                lastKnownSize = 0;
            }

            // Download only new bytes (append-only optimization)
            string newContent;
            await using (var stream = await ftp.OpenRead(monitor.FilePath, FtpDataType.Binary, lastKnownSize, false, ct).ConfigureAwait(false))
            using (var reader = new StreamReader(stream))
            {
                newContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            }

            // Parse new ban lines
            var newBans = ParseBanLines(newContent);

            _logger.LogInformation("[{Title}] Ban file {FilePath}: {NewSize} bytes (was {OldSize}), found {BanCount} new untagged ban(s)",
                context.Title, monitor.FilePath, fileSize, monitor.RemoteFileSize ?? 0, newBans.Count);

            var update = new MonitorUpdate
            {
                BanFileMonitorId = monitor.BanFileMonitorId,
                NewFileSize = fileSize
            };

            return (newBans, update);
        }
        finally
        {
            await ftp.Disconnect(ct).ConfigureAwait(false);
        }
    }

    internal static IReadOnlyList<DetectedBanEntry> ParseBanLines(string content)
    {
        var bans = new List<DetectedBanEntry>();

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Skip lines with known system tags
            if (SkipTags.Any(tag => trimmed.Contains(tag, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Ban file format: "GUID PLAYERNAME"
            var spaceIndex = trimmed.IndexOf(' ');
            if (spaceIndex <= 0)
                continue;

            var guid = trimmed[..spaceIndex].Trim().ToLowerInvariant();
            var name = trimmed[(spaceIndex + 1)..].Trim();

            // Basic GUID validation — skip obviously malformed entries
            if (guid.Length < 2 || string.IsNullOrWhiteSpace(name))
                continue;

            bans.Add(new DetectedBanEntry { PlayerGuid = guid, PlayerName = name });
        }

        return bans;
    }

    /// <summary>
    /// Updates the BanFileMonitor with the new file size after successful event publish.
    /// </summary>
    public async Task AcknowledgeAsync(IReadOnlyList<MonitorUpdate> updates, CancellationToken ct = default)
    {
        foreach (var update in updates)
        {
            try
            {
                var editDto = new EditBanFileMonitorDto(update.BanFileMonitorId, update.NewFileSize, DateTime.UtcNow);
                await _repositoryClient.BanFileMonitors.V1.UpdateBanFileMonitor(editDto, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update BanFileMonitor {MonitorId} after publish",
                    update.BanFileMonitorId);
            }
        }
    }
}
