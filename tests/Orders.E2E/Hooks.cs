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
public class ScenarioHooks(ScenarioBrowser sb)
{
    [BeforeScenario]
    public async Task OpenBrowser()
    {
        sb.Context = await TestEnvironment.Browser.NewContextAsync(
            new BrowserNewContextOptions { BaseURL = TestEnvironment.ApiUrl });
        sb.Page = await sb.Context.NewPageAsync();
    }

    [AfterScenario]
    public async Task CloseBrowser()
    {
        await sb.Context.CloseAsync();
        await TestEnvironment.SetPaymentModeAsync("approve");
    }
}