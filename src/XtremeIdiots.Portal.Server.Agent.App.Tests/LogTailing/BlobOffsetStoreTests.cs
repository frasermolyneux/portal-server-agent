using System.Text;
using System.Text.Json;

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using Microsoft.Extensions.Logging;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.LogTailing;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.LogTailing;

public class BlobOffsetStoreTests
{
    private readonly Mock<BlobServiceClient> _mockServiceClient;
    private readonly Mock<BlobContainerClient> _mockContainerClient;
    private readonly Mock<BlobClient> _mockBlobClient;
    private readonly Mock<ILogger<BlobOffsetStore>> _mockLogger;
    private readonly BlobOffsetStore _store;

    public BlobOffsetStoreTests()
    {
        _mockServiceClient = new Mock<BlobServiceClient>();
        _mockContainerClient = new Mock<BlobContainerClient>();
        _mockBlobClient = new Mock<BlobClient>();
        _mockLogger = new Mock<ILogger<BlobOffsetStore>>();

        _mockServiceClient
            .Setup(s => s.GetBlobContainerClient("tailer-offsets"))
            .Returns(_mockContainerClient.Object);

        _mockContainerClient
            .Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns(_mockBlobClient.Object);

        _store = new BlobOffsetStore(_mockServiceClient.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task SaveOffsetAsync_WritesJsonBlob()
    {
        var serverId = Guid.NewGuid();
        byte[]? capturedContent = null;

        _mockBlobClient
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()))
            .Callback<Stream, bool, CancellationToken>((stream, _, _) =>
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                capturedContent = ms.ToArray();
            })
            .ReturnsAsync(Mock.Of<Response<BlobContentInfo>>());

        await _store.SaveOffsetAsync(serverId, 450000, "/main/games_mp.log");

        _mockContainerClient.Verify(c => c.GetBlobClient($"offsets/{serverId}.json"), Times.Once);
        _mockBlobClient.Verify(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(capturedContent);
        var json = Encoding.UTF8.GetString(capturedContent);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(450000, doc.RootElement.GetProperty("offset").GetInt64());
        Assert.Equal("/main/games_mp.log", doc.RootElement.GetProperty("filePath").GetString());
        Assert.True(doc.RootElement.TryGetProperty("savedAtUtc", out _));
    }

    [Fact]
    public async Task GetOffsetAsync_WhenBlobExists_ReturnsSavedOffset()
    {
        var serverId = Guid.NewGuid();
        var savedJson = """{"offset":123456,"filePath":"/logs/game.log","savedAtUtc":"2026-04-05T10:00:00Z"}""";
        var binaryData = new BinaryData(Encoding.UTF8.GetBytes(savedJson));

        var mockResponse = new Mock<Response>();
        var downloadResult = BlobsModelFactory.BlobDownloadResult(content: binaryData);

        _mockBlobClient
            .Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(downloadResult, mockResponse.Object));

        var result = await _store.GetOffsetAsync(serverId);

        Assert.NotNull(result);
        Assert.Equal(123456, result.Offset);
        Assert.Equal("/logs/game.log", result.FilePath);
        Assert.Equal(new DateTime(2026, 4, 5, 10, 0, 0, DateTimeKind.Utc), result.SavedAtUtc);
    }

    [Fact]
    public async Task GetOffsetAsync_WhenBlobNotFound_ReturnsNull()
    {
        var serverId = Guid.NewGuid();

        _mockBlobClient
            .Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "BlobNotFound"));

        var result = await _store.GetOffsetAsync(serverId);

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveOffsetAsync_WhenStorageFails_LogsErrorDoesNotThrow()
    {
        var serverId = Guid.NewGuid();

        _mockBlobClient
            .Setup(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "InternalServerError"));

        // Should not throw
        await _store.SaveOffsetAsync(serverId, 100, "/logs/test.log");

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((o, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Constructor_WithNullBlobServiceClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BlobOffsetStore(null!, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BlobOffsetStore(_mockServiceClient.Object, null!));
    }
}
