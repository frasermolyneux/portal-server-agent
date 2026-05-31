using MX.Api.Abstractions;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public interface IRconBroadcastService
{
    Task<ApiResult> SayAsync(Guid serverId, string message, CancellationToken ct = default);
}
