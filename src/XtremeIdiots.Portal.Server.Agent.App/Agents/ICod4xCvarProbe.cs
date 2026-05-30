namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// One-shot probe that reads a known-good set of cvars from a CoD4x game server via
/// the Servers Integration RCON API. Used to surface server-side configuration drift
/// that could otherwise silently break log parsing or ban-file sync.
/// </summary>
/// <remarks>
/// Probe-and-log only — no enforcement. Each agent lifecycle probes once, the first
/// time it sees a CoD4x server with RCON enabled. Failures are logged and swallowed
/// so a flaky RCON connection never blocks agent startup.
/// </remarks>
public interface ICod4xCvarProbe
{
    /// <summary>
    /// Probes the configured CoD4x cvar set for <paramref name="context"/> if (and
    /// only if) the server is CoD4x and has RCON enabled. Safe to call multiple
    /// times — the probe self-gates so it runs at most once per process per server.
    /// </summary>
    Task ProbeAsync(ServerContext context, CancellationToken ct = default);
}
