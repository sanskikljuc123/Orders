using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>Auth and ownership against a real Keycloak + PostgreSQL (see StackFixture). Needs Docker.</summary>
[Collection("Stack")]
public class AuthApiTests
{
    private readonly StackFixture _fixture;

    public AuthApiTests(StackFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetPayment(approve: true);
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("/me")]
    public async Task Request_without_token_returns_401(string path)
    {
        var res = await _fixture.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Request_with_garbage_token_returns_401()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var res = await client.GetAsync("/orders");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_signed_in_users_name()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");

        var body = await ana.GetFromJsonAsync<JsonElement>("/me");

        Assert.Equal("ana", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task User_cannot_fetch_another_users_order()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var bob = await _fixture.CreateClientAsAsync("bob");
        var created = await ana.PostAsJsonAsync("/orders", new CreateOrderRequest($"Anas order {Guid.NewGuid():N}", 1));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var asBob = await bob.GetAsync(created.Headers.Location);
        var asAna = await ana.GetAsync(created.Headers.Location);

        Assert.Equal(HttpStatusCode.NotFound, asBob.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asAna.StatusCode);
    }

    // Paid orders are cached per user and order id. The owner reads first, so a cache key
    // without the user would serve the cached order to the next caller.
    [Fact]
    public async Task Owners_cached_order_is_not_served_to_another_user()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var bob = await _fixture.CreateClientAsAsync("bob");
        var created = await ana.PostAsJsonAsync("/orders", new CreateOrderRequest($"Anas order {Guid.NewGuid():N}", 1));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var asAna = await ana.GetFromJsonAsync<Order>(created.Headers.Location);
        var asBob = await bob.GetAsync(created.Headers.Location);

        Assert.Equal(OrderStatus.Paid, asAna!.Status);   // final state: this read was cached
        Assert.Equal(HttpStatusCode.NotFound, asBob.StatusCode);
    }

    [Fact]
    public async Task Users_list_contains_only_their_own_orders()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var bob = await _fixture.CreateClientAsAsync("bob");
        var anas = $"Anas order {Guid.NewGuid():N}";
        var bobs = $"Bobs secret order {Guid.NewGuid():N}";
        Assert.Equal(HttpStatusCode.Created, (await ana.PostAsJsonAsync("/orders", new CreateOrderRequest(anas, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await bob.PostAsJsonAsync("/orders", new CreateOrderRequest(bobs, 1))).StatusCode);

        var anasNames = (await ana.GetFromJsonAsync<List<Order>>("/orders"))!.Select(o => o.ProductName).ToList();
        var bobsNames = (await bob.GetFromJsonAsync<List<Order>>("/orders"))!.Select(o => o.ProductName).ToList();

        Assert.Contains(anas, anasNames);
        Assert.Contains(bobs, bobsNames);         // Bob's order exists, so its absence below means something
        Assert.DoesNotContain(bobs, anasNames);
    }

    [Fact]
    public async Task Users_list_shows_orders_oldest_first()
    {
        var ana = await _fixture.CreateClientAsAsync("ana");
        var first = $"First {Guid.NewGuid():N}";
        var second = $"Second {Guid.NewGuid():N}";
        await ana.PostAsJsonAsync("/orders", new CreateOrderRequest(first, 1));
        await ana.PostAsJsonAsync("/orders", new CreateOrderRequest(second, 1));

        var names = (await ana.GetFromJsonAsync<List<Order>>("/orders"))!.Select(o => o.ProductName).ToList();

        Assert.Contains(first, names);
        Assert.True(names.IndexOf(first) < names.IndexOf(second), "the older order must come first");
    }
}
