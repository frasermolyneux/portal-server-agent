using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.LogTailing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.LogTailing;

public class SftpLogTailerTests
{
    [Fact]
    public async Task ConnectAsync_WhenHostKeyFingerprintMissing_Throws()
    {
        var logger = new Mock<ILogger<SftpLogTailer>>();
        var tailer = new SftpLogTailer(logger.Object);

        var config = new FileTransportTailerConfig
        {
            TransportType = "sftp",
            Hostname = "sftp.example.com",
            Port = 22,
            Username = "user",
            Password = "pass",
            HostKeyFingerprint = null,
            FilePath = "/logs/games_mp.log"
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => tailer.ConnectAsync(config));
    }
}
