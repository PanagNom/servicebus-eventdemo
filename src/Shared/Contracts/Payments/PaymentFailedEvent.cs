namespace Contracts;

public record class PaymentFailedEvent
{
    public required string OrderId { get; init; }
    public required string Reason { get; init; }
}
