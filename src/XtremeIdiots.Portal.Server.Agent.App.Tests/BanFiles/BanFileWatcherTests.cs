using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.BanFiles;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.BanFiles;

public class BanFileWatcherTests
{
    [Fact]
    public void ParseBanLines_ValidLines_ReturnsEntries()
    {
        var content = """
            abc123 TestPlayer
            def456 Another Player
            """;

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("abc123", result[0].PlayerGuid);
        Assert.Equal("TestPlayer", result[0].PlayerName);
        Assert.Equal("def456", result[1].PlayerGuid);
        Assert.Equal("Another Player", result[1].PlayerName);
    }

    [Fact]
    public void ParseBanLines_SkipsTaggedLines_PBBAN()
    {
        var content = """
            abc123 TestPlayer [PBBAN]
            def456 CleanPlayer
            """;

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Single(result);
        Assert.Equal("def456", result[0].PlayerGuid);
    }

    [Fact]
    public void ParseBanLines_SkipsTaggedLines_B3BAN()
    {
        var content = "abc123 TestPlayer [B3BAN]\ndef456 GoodPlayer";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Single(result);
        Assert.Equal("def456", result[0].PlayerGuid);
    }

    [Fact]
    public void ParseBanLines_SkipsTaggedLines_BANSYNC()
    {
        var content = "abc123 TestPlayer [BANSYNC]\ndef456 GoodPlayer";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Single(result);
        Assert.Equal("def456", result[0].PlayerGuid);
    }

    [Fact]
    public void ParseBanLines_SkipsTaggedLines_EXTERNAL()
    {
        var content = "abc123 TestPlayer [EXTERNAL]\ndef456 GoodPlayer";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Single(result);
        Assert.Equal("def456", result[0].PlayerGuid);
    }

    [Fact]
    public void ParseBanLines_SkipsTaggedLines_CaseInsensitive()
    {
        var content = "abc123 TestPlayer [pbban]\ndef456 GoodPlayer";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Single(result);
        Assert.Equal("def456", result[0].PlayerGuid);
    }

    [Fact]
    public void ParseBanLines_SkipsEmptyLines()
    {
        var content = "\nabc123 TestPlayer\n\n\ndef456 Another\n";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseBanLines_SkipsLinesWithoutSpace()
    {
        var content = "abc123\ndef456 ValidPlayer";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Single(result);
        Assert.Equal("def456", result[0].PlayerGuid);
    }

    [Fact]
    public void ParseBanLines_SkipsMalformedGuid_TooShort()
    {
        var content = "a PlayerName\ndef456 ValidPlayer";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Single(result);
        Assert.Equal("def456", result[0].PlayerGuid);
    }

    [Fact]
    public void ParseBanLines_SkipsLineWithBlankName()
    {
        var content = "abc123   \ndef456 ValidPlayer";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Single(result);
        Assert.Equal("def456", result[0].PlayerGuid);
    }

    [Fact]
    public void ParseBanLines_HandlesWindowsLineEndings()
    {
        var content = "abc123 Player1\r\ndef456 Player2\r\n";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Equal(2, result.Count);
        Assert.Equal("abc123", result[0].PlayerGuid);
        Assert.Equal("def456", result[1].PlayerGuid);
    }

    [Fact]
    public void ParseBanLines_EmptyContent_ReturnsEmpty()
    {
        var result = BanFileWatcher.ParseBanLines("");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseBanLines_WhitespaceOnly_ReturnsEmpty()
    {
        var result = BanFileWatcher.ParseBanLines("   \n  \n  ");

        Assert.Empty(result);
    }

    [Fact]
    public void ParseBanLines_PlayerNameWithSpaces_CapturesFullName()
    {
        var content = "abc123 Player With Many Spaces";

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Single(result);
        Assert.Equal("abc123", result[0].PlayerGuid);
        Assert.Equal("Player With Many Spaces", result[0].PlayerName);
    }

    [Fact]
    public void ParseBanLines_MixedTaggedAndUntagged_ReturnsOnlyUntagged()
    {
        var content = """
            abc001 Player1 [PBBAN]
            abc002 Player2
            abc003 Player3 [B3BAN]
            abc004 Player4
            abc005 Player5 [BANSYNC]
            abc006 Player6 [EXTERNAL]
            abc007 Player7
            """;

        var result = BanFileWatcher.ParseBanLines(content);

        Assert.Equal(3, result.Count);
        Assert.Equal("abc002", result[0].PlayerGuid);
        Assert.Equal("abc004", result[1].PlayerGuid);
        Assert.Equal("abc007", result[2].PlayerGuid);
    }

    [Fact]
    public void BanFileCheckResult_Empty_HasNoItems()
    {
        var result = BanFileCheckResult.Empty;

        Assert.Empty(result.NewBans);
        Assert.Null(result.ImportAcknowledgment);
    }

    [Fact]
    public void CountTags_MixedTags_GroupsCorrectly()
    {
        var content = """
            abc001 ManualOne
            abc002 ManualTwo
            abc003 BanSyncOne [BANSYNC]-Player3
            abc004 PbBan [PBBAN]
            abc005 B3Ban [B3BAN]
            abc006 ExternalOne [EXTERNAL]
            abc007 BanSyncTwo [BANSYNC]-Player7
            """;

        var counts = BanFileWatcher.CountTags(content);

        Assert.Equal(7, counts.Total);
        Assert.Equal(2, counts.Untagged);
        Assert.Equal(2, counts.BanSync);
        Assert.Equal(3, counts.External);
    }

    [Fact]
    public void CountTags_EmptyAndWhitespaceLines_AreIgnored()
    {
        var content = "\n\nabc001 OnlyOne\n   \n";

        var counts = BanFileWatcher.CountTags(content);

        Assert.Equal(1, counts.Total);
        Assert.Equal(1, counts.Untagged);
    }

    [Fact]
    public void CountTags_TagComparisonIsCaseInsensitive()
    {
        var content = "abc001 P [bansync]-foo\nabc002 Q [pbBan]";

        var counts = BanFileWatcher.CountTags(content);

        Assert.Equal(2, counts.Total);
        Assert.Equal(1, counts.BanSync);
        Assert.Equal(1, counts.External);
        Assert.Equal(0, counts.Untagged);
    }

    [Fact]
    public void CentralBanFile_Dispose_DisposesUnderlyingStream()
    {
        var stream = new MemoryStream([0x01, 0x02]);
        var central = new CentralBanFile { ETag = "x", Length = 2, Content = stream };

        central.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Length);
    }
}

