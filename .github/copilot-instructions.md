# Copilot Instructions

> Shared conventions: see [`.github-copilot/.github/instructions/terraform.instructions.md`](../../.github-copilot/.github/instructions/terraform.instructions.md) for the standard Terraform layout, providers, remote-state pattern, validation commands, and CI/CD workflows.

## Project Overview

This repository contains the XtremeIdiots Portal server agent — a .NET 9 Worker Service deployed as a Docker container on Azure Container Apps. It connects to game servers via FTP to tail logs, parses events, and publishes them to Azure Service Bus queues.

## Repository Layout

- `src/` — .NET 9 solution with Agent App and Tests projects.
- `src/Dockerfile` — Multi-stage Docker build for the agent container.
- `terraform/` — Infrastructure-as-code for Azure resources (Container App, Container App Environment, health alerts).
- `.github/workflows/` — CI/CD pipelines for build, Docker push, deploy (dev/prd), and environment management.

## Tech Stack

- .NET 9, C# 13, Worker Service (BackgroundService)
- Azure Container Apps (hosting)
- Azure Service Bus (event publishing)
- FluentFTP (game server log tailing)
- Application Insights (telemetry)
- Terraform with azurerm provider
- GitHub Actions CI/CD with Docker build/push

## Development Guidelines

- Run `dotnet build src/XtremeIdiots.Portal.Server.Agent.sln` to build.
- Run `dotnet test src/XtremeIdiots.Portal.Server.Agent.sln` to run tests.
- Docker build: `docker build -t portal-server-agent -f src/Dockerfile src/`
- Terraform: `terraform -chdir=terraform init -backend-config=backends/dev.backend.hcl` then `terraform -chdir=terraform plan -var-file=tfvars/dev.tfvars`.
- Ensure `terraform fmt -recursive` before committing Terraform changes.

## Terraform Conventions

- Use `data` sources for existing Azure resources (resource groups, client config, remote state).
- Follow file-per-resource pattern.
- Variables declared in `variables.tf` with environment-specific values in `terraform/tfvars/`.
