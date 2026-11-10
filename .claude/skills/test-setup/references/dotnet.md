# .NET testing setup

Unit, integration, fakes and CI for C# projects. E2E/Gherkin lives in `gherkin-e2e.md`. Adapt names
to the target repo and match its existing frameworks (NUnit/MSTest, Moq/NSubstitute) if already used.

## Reference stack

.NET 10 SDK (pinned in `global.json`) | .NET Aspire (dev orchestration, E2E host) | xUnit 2.9 |
Reqnroll (Gherkin) + Microsoft.Playwright | FakeItEasy | Testcontainers | Keycloak (auth) |
Autofac 9 (DI) | FusionCache 2.5 | FluentValidation 12.1 | Serilog | GitVersion | NUKE (build) |
Helm + Kubernetes (deploy via `aspire publish` + `helm`)

| Layer | Project | Runs | External services mocked with |
|---|---|---|---|
| Unit | `tests/<App>.UnitTests` | in-process | FakeItEasy |
| Integration | `tests/<App>.IntegrationTests` | in-process (WebApplicationFactory) + Testcontainers | FakeItEasy, replaced where the real service is registered (see §3) |
| E2E | `tests/<App>.E2E` | Aspire, separate processes + Playwright | Fake service project (real process) |
| Browser-side calls | any Playwright test | browser | `Page.RouteAsync` |

Testcontainers = single-service integration tests. Aspire = whole-system E2E and local dev.

## Contents
0. Reference stack
1. Project layout and versions
2. Unit tests
3. Integration tests (WebApplicationFactory + Testcontainers)
4. E2E (see gherkin-e2e.md)
5. Fake external services
6. CI
7. Gotchas

## 1. Project layout and versions

```
tests/
  <App>.UnitTests/          # in-process, fakes only
  <App>.IntegrationTests/   # WebApplicationFactory + Testcontainers
  <App>.E2E/                # whole system + browser
    Features/  Steps/  Pages/  Hooks/  TestEnvironment.cs
```

- The SDK version lives only in `global.json`; don't set it anywhere else.

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
    Assert.Equal("InclusiveBetweenValidator", error.ErrorCode);              // which rule failed
    Assert.Equal($"'Quantity' must be between 1 and 100. You entered {quantity}.", error.ErrorMessage);
}
```
  - Assert the property, the rule (`ErrorCode`) **and** the message the user/caller will see.
  - The message above is illustrative: copy the exact text from a real test run (or the
    validator's `.WithMessage(...)`), never from memory — default texts can change between
    FluentValidation versions. If messages are custom, assert the custom text.
  - FluentValidation's helper does the same in one line:
    `_validator.TestValidate(req).ShouldHaveValidationErrorFor(x => x.Quantity).WithErrorCode("InclusiveBetweenValidator").WithErrorMessage(...)`.
- Time: inject `TimeProvider` and use `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing)
  instead of `DateTime.Now`.
- Interaction checks (`MustHaveHappenedOnceExactly`) only when the call *is* the contract
  (e.g. "charges quantity × price").
