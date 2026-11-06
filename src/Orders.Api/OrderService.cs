public class OrderService(IPaymentClient payment, OrderStore store)
{
    public async Task<Order> PlaceOrderAsync(CreateOrderRequest req)
    {
        var paid = await payment.ChargeAsync(req.Quantity * 10m);
        var order = new Order(Guid.NewGuid(), req.ProductName, req.Quantity, paid ? "Paid" : "PaymentFailed");
        store.Add(order);
        return order;
    }
}