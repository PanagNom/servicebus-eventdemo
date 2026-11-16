using Azure.Messaging.ServiceBus;
using Contracts;
using OrderService.DTOs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddSingleton(_ => new ServiceBusClient(builder.Configuration["ServiceBus:ConnectionString"]));
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(builder.Configuration["ServiceBus:TopicName"]));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPost("/orders", async (PlaceOrderRequestDTO request, ServiceBusSender sender, HttpContext context, CancellationToken cancellationToken) =>
{
    OrderPlacedEvent orderPlacedEvent = new OrderPlacedEvent
    {
        OrderId = Guid.CreateVersion7().ToString(),
        CustomerId = request.CustomerId,
        Email = request.CustomerEmail,
        TotalAmount = request.TotalAmount,
        Currency = request.Currency,
        PlacedAt = DateTimeOffset.UtcNow
    };

    ServiceBusMessage message = new ServiceBusMessage(System.Text.Json.JsonSerializer.Serialize(orderPlacedEvent));
    message.MessageId = orderPlacedEvent.OrderId;
    message.Subject = "OrderPlaced";
    message.ContentType = "application/json";
    message.CorrelationId = context.TraceIdentifier;
    message.ApplicationProperties.Add("SchemaVersion", 1);

    await sender.SendMessageAsync(message, cancellationToken);

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
