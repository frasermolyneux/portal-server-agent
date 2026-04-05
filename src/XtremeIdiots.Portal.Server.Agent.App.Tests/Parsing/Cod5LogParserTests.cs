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
    public void ParseLine_InitGame_ClearsSlotMap()
    {
        _parser.ParseLine("  1:00 J;283895439;0;Player1");
        Assert.Single(_parser.ConnectedPlayers);

        _parser.ParseLine(@"  2:00 InitGame: \mapname\mp_dome\g_gametype\tdm");

        Assert.Empty(_parser.ConnectedPlayers);
        Assert.Equal("mp_dome", _parser.CurrentMap);
    }
}
