using Azure.Messaging.ServiceBus;

namespace Messaging;

public static class MessageEnvelope
{
    public static ServiceBusMessage CreateMessage<T>(T payload, string subject, string messageId, string correlationId) where T : class
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(correlationId);

        var message = new ServiceBusMessage(System.Text.Json.JsonSerializer.Serialize(payload))
        {
            Subject = subject,
            ContentType = "application/json",
            MessageId = messageId,
            CorrelationId = correlationId,
            ApplicationProperties = { ["SchemaVersion"] = 1 }
        };

        return message;
    }
}