using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;

using XtremeIdiots.Portal.Server.Agent.App.Observability;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Observability;

public class TelemetryInitializerTests
{
    [Fact]
    public void Initialize_SetsCloudRoleName()
    {
        // Arrange
        var initializer = new TelemetryInitializer();
        var telemetry = new RequestTelemetry();

        // Act
        initializer.Initialize(telemetry);

        // Assert
        Assert.Equal("Portal Server Agent", telemetry.Context.Cloud.RoleName);
    }
}
