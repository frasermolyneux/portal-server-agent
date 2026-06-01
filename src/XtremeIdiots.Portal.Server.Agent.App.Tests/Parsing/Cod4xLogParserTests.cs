using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Parsing;

public sealed class Cod4xLogParserTests
{
    private const string ValidPlayerId = "2310346616629847491";
    private const string AnotherValidPlayerId = "2310346615375639652";

    private readonly Cod4xLogParser _parser = new();

    [Fact]
    public void ParseLine_JoinEvent_With19DigitPlayerId_ReturnsPlayerConnected()
    {
        var result = _parser.ParseLine($"  3:42 J;{ValidPlayerId};0;Bloodyrbeye>XI<");

        var connected = Assert.IsType<PlayerConnectedEvent>(result);
        Assert.Equal(ValidPlayerId, connected.PlayerGuid);
        Assert.Equal("Bloodyrbeye>XI<", connected.Username);
        Assert.Equal(0, connected.SlotId);
    }

    [Fact]
    public void ParseLine_JoinEvent_WithSentinelPlayerIdZero_ReturnsNull()
    {
        // CoD4x emits a literal "0" as the playerid when identity is unknown — drop it.
        var result = _parser.ParseLine("  3:42 J;0;5;Blaze>XI<MOD");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_JoinEvent_WithAllZero19DigitPlayerId_ReturnsNull()
    {
        var result = _parser.ParseLine("  3:42 J;0000000000000000000;5;Blaze>XI<MOD");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_JoinEvent_WithNonNumericPlayerId_ReturnsNull()
    {
        // CoD4 32-hex GUID would be invalid in a CoD4x log.
        var result = _parser.ParseLine("  3:42 J;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_QuitEvent_With19DigitPlayerId_ReturnsPlayerDisconnected()
    {
        // First join so the player is tracked.
        _parser.ParseLine($"  3:42 J;{ValidPlayerId};2;PlayerName");

        var result = _parser.ParseLine($"  3:50 Q;{ValidPlayerId};2;PlayerName");

        var disconnected = Assert.IsType<PlayerDisconnectedEvent>(result);
        Assert.Equal(ValidPlayerId, disconnected.PlayerGuid);
        Assert.Equal("PlayerName", disconnected.Username);
        Assert.Equal(2, disconnected.SlotId);
    }

    [Fact]
    public void ParseLine_SayEvent_ReturnsChatMessage()
    {
        var result = _parser.ParseLine($"  3:42 say;{AnotherValidPlayerId};3;iBoomBoom>XI<A;message text");

        var chat = Assert.IsType<ChatMessageEvent>(result);
        Assert.Equal(AnotherValidPlayerId, chat.PlayerGuid);
        Assert.Equal("iBoomBoom>XI<A", chat.Username);
        Assert.Equal(3, chat.SlotId);
        Assert.Equal("message text", chat.Message);
        Assert.False(chat.IsTeamChat);
    }

    [Fact]
    public void ParseLine_SayteamEvent_ReturnsChatMessageWithTeamFlag()
    {
        var result = _parser.ParseLine($"  3:42 sayteam;{AnotherValidPlayerId};3;iBoomBoom>XI<A;team msg");

        var chat = Assert.IsType<ChatMessageEvent>(result);
        Assert.Equal("team msg", chat.Message);
        Assert.True(chat.IsTeamChat);
    }

    [Fact]
    public void ParseLine_InitGame_ReturnsMapChange()
    {
        var result = _parser.ParseLine(
            @"  0:00 InitGame: \mapname\mp_atp\g_gametype\ftag\sv_hostname\TestServer");

        var mapChange = Assert.IsType<MapChangeEvent>(result);
        Assert.Equal("mp_atp", mapChange.MapName);
        Assert.Equal("ftag", mapChange.GameType);
    }

    [Fact]
    public void ParseLine_InitGame_ClearsSlotMap()
    {
        _parser.ParseLine($"  1:00 J;{ValidPlayerId};0;Player1");
        _parser.ParseLine($"  1:01 J;{AnotherValidPlayerId};1;Player2");
        Assert.Equal(2, _parser.ConnectedPlayers.Count);

        _parser.ParseLine(@"  2:00 InitGame: \mapname\mp_atp\g_gametype\ftag");

        Assert.Empty(_parser.ConnectedPlayers);
    }

    [Fact]
    public void ParseLine_JoinInfoLineWithCorruptGuid_ReturnsNull()
    {
        // CoD4x "JOIN:" info line is dispatched as a server event but only InitGame is handled,
        // so all other server events (including JOIN:/COD4X_PLAYER_JOIN:) silently no-op.
        var result = _parser.ParseLine("  3:42 JOIN: SomeName | GUID: l\uFFFD\uFFFD");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_Cod4xPlayerJoinInfoLine_ReturnsNull()
    {
        var result = _parser.ParseLine(
            $"  3:42 COD4X_PLAYER_JOIN: SomeName | GUID: {ValidPlayerId}");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_UnrecognisedLine_ReturnsNull()
    {
        Assert.Null(_parser.ParseLine("some random text that is not a log line"));
    }

    [Fact]
    public void ParseLine_EmptyLine_ReturnsNull()
    {
        Assert.Null(_parser.ParseLine(""));
        Assert.Null(_parser.ParseLine("   "));
        Assert.Null(_parser.ParseLine(null!));
    }
}
