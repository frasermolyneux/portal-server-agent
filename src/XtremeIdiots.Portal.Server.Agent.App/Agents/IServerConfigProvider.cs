namespace XtremeIdiots.Portal.Server.Agent.App.Agents;

/// <summary>
/// Abstraction over the Repository API for fetching agent-enabled server configurations.
/// Implementations may call the Repository API, read from config, or return test data.
/// </summary>
public interface IServerConfigProvider
{
    /// <summary>
    /// Fetch all servers that have agent monitoring enabled.
    /// </summary>
    Task<IReadOnlyList<ServerContext>> GetAgentEnabledServersAsync(CancellationToken ct);
}

/// <summary>
/// Placeholder implementation that returns no servers.
/// Replace with Repository API client implementation when the NuGet package is available.
/// </summary>
public sealed class EmptyServerConfigProvider : IServerConfigProvider
{
    private readonly ILogger<EmptyServerConfigProvider> _logger;

    public EmptyServerConfigProvider(ILogger<EmptyServerConfigProvider> logger)
    {
        _logger = logger;
    }

    public Task<IReadOnlyList<ServerContext>> GetAgentEnabledServersAsync(CancellationToken ct)
    {
        _logger.LogWarning("Using placeholder server config provider — no servers will be monitored. " +
                           "Replace with Repository API client implementation.");
        return Task.FromResult<IReadOnlyList<ServerContext>>(Array.Empty<ServerContext>());
    }
}
