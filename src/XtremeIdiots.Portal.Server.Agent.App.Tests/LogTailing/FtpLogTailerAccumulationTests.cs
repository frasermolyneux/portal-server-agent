using XtremeIdiots.Portal.Server.Agent.App.LogTailing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.LogTailing;

public class FtpLogTailerAccumulationTests
{
    [Theory]
    [InlineData(0, 10, 8192)]
    [InlineData(5, 10, 8192)]
    [InlineData(17, 17, 3)]
    [InlineData(20000, 20000, 8192)]
    [InlineData(20000, 9001, 8192)]
    [InlineData(20000, 9001, 7)]
    [InlineData(10, 0, 8192)]
    public async Task ReadBytesAsync_ReturnsExactBytesWithinRequestedLength(
        int available, int requested, int maxRead)
    {
        var data = CreateBytes(available);
        using var stream = new RecordingStream(data, maxRead);
        using var cancellation = new CancellationTokenSource();

        var result = await FtpLogTailer.ReadBytesAsync(stream, requested, cancellation.Token);

        var expectedLength = Math.Min(available, requested);
        Assert.Equal(data[..expectedLength], result);
        Assert.Equal(expectedLength, stream.Position);
        var totalRead = 0;
        foreach (var (readRequested, returned, token) in stream.Reads)
        {
            Assert.Equal(Math.Min(8192, requested - totalRead), readRequested);
            Assert.Equal(cancellation.Token, token);
            totalRead += returned;
        }

        Assert.Equal(expectedLength, totalRead);
        if (requested == 0)
        {
            Assert.Empty(stream.Reads);
        }
        else
        {
            Assert.NotEmpty(stream.Reads);
            Assert.Equal(available < requested ? 0 : Math.Min(maxRead, stream.Reads[^1].Requested),
                stream.Reads[^1].Returned);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ReadBytesAsync_PreservesSourceReadException(int successfulReads)
    {
        var failure = new IOException("Source read failed.");
        using var stream = new RecordingStream(CreateBytes(10), 3)
        {
            Failure = failure,
            FailAfterReads = successfulReads
        };
        using var cancellation = new CancellationTokenSource();

        var exception = await Assert.ThrowsAsync<IOException>(
            () => FtpLogTailer.ReadBytesAsync(stream, 10, cancellation.Token));

        Assert.Same(failure, exception);
        Assert.Equal(successfulReads + 1, stream.ReadAttempts);
        Assert.Equal(cancellation.Token, stream.LastToken);
        Assert.Equal(successfulReads * 3, stream.Position);
    }

    [Fact]
    public async Task ReadBytesAsync_ForwardsAlreadyCanceledTokenToSourceRead()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new RecordingStream(CreateBytes(3), 3);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FtpLogTailer.ReadBytesAsync(stream, 3, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, stream.LastToken);
        Assert.Equal(1, stream.ReadAttempts);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public async Task ReadBytesAsync_CancellationAfterFinalRead_DoesNotCancelMemoryWrite()
    {
        var data = CreateBytes(3);
        using var cancellation = new CancellationTokenSource();
        using var stream = new RecordingStream(data, 3) { CancelAfterRead = cancellation };

        var result = await FtpLogTailer.ReadBytesAsync(stream, data.Length, cancellation.Token);

        Assert.Equal(data, result);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, stream.ReadAttempts);
        Assert.Equal(cancellation.Token, stream.LastToken);
    }

    [Fact]
    public async Task ReadBytesAsync_CancellationAfterShortRead_IsObservedAtNextSourceRead()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new RecordingStream(CreateBytes(6), 3) { CancelAfterRead = cancellation };

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FtpLogTailer.ReadBytesAsync(stream, 6, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(cancellation.Token, stream.LastToken);
        Assert.Equal(2, stream.ReadAttempts);
        Assert.Single(stream.Reads);
        Assert.Equal(3, stream.Position);
    }

    private static byte[] CreateBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private sealed class RecordingStream(byte[] data, int maxRead) : MemoryStream(data)
    {
        public List<(int Requested, int Returned, CancellationToken Token)> Reads { get; } = [];

        public int ReadAttempts { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public IOException? Failure { get; init; }

        public int FailAfterReads { get; init; }

        public CancellationTokenSource? CancelAfterRead { get; init; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadAttempts++;
            LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null && Reads.Count == FailAfterReads)
            {
                return ValueTask.FromException<int>(Failure);
            }

            var count = Read(buffer.Span[..Math.Min(buffer.Length, maxRead)]);
            Reads.Add((buffer.Length, count, cancellationToken));
            CancelAfterRead?.Cancel();
            return ValueTask.FromResult(count);
        }
    }
}
