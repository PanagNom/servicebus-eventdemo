using Azure.Messaging.ServiceBus;

namespace Messaging;

public sealed class ServiceBusEventPublisher : IEventPublisher
{
    private readonly ServiceBusSender _serviceBusSender;
    public ServiceBusEventPublisher(ServiceBusSender serviceBusSender)
    {
        _serviceBusSender = serviceBusSender ?? throw new ArgumentNullException(nameof(serviceBusSender));
    }

    public async Task PublishAsync<TEvent>(TEvent @event, string subject, string messageId, string correlationId, CancellationToken cancellationToken = default) where TEvent : class
    {
        var message = MessageEnvelope.CreateMessage(@event, subject, messageId, correlationId);
        await _serviceBusSender.SendMessageAsync(message, cancellationToken);
    }
}
