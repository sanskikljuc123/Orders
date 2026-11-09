public class OrderStoreTests
{
    private readonly OrderStore _store = new();

    private static Order OrderFor(string userId, string product) =>
        new(Guid.NewGuid(), product, 1, "Paid", userId, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task User_cannot_find_another_users_order()
    {
        var bobs = OrderFor("bob", "Bobs secret order");
        await _store.AddAsync(bobs);

        Assert.Null(await _store.FindAsync(bobs.Id, "ana"));
        Assert.Equal(bobs, await _store.FindAsync(bobs.Id, "bob"));
    }

    [Fact]
    public async Task List_contains_only_the_users_own_orders()
    {
        await _store.AddAsync(OrderFor("ana", "Anas order"));
        await _store.AddAsync(OrderFor("bob", "Bobs secret order"));

        var anas = await _store.ListAsync("ana");

        var only = Assert.Single(anas);
        Assert.Equal("Anas order", only.ProductName);
    }
}
