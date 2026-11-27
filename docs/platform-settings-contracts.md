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

## Broadcast settings

An absent server `broadcasts` namespace inherits the global settings. For a
legacy server document, null or missing `enabled` and `intervalSeconds` inherit
their global values, while its playlist remains server-owned, including an
empty or missing list. Explicit `enabled: false` disables broadcasts.
An explicit `messagesMode: "Inherit"` selects the global playlist;
`"Replace"` selects the server playlist, including an empty one. Omit
`messagesMode` for legacy behavior. Invalid explicit modes or message arrays
disable that configuration rather than inheriting.

An unavailable global configuration prevents a refresh that needs inherited
values, leaving running agents on their previous settings. Effective changes
restart affected agents through the existing orchestrator; the scheduled
broadcast loop and startup announcement remain separate.

## CoD4x plugin Agent Name

`agent.agentName` is the complete rendered, color-coded bot prefix, not a bare
display name. The agent resolves the server override, then global setting, then
its built-in default. The plugin must use this value verbatim.

Install and rollback include the optional `agentName` in the generated
`portal-cod4x-plugin.config.json`. Live name updates use a separate, non-sensitive
`portal-cod4x-plugin.agent-name.json` beside that config:

```json
{
  "gameServerId": "<server GUID>",
  "agentName": "^4[^1>XI< BOT^4]^7"
}
```

The name must fit 139 UTF-8 bytes and contain no quotes, C0/DEL controls, or invalid
encoding. Invalid names produce warnings rather than truncation. Healthy plugins
receive sidecar updates when the effective name or path changes; failures and
cancellations do not mark the name as published and are retried on subsequent
lifecycle checks. The credential-bearing main configuration is not rewritten for
live name changes.

Managed install, rollback, and drift reload force a fresh sidecar upload before
loading the plugin. A failed upload prevents that load and is reported explicitly,
so a stale sidecar cannot silently override the effective name. Install/rollback
failures retain the existing terminal-operation behavior; re-request a failed
operation if no installed version is available for drift reconciliation.

The compatible plugin reads the sidecar at startup and every 60 seconds, requires
a matching server ID, and retains its last good name on missing or invalid reads.
Older plugins ignore the additional field and file; deploy the agent writer before
the plugin reader. Startup announcements, broadcast rotation, and other game
types retain their existing behavior.

## Troubleshooting Runbook

1. Agent skips a server unexpectedly.
   - Check provider logs for settings-validation failure.
   - Verify required namespace payloads are present and use supported schema versions.

2. Broadcast or banfile settings are ignored.
   - Confirm provider namespace resolution is receiving payloads from repository API.
   - Re-run targeted provider tests for fail-closed and fallback behavior.

3. Cross-repo behavior mismatch.
   - Verify consumer repos are pinned to the same published settings-contract package version.
