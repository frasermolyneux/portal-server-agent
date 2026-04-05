using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;

using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Observability;
using XtremeIdiots.Portal.Server.Agent.App.Orchestration;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

var builder = WebApplication.CreateBuilder(args);

// Azure App Configuration
var appConfigEndpoint = builder.Configuration["AzureAppConfiguration:Endpoint"];
if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
{
    var managedIdentityClientId = builder.Configuration["AzureAppConfiguration:ManagedIdentityClientId"];
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = managedIdentityClientId
    });

    var environmentLabel = builder.Configuration["AzureAppConfiguration:Environment"];

    builder.Configuration.AddAzureAppConfiguration(options =>
    {
        options.Connect(new Uri(appConfigEndpoint), credential)
            .Select("RepositoryApi:*", environmentLabel)
            .ConfigureRefresh(refresh =>
            {
                refresh.Register("Sentinel", environmentLabel, refreshAll: true)
                    .SetRefreshInterval(TimeSpan.FromMinutes(5));
            });

        options.ConfigureKeyVault(kv =>
        {
            kv.SetCredential(credential);
            kv.SetSecretRefreshInterval(TimeSpan.FromHours(1));
        });
    });
}

// Application Insights
builder.Services.AddSingleton<ITelemetryInitializer, TelemetryInitializer>();
builder.Services.AddApplicationInsightsTelemetry();

// Repository API client
builder.Services.AddRepositoryApiClient(options => options
    .WithBaseUrl(builder.Configuration["RepositoryApi:BaseUrl"]!)
    .WithEntraIdAuthentication(builder.Configuration["RepositoryApi:ApplicationAudience"]!));

// Server config provider
builder.Services.AddSingleton<IServerConfigProvider, RepositoryServerConfigProvider>();

// Service Bus client (publishes events)
builder.Services.AddSingleton(sp =>
{
    var fqns = builder.Configuration["ServiceBusConnection:fullyQualifiedNamespace"];
    if (string.IsNullOrEmpty(fqns))
        throw new InvalidOperationException("ServiceBusConnection:fullyQualifiedNamespace is not configured");

    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = builder.Configuration["ServiceBusConnection:ManagedIdentityClientId"]
    });
    return new ServiceBusClient(fqns, credential);
});

// Blob storage (offset persistence)
builder.Services.AddSingleton(sp =>
{
    var endpoint = builder.Configuration["AgentStorage:BlobEndpoint"];
    if (string.IsNullOrEmpty(endpoint))
        throw new InvalidOperationException("AgentStorage:BlobEndpoint is not configured");

    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"]
    });
    return new BlobServiceClient(new Uri(endpoint), credential);
});

// Agent components
builder.Services.AddSingleton<ILogTailerFactory, LogTailerFactory>();
builder.Services.AddSingleton<ILogParserFactory, LogParserFactory>();
builder.Services.AddSingleton<IEventPublisher, ServiceBusEventPublisher>();
builder.Services.AddSingleton<IOffsetStore, BlobOffsetStore>();
builder.Services.AddSingleton<IServerLock, BlobServerLock>();

// Agent orchestrator (singleton + hosted service so health checks can access it)
builder.Services.AddSingleton<AgentOrchestrator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentOrchestrator>());

// Health checks
builder.Services.AddHealthChecks()
    .AddCheck<AgentHealthCheck>("agent-status");

var app = builder.Build();

app.MapHealthChecks("/healthz");

app.Run();
