namespace OrderService.DTOs;

public record PlaceOrderRequestDTO(string ProductId, int Quantity, string CustomerId, string CustomerEmail, string ShippingAddress, string BillingAddress, string PaymentMethod, decimal TotalAmount, string Currency);