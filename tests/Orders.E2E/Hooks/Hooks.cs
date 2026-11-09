namespace Orders.E2E.Hooks;

[Binding]
public static class RunHooks
{
    [BeforeTestRun]
    public static Task StartSystem() => TestEnvironment.StartAsync();   // Aspire + browser, once

    [AfterTestRun]
    public static Task StopSystem() => TestEnvironment.StopAsync();
}

[Binding]
public class ScenarioHooks(ScenarioBrowser browser, ScenarioContext scenario)
{
    [BeforeScenario]
    public Task OpenBrowser() => browser.OpenAsync();   // anonymous; "I am signed in" replaces it

    [AfterScenario]
    public async Task CloseBrowserAndResetFakes()
    {
        try
        {
            // Scenario Outline rows share a title; add the example values so artifacts don't overwrite each other.
            var args = scenario.ScenarioInfo.Arguments.Values.Cast<object>().Select(v => v?.ToString());
            var name = string.Join(" ", new[] { scenario.ScenarioInfo.Title }.Concat(args));
            await browser.CloseAsync(name, failed: scenario.TestError is not null);
        }
        finally
        {
            await TestEnvironment.ResetFakesAsync();   // payment back to "approve"
        }
    }
}
