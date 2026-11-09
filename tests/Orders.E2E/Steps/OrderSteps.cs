using Microsoft.Playwright;
using Reqnroll;
using System.Net;
using System.Net.Http.Json;
using Xunit;

[Binding]
public class OrderSteps(ScenarioBrowser sb)
{
    private OrderPage Orders => new(sb.Page);

    [Given("the payment service approves payments")]
    public Task PaymentApproves() => TestEnvironment.SetPaymentModeAsync("approve");

    [Given("the payment service declines payments")]
    public Task PaymentDeclines() => TestEnvironment.SetPaymentModeAsync("decline");

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

        private HttpStatusCode _status;

    [When("I place an order without logging in")]
    public async Task OrderAnonymously()
    {
        using var http = new HttpClient { BaseAddress = new Uri(TestEnvironment.ApiUrl) };
        var res = await http.PostAsJsonAsync("/orders", new { productName = "Book", quantity = 1 });
        _status = res.StatusCode;
    }

    [Then("the request is rejected with status {int}")]
    public void RequestRejected(int code) => Assert.Equal(code, (int)_status);

    [Then("I see the result {string}")]
    public Task SeeResult(string expected)
        => Assertions.Expect(Orders.Result).ToHaveTextAsync(expected);
}