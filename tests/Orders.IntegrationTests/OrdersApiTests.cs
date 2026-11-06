using System.Net;
using System.Net.Http.Json;
using FakeItEasy;
using Xunit;

public class OrdersApiTests : IClassFixture<OrdersApiFactory>
{
    private readonly OrdersApiFactory _factory;
    private readonly HttpClient _client;

    public OrdersApiTests(OrdersApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Approved_payment_returns_201_and_Paid()
    {
        A.CallTo(() => _factory.Payment.ChargeAsync(A<decimal>._)).Returns(true);

        var res = await _client.PostAsJsonAsync("/orders", new { productName = "Book", quantity = 2 });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var order = await res.Content.ReadFromJsonAsync<Order>();
        Assert.Equal("Paid", order!.Status);
    }

    [Fact]
    public async Task Declined_payment_returns_201_and_PaymentFailed()
    {
        A.CallTo(() => _factory.Payment.ChargeAsync(A<decimal>._)).Returns(false);

        var res = await _client.PostAsJsonAsync("/orders", new { productName = "Book", quantity = 2 });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var order = await res.Content.ReadFromJsonAsync<Order>();
        Assert.Equal("PaymentFailed", order!.Status);
    }

    [Fact]
    public async Task Empty_product_name_returns_400()
    {
        var res = await _client.PostAsJsonAsync("/orders", new { productName = "", quantity = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Created_order_can_be_fetched_by_id()
    {
        A.CallTo(() => _factory.Payment.ChargeAsync(A<decimal>._)).Returns(true);
        var created = await (await _client.PostAsJsonAsync("/orders", new { productName = "Pen", quantity = 1 }))
            .Content.ReadFromJsonAsync<Order>();

        var res = await _client.GetAsync($"/orders/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var fetched = await res.Content.ReadFromJsonAsync<Order>();
        Assert.Equal("Pen", fetched!.ProductName);
    }

    [Fact]
    public async Task Unknown_order_returns_404()
    {
        var res = await _client.GetAsync($"/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}