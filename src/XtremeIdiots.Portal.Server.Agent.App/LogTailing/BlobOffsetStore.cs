using System.Text.Json;

using Azure;
using Azure.Storage.Blobs;

using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Agent.App.LogTailing;

/// <summary>
/// Persists tailing offsets as JSON blobs in Azure Blob Storage.
/// Write failures are logged but never thrown — the tailer must not be interrupted by storage issues.
/// </summary>
public sealed class BlobOffsetStore : IOffsetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobOffsetStore> _logger;

    public BlobOffsetStore(BlobServiceClient blobServiceClient, ILogger<BlobOffsetStore> logger)
    {
        ArgumentNullException.ThrowIfNull(blobServiceClient);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _container = blobServiceClient.GetBlobContainerClient("tailer-offsets");
    }

    /// <inheritdoc />
    public async Task SaveOffsetAsync(Guid serverId, long offset, string filePath, CancellationToken ct = default)
    {
        try
        {
            var blobName = $"offsets/{serverId}.json";
            var blob = _container.GetBlobClient(blobName);

            var payload = new SavedOffset
            {
                Offset = offset,
                FilePath = filePath,
                SavedAtUtc = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

            await blob.UploadAsync(stream, overwrite: true, cancellationToken: ct);

            _logger.LogDebug("Saved offset {Offset} for server {ServerId} at {FilePath}",
                offset, serverId, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save offset for server {ServerId} — tailing will continue", serverId);
        }
    }

    /// <inheritdoc />
    public async Task<SavedOffset?> GetOffsetAsync(Guid serverId, CancellationToken ct = default)
    {
        try
        {
            var blobName = $"offsets/{serverId}.json";
            var blob = _container.GetBlobClient(blobName);

            var response = await blob.DownloadContentAsync(ct);
            var json = response.Value.Content.ToString();

            return JsonSerializer.Deserialize<SavedOffset>(json, JsonOptions);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("No saved offset found for server {ServerId} — starting from end of file", serverId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read offset for server {ServerId} — starting from end of file", serverId);
            return null;
        }
    }
}
