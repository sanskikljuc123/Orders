namespace Orders.E2E.Steps;

/// <summary>Controls FakePaymentService (separate process); ScenarioHooks resets it after every scenario.</summary>
[Binding]
public class PaymentSteps
{
    [Given("the payment service approves payments")]
    public Task Approves() => TestEnvironment.SetPaymentModeAsync("approve");

    [Given("the payment service declines payments")]
    public Task Declines() => TestEnvironment.SetPaymentModeAsync("decline");

    [Given("the payment service is failing")]
    public Task Fails() => TestEnvironment.SetPaymentModeAsync("error");
}
