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

    private OrderService CreateSut() => new(_payment, _store, new FixedClock(Now), _locks,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderService>.Instance);

    [Fact]
    public async Task Approved_payment_gives_Paid_order_for_the_user()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);

        var order = (await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana")).Order;

        Assert.Equal("Paid", order.Status);
        Assert.Equal("Book", order.ProductName);
        Assert.Equal(2, order.Quantity);
        Assert.Equal("ana", order.UserId);
    }

    [Fact]
    public async Task Declined_payment_gives_PaymentFailed_order()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(false);

        var order = (await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana")).Order;

        Assert.Equal("PaymentFailed", order.Status);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(3, 30)]
    [InlineData(100, 1000)]
    public async Task Charges_10_per_item(int quantity, int expectedAmount)
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);

        await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", quantity), "ana");

        A.CallTo(() => _payment.ChargeAsync((decimal)expectedAmount, A<Guid>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Declined_order_is_still_saved_for_the_user()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(false);

        var order = (await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana")).Order;

        var saved = await _store.FindAsync(order.Id, "ana");
        Assert.NotNull(saved);
        Assert.Equal("PaymentFailed", saved.Status);
    }

    [Fact]
    public async Task Order_gets_the_current_time()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);

        var placed = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana");

        Assert.Equal(Now, placed.Order.CreatedAt);
    }

    // ---------- idempotency: a repeated request must not charge twice ----------

    [Fact]
    public async Task Repeating_a_request_with_the_same_key_returns_the_first_order_and_charges_once()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);
        var sut = CreateSut();

        var first = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana", "key-1");
        var again = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana", "key-1");

        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.Equal(first.Order.Id, again.Order.Id);
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustHaveHappenedOnceExactly();
        Assert.Single(await _store.ListAsync("ana"));
    }

    [Theory]
    [InlineData("Pen", 2)]    // other product
    [InlineData("Book", 3)]   // other quantity
    [InlineData("book", 2)]   // product names are compared exactly
    public async Task Reusing_a_key_for_a_different_order_is_rejected_without_charging(string product, int quantity)
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);
        var sut = CreateSut();
        var first = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana", "key-1");

        var reused = await sut.PlaceOrderAsync(new CreateOrderRequest(product, quantity), "ana", "key-1");

        Assert.Equal(PlaceOutcome.Created, first.Outcome);
        Assert.Equal(PlaceOutcome.KeyUsedForDifferentOrder, reused.Outcome);
        Assert.False(reused.Replayed);
        Assert.Equal(first.Order.Id, reused.Order.Id);   // the order the key belongs to
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustHaveHappenedOnceExactly();
        Assert.Single(await _store.ListAsync("ana"));
    }

    [Fact]
    public async Task Different_keys_are_different_orders()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);
        var sut = CreateSut();

        var a = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-1");
        var b = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-2");

        Assert.NotEqual(a.Order.Id, b.Order.Id);
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task Without_a_key_every_request_is_a_new_order()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);
        var sut = CreateSut();

        var first = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana");
        var second = await sut.PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana");

        Assert.False(first.Replayed);
        Assert.False(second.Replayed);
        Assert.NotEqual(first.Order.Id, second.Order.Id);
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustHaveHappenedTwiceExactly();
    }

    [Fact]
    public async Task The_same_key_from_another_user_is_their_own_order()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);
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
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).ReturnsLazily(async () => { await charging.Task; return true; });
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

        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustHaveHappenedOnceExactly();
        Assert.Single(placed.Select(p => p.Order.Id).Distinct());
        Assert.Equal(9, placed.Count(p => p.Replayed));
        Assert.Equal(0, _locks.Count);   // no lock left behind
    }

    // ---------- a paid order is never lost, a retry never charges twice ----------

    private OrderService CreateSutWith(IOrderRepository repository, Microsoft.Extensions.Logging.ILogger<OrderService>? log = null) =>
        new(_payment, repository, new FixedClock(Now), _locks,
            log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OrderService>.Instance);

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<OrderService>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state,
            Exception? e, Func<TState, Exception?, string> formatter) => Entries.Add((level, formatter(state, e)));
    }

    /// <summary>The real in-memory store, with single calls made to fail on purpose.</summary>
    private IOrderRepository StoreThat() => A.Fake<IOrderRepository>(o => o.Wrapping(_store));

    [Fact]
    public async Task Order_is_stored_as_Pending_before_it_is_charged()
    {
        string? statusWhileCharging = null;
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).ReturnsLazily((decimal _, Guid reference) =>
        {
            statusWhileCharging = _store.Find(reference)?.Status;
            return Task.FromResult(true);
        });

        var placed = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-1");

        Assert.Equal(OrderStatus.Pending, statusWhileCharging);   // stored, with the order id as reference
        Assert.Equal(OrderStatus.Paid, (await _store.FindAsync(placed.Order.Id, "ana"))!.Status);
    }

    [Fact]
    public async Task Failing_to_store_the_pending_order_charges_nothing()
    {
        var repository = StoreThat();
        A.CallTo(() => repository.TryAddAsync(A<Order>._)).ThrowsAsync(new InvalidOperationException("database down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateSutWith(repository).PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-1"));

        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustNotHaveHappened();
        Assert.Empty(await _store.ListAsync("ana"));
    }

    [Fact]
    public async Task Approved_charge_whose_status_cannot_be_saved_leaves_the_order_Pending_not_lost()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);
        var repository = StoreThat();
        A.CallTo(() => repository.CompletePendingAsync(A<Guid>._, A<string>._)).ThrowsAsync(new InvalidOperationException("database down"));

        var log = new RecordingLogger();

        var placed = await CreateSutWith(repository, log).PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana", "key-1");

        Assert.Equal(PlaceOutcome.Pending, placed.Outcome);
        Assert.Equal(OrderStatus.Pending, Assert.Single(await _store.ListAsync("ana")).Status);   // kept, never shown as Paid
        A.CallTo(() => _payment.ChargeAsync(20m, placed.Order.Id)).MustHaveHappenedOnceExactly();
        // Operators find it by order id and know money may have moved.
        var entry = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, entry.Level);
        Assert.Equal($"Order {placed.Order.Id}: charge approved but its status could not be saved; it stays Pending until a retry", entry.Message);
    }

    [Fact]
    public async Task Declined_charge_whose_status_cannot_be_saved_also_stays_Pending_never_failed_silently()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(false);
        var repository = StoreThat();
        A.CallTo(() => repository.CompletePendingAsync(A<Guid>._, A<string>._)).ThrowsAsync(new InvalidOperationException("database down"));
        var log = new RecordingLogger();

        var placed = await CreateSutWith(repository, log).PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-1");

        Assert.Equal(PlaceOutcome.Pending, placed.Outcome);
        Assert.Equal(OrderStatus.Pending, Assert.Single(await _store.ListAsync("ana")).Status);
        Assert.Contains("charge not approved", Assert.Single(log.Entries).Message);
    }

    // FAKED: settling a Pending order by charging its reference again relies on provider-side
    // idempotency, modelled on Stripe's public documentation for this example only
    // (src/FakePaymentService/README.md). Here only the reference is checked; the contract tests
    // prove the fake answers a repeated reference without a second charge.
    [Fact]
    public async Task Retrying_a_Pending_order_settles_it_with_the_same_payment_reference()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(true);
        var stuck = new Order(Guid.NewGuid(), "Book", 2, OrderStatus.Pending, "ana", Now, "key-1");
        _store.Add(stuck);   // left Pending by a crash or a failed status update

        var retried = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2), "ana", "key-1");

        Assert.Equal(PlaceOutcome.Replayed, retried.Outcome);
        Assert.Equal(stuck.Id, retried.Order.Id);
        Assert.Equal(OrderStatus.Paid, retried.Order.Status);
        Assert.Equal(OrderStatus.Paid, (await _store.FindAsync(stuck.Id, "ana"))!.Status);
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>.That.Not.IsEqualTo(stuck.Id))).MustNotHaveHappened();
    }

    [Fact]
    public async Task Key_reserved_by_another_instance_is_not_charged_by_this_one()
    {
        var theirs = new Order(Guid.NewGuid(), "Book", 1, OrderStatus.Paid, "ana", Now, "key-1");
        var repository = StoreThat();
        // This instance looked before the other one stored its reservation...
        A.CallTo(() => repository.FindByIdempotencyKeyAsync("ana", "key-1")).ReturnsNextFromSequence(null, theirs);
        _store.Add(theirs);   // ...so the database refuses this instance's reservation.

        var placed = await CreateSutWith(repository).PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-1");

        Assert.Equal(PlaceOutcome.Replayed, placed.Outcome);
        Assert.Equal(theirs.Id, placed.Order.Id);
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustNotHaveHappened();
        Assert.Single(await _store.ListAsync("ana"));
    }

    [Fact]
    public async Task Order_completed_elsewhere_keeps_that_result()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._, A<Guid>._)).Returns(false);
        var stuck = new Order(Guid.NewGuid(), "Book", 1, OrderStatus.Pending, "ana", Now, "key-1");
        _store.Add(stuck);
        var repository = StoreThat();
        // Another instance completes it as Paid while this retry is charging.
        A.CallTo(() => repository.CompletePendingAsync(stuck.Id, A<string>._)).Invokes(() => _store.Add(stuck with { Status = OrderStatus.Paid }))
            .ReturnsLazily(() => Task.FromResult(false));

        var retried = await CreateSutWith(repository).PlaceOrderAsync(new CreateOrderRequest("Book", 1), "ana", "key-1");

        Assert.Equal(OrderStatus.Paid, retried.Order.Status);   // the stored result, not this retry's
    }
}
