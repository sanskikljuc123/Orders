using System.Text.Json;
using FakeItEasy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.Keycloak;
using Xunit;

public class AuthApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly KeycloakContainer _keycloak;

    public IPaymentClient Payment { get; } = A.Fake<IPaymentClient>();

    public AuthApiFixture()
    {
        var realmPath = Path.Combine(AppContext.BaseDirectory, "Realms", "orders-realm.json");

        _keycloak = new KeycloakBuilder()
            .WithImage("quay.io/keycloak/keycloak:26.6")
            .WithResourceMapping(File.ReadAllBytes(realmPath), "/opt/keycloak/data/import/orders-realm.json")
            .WithCommand("--import-realm")
            .Build();
    }

    private string KeycloakUrl => _keycloak.GetBaseAddress().ToString().TrimEnd('/');

    public Task InitializeAsync() => _keycloak.StartAsync();

    async Task IAsyncLifetime.DisposeAsync() => await _keycloak.DisposeAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Same URL is used for the token request and for token validation (issuer must match).
        builder.UseSetting("Auth:Authority", $"{KeycloakUrl}/realms/orders");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPaymentClient>();
            services.AddSingleton(Payment);
        });
    }

    public async Task<string> GetTokenAsync(string username = "ana", string password = "ana123")
    {
        using var http = new HttpClient();
        var res = await http.PostAsync(
            $"{KeycloakUrl}/realms/orders/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "orders-web",
                ["username"] = username,
                ["password"] = password
            }));
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }
}