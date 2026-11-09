using System.Net;
using System.Net.Http.Json;
using Microsoft.Playwright;
using Reqnroll;
using Xunit;

[Binding]
public class OrderSteps(ScenarioBrowser sb)
{
    private OrderPage Orders => new(sb.Page);
    private HttpStatusCode _status;

    // ---------- signing in ----------

    [Given("I am signed in as {string}")]
    public async Task SignedInAs(string user)
    {
        var state = await TestEnvironment.GetStorageStateAsync(user);
        await sb.OpenAsync(state);
    }

    [When("I open the app")]
    public Task OpenApp() => Orders.OpenAsync();

    [When("I sign in as {string} with password {string}")]
    public Task SignIn(string user, string password) => Orders.SignInAsync(user, password);

    [Then("I see the log in button")]
    public Task SeeLoginButton() => Assertions.Expect(Orders.LoginButton).ToBeVisibleAsync();

    [Then("I see that I am signed in as {string}")]
    public Task SeeSignedIn(string user) =>
        Assertions.Expect(Orders.SignedInUser).ToContainTextAsync(user, new() { Timeout = 30000 });

    [Then("I see a login error")]
    public Task SeeLoginError() => Assertions.Expect(Orders.LoginError).ToBeVisibleAsync();

    // ---------- payment service ----------

    [Given("the payment service approves payments")]
    public Task PaymentApproves() => TestEnvironment.SetPaymentModeAsync("approve");

    [Given("the payment service declines payments")]
    public Task PaymentDeclines() => TestEnvironment.SetPaymentModeAsync("decline");

    // ---------- ordering ----------

    [Given("{string} has ordered {string}")]
    public async Task UserHasOrdered(string user, string product)
    {
        var state = await TestEnvironment.GetStorageStateAsync(user);
        await using var context = await TestEnvironment.Browser.NewContextAsync(
            new BrowserNewContextOptions { BaseURL = TestEnvironment.ApiUrl, StorageState = state });
        var page = await context.NewPageAsync();
        var orders = new OrderPage(page);
        await orders.OpenAsync();
        await orders.PlaceOrderAsync(product, 1);
        await Assertions.Expect(orders.Result).ToHaveTextAsync("Order Paid");
    }

    [When("I order {int} of {string}")]
    public async Task OrderProduct(int quantity, string product)
    {
        await Orders.OpenAsync();
        await Orders.PlaceOrderAsync(product, quantity);
    }

    [When("I order {int} without a product name")]
    public async Task OrderWithoutName(int quantity)
    {
        await Orders.OpenAsync();
        await Orders.PlaceOrderAsync("", quantity);
    }

    [When("I place an order without logging in")]
    public async Task OrderAnonymously()
    {
        using var http = new HttpClient { BaseAddress = new Uri(TestEnvironment.ApiUrl) };
        var res = await http.PostAsJsonAsync("/orders", new { productName = "Book", quantity = 1 });
        _status = res.StatusCode;
    }

    [Then("I see the result {string}")]
    public Task SeeResult(string expected) =>
        Assertions.Expect(Orders.Result).ToHaveTextAsync(expected);

    [Then("the request is rejected with status {int}")]
    public void RequestRejected(int code) => Assert.Equal(code, (int)_status);

    [Then("my order list contains {string}")]
    public Task ListContains(string product) =>
        Assertions.Expect(Orders.MyOrders).ToContainTextAsync(product);

    [Then("my order list does not contain {string}")]
    public Task ListDoesNotContain(string product) =>
        Assertions.Expect(Orders.MyOrders).Not.ToContainTextAsync(product);
}