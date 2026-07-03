# CoD4x Plugin Source Behaviour Matrix

This matrix defines runtime ownership when `IsCod4xPluginSourceEnabled` is enabled for a CoD4x server.

## Scope

- Goal: prevent duplicate handling for plugin-equivalent paths.
- Constraint: log tailing must continue even when plugin ownership is enabled.
- Toggle source: server configuration loaded by `RepositoryServerConfigProvider`.

## Agent Matrix

| Behaviour                              | CoD4x + Plugin Enabled | CoD4x + Plugin Disabled   | Non-CoD4x      |
| -------------------------------------- | ---------------------- | ------------------------- | -------------- |
| Log tailing / parse loop               | Enabled                | Enabled                   | Enabled        |
| Parsed event publish (non-chat)        | Suppressed             | Enabled                   | Enabled        |
| Chat message publish                   | Enabled                | Enabled                   | Enabled        |
| Server status publish                  | Suppressed             | Enabled                   | Enabled        |
| Player IP resolved publish (RCON sync) | Suppressed             | Enabled                   | Enabled        |
| Startup online broadcast (RCON `say`)  | Suppressed             | Enabled                   | Not applicable |
| Scheduled broadcasts (RCON `say`)      | Suppressed             | Enabled (when configured) | Not applicable |
| Ban file check and publish             | Enabled                | Enabled                   | Enabled        |
| Lock lease + offset persistence        | Enabled                | Enabled                   | Enabled        |

## Processor Matrix (portal-server-events)

These are downstream command/welcome ownership rules used with the same plugin-source toggle:

| Behaviour                              | CoD4x + Plugin Enabled | CoD4x + Plugin Disabled | Non-CoD4x |
| -------------------------------------- | ---------------------- | ----------------------- | --------- |
| Chat command execution in processor    | Suppressed             | Enabled                 | Enabled   |
| Welcome message execution in processor | Suppressed             | Enabled                 | Enabled   |

## Operational Notes

- Re-enable behavior is automatic after config refresh/restart when plugin source is toggled off.
- This matrix intentionally keeps chat ingestion active to preserve non-plugin-owned command paths.
- Duplicate detection is not implemented here and remains an accepted risk.