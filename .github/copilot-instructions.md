# Copilot Instructions

This repository is a .NET 10 worker that runs in Azure Container Apps. It manages
one agent per configured game server, tails logs over FTP/SFTP, parses game events,
and publishes the shared event contracts to Azure Service Bus.

## Work in this repository

- Application code and tests are under `src/`; the production image is built by
  `src/Dockerfile`; Azure infrastructure is under `terraform/`.
- Preserve `AgentOrchestrator` ownership of agent start/restart/stop behavior and
  `GameServerAgent` ownership of each server's run loop.
- Keep distributed blob leases and persisted offsets consistent with cancellation,
  reconnect, and shutdown behavior.
- Respect boundaries between remote file operations, parsing, RCON/plugin/ban-file
  synchronization, and Service Bus publishing.
- Treat `XtremeIdiots.Portal.Server.Events.Abstractions.V1` DTOs and queue names as
  cross-repository contracts. This repository publishes; it does not process them.
- Keep server configuration in Repository/Servers Integration API clients and typed
  settings providers. Never hard-code endpoints or credentials.
- Use managed identity for Azure App Configuration, Key Vault, Service Bus, and
  Blob Storage.
- Preserve Container Apps health checks and Docker entry-point behavior.

Use the exact SDK from `global.json`. Add focused tests for changed behavior and
select the relevant validation commands documented in `AGENTS.md`.
