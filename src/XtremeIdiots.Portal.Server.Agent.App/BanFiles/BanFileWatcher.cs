using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

using FluentFTP;
using FluentFTP.Exceptions;

using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.BanFileMonitors;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Single-monitor-per-server ban file watcher.
///
/// On each cycle:
/// 1. Resolve the FTP path from per-game-type rules + the live mod (no manual FilePath).
/// 2. Stat the remote file; if size changed, download only the new bytes (append-only
///    optimisation) and parse untagged ban entries.
/// 3. If no manual additions detected this cycle, push the central regenerated blob
///    when its ETag has changed and the per-server stagger window has elapsed —
///    avoids 80+ servers all uploading 4 MB simultaneously after a portal-sync regen.
/// 4. Recount per-tag totals (cached by remote file size to avoid re-reading the
///    full file every cycle).
/// 5. Upsert the BanFileMonitor status row with check + push + count results so the
///    dashboard always sees the latest cycle, regardless of whether bans were
///    detected.
///
/// Returns detected bans for the agent to publish; the agent then calls
/// <see cref="AcknowledgeImportAsync"/> after publish so the import-status fields
/// are persisted only after Service Bus acknowledges them.
/// </summary>
public sealed class BanFileWatcher : IBanFileWatcher
{
    private readonly IRepositoryApiClient _repositoryClient;
    private readonly IBanFileSource _banFileSource;
    private readonly IBanFilePathResolver _pathResolver;
    private readonly IAuditLogger _auditLogger;
    private readonly ILogger<BanFileWatcher> _logger;
    private readonly Random _jitterRng;

    /// <summary>Tags that indicate the line was written by a known system — skip these.</summary>
    private static readonly string[] SkipTags = ["[PBBAN]", "[B3BAN]", "[BANSYNC]", "[EXTERNAL]"];

    /// <summary>
    /// Maximum jitter applied when scheduling a central blob push after the ETag changes.
    /// Spreads simultaneous 4 MB FTP uploads across this window. Trade-off: too short and
    /// the storage account sees a thundering herd after every portal-sync regeneration; too
    /// long and admins see stale "Bans on file" counts on the dashboard for that long after
    /// each new ban. 5 min comfortably covers a fleet of ~80 servers without making per-server
    /// push lag user-visible.
    /// </summary>
    internal static readonly TimeSpan PushStaggerMaxJitter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-server scheduled push state. Set when a new central ETag is observed; cleared
    /// after a successful push or when the central ETag changes again.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, ScheduledPush> _scheduledPushes = new();

    /// <summary>
    /// Per-server cached per-tag counts keyed by the remote file size that produced them.
    /// Invalidated on size mismatch; reduces 80× full-file reads per minute to one
    /// re-count only when the file actually changes.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, CachedCounts> _countCache = new();

    /// <summary>
    /// Per-server "consecutive failure" counter, used to drive a degraded badge on
    /// the dashboard. Reset on the next successful check.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, int> _consecutiveFailures = new();

    public BanFileWatcher(
        IRepositoryApiClient repositoryClient,
        IBanFileSource banFileSource,
        IBanFilePathResolver pathResolver,
        IAuditLogger auditLogger,
        ILogger<BanFileWatcher> logger)
    {
        _repositoryClient = repositoryClient ?? throw new ArgumentNullException(nameof(repositoryClient));
        _banFileSource = banFileSource ?? throw new ArgumentNullException(nameof(banFileSource));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _jitterRng = new Random();
    }

    /// <summary>Constructor overload exposed for tests that need deterministic jitter.</summary>
    internal BanFileWatcher(
        IRepositoryApiClient repositoryClient,
        IBanFileSource banFileSource,
        IBanFilePathResolver pathResolver,
        IAuditLogger auditLogger,
        ILogger<BanFileWatcher> logger,
        Random jitterRng)
        : this(repositoryClient, banFileSource, pathResolver, auditLogger, logger)
    {
        _jitterRng = jitterRng ?? throw new ArgumentNullException(nameof(jitterRng));
    }

