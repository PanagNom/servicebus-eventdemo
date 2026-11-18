namespace OrderService.Endpoints;

/// <summary>The JSON body of <c>POST /orders</c>.</summary>
public record PlaceOrderRequest(
    string ProductId,
    int Quantity,
    string CustomerId,
    string CustomerEmail,
    string ShippingAddress,
    string BillingAddress,
    string PaymentMethod,
    decimal TotalAmount,
    string Currency);
