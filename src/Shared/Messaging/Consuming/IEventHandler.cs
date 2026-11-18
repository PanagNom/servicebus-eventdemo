namespace Messaging;

public interface IEventHandler<TEvent> where TEvent : class
{
    Task HandleAsync(TEvent @event, MessageContext context, CancellationToken cancellationToken);
}

