using FakeItEasy;
using Xunit;

public class OrderServiceTests
{
    private readonly IPaymentClient _payment = A.Fake<IPaymentClient>();
    private readonly OrderStore _store = new();

    private OrderService CreateSut() => new(_payment, _store);

    [Fact]
    public async Task Approved_payment_gives_Paid_order_and_saves_it()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);

        var order = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2));

        Assert.Equal("Paid", order.Status);
        Assert.NotNull(_store.Find(order.Id));
    }

    [Fact]
    public async Task Declined_payment_gives_PaymentFailed_order()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(false);

        var order = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2));

        Assert.Equal("PaymentFailed", order.Status);
    }

    [Fact]
    public async Task Charges_quantity_times_ten()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);

        await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 3));

        A.CallTo(() => _payment.ChargeAsync(30m)).MustHaveHappenedOnceExactly();
    }
}