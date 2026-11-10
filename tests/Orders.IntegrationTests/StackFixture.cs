using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Testcontainers.Keycloak;
using Testcontainers.PostgreSql;

[CollectionDefinition("Stack")]
public class StackCollection : ICollectionFixture<StackFixture> { }

/// <summary>
/// Orders.Api with a real PostgreSQL and a real Keycloak (test realm imported), started once
/// for the "Stack" collection. Tests in one collection run sequentially, so sharing the
/// payment fake is safe as long as each test calls ResetPayment first. Needs Docker.
/// </summary>
public class StackFixture : OrdersApiFactory, IAsyncLifetime
{
    // Same image tags the repo already uses/pulls; never "latest".
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17").Build();

    private readonly KeycloakContainer _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.6")
        .WithRealm(Path.Combine(AppContext.BaseDirectory, "Realms", "orders-realm.json"))
        .Build();

    private readonly Dictionary<string, string> _settings = new();
    private readonly Dictionary<string, string> _tokens = new();

    protected override IReadOnlyDictionary<string, string> Settings => _settings;

    /// <summary>The token's "sub" claim: the user id the API locks and stores orders under.</summary>
    public static string SubjectOf(HttpClient client)
    {
        var payload = client.DefaultRequestHeaders.Authorization!.Parameter!.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var json = Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '='));
        return System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("sub").GetString()!;
    }

    /// <summary>The test PostgreSQL, for factories that start the API in another environment.</summary>
    public string DbConnectionString => _db.GetConnectionString();

    /// <summary>Token endpoint and API Authority use the same URL, so the issuer matches.</summary>
    public string Authority => $"{_keycloak.GetBaseAddress().TrimEnd('/')}/realms/orders";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_db.StartAsync(), _keycloak.StartAsync());
        _settings["ConnectionStrings:ordersdb"] = _db.GetConnectionString();
        _settings["Auth:Authority"] = Authority;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
        await _keycloak.DisposeAsync();
    }

    /// <summary>Test users from the imported realm (src/Orders.AppHost/Realms/orders-realm.json).</summary>
    private static string PasswordFor(string user) => user switch
    {
        "ana" => "ana123",
        "bob" => "bob123",
        _ => throw new ArgumentOutOfRangeException(nameof(user), user, "Not a user in the test realm")
    };

    /// <summary>Signed in as a test user. clientId: the Keycloak client the token is issued to.</summary>
    public async Task<HttpClient> CreateClientAsAsync(string user, string clientId = "orders-web")
    {
        if (!_tokens.TryGetValue($"{user}@{clientId}", out var token))
        {
            using var http = new HttpClient();
            var res = await http.PostAsync($"{Authority}/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "password",
                    ["client_id"] = clientId,
                    ["username"] = user,
                    ["password"] = PasswordFor(user),
                }));
            res.EnsureSuccessStatusCode();
            token = (await res.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
            _tokens[$"{user}@{clientId}"] = token;   // never logged
        }

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}
