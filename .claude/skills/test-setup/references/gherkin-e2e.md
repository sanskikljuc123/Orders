# E2E with Gherkin (Reqnroll) + Playwright for .NET

Whole-system journeys: Aspire starts the real services as separate processes, Playwright drives
the browser, Reqnroll turns `.feature` files into xUnit tests.

## Contents
1. Layout
2. Writing features
3. Step definitions
4. Page objects and selectors
5. Hooks, environment and isolation
6. Login once (Keycloak storage state)
7. Waiting, timeouts, assertions
8. Failure artifacts and debugging
9. Running and tagging

## 1. Layout

```
tests/<App>.E2E/
  Features/   *.feature, one per business capability
  Steps/      [Binding] step classes, grouped by domain (OrderSteps, LoginSteps)
  Pages/      page objects - the ONLY place selectors live
  Hooks/      [BeforeTestRun]/[BeforeScenario]/[AfterScenario] bindings, ScenarioBrowser
  TestEnvironment.cs   Aspire app host + browser, started once per run
```

Packages (versions only in `Directory.Packages.props`): `Reqnroll.xUnit`, `Microsoft.Playwright`,
`Aspire.Hosting.Testing`, `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`.

## 2. Writing features

Gherkin is documentation the business can read. Write **what** the user does and sees, not how.

```gherkin
@regression
Feature: Placing an order
  Signed-in customers can order products; payment decides the order status.

  Background:
    Given I am signed in as "ana"

  @smoke
  Scenario: Order is paid when payment is approved
    Given the payment service approves payments
    When I order 2 of "Book"
    Then I see the result "Order Paid"

  Scenario: Empty product name is rejected
    When I order 1 without a product name
    Then I see the result "Validation error"
```

One representative validation scenario is enough in E2E; boundary values (0, 1, 100, 101, empty,
51 chars) belong in unit tests. Use `Scenario Outline` for variations that need the whole
system — users, roles, permissions — not for input permutations:

```gherkin
Feature: Order privacy

  Scenario Outline: A user never sees another user's orders
    Given "<other>" has ordered "<secret>"
    And I am signed in as "<user>"
    When I open my orders
    Then my order list does not contain "<secret>"

    Examples:
      | user | other | secret            |
      | ana  | bob   | Bobs secret order |
      | bob  | ana   | Anas secret order |
```

- **Declarative, not imperative.** `When I order 2 of "Book"` — not `When I fill "#product"
  with "Book" and click "Submit"`. UI changes then touch page objects, not features.
- **One behaviour per scenario**, ideally one `When`. The scenario title states the rule
  ("Users see only their own orders"), so a failing title tells you what broke.
- **Given = state, When = action, Then = observable outcome.** No assertions in Given/When;
  no actions in Then.
- **Background** only for preconditions every scenario needs; keep it to 1–3 lines.
- **Scenario Outline + Examples** to parameterize (roles, boundaries) instead of copy-pasted
  scenarios. Add a descriptive column (`case`) so reports show what each row means.
- **Concrete, meaningful data** ("Bobs secret order") over placeholders ("test1").
- **Tags** for selective runs: `@smoke` (few, critical journeys, run on every push),
  `@regression` (everything else). Tag at Feature level when it applies to all scenarios.
- Keep a feature file to one capability; split when it grows past ~10 scenarios.
- E2E covers journeys. Push validation permutations down to unit tests; keep one representative
  "invalid input shows an error" scenario here.

## 3. Step definitions

```csharp
[Binding]
public class OrderSteps(ScenarioBrowser sb)          // context injection: fresh per scenario
{
    private OrderPage Orders => new(sb.Page);

    [When("I order {int} of {string}")]
    public async Task OrderProduct(int quantity, string product)
    {
        await Orders.OpenAsync();
        await Orders.PlaceOrderAsync(product, quantity);
    }

    [Then("I see the result {string}")]
    public Task SeeResult(string expected) => Assertions.Expect(Orders.Result).ToHaveTextAsync(expected);
}
```

