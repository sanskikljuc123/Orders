using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The real HttpPaymentClient against the real FakePaymentService (in-process). Proves the fake and
/// the client agree on the contract the E2E tests rely on: if either side changes the route, body or
/// meaning of a mode, this fails before E2E does.
/// The fake's contract itself is an UNVERIFIED ASSUMPTION about the real provider
/// (src/FakePaymentService/README.md).
/// </summary>
public class PaymentContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _fake;

    public PaymentContractTests(WebApplicationFactory<Program> fake)
    {
        _fake = fake;
        // Mode is state in the fake: reset it before every test (fixture is shared within the class).
        _fake.CreateClient().PostAsync("/__admin/reset", null).GetAwaiter().GetResult().EnsureSuccessStatusCode();
    }

    private HttpPaymentClient RealClient() => new(_fake.CreateClient(), NullLogger<HttpPaymentClient>.Instance);

    private async Task SetModeAsync(string mode) =>
        (await _fake.CreateClient().PostAsJsonAsync("/__admin/mode", new { mode })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Approve_mode_is_understood_as_paid()
        => Assert.True(await RealClient().ChargeAsync(20m));

    [Fact]
    public async Task Decline_mode_is_understood_as_not_paid()
    {
        await SetModeAsync("decline");

        Assert.False(await RealClient().ChargeAsync(20m));
    }

    [Fact]
    public async Task Error_mode_is_understood_as_not_paid_without_failing()
    {
        await SetModeAsync("error");

        Assert.False(await RealClient().ChargeAsync(20m));
    }

    [Fact]
    public async Task Error_mode_answers_500_as_the_client_expects_for_provider_errors()
    {
        await SetModeAsync("error");

        var res = await _fake.CreateClient().PostAsJsonAsync("/charge", new ChargeRequest(20m));

        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
    }

    [Fact]
    public async Task Reset_goes_back_to_approving()
    {
        await SetModeAsync("decline");

        (await _fake.CreateClient().PostAsync("/__admin/reset", null)).EnsureSuccessStatusCode();

        Assert.True(await RealClient().ChargeAsync(20m));
    }

    [Fact]
    public async Task Charge_answer_has_the_shape_the_client_reads()
    {
        var res = await _fake.CreateClient().PostAsJsonAsync("/charge", new ChargeRequest(20m));

        res.EnsureSuccessStatusCode();
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new ChargeResponse(true), await res.Content.ReadFromJsonAsync<ChargeResponse>());
    }
}
