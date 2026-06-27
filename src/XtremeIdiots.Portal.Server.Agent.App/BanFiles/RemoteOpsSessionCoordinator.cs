using System.Collections.Concurrent;
using System.Threading.RateLimiting;

using XtremeIdiots.Portal.Server.Agent.App.Agents;

namespace XtremeIdiots.Portal.Server.Agent.App.BanFiles;

public sealed class RemoteOpsSessionCoordinator : IRemoteOpsSessionCoordinator, IAsyncDisposable
{
    private readonly IRemoteFileClientFactory _clientFactory;
    private readonly IRemoteOpsSessionPolicies _policies;
    private readonly RemoteOpsSessionOptions _options;
    private readonly ILogger<RemoteOpsSessionCoordinator> _logger;
    private readonly ConcurrencyLimiter _connectLimiter;
    private readonly ConcurrentDictionary<Guid, SessionState> _sessions = new();

    public RemoteOpsSessionCoordinator(
        IRemoteFileClientFactory clientFactory,
        IRemoteOpsSessionPolicies policies,
        RemoteOpsSessionOptions options,
        ILogger<RemoteOpsSessionCoordinator> logger)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectLimiter = new ConcurrencyLimiter(options.ToConnectLimiterOptions());
    }

    public Task ExecuteAsync(ServerContext context, Func<IRemoteFileClient, CancellationToken, Task> operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteAsync<object?>(
            context,
            async (client, token) =>
            {
                await operation(client, token).ConfigureAwait(false);
                return null;
            },
            ct);
    }

    public async Task<T> ExecuteAsync<T>(
        ServerContext context,
        Func<IRemoteFileClient, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        var state = _sessions.GetOrAdd(context.ServerId, static _ => new SessionState());

        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(context, state, ct).ConfigureAwait(false);

            state.LastUsedUtc = DateTime.UtcNow;
            return await operation(state.Client!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DisposeSessionAsync(state, ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _sessions)
        {
            try
            {
                await entry.Value.Gate.WaitAsync().ConfigureAwait(false);
                await DisposeSessionAsync(entry.Value, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                entry.Value.Gate.Release();
                entry.Value.Gate.Dispose();
            }
        }

        _connectLimiter.Dispose();
    }

    public async Task CloseServerSessionAsync(Guid serverId, CancellationToken ct = default)
    {
        if (!_sessions.TryRemove(serverId, out var state))
        {
            return;
        }

        var acquired = false;
        try
        {
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            await DisposeSessionAsync(state, ct).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                state.Gate.Release();
            }

            state.Gate.Dispose();
        }
    }

    private async Task EnsureConnectedAsync(ServerContext context, SessionState state, CancellationToken ct)
    {
        if (!state.IsConnected)
        {
            await ConnectAsync(context, state, ct).ConfigureAwait(false);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var idleAge = nowUtc - state.LastUsedUtc;
        var lifetime = nowUtc - state.CreatedUtc;

        if (idleAge > _options.IdleTimeout || lifetime > _options.MaxLifetime)
        {
            _logger.LogDebug(
                "[{Title}] Recycling shared ops session (idle {IdleAge}, lifetime {Lifetime})",
                context.Title,
                idleAge,
                lifetime);

            await DisposeSessionAsync(state, ct).ConfigureAwait(false);
            await ConnectAsync(context, state, ct).ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(ServerContext context, SessionState state, CancellationToken ct)
    {
        await using var lease = await _connectLimiter.AcquireAsync(1, ct).ConfigureAwait(false);

        await _policies.ConnectPipeline.ExecuteAsync(async token =>
        {
            var client = _clientFactory.Create(context);
            try
            {
                await client.ConnectAsync(token).ConfigureAwait(false);

                state.Client = client;
                state.CreatedUtc = DateTime.UtcNow;
                state.LastUsedUtc = state.CreatedUtc;
                state.IsConnected = true;
            }
            catch
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // best-effort dispose on failed connect
                }

                throw;
            }
        }, ct).ConfigureAwait(false);
    }

    private static async Task DisposeSessionAsync(SessionState state, CancellationToken ct)
    {
        if (state.Client is null)
        {
            state.IsConnected = false;
            return;
        }

        try
        {
            await state.Client.DisconnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort disconnect
        }

        try
        {
            await state.Client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            state.Client = null;
            state.IsConnected = false;
        }
    }

    private sealed class SessionState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public IRemoteFileClient? Client { get; set; }
        public bool IsConnected { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime LastUsedUtc { get; set; }
    }
}
