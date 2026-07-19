using System.Reflection;
using System.Text;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using MX.Observability.ApplicationInsights.Auditing;

using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.BanFileMonitors;
using XtremeIdiots.Portal.Repository.Api.Client.Testing;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.BanFiles;

public class BanFileWatcherTests
{
    [Fact]
    public async Task TryPushCentralBanFileAsync_EqualETagAndSettledImport_DoesNotReadOrUpload()
    {
        const string centralEtag = "central-etag";
        var serverId = Guid.NewGuid();
        var remoteClient = new Mock<IRemoteFileClient>();
        var watcher = CreateWatcher(CreateBanFileSource(centralEtag, "canonical-guid Player\n"));
        var lastPushUtc = DateTime.UtcNow;
        var monitor = CreateMonitor(serverId, centralEtag, lastPushUtc.AddMinutes(-1), lastPushUtc);

        var outcome = await watcher.TryPushCentralBanFileAsync(
            remoteClient.Object,
            CreateServerContext(serverId),
            monitor,
            CreateResolvedPath(),
            42,
            new BanFileWatcher.LaneKey(serverId),
            CancellationToken.None);

        Assert.False(outcome.Pushed);
        remoteClient.Verify(
            client => client.OpenReadAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
        remoteClient.Verify(
            client => client.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryPushCentralBanFileAsync_EqualETagFromPreviousPath_UploadsCanonicalFile()
    {
        const string centralEtag = "central-etag";
        const long remoteSize = 42;
        var serverId = Guid.NewGuid();
        var remoteClient = new Mock<IRemoteFileClient>();
        remoteClient
            .Setup(client => client.GetFileSizeAsync("/main/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(remoteSize);
        remoteClient
            .Setup(client => client.UploadAsync(It.IsAny<Stream>(), "/main/ban.txt", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var watcher = CreateWatcher(CreateBanFileSource(centralEtag, "canonical-guid Canonical Player\n"));
        var lastPushUtc = DateTime.UtcNow;
        var monitor = CreateMonitor(
            serverId,
            centralEtag,
            lastPushUtc.AddMinutes(-1),
            lastPushUtc,
            "/previous/ban.txt");

        var outcome = await watcher.TryPushCentralBanFileAsync(
            remoteClient.Object,
            CreateServerContext(serverId),
            monitor,
            CreateResolvedPath(),
            remoteSize,
            new BanFileWatcher.LaneKey(serverId),
            CancellationToken.None);

        Assert.True(outcome.Pushed);
        remoteClient.Verify(
            client => client.UploadAsync(It.IsAny<Stream>(), "/main/ban.txt", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TryPushCentralBanFileAsync_EqualETagAndRemoteOnlyGuid_DoesNotUpload()
    {
        const string centralEtag = "central-etag";
        var serverId = Guid.NewGuid();
        var remoteClient = new Mock<IRemoteFileClient>();
        remoteClient
            .Setup(client => client.OpenReadAsync("/main/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateStream("remote-only-guid Remote Player\n"));
        var watcher = CreateWatcher(CreateBanFileSource(centralEtag, "canonical-guid Canonical Player\n"));
        var lastPushUtc = DateTime.UtcNow.AddMinutes(-1);
        var monitor = CreateMonitor(serverId, centralEtag, DateTime.UtcNow, lastPushUtc);

        var outcome = await watcher.TryPushCentralBanFileAsync(
            remoteClient.Object,
            CreateServerContext(serverId),
            monitor,
            CreateResolvedPath(),
            42,
            new BanFileWatcher.LaneKey(serverId),
            CancellationToken.None);

        Assert.False(outcome.Pushed);
        remoteClient.Verify(
            client => client.OpenReadAsync("/main/ban.txt", 0, It.IsAny<CancellationToken>()),
            Times.Once);
        remoteClient.Verify(
            client => client.GetFileSizeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        remoteClient.Verify(
            client => client.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryPushCentralBanFileAsync_EqualETagAndCanonicalCoverage_UploadsCanonicalFile()
    {
        const string centralEtag = "central-etag";
        const string canonicalContent = "canonical-guid Canonical Player\n";
        const long remoteSize = 42;
        var serverId = Guid.NewGuid();
        string? uploadedContent = null;
        var remoteClient = new Mock<IRemoteFileClient>();
        remoteClient
            .Setup(client => client.OpenReadAsync("/main/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateStream("canonical-guid Remote Player\n"));
        remoteClient
            .Setup(client => client.GetFileSizeAsync("/main/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(remoteSize);
        remoteClient
            .Setup(client => client.UploadAsync(It.IsAny<Stream>(), "/main/ban.txt", It.IsAny<CancellationToken>()))
            .Returns(async (Stream stream, string _, CancellationToken cancellationToken) =>
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                uploadedContent = await reader.ReadToEndAsync(cancellationToken);
            });
        var watcher = CreateWatcher(CreateBanFileSource(centralEtag, canonicalContent));
        var lastPushUtc = DateTime.UtcNow.AddMinutes(-1);
        var monitor = CreateMonitor(serverId, centralEtag, DateTime.UtcNow, lastPushUtc);

        var outcome = await watcher.TryPushCentralBanFileAsync(
            remoteClient.Object,
            CreateServerContext(serverId),
            monitor,
            CreateResolvedPath(),
            remoteSize,
            new BanFileWatcher.LaneKey(serverId),
            CancellationToken.None);

        Assert.True(outcome.Pushed);
        Assert.Equal(canonicalContent, uploadedContent);
        remoteClient.Verify(
            client => client.OpenReadAsync("/main/ban.txt", 0, It.IsAny<CancellationToken>()),
            Times.Once);
        remoteClient.Verify(
            client => client.GetFileSizeAsync("/main/ban.txt", It.IsAny<CancellationToken>()),
            Times.Once);
        remoteClient.Verify(
            client => client.UploadAsync(It.IsAny<Stream>(), "/main/ban.txt", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("/previous/ban.txt")]
    [InlineData(null)]
    public async Task CheckAsync_PathChangeWithEqualETag_RetainsScheduleAndUploadsOnNextCycle(
        string? previousPath)
    {
        const string centralEtag = "central-etag";
        const string canonicalContent = "canonical-guid Canonical Player\n";
        var serverId = Guid.NewGuid();
        var lastPushUtc = DateTime.UtcNow.AddMinutes(-1);
        var monitor = CreateMonitor(
            serverId,
            centralEtag,
            DateTime.UtcNow,
            lastPushUtc,
            previousPath);
        var repositoryClient = new FakeRepositoryApiClient();
        repositoryClient.BanFileMonitorsApi.AddBanFileMonitor(monitor);

        string? uploadedContent = null;
        var remoteClient = new Mock<IRemoteFileClient>();
        remoteClient
            .Setup(client => client.GetFileSizeAsync("/main/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        remoteClient
            .Setup(client => client.OpenReadAsync("/main/ban.txt", It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(string.Empty));
        remoteClient
            .Setup(client => client.UploadAsync(It.IsAny<Stream>(), "/main/ban.txt", It.IsAny<CancellationToken>()))
            .Returns(async (Stream stream, string _, CancellationToken cancellationToken) =>
            {
                using var reader = new StreamReader(stream, leaveOpen: true);
                uploadedContent = await reader.ReadToEndAsync(cancellationToken);
            });

        var pathResolver = new Mock<IBanFilePathResolver>();
        pathResolver
            .Setup(value => value.Resolve("CallOfDuty5", "/", It.IsAny<string?>()))
            .Returns(CreateResolvedPath());
        var watcher = new BanFileWatcher(
            repositoryClient,
            CreateBanFileSource(centralEtag, canonicalContent),
            pathResolver.Object,
            new PassThroughRemoteOpsSessionCoordinator(remoteClient.Object),
            Mock.Of<IAuditLogger>(),
            NullLogger<BanFileWatcher>.Instance,
            new OneMillisecondJitterRandom());
        var context = CreateServerContext(serverId);

        var firstResult = await watcher.CheckAsync(context, liveMod: null, CancellationToken.None);

        Assert.Empty(firstResult.NewBans);
        remoteClient.Verify(
            client => client.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var firstPersistedResponse = await repositoryClient.BanFileMonitorsApi.GetBanFileMonitor(
            monitor.BanFileMonitorId,
            CancellationToken.None);
        var firstPersisted = firstPersistedResponse.Result!.Data!;
        Assert.Equal("/main/ban.txt", firstPersisted.RemoteFilePath);
        Assert.Equal(string.Empty, firstPersisted.LastPushedETag);

        await Task.Delay(TimeSpan.FromMilliseconds(20));

        var secondResult = await watcher.CheckAsync(context, liveMod: null, CancellationToken.None);

        Assert.Empty(secondResult.NewBans);
        Assert.Equal(canonicalContent, uploadedContent);
        remoteClient.Verify(
            client => client.UploadAsync(It.IsAny<Stream>(), "/main/ban.txt", It.IsAny<CancellationToken>()),
            Times.Once);
    }

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

    private static BanFileWatcher CreateWatcher(IBanFileSource banFileSource) => new(
        Mock.Of<IRepositoryApiClient>(),
        banFileSource,
        Mock.Of<IBanFilePathResolver>(),
        Mock.Of<IRemoteOpsSessionCoordinator>(),
        Mock.Of<IAuditLogger>(),
        NullLogger<BanFileWatcher>.Instance,
        new ZeroJitterRandom());

    private static IBanFileSource CreateBanFileSource(string etag, string content)
    {
        var source = new Mock<IBanFileSource>();
        source
            .Setup(value => value.GetAsync("CallOfDuty5", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateCentralBanFile(etag, content));
        return source.Object;
    }

    private static CentralBanFile CreateCentralBanFile(string etag, string content)
    {
        var stream = CreateStream(content);
        return new CentralBanFile { ETag = etag, Length = stream.Length, Content = stream };
    }

    private static MemoryStream CreateStream(string content) => new(Encoding.UTF8.GetBytes(content));

    private static ResolvedBanFilePath CreateResolvedPath() => new() { Path = "/main/ban.txt" };

    private static ServerContext CreateServerContext(Guid serverId) => new()
    {
        ServerId = serverId,
        GameType = "CallOfDuty5",
        Title = "Test COD5 server",
        FtpHostname = "localhost",
        FtpPort = 21,
        FtpUsername = "test",
        FtpPassword = "test",
        LogFilePath = "/main/games_mp.log",
        Hostname = "localhost",
        QueryPort = 28960,
        RconPassword = string.Empty,
        FtpEnabled = true,
        RconEnabled = false,
        BanFileSyncEnabled = true,
        BanFileRootPath = "/",
        ConfigHash = "test"
    };

    private static BanFileMonitorDto CreateMonitor(
        Guid serverId,
        string lastPushedEtag,
        DateTime lastImportUtc,
        DateTime lastPushUtc,
        string? remoteFilePath = "/main/ban.txt")
    {
        var monitor = RepositoryDtoFactory.CreateBanFileMonitor(gameServerId: serverId);
        SetInternalProperty(monitor, nameof(BanFileMonitorDto.LastPushedETag), lastPushedEtag);
        SetInternalProperty(monitor, nameof(BanFileMonitorDto.LastImportUtc), lastImportUtc);
        SetInternalProperty(monitor, nameof(BanFileMonitorDto.LastPushUtc), lastPushUtc);
        SetInternalProperty(monitor, nameof(BanFileMonitorDto.RemoteFilePath), remoteFilePath);
        return monitor;
    }

    private static void SetInternalProperty<T>(BanFileMonitorDto monitor, string propertyName, T value)
    {
        var property = typeof(BanFileMonitorDto).GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public);

        Assert.NotNull(property);
        property.SetValue(monitor, value);
    }

    private sealed class ZeroJitterRandom : Random
    {
        public override long NextInt64(long minValue, long maxValue) => minValue;
    }

    private sealed class OneMillisecondJitterRandom : Random
    {
        public override long NextInt64(long minValue, long maxValue) => 1;
    }

    private sealed class PassThroughRemoteOpsSessionCoordinator(
        IRemoteFileClient remoteClient) : IRemoteOpsSessionCoordinator
    {
        public Task<T> ExecuteAsync<T>(
            ServerContext context,
            Func<IRemoteFileClient, CancellationToken, Task<T>> operation,
            CancellationToken ct = default)
            => operation(remoteClient, ct);

        public Task ExecuteAsync(
            ServerContext context,
            Func<IRemoteFileClient, CancellationToken, Task> operation,
            CancellationToken ct = default)
            => operation(remoteClient, ct);

        public Task CloseServerSessionAsync(Guid serverId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}

