using System.Net;

using Microsoft.Extensions.DependencyInjection;

using Moq;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Agents;

public class RconBroadcastServiceTests
{
    private readonly Mock<IServersApiClient> _mockServersApiClient = new();
    private readonly Mock<IVersionedCoD4xRconApi> _mockVersionedCoD4xRconApi = new();
    private readonly Mock<ICoD4xRconApi> _mockCoD4xRconApi = new();
    private readonly Mock<IVersionedCod2RconApi> _mockVersionedCod2RconApi = new();
    private readonly Mock<ICod2RconApi> _mockCod2RconApi = new();
    private readonly Mock<IVersionedCod4RconApi> _mockVersionedCod4RconApi = new();
    private readonly Mock<ICod4RconApi> _mockCod4RconApi = new();
    private readonly Mock<IVersionedCod5RconApi> _mockVersionedCod5RconApi = new();
    private readonly Mock<ICod5RconApi> _mockCod5RconApi = new();
    private readonly Guid _serverId = Guid.NewGuid();

    private RconBroadcastService CreateService()
    {
        _mockVersionedCoD4xRconApi.Setup(x => x.V1).Returns(_mockCoD4xRconApi.Object);
        _mockVersionedCod2RconApi.Setup(x => x.V1).Returns(_mockCod2RconApi.Object);
        _mockVersionedCod4RconApi.Setup(x => x.V1).Returns(_mockCod4RconApi.Object);
        _mockVersionedCod5RconApi.Setup(x => x.V1).Returns(_mockCod5RconApi.Object);
        _mockServersApiClient.Setup(x => x.CoD4xRcon).Returns(_mockVersionedCoD4xRconApi.Object);
        _mockServersApiClient.Setup(x => x.Cod2Rcon).Returns(_mockVersionedCod2RconApi.Object);
        _mockServersApiClient.Setup(x => x.Cod4Rcon).Returns(_mockVersionedCod4RconApi.Object);
        _mockServersApiClient.Setup(x => x.Cod5Rcon).Returns(_mockVersionedCod5RconApi.Object);

        _mockCoD4xRconApi
            .Setup(x => x.ConSay(It.IsAny<Guid>(), It.IsAny<CoD4xMessageRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(HttpStatusCode.OK));
        _mockCod2RconApi
            .Setup(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        _mockCod4RconApi
            .Setup(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        _mockCod5RconApi
            .Setup(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        var services = new ServiceCollection();
        services.AddSingleton(_mockServersApiClient.Object);
        var sp = services.BuildServiceProvider();

        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new RconBroadcastService(scopeFactory);
    }

    [Fact]
    public async Task SayAsync_CoD4x_RoutesToConSayAndNotOtherGames()
    {
        var service = CreateService();

        var result = await service.SayAsync(_serverId, "CallOfDuty4x", "hello", CancellationToken.None);

        Assert.True(result.IsSuccess);
        _mockCoD4xRconApi.Verify(
            x => x.ConSay(_serverId, It.Is<CoD4xMessageRequestDto>(r => r.Message == "hello"), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCod2RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod4RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod5RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SayAsync_CoD2_RoutesToCod2Say()
    {
        var service = CreateService();

        var result = await service.SayAsync(_serverId, "CallOfDuty2", "hello", CancellationToken.None);

        Assert.True(result.IsSuccess);
        _mockCod2RconApi.Verify(
            x => x.Say(_serverId, It.Is<SayRequest>(r => r.Message == "hello"), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCoD4xRconApi.Verify(x => x.ConSay(It.IsAny<Guid>(), It.IsAny<CoD4xMessageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod4RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod5RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SayAsync_CoD4_RoutesToCod4Say()
    {
        var service = CreateService();

        var result = await service.SayAsync(_serverId, "CallOfDuty4", "hello", CancellationToken.None);

        Assert.True(result.IsSuccess);
        _mockCod4RconApi.Verify(
            x => x.Say(_serverId, It.Is<SayRequest>(r => r.Message == "hello"), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCod2RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod5RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SayAsync_CoD5_RoutesToCod5Say()
    {
        var service = CreateService();

        var result = await service.SayAsync(_serverId, "CallOfDuty5", "hello", CancellationToken.None);

        Assert.True(result.IsSuccess);
        _mockCod5RconApi.Verify(
            x => x.Say(_serverId, It.Is<SayRequest>(r => r.Message == "hello"), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCod2RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod4RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SayAsync_UnsupportedGameType_ReturnsFailureAndCallsNoRconApi()
    {
        var service = CreateService();

        var result = await service.SayAsync(_serverId, "Insurgency", "hello", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        _mockCoD4xRconApi.Verify(x => x.ConSay(It.IsAny<Guid>(), It.IsAny<CoD4xMessageRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod2RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod4RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockCod5RconApi.Verify(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SayAsync_NonSuccessDownstream_PropagatesFailure()
    {
        var service = CreateService();
        _mockCod2RconApi
            .Setup(x => x.Say(It.IsAny<Guid>(), It.IsAny<SayRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.InternalServerError, new ApiResponse()));

        var result = await service.SayAsync(_serverId, "CallOfDuty2", "hello", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
    }
}
