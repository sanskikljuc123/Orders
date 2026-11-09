using System.Collections.Concurrent;
using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Orders.E2E.Pages;

namespace Orders.E2E;

/// <summary>
/// Whole system (Aspire AppHost: Keycloak, PostgreSQL, FakePaymentService, Orders.Api) and one
/// browser, started once per test run. Per-scenario state lives in ScenarioBrowser, not here.
/// </summary>
public static class TestEnvironment
{
    private static DistributedApplication? _app;
    private static IPlaywright? _playwright;
    private static HttpClient? _payment;
    private static readonly ConcurrentDictionary<string, string> StorageStates = new();

    public static IBrowser Browser { get; private set; } = null!;
    public static string ApiUrl { get; private set; } = null!;
    public static string ArtifactsDir { get; } = Path.Combine(AppContext.BaseDirectory, "playwright-artifacts");

    // Timeouts live here only, never on individual steps or assertions.
    public const int ActionTimeoutMs = 15_000;
    public const int ExpectTimeoutMs = 10_000;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(5);

    public static async Task StartAsync()
    {
        using var startup = new CancellationTokenSource(StartupTimeout);
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Orders_AppHost>(startup.Token);
        _app = await builder.BuildAsync(startup.Token);
        await _app.StartAsync(startup.Token);
        // api has an HTTP health check (/health) and waits for db, keycloak and payment itself.
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("api", startup.Token);
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("payment", startup.Token);

        ApiUrl = _app.GetEndpoint("api", "http").ToString();
        _payment = _app.CreateHttpClient("payment", "http");

        Assertions.SetDefaultExpectTimeout(ExpectTimeoutMs);
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = Environment.GetEnvironmentVariable("HEADED") != "1",
            // Local escape hatch only, for machines where `playwright.ps1 install` cannot download
            // the browser. CI must install the browser matching the Microsoft.Playwright package.
            ExecutablePath = Environment.GetEnvironmentVariable("E2E_CHROMIUM_PATH") is { Length: > 0 } path ? path : null,
        });
    }

    public static async Task StopAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        _payment?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    /// <summary>Test users from src/Orders.AppHost/Realms/orders-realm.json; never real accounts.</summary>
    private static string PasswordFor(string user) => user switch
    {
        "ana" => "ana123",
        "bob" => "bob123",
        _ => throw new ArgumentOutOfRangeException(nameof(user), user, "Not a user in the test realm")
    };

    public static async Task<IBrowserContext> NewContextAsync(string? storageState = null, string? videoDir = null)
    {
        var context = await Browser.NewContextAsync(new()
        {
            BaseURL = ApiUrl,
            StorageState = storageState,
            RecordVideoDir = videoDir,
        });
        context.SetDefaultTimeout(ActionTimeoutMs);
        return context;
    }

    /// <summary>Signs in through the real Keycloak page once per user per run and caches the state.</summary>
    public static async Task<string> GetStorageStateAsync(string user)
    {
        if (StorageStates.TryGetValue(user, out var cached)) return cached;

        var context = await NewContextAsync();
        try
        {
            var shop = new OrderPage(await context.NewPageAsync());
            await shop.OpenAsync();
            await shop.SignInAsync(user, PasswordFor(user));
            await Assertions.Expect(shop.SignedInUser).ToHaveTextAsync(user);
            return StorageStates[user] = await context.StorageStateAsync();
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    /// <summary>Test data the scenario does not exercise goes through the API, as that user.</summary>
    public static async Task PlaceOrderAsAsync(string user, string product, int quantity = 1)
    {
        var context = await NewContextAsync(await GetStorageStateAsync(user));
        try
        {
            var res = await context.APIRequest.PostAsync("/orders", new() { DataObject = new { productName = product, quantity } });
            if (res.Status != 201)
                throw new InvalidOperationException($"Setup order for {user} failed: HTTP {res.Status} {await res.TextAsync()}");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    public static async Task SetPaymentModeAsync(string mode)
    {
        var res = await _payment!.PostAsJsonAsync("/__admin/mode", new { mode });
        res.EnsureSuccessStatusCode();
    }

    public static async Task ResetFakesAsync()
    {
        var res = await _payment!.PostAsync("/__admin/reset", null);
        res.EnsureSuccessStatusCode();
    }
}
