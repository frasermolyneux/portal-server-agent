using System.Reflection;
using System.Text;

using Moq;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.BanFiles;

public class SftpRemoteFileClientTests
{
    [Fact]
    public async Task CreateFileIfMissingAsync_PublishesCompleteValidatedSibling()
    {
        var files = new Dictionary<string, byte[]>();
        await using var client = CreateClient(files);
        using var content = new MemoryStream("central ban data"u8.ToArray());

        Assert.True(await client.CreateFileIfMissingAsync(content, "/Mods/test/ban.txt"));

        Assert.Equal(content.ToArray(), Assert.Single(files).Value);
        Assert.True(files.ContainsKey("/Mods/test/ban.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateFileIfMissingAsync_PreservesFileCreatedDuringUploadOrPublication(bool duringPublication)
    {
        const string path = "/Mods/test/ban.txt";
        var files = new Dictionary<string, byte[]>();
        void CreateManualFile()
        {
            files[path] = "manual ban"u8.ToArray();
        }
        await using var client = CreateClient(files,
            afterUpload: duringPublication ? null : CreateManualFile,
            beforeRename: duringPublication ? CreateManualFile : null);
        using var content = new MemoryStream("central ban data"u8.ToArray());

        Assert.False(await client.CreateFileIfMissingAsync(content, path));

        Assert.Equal("manual ban", Encoding.UTF8.GetString(Assert.Single(files).Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateFileIfMissingAsync_FailedOrIncompleteUploadDoesNotExposeLiveFile(bool uploadThrows)
    {
        var files = new Dictionary<string, byte[]>();
        await using var client = CreateClient(files, afterUpload: () =>
        {
            var stagedPath = Assert.Single(files).Key;
            files[stagedPath] = [1];
            if (uploadThrows)
            {
                throw new IOException("Interrupted upload");
            }
        });
        using var content = new MemoryStream("central ban data"u8.ToArray());

        await Assert.ThrowsAsync<IOException>(() => client.CreateFileIfMissingAsync(content, "/Mods/test/ban.txt"));

        Assert.Empty(files);
    }

    [Fact]
    public async Task CreateFileIfMissingAsync_CanceledUploadDoesNotPublishAndCleansStaging()
    {
        var files = new Dictionary<string, byte[]>();
        using var cancellation = new CancellationTokenSource();
        await using var client = CreateClient(files, afterUpload: cancellation.Cancel);
        using var content = new MemoryStream("central ban data"u8.ToArray());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CreateFileIfMissingAsync(content, "/Mods/test/ban.txt", cancellation.Token));

        Assert.Empty(files);
    }

    private static SftpRemoteFileClient CreateClient(
        Dictionary<string, byte[]> files, Action? afterUpload = null, Action? beforeRename = null)
    {
        var remote = new Mock<ISftpClient>();
        remote.Setup(c => c.Exists(It.IsAny<string>())).Returns((string path) => files.ContainsKey(path));
        remote.Setup(c => c.UploadFile(It.IsAny<Stream>(), It.IsAny<string>(), false, null))
            .Callback((Stream content, string path, bool _, Action<ulong>? _) =>
            {
                using var copy = new MemoryStream();
                content.CopyTo(copy);
                files.Add(path, copy.ToArray());
                afterUpload?.Invoke();
            });
        remote.Setup(c => c.GetAttributes(It.IsAny<string>())).Returns((string path) =>
            Assert.IsType<SftpFileAttributes>(Activator.CreateInstance(typeof(SftpFileAttributes),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                [DateTime.UtcNow, DateTime.UtcNow, (long)files[path].Length, 0, 0, 0u, null], null)));
        remote.Setup(c => c.RenameFile(It.IsAny<string>(), It.IsAny<string>(), false))
            .Callback((string source, string destination, bool _) =>
            {
                beforeRename?.Invoke();
                if (files.ContainsKey(destination))
                {
                    throw new SftpException(StatusCode.Failure);
                }
                files.Add(destination, files[source]);
                files.Remove(source);
            });
        remote.Setup(c => c.DeleteFile(It.IsAny<string>())).Callback((string path) => files.Remove(path));
        var context = CreateContext() with { FileTransportHostKeyFingerprint = "0123456789ABCDEF0123456789ABCDEF" };
        return new SftpRemoteFileClient(context, _ => remote.Object);
    }

    [Fact]
    public void SelectDirectoryName_PreservesExactCaseAndFindsUniqueAlternative()
    {
        Assert.Equal("Mods", SftpRemoteFileClient.SelectDirectoryName(["Mods"], "mods"));
        Assert.Equal("mods", SftpRemoteFileClient.SelectDirectoryName(["Mods", "mods"], "mods"));
        Assert.Throws<InvalidOperationException>(() =>
            SftpRemoteFileClient.SelectDirectoryName(["Mods", "MODS"], "mods"));
        Assert.Throws<RemoteFileNotFoundException>(() =>
            SftpRemoteFileClient.SelectDirectoryName(["main"], "mods"));
    }

    [Fact]
    public void Constructor_WhenHostKeyFingerprintMissing_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new SftpRemoteFileClient(CreateContext()));
    }

    private static ServerContext CreateContext()
    {
        return new ServerContext
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
    }
}
