using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Parsing;

public sealed class Cod5LogParserTests
{
    private readonly Cod5LogParser _parser = new();

    [Fact]
    public void ParseLine_JoinEvent_Accepts8DigitGuid()
    {
        var result = _parser.ParseLine("  3:42 J;283895439;2;PlayerName");

        var connected = Assert.IsType<PlayerConnectedEvent>(result);
        Assert.Equal("283895439", connected.PlayerGuid);
        Assert.Equal("PlayerName", connected.Username);
        Assert.Equal(2, connected.SlotId);
    }

    [Fact]
    public void ParseLine_InvalidGuid_TooShort_ReturnsNull()
    {
        // Less than 8 digits should be rejected for CoD5
        var result = _parser.ParseLine("  3:42 J;1234567;2;PlayerName");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_JoinTeamEvent_UpdatesSlotMap()
    {
        // First join the player normally
        _parser.ParseLine("  1:00 J;283895439;2;PlayerName");
        Assert.Single(_parser.ConnectedPlayers);

        // JT for an already-known player should not add a duplicate
        var result = _parser.ParseLine("  1:05 JT;283895439;2;axis;PlayerName;");

        Assert.Null(result); // No event emitted for team change
        Assert.Single(_parser.ConnectedPlayers); // Still just one player
    }

    [Fact]
    public void ParseLine_JoinTeamEvent_ForNewPlayer_CreatesPlayer()
    {
        // JT for an unknown player should add them to the slot map
        var result = _parser.ParseLine("  1:00 JT;283895439;2;allies;PlayerName;");

        var connected = Assert.IsType<PlayerConnectedEvent>(result);
        Assert.Equal("283895439", connected.PlayerGuid);
        Assert.Equal("PlayerName", connected.Username);
        Assert.Equal(2, connected.SlotId);
        Assert.Single(_parser.ConnectedPlayers);
    }

    [Fact]
    public void ParseLine_JoinTeamEvent_InvalidGuid_ReturnsNull()
    {
        var result = _parser.ParseLine("  1:00 JT;12345;2;allies;PlayerName;");

        Assert.Null(result);
        Assert.Empty(_parser.ConnectedPlayers);
    }

    [Fact]
    public void ParseLine_SayEvent_WorksWithCod5Guid()
    {
        var result = _parser.ParseLine("  3:42 say;283895439;2;PlayerName;hello world");

        var chat = Assert.IsType<ChatMessageEvent>(result);
        Assert.Equal("283895439", chat.PlayerGuid);
        Assert.Equal("hello world", chat.Message);
    }

    [Fact]
    public void ParseLine_SayEvent_EpochTimestamp_ParsesCorrectly()
    {
        var result = _parser.ParseLine("1775927970 say;239040859;10;[>XI<]legi_istra;hi all");

        var chat = Assert.IsType<ChatMessageEvent>(result);
        Assert.Equal("239040859", chat.PlayerGuid);
        Assert.Equal("[>XI<]legi_istra", chat.Username);
        Assert.Equal("hi all", chat.Message);
        Assert.False(chat.IsTeamChat);
    }

    [Fact]
    public void ParseLine_SayEvent_EpochTimestamp_MultipleMessages()
    {
        var result1 = _parser.ParseLine("1775927974 say;631496496;9;WillieG;hi");
        var result2 = _parser.ParseLine("1775927989 say;239040859;10;[>XI<]legi_istra;not work for me :(");
        var result3 = _parser.ParseLine("1775928016 say;396900053;8;[>XI<]wingnut-MOD;CL_MAXPACKETS 100 ?");

        var chat1 = Assert.IsType<ChatMessageEvent>(result1);
        Assert.Equal("631496496", chat1.PlayerGuid);
        Assert.Equal("hi", chat1.Message);

        var chat2 = Assert.IsType<ChatMessageEvent>(result2);
        Assert.Equal("not work for me :(", chat2.Message);

        var chat3 = Assert.IsType<ChatMessageEvent>(result3);
        Assert.Equal("CL_MAXPACKETS 100 ?", chat3.Message);
    }

    [Fact]
    public void ParseLine_KillEvent_EpochTimestamp_DoesNotThrow()
    {
        // Kill/Damage lines with epoch timestamps should be silently consumed
        var result = _parser.ParseLine("1775927923 K;396900053;8;axis;[>XI<]wingnut-MOD;1463873444;6;allies;-Taino-;svt40_flash_mp;32;MOD_RIFLE_BULLET;right_leg_upper");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_JoinEvent_EpochTimestamp_ParsesCorrectly()
    {
        var result = _parser.ParseLine("1775928018 J;239040859;10;[>XI<]legi_istra");

        var connected = Assert.IsType<PlayerConnectedEvent>(result);
        Assert.Equal("239040859", connected.PlayerGuid);
        Assert.Equal("[>XI<]legi_istra", connected.Username);
        Assert.Equal(10, connected.SlotId);
    }

    [Fact]
    public void ParseLine_JoinTeamEvent_EpochTimestamp_ParsesCorrectly()
    {
        var result = _parser.ParseLine("1775927956 JT;239040859;10;allies;[>XI<]legi_istra;");

        var connected = Assert.IsType<PlayerConnectedEvent>(result);
        Assert.Equal("239040859", connected.PlayerGuid);
        Assert.Equal("[>XI<]legi_istra", connected.Username);
    }

    [Fact]
    public void ParseLine_InitGame_EpochTimestamp_ParsesCorrectly()
    {
        var result = _parser.ParseLine(@"1775928018 InitGame: \_Admin\Ruggerxi\_Location\USA\mapname\mp_waw_caen\g_gametype\ftag\sv_hostname\^1>XI< ^3OW FreezeTag\sv_maxclients\30\fs_game\mods/xi_owft");

        var mapChange = Assert.IsType<MapChangeEvent>(result);
        Assert.Equal("mp_waw_caen", mapChange.MapName);
        Assert.Equal("ftag", mapChange.GameType);
    }

    [Fact]
    public void ParseLine_InitGame_ClearsSlotMap()
    {
        _parser.ParseLine("  1:00 J;283895439;0;Player1");
        Assert.Single(_parser.ConnectedPlayers);

        _parser.ParseLine(@"  2:00 InitGame: \mapname\mp_dome\g_gametype\tdm");

        Assert.Empty(_parser.ConnectedPlayers);
        Assert.Equal("mp_dome", _parser.CurrentMap);
    }
}
