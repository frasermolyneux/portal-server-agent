using System.Collections.Concurrent;
using System.Text.Json;

using FluentFTP;

using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.BanFileMonitors;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Checks a game server's ban file via FTP. Downloads only new bytes (append-only),
/// parses untagged ban lines, and returns them for publishing. On cycles where no manual
/// additions are detected, also pushes the central regenerated ban file (produced by
/// portal-sync) to the server's FTP path so portal-issued bans take effect in-game.
/// The caller is responsible for updating the BanFileMonitor after successful publish.
/// </summary>
public sealed class BanFileWatcher : IBanFileWatcher
{
    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IBanFileSource _banFileSource;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<BanFileWatcher> _logger;

    // Tags that indicate the line was written by a known system — skip these
    private static readonly string[] SkipTags = ["[PBBAN]", "[B3BAN]", "[BANSYNC]", "[EXTERNAL]"];

    // Tracks the last central-blob ETag we successfully pushed for each BanFileMonitor.
    // In-memory only — on agent restart this is empty, which causes one (idempotent) push per monitor.
    private readonly ConcurrentDictionary<Guid, string> _lastPushedEtags = new();

    public BanFileWatcher(
        IRepositoryApiClient repositoryClient,
        IBanFileSource banFileSource,
        IAuditLogger auditLogger,
        ILogger<BanFileWatcher> logger)
    {
        _repositoryClient = repositoryClient ?? throw new ArgumentNullException(nameof(repositoryClient));
        _banFileSource = banFileSource ?? throw new ArgumentNullException(nameof(banFileSource));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
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

            IReadOnlyList<DetectedBanEntry> newBans = [];

            // File was truncated/rebuilt — reset and process from beginning
            if (fileSize < lastKnownSize)
            {
                _logger.LogInformation("[{Title}] Ban file {FilePath} was truncated ({OldSize} -> {NewSize}), re-processing from start",
                    context.Title, monitor.FilePath, lastKnownSize, fileSize);
                lastKnownSize = 0;
            }

            // Inbound: download only new bytes (append-only optimization) when the server file changed
            if (fileSize != lastKnownSize)
            {
                string newContent;
                await using (var stream = await ftp.OpenRead(monitor.FilePath, FtpDataType.Binary, lastKnownSize, false, ct).ConfigureAwait(false))
                using (var reader = new StreamReader(stream))
                {
                    newContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                }

                newBans = ParseBanLines(newContent);

                _logger.LogInformation("[{Title}] Ban file {FilePath}: {NewSize} bytes (was {OldSize}), found {BanCount} new untagged ban(s)",
                    context.Title, monitor.FilePath, fileSize, monitor.RemoteFileSize ?? 0, newBans.Count);
            }

            var finalSize = fileSize;

            // Outbound: only push the central blob when no manual additions were detected this cycle.
            // This mirrors the legacy "only push when remote == lastKnown" guard and avoids overwriting
            // a freshly-detected manual ban before the event has been ingested into the central blob.
            if (newBans.Count == 0)
            {
                var pushedSize = await TryPushCentralBanFileAsync(ftp, context, monitor, ct).ConfigureAwait(false);
                if (pushedSize.HasValue)
                {
                    finalSize = pushedSize.Value;
                }
            }

            var update = new MonitorUpdate
            {
                BanFileMonitorId = monitor.BanFileMonitorId,
                NewFileSize = finalSize
            };

            return (newBans, update);
        }
        finally
        {
            await ftp.Disconnect(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Pushes the central regenerated ban file to the game server via the open FTP session,
    /// but only when the central blob's ETag has changed since the last successful push for this monitor.
    /// Returns the new file size on success, or null if no push happened (no central blob, no change, or failure).
    /// </summary>
    internal async Task<long?> TryPushCentralBanFileAsync(
        AsyncFtpClient ftp, ServerContext context, BanFileMonitorDto monitor, CancellationToken ct)
    {
        CentralBanFile? central;
        try
        {
            central = await _banFileSource.GetAsync(context.GameType, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Title}] Failed to fetch central ban file for {GameType}",
                context.Title, context.GameType);
            return null;
        }

        if (central is null)
        {
            return null;
        }

        await using (central)
        {
            if (!ShouldPush(monitor.BanFileMonitorId, central.ETag))
            {
                // Already in sync with the latest central blob — nothing to do.
                return null;
            }

            // Re-check the remote file size immediately before overwriting. If it grew while we
            // were fetching the central blob (~100-800ms), an admin's manual ban could have been
            // appended in that window — overwriting now would silently erase it. Defer the push
            // until the next cycle so the new bytes get ingested first.
            long currentRemoteSize;
            try
            {
                currentRemoteSize = await ftp.GetFileSize(monitor.FilePath, -1, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Title}] Failed to re-check remote ban file size before push to {FilePath}",
                    context.Title, monitor.FilePath);
                return null;
            }

