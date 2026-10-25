using MX.Observability.ApplicationInsights.Filtering.Configuration;

namespace XtremeIdiots.Portal.Server.Agent.App.Observability;

internal static class AgentHeartbeatTelemetry
{
    internal const string EventName = "AgentHeartbeat";

    internal static void ConfigureFiltering(TelemetryFilterOptions options)
    {
        var customEvents = options.CustomEvents;
        var allowedNames = customEvents.AllowedNames?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var allowedPrefixes = customEvents.AllowedNamePrefixes?.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        // An empty allow-list deliberately retains all events; do not make it restrictive.
        if (!customEvents.Enabled ||
            (allowedNames.Length == 0 && allowedPrefixes.Length == 0))
        {
            return;
        }

        customEvents.AllowedNames = string.Join(
            ',', allowedNames.Append(EventName).Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
