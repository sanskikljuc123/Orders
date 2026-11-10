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

        A.CallTo(() => _factory.Payment.ChargeAsync(30m, A<Guid>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task Invalid_order_returns_400_with_field_errors_and_is_not_charged()
    {
        var res = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest("", 0));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var problem = await res.Content.ReadFromJsonAsync<HttpValidationProblemDetails>();
        Assert.Equal(["'Product Name' must not be empty."], problem!.Errors["ProductName"]);
        Assert.Equal(["'Quantity' must be between 1 and 100. You entered 0."], problem.Errors["Quantity"]);
        A.CallTo(() => _factory.Payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Unknown_order_id_returns_404()
    {
        var res = await _client.GetAsync($"/orders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Theory]
    [InlineData("{\"productName\": \"Book\", \"quantity\": ", "application/json")]   // cut-off JSON
    [InlineData("", "application/json")]                                         // empty body
    [InlineData("{\"productName\": \"Book\", \"quantity\": \"two\"}", "application/json")]   // wrong type
    public async Task Unreadable_order_body_returns_400_and_is_not_charged(string body, string contentType)
    {
        var res = await _client.PostAsync("/orders", new StringContent(body, System.Text.Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        A.CallTo(() => _factory.Payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Order_sent_as_plain_text_returns_415_and_is_not_charged()
    {
        var res = await _client.PostAsync("/orders",
            new StringContent("{\"productName\": \"Book\", \"quantity\": 1}", System.Text.Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
        A.CallTo(() => _factory.Payment.ChargeAsync(A<decimal>._, A<Guid>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Order_id_that_is_not_a_guid_returns_404()
    {
        var res = await _client.GetAsync("/orders/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Order_response_has_exactly_the_fields_callers_read()
    {
        // Renaming, adding or removing a field changes the API for every caller: make it a decision.
        // userId (Keycloak's internal subject) and the idempotency key are stored but never sent.
        var res = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest($"Book-{Guid.NewGuid():N}", 1));
        var body = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();

        var fields = body.EnumerateObject().Select(p => p.Name).Order().ToArray();

        Assert.Equal(["createdAt", "id", "productName", "quantity", "status"], fields);   // no userId: internal
        Assert.Equal("application/json; charset=utf-8", res.Content.Headers.ContentType?.ToString());
    }
}
