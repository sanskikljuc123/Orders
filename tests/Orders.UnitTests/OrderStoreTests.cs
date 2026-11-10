public class OrderStoreTests
{
    private readonly OrderStore _store = new();

    private static Order OrderFor(string userId, string product) =>
        new(Guid.NewGuid(), product, 1, "Paid", userId, DateTimeOffset.UnixEpoch, IdempotencyKey: null);

    [Fact]
    public async Task User_cannot_find_another_users_order()
    {
        var bobs = OrderFor("bob", "Bobs secret order");
        await _store.TryAddAsync(bobs);

        Assert.Null(await _store.FindAsync(bobs.Id, "ana"));
        Assert.Equal(bobs, await _store.FindAsync(bobs.Id, "bob"));
    }

    [Fact]
    public async Task List_contains_only_the_users_own_orders()
    {
        await _store.TryAddAsync(OrderFor("ana", "Anas order"));
        await _store.TryAddAsync(OrderFor("bob", "Bobs secret order"));

        var anas = await _store.ListAsync("ana");

        var only = Assert.Single(anas);
        Assert.Equal("Anas order", only.ProductName);
    }

    [Fact]
    public async Task Final_status_is_never_overwritten()
    {
        var order = OrderFor("ana", "Book") with { Status = OrderStatus.Paid };
        await _store.TryAddAsync(order);

        var changed = await _store.CompletePendingAsync(order.Id, OrderStatus.PaymentFailed);

        Assert.False(changed);
        Assert.Equal(OrderStatus.Paid, _store.Find(order.Id)!.Status);
    }

    [Fact]
    public async Task Pending_order_is_completed_once()
    {
        var order = OrderFor("ana", "Book") with { Status = OrderStatus.Pending };
        await _store.TryAddAsync(order);

        Assert.True(await _store.CompletePendingAsync(order.Id, OrderStatus.Paid));
        Assert.False(await _store.CompletePendingAsync(order.Id, OrderStatus.PaymentFailed));
        Assert.Equal(OrderStatus.Paid, _store.Find(order.Id)!.Status);
    }

    [Fact]
    public async Task Second_order_with_the_same_user_and_key_is_refused()
    {
        Assert.True(await _store.TryAddAsync(OrderFor("ana", "Book") with { IdempotencyKey = "k" }));
        Assert.False(await _store.TryAddAsync(OrderFor("ana", "Pen") with { IdempotencyKey = "k" }));
        Assert.True(await _store.TryAddAsync(OrderFor("bob", "Book") with { IdempotencyKey = "k" }));   // per user
        Assert.Equal(2, (await _store.ListAsync("ana")).Count + (await _store.ListAsync("bob")).Count);
    }
}
