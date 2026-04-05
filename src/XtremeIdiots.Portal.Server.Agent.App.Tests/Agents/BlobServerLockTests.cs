using System.Net;

using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class BlobServerLockTests
{
    private readonly Mock<BlobServiceClient> _mockBlobServiceClient = new();
    private readonly Mock<BlobContainerClient> _mockContainerClient = new();
    private readonly Mock<BlobClient> _mockBlobClient = new();
    private readonly Mock<BlobLeaseClient> _mockLeaseClient = new();
    private readonly ILogger<BlobServerLock> _logger = NullLogger<BlobServerLock>.Instance;
    private readonly Guid _serverId = Guid.NewGuid();

    public BlobServerLockTests()
    {
        _mockBlobServiceClient.Setup(s => s.GetBlobContainerClient("server-locks"))
            .Returns(_mockContainerClient.Object);
        _mockContainerClient.Setup(c => c.GetBlobClient(It.IsAny<string>()))
            .Returns(_mockBlobClient.Object);
    }

    /// <summary>
    /// Testable subclass that overrides <see cref="BlobServerLock.GetLeaseClient"/> to return a mock.
    /// </summary>
    private sealed class TestableBlobServerLock : BlobServerLock
    {
        private readonly BlobLeaseClient _leaseClient;

        public TestableBlobServerLock(BlobServiceClient blobServiceClient, ILogger<BlobServerLock> logger, BlobLeaseClient leaseClient)
            : base(blobServiceClient, logger)
        {
            _leaseClient = leaseClient;
        }

        internal override BlobLeaseClient GetLeaseClient(BlobClient blobClient, string? leaseId = null)
            => _leaseClient;
    }

    private BlobServerLock CreateLock() =>
        new TestableBlobServerLock(_mockBlobServiceClient.Object, _logger, _mockLeaseClient.Object);

    [Fact]
    public async Task TryAcquireAsync_WhenLeaseAvailable_ReturnsTrue()
    {
        // Arrange
        var mockResponse = new Mock<Response<BlobLease>>();
        mockResponse.Setup(r => r.Value).Returns(BlobsModelFactory.BlobLease(new ETag("etag"), DateTimeOffset.UtcNow, "lease-id-123"));

        _mockBlobClient.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, new Mock<Response>().Object));

        _mockLeaseClient.Setup(l => l.AcquireAsync(It.IsAny<TimeSpan>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        var serverLock = CreateLock();

        // Act
        var result = await serverLock.TryAcquireAsync(_serverId);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenLeaseHeld_ReturnsFalse()
    {
        // Arrange
        _mockBlobClient.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, new Mock<Response>().Object));

        _mockLeaseClient.Setup(l => l.AcquireAsync(It.IsAny<TimeSpan>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException((int)HttpStatusCode.Conflict, "Lease already held"));

        var serverLock = CreateLock();

        // Act
        var result = await serverLock.TryAcquireAsync(_serverId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenException_ReturnsFalse()
    {
        // Arrange
        _mockBlobClient.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Storage unavailable"));

        var serverLock = CreateLock();

        // Act
        var result = await serverLock.TryAcquireAsync(_serverId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task RenewAsync_WithValidLease_ReturnsTrue()
    {
        // Arrange — first acquire a lease
        var mockResponse = new Mock<Response<BlobLease>>();
        mockResponse.Setup(r => r.Value).Returns(BlobsModelFactory.BlobLease(new ETag("etag"), DateTimeOffset.UtcNow, "lease-id-123"));

        _mockBlobClient.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, new Mock<Response>().Object));

        _mockLeaseClient.Setup(l => l.AcquireAsync(It.IsAny<TimeSpan>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        var renewResponse = new Mock<Response<BlobLease>>();
        renewResponse.Setup(r => r.Value).Returns(BlobsModelFactory.BlobLease(new ETag("etag"), DateTimeOffset.UtcNow, "lease-id-123"));

        _mockLeaseClient.Setup(l => l.RenewAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(renewResponse.Object);

        var serverLock = CreateLock();
        await serverLock.TryAcquireAsync(_serverId);

        // Act
        var result = await serverLock.RenewAsync(_serverId);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task RenewAsync_WhenLeaseLost_ReturnsFalse()
    {
        // Arrange — first acquire a lease
        var mockResponse = new Mock<Response<BlobLease>>();
        mockResponse.Setup(r => r.Value).Returns(BlobsModelFactory.BlobLease(new ETag("etag"), DateTimeOffset.UtcNow, "lease-id-123"));

        _mockBlobClient.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, new Mock<Response>().Object));

        _mockLeaseClient.Setup(l => l.AcquireAsync(It.IsAny<TimeSpan>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        _mockLeaseClient.Setup(l => l.RenewAsync(null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException((int)HttpStatusCode.Conflict, "Lease lost"));

        var serverLock = CreateLock();
        await serverLock.TryAcquireAsync(_serverId);

        // Act
        var result = await serverLock.RenewAsync(_serverId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task RenewAsync_WithoutPriorAcquire_ReturnsFalse()
    {
        // Arrange
        var serverLock = CreateLock();

        // Act
        var result = await serverLock.RenewAsync(_serverId);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ReleaseAsync_ReleasesLease()
    {
        // Arrange — first acquire a lease
        var mockResponse = new Mock<Response<BlobLease>>();
        mockResponse.Setup(r => r.Value).Returns(BlobsModelFactory.BlobLease(new ETag("etag"), DateTimeOffset.UtcNow, "lease-id-123"));

        _mockBlobClient.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, new Mock<Response>().Object));

        _mockLeaseClient.Setup(l => l.AcquireAsync(It.IsAny<TimeSpan>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockResponse.Object);

        var releaseResponse = new Mock<Response<ReleasedObjectInfo>>();
        _mockLeaseClient.Setup(l => l.ReleaseAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(releaseResponse.Object);

        var serverLock = CreateLock();
        await serverLock.TryAcquireAsync(_serverId);

        // Act
        await serverLock.ReleaseAsync(_serverId);

        // Assert
        _mockLeaseClient.Verify(l => l.ReleaseAsync(null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReleaseAsync_WhenNoLease_DoesNotThrow()
    {
        // Arrange
        var serverLock = CreateLock();

        // Act & Assert — should not throw
        await serverLock.ReleaseAsync(_serverId);

        // Verify release was never called since there was no lease
        _mockLeaseClient.Verify(l => l.ReleaseAsync(null, It.IsAny<CancellationToken>()), Times.Never);
    }
}
