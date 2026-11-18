using Contracts;
using Messaging;

namespace NotificationService.Handlers;

/// <summary>
/// Reacts to an order being placed by "sending" a confirmation email.
/// Everything generic (validation, deserialization, duplicates, complete/dead-letter)
/// is done by the Messaging library before and after this runs.
/// </summary>
public sealed class OrderPlacedHandler : IEventHandler<OrderPlacedEvent>
{
    private readonly ILogger<OrderPlacedHandler> _logger;

    public OrderPlacedHandler(ILogger<OrderPlacedHandler> logger)
    {
        _logger = logger;
    }

    public Task HandleAsync(OrderPlacedEvent @event, MessageContext context, CancellationToken cancellationToken)
    {
        // TEST HOOK: always throws, so the message is retried and then dead-lettered.
        if (@event.CustomerId == "poison")
        {
            throw new InvalidOperationException("Simulated exception for testing poison message handling.");
        }

        // The "work": in a real service, this would send the confirmation email.
        // Log ids only: the event contains an email address, and personal data doesn't belong in logs.
        _logger.LogInformation("EMAIL SENT for order {OrderId}", @event.OrderId);

        // TEST HOOK: simulates the work succeeding but Complete never reaching the broker
        // (for example a network blip or lost lock). The library skips Complete, the lock
        // expires after LockDuration (30 s), and the redelivery is caught as a duplicate.
        if (@event.CustomerId == "flaky" && context.DeliveryCount == 1)
        {
            context.SimulateLostComplete();
        }

        return Task.CompletedTask;
    }
}
