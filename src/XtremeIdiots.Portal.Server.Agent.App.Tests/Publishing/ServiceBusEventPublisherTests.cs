using System.Text.Json;

using Azure.Messaging.ServiceBus;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Publishing;

public class ServiceBusEventPublisherTests
{
    private static readonly Guid ServerId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string GameType = "CallOfDuty4";
    private const long SequenceId = 42;

    private readonly Mock<ServiceBusClient> _clientMock;
    private readonly Mock<ServiceBusSender> _senderMock;
    private readonly ServiceBusEventPublisher _publisher;

    public ServiceBusEventPublisherTests()
    {
        _clientMock = new Mock<ServiceBusClient>();
        _senderMock = new Mock<ServiceBusSender>();

        _clientMock
            .Setup(c => c.CreateSender(It.IsAny<string>()))
            .Returns(_senderMock.Object);

        _publisher = new ServiceBusEventPublisher(
            _clientMock.Object,
            NullLogger<ServiceBusEventPublisher>.Instance);
    }

    [Fact]
    public async Task PublishAsync_PlayerConnected_SendsToCorrectQueue()
    {
        var gameEvent = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc123",
            Username = "TestPlayer",
            SlotId = 1
        };

        await _publisher.PublishAsync(gameEvent, ServerId, GameType, SequenceId);

        _clientMock.Verify(c => c.CreateSender("player-connected"), Times.Once);
        _senderMock.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_PlayerDisconnected_SendsToCorrectQueue()
    {
        var gameEvent = new PlayerDisconnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc123",
            Username = "TestPlayer",
            SlotId = 1
        };

        await _publisher.PublishAsync(gameEvent, ServerId, GameType, SequenceId);

        _clientMock.Verify(c => c.CreateSender("player-disconnected"), Times.Once);
        _senderMock.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_ChatMessage_SerializesCorrectJson()
    {
        ServiceBusMessage? captured = null;
        _senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var gameEvent = new ChatMessageEvent
        {
            Timestamp = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc),
            PlayerGuid = "guid456",
            Username = "Chatter",
            Message = "Hello world",
            IsTeamChat = true
        };

        await _publisher.PublishAsync(gameEvent, ServerId, GameType, SequenceId);

