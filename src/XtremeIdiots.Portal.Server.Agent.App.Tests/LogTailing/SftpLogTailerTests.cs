using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.LogTailing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.LogTailing;

public class SftpLogTailerTests
{
    [Fact]
    public async Task DisposeLogStreamAsync_AwaitsAsynchronousDisposal()
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new TestAsyncDisposable(async () =>
        {
            disposalStarted.SetResult();
            await completeDisposal.Task;
        });

        var disposal = SftpLogTailer.DisposeLogStreamAsync(stream).AsTask();
        await disposalStarted.Task;

        Assert.False(disposal.IsCompleted);

        completeDisposal.SetResult();
        await disposal;
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task DisposeLogStreamAsync_WhenDisposalFails_PropagatesException()
    {
        var expected = new IOException("Failed to dispose stream.");
        var stream = new TestAsyncDisposable(() => ValueTask.FromException(expected));

        var actual = await Assert.ThrowsAsync<IOException>(
            async () => await SftpLogTailer.DisposeLogStreamAsync(stream));

        Assert.Same(expected, actual);
    }

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

    private sealed class TestAsyncDisposable(Func<ValueTask> disposeAsync) : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public async ValueTask DisposeAsync()
        {
            await disposeAsync();
            IsDisposed = true;
        }
    }
}
