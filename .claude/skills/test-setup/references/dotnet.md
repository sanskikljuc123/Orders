# .NET testing setup

Patterns proven in a real ASP.NET Core + Aspire repo (xUnit 2.9, FakeItEasy, WebApplicationFactory,
Testcontainers, Aspire.Hosting.Testing, Microsoft.Playwright, Reqnroll). Adapt names to the target
repo and match its existing frameworks (NUnit/MSTest, Moq/NSubstitute) if those are already used.

## Contents
1. Project layout and versions
2. Unit tests
3. Integration tests (WebApplicationFactory + Testcontainers)
4. E2E (Aspire + Playwright, optionally Reqnroll/Gherkin)
5. Fake external services
6. CI
7. Gotchas

## 1. Project layout and versions

```
tests/
  <App>.UnitTests/          # in-process, fakes only
  <App>.IntegrationTests/   # WebApplicationFactory + Testcontainers
  <App>.E2E/                # whole system + browser
    Features/  Steps/  Pages/  Hooks.cs  TestEnvironment.cs
```

- If the repo has `Directory.Packages.props` (central package management), add
  `<PackageVersion>` there and use version-less `<PackageReference Include="..."/>` in .csproj.
- Look up versions: `dotnet package search <Id> --exact-match` (or nuget.org). Don't guess.
- Add the new test projects to the solution (`dotnet sln add`, or edit `.slnx`).
- `Program` must be reachable for `WebApplicationFactory<Program>`: with top-level statements add
  `public partial class Program;` at the end of Program.cs (or `InternalsVisibleTo`).

## 2. Unit tests

```csharp
public class OrderServiceTests
{
    private readonly IPaymentClient _payment = A.Fake<IPaymentClient>();
    private readonly OrderStore _store = new();           // real collaborator, cheap
    private OrderService CreateSut() => new(_payment, _store);

    [Fact]
    public async Task Declined_payment_gives_PaymentFailed_order()
    {
        A.CallTo(() => _payment.ChargeAsync(A<decimal>._)).Returns(false);

        var order = await CreateSut().PlaceOrderAsync(new CreateOrderRequest("Book", 2));

        Assert.Equal("PaymentFailed", order.Status);
    }
}
```

- xUnit creates a new class instance per test → fields are fresh per test (good isolation).
- `[Theory]` + `[InlineData]` for boundaries. Assert *which* rule failed, not only `IsValid`:

```csharp
[Theory]
[InlineData(0)] [InlineData(101)] [InlineData(-5)]
public void Quantity_outside_1_to_100_is_rejected(int quantity)
{
    var result = _validator.Validate(new CreateOrderRequest("Book", quantity));

    var error = Assert.Single(result.Errors);
    Assert.Equal(nameof(CreateOrderRequest.Quantity), error.PropertyName);
}
```
  (FluentValidation also has `TestValidate(...).ShouldHaveValidationErrorFor(x => x.Quantity)`.)
- Time: inject `TimeProvider` and use `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing)
  instead of `DateTime.Now`.
- Interaction checks (`MustHaveHappenedOnceExactly`) only when the call *is* the contract
  (e.g. "charges quantity × price").

## 3. Integration tests (WebApplicationFactory + Testcontainers)

Fast variant — replace only the external dependency:

```csharp
public class OrdersApiFactory : WebApplicationFactory<Program>
{
    public IPaymentClient Payment { get; } = A.Fake<IPaymentClient>();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPaymentClient>();
            services.AddSingleton(Payment);
        });
}
```

If the app uses **Autofac**, `ConfigureTestServices` registrations may be overridden by the
container; register fakes with `builder.ConfigureTestContainer<ContainerBuilder>(b => b.RegisterInstance(Payment))`.
Check which one actually takes effect by running a test that depends on the fake.

Real infrastructure variant — containers started once per collection:

```csharp
[CollectionDefinition("Stack")]
public class StackCollection : ICollectionFixture<StackFixture> { }

public class StackFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:17").Build();

    public Task InitializeAsync() => _db.StartAsync();
    async Task IAsyncLifetime.DisposeAsync() => await _db.DisposeAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:ordersdb", _db.GetConnectionString());
}

