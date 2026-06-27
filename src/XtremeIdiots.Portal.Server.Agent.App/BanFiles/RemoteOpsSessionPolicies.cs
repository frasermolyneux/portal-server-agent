using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public interface IRemoteOpsSessionPolicies
{
    ResiliencePipeline ConnectPipeline { get; }
}

public sealed class RemoteOpsSessionPolicies : IRemoteOpsSessionPolicies
{
    public RemoteOpsSessionPolicies(RemoteOpsSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var timeout = new TimeoutStrategyOptions
        {
            Timeout = options.ConnectTimeout,
            OnTimeout = static _ => default
        };

        var retry = new RetryStrategyOptions
        {
            MaxRetryAttempts = options.MaxConnectRetryAttempts,
            Delay = options.BaseRetryDelay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            ShouldHandle = static args => ValueTask.FromResult(args.Outcome.Exception is not OperationCanceledException)
        };

        ConnectPipeline = new ResiliencePipelineBuilder()
            .AddTimeout(timeout)
            .AddRetry(retry)
            .Build();
    }

    public ResiliencePipeline ConnectPipeline { get; }
}
