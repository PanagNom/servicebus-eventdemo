using Contracts;
using Messaging;

namespace OrderService.Endpoints;

/// <summary>The HTTP API of OrderService. Program.cs calls <see cref="MapOrderEndpoints"/>.</summary>
public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/orders", PlaceOrderAsync);
        // M3: app.MapGet("/orders/{id}", GetOrderAsync);

        return app;
    }

    // A named method instead of a lambda: it shows up by name in stack traces, and you can put a breakpoint on it.
    private static async Task<IResult> PlaceOrderAsync(PlaceOrderRequest request, IEventPublisher eventPublisher, CancellationToken cancellationToken)
    {
        var orderPlacedEvent = new OrderPlacedEvent
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

        return Results.Accepted($"/orders/{orderPlacedEvent.OrderId}", new { orderPlacedEvent.OrderId });
    }
}
