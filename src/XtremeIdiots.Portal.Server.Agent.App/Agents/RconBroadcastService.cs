using Microsoft.Extensions.DependencyInjection;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;

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
        var rconApi = scope.ServiceProvider.GetRequiredService<IRconApi>();

        return await rconApi.Say(serverId, message);
    }
}
