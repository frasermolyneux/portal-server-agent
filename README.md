# Portal Server Agent

Game server agent for the XtremeIdiots portal. Collects events from game servers via FTP log tailing and publishes structured events to Azure Service Bus, hosted on Azure Container Apps.

## Architecture

This agent is a .NET 9 Worker Service that manages persistent connections to game servers. It:

- **Tails game server logs** via FTP to capture player events in near-real-time
- **Parses CoD2/CoD4/CoD5 log formats** into structured events
- **Publishes events** to Azure Service Bus queues (consumed by portal-server-events processors)
- **Monitors ban files** for changes via FTP
- **Publishes periodic server status** snapshots
- **Handles map vote commands** (`!like`/`!dislike`) via RCON

## Project Structure

```
src/
├── XtremeIdiots.Portal.Server.Agent.App/          # Worker Service
│   ├── Orchestration/     # AgentOrchestrator (BackgroundService)
│   ├── Agents/            # Per-server GameServerAgent
│   ├── LogTailing/        # FTP log file polling
│   ├── Parsing/           # CoD log line parsers
│   ├── Rcon/              # Quake3 RCON client (UDP)
│   ├── Publishing/        # Service Bus event publisher
│   └── BanFiles/          # Ban file change monitoring
├── XtremeIdiots.Portal.Server.Agent.App.Tests/
└── Dockerfile
```

## Dependencies

- `XtremeIdiots.Portal.Server.Events.Abstractions.V1` — Event contracts (NuGet)
- `XtremeIdiots.Portal.Repository.Api.Client.V1` — Fetch server configurations (NuGet)

## Platform Settings Contracts

Settings consumed by the agent runtime are resolved through typed contracts from `XtremeIdiots.Portal.Settings.Contracts.V1`.

See `docs/platform-settings-contracts.md` for migration and troubleshooting guidance.

## Local dev: MCP wire-up

This repo wires the shared `frasermolyneux-copilot` MCP server for AI agents (Copilot CLI, VS Code Copilot Chat, the GitHub Copilot coding agent). It is configured via `.github/copilot/mcp_config.json` and built in `.github/workflows/copilot-setup-steps.yml` (Node 20 + `npm ci && npm run build` in `.github-copilot/mcp-server`, pinned to `frasermolyneux/.github-copilot@refs/tags/v0.1.0`).

For tool contract details, content-root resolution, and per-client wire-up snippets, see `.github-copilot/mcp-server/README.md` in the [`frasermolyneux/.github-copilot`](https://github.com/frasermolyneux/.github-copilot) repository.