- Cucumber expressions (`{string}`, `{int}`) over regex.
- Steps are **thin**: call page objects / test-environment helpers, then assert. No selectors,
  no `HttpClient` plumbing duplicated across steps (put it in a helper).
- **Per-scenario state** goes in injected classes (constructor injection), never static fields.
  Run-wide expensive things (browser, app host) are static on `TestEnvironment`.
- **Reusable, generic wording**, so one step serves many scenarios. Don't call steps from steps;
  share a C# method instead.
- Group steps by domain, not by feature file. Delete unused steps (Reqnroll reports them).
- Data tables: `DataTable` + `CreateSet<T>()` / `CreateInstance<T>()` (`Reqnroll.Assist`) for
  structured input rather than many parameters.
- Setting up data the scenario doesn't test (e.g. "bob has ordered X") may go through the API
  or a separate browser context — faster and less flaky than clicking through the UI.

## 4. Page objects and selectors

```csharp
public class OrderPage(IPage page)
{
    public ILocator Result => page.GetByTestId("result");
    public ILocator MyOrders => page.GetByRole(AriaRole.List, new() { Name = "My orders" });

    public Task OpenAsync() => page.GotoAsync("/");          // relative to BaseURL

    public async Task PlaceOrderAsync(string product, int quantity)
    {
        await page.GetByLabel("Product").FillAsync(product);
        await page.GetByLabel("Quantity").FillAsync(quantity.ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Place order" }).ClickAsync();
    }
}
```

- Selectors live **only** in page objects. Prefer `GetByRole` → `GetByLabel` → `GetByTestId`.
  No brittle CSS/XPath. Missing a good hook? Add a `data-testid` or accessible label to the app.
- Exception: third-party pages you don't own (e.g. Keycloak's login form, `#username`) —
  isolate them in one method with a comment.
- **Explore the live UI first** with `playwright-cli` and its skills
  (`playwright-cli install --skills`) to find real roles/labels/test ids before writing selectors.
- Methods express user intent (`PlaceOrderAsync`), and expose locators for assertions;
  page objects don't assert.
- Always relative URLs in `GotoAsync`; `BaseURL` is set on the context.

## 5. Hooks, environment and isolation

```csharp
[Binding]
public static class RunHooks
{
    [BeforeTestRun] public static Task Start() => TestEnvironment.StartAsync();   // Aspire + browser once
    [AfterTestRun]  public static Task Stop()  => TestEnvironment.StopAsync();
}

[Binding]
public class ScenarioHooks(ScenarioBrowser sb, ScenarioContext scenario)
{
    [BeforeScenario] public Task Open() => sb.OpenAsync();      // fresh BrowserContext per scenario

    [AfterScenario]
    public async Task Close()
    {
        try { await sb.SaveArtifactsAsync(scenario.ScenarioInfo.Title, failed: scenario.TestError is not null); }
        finally
        {
            await sb.Context.CloseAsync();
            await TestEnvironment.ResetFakesAsync();             // e.g. payment mode back to "approve"
        }
    }
}
```

`TestEnvironment.StartAsync` (Aspire):

```csharp
var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.App_AppHost>();
_app = await builder.BuildAsync();
await _app.StartAsync();
await _app.ResourceNotifications.WaitForResourceHealthyAsync("api").WaitAsync(TimeSpan.FromMinutes(5));
ApiUrl = _app.GetEndpoint("api", "http").ToString();
_playwright = await Playwright.CreateAsync();
Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" });
```

- Aspire runs services as **separate processes**: DI replacement / FakeItEasy do **not** work.
  Replace external services with a fake service project in the AppHost (see `dotnet.md` §5);
  give it a test-only control endpoint and reset it in `[AfterScenario]`.
