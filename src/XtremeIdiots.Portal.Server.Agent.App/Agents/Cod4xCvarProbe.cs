using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Default <see cref="ICod4xCvarProbe"/> implementation. Reads four cvars via
/// <see cref="IRconApi.GetDvar"/> and emits structured log entries:
/// <list type="bullet">
///   <item><c>sv_legacymode</c> — expected <c>"0"</c>. Non-zero values are logged as
///     <see cref="LogLevel.Warning"/> with a <c>Cod4xCvarMismatch</c> event name embedded
///     in the message template so they surface as warning traces in Application Insights
///     (in the <c>traces</c> table, with <c>EventName</c> as a custom dimension) and can be alerted on.</item>
///   <item><c>g_logSync</c> / <c>g_logTimeStampInSeconds</c> / <c>logfile</c> — probe
///     and record only. The parser tolerates both common value combinations, so these
///     are observed for ops visibility rather than enforced.</item>
/// </list>
/// </summary>
public sealed class Cod4xCvarProbe : ICod4xCvarProbe
{
    /// <summary>The marker GameType string for CoD4x. Matches <c>GameType.CallOfDuty4x.ToString()</c>.</summary>
    internal const string Cod4xGameType = "CallOfDuty4x";

    /// <summary>Structured-log event name for a non-zero <c>sv_legacymode</c>. Embedded in the warning message template as <c>{EventName}</c> so it surfaces in App Insights traces with <c>EventName</c> as a custom dimension.</summary>
    internal const string MismatchEventName = "Cod4xCvarMismatch";

    /// <summary>Cvar names probed in order. Kept as a static array so tests can assert exact coverage.</summary>
    internal static readonly string[] ProbedCvars =
    {
        "sv_legacymode",
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
            return;

        if (!context.RconEnabled)
            return;

        // Probe at most once per process per server. Concurrent invocations race
        // safely — TryAdd returns false on the loser without running the probe.
        if (!_probed.TryAdd(context.ServerId, 0))
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var rconApi = scope.ServiceProvider.GetRequiredService<IRconApi>();

            foreach (var cvar in ProbedCvars)
            {
                if (ct.IsCancellationRequested)
                    return;

                await ProbeOneAsync(rconApi, context, cvar, ct);
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

    private async Task ProbeOneAsync(IRconApi rconApi, ServerContext context, string cvar, CancellationToken ct)
    {
        try
        {
            var result = await rconApi.GetDvar(context.ServerId, cvar, ct);

            if (!result.IsSuccess || result.Result?.Data is null)
            {
                _logger.LogWarning(
                    "CoD4x cvar probe could not read {Cvar} for server {ServerId} ({Title}): API returned non-success",
                    cvar, context.ServerId, context.Title);
                return;
            }

            var value = result.Result.Data.Value ?? string.Empty;

            if (string.Equals(cvar, "sv_legacymode", StringComparison.Ordinal) &&
                !string.Equals(value, "0", StringComparison.Ordinal))
            {
                // Warning + named event so App Insights surfaces it as Cod4xCvarMismatch
                // in the traces table (EventName becomes a custom dimension).
                _logger.LogWarning(
                    "{EventName}: server {ServerId} ({Title}) reports {Cvar}={Actual}, expected {Expected}",
                    MismatchEventName, context.ServerId, context.Title, cvar, value, "0");
                return;
            }

            _logger.LogInformation(
                "CoD4x cvar probe: server {ServerId} ({Title}) {Cvar}={Value}",
                context.ServerId, context.Title, cvar, value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CoD4x cvar probe failed reading {Cvar} for server {ServerId} ({Title})",
                cvar, context.ServerId, context.Title);
        }
    }
}
