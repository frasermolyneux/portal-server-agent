using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;

namespace XtremeIdiots.Portal.Server.Agent.App.Observability;

/// <summary>
/// Sets the cloud role name for Application Insights telemetry.
/// </summary>
public sealed class TelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        telemetry.Context.Cloud.RoleName = "Portal Server Agent";
    }
}
