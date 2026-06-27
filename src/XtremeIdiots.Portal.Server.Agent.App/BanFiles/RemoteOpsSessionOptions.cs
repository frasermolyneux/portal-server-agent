using System.Threading.RateLimiting;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public sealed record RemoteOpsSessionOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxConcurrentConnects { get; init; } = 15;
    public int MaxConcurrentOpsPerServer { get; init; } = 1;
    public int CircuitBreakerFailureThreshold { get; init; } = 5;
    public TimeSpan CircuitBreakerBreakDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxConnectRetryAttempts { get; init; } = 3;
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    public ConcurrencyLimiterOptions ToConnectLimiterOptions()
    {
        return new ConcurrencyLimiterOptions
        {
            PermitLimit = MaxConcurrentConnects,
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        };
    }
}
