using System.Net;
using System.Net.Http.Json;
using FakeItEasy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Collection("Stack")]
public class OrdersDatabaseTests(AuthApiFixture fixture)
{
    private async Task<Order> PlaceAsync(HttpClient client, string product)
    {
        A.CallTo(() => fixture.Payment.ChargeAsync(A<decimal>._)).Returns(true);
        var res = await client.PostAsJsonAsync("/orders", new { productName = product, quantity = 1 });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<Order>())!;
    }

    [Fact]
    public async Task Order_is_stored_in_postgres_with_its_owner()
    {
        var ana = await fixture.CreateAuthenticatedClientAsync("ana", "ana123");
        var product = $"Book-{Guid.NewGuid():N}";

        var order = await PlaceAsync(ana, product);

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var saved = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == order.Id);

        Assert.Equal(product, saved.ProductName);
        Assert.Equal("Paid", saved.Status);
        Assert.NotEqual("anonymous", saved.UserId);
    }

    [Fact]
    public async Task User_sees_own_order_in_list()
    {
        var ana = await fixture.CreateAuthenticatedClientAsync("ana", "ana123");
        var order = await PlaceAsync(ana, "Pen");

        var list = await ana.GetFromJsonAsync<List<Order>>("/orders");

        Assert.Contains(list!, o => o.Id == order.Id);
    }

    [Fact]
    public async Task User_cannot_read_another_users_order_by_id()
    {
        var ana = await fixture.CreateAuthenticatedClientAsync("ana", "ana123");
        var bob = await fixture.CreateAuthenticatedClientAsync("bob", "bob123");
        var bobsOrder = await PlaceAsync(bob, "Bobs secret order");

        var res = await ana.GetAsync($"/orders/{bobsOrder.Id}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Another_users_order_is_not_in_my_list()
    {
        var ana = await fixture.CreateAuthenticatedClientAsync("ana", "ana123");
        var bob = await fixture.CreateAuthenticatedClientAsync("bob", "bob123");
        var bobsOrder = await PlaceAsync(bob, "Bobs other order");

        var anasList = await ana.GetFromJsonAsync<List<Order>>("/orders");

        Assert.DoesNotContain(anasList!, o => o.Id == bobsOrder.Id);
    }
}