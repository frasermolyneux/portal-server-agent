using Microsoft.Extensions.DependencyInjection;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public sealed class RconBroadcastService : IRconBroadcastService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public RconBroadcastService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<ApiResult> SayAsync(Guid serverId, string message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var scope = _scopeFactory.CreateScope();
        var serversApiClient = scope.ServiceProvider.GetRequiredService<IServersApiClient>();
        var rconApi = serversApiClient.CoD4xRcon.V1;

        var result = await rconApi.ConSay(
            serverId,
            new CoD4xMessageRequestDto { Message = message },
            ct).ConfigureAwait(false);

        return result.IsSuccess
            ? new ApiResult(result.StatusCode)
            : new ApiResult(result.StatusCode, new ApiResponse());
    }
}
