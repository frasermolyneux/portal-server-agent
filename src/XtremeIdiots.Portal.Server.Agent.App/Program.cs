using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;

using Microsoft.Extensions.Configuration.AzureAppConfiguration;

using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Server.Agent.App.Agents;
using XtremeIdiots.Portal.Server.Agent.App.LogTailing;
using XtremeIdiots.Portal.Server.Agent.App.Orchestration;
using XtremeIdiots.Portal.Server.Agent.App.Parsing;
using XtremeIdiots.Portal.Server.Agent.App.Publishing;

var builder = Host.CreateApplicationBuilder(args);

// Azure App Configuration
var appConfigEndpoint = builder.Configuration["AzureAppConfiguration:Endpoint"];
if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
{
    var managedIdentityClientId = builder.Configuration["AzureAppConfiguration:ManagedIdentityClientId"];
    var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = managedIdentityClientId
    });

    builder.Configuration.AddAzureAppConfiguration(options =>
    {
        options.Connect(new Uri(appConfigEndpoint), credential)
            .Select("RepositoryApi:*", builder.Configuration["AzureAppConfiguration:Environment"])
            .ConfigureRefresh(refresh =>
            {
                refresh.SetRefreshInterval(TimeSpan.FromMinutes(5));
            });
    });
}

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

// Agent orchestrator
builder.Services.AddHostedService<AgentOrchestrator>();

var host = builder.Build();
host.Run();
