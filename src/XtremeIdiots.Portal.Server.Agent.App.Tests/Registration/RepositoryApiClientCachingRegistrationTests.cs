using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Tests.Registration;

/// <summary>
/// Guardrail tests locking in the Repository API client caching registration used by
/// <c>Program.cs</c>. The agent enables library-default caching (game-server single/list
/// reads at 60s L1 and map single/list reads at 10m L1) and relies on server-side Tiered
/// caching for configuration reads. No consumer-side cache-policy overrides are expected.
/// </summary>
public sealed class RepositoryApiClientCachingRegistrationTests
{
    private const string TestPartition = "portal-server-agent";

    [Fact]
    public void AddRepositoryApiClient_WithLibraryDefaults_EnablesCachingAndDefaults()
    {
        var services = new ServiceCollection();

        services.AddRepositoryApiClient(options => options
            .WithBaseUrl("https://example.invalid")
            .WithEntraIdAuthentication("api://example")
            .WithCachePartition(TestPartition)
            .WithCaching(c => c.UseLibraryDefaults()));

        using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<RepositoryApiClientOptions>>().Value;

        Assert.True(opts.EnableCaching, "Repository API client caching should be enabled via WithCaching(UseLibraryDefaults).");
        Assert.True(opts.UseLibraryCacheDefaults, "Repository API client should opt in to library cache defaults (game-servers, maps).");
        Assert.Equal(TestPartition, opts.CachePartition);
    }

    [Fact]
    public void AddRepositoryApiClient_WithLibraryDefaults_DoesNotRegisterConsumerCachePolicyOverrides()
    {
        var services = new ServiceCollection();

        services.AddRepositoryApiClient(options => options
            .WithBaseUrl("https://example.invalid")
            .WithEntraIdAuthentication("api://example")
            .WithCachePartition(TestPartition)
            .WithCaching(c => c.UseLibraryDefaults()));

        using var provider = services.BuildServiceProvider();
        var opts = provider.GetRequiredService<IOptions<RepositoryApiClientOptions>>().Value;

        Assert.Empty(opts.CachePolicies);
        Assert.Empty(opts.CachePolicyOperations);
    }
}

