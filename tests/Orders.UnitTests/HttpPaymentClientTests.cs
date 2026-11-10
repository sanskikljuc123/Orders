using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The real payment client against every kind of answer the provider can give. Only the network is
/// replaced (a stub HttpMessageHandler); the client code is real.
/// </summary>
public class HttpPaymentClientTests
{
    public sealed class StubProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
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

    /// <summary>Records what the client logs: an unpaid order must leave a visible reason for operators.</summary>
    private sealed class RecordingLogger : ILogger<HttpPaymentClient>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format) =>
            Entries.Add((level, format(state, e)));
    }

    private static HttpPaymentClient Client(HttpMessageHandler provider, TimeSpan? timeout = null, ILogger<HttpPaymentClient>? log = null) =>
        new(new HttpClient(provider) { BaseAddress = new Uri("http://payment.test"), Timeout = timeout ?? TimeSpan.FromSeconds(10) },
            log ?? NullLogger<HttpPaymentClient>.Instance);

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

    // ---------- every unpaid outcome leaves a warning that says why ----------

    // The expected reason text comes first so each test row is named after it.
    public static TheoryData<string, Func<StubProvider>> FailuresAndReasons => new()
    {
        { "answered 500", () => Answering(HttpStatusCode.InternalServerError, "") },
        { "unreadable answer", () => Answering(HttpStatusCode.OK, "not json") },
        { "unreachable", () => new StubProvider((_, _) => throw new HttpRequestException("refused")) },
        { "timed out", () => new StubProvider(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; }) },
    };

    [Theory]
    [MemberData(nameof(FailuresAndReasons))]
    public async Task Unpaid_outcome_is_logged_as_a_warning_with_its_reason(string reason, Func<StubProvider> provider)
    {
        var log = new RecordingLogger();

        await Client(provider(), TimeSpan.FromMilliseconds(200), log).ChargeAsync(10m);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(reason, entry.Message);
        Assert.Contains("order not paid", entry.Message);
    }

    [Fact]
    public async Task Approved_payment_logs_no_warning()
    {
        var log = new RecordingLogger();

        await Client(Answering(HttpStatusCode.OK, """{"approved":true}"""), log: log).ChargeAsync(10m);

        Assert.Empty(log.Entries);
    }
}
