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
        => Assert.True(await RealClient().ChargeAsync(20m, Guid.NewGuid()));

    [Fact]
    public async Task Decline_mode_is_understood_as_not_paid()
    {
        await SetModeAsync("decline");

        Assert.False(await RealClient().ChargeAsync(20m, Guid.NewGuid()));
    }

    [Fact]
    public async Task Error_mode_is_understood_as_not_paid_without_failing()
    {
        await SetModeAsync("error");

        Assert.False(await RealClient().ChargeAsync(20m, Guid.NewGuid()));
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

        Assert.True(await RealClient().ChargeAsync(20m, Guid.NewGuid()));
    }

    [Fact]
    public async Task Charge_answer_has_the_shape_the_client_reads()
    {
        var res = await _fake.CreateClient().PostAsJsonAsync("/charge", new ChargeRequest(20m));

        res.EnsureSuccessStatusCode();
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new ChargeResponse(true), await res.Content.ReadFromJsonAsync<ChargeResponse>());
    }

    // ---------- provider-side idempotency ----------
    // FAKED: provider-side idempotency is not part of any verified provider contract. It is modelled
    // on Stripe's public documentation for this example only (src/FakePaymentService/README.md).
    // These tests prove our client and the fake agree on it, not that a real provider behaves so.

    private async Task<int> ChargesAsync() =>
        (await _fake.CreateClient().GetFromJsonAsync<System.Text.Json.JsonElement>("/__admin/charges")).GetProperty("count").GetInt32();

    [Fact]
    public async Task FAKED_charging_the_same_reference_twice_charges_once_and_returns_the_first_answer()
    {
        var reference = Guid.NewGuid();

        var first = await RealClient().ChargeAsync(20m, reference);
        var again = await RealClient().ChargeAsync(20m, reference);

        Assert.True(first);
        Assert.True(again);
        Assert.Equal(1, await ChargesAsync());
    }

    [Fact]
    public async Task FAKED_a_stored_decline_is_replayed_even_after_the_provider_would_approve()
    {
        var reference = Guid.NewGuid();
        await SetModeAsync("decline");
        var first = await RealClient().ChargeAsync(20m, reference);

        await SetModeAsync("approve");
        var again = await RealClient().ChargeAsync(20m, reference);

        Assert.False(first);
        Assert.False(again);   // the first answer for the reference, not a new charge
        Assert.Equal(1, await ChargesAsync());
    }

    [Fact]
    public async Task FAKED_a_provider_error_is_not_stored_so_a_retry_with_the_same_reference_is_charged()
    {
        var reference = Guid.NewGuid();
        await SetModeAsync("error");
        var failed = await RealClient().ChargeAsync(20m, reference);

        await SetModeAsync("approve");
        var retried = await RealClient().ChargeAsync(20m, reference);

        Assert.False(failed);
        Assert.True(retried);
        Assert.Equal(1, await ChargesAsync());
    }

    [Fact]
    public async Task FAKED_different_references_are_separate_charges()
    {
        Assert.True(await RealClient().ChargeAsync(20m, Guid.NewGuid()));
        Assert.True(await RealClient().ChargeAsync(20m, Guid.NewGuid()));

        Assert.Equal(2, await ChargesAsync());
    }
}
