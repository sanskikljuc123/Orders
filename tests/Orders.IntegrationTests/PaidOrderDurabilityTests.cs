using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// "A paid order is never lost and a retry never charges twice", through HTTP and the real
/// PostgreSQL. Failures are made deterministic with a database trigger that fails one save for
/// this test's product only. Needs Docker.
///
/// FAKED: settling a Pending order charges its reference again and relies on provider-side
/// idempotency, modelled on Stripe's public documentation for this example only
/// (src/FakePaymentService/README.md). The payment here is a FakeItEasy fake, so these tests check
/// that a repeat uses the same reference; the contract tests prove the fake charges it only once.
/// </summary>
[Collection("Stack")]
public class PaidOrderDurabilityTests
{
    private readonly StackFixture _fixture;

    public PaidOrderDurabilityTests(StackFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetPayment(approve: true);
    }

    private static HttpRequestMessage Post(string product, int quantity, string key) =>
        new(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new CreateOrderRequest(product, quantity)),
            Headers = { { "Idempotency-Key", key } },
        };

    private async Task<T> WithDbAsync<T>(Func<OrdersDbContext, Task<T>> work)
    {
        using var scope = _fixture.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<OrdersDbContext>());
    }

    private Task<List<Order>> RowsAsync(string product) =>
        WithDbAsync(db => db.Orders.AsNoTracking().Where(o => o.ProductName == product).ToListAsync());

    /// <summary>Makes every INSERT or UPDATE of this product's order fail until disposed.</summary>
    private async Task<IAsyncDisposable> FailingAsync(string operation, string product)
    {
        var name = $"fail_{Guid.NewGuid():N}";
        await WithDbAsync(db => db.Database.ExecuteSqlRawAsync($"""
            CREATE FUNCTION {name}() RETURNS trigger AS $$
            BEGIN
              IF NEW."ProductName" = '{product}' THEN RAISE EXCEPTION 'test: {operation} fails on purpose'; END IF;
              RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER {name} BEFORE {operation} ON "Orders" FOR EACH ROW EXECUTE FUNCTION {name}();
            """));
        return new Dropper(this, name);
    }

    private sealed class Dropper(PaidOrderDurabilityTests t, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await t.WithDbAsync(db =>
            db.Database.ExecuteSqlRawAsync($"""DROP TRIGGER {name} ON "Orders"; DROP FUNCTION {name}();"""));
    }

    private IEnumerable<Guid> ChargedReferences() =>
        Fake.GetCalls(_fixture.Payment).Where(c => c.Method.Name == nameof(IPaymentClient.ChargeAsync))
            .Select(c => (Guid)c.Arguments[1]!);

    [Fact]
    public async Task Approved_charge_whose_status_cannot_be_saved_is_kept_Pending_and_a_retry_completes_it()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var product = $"Book-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString();

        HttpResponseMessage first;
        await using (await FailingAsync("UPDATE", product))
            first = await ana.SendAsync(Post(product, 2, key));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var pending = (await first.Content.ReadFromJsonAsync<Order>())!;
        Assert.Equal(OrderStatus.Pending, pending.Status);
        Assert.Equal($"/orders/{pending.Id}", first.Headers.Location?.ToString());
        Assert.Equal(OrderStatus.Pending, Assert.Single(await RowsAsync(product)).Status);   // kept, never shown as Paid
        Assert.Contains(_fixture.Logs.Events, e => e.Contains(pending.Id.ToString()) && e.Contains("stays Pending"));   // operators can find it
        Assert.DoesNotContain(_fixture.Logs.Events, e => e.Contains(key));   // the key is never logged

        var retry = await ana.SendAsync(Post(product, 2, key));   // the database works again

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        var settled = (await retry.Content.ReadFromJsonAsync<Order>())!;
        Assert.Equal((pending.Id, OrderStatus.Paid), (settled.Id, settled.Status));
        Assert.Equal(OrderStatus.Paid, Assert.Single(await RowsAsync(product)).Status);
        Assert.All(ChargedReferences(), r => Assert.Equal(pending.Id, r));   // FAKED: same reference, see class comment
    }

    [Fact]
    public async Task Order_that_cannot_be_reserved_returns_503_charges_nothing_and_a_retry_places_it_once()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var product = $"Book-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString();

        HttpResponseMessage first;
        await using (await FailingAsync("INSERT", product))
            first = await ana.SendAsync(Post(product, 1, key));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Contains("Nothing was charged", await first.Content.ReadAsStringAsync());
        Assert.DoesNotContain("test: INSERT fails", await first.Content.ReadAsStringAsync());   // no internals leak
        Assert.Empty(ChargedReferences());
        Assert.Empty(await RowsAsync(product));

        var retry = await ana.SendAsync(Post(product, 1, key));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(OrderStatus.Paid, Assert.Single(await RowsAsync(product)).Status);
        Assert.Single(ChargedReferences());
    }

    [Fact]
    public async Task Order_left_Pending_by_a_crash_is_settled_on_retry_with_its_own_reference()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var product = $"Book-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString();
        var stuck = new Order(Guid.NewGuid(), product, 1, OrderStatus.Pending, StackFixture.SubjectOf(ana), DateTimeOffset.UtcNow, key);
        await WithDbAsync(async db => { db.Orders.Add(stuck); return await db.SaveChangesAsync(); });   // what a crash leaves

        var retry = await ana.SendAsync(Post(product, 1, key));

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((stuck.Id, OrderStatus.Paid), ((await retry.Content.ReadFromJsonAsync<Order>())!.Id, Assert.Single(await RowsAsync(product)).Status));
        Assert.Equal(stuck.Id, Assert.Single(ChargedReferences()));   // FAKED: same reference, see class comment
    }

    [Fact]
    public async Task Two_API_instances_with_the_same_key_store_one_order_under_one_payment_reference()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var product = $"Book-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString();
        // A second instance on the same database: its own DI container, so its own in-process lock.
        using var otherInstance = _fixture.WithWebHostBuilder(_ => { });
        var anaOnOther = otherInstance.CreateClient();
        anaOnOther.DefaultRequestHeaders.Authorization = ana.DefaultRequestHeaders.Authorization;
        var charging = new TaskCompletionSource();
        A.CallTo(() => _fixture.Payment.ChargeAsync(A<decimal>._, A<Guid>._)).ReturnsLazily(async () => { await charging.Task; return true; });

        var onFirst = ana.SendAsync(Post(product, 1, key));
        var reserved = SpinWait.SpinUntil(() => ChargedReferences().Any(), TimeSpan.FromSeconds(10));   // first instance is charging
        var onSecond = anaOnOther.SendAsync(Post(product, 1, key));
        SpinWait.SpinUntil(() => ChargedReferences().Count() == 2 || onSecond.IsCompleted, TimeSpan.FromSeconds(10));
        charging.SetResult();
        var responses = await Task.WhenAll(onFirst, onSecond);

        Assert.True(reserved);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));   // no 500 on the second instance
        var row = Assert.Single(await RowsAsync(product));
        Assert.Equal(OrderStatus.Paid, row.Status);
        Assert.All(ChargedReferences(), r => Assert.Equal(row.Id, r));   // FAKED: one reference, see class comment
    }

    [Fact]
    public async Task Database_refuses_a_second_reservation_and_never_overwrites_a_final_status()
    {
        var product = $"Book-{Guid.NewGuid():N}";
        var order = new Order(Guid.NewGuid(), product, 1, OrderStatus.Pending, "user-x", DateTimeOffset.UtcNow, "key-x");
        using var scope = _fixture.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        Assert.IsType<EfOrderRepository>(repository);

        Assert.True(await repository.TryAddAsync(order));
        Assert.False(await repository.TryAddAsync(order with { Id = Guid.NewGuid() }));   // same user + key: the real unique index
        Assert.True(await repository.CompletePendingAsync(order.Id, OrderStatus.Paid));
        Assert.False(await repository.CompletePendingAsync(order.Id, OrderStatus.PaymentFailed));

        Assert.Equal(OrderStatus.Paid, Assert.Single(await RowsAsync(product)).Status);
    }

    [Fact]
    public async Task Pending_order_read_by_id_shows_its_final_status_once_completed()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var product = $"Book-{Guid.NewGuid():N}";
        var key = Guid.NewGuid().ToString();
        HttpResponseMessage first;
        await using (await FailingAsync("UPDATE", product))
            first = await ana.SendAsync(Post(product, 1, key));
        var location = first.Headers.Location;

        var whilePending = await ana.GetFromJsonAsync<Order>(location);
        await ana.SendAsync(Post(product, 1, key));   // the retry completes it
        var afterwards = await ana.GetFromJsonAsync<Order>(location);

        Assert.Equal(OrderStatus.Pending, whilePending!.Status);
        Assert.Equal(OrderStatus.Paid, afterwards!.Status);   // not the cached Pending
    }
}
