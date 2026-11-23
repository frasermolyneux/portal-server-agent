using System.Net;
using System.Collections.Immutable;

using Moq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.BanFiles;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Playtime;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.FileTransport;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class GameServerAgentTests
{
    private sealed class ZeroRandom : Random
    {
        public override long NextInt64(long minValue, long maxValue) => minValue;
    }

    private sealed class MaxRandom : Random
    {
        public override long NextInt64(long minValue, long maxValue) => maxValue - 1;
    }

    private readonly ServerContext _testContext = new()
    {
        ServerId = Guid.NewGuid(),
        GameType = "CallOfDuty4x",
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
        BanFileSyncEnabled = true,
        BanFileRootPath = "/",
        ConfigHash = "test-hash"
    };

    private readonly Mock<ILogTailer> _mockTailer = new();
    private readonly Mock<ILogParser> _mockParser = new();
    private readonly Mock<IEventPublisher> _mockPublisher = new();
    private readonly Mock<IOffsetStore> _mockOffsetStore = new();
    private readonly Mock<IServerLock> _mockServerLock = new();
    private readonly Mock<IServerSyncService> _mockSyncService = new();
    private readonly Mock<IRconBroadcastService> _mockBroadcastService = new();
    private readonly Mock<ICod4xCvarProbe> _mockCvarProbe = new();
    private readonly Mock<ICoD4xPluginLifecycleService> _mockCoD4xPluginLifecycleService = new();
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
        _mockBanFileWatcher.Setup(b => b.CheckAsync(It.IsAny<ServerContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BanFileCheckResult.Empty);

        // Default: RCON sync returns no IP-resolved events
        _mockSyncService.Setup(s => s.SyncAsync(It.IsAny<Guid>(), It.IsAny<ILogParser>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PlayerIpResolvedEvent>)Array.Empty<PlayerIpResolvedEvent>());
        _mockBroadcastService.Setup(r => r.SayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
    }

    private GameServerAgent CreateAgent() => CreateAgent(_testContext);

    private GameServerAgent CreateAgent(ServerContext context) =>
        new(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

    [Fact]
    public async Task PlaytimeRosterSchedule_RefreshesAndBacksOffAfterFailure()
    {
        var context = _testContext with { GameType = "CallOfDuty4" };
        var producer = new PlaytimeProducer(context.ServerId, context.GameType, new Mock<IPlaytimeOutbox>().Object,
            (_, _) => Task.CompletedTask, NullLogger.Instance);
        _mockSyncService.SetupSequence(s => s.CaptureRosterAsync(context.ServerId, context.GameType,
                It.IsAny<Func<ServerRosterSnapshot?, CancellationToken, Task<bool>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object,
            _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object,
            _logger, new ZeroRandom(), playtimeProducer: producer);
        var now = DateTime.UtcNow;
        agent.SchedulePlaytimeRosterSync(now.AddMinutes(-1), captured: true);

        await agent.RefreshPlaytimeRosterIfDueAsync(now, CancellationToken.None);
        await agent.RefreshPlaytimeRosterIfDueAsync(now.AddSeconds(30), CancellationToken.None);
        await agent.RefreshPlaytimeRosterIfDueAsync(now.AddSeconds(61), CancellationToken.None);
        await agent.RefreshPlaytimeRosterIfDueAsync(now.AddMinutes(2), CancellationToken.None);

        _mockSyncService.Verify(s => s.CaptureRosterAsync(context.ServerId, context.GameType,
            It.IsAny<Func<ServerRosterSnapshot?, CancellationToken, Task<bool>>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PlaytimeRosterSchedule_WhenPluginOnly_DoesNotCaptureAgentRoster()
    {
        var agent = CreateAgent();
        var now = DateTime.UtcNow;
        agent.SchedulePlaytimeRosterSync(now.AddMinutes(-1), captured: true);

        await agent.RefreshPlaytimeRosterIfDueAsync(now, CancellationToken.None);

        _mockSyncService.Verify(s => s.CaptureRosterAsync(It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<Func<ServerRosterSnapshot?, CancellationToken, Task<bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PlaytimeRosterSchedule_WhenFullSyncFails_WaitsForNormalRetry()
    {
        var context = _testContext with { GameType = "CallOfDuty4" };
        var producer = new PlaytimeProducer(context.ServerId, context.GameType, new Mock<IPlaytimeOutbox>().Object,
            (_, _) => Task.CompletedTask, NullLogger.Instance);
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object,
            _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object,
            _logger, new ZeroRandom(), playtimeProducer: producer);
        var now = DateTime.UtcNow;
        agent.SchedulePlaytimeRosterSync(now, captured: false);

        await agent.RefreshPlaytimeRosterIfDueAsync(now.AddMinutes(2), CancellationToken.None);

        _mockSyncService.Verify(s => s.CaptureRosterAsync(It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<Func<ServerRosterSnapshot?, CancellationToken, Task<bool>>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_QuietPluginServerWithoutMap_ReportsOperationalHeartbeat()
    {
        var context = _testContext with { IsCod4xPluginSourceEnabled = true };
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object,
            _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object,
            _logger, new ZeroRandom(), () => heartbeat.TrySetResult());

        var running = agent.RunAsync(cts.Token);
        await heartbeat.Task.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await running;

        _mockPublisher.Verify(p => p.PublishServerStatusAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<int, PlayerInfo>>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockSyncService.Verify(s => s.SyncIdentityAsync(It.IsAny<Guid>(), It.IsAny<ILogParser>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_AfterCatchUp_BaselinesBeforeCorrelatingNewJoin()
    {
        var parser = new Cod4xLogParser();
        parser.SetPlayer(2, new PlayerInfo
        {
            Guid = "2310346616629847491",
            Name = "Player",
            SlotId = 2,
            ConnectedAt = DateTime.UtcNow,
            IpAddress = "9.9.9.9"
        });
        var poll = 0;
        var joined = new TaskCompletionSource<PlayerConnectedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockTailer.SetupGet(t => t.LastPollReachedEnd).Returns(true);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++poll == 1
                ? Array.Empty<string>()
                : ["  1:00 J;2310346616629847491;2;Player"]);
        _mockSyncService.Setup(s => s.SyncIdentityAsync(_testContext.ServerId, parser, _testContext.GameType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerSyncResult([], new ServerRosterSnapshot(
                DateTime.UtcNow, RconRosterState.Complete, [], 0, 0, 0)));
        _mockPublisher.Setup(p => p.PublishAsync(It.IsAny<GameEvent>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Callback<GameEvent, Guid, string, long, CancellationToken>((e, _, _, _, _) =>
            {
                if (e is PlayerConnectedEvent connected)
                {
                    joined.TrySetResult(connected);
                }
            })
            .Returns(Task.CompletedTask);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var agent = new GameServerAgent(_testContext, _mockTailer.Object, parser, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object,
            _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        var running = agent.RunAsync(cts.Token);
        var connected = await joined.Task.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await running;

        Assert.NotNull(connected.ConnectionId);
        Assert.Empty(connected.IpAddress);
        Assert.Null(parser.ConnectedPlayers[2].IpAddress);
        _mockSyncService.Verify(s => s.SyncIdentityAsync(_testContext.ServerId, parser, _testContext.GameType, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishIpResolvedEventsAsync_FailureClearsOnlyMatchingPlayerForRetry()
    {
        var current = new PlayerInfo
        {
            Guid = "one",
            Name = "Alice",
            SlotId = 1,
            ConnectedAt = DateTime.UtcNow,
            IpAddress = "1.2.3.4"
        };
        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(new Dictionary<int, PlayerInfo> { [1] = current });
        _mockPublisher.Setup(p => p.PublishAsync(It.IsAny<GameEvent>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Publish failed"));
        var agent = CreateAgent();

        await agent.PublishIpResolvedEventsAsync(
            [new PlayerIpResolvedEvent { Timestamp = DateTime.UtcNow, PlayerGuid = "other", IpAddress = "1.2.3.4", SlotId = 1 }],
            CancellationToken.None);
        Assert.Equal("1.2.3.4", current.IpAddress);
        await agent.PublishIpResolvedEventsAsync(
            [new PlayerIpResolvedEvent { Timestamp = DateTime.UtcNow, PlayerGuid = "one", IpAddress = "1.2.3.4", SlotId = 1 }],
            CancellationToken.None);
        Assert.Null(current.IpAddress);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshSiegeIfDueAsync_OnlyRefreshesRconWhenSessionIsActive(bool active)
    {
        _mockTailer.SetupGet(t => t.LastPollReachedEnd).Returns(true);
        _mockSyncService.Setup(s => s.GetActiveSiegeExpiryAsync(_testContext.ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(active ? DateTimeOffset.UtcNow.AddMinutes(2) : (DateTimeOffset?)null);
        _mockSyncService.Setup(s => s.SyncIdentityAsync(_testContext.ServerId, _mockParser.Object,
                _testContext.GameType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ServerSyncResult([], new ServerRosterSnapshot(
                DateTime.UtcNow, RconRosterState.Complete, [], 0, 0, 0)));
        var agent = CreateAgent();

        await agent.RefreshSiegeIfDueAsync(CancellationToken.None);
        await agent.RefreshSiegeIfDueAsync(CancellationToken.None);

        _mockSyncService.Verify(s => s.GetActiveSiegeExpiryAsync(_testContext.ServerId, It.IsAny<CancellationToken>()), Times.Once);
        _mockSyncService.Verify(s => s.SyncIdentityAsync(_testContext.ServerId, _mockParser.Object,
            _testContext.GameType, It.IsAny<CancellationToken>()), active ? Times.Once : Times.Never);
    }

    [Fact]
    public async Task RunAsync_LostLease_DoesNotReportHeartbeat()
    {
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _mockServerLock.Setup(l => l.RenewAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var heartbeats = 0;
        var agent = new GameServerAgent(_testContext, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object,
            _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object,
            _logger, new ZeroRandom(), () => heartbeats++);

        await agent.RunAsync(CancellationToken.None);

        Assert.Equal(0, heartbeats);
    }

    [Fact]
    public async Task RunAsync_WithSftpPrivateKey_PassesAuthenticationSettingsToTailer()
    {
        var context = _testContext with
        {
            FileTransportEnabled = true,
            FileTransportType = FileTransportTypes.Sftp,
            FileTransportHostname = "sftp.example.com",
            FileTransportPort = 22,
            FileTransportUsername = "sftp-user",
            FileTransportPassword = string.Empty,
            FileTransportHostKeyFingerprint = "AA:BB",
            FileTransportAuthenticationType = SftpAuthenticationType.PrivateKey,
            FileTransportPrivateKey = "k",
            FileTransportPrivateKeyPassphrase = "p"
        };
        FileTransportTailerConfig? capturedConfig = null;
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(context.ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer
            .Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Callback<FileTransportTailerConfig, long?, CancellationToken>((config, _, _) => capturedConfig = config)
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await CreateAgent(context).RunAsync(cts.Token);

        Assert.NotNull(capturedConfig);
        Assert.Equal(SftpAuthenticationType.PrivateKey, capturedConfig.AuthenticationType);
        Assert.Equal("k", capturedConfig.PrivateKey);
        Assert.Equal("p", capturedConfig.PrivateKeyPassphrase);
    }

    [Fact]
    public async Task RunAsync_PublishesServerConnectedOnStart()
    {
        // Arrange
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
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

        _mockBroadcastService.Verify(
            r => r.SayAsync(
                _testContext.ServerId,
                _testContext.GameType,
                It.Is<string>(msg => msg.StartsWith("^4[^1>XI< BOT^4]^7 Agent is now online (version ")),
                It.IsAny<CancellationToken>()),
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

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
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
    public async Task RunAsync_NonCod4x_UsesSameSyncForLegacyAndPlaytimeRoster()
    {
        var context = _testContext with { GameType = "CallOfDuty4" };
        var resolved = new PlayerIpResolvedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "id",
            IpAddress = "127.0.0.1"
        };
        _mockSyncService.Setup(s => s.SyncWithRosterCallbackAsync(context.ServerId, _mockParser.Object,
                context.GameType, false, It.IsAny<Func<ServerRosterSnapshot?, CancellationToken, Task<bool>>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, ILogParser _, string? _, bool _, Func<ServerRosterSnapshot?, CancellationToken, Task<bool>> callback, CancellationToken token) =>
            {
                var roster = new ServerRosterSnapshot(DateTime.UtcNow, RconRosterState.Partial,
                    [new ServerRosterPlayer(1, "id", "Player", null)], 0, 0, 0);
                var recorded = await callback(roster, token);
                return new ServerSyncResult([resolved], roster, recorded);
            });
        _mockTailer.SetupGet(t => t.CurrentFilePath).Returns(context.LogFilePath);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<string>());
        var pending = new List<PlaytimeObservation>();
        var outbox = new Mock<IPlaytimeOutbox>();
        outbox.Setup(o => o.GetOrCreateCoverageAsync(context.ServerId, It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid epoch, DateTimeOffset started, CancellationToken _) =>
                new PlaytimeCoverageState(epoch, new PlaytimeCoverage
                {
                    ProducerStartedUtc = started,
                    DroppedCapacity = 0,
                    DroppedAttempts = 0,
                    DroppedAge = 0
                }));
        outbox.Setup(o => o.EnqueueAsync(context.ServerId, It.IsAny<PlaytimeObservation>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, PlaytimeObservation, CancellationToken>((_, observation, _) => pending.Add(observation))
            .Returns(Task.CompletedTask);
        outbox.Setup(o => o.GetPendingAsync(context.ServerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => pending.ToImmutableArray());
        outbox.Setup(o => o.PreparePublishAsync(context.ServerId, It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid id, DateTimeOffset when, CancellationToken _) =>
            {
                var index = pending.FindIndex(item => item.EventId == id);
                if (index < 0)
                {
                    return null;
                }
                pending[index] = pending[index] with { EventPublishedUtc = pending[index].EventPublishedUtc ?? when };
                return pending[index];
            });
        outbox.Setup(o => o.AcknowledgeAsync(context.ServerId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid id, CancellationToken _) => pending.RemoveAll(x => x.EventId == id) != 0);
        var sent = new TaskCompletionSource<PlaytimeObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new PlaytimeProducer(context.ServerId, context.GameType, outbox.Object,
            (observation, _) => { sent.TrySetResult(observation); return Task.CompletedTask; }, _logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object,
            _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object,
            _logger, new ZeroRandom(), playtimeProducer: producer);

        var run = agent.RunAsync(cts.Token);
        var observation = await sent.Task.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await run;

        Assert.Equal(PlaytimeCompleteness.Partial, observation.Completeness);
        _mockSyncService.Verify(s => s.SyncWithRosterCallbackAsync(context.ServerId, _mockParser.Object, context.GameType,
            false, It.IsAny<Func<ServerRosterSnapshot?, CancellationToken, Task<bool>>>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockSyncService.Verify(s => s.SyncAsync(context.ServerId, _mockParser.Object, context.GameType,
            false, It.IsAny<CancellationToken>()), Times.Never);
        _mockPublisher.Verify(p => p.PublishAsync(resolved, context.ServerId, context.GameType,
            It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_TracksSourceWithoutChangingLegacyPublication()
    {
        var evt = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc123",
            Username = "TestPlayer",
            SlotId = 0
        };
        var offset = 0L;
        var outbox = new Mock<IPlaytimeOutbox>();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _mockTailer.SetupGet(t => t.CurrentFilePath).Returns(_testContext.LogFilePath);
        _mockTailer.SetupGet(t => t.InitialEndOffset).Returns(10);
        _mockTailer.SetupGet(t => t.CurrentOffset).Returns(() => offset);
        _mockTailer.SetupGet(t => t.LastPollRecords).Returns(
        [
            new LogTailRecord("old", 0, 10),
            new LogTailRecord("J;0123456789abcdef0123456789abcdef;0;TestPlayer", 10, 20)
        ]);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            offset = 20;
            return new[] { "join" };
        });
        _mockParser.Setup(p => p.ParseLine("join")).Returns(evt);
        _mockPublisher.Setup(p => p.PublishAsync(evt, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                published.TrySetResult();
                return Task.CompletedTask;
            });
        var tracker = new PlaytimeSourceTracker(outbox.Object, _testContext.ServerId, "CallOfDuty4",
            _ => Task.CompletedTask, () => 1);
        var agent = new GameServerAgent(_testContext, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object,
            _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object,
            _logger, new ZeroRandom(), playtimeSourceTracker: tracker);

        var running = agent.RunAsync(cts.Token);
        await published.Task.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await running;

        outbox.Verify(o => o.CommitSourceBatchAsync(_testContext.ServerId, null,
            It.Is<PlaytimeSourceCursor>(cursor => cursor.Offset == 10),
            It.Is<IReadOnlyList<PlaytimeObservation>>(items => items.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
        outbox.Verify(o => o.CommitSourceBatchAsync(_testContext.ServerId,
            It.Is<PlaytimeSourceCursor>(cursor => cursor != null && cursor.Offset == 10),
            It.Is<PlaytimeSourceCursor>(cursor => cursor.Offset == 20),
            It.Is<IReadOnlyList<PlaytimeObservation>>(items => items.Count == 1
                && items[0].Kind == PlaytimeEventKind.Join
                && items[0].TimestampQuality == PlaytimeTimestampQuality.ParseTime
                && items[0].Freshness == PlaytimeFreshness.Unknown), It.IsAny<CancellationToken>()), Times.Once);
        _mockPublisher.Verify(p => p.PublishAsync(evt, _testContext.ServerId, _testContext.GameType,
            It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenPollThrows_RecordsGapAndContinuesLegacyPolling()
    {
        var evt = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc123",
            Username = "TestPlayer",
            SlotId = 0
        };
        var outbox = new Mock<IPlaytimeOutbox>();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _mockTailer.SetupGet(t => t.CurrentFilePath).Returns(_testContext.LogFilePath);
        _mockTailer.SetupGet(t => t.InitialEndOffset).Returns(10);
        _mockTailer.SetupGet(t => t.LastPollReachedEnd).Returns(true);
        _mockTailer.SetupSequence(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("poll failed"))
            .ReturnsAsync(new[] { "join" });
        _mockParser.Setup(p => p.ParseLine("join")).Returns(evt);
        _mockPublisher.Setup(p => p.PublishAsync(evt, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                published.TrySetResult();
                return Task.CompletedTask;
            });
        var tracker = new PlaytimeSourceTracker(outbox.Object, _testContext.ServerId, "CallOfDuty4",
            _ => Task.CompletedTask, () => 1);
        var agent = new GameServerAgent(_testContext, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object,
            _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object,
            _logger, new ZeroRandom(), playtimeSourceTracker: tracker);

        var running = agent.RunAsync(cts.Token);
        await published.Task.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await running;

        outbox.Verify(o => o.RecordGapAndResetCursorAsync(_testContext.ServerId,
            It.Is<PlaytimeSourceCursor>(cursor => cursor.Offset == 10), It.IsAny<PlaytimeSourceCursor>(),
            PlaytimeGapReason.PollFailure, It.IsAny<CancellationToken>()), Times.Once);
        _mockPublisher.Verify(p => p.PublishAsync(evt, _testContext.ServerId, _testContext.GameType,
            It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_WhenPlaytimeTrackingFails_StillPublishesLegacyEvents()
    {
        var evt = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "abc123",
            Username = "TestPlayer",
            SlotId = 0
        };
        var outbox = new Mock<IPlaytimeOutbox>();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _mockTailer.SetupGet(t => t.CurrentFilePath).Returns(_testContext.LogFilePath);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { "join" });
        _mockParser.Setup(p => p.ParseLine("join")).Returns(evt);
        outbox.Setup(o => o.GetSourceCursorAsync(_testContext.ServerId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("storage unavailable"));
        _mockPublisher.Setup(p => p.PublishAsync(evt, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                published.TrySetResult();
                return Task.CompletedTask;
            });
        var tracker = new PlaytimeSourceTracker(outbox.Object, _testContext.ServerId, "CallOfDuty4",
            _ => Task.CompletedTask, () => 1);
        var agent = new GameServerAgent(_testContext, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object,
            _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object,
            _logger, new ZeroRandom(), playtimeSourceTracker: tracker);

        var running = agent.RunAsync(cts.Token);
        await published.Task.WaitAsync(cts.Token);
        await cts.CancelAsync();
        await running;

        _mockPublisher.Verify(p => p.PublishAsync(evt, _testContext.ServerId, _testContext.GameType,
            It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Once);
        outbox.Verify(o => o.GetSourceCursorAsync(_testContext.ServerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_SavesOffsetPeriodically()
    {
        // Arrange
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
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

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
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

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
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

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), 42L, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — tailer connected with the saved offset
        _mockTailer.Verify(
            t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), 42L, It.IsAny<CancellationToken>()),
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

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — tailer connected without an offset (null) since file path doesn't match
        _mockTailer.Verify(
            t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()),
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
            t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
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

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
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
            _mockPublisher.Object, _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(cts.Token));
    }

    [Fact]
    public async Task RunAsync_DoesNotPublishOrAcknowledgeWhenWatcherReturnsNoBans()
    {
        // Arrange — ban file watcher returns empty result (file unchanged heartbeat).
        // Status writes happen inside the watcher itself, so the agent has nothing
        // to publish or acknowledge in this case.
        _mockBanFileWatcher.Setup(b => b.CheckAsync(It.IsAny<ServerContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BanFileCheckResult.Empty);

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        // Run long enough for ban file check to trigger (BanFileCheckInterval = 60s, but _lastBanFileCheck starts at MinValue)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — CheckAsync should be called at least once
        _mockBanFileWatcher.Verify(
            b => b.CheckAsync(It.IsAny<ServerContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // No publish (no bans) and no acknowledge (no ImportAcknowledgment)
        _mockPublisher.Verify(
            p => p.PublishBanDetectedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(),
                It.IsAny<IReadOnlyList<DetectedBanEntry>>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _mockBanFileWatcher.Verify(
            b => b.AcknowledgeImportAsync(It.IsAny<Guid>(), It.IsAny<ImportAcknowledgment>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_BanFileIntervalOneSecond_ChecksMoreThanOnce()
    {
        var context = _testContext with
        {
            BanFileCheckIntervalSeconds = 1,
            BanFileSyncEnabled = true
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2300));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockBanFileWatcher.Verify(
            b => b.CheckAsync(It.IsAny<ServerContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
    }

    [Fact]
    public async Task RunAsync_BanFileIntervalLong_ChecksOnlyOnceWithinShortRun()
    {
        var context = _testContext with
        {
            BanFileCheckIntervalSeconds = 120,
            BanFileSyncEnabled = true
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2300));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockBanFileWatcher.Verify(
            b => b.CheckAsync(It.IsAny<ServerContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_PublishesBansThenAcknowledgesImport_WhenWatcherReturnsBans()
    {
        // Arrange — watcher returns one detected ban + acknowledgment payload.
        var detectedBan = new DetectedBanEntry { PlayerGuid = "abc123", PlayerName = "Hacker" };
        var ack = new ImportAcknowledgment
        {
            ImportUtc = DateTime.UtcNow,
            BanCount = 1,
            SampleNamesJson = "[\"Hacker\"]"
        };
        var publishResult = new BanFileCheckResult
        {
            NewBans = [detectedBan],
            ImportAcknowledgment = ack
        };

        _mockBanFileWatcher.Setup(b => b.CheckAsync(It.IsAny<ServerContext>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(publishResult);

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — publish first, then acknowledge.
        _mockPublisher.Verify(
            p => p.PublishBanDetectedAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.Is<IReadOnlyList<DetectedBanEntry>>(b => b.Count == 1 && b[0].PlayerGuid == "abc123"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        _mockBanFileWatcher.Verify(
            b => b.AcknowledgeImportAsync(
                _testContext.ServerId,
                It.Is<ImportAcknowledgment>(a => a.BanCount == 1 && a.SampleNamesJson == "[\"Hacker\"]"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_CallsRconSyncOnStartup()
    {
        // Arrange
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var agent = CreateAgent();

        // Act
        await agent.RunAsync(cts.Token);

        // Assert — sync should be called at least once on startup
        _mockSyncService.Verify(
            s => s.SyncAsync(_testContext.ServerId, _mockParser.Object, _testContext.GameType, _testContext.IsCod4xPluginSourceEnabled, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_WhenPollReturnsEmpty_DoesNotPublishEvents()
    {
        // Arrange — simulates the fix where PollAsync returns empty on transient FTP failure
        // (GetFileSize returns -1, guard skips the poll instead of triggering false log rotation)
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
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

        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
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

    [Fact]
    public async Task RunAsync_BroadcastsEnabled_RotatesEnabledMessages()
    {
        var context = _testContext with
        {
            AgentNamePrefix = "^1[Agent]^7",
            Broadcasts = new BroadcastSettings
            {
                Enabled = true,
                IntervalSeconds = 1,
                Messages = new[]
                {
                    new BroadcastMessage { Message = "message-1", Enabled = true },
                    new BroadcastMessage { Message = "message-disabled", Enabled = false },
                    new BroadcastMessage { Message = "message-2", Enabled = true }
                }
            }
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2600));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockBroadcastService.Verify(r => r.SayAsync(context.ServerId, context.GameType, "^1[Agent]^7 message-1", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _mockBroadcastService.Verify(r => r.SayAsync(context.ServerId, context.GameType, "^1[Agent]^7 message-2", It.IsAny<CancellationToken>()), Times.Once);
        _mockBroadcastService.Verify(r => r.SayAsync(context.ServerId, context.GameType, "^1[Agent]^7 message-disabled", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_BroadcastsDisabled_DoesNotSendBroadcasts()
    {
        var context = _testContext with
        {
            Broadcasts = new BroadcastSettings
            {
                Enabled = false,
                IntervalSeconds = 1,
                Messages = [new BroadcastMessage { Message = "message-1", Enabled = true }]
            }
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockBroadcastService.Verify(
            r => r.SayAsync(
                context.ServerId,
                context.GameType,
                It.Is<string>(msg => msg.StartsWith("^4[^1>XI< BOT^4]^7 Agent is now online (version ")),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _mockBroadcastService.Verify(
            r => r.SayAsync(context.ServerId, context.GameType, "message-1", It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_UnsupportedGameType_DoesNotSendAnyBroadcasts()
    {
        var context = _testContext with
        {
            GameType = "Insurgency",
            Broadcasts = new BroadcastSettings
            {
                Enabled = true,
                IntervalSeconds = 1,
                Messages = [new BroadcastMessage { Message = "message-1", Enabled = true }]
            }
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockBroadcastService.Verify(r => r.SayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_BroadcastsEnabled_NonCoD4xGame_RotatesEnabledMessages()
    {
        var context = _testContext with
        {
            GameType = "CallOfDuty4",
            AgentNamePrefix = "^1[Agent]^7",
            Broadcasts = new BroadcastSettings
            {
                Enabled = true,
                IntervalSeconds = 1,
                Messages = new[]
                {
                    new BroadcastMessage { Message = "message-1", Enabled = true },
                    new BroadcastMessage { Message = "message-2", Enabled = true }
                }
            }
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2600));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockBroadcastService.Verify(r => r.SayAsync(context.ServerId, context.GameType, "^1[Agent]^7 message-1", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _mockBroadcastService.Verify(r => r.SayAsync(context.ServerId, context.GameType, "^1[Agent]^7 message-2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_PluginSourceEnabled_StillSendsBroadcasts()
    {
        var context = _testContext with
        {
            IsCod4xPluginSourceEnabled = true,
            Broadcasts = new BroadcastSettings
            {
                Enabled = true,
                IntervalSeconds = 1,
                Messages = [new BroadcastMessage { Message = "message-1", Enabled = true }]
            }
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1200));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        // Broadcasts are owned by the agent for CoD4x regardless of the plugin-source flag.
        // Assert the scheduled message body specifically (only the scheduled path emits "message-1")
        // so this proves scheduled broadcasts are no longer suppressed, not just the startup one.
        _mockBroadcastService.Verify(
            r => r.SayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.Is<string>(m => m.Contains("message-1")), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_PluginSourceEnabled_SuppressesAllParsedEvents()
    {
        var context = _testContext with
        {
            IsCod4xPluginSourceEnabled = true
        };

        var chatEvent = new ChatMessageEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "player-1",
            Username = "PlayerOne",
            SlotId = 2,
            Message = "!whoami",
            IsTeamChat = false
        };

        var connectedEvent = new PlayerConnectedEvent
        {
            Timestamp = DateTime.UtcNow,
            PlayerGuid = "player-2",
            Username = "PlayerTwo",
            SlotId = 3
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pollCount = 0;
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                pollCount++;
                return pollCount == 1
                    ? ["chat-line", "connect-line"]
                    : Array.Empty<string>();
            });

        _mockParser.Setup(p => p.ParseLine("chat-line")).Returns(chatEvent);
        _mockParser.Setup(p => p.ParseLine("connect-line")).Returns(connectedEvent);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockPublisher.Verify(
            p => p.PublishAsync(chatEvent, context.ServerId, context.GameType, It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _mockPublisher.Verify(
            p => p.PublishAsync(connectedEvent, context.ServerId, context.GameType, It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_PluginSourceEnabled_SuppressesStatusAndIpResolvedPublishing()
    {
        var context = _testContext with
        {
            IsCod4xPluginSourceEnabled = true
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        _mockParser.SetupGet(p => p.CurrentMap).Returns("mp_crash");
        _mockParser.SetupGet(p => p.ConnectedPlayers).Returns(new Dictionary<int, PlayerInfo>());
        _mockParser.SetupGet(p => p.ServerTitle).Returns("Test Server");
        _mockParser.SetupGet(p => p.ServerMod).Returns("mod");
        _mockParser.SetupGet(p => p.MaxPlayers).Returns(24);

        var ipEvents = new[]
        {
            new PlayerIpResolvedEvent
            {
                Timestamp = DateTime.UtcNow,
                PlayerGuid = "player-1",
                IpAddress = "127.0.0.1"
            }
        };

        _mockSyncService
            .Setup(s => s.SyncAsync(context.ServerId, _mockParser.Object, context.GameType, context.IsCod4xPluginSourceEnabled, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ipEvents);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockPublisher.Verify(
            p => p.PublishServerStatusAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<int, PlayerInfo>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _mockPublisher.Verify(
            p => p.PublishAsync(It.Is<PlayerIpResolvedEvent>(e => e.PlayerGuid == "player-1"), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_PluginSourceEnabled_SuppressesServerConnectedPublishing()
    {
        var context = _testContext with
        {
            IsCod4xPluginSourceEnabled = true
        };

        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockPublisher.Verify(
            p => p.PublishServerConnectedAsync(context.ServerId, context.GameType, It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_BroadcastSendFails_RetriesSameMessageWithoutRotating()
    {
        var context = _testContext with
        {
            AgentNamePrefix = "^3[RetryAgent]^7",
            Broadcasts = new BroadcastSettings
            {
                Enabled = true,
                IntervalSeconds = 1,
                Messages =
                [
                    new BroadcastMessage { Message = "message-1", Enabled = true },
                    new BroadcastMessage { Message = "message-2", Enabled = true }
                ]
            }
        };

        _mockBroadcastService.Setup(r => r.SayAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.InternalServerError));
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(2500));
        var agent = new GameServerAgent(context, _mockTailer.Object, _mockParser.Object, _mockPublisher.Object,
            _mockOffsetStore.Object, _mockServerLock.Object, _mockSyncService.Object, _mockBroadcastService.Object, _mockCvarProbe.Object, _mockCoD4xPluginLifecycleService.Object, _mockBanFileWatcher.Object, _logger, new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockBroadcastService.Verify(r => r.SayAsync(context.ServerId, context.GameType, "^3[RetryAgent]^7 message-1", It.IsAny<CancellationToken>()), Times.AtLeast(2));
        _mockBroadcastService.Verify(r => r.SayAsync(context.ServerId, context.GameType, "^3[RetryAgent]^7 message-2", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_StartupJitterZeroRandom_StartsPromptly()
    {
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var agent = new GameServerAgent(
            _testContext,
            _mockTailer.Object,
            _mockParser.Object,
            _mockPublisher.Object,
            _mockOffsetStore.Object,
            _mockServerLock.Object,
            _mockSyncService.Object,
            _mockBroadcastService.Object,
            _mockCvarProbe.Object,
            _mockCoD4xPluginLifecycleService.Object,
            _mockBanFileWatcher.Object,
            _logger,
            new ZeroRandom());

        await agent.RunAsync(cts.Token);

        _mockPublisher.Verify(
            p => p.PublishServerConnectedAsync(
                _testContext.ServerId,
                _testContext.GameType,
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_StartupJitterMaxRandom_DelaysStartup()
    {
        _mockOffsetStore.Setup(o => o.GetOffsetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SavedOffset?)null);
        _mockTailer.Setup(t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockTailer.Setup(t => t.PollAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var agent = new GameServerAgent(
            _testContext,
            _mockTailer.Object,
            _mockParser.Object,
            _mockPublisher.Object,
            _mockOffsetStore.Object,
            _mockServerLock.Object,
            _mockSyncService.Object,
            _mockBroadcastService.Object,
            _mockCvarProbe.Object,
            _mockCoD4xPluginLifecycleService.Object,
            _mockBanFileWatcher.Object,
            _logger,
            new MaxRandom());

        await agent.RunAsync(cts.Token);

        _mockTailer.Verify(
            t => t.ConnectAsync(It.IsAny<FileTransportTailerConfig>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
