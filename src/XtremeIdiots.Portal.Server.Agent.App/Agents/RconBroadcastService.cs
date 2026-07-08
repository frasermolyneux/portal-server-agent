using System.Net;

using Microsoft.Extensions.DependencyInjection;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

public sealed class RconBroadcastService : IRconBroadcastService
{
    private const string CallOfDuty2GameType = "CallOfDuty2";
    private const string CallOfDuty4GameType = "CallOfDuty4";
    private const string CallOfDuty4xGameType = "CallOfDuty4x";
    private const string CallOfDuty5GameType = "CallOfDuty5";

    private readonly IServiceScopeFactory _scopeFactory;

    public RconBroadcastService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<ApiResult> SayAsync(Guid serverId, string gameType, string message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var scope = _scopeFactory.CreateScope();
        var serversApiClient = scope.ServiceProvider.GetRequiredService<IServersApiClient>();

        if (string.Equals(gameType, CallOfDuty4xGameType, StringComparison.OrdinalIgnoreCase))
        {
            // CoD4x broadcasts use the console-say command, which is the game-specific
            // say variant exposed by the CoD4x RCON API.
            var result = await serversApiClient.CoD4xRcon.V1
                .ConSay(serverId, new CoD4xMessageRequestDto { Message = message }, ct)
                .ConfigureAwait(false);
            return Normalize(result);
        }

        if (string.Equals(gameType, CallOfDuty2GameType, StringComparison.OrdinalIgnoreCase))
        {
            var result = await serversApiClient.Cod2Rcon.V1
                .Say(serverId, new SayRequest { Message = message }, ct)
                .ConfigureAwait(false);
            return Normalize(result);
        }

        if (string.Equals(gameType, CallOfDuty4GameType, StringComparison.OrdinalIgnoreCase))
        {
            var result = await serversApiClient.Cod4Rcon.V1
                .Say(serverId, new SayRequest { Message = message }, ct)
                .ConfigureAwait(false);
            return Normalize(result);
        }

        if (string.Equals(gameType, CallOfDuty5GameType, StringComparison.OrdinalIgnoreCase))
        {
            var result = await serversApiClient.Cod5Rcon.V1
                .Say(serverId, new SayRequest { Message = message }, ct)
                .ConfigureAwait(false);
            return Normalize(result);
        }

        return new ApiResult(HttpStatusCode.BadRequest, new ApiResponse());
    }

    private static ApiResult Normalize(ApiResult result)
        => result.IsSuccess
            ? new ApiResult(result.StatusCode)
            : new ApiResult(result.StatusCode, new ApiResponse());

    private static ApiResult Normalize<T>(ApiResult<T> result)
        => result.IsSuccess
            ? new ApiResult(result.StatusCode)
            : new ApiResult(result.StatusCode, new ApiResponse());
}
