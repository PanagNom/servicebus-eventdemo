namespace NotificationService;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Contracts;

public class OrderPlacedConsumer : BackgroundService
{
    private readonly ServiceBusProcessor _processor;
    private readonly ILogger<OrderPlacedConsumer> _logger;

    public OrderPlacedConsumer(ServiceBusProcessor processor, ILogger<OrderPlacedConsumer> logger)
    {
        _processor = processor;
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

            // TEST HOOK
            if (orderPlacedEvent.CustomerId == "poison")
            {
                throw new InvalidOperationException("Simulated exception for testing poison message handling.");
            }
            // Here you can add logic to send an email notification or any other processing

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