using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Every endpoint needs a login unless it is on the public list below. The endpoints are read from
/// the running app, so a new endpoint without RequireAuthorization fails this test even if nobody
/// writes a test for it. Real Keycloak + PostgreSQL (see StackFixture). Needs Docker.
/// </summary>
[Collection("Stack")]
public class EndpointAuthTests
{
    /// <summary>Reachable without a token, on purpose. Adding to this list is a reviewed decision.</summary>
    private static readonly HashSet<string> Public = ["GET /health", "GET /login", "GET /logout"];

    private readonly StackFixture _fixture;

    public EndpointAuthTests(StackFixture fixture)
    {
        _fixture = fixture;
        _fixture.ResetPayment(approve: true);
    }

    /// <summary>"METHOD /route" for every endpoint the app has mapped, e.g. "GET /orders/{id:guid}".</summary>
    private IReadOnlyList<string> MappedEndpoints() =>
        _fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(method => $"{method} /{e.RoutePattern.RawText!.Trim('/')}"))   // a group route ends in "/"
            .Distinct()
            .ToList();

    private static HttpRequestMessage RequestFor(string endpoint)
    {
        var (method, route) = (endpoint.Split(' ')[0], endpoint.Split(' ')[1]);
        var path = route.Replace("{id:guid}", Guid.NewGuid().ToString());
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT" or "PATCH")
            request.Content = JsonContent.Create(new CreateOrderRequest("Book", 1));   // valid body: only the missing token may fail it
        return request;
    }

    [Fact]
    public void The_endpoint_list_is_read_from_the_app()
    {
        // Proves the discovery works, so the next test cannot pass by finding nothing.
        var endpoints = MappedEndpoints();

        Assert.Contains("POST /orders", endpoints);
        Assert.Contains("GET /orders", endpoints);
        Assert.Contains("GET /orders/{id:guid}", endpoints);
        Assert.Contains("GET /me", endpoints);
        Assert.Contains("GET /health", endpoints);
    }

    [Fact]
    public async Task Every_endpoint_except_the_public_ones_returns_401_without_a_token()
    {
        var client = _fixture.CreateClient();
        var accepted = new List<string>();

        foreach (var endpoint in MappedEndpoints().Where(e => !Public.Contains(e)))
        {
            var res = await client.SendAsync(RequestFor(endpoint));
            if (res.StatusCode != HttpStatusCode.Unauthorized)
                accepted.Add($"{endpoint} -> {(int)res.StatusCode}");
        }

        Assert.True(accepted.Count == 0,
            "Endpoints reachable without a login (add RequireAuthorization, or add them to Public on purpose):\n"
            + string.Join("\n", accepted));
        A.CallTo(() => _fixture.Payment.ChargeAsync(A<decimal>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Order_without_a_token_is_rejected_and_not_charged()
    {
        var res = await _fixture.CreateClient().PostAsJsonAsync("/orders", new CreateOrderRequest("Book", 1));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        A.CallTo(() => _fixture.Payment.ChargeAsync(A<decimal>._)).MustNotHaveHappened();
    }
}
