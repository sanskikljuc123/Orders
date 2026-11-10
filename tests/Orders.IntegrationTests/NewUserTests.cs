using System.Net;
using System.Net.Http.Json;

/// <summary>
/// A caller with no orders yet. Own API instance (in-memory store, no auth), so no other test's
/// orders are in it. No Docker needed.
/// </summary>
public class NewUserTests
{
    [Fact]
    public async Task User_without_orders_gets_an_empty_list()
    {
        using var api = new OrdersApiFactory();

        var res = await api.CreateClient().GetAsync("/orders");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("[]", await res.Content.ReadAsStringAsync());
    }
}
