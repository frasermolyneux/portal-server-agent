using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Registration;

/// <summary>
/// Startup DI resolution smoke tests that mirror the Repository API client registration in
/// <c>Program.cs</c> — including the consumer-side <c>WithCachePartition</c> +
/// <c>WithCaching(UseLibraryDefaults)</c> composition. Repository client 4.2.21 (on
/// MX.Api.Client 2.3.76) deterministically threw
/// <see cref="ArgumentException"/> ("The expression must invoke a method declared by
/// ...IAdminActionsApi ...") during typed subclient composition when this shape was used,
/// because a single cache delegate was fanned across ~34 typed sub-API registrations.
/// Repository 4.2.22 + MX.Api.Client 2.3.77 (reflection-free <c>SharedCacheConfiguration</c>)
/// scopes each cache policy to its matching typed sub-API and must resolve cleanly.
/// This test is the "never again" gate for that regression and MUST run under the
/// default CI test filter.
/// </summary>
public class RepositoryApiClientRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // NOTE: This must stay in lockstep with Program.cs. If Program.cs adds/removes a
        // Repository client option (base URL, auth, cache partition, caching, policy), mirror
        // it here so the smoke test keeps reproducing the real host-boot composition.
        services.AddRepositoryApiClient(options => options
            .WithBaseUrl("https://repository.example.invalid/")
            .WithEntraIdAuthentication("api://portal-repository-test")
            .WithCachePartition("portal-server-agent")
            .WithCaching(c => c.UseLibraryDefaults()));

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void AddRepositoryApiClient_WithLibraryCachingDefaults_ResolvesRootClient()
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
        // caching options were configured. On 4.2.22 (MX.Api.Client 2.3.77 reflection-free
        // SharedCacheConfiguration) resolution must succeed and produce the versioned
        // IAdminActionsApi surface downstream services consume.
        var versionedAdminActions = client.AdminActions;
        Assert.NotNull(versionedAdminActions);

        IAdminActionsApi adminActions = versionedAdminActions.V1;
        Assert.NotNull(adminActions);
    }

    [Fact]
    public void AddRepositoryApiClient_ResolvesAllTypedSubclientsUsedByAgent_WithoutArgumentException()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();

        // Touch every typed subclient the agent depends on at runtime (BanFileWatcher,
        // RepositoryServerConfigProvider, CoD4x reconciliation/plugin lifecycle services).
        // Any of these throwing during property access — or during resolution of their .V1
        // typed surface — would deterministically crash the worker on startup. This is the
        // exact regression that took portal-sync and portal-repository-func down on 4.2.21
        // and that hotfix PR #65 disabled caching to avoid; 4.2.22 makes it safe again.
        Assert.NotNull(client.AdminActions.V1);
        Assert.NotNull(client.BanFileMonitors.V1);
        Assert.NotNull(client.ConnectedPlayers.V1);
        Assert.NotNull(client.GameServers.V1);
        Assert.NotNull(client.GameServersEvents.V1);
        Assert.NotNull(client.GameServerConfigurations.V1);
        Assert.NotNull(client.GlobalConfigurations.V1);
        Assert.NotNull(client.Maps.V1);
        Assert.NotNull(client.Players.V1);
    }
}

