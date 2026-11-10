using System.Text.Json;

/// <summary>
/// Guards deploy/realms/orders-realm.prod.json: the production Keycloak realm must never get the
/// test realm's shortcuts (test users, wildcard redirects, plain HTTP, password grant).
/// </summary>
public class ProductionRealmTests
{
    private static readonly JsonElement Realm = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Realms", "orders-realm.prod.json"))).RootElement;

    private static JsonElement WebClient => Realm.GetProperty("clients").EnumerateArray()
        .Single(c => c.GetProperty("clientId").GetString() == "orders-web");

    [Fact]
    public void Contains_no_users()
        => Assert.False(Realm.TryGetProperty("users", out _), "production realm must not ship users");

    [Fact]
    public void Requires_https_for_all_requests()
        => Assert.Equal("all", Realm.GetProperty("sslRequired").GetString());

    [Fact]
    public void Has_brute_force_protection_and_no_self_registration()
    {
        Assert.True(Realm.GetProperty("bruteForceProtected").GetBoolean());
        Assert.False(Realm.GetProperty("registrationAllowed").GetBoolean());
    }

    [Fact]
    public void Web_client_uses_code_flow_with_pkce_only()
    {
        Assert.True(WebClient.GetProperty("standardFlowEnabled").GetBoolean());
        Assert.False(WebClient.GetProperty("directAccessGrantsEnabled").GetBoolean());
        Assert.False(WebClient.GetProperty("implicitFlowEnabled").GetBoolean());
        Assert.Equal("S256", WebClient.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
    }

    [Fact]
    public void Logout_returns_only_to_the_exact_https_callback()
    {
        // Keycloak stores several URIs separated by "##"; "+" (= same as redirectUris) is not allowed
        // here so the allowed logout targets stay explicit.
        var uris = WebClient.GetProperty("attributes").GetProperty("post.logout.redirect.uris")
            .GetString()!.Split("##");

        Assert.All(uris, uri =>
        {
            Assert.DoesNotContain("*", uri);
            Assert.NotEqual("+", uri);
            Assert.StartsWith("https://", uri);
            Assert.EndsWith("/signout-callback-oidc", uri);
        });
    }

    [Theory]
    [InlineData("redirectUris")]
    [InlineData("webOrigins")]
    public void Web_client_urls_are_exact_https(string property)
    {
        var urls = WebClient.GetProperty(property).EnumerateArray().Select(u => u.GetString()!).ToList();

        Assert.NotEmpty(urls);
        Assert.All(urls, url =>
        {
            Assert.DoesNotContain("*", url);
            Assert.StartsWith("https://", url);
        });
    }
}
