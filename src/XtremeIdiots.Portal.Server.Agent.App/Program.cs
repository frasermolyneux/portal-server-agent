using XtremeIdiots.Portal.Server.Agent.App.Orchestration;

var builder = Host.CreateApplicationBuilder(args);

// TODO: Phase B — Add Repository API client, Service Bus publisher, log tailer/parser factories
builder.Services.AddHostedService<AgentOrchestrator>();

var host = builder.Build();
host.Run();
