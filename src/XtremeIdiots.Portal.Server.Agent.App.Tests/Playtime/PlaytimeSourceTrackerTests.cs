using Moq;

using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Playtime;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Playtime;

public class PlaytimeSourceTrackerTests
{
    [Fact]
    public async Task InitializeAndPoll_StartsAtEofAndPausesInsteadOfTreatingUnknownAgeAsLive()
    {
        var serverId = Guid.NewGuid();
        var outbox = new Mock<IPlaytimeOutbox>();
        var tailer = new Mock<ILogTailer>();
        var offset = 50L;
        PlaytimeSourceCursor? cursor = null;
        tailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/game.log");
        tailer.SetupGet(t => t.InitialEndOffset).Returns(100);
        tailer.SetupGet(t => t.CurrentOffset).Returns(() => offset);
        outbox.Setup(o => o.CommitSourceBatchAsync(serverId, null, It.IsAny<PlaytimeSourceCursor>(),
                It.IsAny<IReadOnlyList<PlaytimeObservation>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, PlaytimeSourceCursor?, PlaytimeSourceCursor, IReadOnlyList<PlaytimeObservation>, CancellationToken>(
                (_, _, next, _, _) => cursor = next)
            .Returns(Task.CompletedTask);
        var tracker = new PlaytimeSourceTracker(outbox.Object, serverId);

        await tracker.InitializeAsync(tailer.Object, CancellationToken.None);
        await tracker.ObservePollAsync(tailer.Object, CancellationToken.None);
        offset = 120;
        await tracker.ObservePollAsync(tailer.Object, CancellationToken.None);
        await tracker.ObservePollAsync(tailer.Object, CancellationToken.None);

        Assert.Equal(100, cursor?.Offset);
        outbox.Verify(o => o.PauseSourceAsync(serverId, cursor!, PlaytimeGapReason.UnverifiedFreshness,
            It.IsAny<CancellationToken>()), Times.Once);
        outbox.Verify(o => o.EnqueueAsync(It.IsAny<Guid>(), It.IsAny<PlaytimeObservation>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ObservePoll_WhenContinuityChanges_PausesAndRecordsGap()
    {
        var serverId = Guid.NewGuid();
        var outbox = new Mock<IPlaytimeOutbox>();
        var tailer = new Mock<ILogTailer>();
        var version = 0L;
        tailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/game.log");
        tailer.SetupGet(t => t.InitialEndOffset).Returns(100);
        tailer.SetupGet(t => t.ContinuityVersion).Returns(() => version);
        var tracker = new PlaytimeSourceTracker(outbox.Object, serverId);

        await tracker.InitializeAsync(tailer.Object, CancellationToken.None);
        version++;
        await tracker.ObservePollAsync(tailer.Object, CancellationToken.None);

        outbox.Verify(o => o.PauseSourceAsync(serverId, It.Is<PlaytimeSourceCursor>(c => c.Offset == 100),
            PlaytimeGapReason.ContinuityLost, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Initialize_WhenResuming_RecordsRestartGapAtNewEof()
    {
        var serverId = Guid.NewGuid();
        var outbox = new Mock<IPlaytimeOutbox>();
        var tailer = new Mock<ILogTailer>();
        var previous = new PlaytimeSourceCursor(Guid.NewGuid(), "/logs/game.log", 50);
        outbox.Setup(o => o.GetSourceCursorAsync(serverId, It.IsAny<CancellationToken>())).ReturnsAsync(previous);
        tailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/game.log");
        tailer.SetupGet(t => t.InitialEndOffset).Returns(100);

        await new PlaytimeSourceTracker(outbox.Object, serverId).InitializeAsync(tailer.Object, CancellationToken.None);

        outbox.Verify(o => o.RecordGapAndResetCursorAsync(serverId, previous,
            It.Is<PlaytimeSourceCursor>(next => next.Offset == 100 && next.SourceId != previous.SourceId),
            PlaytimeGapReason.Restart, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ObservePollFailure_PausesOnceAndDoesNotAdvanceCursor()
    {
        var serverId = Guid.NewGuid();
        var outbox = new Mock<IPlaytimeOutbox>();
        var tailer = new Mock<ILogTailer>();
        tailer.SetupGet(t => t.CurrentFilePath).Returns("/logs/game.log");
        tailer.SetupGet(t => t.InitialEndOffset).Returns(100);
        var tracker = new PlaytimeSourceTracker(outbox.Object, serverId);

        await tracker.InitializeAsync(tailer.Object, CancellationToken.None);
        await tracker.ObservePollFailureAsync(CancellationToken.None);
        await tracker.ObservePollFailureAsync(CancellationToken.None);

        outbox.Verify(o => o.PauseSourceAsync(serverId, It.Is<PlaytimeSourceCursor>(c => c.Offset == 100),
            PlaytimeGapReason.PollFailure, It.IsAny<CancellationToken>()), Times.Once);
        outbox.Verify(o => o.CommitSourceBatchAsync(serverId, It.Is<PlaytimeSourceCursor>(c => c != null),
            It.IsAny<PlaytimeSourceCursor>(), It.IsAny<IReadOnlyList<PlaytimeObservation>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
