using Contracts;
using Messaging;
using OrderService.DTOs;
using ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services
.AddServiceBus(builder.Configuration["ServiceBus:ConnectionString"] 
?? throw new InvalidOperationException("ServiceBus:ConnectionString is not configured"))
.AddEventPublisher(Topics.Orders);

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapMessagingDebugEndpoints();
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

app.Run();
