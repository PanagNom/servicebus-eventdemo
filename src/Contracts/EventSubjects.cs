namespace Contracts;

/// <summary>
/// The Subject (AMQP "Label") of every event the services publish.
/// Subscription filters in infra/Config.json match on these exact strings,
/// so a change here must be mirrored there.
/// </summary>
public static class EventSubjects
{
    // topic: orders
    public const string OrderPlaced = "OrderPlaced";

    // topic: inventory
    public const string StockReserved = "StockReserved";
    public const string StockReservationFailed = "StockReservationFailed";

    // topic: payments
    public const string PaymentSucceeded = "PaymentSucceeded";
    public const string PaymentFailed = "PaymentFailed";

    // topic: shipping
    public const string OrderShipped = "OrderShipped";
}
