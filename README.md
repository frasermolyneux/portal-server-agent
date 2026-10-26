# Portal Server Agent

[![Build and Test](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/build-and-test.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/build-and-test.yml)
[![Code Quality](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/codequality.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/codequality.yml)
[![Copilot Setup Steps](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/copilot-setup-steps.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/copilot-setup-steps.yml)
[![Dependabot Auto-Merge](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/dependabot-automerge.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/dependabot-automerge.yml)
[![Deploy Dev](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/deploy-dev.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/deploy-dev.yml)
[![Deploy Prd](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/deploy-prd.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/deploy-prd.yml)
[![Destroy Development](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/destroy-development.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/destroy-development.yml)
[![Destroy Environment](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/destroy-environment.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/destroy-environment.yml)
[![PR Verify](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/pr-verify.yml/badge.svg)](https://github.com/frasermolyneux/portal-server-agent/actions/workflows/pr-verify.yml)

## Documentation

* [Manual Steps](/docs/manual-steps.md) - Post-deployment and environment-specific operational actions
* [Platform Settings Contracts](/docs/platform-settings-contracts.md) - Typed settings contract usage and migration guidance
* [CoD4x Plugin Source Behaviour Matrix](/docs/cod4x-plugin-source-behaviour-matrix.md) - Runtime ownership matrix for plugin-enabled vs agent-owned behavior

## Overview

Portal Server Agent is a .NET 10 worker that tails game-server logs over FTP or SFTP and publishes structured events to Azure Service Bus. It runs in Azure Container Apps and coordinates per-server agents for log parsing, ban-file monitoring, status publication, and RCON-backed vote handling. The produced events are consumed by downstream processing components in the portal event pipeline. Infrastructure and deployment are managed through Terraform and GitHub Actions.

## Process health

The monitoring-owned contract mirrored under `src/contract/` publishes bounded
five-minute fleet and ban-delivery snapshots. Expected agents come from the full
paginated inventory, including invalid configurations and disabled-agent ban-sync
intent. Heartbeats prove loop progress; ban checks additionally report transport
failure, overdue delivery and import convergence. Publication acknowledgement is
not database persistence. Individual server failures remain aggregate Degraded;
operational per-server detail stays in Portal. Missing or stale inventory is Unknown.

## Contributing

Please read the [contributing](CONTRIBUTING.md) guidance; this is a learning and development project.

## Security

Please read the [security](SECURITY.md) guidance; I am always open to security feedback through email or opening an issue.
