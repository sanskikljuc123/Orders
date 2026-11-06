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

    public static async Task StartAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Orders_AppHost>();
        _app = await builder.BuildAsync();
        await _app.StartAsync();

        var timeout = TimeSpan.FromMinutes(3);
        await _app.ResourceNotifications.WaitForResourceAsync("payment", KnownResourceStates.Running).WaitAsync(timeout);
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