using System.Collections.Concurrent;

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;

using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public class BlobServerLock : IServerLock
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobServerLock> _logger;
    private readonly ConcurrentDictionary<Guid, string> _leaseIds = new();

    public BlobServerLock(BlobServiceClient blobServiceClient, ILogger<BlobServerLock> logger)
    {
        _container = blobServiceClient.GetBlobContainerClient("server-locks");
        _logger = logger;
    }

    internal virtual BlobLeaseClient GetLeaseClient(BlobClient blobClient, string? leaseId = null)
        => blobClient.GetBlobLeaseClient(leaseId);

    public async Task<bool> TryAcquireAsync(Guid serverId, CancellationToken ct = default)
    {
        try
        {
            var blobClient = _container.GetBlobClient($"{serverId}.lock");

            // Ensure the blob exists (upload empty content if not)
            if (!await blobClient.ExistsAsync(ct))
            {
                await blobClient.UploadAsync(BinaryData.FromString(string.Empty), overwrite: false, ct);
            }

            var leaseClient = GetLeaseClient(blobClient);
            var lease = await leaseClient.AcquireAsync(TimeSpan.FromSeconds(30), cancellationToken: ct);

            _leaseIds[serverId] = lease.Value.LeaseId;
            _logger.LogInformation("Acquired lock for server {ServerId}, leaseId: {LeaseId}", serverId, lease.Value.LeaseId);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409) // Conflict — lease already held
        {
            _logger.LogWarning("Lock for server {ServerId} is held by another instance", serverId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to acquire lock for server {ServerId}", serverId);
            return false;
        }
    }

    public async Task<bool> RenewAsync(Guid serverId, CancellationToken ct = default)
    {
        if (!_leaseIds.TryGetValue(serverId, out var leaseId))
            return false;

        try
        {
            var blobClient = _container.GetBlobClient($"{serverId}.lock");
            var leaseClient = GetLeaseClient(blobClient, leaseId);
            await leaseClient.RenewAsync(cancellationToken: ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            _logger.LogWarning("Failed to renew lock for server {ServerId} — lease lost", serverId);
            _leaseIds.TryRemove(serverId, out _);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to renew lock for server {ServerId}", serverId);
            return false;
        }
    }

    public async Task ReleaseAsync(Guid serverId, CancellationToken ct = default)
    {
        if (!_leaseIds.TryRemove(serverId, out var leaseId))
            return;

        try
        {
            var blobClient = _container.GetBlobClient($"{serverId}.lock");
            var leaseClient = GetLeaseClient(blobClient, leaseId);
            await leaseClient.ReleaseAsync(cancellationToken: ct);
            _logger.LogInformation("Released lock for server {ServerId}", serverId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release lock for server {ServerId} — will expire naturally", serverId);
        }
    }
}
