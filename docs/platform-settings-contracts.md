# Platform Settings Contracts

This document describes the settings architecture used by `portal-server-agent` after the typed-contract migration.

## Architecture

- Typed contract source: `XtremeIdiots.Portal.Settings.Contracts.V1`.
- `RepositoryServerConfigProvider` consumes typed settings contracts for migrated namespaces such as:
  - `agent`
  - `banfiles`
  - `broadcasts`
- Provider logic remains fail-safe and does not execute behavior when required settings are invalid.
- Back-end persistence remains dynamic in `portal-repository` (`namespace + JSON string`).

## Migration Summary

- Old approach: mixed JSON parsing and namespace-specific assumptions in runtime provider paths.
- New approach: typed contracts + validators centralize schema behavior and reduce resolver drift.
- Compatibility shims can remain during rollout, but new settings behavior must be implemented against `XtremeIdiots.Portal.Settings.Contracts.V1`.
- Runtime ownership for CoD4x plugin-enabled servers is defined in [CoD4x Plugin Source Behaviour Matrix](./cod4x-plugin-source-behaviour-matrix.md).

## Troubleshooting Runbook

1. Agent skips a server unexpectedly.
   - Check provider logs for settings-validation failure.
   - Verify required namespace payloads are present and use supported schema versions.

2. Broadcast or banfile settings are ignored.
   - Confirm provider namespace resolution is receiving payloads from repository API.
   - Re-run targeted provider tests for fail-closed and fallback behavior.

3. Cross-repo behavior mismatch.
   - Verify consumer repos are pinned to the same published settings-contract package version.
