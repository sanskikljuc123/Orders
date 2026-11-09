using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FakeItEasy;
using Xunit;

public class AuthApiTests(AuthApiFixture fixture) : IClassFixture<AuthApiFixture>
{
    private static readonly object Body = new { productName = "Book", quantity = 1 };

    [Fact]
    public async Task Request_without_token_returns_401()
    {
        var client = fixture.CreateClient();

        var res = await client.PostAsJsonAsync("/orders", Body);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Request_with_garbage_token_returns_401()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var res = await client.PostAsJsonAsync("/orders", Body);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Request_with_valid_keycloak_token_returns_201()
    {
        A.CallTo(() => fixture.Payment.ChargeAsync(A<decimal>._)).Returns(true);
        var client = fixture.CreateClient();
        var token = await fixture.GetTokenAsync();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.PostAsJsonAsync("/orders", Body);

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_cannot_get_a_token()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.GetTokenAsync(password: "wrong"));
    }
}