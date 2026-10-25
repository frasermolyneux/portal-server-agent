using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Moq;

using MX.Observability.ApplicationInsights.AspNetCore;
using MX.Observability.ApplicationInsights.Filtering;
using MX.Observability.ApplicationInsights.Filtering.Configuration;

using XtremeIdiots.Portal.Server.Agent.App.Observability;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Observability;

public class AgentHeartbeatTelemetryTests
{
    [Theory]
    [InlineData(true, "", "Audit:", "AgentHeartbeat,Audit:BanFilePushed", "AgentHeartbeat,Audit:BanFilePushed,UnrelatedEvent")]
    [InlineData(true, "ExistingEvent", "", "AgentHeartbeat,ExistingEvent", "AgentHeartbeat,UnrelatedEvent")]
    [InlineData(true, "", "", "AgentHeartbeat,Audit:BanFilePushed,ExistingEvent,UnrelatedEvent", "AgentHeartbeat,UnrelatedEvent")]
    [InlineData(true, " , , ", " , ", "AgentHeartbeat,Audit:BanFilePushed,ExistingEvent,UnrelatedEvent", "AgentHeartbeat,UnrelatedEvent")]
    [InlineData(false, "", "Audit:", "AgentHeartbeat,Audit:BanFilePushed,ExistingEvent,UnrelatedEvent", "AgentHeartbeat,Audit:BanFilePushed,ExistingEvent,UnrelatedEvent")]
    public void FilteringRetainsHeartbeatAndPreservesConfiguredPolicyAcrossReload(
        bool enabled,
        string allowedNames,
        string allowedPrefixes,
        string expectedBeforeReload,
        string expectedAfterReload)
    {
        var section = $"{TelemetryFilterOptions.SectionName}:CustomEvents";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{section}:Enabled"] = enabled.ToString(),
                [$"{section}:AllowedNames"] = allowedNames,
                [$"{section}:AllowedNamePrefixes"] = allowedPrefixes
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddObservability(AgentHeartbeatTelemetry.ConfigureFiltering);
        using var provider = services.BuildServiceProvider();

        var retained = new List<string>();
        var next = new Mock<ITelemetryProcessor>();
        next.Setup(processor => processor.Process(It.IsAny<ITelemetry>()))
            .Callback<ITelemetry>(item => retained.Add(Assert.IsType<EventTelemetry>(item).Name));
        var processor = new TelemetryFilterProcessor(
            next.Object,
            provider.GetRequiredService<IOptionsMonitor<TelemetryFilterOptions>>());
        string[] names = ["AgentHeartbeat", "Audit:BanFilePushed", "ExistingEvent", "UnrelatedEvent"];

        foreach (var name in names)
        {
            processor.Process(new EventTelemetry(name));
        }

        Assert.Equal(expectedBeforeReload.Split(','), retained);

        configuration[$"{section}:AllowedNames"] = "UnrelatedEvent";
        configuration.Reload();
        retained.Clear();

        foreach (var name in names)
        {
            processor.Process(new EventTelemetry(name));
        }

        Assert.Equal(expectedAfterReload.Split(','), retained);
    }
}
