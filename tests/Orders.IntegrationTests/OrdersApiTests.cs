using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;

/// <summary>HTTP pipeline (routing, validation, payment wiring) without DB or auth. No Docker needed.</summary>
public class OrdersApiTests : IClassFixture<OrdersApiFactory>
{
    private readonly OrdersApiFactory _factory;
    private readonly HttpClient _client;

    public OrdersApiTests(OrdersApiFactory factory)
    {
        _factory = factory;
        _factory.ResetPayment(approve: true);
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Valid_order_returns_201_with_Location_and_Paid_status()
    {
        var product = $"Book-{Guid.NewGuid():N}";

        var res = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest(product, 2));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var order = await res.Content.ReadFromJsonAsync<Order>();
        Assert.NotNull(order);
        Assert.Equal(product, order.ProductName);
        Assert.Equal(2, order.Quantity);
        Assert.Equal("Paid", order.Status);
        Assert.Equal($"/orders/{order.Id}", res.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Created_order_can_be_fetched_from_its_Location()
    {
        var created = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest($"Book-{Guid.NewGuid():N}", 1));

        var res = await _client.GetAsync(created.Headers.Location);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var order = await res.Content.ReadFromJsonAsync<Order>();
        Assert.Equal((await created.Content.ReadFromJsonAsync<Order>())!.Id, order!.Id);
    }

    [Fact]
    public async Task Declined_payment_returns_order_with_PaymentFailed_status()
    {
        // Also proves the FakeItEasy fake is the client the API really uses.
        _factory.ResetPayment(approve: false);

        var res = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest($"Book-{Guid.NewGuid():N}", 1));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("PaymentFailed", (await res.Content.ReadFromJsonAsync<Order>())!.Status);
    }

    [Fact]
    public async Task Order_charges_quantity_times_10()
    {
        await _client.PostAsJsonAsync("/orders", new CreateOrderRequest($"Book-{Guid.NewGuid():N}", 3));

        A.CallTo(() => _factory.Payment.ChargeAsync(30m)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Invalid_order_returns_400_with_field_errors_and_is_not_charged()
    {
        var res = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest("", 0));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.Equal(["'Product Name' must not be empty."], problem!.Errors["ProductName"]);
        Assert.Equal(["'Quantity' must be between 1 and 100. You entered 0."], problem.Errors["Quantity"]);
        A.CallTo(() => _factory.Payment.ChargeAsync(A<decimal>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Unknown_order_id_returns_404()
    {
        var res = await _client.GetAsync($"/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
