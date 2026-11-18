namespace Messaging;

public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent @event, string subject, string messageId, string correlationId, CancellationToken cancellationToken = default) where TEvent : class;
}
