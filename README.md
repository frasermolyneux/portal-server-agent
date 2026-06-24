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

## Overview

Portal Server Agent is a .NET 9 worker that tails game-server logs over FTP and publishes structured events to Azure Service Bus. It runs in Azure Container Apps and coordinates per-server agents for log parsing, ban-file monitoring, status publication, and RCON-backed vote handling. The produced events are consumed by downstream processing components in the portal event pipeline. Infrastructure and deployment are managed through Terraform and GitHub Actions.

## Contributing

Please read the [contributing](CONTRIBUTING.md) guidance; this is a learning and development project.

## Security

Please read the [security](SECURITY.md) guidance; I am always open to security feedback through email or opening an issue.
