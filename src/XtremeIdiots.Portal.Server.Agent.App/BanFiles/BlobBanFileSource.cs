using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

/// <summary>
/// Reads the central <c>{GameType}-bans.txt</c> blob produced by portal-sync's
/// <c>GenerateLatestBansFile</c> timer trigger. The blob is regenerated every 10 minutes
/// from the active bans in the repository.
/// </summary>
public sealed class BlobBanFileSource : IBanFileSource
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobBanFileSource> _logger;

    public BlobBanFileSource(
        IOptions<BanFileStorageOptions> options,
        ILogger<BlobBanFileSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.BlobEndpoint))
            throw new InvalidOperationException(
                $"{BanFileStorageOptions.SectionName}:BlobEndpoint is not configured");
        if (string.IsNullOrWhiteSpace(opts.ContainerName))
            throw new InvalidOperationException(
                $"{BanFileStorageOptions.SectionName}:ContainerName is not configured");

        // The ban-files container lives on portal-sync's storage account, separate from the
        // agent's own offset/lock storage, so build a dedicated BlobServiceClient. Uses the
        // same DefaultAzureCredential flow as the rest of the app (managed identity in Azure).
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
        });
        var serviceClient = new BlobServiceClient(new Uri(opts.BlobEndpoint), credential);
        _container = serviceClient.GetBlobContainerClient(opts.ContainerName);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CentralBanFile?> GetAsync(string gameType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameType))
            throw new ArgumentException("Game type is required", nameof(gameType));

        var blobKey = $"{gameType}-bans.txt";
        var blob = _container.GetBlobClient(blobKey);

        try
        {
            // Download content + ETag in a single atomic response so a portal-sync regeneration
            // mid-fetch can never produce a (content, ETag) mismatch that would cause us to
            // mark a newer ETag as pushed while actually delivering older content.
            var response = await blob.DownloadContentAsync(ct).ConfigureAwait(false);
            var bytes = response.Value.Content.ToArray();

            return new CentralBanFile
            {
                ETag = response.Value.Details.ETag.ToString(),
                Length = bytes.LongLength,
                Content = new MemoryStream(bytes, writable: false)
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("Central ban file {BlobKey} does not exist yet", blobKey);
            return null;
        }
    }
}

