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

Detected truncation or rotation is logged as a possible data gap; replacement
files that grow past the previous offset may not be detectable.

The dedicated playtime producer uses the existing RCON poll for bounded
CoD2, CoD4 and CoD5 roster observations. CoD4x playtime is reserved for the
plugin; the agent does not emit CoD4x observations. Log records with
unverified freshness are not emitted as playtime. Sanitized pending
observations and source cursors use the existing server lock blob, with the
blob lease fencing writes. Coverage counters use a durable per-server producer
epoch (retained across process restarts) and are frozen with each roster; a roster skipped because the in-memory
outbox backlog is occupied increments the capacity counter. Send failures are
retried, not counted as attempt drops. Pending observations more than 24 hours
old are dropped and counted atomically because first submissions beyond that
age are rejected downstream. Stale or untrusted source data is recorded
separately as a gap rather than an outbox age drop. Gaps are logged
and recorded in the blob; they are not all represented by the coverage counters.
An observation or drop held only in memory while blob storage is unavailable
can be lost on abrupt termination. A stored gap does not establish that the
replacement log is fresh or that all source losses were detected.

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
