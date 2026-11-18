using System;

namespace Contracts;

public record StockReservedEvent
{
    public required string OrderId { get; init; }
    public required string ProductId { get; init; }
    public required int Quantity { get; init; }
    public required DateTimeOffset ReservedAt { get; init; }
}
