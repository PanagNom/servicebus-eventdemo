using Azure.Messaging.ServiceBus;
using Contracts;
using Messaging;
using OrderService.DTOs;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddServiceBus(builder.Configuration["ServiceBus:ConnectionString"] ?? throw new InvalidOperationException("ServiceBus:ConnectionString is not configured")).AddEventPublisher(Topics.Orders);

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/orders", async (PlaceOrderRequestDTO request, IEventPublisher eventPublisher, CancellationToken cancellationToken) =>
{
    OrderPlacedEvent orderPlacedEvent = new OrderPlacedEvent
    {
        OrderId = Guid.CreateVersion7().ToString(),
        CustomerId = request.CustomerId,
        Email = request.CustomerEmail,
        ProductId = request.ProductId,
        Quantity = request.Quantity,
        ShippingAddress = request.ShippingAddress,
        PaymentMethod = request.PaymentMethod,
        TotalAmount = request.TotalAmount,
        Currency = request.Currency,
        PlacedAt = DateTimeOffset.UtcNow
    };

    await eventPublisher.PublishAsync(orderPlacedEvent, EventSubjects.OrderPlaced, messageId: orderPlacedEvent.OrderId, correlationId: orderPlacedEvent.OrderId, cancellationToken);

    return Results.Accepted($"/orders/{orderPlacedEvent.OrderId}", new { OrderId = orderPlacedEvent.OrderId });
});

app.MapGet("/debug/peek", async (ServiceBusClient client) =>
{
    await using var receiver = client.CreateReceiver(builder.Configuration["ServiceBus:TopicName"], "notifications");
    var messages = await receiver.PeekMessagesAsync(10);
    return messages.Select(m => new
    {
        MessageId = m.MessageId,
        Subject = m.Subject,
        CorrelationId = m.CorrelationId,
        ContentType = m.ContentType,
        ApplicationProperties = m.ApplicationProperties,
        Body = System.Text.Json.JsonSerializer.Deserialize<OrderPlacedEvent>(m.Body.ToString())
    });
});

app.MapGet("/debug/peek-dlq", async (ServiceBusClient client) =>
{
    await using var receiver = client.CreateReceiver(builder.Configuration["ServiceBus:TopicName"], "notifications", new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
    var messages = await receiver.PeekMessagesAsync(10);
    return messages.Select(m => new
    {
        DeadLetterReason = m.DeadLetterReason,
        DeadLetterErrorDescription = m.DeadLetterErrorDescription,
        DeliveryCount = m.DeliveryCount,
        MessageId = m.MessageId,
        EnqueuedTime = m.EnqueuedTime,
        Body = m.Body.ToString(),
    });
});

app.Run();
