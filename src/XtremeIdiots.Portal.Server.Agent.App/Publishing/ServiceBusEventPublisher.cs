using System.Collections.Concurrent;
using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;

using Parsing = XtremeIdiots.Portal.Server.Agent.App.Parsing;
using SbEvents = XtremeIdiots.Portal.Server.Events.Abstractions.V1.Events;

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
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
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
    public async Task PublishAsync(Parsing.GameEvent gameEvent, Guid serverId, string gameType, long sequenceId, CancellationToken ct = default)
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
        string mapName, string gameName, IReadOnlyDictionary<int, Parsing.PlayerInfo> players,
        string? serverTitle, string? serverMod, int? maxPlayers,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var payload = new SbEvents.ServerStatusEvent
        {
            EventGeneratedUtc = now,
            EventPublishedUtc = now,
            ServerId = serverId,
            GameType = gameType,
            SequenceId = sequenceId,
            MapName = mapName,
            GameName = gameName,
            PlayerCount = players.Count,
            ServerTitle = serverTitle,
            ServerMod = serverMod,
            MaxPlayers = maxPlayers,
            Players = players.Values.Select(p => new SbEvents.ConnectedPlayer
            {
                PlayerGuid = p.Guid,
                Username = p.Name,
                IpAddress = p.IpAddress ?? string.Empty,
                SlotId = p.SlotId,
                ConnectedAtUtc = p.ConnectedAt,
                Score = p.Score,
                Ping = p.Ping,
                Rate = p.Rate
            }).ToArray()
        };

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        await SendAsync(Queues.ServerStatus, body, serverId, sequenceId, nameof(SbEvents.ServerStatusEvent), ct);
    }

    /// <inheritdoc />
    public async Task PublishServerConnectedAsync(
        Guid serverId, string gameType, long sequenceId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var payload = new SbEvents.ServerConnectedEvent
        {
            EventGeneratedUtc = now,
            EventPublishedUtc = now,
            ServerId = serverId,
            GameType = gameType,
            SequenceId = sequenceId
        };

        var body = JsonSerializer.Serialize(payload, JsonOptions);
        await SendAsync(Queues.ServerConnected, body, serverId, sequenceId, nameof(SbEvents.ServerConnectedEvent), ct);
    }

    /// <inheritdoc />
    public async Task PublishBanDetectedAsync(
        Guid serverId, string gameType, long sequenceId,
        IReadOnlyList<DetectedBanEntry> newBans, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var evt = new SbEvents.BanDetectedEvent
        {
            EventGeneratedUtc = now,
            EventPublishedUtc = now,
            ServerId = serverId,
            GameType = gameType,
            SequenceId = sequenceId,
            NewBans = newBans.Select(b => new SbEvents.DetectedBan
            {
                PlayerGuid = b.PlayerGuid,
                PlayerName = b.PlayerName
            }).ToList()
        };

        var body = JsonSerializer.Serialize(evt, JsonOptions);
        await SendAsync(Queues.BanFileChanged, body, serverId, sequenceId, nameof(SbEvents.BanDetectedEvent), ct);
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

    private (string? QueueName, string? Body) MapEvent(Parsing.GameEvent gameEvent, Guid serverId, string gameType, long sequenceId)
    {
        var now = DateTime.UtcNow;

        return gameEvent switch
        {
            Parsing.PlayerConnectedEvent e => (Queues.PlayerConnected, JsonSerializer.Serialize(new SbEvents.PlayerConnectedEvent
            {
                EventGeneratedUtc = e.Timestamp,
                EventPublishedUtc = now,
                ServerId = serverId,
                GameType = gameType,
                SequenceId = sequenceId,
                PlayerGuid = e.PlayerGuid,
                Username = e.Username,
                IpAddress = string.Empty, // IP resolved from RCON — placeholder
                SlotId = e.SlotId
            }, JsonOptions)),

            Parsing.PlayerDisconnectedEvent e => (Queues.PlayerDisconnected, JsonSerializer.Serialize(new SbEvents.PlayerDisconnectedEvent
            {
                EventGeneratedUtc = e.Timestamp,
                EventPublishedUtc = now,
                ServerId = serverId,
                GameType = gameType,
                SequenceId = sequenceId,
                PlayerGuid = e.PlayerGuid,
                Username = e.Username,
                SlotId = e.SlotId
            }, JsonOptions)),

            Parsing.ChatMessageEvent e => (Queues.ChatMessage, JsonSerializer.Serialize(new SbEvents.ChatMessageEvent
            {
                EventGeneratedUtc = e.Timestamp,
                EventPublishedUtc = now,
                ServerId = serverId,
                GameType = gameType,
                SequenceId = sequenceId,
                PlayerGuid = e.PlayerGuid,
                Username = e.Username,
                Message = e.Message,
                Type = e.IsTeamChat ? SbEvents.ChatMessageType.Team : SbEvents.ChatMessageType.All
            }, JsonOptions)),

            Parsing.MapChangeEvent e => (Queues.MapChange, JsonSerializer.Serialize(new SbEvents.MapChangeEvent
            {
                EventGeneratedUtc = e.Timestamp,
                EventPublishedUtc = now,
                ServerId = serverId,
                GameType = gameType,
                SequenceId = sequenceId,
                MapName = e.MapName,
                GameName = e.GameType
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
