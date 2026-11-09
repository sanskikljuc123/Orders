using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Persistence in a real PostgreSQL (migrations applied on startup). Needs Docker.</summary>
[Collection("Stack")]
public class OrdersDatabaseTests
{
    private readonly StackFixture _fixture;

    public OrdersDatabaseTests(StackFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetPayment(approve: true);
    }

    [Fact]
    public async Task Placed_order_is_stored_with_owner_and_status()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var product = $"Book-{Guid.NewGuid():N}";

        var res = await ana.PostAsJsonAsync("/orders", new CreateOrderRequest(product, 2));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<Order>())!.Id;
        var row = await ReadOrderAsync(id);
        Assert.NotNull(row);
        Assert.Equal(product, row.ProductName);
        Assert.Equal(2, row.Quantity);
        Assert.Equal("Paid", row.Status);
        Assert.NotEqual("anonymous", row.UserId);   // owner comes from the token's subject
    }

    [Fact]
    public async Task Declined_order_is_stored_as_PaymentFailed()
    {
        _fixture.ResetPayment(approve: false);
        var ana = await _fixture.CreateClientAsAsync("ana");

        var res = await ana.PostAsJsonAsync("/orders", new CreateOrderRequest($"Book-{Guid.NewGuid():N}", 1));

        var row = await ReadOrderAsync((await res.Content.ReadFromJsonAsync<Order>())!.Id);
        Assert.Equal("PaymentFailed", row!.Status);
    }

    [Fact]
    public async Task Invalid_order_is_not_stored()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var product = new string('x', 51);

        var res = await ana.PostAsJsonAsync("/orders", new CreateOrderRequest(product, 1));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        Assert.False(await db.Orders.AnyAsync(o => o.ProductName == product));
    }

    private async Task<Order?> ReadOrderAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        return await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id);
    }
}
