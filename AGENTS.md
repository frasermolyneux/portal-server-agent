# portal-server-agent

## Purpose

.NET 10 worker hosted as a Docker container in Azure Container Apps. It discovers
agent-enabled game servers, connects over FTP or SFTP, tails and parses logs, and
publishes server events to Azure Service Bus.

## Repository map

- `src/XtremeIdiots.Portal.Server.Agent.App/` — worker, orchestration, transports,
  parsers, storage, integrations, and event publishing.
- `src/XtremeIdiots.Portal.Server.Agent.App.Tests/` — unit tests.
- `src/Dockerfile` — production container build and entry point.
- `terraform/` — Container App, storage, identity/RBAC, monitoring, and remote-state
  integration for dev and production.
- `.github/workflows/` — CI, container delivery, infrastructure, and deployment.

## Boundaries

- This service is the event producer. Service Bus processing belongs in
  `portal-server-events`.
- Preserve the per-server lifecycle: configuration refresh starts, restarts, or
  stops agents; shutdown closes remote-operation sessions and releases resources.
- Blob-backed locks prevent concurrent ownership, and blob offsets preserve log
  tail progress. Do not weaken lease, offset, or cancellation behavior.
- Keep file transport, parsing, publishing, RCON synchronization, plugin lifecycle,
  and ban-file reconciliation as separate integration boundaries.
- Event payloads and queue names come from
  `XtremeIdiots.Portal.Server.Events.Abstractions.V1`; coordinate contract changes
  with its publisher/consumer compatibility.
- Runtime configuration comes from the Repository and Servers Integration APIs plus
  typed settings contracts. Do not embed server addresses or credentials.
- Azure App Configuration, Key Vault, Service Bus, and Blob Storage authenticate
  with managed identity. Do not introduce client secrets or connection strings.
- Preserve Container Apps hosting, health endpoints, and the Docker execution path.

## Change guidance

- Target .NET 10 and use the SDK pinned in `global.json`.
- Keep settings parsing centralized in `IServerConfigProvider` implementations and
  retain compatibility handling unless the consuming contract is deliberately
  changed.
- Add or update focused tests beside changed lifecycle, transport, parsing, storage,
  or publishing behavior.
- For Terraform changes, retain the existing azurerm backend, OIDC remote-state
  access, file-per-resource layout, and dev/prd backend and tfvars selection.

## Useful validation

Choose checks that cover the change:

```pwsh
dotnet build src/XtremeIdiots.Portal.Server.Agent.slnx
dotnet test src/XtremeIdiots.Portal.Server.Agent.slnx
dotnet format src/XtremeIdiots.Portal.Server.Agent.slnx --verify-no-changes
docker build -t portal-server-agent -f src/Dockerfile src/
terraform -chdir=terraform fmt -check -recursive
terraform -chdir=terraform validate
```
