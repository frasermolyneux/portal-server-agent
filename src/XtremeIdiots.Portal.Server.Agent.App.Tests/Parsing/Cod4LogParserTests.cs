using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Parsing;

public sealed class Cod4LogParserTests
{
    private readonly Cod4LogParser _parser = new();

    [Fact]
    public void ParseLine_JoinEvent_ReturnsPlayerConnected()
    {
        var result = _parser.ParseLine("  3:42 J;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName");

        var connected = Assert.IsType<PlayerConnectedEvent>(result);
        Assert.Equal("e42b78c9b7b00bffe42b78c9b7b00bff", connected.PlayerGuid);
        Assert.Equal("PlayerName", connected.Username);
        Assert.Equal(2, connected.SlotId);
    }

    [Fact]
    public void ParseLine_QuitEvent_ReturnsPlayerDisconnected()
    {
        // First join so the player is tracked
        _parser.ParseLine("  3:42 J;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName");

        var result = _parser.ParseLine("  3:50 Q;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName");

        var disconnected = Assert.IsType<PlayerDisconnectedEvent>(result);
        Assert.Equal("e42b78c9b7b00bffe42b78c9b7b00bff", disconnected.PlayerGuid);
        Assert.Equal("PlayerName", disconnected.Username);
        Assert.Equal(2, disconnected.SlotId);
    }

    [Fact]
    public void ParseLine_SayEvent_ReturnsChatMessage()
    {
        var result = _parser.ParseLine("  3:42 say;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName;hello everyone");

        var chat = Assert.IsType<ChatMessageEvent>(result);
        Assert.Equal("e42b78c9b7b00bffe42b78c9b7b00bff", chat.PlayerGuid);
        Assert.Equal("PlayerName", chat.Username);
        Assert.Equal("hello everyone", chat.Message);
        Assert.False(chat.IsTeamChat);
    }

    [Fact]
    public void ParseLine_SayteamEvent_ReturnsChatMessageWithTeamFlag()
    {
        var result = _parser.ParseLine("  3:42 sayteam;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName;team message");

        var chat = Assert.IsType<ChatMessageEvent>(result);
        Assert.Equal("team message", chat.Message);
        Assert.True(chat.IsTeamChat);
    }

    [Fact]
    public void ParseLine_InitGame_ReturnsMapChange()
    {
        var result = _parser.ParseLine(@"  0:00 InitGame: \mapname\mp_crash\g_gametype\tdm\sv_hostname\TestServer");

        var mapChange = Assert.IsType<MapChangeEvent>(result);
        Assert.Equal("mp_crash", mapChange.MapName);
        Assert.Equal("tdm", mapChange.GameType);
    }

    [Fact]
    public void ParseLine_InitGame_ClearsSlotMap()
    {
        // Join two players
        _parser.ParseLine("  1:00 J;e42b78c9b7b00bffe42b78c9b7b00bff;0;Player1");
        _parser.ParseLine("  1:01 J;a42b78c9b7b00bffe42b78c9b7b00baa;1;Player2");
        Assert.Equal(2, _parser.ConnectedPlayers.Count);

        // InitGame clears the slot map
        _parser.ParseLine(@"  2:00 InitGame: \mapname\mp_crossfire\g_gametype\sd");

        Assert.Empty(_parser.ConnectedPlayers);
    }

    [Fact]
    public void ParseLine_SayLikeCommand_ReturnsMapVoteEvent()
    {
        // Set current map first
        _parser.ParseLine(@"  0:00 InitGame: \mapname\mp_crash\g_gametype\tdm");

        var result = _parser.ParseLine("  3:42 say;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName;!like");

        var vote = Assert.IsType<MapVoteEvent>(result);
        Assert.Equal("e42b78c9b7b00bffe42b78c9b7b00bff", vote.PlayerGuid);
        Assert.Equal("PlayerName", vote.Username);
        Assert.Equal("mp_crash", vote.MapName);
        Assert.True(vote.Like);
    }

    [Fact]
    public void ParseLine_SayDislikeCommand_ReturnsMapVoteEvent()
    {
        _parser.ParseLine(@"  0:00 InitGame: \mapname\mp_crash\g_gametype\tdm");

        var result = _parser.ParseLine("  3:42 say;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName;!dislike");

        var vote = Assert.IsType<MapVoteEvent>(result);
        Assert.Equal("mp_crash", vote.MapName);
        Assert.False(vote.Like);
    }