- Browser-side calls to third parties: `Page.RouteAsync(...)` to fulfil them.
- Wait on resource health (add health checks to resources) instead of polling loops/sleeps.
- **Each scenario isolated**: fresh `BrowserContext` (own cookies/storage), unique data
  (`$"Book-{Guid.NewGuid():N}"` when names must not collide), cleanup in `finally`, never depend
  on another scenario having run.
- Keep hooks minimal; tag-scoped hooks (`[BeforeScenario("needs-admin")]`) for setup only some
  scenarios need.
- One shared browser/app host usually means `[assembly: CollectionBehavior(DisableTestParallelization = true)]`.
  Comment why. If you enable parallelism later, isolation above must already hold.

## 6. Login once (Keycloak storage state)

```csharp
public static async Task<string> GetStorageStateAsync(string user)
{
    if (StorageStates.TryGetValue(user, out var cached)) return cached;
    var context = await Browser.NewContextAsync(new() { BaseURL = ApiUrl });
    var app = new OrderPage(await context.NewPageAsync());
    await app.OpenAsync();
    await app.SignInAsync(user, PasswordFor(user));
    await Assertions.Expect(app.SignedInUser).ToContainTextAsync(user);
    var state = await context.StorageStateAsync();
    await context.CloseAsync();
    return StorageStates[user] = state;
}

[Given("I am signed in as {string}")]
public async Task SignedInAs(string user) => await sb.OpenAsync(await TestEnvironment.GetStorageStateAsync(user));
```

- Sign in through the real login page once per user per run, reuse the state in new contexts.
- Only the login feature itself exercises the login form every time.
- Test users and passwords come from the imported test realm, never real accounts; never log tokens.

## 7. Waiting, timeouts, assertions

- No `Thread.Sleep` / `Task.Delay` as a wait. Rely on auto-waiting actions and web-first
  `Assertions.Expect(locator)...` (they retry). For a specific event:
  `page.WaitForResponseAsync("**/orders")`, `page.WaitForURLAsync("**/done")`.
- **Timeouts in one place**, at startup: `Assertions.SetDefaultExpectTimeout(10_000)` and
  `context.SetDefaultTimeout(...)` — not `new() { Timeout = 30000 }` on individual assertions.
- Assert specific text/values: `ToHaveTextAsync("Order Paid")`, `Not.ToContainTextAsync("Bobs secret order")`.
- Playwright .NET has no soft assertions; for several independent checks, use separate `Then`
  steps (each failure is reported with its step) or split the scenario.
- For non-UI checks inside a step (status codes), `Assert.Equal(expected, actual)` with values.

## 8. Failure artifacts and debugging

Per scenario context:

```csharp
Context = await TestEnvironment.Browser.NewContextAsync(new()
{
    BaseURL = TestEnvironment.ApiUrl,
    StorageState = storageState,
    RecordVideoDir = Path.Combine(ArtifactsDir, "videos")
});
await Context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
```

On failure: full-page screenshot, `Tracing.StopAsync(new() { Path = $"{name}.zip" })`, close the
context, then `await page.Video!.SaveAsAsync($"{name}.webm")`. On success: `Tracing.StopAsync()`
without a path, close, `await page.Video!.DeleteAsync()`. CI uploads the artifacts folder with
`if: always()`.

Debug: `pwsh tests/<App>.E2E/bin/Debug/net10.0/playwright.ps1 show-trace <name>.zip`, or run
headed (`HEADED=1`) with `SlowMo`.

## 9. Running and tagging

- Install browsers after build: `pwsh tests/<App>.E2E/bin/Debug/net10.0/playwright.ps1 install chromium`
  (`--with-deps` on CI). Browser/Docker image version must match the `Microsoft.Playwright` package.
- Reqnroll exposes tags as xUnit traits (`Category`): `dotnet test tests/<App>.E2E --filter "Category=smoke"`.
  Confirm against the generated code if a filter selects nothing.
- Run the suite twice before declaring it stable; a scenario that fails once in N runs is a bug
  to fix, not a retry to add.
