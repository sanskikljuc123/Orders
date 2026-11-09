using Orders.E2E.Hooks;
using Orders.E2E.Pages;

namespace Orders.E2E.Steps;

/// <summary>Per-scenario memory of what this scenario ordered (injected, never static).</summary>
public class OrderContext
{
    public string? LastProduct { get; set; }
}

[Binding]
public class OrderSteps(ScenarioBrowser browser, OrderContext ordered)
{
    private OrderPage Shop => new(browser.Page);

    // The product name gets a unique suffix so this scenario's order can't be confused with
    // orders from other scenarios in the same run (they share one database).
    [When("I order {int} of {string}")]
    public async Task OrderProduct(int quantity, string product)
    {
        ordered.LastProduct = $"{product} {Guid.NewGuid().ToString("N")[..8]}";
        await Shop.OpenAsync();
        await Shop.PlaceOrderAsync(ordered.LastProduct, quantity);
    }

    [When("I order {int} without a product name")]
    public async Task OrderWithoutProduct(int quantity)
    {
        await Shop.OpenAsync();
        await Shop.PlaceOrderAsync("", quantity);
    }

    [Given("{string} has ordered {string}")]
    public Task UserHasOrdered(string user, string product) => TestEnvironment.PlaceOrderAsAsync(user, product);

    [Given("I have ordered {string}")]
    public Task IHaveOrdered(string product) => OrderProduct(1, product);

    [When("I open my orders")]
    public Task OpenMyOrders() => Shop.OpenMyOrdersAsync();

    // Checks the UI and the server: a hidden form alone would still let the old session order.
    // The login link comes first, so the hidden-form check runs on the reloaded, signed-out page.
    [Then("I can no longer place an order")]
    public async Task CannotPlaceOrder()
    {
        await Assertions.Expect(Shop.LoginLink).ToBeVisibleAsync();
        await Assertions.Expect(Shop.PlaceOrderButton).ToBeHiddenAsync();
        Assert.Equal(401, await Shop.SendOrderRequestAsync($"Book {Guid.NewGuid().ToString("N")[..8]}", 1));
    }

    [Then("I see the result {string}")]
    public Task SeeResult(string expected) => Assertions.Expect(Shop.Result).ToHaveTextAsync(expected);

    [Then("my orders show that order as {string}")]
    public Task MyOrdersShowLastOrder(string status) =>
        Assertions.Expect(Shop.MyOrders.Filter(new() { HasText = ordered.LastProduct! }))
            .ToHaveTextAsync($"{ordered.LastProduct} ({status})");

    [Then("my order list contains my order")]
    public Task MyListContainsMyOrder() =>
        Assertions.Expect(Shop.MyOrders.Filter(new() { HasText = ordered.LastProduct! })).ToHaveCountAsync(1);

    [Then("my order list does not contain {string}")]
    public Task MyListDoesNotContain(string product) =>
        Assertions.Expect(Shop.MyOrders.Filter(new() { HasText = product })).ToHaveCountAsync(0);
}
