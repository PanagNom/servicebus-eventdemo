namespace Contracts;

public record OrderPlacedEvent
{
    public required string OrderId { get; init; }
    public required string CustomerId { get; init; }
    public required string ProductId { get; init; }
    public required int Quantity { get; init; }
    public required string ShippingAddress { get; init; }
    public required string Email { get; init; }
    public required decimal TotalAmount { get; init; }
    public required string Currency { get; init; }
    public required string PaymentMethod { get; init; }
    public required DateTimeOffset PlacedAt { get; init; }
}
