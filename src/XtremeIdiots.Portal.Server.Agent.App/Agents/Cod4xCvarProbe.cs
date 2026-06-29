using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Default <see cref="ICod4xCvarProbe"/> implementation. Reads three cvars via
/// <see cref="ICoD4xRconApi.CvarList"/> and emits structured log entries:
/// <list type="bullet">
///   <item><c>g_logSync</c> / <c>g_logTimeStampInSeconds</c> / <c>logfile</c> — probe
///     and record only. The parser tolerates common value combinations, so these
///     are observed for ops visibility rather than enforced.</item>
/// </list>
/// </summary>
public sealed class Cod4xCvarProbe : ICod4xCvarProbe
{
    /// <summary>The marker GameType string for CoD4x. Matches <c>GameType.CallOfDuty4x.ToString()</c>.</summary>
    internal const string Cod4xGameType = "CallOfDuty4x";

    /// <summary>Cvar names probed in order. Kept as a static array so tests can assert exact coverage.</summary>
    internal static readonly string[] ProbedCvars =
    {
        "g_logSync",
        "g_logTimeStampInSeconds",
        "logfile",
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<Cod4xCvarProbe> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _probed = new();

    public Cod4xCvarProbe(IServiceScopeFactory scopeFactory, ILogger<Cod4xCvarProbe> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ProbeAsync(ServerContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!string.Equals(context.GameType, Cod4xGameType, StringComparison.Ordinal))
        {
            return;
        }

        if (!context.RconEnabled)
        {
            return;
        }

        // Probe at most once per process per server. Concurrent invocations race
        // safely — TryAdd returns false on the loser without running the probe.
        if (!_probed.TryAdd(context.ServerId, 0))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var serversApiClient = scope.ServiceProvider.GetRequiredService<IServersApiClient>();
            var rconApi = serversApiClient.CoD4xRcon.V1;

            var cvarListResult = await rconApi.CvarList(context.ServerId, ct).ConfigureAwait(false);
            if (!cvarListResult.IsSuccess || string.IsNullOrWhiteSpace(cvarListResult.Result?.Data))
            {
                _logger.LogWarning(
                    "CoD4x cvar probe could not read cvarlist for server {ServerId} ({Title}): API returned non-success",
                    context.ServerId,
                    context.Title);
                return;
            }

            var cvarListOutput = cvarListResult.Result.Data;

            foreach (var cvar in ProbedCvars)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                ProbeOne(context, cvar, cvarListOutput);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown — let it propagate to the agent loop.
            throw;
        }
        catch (Exception ex)
        {
            // Don't let a probe failure block agent startup. The run-once gate is
            // process-scoped (singleton), so a failure here permanently disables the
            // probe for this server until the process is restarted — by design, to
            // avoid hammering an operator-side misconfiguration on every config change.
            _logger.LogWarning(ex,
                "CoD4x cvar probe failed for server {ServerId} ({Title})",
                context.ServerId, context.Title);
        }
    }

    private void ProbeOne(ServerContext context, string cvar, string cvarListOutput)
    {
        if (TryExtractCvarValue(cvarListOutput, cvar, out var value))
        {
            _logger.LogInformation(
                "CoD4x cvar probe: server {ServerId} ({Title}) {Cvar}={Value}",
                context.ServerId, context.Title, cvar, value);
            return;
        }

        _logger.LogWarning(
            "CoD4x cvar probe could not read {Cvar} for server {ServerId} ({Title}) from cvarlist output",
            cvar,
            context.ServerId,
            context.Title);
    }

    private static bool TryExtractCvarValue(string cvarListOutput, string cvarName, out string value)
    {
        value = string.Empty;

        foreach (var line in cvarListOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.Contains(cvarName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var cvarIndex = line.IndexOf(cvarName, StringComparison.OrdinalIgnoreCase);
            if (cvarIndex < 0)
            {
                continue;
            }

            var afterName = line[(cvarIndex + cvarName.Length)..].Trim();
            if (afterName.Length == 0)
            {
                value = string.Empty;
                return true;
            }

            var quoteStart = afterName.IndexOf('"');
            if (quoteStart >= 0)
            {
                var quoteEnd = afterName.IndexOf('"', quoteStart + 1);
                if (quoteEnd > quoteStart)
                {
                    value = afterName.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                    return true;
                }
            }

            value = afterName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
            return true;
        }

        return false;
    }
}
