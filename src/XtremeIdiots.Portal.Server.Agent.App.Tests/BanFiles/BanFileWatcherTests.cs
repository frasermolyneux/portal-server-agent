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
        Assert.Empty(result.MonitorUpdates);
    }

    [Fact]
    public void ShouldPush_FirstTimeForMonitor_ReturnsTrue()
    {
        var watcher = CreateWatcher();
        var monitorId = Guid.NewGuid();

        Assert.True(watcher.ShouldPush(monitorId, "etag-1"));
    }

    [Fact]
    public void ShouldPush_AfterMarkPushed_SameEtag_ReturnsFalse()
    {
        var watcher = CreateWatcher();
        var monitorId = Guid.NewGuid();
        watcher.MarkPushed(monitorId, "etag-1");

        Assert.False(watcher.ShouldPush(monitorId, "etag-1"));
    }

    [Fact]
    public void ShouldPush_AfterMarkPushed_DifferentEtag_ReturnsTrue()
    {
        var watcher = CreateWatcher();
        var monitorId = Guid.NewGuid();
        watcher.MarkPushed(monitorId, "etag-1");

        Assert.True(watcher.ShouldPush(monitorId, "etag-2"));
    }

    [Fact]
    public void ShouldPush_PerMonitorTracking_IsIndependent()
    {
        var watcher = CreateWatcher();
        var monitorA = Guid.NewGuid();
        var monitorB = Guid.NewGuid();
        watcher.MarkPushed(monitorA, "etag-1");

        // Same etag on a different monitor should still trigger a push (each monitor is independent)
        Assert.True(watcher.ShouldPush(monitorB, "etag-1"));
        // And the original monitor should still consider that etag pushed
        Assert.False(watcher.ShouldPush(monitorA, "etag-1"));
    }

    [Fact]
    public void CentralBanFile_Dispose_DisposesUnderlyingStream()
    {
        var stream = new MemoryStream([0x01, 0x02]);
        var central = new CentralBanFile { ETag = "x", Length = 2, Content = stream };

        central.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Length);
    }

    private static BanFileWatcher CreateWatcher()
    {
        var repoClient = new Mock<XtremeIdiots.Portal.Repository.Api.Client.V1.IRepositoryApiClient>();
        var source = new Mock<IBanFileSource>();
        var auditLogger = new Mock<MX.Observability.ApplicationInsights.Auditing.IAuditLogger>();
        var logger = new Mock<ILogger<BanFileWatcher>>();
        return new BanFileWatcher(repoClient.Object, source.Object, auditLogger.Object, logger.Object);
    }
}
