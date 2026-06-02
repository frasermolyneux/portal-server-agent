using XtremeIdiots.Portal.Server.Agent.App.Screenshots;
using Xunit;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Screenshots;

[Trait("Category", "Unit")]
public class ScreenshotWatcherTests
{
    [Fact]
    public void ComputeFingerprint_IsDeterministicForSameInput()
    {
        var serverId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var timestamp = new DateTime(2025, 1, 1, 12, 34, 56, DateTimeKind.Utc);

        var first = ScreenshotWatcher.ComputeFingerprint(serverId, "shot001.jpg", 1234, timestamp);
        var second = ScreenshotWatcher.ComputeFingerprint(serverId, "shot001.jpg", 1234, timestamp);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void ComputeFingerprint_ChangesWhenSourceMetadataChanges()
    {
        var serverId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var timestamp = new DateTime(2025, 1, 1, 12, 34, 56, DateTimeKind.Utc);

        var first = ScreenshotWatcher.ComputeFingerprint(serverId, "shot001.jpg", 1234, timestamp);
        var second = ScreenshotWatcher.ComputeFingerprint(serverId, "shot002.jpg", 1234, timestamp);

        Assert.NotEqual(first, second);
    }
}
