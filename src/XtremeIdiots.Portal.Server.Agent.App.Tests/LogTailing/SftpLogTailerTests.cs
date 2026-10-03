using Microsoft.Extensions.Logging;

using Moq;

using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

using XtremeIdiots.Portal.Server.Agent.App.FileTransport;
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
    public async Task DisposeAsync_AwaitsLogStreamDisposal()
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new TestLogStream(_ => ValueTask.FromResult(0), async () =>
        {
            disposalStarted.SetResult();
            await completeDisposal.Task;
        });
        var tailer = CreateTailer(stream);

        var disposal = tailer.DisposeAsync().AsTask();
        await disposalStarted.Task;

        Assert.False(disposal.IsCompleted);

        completeDisposal.SetResult();
        await disposal;
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task ResetConnectionAsync_AwaitsLogStreamDisposal()
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new TestLogStream(_ => ValueTask.FromResult(0), async () =>
        {
            disposalStarted.SetResult();
            await completeDisposal.Task;
        });
        var tailer = CreateTailer(stream);

        var reset = tailer.ResetConnectionAsync();
        await disposalStarted.Task;

        Assert.False(reset.IsCompleted);

        completeDisposal.SetResult();
        await reset;
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task PollAsync_WhenFileRotates_AwaitsDisposalBeforeOpeningReplacement()
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new TestLogStream(_ => ValueTask.FromResult(0), async () =>
        {
            disposalStarted.SetResult();
            await completeDisposal.Task;
        });
        var replacement = new TestLogStream(_ => ValueTask.FromResult(0));
        var openCount = 0;
        var tailer = CreateTailer(
            original,
            offset: 10,
            getFileSize: (_, _) => Task.FromResult(0L),
            openLogStream: (_, _) =>
            {
                Assert.True(original.IsDisposed);
                openCount++;
                return Task.FromResult<Stream>(replacement);
            });

        var poll = tailer.PollAsync();
        await disposalStarted.Task;

        Assert.False(poll.IsCompleted);
        Assert.Equal(0, openCount);

        completeDisposal.SetResult();
        await poll;

        Assert.Equal(1, openCount);
        Assert.True(original.IsDisposed);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("sftp")]
    [InlineData("io")]
    public async Task PollAsync_WhenReadFails_AwaitsDisposalBeforeReconnect(string failureType)
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new TestLogStream(
            _ => ValueTask.FromException<int>(failureType switch
            {
                "missing" => new SftpPathNotFoundException("Log path not found."),
                "sftp" => new SshException("SFTP read failed."),
                _ => new IOException("Read failed.")
            }),
            async () =>
            {
                disposalStarted.SetResult();
                await completeDisposal.Task;
            });
        var tailer = CreateTailer(
            original,
            reconnect: _ =>
            {
                Assert.True(original.IsDisposed);
                reconnectStarted.SetResult();
                return Task.CompletedTask;
            });

        var poll = tailer.PollAsync();
        await disposalStarted.Task;

        Assert.False(poll.IsCompleted);
        Assert.False(reconnectStarted.Task.IsCompleted);

        completeDisposal.SetResult();
        await poll;

        Assert.True(original.IsDisposed);
        Assert.True(reconnectStarted.Task.IsCompleted);
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

    private static SftpLogTailer CreateTailer(
        Stream stream,
        long offset = 0,
        Func<string, CancellationToken, Task<long>>? getFileSize = null,
        Func<string, CancellationToken, Task<Stream>>? openLogStream = null,
        Func<CancellationToken, Task>? reconnect = null)
    {
        var config = new FileTransportTailerConfig
        {
            TransportType = "sftp",
            Hostname = "sftp.example.com",
            Port = 22,
            Username = "user",
            Password = "pass",
            HostKeyFingerprint = "fingerprint",
            FilePath = "/logs/games_mp.log"
        };

        return new SftpLogTailer(
            new Mock<ILogger<SftpLogTailer>>().Object,
            config,
            stream,
            offset,
            () => true,
            getFileSize ?? ((_, _) => Task.FromResult(0L)),
            openLogStream ?? ((_, _) => Task.FromResult<Stream>(new TestLogStream(_ => ValueTask.FromResult(0)))),
            reconnect ?? (_ => Task.CompletedTask));
    }

    private sealed class TestLogStream(
        Func<Memory<byte>, ValueTask<int>> readAsync,
        Func<ValueTask>? disposeAsync = null) : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            readAsync(buffer);

        public override async ValueTask DisposeAsync()
        {
            if (disposeAsync is not null)
            {
                await disposeAsync();
            }

            await base.DisposeAsync();
            IsDisposed = true;
        }
    }
}
