using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Screenshots;

using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;

namespace XtremeIdiots.Portal.Server.Agent.App.Screenshots;

public sealed class ScreenshotWatcher : IScreenshotWatcher
{
    private const string SourceName = "agent-monitor";
    private const int MaxProcessedFingerprintsPerServer = 10_000;

    private readonly IRemoteFileClientFactory _remoteFileClientFactory;
    private readonly IRepositoryScreenshotsClient _repositoryScreenshotsClient;
    private readonly ILogger<ScreenshotWatcher> _logger;
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _processedFingerprints = new();

    public ScreenshotWatcher(
        IRemoteFileClientFactory remoteFileClientFactory,
        IRepositoryScreenshotsClient repositoryScreenshotsClient,
        ILogger<ScreenshotWatcher> logger)
    {
        _remoteFileClientFactory = remoteFileClientFactory ?? throw new ArgumentNullException(nameof(remoteFileClientFactory));
        _repositoryScreenshotsClient = repositoryScreenshotsClient ?? throw new ArgumentNullException(nameof(repositoryScreenshotsClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task CheckAsync(ServerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Screenshots.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(context.Screenshots.DirectoryPath))
        {
            _logger.LogWarning("[{Title}] Screenshots enabled but directoryPath is empty", context.Title);
            return;
        }

        var processed = _processedFingerprints.GetOrAdd(context.ServerId, _ => new ConcurrentDictionary<string, byte>());

        int scanned = 0;
        int uploaded = 0;
        int metadataWritten = 0;
        int duplicatesSkipped = 0;
        int failures = 0;

        try
        {
            await using var remoteClient = _remoteFileClientFactory.Create(context);
            await remoteClient.ConnectAsync(ct).ConfigureAwait(false);

            IReadOnlyList<RemoteFileEntry> remoteFiles;
            try
            {
                remoteFiles = await remoteClient.ListFilesAsync(context.Screenshots.DirectoryPath, ct).ConfigureAwait(false);
            }
            catch (RemoteFileNotFoundException)
            {
                _logger.LogWarning("[{Title}] Screenshot directory {DirectoryPath} not found", context.Title, context.Screenshots.DirectoryPath);
                return;
            }

            var matchingFiles = remoteFiles
                .Where(file => FileSystemName.MatchesSimpleExpression(context.Screenshots.FilePattern, file.Name, ignoreCase: true))
                .OrderBy(file => file.LastWriteUtc)
                .ToList();

            foreach (var file in matchingFiles)
            {
                scanned++;

                EnsureFingerprintCacheWithinLimit(context, processed);

                var fingerprint = ComputeFingerprint(context.ServerId, file.Name, file.Size, file.LastWriteUtc);
                if (processed.ContainsKey(fingerprint))
                {
                    duplicatesSkipped++;
                    continue;
                }

                try
                {
                    var playerIdentifier = ResolvePlayerIdentifier(file.Name);
                    var temporaryFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

                    try
                    {
                        await using (var content = await remoteClient.OpenReadAsync(file.Path, 0, ct).ConfigureAwait(false))
                        await using (var fileStream = System.IO.File.Create(temporaryFilePath))
                        {
                            await content.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                        }

                        var uploadResult = await _repositoryScreenshotsClient.UploadScreenshotAsync(
                            new UploadScreenshotDto
                            {
                                GameServerId = context.ServerId,
                                GameType = context.GameType,
                                PlayerIdentifier = playerIdentifier,
                                PlayerName = null,
                                CapturedUtc = file.LastWriteUtc,
                                Source = SourceName,
                                Fingerprint = fingerprint,
                                SourceFileName = file.Name,
                                SourceSizeBytes = file.Size,
                                SourceLastWriteUtc = file.LastWriteUtc
                            },
                            temporaryFilePath,
                            ct).ConfigureAwait(false);

                        if (uploadResult != RepositoryScreenshotUploadResult.Success)
                        {
                            failures++;

                            if (uploadResult == RepositoryScreenshotUploadResult.PermanentFailure)
                            {
                                processed.TryAdd(fingerprint, 0);
                            }

                            _logger.LogWarning(
                                "[{Title}] Screenshot upload failed for remote file {RemotePath} ({GameServerId}) with outcome {UploadOutcome}",
                                context.Title,
                                file.Path,
                                context.ServerId,
                                uploadResult);
                            continue;
                        }

                        uploaded++;
                        metadataWritten++;
                        processed.TryAdd(fingerprint, 0);
                    }
                    finally
                    {
                        TryDeleteTemporaryFile(context, temporaryFilePath);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    _logger.LogWarning(ex, "[{Title}] Failed processing screenshot file {RemotePath}", context.Title, file.Path);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Title}] Screenshot monitor cycle failed", context.Title);
            return;
        }

        _logger.LogInformation(
            "[{Title}] Screenshot monitor cycle complete: scanned={Scanned}, uploaded={Uploaded}, metadataWritten={MetadataWritten}, duplicatesSkipped={DuplicatesSkipped}, failures={Failures}",
            context.Title,
            scanned,
            uploaded,
            metadataWritten,
            duplicatesSkipped,
            failures);
    }

    internal static string ComputeFingerprint(Guid gameServerId, string sourceFileName, long sourceSizeBytes, DateTime sourceLastWriteUtc)
    {
        var normalizedTimestamp = sourceLastWriteUtc.ToUniversalTime();
        var source = $"{gameServerId}|{sourceFileName}|{sourceSizeBytes}|{normalizedTimestamp:O}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string ResolvePlayerIdentifier(string sourceFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(sourceFileName);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return "unknown";
        }

        foreach (var token in stem.Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.All(char.IsDigit))
            {
                return token;
            }
        }

        return "unknown";
    }

    private void EnsureFingerprintCacheWithinLimit(ServerContext context, ConcurrentDictionary<string, byte> processed)
    {
        if (processed.Count < MaxProcessedFingerprintsPerServer)
        {
            return;
        }

        processed.Clear();
        _logger.LogInformation(
            "[{Title}] Cleared screenshot fingerprint cache after reaching {MaxCount} entries",
            context.Title,
            MaxProcessedFingerprintsPerServer);
    }

    private void TryDeleteTemporaryFile(ServerContext context, string temporaryFilePath)
    {
        if (!System.IO.File.Exists(temporaryFilePath))
        {
            return;
        }

        try
        {
            System.IO.File.Delete(temporaryFilePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{Title}] Failed to delete temporary screenshot file {TemporaryFilePath}", context.Title, temporaryFilePath);
        }
    }

}
