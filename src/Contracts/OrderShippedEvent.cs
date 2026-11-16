namespace Contracts;

public record class OrderShippedEvent
{
    public required string OrderId { get; init; }
    public required string TrackingNumber { get; init; }
    public required DateTimeOffset ShippedAt { get; init; }
}
