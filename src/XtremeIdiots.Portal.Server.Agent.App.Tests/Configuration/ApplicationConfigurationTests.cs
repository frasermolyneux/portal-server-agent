using System.Text.Json;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Configuration;

public class ApplicationConfigurationTests
{
    private static readonly string FixturesDirectory =
        Path.Combine(AppContext.BaseDirectory, "Configuration", "Fixtures");

    [Fact]
    public void BaseLoggingConfiguration_SuppressesProbeNoiseWithoutSuppressingApplicationInformation()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixturesDirectory, "appsettings.json")));

        var logLevels = document.RootElement
            .GetProperty("Logging")
            .GetProperty("LogLevel");

        Assert.Equal("Information", logLevels.GetProperty("Default").GetString());
        Assert.Equal(
            "Warning",
            logLevels.GetProperty("Microsoft.AspNetCore.Hosting.Diagnostics").GetString());
        Assert.Equal(
            "Warning",
            logLevels.GetProperty("Microsoft.AspNetCore.Routing.EndpointMiddleware").GetString());
    }

    [Fact]
    public void Program_KeepsLiveAndReadyHealthEndpointsConfigured()
    {
        var programSource = File.ReadAllText(Path.Combine(FixturesDirectory, "Program.cs"));

        Assert.Contains("app.MapHealthChecks(\"/health/live\"", programSource, StringComparison.Ordinal);
        Assert.Contains("app.MapHealthChecks(\"/health/ready\")", programSource, StringComparison.Ordinal);
    }
}
