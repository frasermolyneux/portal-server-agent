using MX.Observability.ApplicationInsights.Filtering.Configuration;

namespace XtremeIdiots.Portal.Server.Agent.App.Observability;

internal static class AgentHeartbeatTelemetry
{
    internal const string EventName = "AgentHeartbeat";

    internal static void ConfigureFiltering(TelemetryFilterOptions options)
    {
        var customEvents = options.CustomEvents;

        // An empty allow-list deliberately retains all events; do not make it restrictive.
        if (!customEvents.Enabled ||
            (string.IsNullOrWhiteSpace(customEvents.AllowedNames) &&
             string.IsNullOrWhiteSpace(customEvents.AllowedNamePrefixes)))
        {
            return;
        }

        customEvents.AllowedNames = string.IsNullOrWhiteSpace(customEvents.AllowedNames)
            ? EventName
            : $"{customEvents.AllowedNames},{EventName}";
    }
}
