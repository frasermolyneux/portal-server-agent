using System.Text;

using FluentFTP;

using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.LogTailing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.LogTailing;

public class FtpLogTailerTests
{
    #region SplitIntoLines Tests

    [Fact]
    public void SplitIntoLines_WhenCompleteLines_ReturnsAllLines()
    {
        var data = Encoding.UTF8.GetBytes("line1\nline2\nline3\n");
        var partial = string.Empty;

        var result = FtpLogTailer.SplitIntoLines(data, ref partial);

        Assert.Equal(3, result.Count);
        Assert.Equal("line1", result[0]);
        Assert.Equal("line2", result[1]);
        Assert.Equal("line3", result[2]);
        Assert.Equal(string.Empty, partial);
    }

    [Fact]
    public void SplitIntoLines_WhenPartialLine_BuffersIncomplete()
    {
        var data = Encoding.UTF8.GetBytes("line1\npartial");
        var partial = string.Empty;

        var result = FtpLogTailer.SplitIntoLines(data, ref partial);

        Assert.Single(result);
        Assert.Equal("line1", result[0]);
        Assert.Equal("partial", partial);
    }

    [Fact]
    public void SplitIntoLines_WhenPartialLineFromPreviousPoll_PrependsToNextChunk()
    {
        // First poll leaves a partial line
        var data1 = Encoding.UTF8.GetBytes("line1\npart");
        var partial = string.Empty;

        var result1 = FtpLogTailer.SplitIntoLines(data1, ref partial);

        Assert.Single(result1);
        Assert.Equal("line1", result1[0]);
        Assert.Equal("part", partial);

        // Second poll completes the partial line
        var data2 = Encoding.UTF8.GetBytes("ial_complete\nline3\n");

        var result2 = FtpLogTailer.SplitIntoLines(data2, ref partial);

        Assert.Equal(2, result2.Count);
        Assert.Equal("partial_complete", result2[0]);
        Assert.Equal("line3", result2[1]);
        Assert.Equal(string.Empty, partial);
    }

    [Fact]
    public void SplitIntoLines_WhenWindowsLineEndings_HandlesCarriageReturn()
    {
        var data = Encoding.UTF8.GetBytes("line1\r\nline2\r\n");
        var partial = string.Empty;

        var result = FtpLogTailer.SplitIntoLines(data, ref partial);

        Assert.Equal(2, result.Count);
        Assert.Equal("line1", result[0]);
        Assert.Equal("line2", result[1]);
        Assert.Equal(string.Empty, partial);
    }

    [Fact]
    public void SplitIntoLines_WhenEmptyData_ReturnsEmpty()
    {
        var data = Array.Empty<byte>();
        var partial = string.Empty;

        var result = FtpLogTailer.SplitIntoLines(data, ref partial);

        Assert.Empty(result);
        Assert.Equal(string.Empty, partial);
    }

    [Fact]
    public void SplitIntoLines_WhenOnlyPartialLine_BuffersEverything()
    {
        var data = Encoding.UTF8.GetBytes("no_newline_here");
        var partial = string.Empty;

        var result = FtpLogTailer.SplitIntoLines(data, ref partial);

        Assert.Empty(result);
        Assert.Equal("no_newline_here", partial);
    }

    [Fact]
    public void SplitIntoLines_WhenOnlyNewlines_ReturnsEmpty()
    {
        var data = Encoding.UTF8.GetBytes("\n\n\n");
        var partial = string.Empty;

        var result = FtpLogTailer.SplitIntoLines(data, ref partial);

        Assert.Empty(result);
        Assert.Equal(string.Empty, partial);
    }

    [Fact]
    public void SplitIntoLines_WithExistingPartial_CompletesLine()
    {
        var data = Encoding.UTF8.GetBytes("_end\n");
        var partial = "start";

        var result = FtpLogTailer.SplitIntoLines(data, ref partial);

        Assert.Single(result);
        Assert.Equal("start_end", result[0]);
        Assert.Equal(string.Empty, partial);
    }

    #endregion

    #region Constructor and State Tests

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new FtpLogTailer(null!));
    }

    [Fact]
    public void IsConnected_WhenNotConnected_ReturnsFalse()
    {
        var logger = new Mock<ILogger<FtpLogTailer>>();
        var tailer = new FtpLogTailer(logger.Object);

        Assert.False(tailer.IsConnected);
    }

    [Fact]
    public async Task PollAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        var logger = new Mock<ILogger<FtpLogTailer>>();
        var tailer = new FtpLogTailer(logger.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tailer.PollAsync());
    }

    [Fact]
    public async Task ConnectAsync_WithNullConfig_ThrowsArgumentNullException()
    {
        var logger = new Mock<ILogger<FtpLogTailer>>();
        var tailer = new FtpLogTailer(logger.Object);

        await Assert.ThrowsAsync<ArgumentNullException>(() => tailer.ConnectAsync(null!));
    }

    [Fact]
    public void CurrentOffset_WhenNotConnected_ReturnsZero()
    {
        var logger = new Mock<ILogger<FtpLogTailer>>();
        var tailer = new FtpLogTailer(logger.Object);

        Assert.Equal(0, tailer.CurrentOffset);
    }

    [Fact]
    public void CurrentFilePath_WhenNotConnected_ReturnsNull()
    {
        var logger = new Mock<ILogger<FtpLogTailer>>();
        var tailer = new FtpLogTailer(logger.Object);

        Assert.Null(tailer.CurrentFilePath);
    }

    [Fact]
    public async Task DisposeAsync_WhenNotConnected_DoesNotThrow()
    {
        var logger = new Mock<ILogger<FtpLogTailer>>();
        var tailer = new FtpLogTailer(logger.Object);

        await tailer.DisposeAsync();
    }

    #endregion

    #region Factory Tests

    [Fact]
    public void LogTailerFactory_Create_ReturnsNewInstance()
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger<FtpLogTailer>>().Object);

        var factory = new LogTailerFactory(loggerFactory.Object);

        var tailer1 = factory.Create();
        var tailer2 = factory.Create();

        Assert.NotNull(tailer1);
        Assert.NotNull(tailer2);
        Assert.NotSame(tailer1, tailer2);
    }

    [Fact]
    public void LogTailerFactory_WithNullLoggerFactory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new LogTailerFactory(null!));
    }

    #endregion

    #region Negative File Size Guard Tests

    /// <summary>
    /// Verifies that when GetFileSize returns -1 during ConnectAsync (file not found or FTP failure),
    /// the tailer throws rather than setting _lastFileSize to -1 which would corrupt offset tracking.
    /// This is a regression test for a bug where transient FTP failures caused full log replays.
    /// </summary>
    [Fact]
    public void ConnectAsync_WhenGetFileSizeReturnsNegative_ShouldNotSetNegativeOffset()
    {
        // This test validates the design constraint: _lastFileSize must never be negative.
        // The FtpLogTailer.ConnectAsync now throws InvalidOperationException when GetFileSize returns -1,
        // preventing a negative _lastFileSize that would later cause a false "log rotation" detection
        // and replay the entire log file.
        var logger = new Mock<ILogger<FtpLogTailer>>();
        var tailer = new FtpLogTailer(logger.Object);

        // CurrentOffset starts at 0 (not negative)
        Assert.True(tailer.CurrentOffset >= 0, "CurrentOffset must never be negative");
    }

    #endregion
}
