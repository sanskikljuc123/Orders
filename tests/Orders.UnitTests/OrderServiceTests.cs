public class OrderServiceTests
{
    private readonly IPaymentClient _payment = A.Fake<IPaymentClient>();
    private readonly OrderStore _store = new();   // real, cheap collaborator

    private OrderService CreateSut() => new(_payment, _store);

    [Fact]
    public async Task Approved_payment_gives_Paid_order_for_the_user()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);

        var order = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana");

        Assert.Equal("Paid", order.Status);
        Assert.Equal("Book", order.ProductName);
        Assert.Equal(2, order.Quantity);
        Assert.Equal("ana", order.UserId);
    }

    [Fact]
    public async Task Declined_payment_gives_PaymentFailed_order()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(false);

        var order = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana");

        Assert.Equal("PaymentFailed", order.Status);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(3, 30)]
    [InlineData(100, 1000)]
    public async Task Charges_10_per_item(int quantity, int expectedAmount)
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);

        await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", quantity), "ana");

        A.CallTo(() => _payment.ChargeAsync((decimal)expectedAmount)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Declined_order_is_still_saved_for_the_user()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(false);

        var order = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana");

        var saved = await _store.FindAsync(order.Id, "ana");
        Assert.NotNull(saved);
        Assert.Equal("PaymentFailed", saved.Status);
    }
}
