using FluentFTP;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.BanFiles;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.BanFiles;

public class FtpRemoteFileClientTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_DisconnectsWhenConnectedAndAwaitsClientDisposal(bool isConnected)
    {
        var clientMock = new Mock<IAsyncFtpClient>();
        var calls = new List<string>();
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completeDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        clientMock.Setup(client => client.IsConnected).Returns(isConnected);
        clientMock.Setup(client => client.Disconnect(It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("disconnect"))
            .Returns(Task.CompletedTask);
        clientMock.Setup(client => client.DisposeAsync()).Returns(() =>
        {
            calls.Add("dispose");
            disposalStarted.SetResult();
            return new ValueTask(completeDisposal.Task);
        });

        var remoteClient = new FtpRemoteFileClient(clientMock.Object);
        var disposeTask = remoteClient.DisposeAsync().AsTask();

        await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(disposeTask.IsCompleted);
        Assert.Equal(isConnected ? ["disconnect", "dispose"] : ["dispose"], calls);

        completeDisposal.SetResult();
        await disposeTask;
    }

    [Fact]
    public async Task DisposeAsync_WhenClientDisposalFails_PropagatesFailure()
    {
        var clientMock = new Mock<IAsyncFtpClient>();
        clientMock.Setup(client => client.DisposeAsync())
            .Returns(() => ValueTask.FromException(new IOException("Disposal failed")));
        var remoteClient = new FtpRemoteFileClient(clientMock.Object);

        await Assert.ThrowsAsync<IOException>(() => remoteClient.DisposeAsync().AsTask());
    }
}
