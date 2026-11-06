using Microsoft.Playwright;
using Reqnroll;

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

    [Then("I see the result {string}")]
    public Task SeeResult(string expected)
        => Assertions.Expect(Orders.Result).ToHaveTextAsync(expected);
}