using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Messaging;

public static class DebugEndpoints
{
    public static IEndpointRouteBuilder MapMessagingDebugEndpoints(this IEndpointRouteBuilder app)
    {
        var debug = app.MapGroup("/debug");
                
        debug.MapGet("/peek/{topic}/{subscription}", async (ServiceBusClient client, string topic, string subscription) =>
        {
            await using var receiver = client.CreateReceiver(topic, subscription);
            var messages = await receiver.PeekMessagesAsync(10);
            return messages.Select(m => new
            {
                MessageId = m.MessageId,
                Subject = m.Subject,
                CorrelationId = m.CorrelationId,
                ContentType = m.ContentType,
                ApplicationProperties = m.ApplicationProperties,
                DeliveryCount = m.DeliveryCount,
                Body = m.Body.ToString()
            });
        });

        debug.MapGet("/peek-dlq/{topic}/{subscription}", async (ServiceBusClient client, string topic, string subscription) =>
        {
            await using var receiver = client.CreateReceiver(topic, subscription, new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
            var messages = await receiver.PeekMessagesAsync(10);
            return messages.Select(m => new
            {
                MessageId = m.MessageId,
                Subject = m.Subject,
                CorrelationId = m.CorrelationId,
                ContentType = m.ContentType,
                ApplicationProperties = m.ApplicationProperties,
                DeadLetterReason = m.DeadLetterReason,
                DeadLetterErrorDescription = m.DeadLetterErrorDescription,
                DeliveryCount = m.DeliveryCount,
                EnqueuedTime = m.EnqueuedTime,
                Body = m.Body.ToString()
            });
        });

        return app;
    }
}
