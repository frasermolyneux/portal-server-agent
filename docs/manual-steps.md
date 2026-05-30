# Manual Steps

These are the manual prerequisites and one-off operator tasks that sit outside Terraform / CI for `portal-server-agent`. Keep this short — anything that *can* be automated should be.

---

## CoD4x server operator prerequisites

`portal-server-agent` supports CoD4x as a distinct `GameType` (`CallOfDuty4x`) alongside vanilla `CallOfDuty4`. CoD4x writes a different log format (UTC datestamped per-line; modern player identifiers) and ships with its own ban-file plugins. Server operators must apply the steps below **once per CoD4x server** before the agent is enabled.

### Required server-side plugins

Both plugins are required so the agent's dual-format ban-file push (Phase 3) lands cleanly:

| Plugin          | Purpose                                                                                           |
| --------------- | ------------------------------------------------------------------------------------------------- |
| `legacybanlist` | Maintains the original `ban.txt` (vanilla CoD4 format) — preserved for backwards compatibility.   |
| `simplebanlist` | Maintains the CoD4x "simple" ban list — the format actually consumed by CoD4x servers at runtime. |

Add both to the server's plugin load configuration (typically `loadPlugin legacybanlist` and `loadPlugin simplebanlist` in `server.cfg`). The agent pushes both files on every ban-file sync cycle; either plugin missing on the server side will silently drop the corresponding ban list.

### Required server-side cvars

The agent's CoD4x log parser is built against the **modern** CoD4x log format. The following cvar values are checked once per agent lifecycle via a startup probe (`Cod4xCvarProbe`) that runs immediately after the first successful RCON sync. The probe is **read-only** — it never writes cvars.

| Cvar                      | Required value               | Action on mismatch                                                                                          |
| ------------------------- | ---------------------------- | ----------------------------------------------------------------------------------------------------------- |
| `g_logSync`               | `1` or `3` (either accepted) | Probe-only — value is logged at Information level for inventory. `3` (instant flush) is preferred for low-latency event ingest; `1` (per-line flush) is acceptable. |
| `g_logTimeStampInSeconds` | `0` or `1` (either accepted) | Probe-only — value is logged at Information level for inventory. Parser handles both timestamp shapes.      |
| `logfile`                 | any non-zero value           | Probe-only — value is logged at Information level for inventory. `0` means no log file is written and the agent will have nothing to tail.  |

### Verifying after rollout

1. Bring the agent up against the server (`RconEnabled=true`, `GameType="CallOfDuty4x"`).
2. Optional — to see the per-cvar inventory traces (`g_logSync`, `g_logTimeStampInSeconds`, and `logfile`), temporarily override `ApplicationInsights:TelemetryFilter:Traces:MinSeverity` to `Information` in App Configuration. The default `Warning` minimum filters these out.

### Cross-references

- Log parser: `src/XtremeIdiots.Portal.Server.Agent.App/Parsing/Cod4xLogParser.cs` (Phase 1 — modern format only).
- Ban-file dual-format push: `src/XtremeIdiots.Portal.Server.Agent.App/BanFiles/` (Phase 3 — pushes both `ban.txt` legacy and CoD4x simple formats).
- Cvar probe service: `src/XtremeIdiots.Portal.Server.Agent.App/Agents/Cod4xCvarProbe.cs`.
