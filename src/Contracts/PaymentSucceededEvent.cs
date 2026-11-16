namespace Contracts;

public record class PaymentSucceededEvent
{
    public required string OrderId { get; init; }
    public required string PaymentId { get; init; }
    public required decimal Amount { get; init; }
    public required string Currency { get; init; }
}
