using Azure.Messaging.ServiceBus;
using XtremeIdiots.Portal.Server.Events.Abstractions.V1;

namespace XtremeIdiots.Portal.Server.Agent.App.Playtime;

public sealed class PlaytimeServiceBusPublisher : IAsyncDisposable
{
    private readonly Lazy<ServiceBusSender> _sender;

    public PlaytimeServiceBusPublisher(ServiceBusClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _sender = new Lazy<ServiceBusSender>(() => client.CreateSender(Queues.PlaytimeObservation));
    }

    internal Task SendAsync(PlaytimeObservation observation, CancellationToken ct)
        => PlaytimeProducer.SendAsync(_sender.Value, observation, ct);

    public async ValueTask DisposeAsync()
    {
        if (_sender.IsValueCreated)
        {
            await _sender.Value.DisposeAsync().ConfigureAwait(false);
        }
    }
}
