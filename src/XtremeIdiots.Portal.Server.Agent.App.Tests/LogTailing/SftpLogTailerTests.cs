using Microsoft.Extensions.Logging;

using Moq;

using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

using XtremeIdiots.Portal.Server.Agent.App.FileTransport;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Settings.Contracts.V1.Contracts.FileTransport;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.LogTailing;

public class SftpLogTailerTests
{
    [Fact]
    public Task DisposeAsync_AwaitsLogStreamDisposal()
    {
        return AssertDisposalIsAwaited(tailer => tailer.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task ConnectAsync_WhenResettingConnection_AwaitsLogStreamDisposal()
    {
        await AssertDisposalIsAwaited(tailer =>
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                tailer.ConnectAsync(CreateConfig((SftpAuthenticationType)int.MaxValue))));
    }

    [Fact]
    public async Task ConnectAsync_WhenStreamDisposalFails_ClearsStreamAndPropagatesException()
    {
        var expected = new IOException("Failed to dispose stream.");
        var stream = new TestLogStream(
            _ => ValueTask.FromResult(0),
            () => ValueTask.FromException(expected));
        var tailer = CreateTailer(stream);
        var config = CreateConfig((SftpAuthenticationType)int.MaxValue);

        var actual = await Assert.ThrowsAsync<IOException>(() => tailer.ConnectAsync(config));
        Assert.Same(expected, actual);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => tailer.ConnectAsync(config));
        Assert.Equal(1, stream.DisposeCount);
    }

    [Theory, InlineData(0), InlineData(20)]
    public async Task PollAsync_WhenFileRotatesOrRenames_AwaitsDisposalBeforeOpeningReplacement(long fileSize)
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = CreateDelayedStream(disposalStarted, completeDisposal, _ => ValueTask.FromResult(0));
        var replacement = new TestLogStream(_ => ValueTask.FromResult(0));
        var openCount = 0;
        var tailer = CreateTailer(
            original,
            offset: 10,
            getFileSize: (_, _) => Task.FromResult(fileSize),
            openLogStream: (_, _) =>
            {
                Assert.True(original.IsDisposed);
                openCount++;
                return Task.FromResult<Stream>(replacement);
            });

        var poll = tailer.PollAsync();
        await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(poll.IsCompleted);
        Assert.Equal(0, openCount);

        completeDisposal.SetResult();
        _ = await poll;

        Assert.Equal(1, openCount);
        Assert.True(original.IsDisposed);
    }

    [Theory, InlineData("missing"), InlineData("sftp"), InlineData("io")]
    public async Task PollAsync_WhenReadFails_AwaitsDisposalBeforeReconnect(string failureType)
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = CreateDelayedStream(
            disposalStarted,
            completeDisposal,
            _ => ValueTask.FromException<int>(failureType switch
            {
                "missing" => new SftpPathNotFoundException("Log path not found."),
                "sftp" => new SshException("SFTP read failed."),
                _ => new IOException("Read failed.")
            }));
        var tailer = CreateTailer(
            original,
            reconnect: _ =>
            {
                Assert.True(original.IsDisposed);
                reconnectStarted.SetResult();
                return Task.CompletedTask;
            });

        var poll = tailer.PollAsync();
        await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(poll.IsCompleted);
        Assert.False(reconnectStarted.Task.IsCompleted);

        completeDisposal.SetResult();
        _ = await poll;

        Assert.True(original.IsDisposed);
        Assert.True(reconnectStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task ConnectAsync_WhenHostKeyFingerprintMissing_Throws()
    {
        var logger = new Mock<ILogger<SftpLogTailer>>();
        var tailer = new SftpLogTailer(logger.Object);
        var config = CreateConfig(hostKeyFingerprint: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tailer.ConnectAsync(config));
    }

    private static async Task AssertDisposalIsAwaited(Func<SftpLogTailer, Task> operation)
    {
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = CreateDelayedStream(disposalStarted, completeDisposal, _ => ValueTask.FromResult(0));
        var tailer = CreateTailer(stream);

        var pending = operation(tailer);
        await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(pending.IsCompleted);

        completeDisposal.SetResult();
        await pending;
        Assert.True(stream.IsDisposed);
    }

    private static SftpLogTailer CreateTailer(
        Stream stream,
        long offset = 0,
        Func<string, CancellationToken, Task<long>>? getFileSize = null,
        Func<string, CancellationToken, Task<Stream>>? openLogStream = null,
        Func<CancellationToken, Task>? reconnect = null)
    {
        return new(
            new Mock<ILogger<SftpLogTailer>>().Object, CreateConfig(), stream, offset, () => true,
            getFileSize ?? ((_, _) => Task.FromResult(0L)),
            openLogStream ?? ((_, _) => Task.FromResult<Stream>(new TestLogStream(_ => ValueTask.FromResult(0)))),
            reconnect ?? (_ => Task.CompletedTask));
    }

    private static TestLogStream CreateDelayedStream(
        TaskCompletionSource disposalStarted,
        TaskCompletionSource completeDisposal,
        Func<Memory<byte>, ValueTask<int>> readAsync)
    {
        return new TestLogStream(readAsync, async () =>
        {
            disposalStarted.SetResult();
            await completeDisposal.Task;
        });
    }

    private static FileTransportTailerConfig CreateConfig(
        SftpAuthenticationType authenticationType = SftpAuthenticationType.Password,
        string? hostKeyFingerprint = "fingerprint")
    {
        return new FileTransportTailerConfig
        {
            TransportType = "sftp",
            Hostname = "sftp.example.com",
            Port = 22,
            Username = "user",
            Password = "pass",
            AuthenticationType = authenticationType,
            HostKeyFingerprint = hostKeyFingerprint,
            FilePath = "/logs/games_mp.log"
        };
    }

    private sealed class TestLogStream(
        Func<Memory<byte>, ValueTask<int>> readAsync,
        Func<ValueTask>? disposeAsync = null) : MemoryStream
    {
        public bool IsDisposed => !CanRead;
        public int DisposeCount { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return readAsync(buffer);
        }

        public override async ValueTask DisposeAsync()
        {
            DisposeCount++;
            await (disposeAsync?.Invoke() ?? ValueTask.CompletedTask);
            await base.DisposeAsync();
        }
    }
}
