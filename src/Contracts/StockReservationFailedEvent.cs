using System;

namespace Contracts;

public record StockReservationFailedEvent
{
    public required string OrderId { get; init; }
    public required string ProductId { get; init; }
    public required string Reason { get; init; }
}