[Collection("Stack")]
public class OrdersDatabaseTests(StackFixture fixture) { ... }
```

- Pin container image tags (`postgres:17`, `keycloak:26.x`), never `latest`.
- Tests sharing a container must use unique data (`$"Book-{Guid.NewGuid():N}"`) and must not
  assert on global counts ("list has 3 items") — assert `Contains` / `DoesNotContain` on own data.
- Auth (Keycloak or other OIDC): import a test realm into the container, get tokens with the
  password grant in a fixture helper (`CreateAuthenticatedClientAsync(user, pwd)`), and make the
  API's Authority the *same* URL used to get the token (issuer must match).
- Cover for each endpoint: success code + body fields, 400 with the validation message,
  401 (no/garbage token), 403/404 for another user's resource, 404 unknown id. See
  `api-and-contracts.md`.
- Read the DB through a scope to assert persistence:
  `using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();`

## 4. E2E (Aspire + Playwright)

Aspire runs services as **separate processes** — DI replacement and FakeItEasy do not work there.
Replace external services with a fake *project* registered in the AppHost.

```csharp
public static class TestEnvironment
{
    public static async Task StartAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.App_AppHost>();
        _app = await builder.BuildAsync();
        await _app.StartAsync();
        await _app.ResourceNotifications.WaitForResourceHealthyAsync("api").WaitAsync(TimeSpan.FromMinutes(5));
        ApiUrl = _app.GetEndpoint("api", "http").ToString();
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" });
    }
}
```

- Start the app host + browser once per test run (`[BeforeTestRun]` in Reqnroll, an assembly/
  collection fixture in plain xUnit). Wait on resource health, not on sleeps; prefer adding a
  health check to the resource over a polling loop.
- **Fresh `BrowserContext` per scenario** with `BaseURL = ApiUrl`; start tracing in it.
- **Login once per user**: sign in through the real login page, `context.StorageStateAsync()`,
  cache it, and open later contexts with `StorageState = cached`.
- On failure: screenshot + `Tracing.StopAsync(new() { Path = ... .zip })` into an artifacts folder
  the CI uploads. Always close the context and reset fakes in a `finally`.
- Page objects own all locators (`GetByTestId`, `GetByRole`, `GetByLabel`); steps call page-object
  methods and use `Assertions.Expect(locator).ToHaveTextAsync(...)` (auto-retrying).
- Timeouts: set once via `Assertions.SetDefaultExpectTimeout(ms)` and `context.SetDefaultTimeout(ms)`
  rather than `new() { Timeout = 30000 }` on individual assertions.
- One shared browser/app host → xUnit parallelism usually disabled for the E2E assembly
  (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`). Say why in a comment;
  isolation still comes from fresh contexts and unique data.
- Install browsers after build: `pwsh tests/<App>.E2E/bin/Debug/net10.0/playwright.ps1 install chromium`
  (`--with-deps` on CI). The browser version must match the `Microsoft.Playwright` package.

Reqnroll (Gherkin), if the repo uses it:
- `Features/*.feature` written in user language; tags `@smoke`, `@regression`.
- Steps describe behaviour (`When I order 2 of "Book"`), not clicks.
- Per-scenario state via DI-injected classes (context injection), not static fields.

## 5. Fake external services

A minimal ASP.NET project that mimics the provider's endpoints, plus a test-only admin endpoint
to switch behaviour, reset after each scenario:

```csharp
var mode = "approve"; // approve | decline | error
app.MapPost("/charge", (ChargeRequest r) => mode switch
{
    "decline" => Results.Ok(new ChargeResponse(false)),
    "error"   => Results.StatusCode(500),
    _         => Results.Ok(new ChargeResponse(true))
});
app.MapPost("/__admin/mode", (ModeRequest r) => { mode = r.Mode; return Results.Ok(); });
```

Build request/response shapes from the provider's OpenAPI spec or recorded real responses. With
neither, add a README stating `UNVERIFIED ASSUMPTION` and what was assumed. Add a contract test
(Pact) when the real provider can verify it.

## 6. CI

```yaml
- run: dotnet build -c Debug
- run: dotnet test tests/<App>.UnitTests --no-build --logger trx --results-directory TestResults
- run: dotnet test tests/<App>.IntegrationTests --no-build --logger trx --results-directory TestResults   # needs Docker
- run: pwsh tests/<App>.E2E/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
- run: dotnet test tests/<App>.E2E --no-build --logger trx --results-directory TestResults
- uses: actions/upload-artifact@v4   # if: always(); TestResults + playwright-artifacts
```

If the repo has a build entry point (NUKE, Cake, make), add targets there and have CI call it.
Coverage: `coverlet.collector` + `dotnet test --collect:"XPlat Code Coverage"`, gate with a report
tool (e.g. ReportGenerator) or `coverlet.msbuild` thresholds (`/p:Threshold=<n> /p:ThresholdType=branch`).
Verify the package and property names for the version you install.

## 7. Gotchas

- `WebApplicationFactory` + Serilog bootstrap logger can throw "logger is already frozen" when
  several factories start in one process — use `CreateLogger()` in tests or guard the bootstrap.
- Narrow catches only: a readiness poll may catch `HttpRequestException`, never `catch { }`.
- xUnit 2 has no built-in random order; keep tests order-independent by construction (fresh
  instance per test, unique data) and run the suite twice.
