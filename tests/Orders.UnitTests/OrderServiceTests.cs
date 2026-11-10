public class OrderServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 8, 30, 0, TimeSpan.Zero);

    /// <summary>Fixed clock: the order time is a value the test can assert exactly.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private readonly IPaymentClient _payment = A.Fake<IPaymentClient>();
    private readonly OrderStore _store = new();   // real, cheap collaborator
    private readonly IdempotencyLocks _locks = new();

    private OrderService CreateSut() => new(_payment, _store, new FixedClock(Now), _locks);

    [Fact]
    public async Task Approved_payment_gives_Paid_order_for_the_user()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);

        var order = (await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana")).Order;

        Assert.Equal("Paid", order.Status);
        Assert.Equal("Book", order.ProductName);
        Assert.Equal(2, order.Quantity);
        Assert.Equal("ana", order.UserId);
    }

    [Fact]
    public async Task Declined_payment_gives_PaymentFailed_order()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(false);

        var order = (await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana")).Order;

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

        var order = (await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana")).Order;

        var saved = await _store.FindAsync(order.Id, "ana");
        Assert.NotNull(saved);
        Assert.Equal("PaymentFailed", saved.Status);
    }

    [Fact]
    public async Task Order_gets_the_current_time()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);

        var placed = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana");

        Assert.Equal(Now, placed.Order.CreatedAt);
    }

    // ---------- idempotency: a repeated request must not charge twice ----------

    [Fact]
    public async Task Repeating_a_request_with_the_same_key_returns_the_first_order_and_charges_once()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);
        var sut = CreateSut();

        var first = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana", "key-1");
        var again = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana", "key-1");

        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.Equal(first.Order.Id, again.Order.Id);
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).MustHaveHappenedOnceExactly();
        Assert.Single(await _store.ListAsync("ana"));
    }

    [Fact]
    public async Task Different_keys_are_different_orders()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);
        var sut = CreateSut();

        var a = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-1");
        var b = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-2");

        Assert.NotEqual(a.Order.Id, b.Order.Id);
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task Without_a_key_every_request_is_a_new_order()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);
        var sut = CreateSut();

        var first = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana");
        var second = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana");

        Assert.False(first.Replayed);
        Assert.False(second.Replayed);
        Assert.NotEqual(first.Order.Id, second.Order.Id);
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task The_same_key_from_another_user_is_their_own_order()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(true);
        var sut = CreateSut();

        var ana = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-1");
        var bob = await sut.PlaceOrderAsync(new CreateOrderRequest("Pen", 1), "bob", "key-1");

        Assert.False(bob.Replayed);
        Assert.NotEqual(ana.Order.Id, bob.Order.Id);
        Assert.Equal("bob", bob.Order.UserId);
    }

    [Fact]
    public async Task Concurrent_requests_with_the_same_key_charge_only_once()
    {
        // A slow provider keeps the first request inside the charge while the others arrive.
        var charging = new TaskCompletionSource();
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).ReturnsLazily(async () => { await charging.Task; return true; });
        var sut = CreateSut();

        var requests = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "double-click")))
            .ToList();
        // Wait until all 10 hold or wait for the same key's lock (no fixed sleep). If the lock were
        // missing this never becomes true, the wait gives up, and the charge count below fails.
        var allQueued = SpinWait.SpinUntil(() => _locks.UsersOf("ana\ndouble-click") == 10, TimeSpan.FromSeconds(5));
        charging.SetResult();
        Assert.True(allQueued, "all 10 requests should wait on the same per-user, per-key lock");
        var placed = await Task.WhenAll(requests);

        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).MustHaveHappenedOnceExactly();
        Assert.Single(placed.Select(p => p.Order.Id).Distinct());
        Assert.Equal(9, placed.Count(p => p.Replayed));
        Assert.Equal(0, _locks.Count);   // no lock left behind
    }
}
