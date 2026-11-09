using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Playwright;

public static class TestEnvironment
{
    private static DistributedApplication _app = default!;
    private static HttpClient _payment = default!;
    private static IPlaywright _playwright = default!;

    public static IBrowser Browser { get; private set; } = default!;
    public static string ApiUrl { get; private set; } = default!;
    public static string KeycloakUrl { get; private set; } = default!;

    public static async Task StartAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Orders_AppHost>();
        _app = await builder.BuildAsync();
        await _app.StartAsync();

        var timeout = TimeSpan.FromMinutes(5);
        await _app.ResourceNotifications.WaitForResourceAsync("payment", KnownResourceStates.Running).WaitAsync(timeout);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("keycloak").WaitAsync(timeout);
        KeycloakUrl = _app.GetEndpoint("keycloak", "http").ToString().TrimEnd('/');
        await _app.ResourceNotifications.WaitForResourceAsync("api", KnownResourceStates.Running).WaitAsync(timeout);

        ApiUrl = _app.GetEndpoint("api", "http").ToString();
        _payment = _app.CreateHttpClient("payment", "http");

        await WaitUntilApiRespondsAsync();

        _playwright = await Playwright.CreateAsync();
        var headed = Environment.GetEnvironmentVariable("HEADED") == "1";
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = !headed,
            SlowMo = headed ? 300 : 0
        });
    }

    public static async Task StopAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }
    public static async Task<string> GetAccessTokenAsync()
    {
          using var http = new HttpClient();
          var res = await http.PostAsync(
              $"{KeycloakUrl}/realms/orders/protocol/openid-connect/token",
              new FormUrlEncodedContent(new Dictionary<string, string>
              {
                  ["grant_type"] = "password",
                  ["client_id"] = "orders-web",
                  ["username"] = "ana",
                  ["password"] = "ana123"
              }));
          res.EnsureSuccessStatusCode();
          using var doc = System.Text.Json.JsonDocument.Parse(await res.Content.ReadAsStringAsync());
          return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static readonly Dictionary<string, string> StorageStates = new();

    public static string PasswordFor(string user) => user switch
    {
        "ana" => "ana123",
        "bob" => "bob123",
        _ => throw new ArgumentException($"Unknown test user '{user}'")
    };

    // Logs in through the real Keycloak page once per user and reuses the saved session.
    public static async Task<string> GetStorageStateAsync(string user)
    {
        if (StorageStates.TryGetValue(user, out var cached)) return cached;

        var context = await Browser.NewContextAsync(new BrowserNewContextOptions { BaseURL = ApiUrl });
        var page = await context.NewPageAsync();
        var app = new OrderPage(page);
        await app.OpenAsync();
        await app.SignInAsync(user, PasswordFor(user));
        await Assertions.Expect(app.SignedInUser).ToContainTextAsync(user, new() { Timeout = 30000 });
        var state = await context.StorageStateAsync();
        await context.CloseAsync();
        return StorageStates[user] = state;
    }

    public static async Task SetPaymentModeAsync(string mode)
    {
        var res = await _payment.PostAsJsonAsync("/__admin/mode", new { mode });
        res.EnsureSuccessStatusCode();
    }

    private static async Task WaitUntilApiRespondsAsync()
    {
        using var http = new HttpClient();
        for (var i = 0; i < 60; i++)
        {
            try
            {
                var res = await http.GetAsync(ApiUrl);
                if (res.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(1000);
        }
        throw new TimeoutException($"API did not respond at {ApiUrl}");
    }
}