using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Registration;

/// <summary>
/// Startup DI resolution smoke tests that mirror the Repository API client registration in
/// <c>Program.cs</c>. These lock in the "BaseUrl + Entra ID authentication only" registration
/// shape after the removal of the <c>WithCachePartition</c>/<c>WithCaching</c> calls that caused
/// deterministic <see cref="ArgumentException"/> failures during typed subclient composition on
/// Repository client 4.2.21.
/// </summary>
public class RepositoryApiClientRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddRepositoryApiClient(options => options
            .WithBaseUrl("https://repository.example.invalid/")
            .WithEntraIdAuthentication("api://portal-repository-test"));

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void AddRepositoryApiClient_WithBaseUrlAndEntraAuthOnly_ResolvesRootClient()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public void AddRepositoryApiClient_ResolvesAdminActionsSubclient_WithoutArgumentException()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();

        // Property access forces typed subclient composition — this is the codepath that threw
        // ArgumentException on Repository client 4.2.21 when consumer-side cache partition /
        // caching options were configured. Resolving without those options must succeed and
        // must produce the versioned IAdminActionsApi surface downstream services consume.
        var versionedAdminActions = client.AdminActions;
        Assert.NotNull(versionedAdminActions);

        IAdminActionsApi adminActions = versionedAdminActions.V1;
        Assert.NotNull(adminActions);
    }

    [Fact]
    public void AddRepositoryApiClient_ResolvesRepresentativeSubclients_WithoutArgumentException()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();

        // Touch every typed subclient the agent depends on at runtime (BanFileWatcher,
        // RepositoryServerConfigProvider, CoD4x reconciliation services, plugin lifecycle).
        // Any of these throwing during property access — or during resolution of their .V1
        // typed surface — would deterministically crash the worker on startup, which is the
        // exact regression this hotfix prevents.
        Assert.NotNull(client.AdminActions.V1);
        Assert.NotNull(client.BanFileMonitors.V1);
        Assert.NotNull(client.ConnectedPlayers.V1);
        Assert.NotNull(client.GameServers.V1);
        Assert.NotNull(client.GameServerConfigurations.V1);
        Assert.NotNull(client.GlobalConfigurations.V1);
        Assert.NotNull(client.Maps.V1);
        Assert.NotNull(client.Players.V1);
    }
}
