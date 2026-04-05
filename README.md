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
