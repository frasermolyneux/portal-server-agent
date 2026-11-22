namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Distributed lock ensuring only one agent instance connects to a given game server.
/// Uses Azure Blob Storage leases for coordination.
/// </summary>
public interface IServerLock
{
    /// <summary>
    /// Try to acquire an exclusive lock for the specified server.
    /// Returns true if the lock was acquired, false if another instance holds it.
    /// </summary>
    Task<bool> TryAcquireAsync(Guid serverId, CancellationToken ct = default);

    /// <summary>
    /// Renew the lock lease. Must be called before the lease expires (every ~15 seconds for a 30-second lease).
    /// </summary>
    Task<bool> RenewAsync(Guid serverId, CancellationToken ct = default);

    /// <summary>
    /// Gets the currently held lease ID for storage operations on the lock blob.
    /// The storage service validates it again when the operation is performed.
    /// </summary>
    bool TryGetLeaseId(Guid serverId, out string leaseId);

    /// <summary>
    /// Release the lock, allowing other instances to acquire it.
    /// </summary>
    Task ReleaseAsync(Guid serverId, CancellationToken ct = default);
}
