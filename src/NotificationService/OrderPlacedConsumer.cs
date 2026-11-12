namespace NotificationService;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Contracts;

public class OrderPlacedConsumer : BackgroundService
{
    private readonly ServiceBusProcessor _processor;
    private readonly IProcessedMessageStore _processedStore;
    private readonly ILogger<OrderPlacedConsumer> _logger;

    public OrderPlacedConsumer(ServiceBusProcessor processor, IProcessedMessageStore processedStore, ILogger<OrderPlacedConsumer> logger)
    {
        _processor = processor;
        _processedStore = processedStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor.ProcessMessageAsync += MessageHandler;
        _processor.ProcessErrorAsync += ErrorHandler;

        await _processor.StartProcessingAsync(stoppingToken);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task MessageHandler(ProcessMessageEventArgs args)
    {
        var message = args.Message;
        var body = message.Body.ToString();

        var subject = message.Subject;
        var hasValidVersion = message.ApplicationProperties.TryGetValue("SchemaVersion", out var v) && v is int version && version == 1;

        if (subject != "OrderPlaced" || !hasValidVersion)
        {
            _logger.LogWarning("Received a message with unexpected subject or schema version: {Subject}, {SchemaVersion}", subject, v);
            await args.DeadLetterMessageAsync(message, "InvalidMessage", "Unexpected subject or schema version");
            return;
        }

        OrderPlacedEvent? orderPlacedEvent = null;
        try
        {
            // Deserialize the message body to OrderPlacedEvent
            orderPlacedEvent = JsonSerializer.Deserialize<OrderPlacedEvent>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize messageId: {MessageId}", message.MessageId);
            await args.DeadLetterMessageAsync(message, "DeserializationFailed", "Could not deserialize the message body");
            return;
        }

        if (orderPlacedEvent != null)
        {
            // Log the received event
            _logger.LogInformation("Received OrderPlacedEvent: {OrderId}, {CustomerId}, {DeliveryCount}, {CorrelationId}, {Subject}",
            orderPlacedEvent.OrderId, orderPlacedEvent.CustomerId, message.DeliveryCount, message.CorrelationId, message.Subject);

            // Idempotency: a redelivered message we already handled is skipped, but still
            // completed. Otherwise it would keep coming back until it was dead-lettered.
            if (_processedStore.HasBeenProcessed(message.MessageId))
            {
                _logger.LogInformation("Duplicate {MessageId} (delivery {DeliveryCount}), skipping", message.MessageId, message.DeliveryCount);
                await args.CompleteMessageAsync(message, args.CancellationToken);
                return;
            }

            // TEST HOOK: always throws, so the message is retried and then dead-lettered.
            if (orderPlacedEvent.CustomerId == "poison")
            {
                throw new InvalidOperationException("Simulated exception for testing poison message handling.");
            }

            // The "work": in a real service, this would send the confirmation email.
            _logger.LogInformation("EMAIL SENT for order {OrderId}", orderPlacedEvent.OrderId);

            // Mark AFTER the work. If we crash between the work and this line, the message
            // is redelivered and the work runs again: a duplicate, but nothing is lost.
            // Marking BEFORE the work would risk losing the email instead.
            _processedStore.MarkProcessed(message.MessageId);

            // TEST HOOK: simulates the work succeeding but Complete never reaching the broker
            // (for example a network blip or lost lock). The lock expires after LockDuration
            // (30 s) and the message is redelivered.
            if (orderPlacedEvent.CustomerId == "flaky" && message.DeliveryCount == 1)
            {
                _logger.LogWarning("TEST HOOK: not completing {MessageId}; expect redelivery after the lock expires", message.MessageId);
                return;
            }

            // Complete the message so that it is not received again.
            await args.CompleteMessageAsync(message, args.CancellationToken);
        }
        else
        {
            _logger.LogWarning("Received a message that could not be deserialized to OrderPlacedEvent: {Body}", body);
            await args.DeadLetterMessageAsync(message, "DeserializationFailed", "Could not deserialize the message to OrderPlacedEvent");
        }
    }

    private Task ErrorHandler(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Error processing message: {ErrorSource}, entity path: {EntityPath}", args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _processor.StopProcessingAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}