            var expectedRemoteSize = monitor.RemoteFileSize ?? 0;
            if (currentRemoteSize != expectedRemoteSize)
            {
                _logger.LogInformation(
                    "[{Title}] Skipping central ban file push to {FilePath} — remote size changed during fetch ({Expected} -> {Current}); will retry next cycle after manual additions are ingested",
                    context.Title, monitor.FilePath, expectedRemoteSize, currentRemoteSize);
                return null;
            }

            try
            {
                central.Content.Seek(0, SeekOrigin.Begin);
                await ftp.UploadStream(central.Content, monitor.FilePath, FtpRemoteExists.Overwrite, true, null, ct).ConfigureAwait(false);

                MarkPushed(monitor.BanFileMonitorId, central.ETag);
                _logger.LogInformation("[{Title}] Pushed central ban file ({Length} bytes, ETag {ETag}) to {FilePath}",
                    context.Title, central.Length, central.ETag, monitor.FilePath);

                await RecordPushAsync(context, monitor, central, ct).ConfigureAwait(false);

                return central.Length;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Title}] Failed to push central ban file to {FilePath}",
                    context.Title, monitor.FilePath);
                return null;
            }
        }
    }

    /// <summary>
    /// Records a successful central-blob push as both a per-server <c>BanFilePushed</c>
    /// game-server event (visible on the Server Events page) and an
    /// <c>Audit:BanFilePushed</c> audit (visible on the Activity Log page).
    /// Failures here are logged and swallowed — they must not roll back the actual push.
    /// </summary>
    private async Task RecordPushAsync(
        ServerContext context,
        BanFileMonitorDto monitor,
        CentralBanFile central,
        CancellationToken ct)
    {
        var eventData = JsonSerializer.Serialize(new
        {
            monitor.BanFileMonitorId,
            monitor.FilePath,
            central.Length,
            central.ETag
        });

        try
        {
            await _repositoryClient.GameServersEvents.V1
                .CreateGameServerEvent(
                    new CreateGameServerEventDto(context.ServerId, "BanFilePushed", eventData),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Title}] Failed to write BanFilePushed game server event for {FilePath}",
                context.Title, monitor.FilePath);
        }

        try
        {
            _auditLogger.LogAudit(AuditEvent.SystemAction("BanFilePushed", AuditAction.Export)
                .WithService("BanFileWatcher")
                .WithGameContext(context.GameType, context.ServerId)
                .WithProperty("BanFileMonitorId", monitor.BanFileMonitorId.ToString())
                .WithProperty("FilePath", monitor.FilePath ?? string.Empty)
                .WithProperty("Length", central.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithProperty("ETag", central.ETag)
                .Build());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Title}] Failed to emit BanFilePushed audit for {FilePath}",
                context.Title, monitor.FilePath);
        }
    }

    /// <summary>
    /// Returns true when the central blob's ETag has not yet been pushed for this monitor.
    /// Exposed for unit testing.
    /// </summary>
    internal bool ShouldPush(Guid banFileMonitorId, string centralEtag)
    {
        return !_lastPushedEtags.TryGetValue(banFileMonitorId, out var lastEtag)
            || lastEtag != centralEtag;
    }

    /// <summary>
    /// Records the ETag of the central blob most recently pushed for this monitor.
    /// Exposed for unit testing.
    /// </summary>
    internal void MarkPushed(Guid banFileMonitorId, string centralEtag)
    {
        _lastPushedEtags[banFileMonitorId] = centralEtag;
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