    [Fact]
    public void ParseLine_UnrecognisedLine_ReturnsNull()
    {
        var result = _parser.ParseLine("some random text that is not a log line");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_EmptyLine_ReturnsNull()
    {
        Assert.Null(_parser.ParseLine(""));
        Assert.Null(_parser.ParseLine("   "));
        Assert.Null(_parser.ParseLine(null!));
    }

    [Fact]
    public void ParseLine_SayWithControlCharacter_StripsChar21()
    {
        var result = _parser.ParseLine("  3:42 say;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName;\x15hello world");

        var chat = Assert.IsType<ChatMessageEvent>(result);
        Assert.Equal("hello world", chat.Message);
    }

    [Fact]
    public void ParseLine_JoinThenQuit_UpdatesSlotMap()
    {
        _parser.ParseLine("  1:00 J;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName");
        Assert.Single(_parser.ConnectedPlayers);
        Assert.True(_parser.ConnectedPlayers.ContainsKey(2));

        _parser.ParseLine("  1:30 Q;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName");
        Assert.Empty(_parser.ConnectedPlayers);
    }

    [Fact]
    public void ParseLine_InvalidGuid_ReturnsNull()
    {
        // CoD4 requires exactly 32 hex characters — short GUID should fail
        var result = _parser.ParseLine("  3:42 J;12345;2;PlayerName");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_InvalidGuid_NonHex_ReturnsNull()
    {
        // 32 chars but contains non-hex characters
        var result = _parser.ParseLine("  3:42 J;zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz;2;PlayerName");

        Assert.Null(result);
    }

    [Fact]
    public void ConnectedPlayers_AfterJoins_ReturnsCorrectCount()
    {
        _parser.ParseLine("  1:00 J;e42b78c9b7b00bffe42b78c9b7b00bff;0;Player1");
        _parser.ParseLine("  1:01 J;a42b78c9b7b00bffe42b78c9b7b00baa;1;Player2");
        _parser.ParseLine("  1:02 J;b42b78c9b7b00bffe42b78c9b7b00bcc;2;Player3");

        Assert.Equal(3, _parser.ConnectedPlayers.Count);

        var player1 = _parser.ConnectedPlayers[0];
        Assert.Equal("e42b78c9b7b00bffe42b78c9b7b00bff", player1.Guid);
        Assert.Equal("Player1", player1.Name);
        Assert.Equal(0, player1.SlotId);
    }

    [Fact]
    public void CurrentMap_AfterInitGame_ReturnsMapName()
    {
        Assert.Null(_parser.CurrentMap);

        _parser.ParseLine(@"  0:00 InitGame: \mapname\mp_backlot\g_gametype\dom");

        Assert.Equal("mp_backlot", _parser.CurrentMap);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        _parser.ParseLine(@"  0:00 InitGame: \mapname\mp_crash\g_gametype\tdm");
        _parser.ParseLine("  1:00 J;e42b78c9b7b00bffe42b78c9b7b00bff;0;Player1");
        Assert.NotNull(_parser.CurrentMap);
        Assert.NotEmpty(_parser.ConnectedPlayers);

        _parser.Reset();

        Assert.Null(_parser.CurrentMap);
        Assert.Empty(_parser.ConnectedPlayers);
    }

    [Fact]
    public void ParseLine_ShutdownGame_ReturnsNull()
    {
        var result = _parser.ParseLine("  5:00 ShutdownGame:");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_ExitLevel_ReturnsNull()
    {
        var result = _parser.ParseLine("  5:00 ExitLevel: executed");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_LikeWithNoMap_ReturnsChatMessage()
    {
        // No InitGame has been parsed, so CurrentMap is null — !like should be treated as chat
        var result = _parser.ParseLine("  3:42 say;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName;!like");

        var chat = Assert.IsType<ChatMessageEvent>(result);
        Assert.Equal("!like", chat.Message);
    }

    [Fact]
    public void ParseLine_SayteamLikeCommand_ReturnsMapVoteEvent()
    {
        _parser.ParseLine(@"  0:00 InitGame: \mapname\mp_crash\g_gametype\tdm");

        var result = _parser.ParseLine("  3:42 sayteam;e42b78c9b7b00bffe42b78c9b7b00bff;2;PlayerName;!like");

        Assert.IsType<MapVoteEvent>(result);
    }
}
