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
}
