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
using XtremeIdiots.Portal.Server.Agent.App.Observability;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.BanFiles;

public class BanFileWatcherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedFilesAreRepairedEvenWhenTheCentralVersionIsUnchanged(bool uploadFails)
    {
        const string content = "central-guid [BANSYNC]-Player\n";
        var remote = new Mock<IRemoteFileClient>();
        remote.Setup(client => client.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/mods/test");
        remote.Setup(client => client.GetFileSizeAsync("/mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        remote.Setup(client => client.OpenReadAsync("/mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(""));
        if (uploadFails)
        {
            remote.Setup(client => client.UploadAsync(It.IsAny<Stream>(), "/mods/test/ban.txt", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Repair upload failed."));
        }
        var health = new AgentProcessHealth();
        var (watcher, context, repository) = CreateCheckWatcher(remote.Object, content, health);
        var monitor = CreateMonitor(context.ServerId, "etag", DateTime.UtcNow.AddHours(-2),
            DateTime.UtcNow.AddHours(-1));
        SetInternalProperty<DateTime?>(monitor, nameof(BanFileMonitorDto.LastImportUtc), null);
        SetInternalProperty(monitor, nameof(BanFileMonitorDto.RemoteFileSize), (long?)Encoding.UTF8.GetByteCount(content));
        repository.BanFileMonitorsApi.AddBanFileMonitor(monitor);

        await watcher.CheckAsync(context, "test");

        remote.Verify(client => client.UploadAsync(It.IsAny<Stream>(), "/mods/test/ban.txt", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(!uploadFails, health.BanChecks[context.ServerId].Success);
        Assert.Equal(uploadFails ? "degraded" : "operational",
            health.Evaluate(new(DateTimeOffset.UtcNow, [new(context.ServerId, true, true)]), true, DateTimeOffset.UtcNow).Status);
    }

    [Fact]
    public async Task UnacknowledgedBansAreRetriedWithoutAdvancingTheirOffsetOrResettingTheirDeadline()
    {
        const string content = "pending-guid Manual Ban\n";
        var remote = new Mock<IRemoteFileClient>();
        remote.Setup(client => client.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/mods/test");
        remote.Setup(client => client.GetFileSizeAsync("/mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetByteCount(content));
        remote.Setup(client => client.OpenReadAsync("/mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(content));
        var health = new AgentProcessHealth();
        var (watcher, context, repository) = CreateCheckWatcher(remote.Object, processHealth: health);

        var first = await watcher.CheckAsync(context, "test");
        Assert.Single(first.NewBans);
        var oldest = health.BanChecks[context.ServerId].PendingImportSince;
        var rows = await repository.BanFileMonitorsApi.GetBanFileMonitors(null, null, context.ServerId, 0, 1, null);
        Assert.Equal(0, Assert.Single(rows.Result!.Data!.Items!).RemoteFileSize);

        var retry = await watcher.CheckAsync(context, "test");
        Assert.Equal(first.NewBans, retry.NewBans);
        Assert.Equal(oldest, health.BanChecks[context.ServerId].PendingImportSince);
        await watcher.AcknowledgeImportAsync(context.ServerId, retry.ImportAcknowledgment!);
        rows = await repository.BanFileMonitorsApi.GetBanFileMonitors(null, null, context.ServerId, 0, 1, null);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), Assert.Single(rows.Result!.Data!.Items!).RemoteFileSize);
        Assert.Empty((await watcher.CheckAsync(context, "test")).NewBans);
        Assert.Equal(oldest, health.BanChecks[context.ServerId].PendingImportSince);
        remote.Verify(client => client.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UploadFailureCannotBeRecordedAsSuccessfulNoChange()
    {
        var remote = new Mock<IRemoteFileClient>();
        remote.Setup(client => client.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>())).ReturnsAsync("/mods/test");
        remote.Setup(client => client.GetFileSizeAsync("/mods/test/ban.txt", It.IsAny<CancellationToken>())).ReturnsAsync(0);
        remote.Setup(client => client.OpenReadAsync("/mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(""));
        remote.Setup(client => client.UploadAsync(It.IsAny<Stream>(), "/mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Upload failed."));
        var health = new AgentProcessHealth();
        var (watcher, context, repository) = CreateCheckWatcher(remote.Object, processHealth: health);
        await watcher.CheckAsync(context, "test");
        Assert.False(health.BanChecks[context.ServerId].Success);
        var rows = await repository.BanFileMonitorsApi.GetBanFileMonitors(null, null, context.ServerId, 0, 1, null);
        Assert.NotEqual("Success", Assert.Single(rows.Result!.Data!.Items!).LastCheckResult);
    }

    [Fact]
    public async Task AnUnacknowledgedPendingImportRequiresCanonicalCoverageBeforeAnyPush()
    {
        const string content = "pending-guid Manual Ban\n";
        var remote = new Mock<IRemoteFileClient>();
        remote.Setup(client => client.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>())).ReturnsAsync("/mods/test");
        remote.Setup(client => client.GetFileSizeAsync("/mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetByteCount(content));
        remote.Setup(client => client.OpenReadAsync("/mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(content));
        var health = new AgentProcessHealth();
        var (watcher, context, repository) = CreateCheckWatcher(remote.Object, processHealth: health);
        var now = DateTimeOffset.UtcNow;
        var oldest = now.AddMinutes(-21);
        health.BanChecks[context.ServerId] = new(now, true, null, null, oldest);
        var monitor = CreateMonitor(context.ServerId, "previous-etag", now.AddDays(-2).UtcDateTime, now.AddHours(-1).UtcDateTime);
        SetInternalProperty(monitor, nameof(BanFileMonitorDto.RemoteFileSize), (long?)Encoding.UTF8.GetByteCount(content));
        repository.BanFileMonitorsApi.AddBanFileMonitor(monitor);

        await watcher.CheckAsync(context, "test");

        Assert.Equal(oldest, health.BanChecks[context.ServerId].PendingImportSince);
        remote.Verify(client => client.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCentralObservationPreservesThePendingDeliveryDeadline(bool manualBans)
    {
        const string content = "pending-guid Manual Ban\n";
        var remote = new Mock<IRemoteFileClient>();
        var directory = remote.Setup(client => client.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>()));
        if (manualBans)
        {
            directory.ReturnsAsync("/mods/test");
        }
        else
        {
            directory.ThrowsAsync(new IOException("Transport unavailable."));
        }
        remote.Setup(client => client.GetFileSizeAsync("/mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetByteCount(content));
        remote.Setup(client => client.OpenReadAsync("/mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(content));
        var health = new AgentProcessHealth();
        var (watcher, context, _) = CreateCheckWatcher(remote.Object, processHealth: health);
        var now = DateTimeOffset.UtcNow;
        var oldest = now.AddMinutes(-11);
        health.BanChecks[context.ServerId] = new(now, true, "pending-etag", oldest, null);

        await watcher.CheckAsync(context, "test");

        Assert.Equal(oldest, health.BanChecks[context.ServerId].PendingDeliverySince);
        Assert.Equal("pending-etag", health.BanChecks[context.ServerId].PendingETag);
    }

    [Fact]
    public async Task ANewCentralVersionDoesNotResetAnAlreadyOverdueDelivery()
    {
        var remote = new Mock<IRemoteFileClient>();
        remote.Setup(client => client.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>())).ReturnsAsync("/mods/test");
        remote.SetupSequence(client => client.GetFileSizeAsync("/mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(0).ReturnsAsync(1);
        remote.Setup(client => client.OpenReadAsync("/mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(""));
        var health = new AgentProcessHealth();
        var (watcher, context, _) = CreateCheckWatcher(remote.Object, processHealth: health);
        var now = DateTimeOffset.UtcNow;
        var oldest = now.AddMinutes(-11);
        health.BanChecks[context.ServerId] = new(now, true, "previous-etag", oldest, null);

        await watcher.CheckAsync(context, "test");

        Assert.Equal(oldest, health.BanChecks[context.ServerId].PendingDeliverySince);
        Assert.Equal("degraded", health.Evaluate(new(now, [new(context.ServerId, true, true)]), true, now).Status);
        remote.Verify(client => client.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RepeatedPublicationAcknowledgementsDoNotResetTheOldestUnconvergedImportDeadline()
    {
        const string content = "pending-guid Manual Ban\n";
        var remote = new Mock<IRemoteFileClient>();
        remote.Setup(client => client.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>())).ReturnsAsync("/mods/test");
        remote.Setup(client => client.GetFileSizeAsync("/mods/test/ban.txt", It.IsAny<CancellationToken>())).ReturnsAsync(Encoding.UTF8.GetByteCount(content));
        remote.Setup(client => client.OpenReadAsync("/mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(content));
        var health = new AgentProcessHealth();
        var (watcher, context, repository) = CreateCheckWatcher(remote.Object, processHealth: health);
        var now = DateTimeOffset.UtcNow;
        var oldest = now.AddMinutes(-21);
        health.BanChecks[context.ServerId] = new(now, true, null, null, oldest);
        var monitor = CreateMonitor(context.ServerId, "etag", oldest.UtcDateTime, now.AddHours(-1).UtcDateTime);
        SetInternalProperty(monitor, nameof(BanFileMonitorDto.RemoteFileSize), (long?)Encoding.UTF8.GetByteCount(content));
        repository.BanFileMonitorsApi.AddBanFileMonitor(monitor);
        await watcher.AcknowledgeImportAsync(context.ServerId, new()
        {
            ImportUtc = now.UtcDateTime,
            BanCount = 1,
            SampleNamesJson = "[\"Manual Ban\"]"
        });
        Assert.Equal(oldest, health.BanChecks[context.ServerId].PendingImportSince);
        await watcher.CheckAsync(context, "test");
        Assert.Equal(oldest, health.BanChecks[context.ServerId].PendingImportSince);
        Assert.Equal("degraded", health.Evaluate(new(now, [new(context.ServerId, true, true)]), true, now).Status);
    }

    [Fact]
    public async Task CheckAsync_SftpCanonicalDirectory_ImportsExistingManualBansBeforeAnyPush()
    {
        var remote = new Mock<IRemoteFileClient>();
        remote.Setup(c => c.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/Mods/test");
        remote.Setup(c => c.GetFileSizeAsync("/Mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(23);
        remote.Setup(c => c.OpenReadAsync("/Mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream("manual-guid Manual Ban\n"));
        var health = new AgentProcessHealth();
        var (watcher, context, repository) = CreateCheckWatcher(remote.Object, processHealth: health);
        var previous = CreateMonitor(context.ServerId, "etag", DateTime.UtcNow.AddDays(-2),
            DateTime.UtcNow.AddDays(-1), "/mods/test/ban.txt");
        SetInternalProperty(previous, nameof(BanFileMonitorDto.RemoteFileSize), (long?)23);
        repository.BanFileMonitorsApi.AddBanFileMonitor(previous);

        var result = await watcher.CheckAsync(context, "test");

        Assert.Single(result.NewBans);
        Assert.NotNull(health.BanChecks[context.ServerId].PendingImportSince);
        var monitors = await repository.BanFileMonitorsApi.GetBanFileMonitors(null, null, context.ServerId, 0, 1, null);
        Assert.Equal("/Mods/test/ban.txt", Assert.Single(monitors.Result!.Data!.Items!).RemoteFilePath);
        remote.Verify(c => c.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        remote.Verify(c => c.CreateFileIfMissingAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckAsync_MissingFile_InitializesOrPreservesConcurrentManualFile(bool created)
    {
        const string centralContent = "central-guid [BANSYNC]-Player\n";
        var remote = new Mock<IRemoteFileClient>();
        remote.Setup(c => c.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/Mods/test");
        remote.SetupSequence(c => c.GetFileSizeAsync("/Mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RemoteFileNotFoundException("Missing"))
            .ReturnsAsync(created ? Encoding.UTF8.GetByteCount(centralContent) : 23);
        remote.Setup(c => c.CreateFileIfMissingAsync(It.IsAny<Stream>(), "/Mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        remote.Setup(c => c.OpenReadAsync("/Mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(created ? centralContent : "manual-guid Manual Ban\n"));
        var (watcher, context, repository) = CreateCheckWatcher(remote.Object, centralContent);

        var result = await watcher.CheckAsync(context, "test");

        Assert.Equal(created ? 0 : 1, result.NewBans.Count);
        remote.Verify(c => c.CreateFileIfMissingAsync(It.IsAny<Stream>(), "/Mods/test/ban.txt", It.IsAny<CancellationToken>()), Times.Once);
        remote.Verify(c => c.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var monitors = await repository.BanFileMonitorsApi.GetBanFileMonitors(null, null, context.ServerId, 0, 1, null);
        var monitor = Assert.Single(monitors.Result!.Data!.Items!);
        Assert.Equal("Success", monitor.LastCheckResult);
        Assert.Equal(string.Empty, monitor.LastCheckErrorMessage);
        Assert.Equal(created, monitor.LastPushUtc.HasValue);
    }

    [Fact]
    public async Task CheckAsync_MissingFileWithoutCentralSource_DoesNotInitialize()
    {
        var remote = new Mock<IRemoteFileClient>();
        remote.Setup(c => c.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>()))
            .ReturnsAsync("/Mods/test");
        remote.Setup(c => c.GetFileSizeAsync("/Mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RemoteFileNotFoundException("Missing"));
        var (watcher, context, repository) = CreateCheckWatcher(remote.Object, centralContent: string.Empty);

        await watcher.CheckAsync(context, "test");

        var monitors = await repository.BanFileMonitorsApi.GetBanFileMonitors(null, null, context.ServerId, 0, 1, null);
        var monitor = Assert.Single(monitors.Result!.Data!.Items!);
        Assert.Equal("FileTransportError", monitor.LastCheckResult);
        Assert.Contains("no valid central ban file", monitor.LastCheckErrorMessage);
        remote.Verify(c => c.CreateFileIfMissingAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckAsync_InvalidChangedDirectory_ClearsPreviousETagAndPushesAfterRecovery()
    {
        const string centralContent = "central-guid [BANSYNC]-Player\n";
        var remote = new Mock<IRemoteFileClient>();
        remote.SetupSequence(c => c.ResolveDirectoryPathAsync("/mods/test", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RemoteFileNotFoundException("Game directory missing"))
            .ReturnsAsync("/mods/test");
        remote.Setup(c => c.GetFileSizeAsync("/mods/test/ban.txt", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetByteCount(centralContent));
        remote.Setup(c => c.OpenReadAsync("/mods/test/ban.txt", 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => CreateStream(centralContent));
        var (watcher, context, repository) = CreateCheckWatcher(remote.Object, centralContent);
        var previous = CreateMonitor(context.ServerId, "etag", DateTime.UtcNow.AddDays(-2),
            DateTime.UtcNow.AddDays(-1), "/mods/previous/ban.txt");
        SetInternalProperty(previous, nameof(BanFileMonitorDto.RemoteFileSize),
            (long?)Encoding.UTF8.GetByteCount(centralContent));
        repository.BanFileMonitorsApi.AddBanFileMonitor(previous);

        await watcher.CheckAsync(context, "test");

        var monitors = await repository.BanFileMonitorsApi.GetBanFileMonitors(null, null, context.ServerId, 0, 1, null);
        var failed = Assert.Single(monitors.Result!.Data!.Items!);
        Assert.Equal("FileNotFound", failed.LastCheckResult);
        Assert.Equal(string.Empty, failed.LastPushedETag);
        remote.Verify(c => c.CreateFileIfMissingAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        remote.Verify(c => c.UploadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        await watcher.CheckAsync(context, "test");

        remote.Verify(c => c.UploadAsync(It.IsAny<Stream>(), "/mods/test/ban.txt", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (BanFileWatcher Watcher, ServerContext Context, FakeRepositoryApiClient Repository) CreateCheckWatcher(
        IRemoteFileClient remote, string centralContent = "central-guid [BANSYNC]-Player\n",
        AgentProcessHealth? processHealth = null)
    {
        var repository = new FakeRepositoryApiClient();
        var context = CreateServerContext(Guid.NewGuid()) with
        {
            FileTransportEnabled = true,
            FileTransportType = "sftp"
        };
        var resolver = new Mock<IBanFilePathResolver>();
        resolver.Setup(r => r.Resolve("CallOfDuty5", "/", It.IsAny<string?>()))
            .Returns(new ResolvedBanFilePath { Path = "/mods/test/ban.txt", ResolvedForMod = "test" });
        return (new BanFileWatcher(repository, CreateBanFileSource("etag", centralContent), resolver.Object,
            new PassThroughRemoteOpsSessionCoordinator(remote), Mock.Of<IAuditLogger>(),
            NullLogger<BanFileWatcher>.Instance, new ZeroJitterRandom(), processHealth), context, repository);
    }

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
