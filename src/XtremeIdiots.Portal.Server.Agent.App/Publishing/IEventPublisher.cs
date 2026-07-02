using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Publishing;

/// <summary>
/// Publishes game events to Azure Service Bus queues.
/// </summary>
public interface IEventPublisher : IAsyncDisposable
{
    /// <summary>
    /// Publish a game event to the appropriate Service Bus queue.
    /// Maps the local GameEvent to the Service Bus DTO format and routes to the correct queue.
    /// </summary>
    Task PublishAsync(GameEvent gameEvent, Guid serverId, string gameType, long sequenceId, CancellationToken ct = default);

    /// <summary>
    /// Publish a periodic server status snapshot.
    /// </summary>
    Task PublishServerStatusAsync(Guid serverId, string gameType, long sequenceId,
        string mapName, string gameName, IReadOnlyDictionary<int, PlayerInfo> players,
        string? serverTitle, string? serverMod, int? maxPlayers,
        CancellationToken ct = default);

    /// <summary>
    /// Publish a server connected event (agent started monitoring this server).
    /// </summary>
    Task PublishServerConnectedAsync(Guid serverId, string gameType, long sequenceId,
        CancellationToken ct = default);

    /// <summary>
    /// Publish a ban detected event with new untagged bans found in the ban file.
    /// </summary>
    Task PublishBanDetectedAsync(Guid serverId, string gameType, long sequenceId,
        IReadOnlyList<DetectedBanEntry> newBans, CancellationToken ct = default);

    /// <summary>
    /// Publish an event when a ban has been successfully applied on the game server.
    /// </summary>
    Task PublishBanAppliedAsync(Guid serverId, string gameType, long sequenceId,
        string playerGuid, string playerName, bool isTemporary, DateTime? expiresUtc,
        string source, string reason, string? correlationId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Publish an event when an existing ban has been lifted on the game server.
    /// </summary>
    Task PublishBanLiftAppliedAsync(Guid serverId, string gameType, long sequenceId,
        string playerGuid, string playerName,
        string source, string liftReason, string? correlationId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Publish an event when a ban sync operation fails.
    /// </summary>
    Task PublishBanSyncFailedAsync(Guid serverId, string gameType, long sequenceId,
        string operation, string failureReason, string source,
        string? playerGuid = null, string? playerName = null, string? correlationId = null,
        CancellationToken ct = default);
}
