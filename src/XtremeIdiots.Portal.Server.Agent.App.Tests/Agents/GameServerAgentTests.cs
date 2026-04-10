using Moq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class GameServerAgentTests
{
    private readonly ServerContext _testContext = new()
    {
        ServerId = Guid.NewGuid(),
        GameType = "CallOfDuty4",
        Title = "Test Server",
        FtpHostname = "ftp.example.com",
        FtpPort = 21,
        FtpUsername = "user",
        FtpPassword = "pass",
        LogFilePath = "/logs/games_mp.log",
        Hostname = "game.example.com",
        QueryPort = 28960,
        RconPassword = "secret",
        FtpEnabled = true,
        RconEnabled = true,
        BanFileSyncEnabled = true
    };

    private readonly Mock<ILogTailer> _mockTailer = new();
    private readonly Mock<ILogParser> _mockParser = new();
    private readonly Mock<IEventPublisher> _mockPublisher = new();
    private readonly Mock<IOffsetStore> _mockOffsetStore = new();
    private readonly Mock<IServerLock> _mockServerLock = new();
    private readonly Mock<IServerSyncService> _mockSyncService = new();
    private readonly Mock<IBanFileWatcher> _mockBanFileWatcher = new();
    private readonly ILogger _logger = NullLogger.Instance;

    public GameServerAgentTests()
    {
        // Default: lock acquisition and renewal succeed
        _mockServerLock.Setup(l => l.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockServerLock.Setup(l => l.RenewAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Default: ban file watcher returns empty
        _mockBanFileWatcher.Setup(b => b.CheckAsync(It.IsAny<ServerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BanFileCheckResult.Empty);

        // Default: RCON sync returns no IP-resolved events
        _mockSyncService.Setup(s => s.SyncAsync(It.IsAny<Guid>(), It.IsAny<ILogParser>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<XtremeIdiots.Portal.Server.Agent.App.Parsing.PlayerIpResolvedEvent>)Array.Empty<XtremeIdiots.Portal.Server.Agent.App.Parsing.PlayerIpResolvedEvent>());
    }

    private GameServerAgent CreateAgent() =>
        new(_testContext, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBanFileWatcher.Object, _logger);

    [Fact]
    public async Task RunAsync_PublishesServerConnectedOnStart()
    {
        // Arrange
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert
        _mockPublisher.Verify(
            p => p.PublishServerConnectedAsync(
                _testContext.ServerId, _testContext.GameType, It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ParsesAndPublishesEvents()
    {
        // Arrange
        var testLines = new[] { "J;abc123;0;TestPlayer" };
        var testEvent = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc123",
            Username = "TestPlayer",
            SlotId = 0
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pollCount = 0;
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCount++;
                // Return lines on first poll, empty on subsequent
                return pollCount == 1 ? testLines : Array.Empty<string>();
            });

        _mockParser.Setup(p => p.ParseLine("J;abc123;0;TestPlayer")).Returns(testEvent);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert
        _mockParser.Verify(p => p.ParseLine("J;abc123;0;TestPlayer"), Times.Once);
        _mockPublisher.Verify(
            p => p.PublishAsync(testEvent, _testContext.ServerId, _testContext.GameType,
                It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_SavesOffsetPeriodically()
    {
        // Arrange
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockTailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/games_mp.log");
        _mockTailer.SetupGet(t => t.CurrentOffset).Returns(1234L);

        // Run long enough for offset save to trigger (OffsetSaveInterval = 30s, but first save happens immediately)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — offset should be saved at least once (on first loop iteration since _lastOffsetSave = MinValue)
        // plus once on shutdown
        _mockOffsetStore.Verify(
            o => o.SaveOffsetAsync(_testContext.ServerId, 1234L, "/logs/games_mp.log", It.IsAny<CancellationToken>()),
            Times.AtLeast(1));
    }

    [Fact]
    public async Task RunAsync_OnCancellation_SavesOffsetAndDisposes()
    {
        // Arrange
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockTailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/games_mp.log");
        _mockTailer.SetupGet(t => t.CurrentOffset).Returns(5678L);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — offset saved on shutdown
        _mockOffsetStore.Verify(
            o => o.SaveOffsetAsync(_testContext.ServerId, 5678L, "/logs/games_mp.log", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // Tailer disposed
        _mockTailer.Verify(t => t.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task RunAsync_OnParseError_ContinuesProcessing()
    {
        // Arrange
        var testLines = new[] { "bad-line", "J;abc123;0;TestPlayer" };
        var testEvent = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc123",
            Username = "TestPlayer",
            SlotId = 0
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pollCount = 0;
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCount++;
                return pollCount == 1 ? testLines : Array.Empty<string>();
            });

        // First line returns null (unrecognised), second returns an event
        _mockParser.Setup(p => p.ParseLine("bad-line")).Returns((GameEvent?)null);
        _mockParser.Setup(p => p.ParseLine("J;abc123;0;TestPlayer")).Returns(testEvent);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — the valid event was still published despite the first line being unrecognised
        _mockPublisher.Verify(
            p => p.PublishAsync(testEvent, _testContext.ServerId, _testContext.GameType,
                It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ResumesFromSavedOffset()
    {
        // Arrange
        var savedOffset = new SavedOffset
        {
            Offset = 42L,
            FilePath = "/logs/games_mp.log",
            SavedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(_testContext.ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(savedOffset);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), 42L, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — tailer connected with the saved offset
        _mockTailer.Verify(
            t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), 42L, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_DoesNotResumeWhenFilePathDiffers()
    {
        // Arrange — saved offset is for a different file
        var savedOffset = new SavedOffset
        {
            Offset = 42L,
            FilePath = "/logs/different_file.log",
            SavedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(_testContext.ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(savedOffset);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — tailer connected without an offset (null) since file path doesn't match
        _mockTailer.Verify(
            t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenLockNotAcquired_ReturnsImmediately()
    {
        // Arrange — lock acquisition fails
        _mockServerLock.Setup(l => l.TryAcquireAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var agent = CreateAgent();

        // Act
        await agent.RunAsync(CancellationToken.None);

        // Assert — tailer should never connect, no events published
        _mockTailer.Verify(
            t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockPublisher.Verify(
            p => p.PublishServerConnectedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenLeaseLost_StopsAgent()
    {
        // Arrange
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        // Lease renewal fails on first attempt
        _mockServerLock.Setup(l => l.RenewAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — agent should have stopped (task completes before timeout)
        Assert.False(cts.IsCancellationRequested, "Agent should stop from lost lease before cancellation timeout");

        // Verify lease release was attempted
        _mockServerLock.Verify(
            l => l.ReleaseAsync(_testContext.ServerId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ThrowsWhenLogFilePathIsNull()
    {
        // Arrange — context with null LogFilePath
        var context = _testContext with { LogFilePath = null };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object,
            _mockPublisher.Object, _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBanFileWatcher.Object, _logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(cts.Token));
    }

    [Fact]
    public async Task RunAsync_AcknowledgesBanFileMonitorWhenNoNewBans()
    {
        // Arrange — ban file watcher returns monitor updates but no new bans (file unchanged heartbeat)
        var monitorId = Guid.NewGuid();
        var heartbeatResult = new BanFileCheckResult
        {
            NewBans = [],
            MonitorUpdates = [new MonitorUpdate { BanFileMonitorId = monitorId, NewFileSize = 1024 }]
        };

        _mockBanFileWatcher.Setup(b => b.CheckAsync(It.IsAny<ServerContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(heartbeatResult);

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        // Run long enough for ban file check to trigger (BanFileCheckInterval = 60s, but _lastBanFileCheck starts at MinValue)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — AcknowledgeAsync should be called to update LastSync even without new bans
        _mockBanFileWatcher.Verify(
            b => b.AcknowledgeAsync(
                It.Is<IReadOnlyList<MonitorUpdate>>(u => u.Count == 1 && u[0].BanFileMonitorId == monitorId),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // Verify no ban events were published
        _mockPublisher.Verify(
            p => p.PublishBanDetectedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<IReadOnlyList<DetectedBanEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_CallsRconSyncOnStartup()
    {
        // Arrange
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — sync should be called at least once on startup
        _mockSyncService.Verify(
            s => s.SyncAsync(_testContext.ServerId, _mockParser.Object, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_WhenPollReturnsEmpty_DoesNotPublishEvents()
    {
        // Arrange — simulates the fix where PollAsync returns empty on transient FTP failure
        // (GetFileSize returns -1, guard skips the poll instead of triggering false log rotation)
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Simulate several polls returning empty (as would happen with the -1 file size guard)
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — no game events should be published when polls return empty
        _mockPublisher.Verify(
            p => p.PublishAsync(It.IsAny<GameEvent>(), It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_WhenPollFailsIntermittently_ContinuesProcessingSubsequentPolls()
    {
        // Arrange — simulates transient FTP failures (empty polls) followed by recovery with real data.
        // This validates the agent keeps running through transient failures.
        var testEvent = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc123",
            Username = "TestPlayer",
            SlotId = 0
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FtpTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pollCount = 0;
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCount++;
                // First 3 polls return empty (simulating transient FTP failures where GetFileSize returned -1),
                // then poll 4 returns real data
                return pollCount == 4
                    ? new[] { "J;abc123;0;TestPlayer" }
                    : Array.Empty<string>();
            });

        _mockParser.Setup(p => p.ParseLine("J;abc123;0;TestPlayer")).Returns(testEvent);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — the event from poll 4 should still be published
        _mockPublisher.Verify(
            p => p.PublishAsync(testEvent, _testContext.ServerId, _testContext.GameType,
                It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
