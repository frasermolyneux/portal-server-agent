using System.Collections.Concurrent;
using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Publishing;

/// <summary>
/// Publishes game events to Azure Service Bus queues using <see cref="ServiceBusClient"/>.
/// Caches <see cref="ServiceBusSender"/> instances per queue for reuse.
/// </summary>
public sealed class ServiceBusEventPublisher : IEventPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusEventPublisher> _logger;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();

    public ServiceBusEventPublisher(ServiceBusClient client, ILogger<ServiceBusEventPublisher> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task PublishAsync(GameEvent gameEvent, Guid serverId, string gameType, long sequenceId, CancellationToken ct = default)
    {
        var (queueName, body) = MapEvent(gameEvent, serverId, gameType, sequenceId);

        if (queueName is null || body is null)
        {
            _logger.LogDebug("Skipping unmapped event type {EventType}", gameEvent.GetType().Name);
            return;
        }

        await SendAsync(queueName, body, serverId, sequenceId, gameEvent.GetType().Name, ct);
    }

    /// <inheritdoc />
    public async Task PublishServerStatusAsync(
        Guid serverId, string gameType, long sequenceId,
        string mapName, string gameName, IReadOnlyDictionary<int, PlayerInfo> players,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var payload = new
        {
            eventGeneratedUtc = now,
            eventPublishedUtc = now,
            serverId,
            gameType,
            sequenceId,
            mapName,
            gameName,
            playerCount = players.Count,
            players = players.Values.Select(p => new
            {
                playerGuid = p.Guid,
                username = p.Name,
                ipAddress = string.Empty, // IP resolved from RCON — placeholder
                slotId = p.SlotId,
                connectedAtUtc = p.ConnectedAt
            }).ToArray()
        };

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        await SendAsync(QueueNames.ServerStatus, body, serverId, sequenceId, nameof(ServerStatusEvent), ct);
    }

    /// <inheritdoc />
    public async Task PublishServerConnectedAsync(
        Guid serverId, string gameType, long sequenceId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var payload = new
        {
            eventGeneratedUtc = now,
            eventPublishedUtc = now,
            serverId,
            gameType,
            sequenceId
        };

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        await SendAsync(QueueNames.ServerConnected, body, serverId, sequenceId, nameof(ServerConnectedEvent), ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
        {
            await sender.DisposeAsync();
        }

        _senders.Clear();
    }

    // Placeholder type names for logging (avoids referencing Abstractions project)
    private static class ServerStatusEvent { }
    private static class ServerConnectedEvent { }

    private (string? QueueName, string? Body) MapEvent(GameEvent gameEvent, Guid serverId, string gameType, long sequenceId)
    {
        var now = DateTime.UtcNow;

        return gameEvent switch
        {
            PlayerConnectedEvent e => (QueueNames.PlayerConnected, JsonSerializer.Serialize(new
            {
                eventGeneratedUtc = e.Timestamp,
                eventPublishedUtc = now,
                serverId,
                gameType,
                sequenceId,
                playerGuid = e.PlayerGuid,
                username = e.Username,
                ipAddress = string.Empty, // IP resolved from RCON — placeholder
                slotId = e.SlotId
            }, JsonOptions)),

            PlayerDisconnectedEvent e => (QueueNames.PlayerDisconnected, JsonSerializer.Serialize(new
            {
                eventGeneratedUtc = e.Timestamp,
                eventPublishedUtc = now,
                serverId,
                gameType,
                sequenceId,
                playerGuid = e.PlayerGuid,
                username = e.Username,
                slotId = e.SlotId
            }, JsonOptions)),

            ChatMessageEvent e => (QueueNames.ChatMessage, JsonSerializer.Serialize(new
            {
                eventGeneratedUtc = e.Timestamp,
                eventPublishedUtc = now,
                serverId,
                gameType,
                sequenceId,
                playerGuid = e.PlayerGuid,
                username = e.Username,
                message = e.Message,
                type = e.IsTeamChat ? "Team" : "All"
            }, JsonOptions)),

            MapVoteEvent e => (QueueNames.MapVote, JsonSerializer.Serialize(new
            {
                eventGeneratedUtc = e.Timestamp,
                eventPublishedUtc = now,
                serverId,
                gameType,
                sequenceId,
                playerGuid = e.PlayerGuid,
                mapName = e.MapName,
                like = e.Like
            }, JsonOptions)),

            MapChangeEvent e => (QueueNames.MapChange, JsonSerializer.Serialize(new
            {
                eventGeneratedUtc = e.Timestamp,
                eventPublishedUtc = now,
                serverId,
                gameType,
                sequenceId,
                mapName = e.MapName,
                gameName = e.GameType
            }, JsonOptions)),

            _ => (null, null)
        };
    }

    private async Task SendAsync(string queueName, string body, Guid serverId, long sequenceId, string eventTypeName, CancellationToken ct)
    {
        var sender = _senders.GetOrAdd(queueName, name => _client.CreateSender(name));

        var message = new ServiceBusMessage(BinaryData.FromString(body))
        {
            ContentType = "application/json",
            MessageId = $"{serverId}-{sequenceId}"
        };

        await sender.SendMessageAsync(message, ct);

        _logger.LogDebug("Published {EventType} to {Queue} for server {ServerId}", eventTypeName, queueName, serverId);
    }
}
