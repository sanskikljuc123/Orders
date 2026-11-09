using Microsoft.Playwright;
using Reqnroll;

public class ScenarioBrowser
{
    public IBrowserContext Context { get; set; } = default!;
    public IPage Page { get; set; } = default!;
}

[Binding]
public static class Setup
{
    [BeforeTestRun]
    public static Task Start() => TestEnvironment.StartAsync();

    [AfterTestRun]
    public static Task Stop() => TestEnvironment.StopAsync();
}

[Binding]
public class ScenarioHooks(ScenarioBrowser sb, ScenarioContext scenario)
{
    private static readonly string ArtifactsDir =
        Path.Combine(AppContext.BaseDirectory, "playwright-artifacts");

    [BeforeScenario]
    public async Task OpenBrowser()
    {
         var token = await TestEnvironment.GetAccessTokenAsync();
        sb.Context = await TestEnvironment.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = TestEnvironment.ApiUrl,
            ExtraHTTPHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
        });

        await sb.Context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true
        });

        sb.Page = await sb.Context.NewPageAsync();
    }

    [AfterScenario]
    public async Task CloseBrowser()
    {
        try
        {
            if (scenario.TestError is not null)
            {
                Directory.CreateDirectory(ArtifactsDir);
                var name = SafeName(scenario.ScenarioInfo.Title);

                await sb.Page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(ArtifactsDir, $"{name}.png"),
                    FullPage = true
                });
                await sb.Context.Tracing.StopAsync(new TracingStopOptions
                {
                    Path = Path.Combine(ArtifactsDir, $"{name}.zip")
                });
            }
            else
            {
                await sb.Context.Tracing.StopAsync();
            }
        }
        finally
        {
            await sb.Context.CloseAsync();
            await TestEnvironment.SetPaymentModeAsync("approve");
        }
    }

    private static string SafeName(string title) =>
        string.Concat(title.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}