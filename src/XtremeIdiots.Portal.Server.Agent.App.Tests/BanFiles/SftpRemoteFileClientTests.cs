using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.BanFiles;

public class SftpRemoteFileClientTests
{
    [Fact]
    public void Constructor_WhenHostKeyFingerprintMissing_Throws()
    {
        var context = new ServerContext
        {
            ServerId = Guid.NewGuid(),
            GameType = "CallOfDuty4",
            Title = "SFTP Remote Client Test",
            FtpHostname = "ftp.example.com",
            FtpPort = 21,
            FtpUsername = "user",
            FtpPassword = "pass",
            FileTransportEnabled = true,
            FileTransportType = "sftp",
            FileTransportHostname = "sftp.example.com",
            FileTransportPort = 22,
            FileTransportUsername = "sftp-user",
            FileTransportPassword = "sftp-pass",
            FileTransportHostKeyFingerprint = null,
            LogFilePath = "/logs/games_mp.log",
            Hostname = "game.example.com",
            QueryPort = 28960,
            RconPassword = "secret",
            FtpEnabled = true,
            RconEnabled = true,
            BanFileSyncEnabled = true,
            BanFileRootPath = "/",
            ConfigHash = "sftp-client-test"
        };

        Assert.Throws<InvalidOperationException>(() => new SftpRemoteFileClient(context));
    }
}
