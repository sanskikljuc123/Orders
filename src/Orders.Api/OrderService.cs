public class OrderService(IPaymentClient payment, IOrderRepository orders)
{
    public async Task<Order> PlaceOrderAsync(CreateOrderRequest req, string userId = "anonymous")
    {
        var paid = await payment.ChargeAsync(req.Quantity * 10m);
        var order = new Order(Guid.NewGuid(), req.ProductName, req.Quantity,
            paid ? "Paid" : "PaymentFailed", userId, DateTimeOffset.UtcNow);
        await orders.AddAsync(order);
        return order;
    }
}