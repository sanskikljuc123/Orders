using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The real payment client against every kind of answer the provider can give. Only the network is
/// replaced (a stub HttpMessageHandler); the client code is real.
/// </summary>
public class HttpPaymentClientTests
{
    private sealed class StubProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await answer(request, ct);
        }
    }

    private static StubProvider Answering(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

    private static HttpPaymentClient Client(HttpMessageHandler provider, TimeSpan? timeout = null) =>
        new(new HttpClient(provider) { BaseAddress = new Uri("http://payment.test"), Timeout = timeout ?? TimeSpan.FromSeconds(10) },
            NullLogger<HttpPaymentClient>.Instance);

    [Fact]
    public async Task Sends_the_amount_to_POST_charge()
    {
        var provider = Answering(HttpStatusCode.OK, """{"approved":true}""");

        await Client(provider).ChargeAsync(30m);

        Assert.Equal(HttpMethod.Post, provider.LastRequest!.Method);
        Assert.Equal("/charge", provider.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal(30m, JsonDocument.Parse(provider.LastBody!).RootElement.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Approved_answer_means_paid()
        => Assert.True(await Client(Answering(HttpStatusCode.OK, """{"approved":true}""")).ChargeAsync(10m));

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"approved":false}""", "declined")]
    [InlineData(HttpStatusCode.OK, """{}""", "approved field missing")]
    [InlineData(HttpStatusCode.OK, "null", "empty JSON")]
    [InlineData(HttpStatusCode.OK, "not json", "unreadable body")]
    [InlineData(HttpStatusCode.InternalServerError, "", "provider error")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "", "provider unavailable")]
    [InlineData(HttpStatusCode.BadRequest, """{"approved":true}""", "error status wins over body")]
    public async Task Anything_but_a_clear_approval_means_not_paid(HttpStatusCode status, string body, string because)
        => Assert.False(await Client(Answering(status, body)).ChargeAsync(10m), because);

    [Fact]
    public async Task Unreachable_provider_means_not_paid_instead_of_an_error()
    {
        var provider = new StubProvider((_, _) => throw new HttpRequestException("connection refused"));

        Assert.False(await Client(provider).ChargeAsync(10m));
    }

    [Fact]
    public async Task Provider_that_does_not_answer_in_time_means_not_paid()
    {
        // Never answers; the client's own timeout must end the call.
        var provider = new StubProvider(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; });

        var paid = await Client(provider, timeout: TimeSpan.FromMilliseconds(200)).ChargeAsync(10m);

        Assert.False(paid);
    }
}