- FusionCache: use a real in-memory `FusionCache` instance rather than faking `IFusionCache`,
  and assert the observable effect (second read doesn't hit the fake repository). In
  integration tests, use unique keys/data per test so cached values from one test can't leak
  into another.

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

**Replace the service where the real one is registered** — find it in `Program.cs` first:

| Real registration | Replace with |
|---|---|
| `builder.Services.Add...<IPaymentClient, ...>()` (also with Autofac as the provider) | `ConfigureTestServices` + `RemoveAll<T>()` + `AddSingleton(fake)` (above) |
| Autofac `ContainerBuilder` / module (`c.RegisterType<...>().As<IPaymentClient>()`) | `builder.ConfigureTestContainer<ContainerBuilder>(b => b.RegisterInstance(Payment).As<IPaymentClient>())` |

Autofac registrations win over `IServiceCollection` ones, so a fake added in the wrong place is
silently ignored and tests run against the real client. **Prove the fake is used** with one
test whose result depends on it (e.g. fake declines → order is `PaymentFailed`); if the real
client were used, that test fails.

Real infrastructure variant — containers started once per collection:

```csharp
[CollectionDefinition("Stack")]
public class StackCollection : ICollectionFixture<StackFixture> { }

public class StackFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder("postgres:17").Build();
    private readonly KeycloakContainer _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.6")
        .WithRealm(Path.Combine(AppContext.BaseDirectory, "Realms", "orders-realm.json"))   // realm JSON copied to output
        .Build();

    public Task InitializeAsync() => Task.WhenAll(_db.StartAsync(), _keycloak.StartAsync());
    async Task IAsyncLifetime.DisposeAsync() { await _db.DisposeAsync(); await _keycloak.DisposeAsync(); }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:ordersdb", _db.GetConnectionString());
}

[Collection("Stack")]
public class OrdersDatabaseTests(StackFixture fixture) { ... }
```

- **A fake on a shared fixture is shared state.** `IClassFixture`/`ICollectionFixture` live
  across tests, so a `.Returns(false)` set in one test leaks into the next and makes results
  depend on run order. Reset it at the start of every test class constructor:

  ```csharp
  [Collection("Stack")]
  public class OrdersDatabaseTests
  {
      private readonly StackFixture _fixture;

      public OrdersDatabaseTests(StackFixture fixture)
      {
          _fixture = fixture;
          Fake.Reset(fixture.Payment);   // clears configured behaviour AND recorded calls
      }
  }
  ```
  Each test then configures exactly the behaviour it needs. This only works because tests in
  one collection run sequentially; if a fixture's tests can run in parallel, don't share a fake —
  give each test its own factory or a per-test fake.
- Testcontainers 4.x takes the image in the builder constructor (`new PostgreSqlBuilder("postgres:17")`);
  the parameterless constructor + `.WithImage(...)` is the older style. Keycloak imports a realm
  with `.WithRealm(path)`. Check the package's XML docs for the installed version.
- Pin container image tags (`postgres:17`, `keycloak:26.6`), never `latest`.
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

## 4. E2E (Aspire + Playwright + Reqnroll)

See `gherkin-e2e.md`.

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
neither, add a README stating `UNVERIFIED ASSUMPTION` and what was assumed. For a service you own, add a
Pact contract test; for a third-party provider, check the fake against its spec instead
(see `api-and-contracts.md`).

## 6. CI

```yaml
- run: dotnet build -c Debug
- run: dotnet test tests/<App>.UnitTests --no-build --logger trx --results-directory TestResults
- run: dotnet test tests/<App>.IntegrationTests --no-build --logger trx --results-directory TestResults   # needs Docker
- run: pwsh tests/<App>.E2E/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium
- run: dotnet test tests/<App>.E2E --no-build --logger trx --results-directory TestResults
- uses: actions/upload-artifact@v4   # if: always(); TestResults + playwright-artifacts
```

If the repo has a build entry point (NUKE, Cake, make), add targets there and have CI call only
`./build.sh` / `./build.cmd`; the version comes from GitVersion into NUKE. Upload trace,
screenshot and video artifacts with `if: always()`.
Helm charts (from `aspire publish -o <dir>`): gate them with `helm lint` and `helm template`.
Coverage: `coverlet.collector` + `dotnet test --collect:"XPlat Code Coverage"`, gate with a report
tool (e.g. ReportGenerator) or `coverlet.msbuild` thresholds (`/p:Threshold=<n> /p:ThresholdType=branch`).
Verify the package and property names for the version you install.

## 7. Gotchas

- `WebApplicationFactory` + Serilog bootstrap logger can throw "logger is already frozen" when
  several factories start in one process — use `CreateLogger()` in tests or guard the bootstrap.
- Serilog: structured logs only; never log secrets or tokens (including tokens fetched in test helpers).
- Narrow catches only: a readiness poll may catch `HttpRequestException`, never `catch { }`.
- xUnit 2 has no built-in random order; keep tests order-independent by construction (fresh
  instance per test, unique data) and run the suite twice.

## 8. Lessons from a full project (each was a real bug or a gate that lied)

- **Library messages depend on the culture.** FluentValidation translates its default messages
  from `CurrentUICulture`; exact-message tests failed on a German machine, and the API would send
  translated errors. Give contract-relevant messages explicitly (`.WithMessage(...)`) and run the
  message tests under a non-English culture.
- **Coverage must exclude generated code.** EF migrations made integration coverage look better
  and unit coverage worse than reality. Exclude them (`coverlet.runsettings`:
  `ExcludeByFile **/Migrations/*.cs`), merge unit + contract + integration reports, and put floors
  on critical classes (lines AND branches: a class with no branches passes any branch floor).
- **Mutation testing finds weak assertions coverage cannot.** Stryker.NET as a local tool on the
  critical files; survivors become tests or are documented as unobservable. Its thresholds must
  satisfy `break <= low <= high`, otherwise it exits without a message.
- **Test the real HTTP client.** When integration tests replace a client with a fake, the client
  itself is untested. Unit-test it with a stub `HttpMessageHandler` (errors, garbage, timeout) and
  contract-test it against the fake in-process; share the contract types between both.
- **Double submit / retry:** a payment endpoint needs an idempotency key and a concurrency test.
  Wait deterministically (e.g. until N callers wait on the lock), never with a fixed delay.
- **Inject `TimeProvider`** wherever time affects behaviour (order timestamps, session expiry).
- **Random test order** (xUnit 2 `ITestCaseOrderer` / `ITestCollectionOrderer`, printed seed) and a
  repeat-N "stability" run expose order-dependent tests; prove it with a deliberate pair once.
- **`helm lint`/`template` do not prove a deployment works.** Run the generated containers with the
  chart's exact settings (e.g. Keycloak `start` without TLS/hostname exits immediately) and install
  the chart in a throwaway cluster (kind) where the environment allows.
- **NUKE quotes interpolated values with spaces as one argument** (`$"cmd{flags}"` with
  `flags = " --x"` breaks). Write the full command per case.
- **Charge (or any external side effect) before saving is a design bug, not a test gap.** If the
  save fails after an approved charge, the money is gone and a retry charges again. Reserve the
  record first (Pending, unique key in the database), then call out, then complete it only while
  still Pending. Test the failure point deterministically: a fake that wraps the real store and
  fails one call (unit), a database trigger that fails one row (integration).
- **Every negative test needs a positive control.** Five "returns 401" token tests passed while
  the valid token was refused too (wrong test setup); only the control test showed it. Add the
  "valid case is accepted" test next to the rejections.
- **Don't wait for time, construct it.** An expired JWT is minted with `exp` in the past (test RSA
  key the API trusts), not obtained by waiting out the lifetime plus clock skew.
- **Framework settings are invisible to coverage and mutation tools.** Startup fail-open defaults
  (no auth / fake payment / in-memory store when a setting is missing), `ValidateAudience = false`,
  DI lifetimes (a non-shared lock charged three times while all tests stayed green): test them
  through the real host (WebApplicationFactory, a second instance on the same database).
- **A check that never fails proves nothing.** For every new gate or guard (hook, CI check,
  analyzer setting), make it fail once on purpose: an `||` fallback once turned a blocking hook
  into a silent pass, and an `.editorconfig` severity did not raise analyzer rules.
- **Your own review misses things.** Several of these were found by an outside review or the
  `coverage-review` agent, not by the author. Get an independent review before calling it done.

