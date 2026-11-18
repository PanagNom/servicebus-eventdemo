namespace Messaging;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Consumes one topic/subscription: validates each message, dispatches it by Subject to the
/// registered <see cref="IEventHandler{TEvent}"/>, and completes or dead-letters it.
/// One instance per <c>AddSubscription(...)</c> call.
/// </summary>
internal sealed class SubscriptionProcessor : BackgroundService
{
    private readonly ServiceBusProcessor _processor;
    private readonly IProcessedMessageStore _processedStore;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly SubscriptionRegistration _registration;
    private readonly ILogger<SubscriptionProcessor> _logger;

    public SubscriptionProcessor(SubscriptionRegistration registration, ServiceBusClient serviceBusClient, IServiceScopeFactory serviceScopeFactory, IProcessedMessageStore processedStore, ILogger<SubscriptionProcessor> logger)
    {
        _processor = serviceBusClient.CreateProcessor(registration.Topic, registration.Subscription, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 1
        });
        _registration = registration;
        _serviceScopeFactory = serviceScopeFactory;
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
        var topic = _registration.Topic;
        var subscription = _registration.Subscription;

        _logger.LogInformation("Received {Subject} {MessageId} (delivery {DeliveryCount}, correlation {CorrelationId}) on {Topic}/{Subscription}",
            message.Subject, message.MessageId, message.DeliveryCount, message.CorrelationId, topic, subscription);

        // 1. Which handler? A subject nobody registered for is a permanent failure: retrying won't help.
        if (string.IsNullOrEmpty(message.Subject) || !_registration.Handlers.TryGetValue(message.Subject, out var entry))
        {
            _logger.LogWarning("No handler for subject {Subject} on {Topic}/{Subscription}; dead-lettering {MessageId}",
                message.Subject, topic, subscription, message.MessageId);
            await args.DeadLetterMessageAsync(message, "UnknownSubject", $"No handler for subject '{message.Subject}' on {topic}/{subscription}", args.CancellationToken);
            return;
        }

        // 2. A schema version we don't understand is also permanent.
        var hasValidVersion = message.ApplicationProperties.TryGetValue("SchemaVersion", out var version) && version is int v && v == 1;
        if (!hasValidVersion)
        {
            _logger.LogWarning("Unexpected schema version {SchemaVersion} for {MessageId}; dead-lettering", version, message.MessageId);
            await args.DeadLetterMessageAsync(message, "InvalidMessage", "Unexpected schema version", args.CancellationToken);
            return;
        }

        // 3. Deserialize to the type registered for this subject. Bad JSON is permanent too.
        object? evt;
        try
        {
            evt = entry.Deserialize(message.Body.ToString());
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize {MessageId}; dead-lettering", message.MessageId);
            await args.DeadLetterMessageAsync(message, "DeserializationFailed", "Could not deserialize the message body", args.CancellationToken);
            return;
        }

        if (evt is null)
        {
            _logger.LogWarning("Message {MessageId} has an empty (null) body; dead-lettering", message.MessageId);
            await args.DeadLetterMessageAsync(message, "DeserializationFailed", "The message body deserialized to null", args.CancellationToken);
            return;
        }

        // 4. What the handler may know about the envelope.
        var context = new MessageContext
        {
            MessageId = message.MessageId,
            CorrelationId = message.CorrelationId,
            Subject = message.Subject,
            DeliveryCount = message.DeliveryCount
        };

        // 5. Idempotency: a redelivered message we already handled is skipped, but still completed.
        //    Otherwise it would keep coming back until it was dead-lettered. The key includes the
        //    subscription, because one service may receive the same message on two subscriptions.
        var processedKey = $"{subscription}:{message.MessageId}";
        if (_processedStore.HasBeenProcessed(processedKey))
        {
            _logger.LogInformation("Duplicate {MessageId} (delivery {DeliveryCount}) on {Topic}/{Subscription}, skipping",
                message.MessageId, message.DeliveryCount, topic, subscription);
            await args.CompleteMessageAsync(message, args.CancellationToken);
            return;
        }

        // 6. The business work, in its own DI scope (one unit of work per message).
        //    No try/catch on purpose: if the handler throws, the processor abandons the message,
        //    so it's redelivered and, after MaxDeliveryCount attempts, dead-lettered.
        await using (var scope = _serviceScopeFactory.CreateAsyncScope())
        {
            await entry.Invoke(scope.ServiceProvider, evt, context, args.CancellationToken);
        }

        // 7. Mark AFTER the work. A crash between the work and this line causes a duplicate,
        //    never a lost message.
        _processedStore.MarkProcessed(processedKey);

        // 8. TEST HOOK: the handler asked us to act as if Complete never reached the broker.
        //    The lock expires after LockDuration (30 s) and the message is redelivered.
        if (context.CompleteSuppressed)
        {
            _logger.LogWarning("TEST HOOK: not completing {MessageId}; expect redelivery after the lock expires", message.MessageId);
            return;
        }

        await args.CompleteMessageAsync(message, args.CancellationToken);
    }

    private Task ErrorHandler(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception, "Error on {Topic}/{Subscription}: {ErrorSource}, entity path: {EntityPath}",
            _registration.Topic, _registration.Subscription, args.ErrorSource, args.EntityPath);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _processor.StopProcessingAsync(cancellationToken);
        await _processor.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}