    public async Task<BanFileCheckResult> CheckAsync(ServerContext context, string? liveMod, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolvedPath = _pathResolver.Resolve(context.GameType, context.BanFileRootPath, liveMod);

        // Pull the existing status row up-front — its RemoteFileSize is the baseline
        // for the append-only ingest, and its LastPushedETag tells us whether the
        // central blob still needs propagating to this server.
        var existingMonitor = await GetExistingMonitorAsync(context.ServerId, ct).ConfigureAwait(false);
        var lastKnownSize = existingMonitor?.RemoteFileSize ?? 0;
        var monitorIdForLogs = existingMonitor?.BanFileMonitorId.ToString() ?? "(new)";

        IReadOnlyList<DetectedBanEntry> detectedBans = [];
        long? finalSize = null;
        long? pushedSize = null;
        string? pushedEtag = null;
        DateTime? pushedAt = null;
        string? centralEtag = null;
        DateTime? centralEtagSeenAt = null;
        string? checkErrorMessage = null;
        var checkResult = "Success";

        try
        {
            using var ftp = new AsyncFtpClient(context.FtpHostname, context.FtpUsername, context.FtpPassword, context.FtpPort);
            await ftp.Connect(ct).ConfigureAwait(false);

            try
            {
                long currentSize;
                try
                {
                    currentSize = await ftp.GetFileSize(resolvedPath.Path, -1, ct).ConfigureAwait(false);
                }
                catch (FtpException ex) when (IsFileNotFound(ex))
                {
                    // Path resolution may have raced with a mod change, or the server has
                    // never had a ban.txt at the resolved path. Surface as a clean
                    // FileNotFound rather than crashing the whole cycle.
                    checkResult = "FileNotFound";
                    checkErrorMessage = ex.Message;
                    currentSize = -1;
                }

                if (currentSize >= 0)
                {
                    var truncated = currentSize < lastKnownSize;
                    if (truncated)
                    {
                        _logger.LogInformation(
                            "[{Title}] Ban file {Path} truncated ({OldSize} -> {NewSize}); re-processing from start",
                            context.Title, resolvedPath.Path, lastKnownSize, currentSize);
                        lastKnownSize = 0;
                        // Truncation invalidates count cache.
                        _countCache.TryRemove(context.ServerId, out _);
                    }

                    if (currentSize != lastKnownSize)
                    {
                        // Append-only download: only the new tail.
                        string newContent;
                        await using (var stream = await ftp.OpenRead(resolvedPath.Path, FtpDataType.Binary, lastKnownSize, false, ct).ConfigureAwait(false))
                        using (var reader = new StreamReader(stream))
                        {
                            newContent = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                        }

                        detectedBans = ParseBanLines(newContent);

                        _logger.LogInformation(
                            "[{Title}] Ban file {Path}: {NewSize} bytes (was {OldSize}), found {BanCount} new untagged ban(s)",
                            context.Title, resolvedPath.Path, currentSize, lastKnownSize, detectedBans.Count);
                    }

                    finalSize = currentSize;

                    // Outbound push only when no manual additions detected this cycle.
                    // Mirrors the legacy "remote == lastKnown" guard that prevents
                    // overwriting a freshly-detected manual ban before it's been ingested.
                    if (detectedBans.Count == 0)
                    {
                        var pushOutcome = await TryPushCentralBanFileAsync(
                            ftp, context, existingMonitor, resolvedPath, currentSize, ct).ConfigureAwait(false);

                        centralEtag = pushOutcome.CentralEtag;
                        centralEtagSeenAt = pushOutcome.CentralEtagSeenAt;
                        if (pushOutcome.Pushed)
                        {
                            finalSize = pushOutcome.NewSize;
                            pushedSize = pushOutcome.NewSize;
                            pushedEtag = pushOutcome.CentralEtag;
                            pushedAt = DateTime.UtcNow;
                        }
                    }
                }
            }
            finally
            {
                await ftp.Disconnect(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            checkResult = "FtpError";
            checkErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            _logger.LogWarning(ex, "[{Title}] Ban file check failed (monitor {MonitorId}, path {Path})",
                context.Title, monitorIdForLogs, resolvedPath.Path);
        }

        // Update or invalidate the consecutive failure counter.
        var consecutiveFailures = checkResult == "Success"
            ? 0
            : _consecutiveFailures.AddOrUpdate(context.ServerId, 1, (_, prev) => prev + 1);
        if (checkResult == "Success")
            _consecutiveFailures[context.ServerId] = 0;

        // Re-count per-tag totals from the remote size — cached to avoid re-reading
        // the full file on every cycle when nothing has changed.
        var counts = await GetOrRecountAsync(
            context, resolvedPath.Path, finalSize, ct).ConfigureAwait(false);

        // Build and persist the status snapshot. This always happens — even when bans
        // were detected, so the dashboard sees the cycle even before publish completes.
        // The import-status fields are deliberately omitted here; they are written by
        // AcknowledgeImportAsync only after the agent confirms publish succeeded.
        var nowUtc = DateTime.UtcNow;
        var statusDto = new UpsertBanFileMonitorStatusDto(context.ServerId)
        {
            LastCheckUtc = nowUtc,
            LastCheckResult = checkResult,
            LastCheckErrorMessage = checkErrorMessage,
            RemoteFilePath = resolvedPath.Path,
            ResolvedForMod = resolvedPath.ResolvedForMod,
            RemoteFileSize = finalSize,
            LastPushUtc = pushedAt,
            LastPushedETag = pushedEtag,
            LastPushedSize = pushedSize,
            LastCentralBlobETag = centralEtag,
            LastCentralBlobUtc = centralEtagSeenAt,
            ConsecutiveFailureCount = consecutiveFailures,
            RemoteTotalLineCount = counts?.Total,
            RemoteUntaggedCount = counts?.Untagged,
            RemoteBanSyncCount = counts?.BanSync,
            RemoteExternalCount = counts?.External
        };

        await UpsertStatusAsync(statusDto, context, ct).ConfigureAwait(false);

        if (detectedBans.Count == 0)
            return BanFileCheckResult.Empty;

        var sampleNames = JsonSerializer.Serialize(
            detectedBans.Take(5).Select(b => b.PlayerName).ToArray());

        return new BanFileCheckResult
        {
            NewBans = detectedBans,
            ImportAcknowledgment = new ImportAcknowledgment
            {
                ImportUtc = nowUtc,
                BanCount = detectedBans.Count,
                SampleNamesJson = sampleNames
            }
        };
    }

    public async Task AcknowledgeImportAsync(Guid serverId, ImportAcknowledgment acknowledgment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgment);

        var statusDto = new UpsertBanFileMonitorStatusDto(serverId)
        {
            LastImportUtc = acknowledgment.ImportUtc,
            LastImportBanCount = acknowledgment.BanCount,
            LastImportSampleNames = acknowledgment.SampleNamesJson
        };

        try
        {
            var response = await _repositoryClient.BanFileMonitors.V1
                .UpsertBanFileMonitorStatus(statusDto, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                _logger.LogWarning(
                    "Failed to acknowledge import for server {ServerId}: status {StatusCode}",
                    serverId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exception acknowledging import for server {ServerId}", serverId);
        }
    }

    /// <summary>
    /// Pushes the central regenerated ban file to the game server when (a) the central
    /// blob's ETag has changed since the last successful push and (b) the per-server
    /// stagger window has elapsed. Returns push outcome and the central ETag observed
    /// this cycle so the caller can record it on the status row even when no push occurred.
    /// </summary>
    internal async Task<PushOutcome> TryPushCentralBanFileAsync(
        AsyncFtpClient ftp,
        ServerContext context,
        BanFileMonitorDto? existingMonitor,
        ResolvedBanFilePath resolvedPath,
        long currentRemoteSize,
        CancellationToken ct)
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
            return PushOutcome.NoPush(centralEtag: null, centralEtagSeenAt: null);
        }

        if (central is null)
            return PushOutcome.NoPush(centralEtag: null, centralEtagSeenAt: null);

        await using (central)
        {
            var nowUtc = DateTime.UtcNow;
            var lastPushedEtag = existingMonitor?.LastPushedETag;
            var alreadyPushed = string.Equals(lastPushedEtag, central.ETag, StringComparison.Ordinal);

            if (alreadyPushed)
            {
                _scheduledPushes.TryRemove(context.ServerId, out _);
                return PushOutcome.NoPush(central.ETag, nowUtc);
            }

            // ETag-jittered scheduling: spread fleet-wide pushes after a portal-sync regen
            // across PushStaggerMaxJitter so the storage account does not see N×4 MB
            // simultaneous reads.
            var scheduled = _scheduledPushes.AddOrUpdate(
                context.ServerId,
                _ => new ScheduledPush(central.ETag, nowUtc + RandomJitter()),
                (_, existing) => string.Equals(existing.CentralEtag, central.ETag, StringComparison.Ordinal)
                    ? existing
                    : new ScheduledPush(central.ETag, nowUtc + RandomJitter()));

            if (nowUtc < scheduled.ScheduledFor)
            {
                _logger.LogDebug(
                    "[{Title}] Push deferred to {ScheduledFor:o} (ETag {ETag}, jitter {Remaining})",
                    context.Title, scheduled.ScheduledFor, central.ETag, scheduled.ScheduledFor - nowUtc);
                return PushOutcome.NoPush(central.ETag, nowUtc);
            }

            // Re-check the remote file size immediately before overwriting. If it grew
            // while we were fetching the central blob, an admin's manual ban could have
            // been appended in that window — defer the push so the new bytes get
            // ingested first.
            long currentSizeBeforePush;
            try
            {
                currentSizeBeforePush = await ftp.GetFileSize(resolvedPath.Path, -1, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Title}] Failed to re-check remote ban file size before push to {Path}",
                    context.Title, resolvedPath.Path);
                return PushOutcome.NoPush(central.ETag, nowUtc);
            }

            if (currentSizeBeforePush != currentRemoteSize)
            {
                _logger.LogInformation(
                    "[{Title}] Skipping central ban file push to {Path} — remote size changed during fetch ({Expected} -> {Current}); will retry next cycle after manual additions are ingested",
                    context.Title, resolvedPath.Path, currentRemoteSize, currentSizeBeforePush);
                return PushOutcome.NoPush(central.ETag, nowUtc);
            }

            try
            {
                central.Content.Seek(0, SeekOrigin.Begin);
                await ftp.UploadStream(central.Content, resolvedPath.Path, FtpRemoteExists.Overwrite, true, null, ct).ConfigureAwait(false);

                _scheduledPushes.TryRemove(context.ServerId, out _);
                _logger.LogInformation(
                    "[{Title}] Pushed central ban file ({Length} bytes, ETag {ETag}) to {Path}",
                    context.Title, central.Length, central.ETag, resolvedPath.Path);

                await RecordPushAsync(context, resolvedPath, central, ct).ConfigureAwait(false);

                return PushOutcome.Successful(central.ETag, nowUtc, central.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Title}] Failed to push central ban file to {Path}",
                    context.Title, resolvedPath.Path);
                return PushOutcome.NoPush(central.ETag, nowUtc);
            }
        }
    }

    /// <summary>
    /// Records a successful central-blob push as both a per-server <c>BanFilePushed</c>
    /// game-server event and an <c>Audit:BanFilePushed</c> audit. Failures here are
    /// logged and swallowed — they must not roll back the actual push.
    /// </summary>
    private async Task RecordPushAsync(
        ServerContext context,
        ResolvedBanFilePath resolvedPath,
        CentralBanFile central,
        CancellationToken ct)
    {
        var eventData = JsonSerializer.Serialize(new
        {
            FilePath = resolvedPath.Path,
            ResolvedForMod = resolvedPath.ResolvedForMod,
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
            _logger.LogWarning(ex, "[{Title}] Failed to write BanFilePushed game server event for {Path}",
                context.Title, resolvedPath.Path);
        }

        try
        {
            _auditLogger.LogAudit(AuditEvent.SystemAction("BanFilePushed", AuditAction.Export)
                .WithService("BanFileWatcher")
                .WithGameContext(context.GameType, context.ServerId)
                .WithProperty("FilePath", resolvedPath.Path)
                .WithProperty("ResolvedForMod", resolvedPath.ResolvedForMod ?? string.Empty)
                .WithProperty("Length", central.Length.ToString(CultureInfo.InvariantCulture))
                .WithProperty("ETag", central.ETag)
                .Build());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Title}] Failed to emit BanFilePushed audit for {Path}",
                context.Title, resolvedPath.Path);
        }
    }

    /// <summary>
    /// Returns cached per-tag counts when the remote file size matches the cached
    /// snapshot. On size change the file is re-counted via FTP read; this is
    /// inevitable on truncation/append cycles but skipped on the steady-state path.
    /// </summary>
    private async Task<TagCounts?> GetOrRecountAsync(
        ServerContext context,
        string remoteFilePath,
        long? remoteSize,
        CancellationToken ct)
    {
        if (!remoteSize.HasValue)
            return null;

        if (_countCache.TryGetValue(context.ServerId, out var cached) && cached.RemoteFileSize == remoteSize.Value)
            return cached.Counts;

        try
        {
            using var ftp = new AsyncFtpClient(context.FtpHostname, context.FtpUsername, context.FtpPassword, context.FtpPort);
            await ftp.Connect(ct).ConfigureAwait(false);
            try
            {
                string content;
                await using (var stream = await ftp.OpenRead(remoteFilePath, FtpDataType.Binary, 0, false, ct).ConfigureAwait(false))
                using (var reader = new StreamReader(stream))
                {
                    content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                }

                var counts = CountTags(content);
                _countCache[context.ServerId] = new CachedCounts(remoteSize.Value, counts);
                return counts;
            }
            finally
            {
                await ftp.Disconnect(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{Title}] Failed to recount ban file tags at {Path}",
                context.Title, remoteFilePath);
            return null;
        }
    }

    /// <summary>
    /// Counts lines per tag category. <c>Untagged</c> is "no known system tag in the
    /// line" — i.e. manual / RCON bans the admin added.
    /// </summary>
    internal static TagCounts CountTags(string content)
    {
        var total = 0;
        var untagged = 0;
        var bansync = 0;
        var external = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            total++;

            var hasBanSync = trimmed.Contains("[BANSYNC]", StringComparison.OrdinalIgnoreCase);
            var hasExternal = trimmed.Contains("[PBBAN]", StringComparison.OrdinalIgnoreCase)
                              || trimmed.Contains("[B3BAN]", StringComparison.OrdinalIgnoreCase)
                              || trimmed.Contains("[EXTERNAL]", StringComparison.OrdinalIgnoreCase);

            if (hasBanSync) bansync++;
            else if (hasExternal) external++;
            else untagged++;
        }

        return new TagCounts(total, untagged, bansync, external);
    }

    private async Task<BanFileMonitorDto?> GetExistingMonitorAsync(Guid serverId, CancellationToken ct)
    {
        try
        {
            var response = await _repositoryClient.BanFileMonitors.V1.GetBanFileMonitors(
                gameTypes: null,
                banFileMonitorIds: null,
                gameServerId: serverId,
                skipEntries: 0,
                takeEntries: 1,
                order: null,
                cancellationToken: ct).ConfigureAwait(false);

            if (!response.IsSuccess || response.Result?.Data?.Items is null)
                return null;

            return response.Result.Data.Items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch existing BanFileMonitor for server {ServerId} — treating as new", serverId);
            return null;
        }
    }

    private async Task UpsertStatusAsync(UpsertBanFileMonitorStatusDto dto, ServerContext context, CancellationToken ct)
    {
        try
        {
            var response = await _repositoryClient.BanFileMonitors.V1
                .UpsertBanFileMonitorStatus(dto, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                _logger.LogWarning(
                    "[{Title}] Failed to upsert BanFileMonitor status for server {ServerId}: status {StatusCode}",
                    context.Title, context.ServerId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Status writes are observability — failures must not break the watcher
            // loop. The next cycle will retry.
            _logger.LogWarning(ex, "[{Title}] Exception upserting BanFileMonitor status for server {ServerId}",
                context.Title, context.ServerId);
        }
    }

    private TimeSpan RandomJitter()
    {
        var ms = _jitterRng.NextInt64(0, (long)PushStaggerMaxJitter.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms);
    }

    private static bool IsFileNotFound(FtpException ex)
        => ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("550", StringComparison.Ordinal);

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

    /// <summary>Per-tag count snapshot for a remote ban file.</summary>
    internal sealed record TagCounts(int Total, int Untagged, int BanSync, int External);

    private sealed record CachedCounts(long RemoteFileSize, TagCounts Counts);

    private sealed record ScheduledPush(string CentralEtag, DateTime ScheduledFor);

    /// <summary>Outcome of <see cref="TryPushCentralBanFileAsync"/>.</summary>
    internal sealed record PushOutcome
    {
        public required bool Pushed { get; init; }
        public required string? CentralEtag { get; init; }
        public required DateTime? CentralEtagSeenAt { get; init; }
        public long? NewSize { get; init; }

        public static PushOutcome NoPush(string? centralEtag, DateTime? centralEtagSeenAt) => new()
        {
            Pushed = false,
            CentralEtag = centralEtag,
            CentralEtagSeenAt = centralEtagSeenAt
        };

        public static PushOutcome Successful(string centralEtag, DateTime centralEtagSeenAt, long newSize) => new()
        {
            Pushed = true,
            CentralEtag = centralEtag,
            CentralEtagSeenAt = centralEtagSeenAt,
            NewSize = newSize
        };
    }
}
