using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Registration;

/// <summary>
/// Startup DI resolution smoke tests that mirror the Servers Integration API client
/// registration in <c>Program.cs</c>. The Servers client (as of 4.1.14, built on
/// MX.Api.Client 2.3.77's reflection-free <c>SharedCacheConfiguration</c>) ships no
/// library cache defaults — its surface is mostly live RCON / query data — so this
/// registration intentionally does NOT call <c>WithCachePartition</c> /
/// <c>WithCaching(UseLibraryDefaults)</c>. The purpose of these tests is to be the
/// crash-guard against the class of DI composition failure
/// (<see cref="ArgumentException"/> "The expression must invoke a method declared by
/// ...") that has previously taken sibling worker apps down on startup when typed
/// sub-API registrations were fanned out incorrectly.
///
/// If <c>Program.cs</c> ever adds caching or other builder options for the Servers
/// client, mirror them here so this test keeps reproducing the real host-boot
/// composition. MUST run under the default CI test filter.
/// </summary>
public class ServersApiClientRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Must stay in lockstep with Program.cs Servers client registration.
        services.AddServersApiClient(options => options
            .WithBaseUrl("https://servers.example.invalid/")
            .WithEntraIdAuthentication("api://portal-servers-test"));

        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void AddServersApiClient_ResolvesRootClient()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IServersApiClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public void AddServersApiClient_ResolvesAllTypedSubApisUsedByAgent_WithoutArgumentException()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IServersApiClient>();

        // Property access forces typed sub-API composition — this is the codepath that
        // has thrown ArgumentException in sibling apps when a shared cache delegate was
        // fanned across typed sub-APIs. On the reflection-free SharedCacheConfiguration
        // pipeline (MX.Api.Client 2.3.77+), each sub-API must resolve cleanly.
        //
        // Covers every Servers sub-API the agent actually calls at runtime
        // (RconBroadcastService, ServerSyncService, Cod4xCvarProbe,
        // CoD4xPluginLifecycleService) plus a representative sample of the surface
        // (IQueryApi, IMapsApi) as the task brief requires.
        ICoD4xRconApi cod4xRcon = client.CoD4xRcon.V1;
        ICod2RconApi cod2Rcon = client.Cod2Rcon.V1;
        ICod4RconApi cod4Rcon = client.Cod4Rcon.V1;
        ICod5RconApi cod5Rcon = client.Cod5Rcon.V1;
        IQueryApi query = client.Query.V1;
        IMapsApi maps = client.Maps.V1;

        Assert.NotNull(cod4xRcon);
        Assert.NotNull(cod2Rcon);
        Assert.NotNull(cod4Rcon);
        Assert.NotNull(cod5Rcon);
        Assert.NotNull(query);
        Assert.NotNull(maps);
    }
}
