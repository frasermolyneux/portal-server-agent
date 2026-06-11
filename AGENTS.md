# AGENTS.md — portal-server-agent

.NET 9 Worker Service (BackgroundService) deployed as a Docker container on Azure Container Apps. Connects to game servers via FTP, tails logs, parses events, and publishes them to Azure Service Bus queues — consumed downstream by portal-server-events.

This file is the brief for the **GitHub Copilot coding agent** (and any other agent that follows the [agents.md](https://agents.md) convention) when it runs in a cloud runner without the local VS Code multi-root workspace context.

> If you are a human reading this in VS Code, prefer `.github/copilot-instructions.md` for project orientation. `AGENTS.md` is the agent execution brief.

---

## Required reading (read these BEFORE doing any work)

The `copilot-setup-steps.yml` workflow checks out `frasermolyneux/.github-copilot` at `./.github-copilot/` in the runner, so the paths below resolve.

1. `.github/copilot-instructions.md` — repo-specific orientation, build commands, conventions
2. `.github-copilot/.github/instructions/personal.working-preferences.instructions.md`
3. `.github-copilot/.github/copilot-instructions.md` — org-wide catalog
4. Stack-specific files — see **Stack guardrails** below

---

## Org conventions via MCP (when available)

If a `frasermolyneux-copilot` MCP server is configured in your client (`~/.copilot/mcp-config.json`, VS Code user `mcp.json`, or an equivalent stdio MCP wire-up), **prefer its catalog tools** over your own assumptions when answering questions about org standards, branching, workflows, Terraform, .NET projects, Azure patterns, or shared library / platform consumption contracts. The catalog source-of-truth lives in `frasermolyneux/.github-copilot` — see `mcp-server/README.md` there for the tool contract.

This is **complementary** to the file-load model: if `./.github-copilot/` is checked out in the runner (per `copilot-setup-steps.yml`), continue to read those files directly. If both are available, prefer MCP for freshness. If no MCP server is configured in your client, treat this section as a no-op and fall back to the file paths above.

---

## Stack guardrails

### Tenant facts (always-on)
- `tenant.subscriptions`, `tenant.regions`, `tenant.identity`, `tenant.network-topology`

### Enforceable standards
- `standards.oidc-and-secrets` — **no client secrets** (FTP credentials are pulled from Key Vault via managed identity)
- `standards.dotnet-project`
- `standards.azure-naming`, `standards.azure-tagging`, `standards.terraform-style`
- `standards.branching-and-prs`

### Patterns
- `patterns.api-client` — consumes Portal Repository client + Servers Integration client
- `patterns.nbgv-versioning`
- `patterns.terraform-remote-state`

### Platform settings contracts
- Server configuration provider logic should consume typed contracts from `XtremeIdiots.Portal.Settings.Contracts.V1` for migrated namespaces.
- Do not introduce new ad hoc namespace/property parsing paths for migrated settings.
- Do not remove compatibility shims unless shim-removal gate criteria are met and evidenced.
- Follow `docs/platform-settings-contracts.md` for migration and troubleshooting guidance.

### Platform consumption contracts
- `platform.workloads`, `platform.monitoring`, `platform.registry` (ACR for the container image)

### Shared
- `shared.api-client-abstractions`
- `shared.observability-appinsights`
- `shared.actions` — Docker build/push composites in CI

---

## Build, test, format

```pwsh
dotnet build src/XtremeIdiots.Portal.Server.Agent.slnx
dotnet test src/XtremeIdiots.Portal.Server.Agent.slnx --filter "FullyQualifiedName!~IntegrationTests"
dotnet format src/XtremeIdiots.Portal.Server.Agent.slnx --verify-no-changes

docker build -t portal-server-agent -f src/Dockerfile src/

terraform -chdir=terraform fmt -check -recursive
terraform -chdir=terraform init -backend-config=backends/dev.backend.hcl
terraform -chdir=terraform validate
terraform -chdir=terraform plan -var-file=tfvars/dev.tfvars
```

---

## Do NOT

- ❌ Do not `git commit`, `git push`, force-push, rebase, or branch-mutate. Work on the assigned branch only.
- ❌ Do not introduce client secrets. Managed identity / Key Vault references only — including FTP / RCON credentials.
- ❌ Do not bypass `dotnet format`, `dotnet test`, `terraform fmt`, `terraform validate`, or the Docker build.
- ❌ **Do not add Service Bus consumer / event-processing logic here** — that belongs in portal-server-events. This repo is a **producer**.
- ❌ Do not embed game-server addresses or credentials in code or Terraform — they come from the Portal Repository API at runtime.
- ❌ Do not modify `.github/workflows/`, `.github/dependabot.yml`, or `version.json` unless that is the explicit task.
- ❌ Do not change the Docker base image tag without bumping `version.json` and validating the new image in dev.

- ❌ Do not pull context from sibling workspace folders. Only what is inside this repo and `./.github-copilot/` is in scope.
- ❌ Do not assume tools/SDKs are installed beyond what `.github/workflows/copilot-setup-steps.yml` provisions. If you need more, add the step and explain why.

---

## Opening the PR

You MUST use `.github/PULL_REQUEST_TEMPLATE.md` as your PR body — do **not** write a freeform body. The org template is inherited from `frasermolyneux/.github` and GitHub pre-populates it when you open the PR. Concretely:

1. Fill `## Summary` (one line) and `Closes #<issue>`.
2. Tick the relevant `## Type of change` box.
3. Paste the **actual command output** from your Build, Tests, and Format check runs into `## Validation evidence`. Show the real summary line, not "tests passed".
4. Fill `## Risk and rollout` — blast radius, auto-deploy?, manual steps post-merge, rollback plan.
5. Tick **every** box in `## Agent attestation`.
6. Delete `## Consumer impact` only if no published contract (Abstractions / Client NuGet / Service Bus DTO / Terraform output) changed.

Complete the `## Agent attestation` section before requesting review; reviewers use it as a readiness checklist.

---

## Pre-PR checks (run before you open the PR)

- [ ] `dotnet build` succeeds (clean)
- [ ] `dotnet test --filter "FullyQualifiedName!~IntegrationTests"` passes
- [ ] `dotnet format --verify-no-changes` passes
- [ ] `docker build` succeeds locally / in CI
- [ ] `terraform fmt -check -recursive` passes
- [ ] `terraform validate` + `terraform plan -var-file=tfvars/dev.tfvars` succeed
- [ ] No new secrets / GUIDs / connection strings
- [ ] PR body cites each acceptance criterion
- [ ] Risk/rollout section filled in

- [ ] `code-review` sub-agent run; High/Medium findings resolved or justified in the PR body

---

## Escalation

If you hit any of the conditions below, **open the PR as draft** and **apply the `needs-decision` label** instead of pushing forward to ready-for-review. Post a comment on the originating issue summarising what's blocking you and what decision is needed.

Stop and escalate when:

- The task implies adding Service Bus consumer logic here (wrong repo).
- ACR access from the runner identity is missing (`AcrPush` role).
- A `code-review` finding is **High** and cannot be resolved in-scope.
- A FTP/RCON credential schema change would force a coordinated change in `portal-environments` Key Vault entries.




