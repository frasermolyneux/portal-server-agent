using XtremeIdiots.Portal.Server.Agent.App.Parsing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Parsing;

public sealed class Cod2LogParserTests
{
    private readonly Cod2LogParser _parser = new();

    [Fact]
    public void ParseLine_JoinEvent_AcceptsNumericGuid()
    {
        var result = _parser.ParseLine("  3:42 J;160913;2;PlayerName");

        var connected = Assert.IsType<PlayerConnectedEvent>(result);
        Assert.Equal("160913", connected.PlayerGuid);
        Assert.Equal("PlayerName", connected.Username);
        Assert.Equal(2, connected.SlotId);
    }

    [Fact]
    public void ParseLine_JoinEvent_AcceptsLongerNumericGuid()
    {
        var result = _parser.ParseLine("  3:42 J;12345678;5;LongGuidPlayer");

        var connected = Assert.IsType<PlayerConnectedEvent>(result);
        Assert.Equal("12345678", connected.PlayerGuid);
    }

    [Fact]
    public void ParseLine_InvalidGuid_TooShort_ReturnsNull()
    {
        // Less than 6 digits should be rejected
        var result = _parser.ParseLine("  3:42 J;12345;2;PlayerName");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_InvalidGuid_NonNumeric_ReturnsNull()
    {
        // CoD2 requires all-numeric GUIDs
        var result = _parser.ParseLine("  3:42 J;abcdef;2;PlayerName");

        Assert.Null(result);
    }

    [Fact]
    public void ParseLine_SayEvent_WorksWithNumericGuid()
    {
        var result = _parser.ParseLine("  3:42 say;160913;2;PlayerName;hello world");

        var chat = Assert.IsType<ChatMessageEvent>(result);
        Assert.Equal("160913", chat.PlayerGuid);
        Assert.Equal("hello world", chat.Message);
    }

    [Fact]
    public void ParseLine_QuitEvent_RemovesFromSlotMap()
    {
        _parser.ParseLine("  1:00 J;160913;2;PlayerName");
        Assert.Single(_parser.ConnectedPlayers);

        _parser.ParseLine("  1:30 Q;160913;2;PlayerName");
        Assert.Empty(_parser.ConnectedPlayers);
    }
}