        Assert.NotNull(captured);
        var json = captured!.Body.ToString();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("guid456", root.GetProperty("playerGuid").GetString());
        Assert.Equal("Chatter", root.GetProperty("username").GetString());
        Assert.Equal("Hello world", root.GetProperty("message").GetString());
        Assert.Equal("Team", root.GetProperty("type").GetString());
        Assert.Equal(ServerId, root.GetProperty("serverId").GetGuid());
        Assert.Equal(GameType, root.GetProperty("gameType").GetString());
        Assert.Equal(SequenceId, root.GetProperty("sequenceId").GetInt64());
    }

    [Fact]
    public async Task PublishAsync_ChatMessage_AllChat_TypeIsAll()
    {
        ServiceBusMessage? captured = null;
        _senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var gameEvent = new ChatMessageEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "guid456",
            Username = "Chatter",
            Message = "Hi",
            IsTeamChat = false
        };

        await _publisher.PublishAsync(gameEvent, ServerId, GameType, SequenceId);

        Assert.NotNull(captured);
        var json = captured!.Body.ToString();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("All", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task PublishAsync_MapChange_SerializesGameName()
    {
        ServiceBusMessage? captured = null;
        _senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var gameEvent = new MapChangeEvent
        {
            Timestamp = DateTime.UtcNow,
            MapName = "mp_crossfire",
            GameType = "tdm"
        };

        await _publisher.PublishAsync(gameEvent, ServerId, GameType, SequenceId);

        _clientMock.Verify(c => c.CreateSender("map-change"), Times.Once);

        Assert.NotNull(captured);
        var json = captured!.Body.ToString();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("mp_crossfire", root.GetProperty("mapName").GetString());
        Assert.Equal("tdm", root.GetProperty("gameName").GetString());
    }

    [Fact]
    public async Task PublishAsync_SetsMessageIdForDedup()
    {
        ServiceBusMessage? captured = null;
        _senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var gameEvent = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc",
            Username = "Player",
            SlotId = 0
        };

        await _publisher.PublishAsync(gameEvent, ServerId, GameType, SequenceId);

        Assert.NotNull(captured);
        Assert.Equal($"{ServerId}-{SequenceId}", captured!.MessageId);
    }

    [Fact]
    public async Task PublishAsync_SetsContentType()
    {
        ServiceBusMessage? captured = null;
        _senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var gameEvent = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc",
            Username = "Player",
            SlotId = 0
        };

        await _publisher.PublishAsync(gameEvent, ServerId, GameType, SequenceId);

        Assert.NotNull(captured);
        Assert.Equal("application/json", captured!.ContentType);
    }

    [Fact]
    public async Task PublishAsync_CachesSenders()
    {
        var event1 = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "a",
            Username = "P1",
            SlotId = 0
        };
        var event2 = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "b",
            Username = "P2",
            SlotId = 1
        };

        await _publisher.PublishAsync(event1, ServerId, GameType, 1);
        await _publisher.PublishAsync(event2, ServerId, GameType, 2);

        // CreateSender should be called only once for the same queue
        _clientMock.Verify(c => c.CreateSender("player-connected"), Times.Once);
        _senderMock.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PublishServerStatusAsync_IncludesPlayerList()
    {
        ServiceBusMessage? captured = null;
        _senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        var players = new Dictionary<int, PlayerInfo>
        {
            [0] = new PlayerInfo
            {
                Guid = "p1guid",
                Name = "Player1",
                SlotId = 0,
                ConnectedAt = DateTime.UtcNow.AddMinutes(-10)
            },
            [1] = new PlayerInfo
            {
                Guid = "p2guid",
                Name = "Player2",
                SlotId = 1,
                ConnectedAt = DateTime.UtcNow.AddMinutes(-5)
            }
        };

        await _publisher.PublishServerStatusAsync(
            ServerId, GameType, SequenceId, "mp_crash", "tdm", players,
            "My Test Server", "mods/mymod", 24);

        _clientMock.Verify(c => c.CreateSender("server-status"), Times.Once);

        Assert.NotNull(captured);
        var json = captured!.Body.ToString();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("mp_crash", root.GetProperty("mapName").GetString());
        Assert.Equal("tdm", root.GetProperty("gameName").GetString());
        Assert.Equal(2, root.GetProperty("playerCount").GetInt32());

        var playersArray = root.GetProperty("players");
        Assert.Equal(2, playersArray.GetArrayLength());

        var first = playersArray[0];
        Assert.Equal("p1guid", first.GetProperty("playerGuid").GetString());
        Assert.Equal("Player1", first.GetProperty("username").GetString());
        Assert.Equal(0, first.GetProperty("slotId").GetInt32());
    }

    [Fact]
    public async Task PublishServerConnectedAsync_SendsToCorrectQueue()
    {
        ServiceBusMessage? captured = null;
        _senderMock
            .Setup(s => s.SendMessageAsync(It.IsAny<ServiceBusMessage>(), It.IsAny<CancellationToken>()))
            .Callback<ServiceBusMessage, CancellationToken>((msg, _) => captured = msg)
            .Returns(Task.CompletedTask);

        await _publisher.PublishServerConnectedAsync(ServerId, GameType, SequenceId);

        _clientMock.Verify(c => c.CreateSender("server-connected"), Times.Once);

        Assert.NotNull(captured);
        var json = captured!.Body.ToString();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(ServerId, root.GetProperty("serverId").GetGuid());
        Assert.Equal(GameType, root.GetProperty("gameType").GetString());
        Assert.Equal(SequenceId, root.GetProperty("sequenceId").GetInt64());
    }

    [Fact]
    public async Task PublishAsync_UnknownEventType_DoesNotSend()
    {
        var unknownEvent = new UnknownTestEvent { Timestamp = DateTime.UtcNow };

        await _publisher.PublishAsync(unknownEvent, ServerId, GameType, SequenceId);

        _senderMock.Verify(s => s.SendMessageAsync(
            It.IsAny<ServiceBusMessage>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_DisposesAllSenders()
    {
        var sender1 = new Mock<ServiceBusSender>();
        var sender2 = new Mock<ServiceBusSender>();
        var callCount = 0;

        _clientMock
            .Setup(c => c.CreateSender(It.IsAny<string>()))
            .Returns(() => ++callCount == 1 ? sender1.Object : sender2.Object);

        // Trigger creation of two different senders
        await _publisher.PublishAsync(
            new PlayerConnectedEvent { Timestamp = DateTime.UtcNow, PlayerGuid = "a", Username = "P", SlotId = 0 },
            ServerId, GameType, 1);
        await _publisher.PublishAsync(
            new ChatMessageEvent { Timestamp = DateTime.UtcNow, PlayerGuid = "b", Username = "Q", Message = "hi", IsTeamChat = false },
            ServerId, GameType, 2);

        await _publisher.DisposeAsync();

        sender1.Verify(s => s.DisposeAsync(), Times.Once);
        sender2.Verify(s => s.DisposeAsync(), Times.Once);
    }

    /// <summary>
    /// A game event subtype unknown to the publisher, used to verify graceful skip behaviour.
    /// </summary>
    private sealed record UnknownTestEvent : GameEvent;
}
