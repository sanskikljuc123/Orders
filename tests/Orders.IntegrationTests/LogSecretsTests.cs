using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

/// <summary>
/// "Never log secrets or tokens" (CLAUDE.md), checked: the sensitive flows run against the real
/// API, Keycloak and PostgreSQL, and every log event the API wrote is searched. Needs Docker.
/// </summary>
[Collection("Stack")]
public partial class LogSecretsTests(StackFixture fixture)
{
    /// <summary>Any JWT (header.payload.signature, base64url, header starts with {" = eyJ).</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+")]
    private static partial Regex Jwt();

    private static HttpRequestMessage Post(CreateOrderRequest body, string key) =>
        new(HttpMethod.Post, "/orders") { Content = JsonContent.Create(body), Headers = { { "Idempotency-Key", key } } };

    [Fact]
    public async Task Logs_never_contain_tokens_passwords_or_idempotency_keys()
    {
        fixture.ResetPayment(approve: true);
        var ana = await fixture.CreateClientAsAsync("ana");
        var key = $"secret-key-{Guid.NewGuid():N}";
        var product = $"Book-{Guid.NewGuid():N}";

        // Sensitive flows: authenticated calls, order + replay + reused key, invalid input,
        // a garbage token and a real token issued to another client (refused, so logged as a failure).
        Assert.Equal(HttpStatusCode.OK, (await ana.GetAsync("/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ana.SendAsync(Post(new(product, 1), key))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ana.SendAsync(Post(new(product, 1), key))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await ana.SendAsync(Post(new(product, 2), key))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ana.SendAsync(Post(new("", 0), key))).StatusCode);
        var garbage = fixture.CreateClient();
        garbage.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJmb3JnZWQifQ.c2lnbmF0dXJl");
        Assert.Equal(HttpStatusCode.Unauthorized, (await garbage.GetAsync("/orders")).StatusCode);
        var foreign = await fixture.CreateClientAsAsync("ana", clientId: "admin-cli");
        Assert.Equal(HttpStatusCode.Unauthorized, (await foreign.GetAsync("/orders")).StatusCode);

        var events = fixture.Logs.Events;
        Assert.Contains(events, e => e.Contains("/orders"));   // the capture works: requests were logged

        var leaks = events.Where(e => Jwt().IsMatch(e) || e.Contains("ana123") || e.Contains(key)).ToList();
        Assert.True(leaks.Count == 0, "Secrets in the logs:\n" + string.Join("\n", leaks));
    }
}
