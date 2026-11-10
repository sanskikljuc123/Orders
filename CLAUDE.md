# Project: Orders E2E / test platform (learning project)

Prefer small steps over big dumps.

## Golden rules
1. **Run before you trust.** Never claim something works until you ran `dotnet build` and the
   relevant tests and saw them pass. Show the real output summary.
2. **Do not invent.** Never invent package versions, APIs, fields or external-service behaviour.
   Check NuGet / official docs. If unsure, say so and ask.
3. **Fakes come from contracts.** An external-service fake is built ONLY from an attached
   OpenAPI spec or recorded real responses. No spec/recordings -> keep the fake minimal and
   write "UNVERIFIED ASSUMPTION" in its README.
4. **One phase at a time.** Finish a phase, run the gate, commit, then ask before starting the next.

## Stack
.NET 10 SDK (see global.json) | .NET Aspire (dev orchestration, E2E host) | xUnit 2.9 |
Reqnroll (Gherkin) + Microsoft.Playwright | FakeItEasy | Testcontainers |
Keycloak (auth) | Autofac 9 (DI) | FusionCache 2.5 | FluentValidation 12.1 | Serilog |
GitVersion | NUKE (build) | Helm + Kubernetes (deploy via `aspire publish` + `helm`)

## Versions
- All NuGet versions ONLY in `Directory.Packages.props`. No `Version=` in .csproj files.
- .NET SDK ONLY in `global.json`.
- Playwright browsers/Docker image version in CI must match the `Microsoft.Playwright` package.
- Dependency bumps: one PR, full test run.

## Test layers and mocking (important)
| Layer | Project | Runs | External services mocked with |
|---|---|---|---|
| Unit | tests/Orders.UnitTests | in-process | FakeItEasy |
| Integration | tests/Orders.IntegrationTests | in-process (WebApplicationFactory) + Testcontainers | FakeItEasy, replaced where the real service is registered: `ConfigureTestServices` for `builder.Services` (e.g. `IPaymentClient`), Autofac `ConfigureTestContainer` for container registrations |
| E2E | tests/Orders.E2E | Aspire separate processes + Playwright | `FakePaymentService` project (real process) |
| Browser-side calls | any Playwright test | browser | `Page.RouteAsync` |

- Aspire tests run the app as separate processes: DI replacement / FakeItEasy does NOT work there.
- Testcontainers = single-service integration tests. Aspire = whole-system E2E and local dev.
- Writing or setting up tests: use the `test-setup` skill (`.claude/skills/test-setup/`).

## Gherkin / Playwright conventions
- Features in `tests/Orders.E2E/Features`, steps in `Steps`, page objects in `Pages`, hooks in `Hooks`.
- Steps describe behaviour, not clicks. Selectors live ONLY in page objects.
- Selectors: `GetByRole`, `GetByLabel`, `GetByTestId`. No brittle CSS/XPath.
- No `Thread.Sleep`; rely on Playwright auto-waiting and `Expect(...)`.
- Each scenario isolated: fresh browser context. Tags: `@smoke`, `@regression`.
- Keycloak login: log in once, save Playwright storage state, reuse it.
- On failure keep trace + screenshot + video as CI artifacts.
- Use `playwright-cli` + its skills (`playwright-cli install --skills`) to explore the live UI
  BEFORE writing selectors.

## Git
- Commit messages and PR descriptions: no AI attribution. No `Co-Authored-By: Claude ...`,
  no `Claude-Session: ...` links, no "Generated with Claude Code" lines.
- Ask before pushing.

## Build / CI / deploy
- NUKE (`build/`) is the single entry point: Restore, Compile, Test, Publish, Helm.
  GitHub Actions only calls `./build.sh` / `./build.cmd`.
- Version comes from GitVersion into NUKE.
- NUKE commands: never interpolate a value that contains spaces (e.g. `$" --flag"`) into
  `DotNet($"...")` / tool calls; NUKE quotes it as ONE argument. Write the full command per case.
  (Broke `InstallBrowser` and the ghcr.io push.) Test the push path with
  `./build.sh --target Images --push-images true --image-registry localhost:5000` and a local `registry:2`.
- Deploy: `aspire publish -o <dir>` then `helm upgrade --install ... -f deploy/values.<env>.yaml`.
  Do NOT rely on `aspire deploy` for Kubernetes until it is proven stable here.
- Logging: Serilog structured logs; never log secrets/tokens.
- Keycloak: the test realm (`src/Orders.AppHost/Realms`, users ana/bob with known passwords) is
  imported only for local runs and tests, never in `aspire publish`. A deployment needs its own
  realm: no test users, exact redirect URIs, HTTPS required, brute-force protection on.

**Not done yet** (target state above, current state here):
- Only `deploy/values.dev.yaml` exists; image tags are passed with `--set` at deploy time.
  Production Keycloak realm: `deploy/realms/orders-realm.prod.json` (no users, HTTPS required,
  exact redirect `https://orders.example.com/signin-oidc` = PLACEHOLDER URL, brute-force
  protection, no password grant), guarded by `ProductionRealmTests`. Aspire's publisher cannot
  carry a realm import (it breaks `aspire publish`), so the NUKE Publish target adds
  `deploy/chart-extras` to the generated chart: a Helm post-install/upgrade Job that creates the
  realm with `kcadm.sh` if missing (never overwrites). Its Keycloak image must match the chart's.
- API login settings: secure by default (HTTPS metadata, `form_post`, Secure cookies). Plain-http
  settings only via `Auth:AllowInsecureHttp` in `appsettings.Development.json` (local runs, tests).
  Guarded by `AuthSecurityOptionsTests`.
- Login sessions are stored server-side (`SessionStore`, in-memory FusionCache): the cookie holds
  only a key and logout revokes it. Limitation: an API restart logs everyone out and sessions are
  not shared between replicas; run one API replica or add a distributed L2 cache first.
- Orders are idempotent: the UI sends an `Idempotency-Key` per order attempt; a repeated key
  returns the first order (header `Idempotent-Replayed: true`) without charging again. Concurrent
  requests with one key are serialized in-process; a unique (UserId, IdempotencyKey) index is the
  backstop across replicas. Deployed Keycloak needs HTTPS: `Auth__Authority` in
  `deploy/values.dev.yaml` is a PLACEHOLDER (`https://auth.example.com/realms/orders`).
- E2E locally where the Playwright download is blocked: `E2E_CHROMIUM_PATH=<installed chrome>`
  and `E2E_VIDEO=0` are a local-only workaround; CI must install the matching browser.

## Phases (each ends with a GATE; do not skip)
0. Foundation: solution, global.json, props, GitVersion, .gitignore, NUKE skeleton.
   GATE: `dotnet build` green.
1. Sample app "Orders": API (FluentValidation, FusionCache, Serilog, Autofac) + simple web UI.
   GATE: app starts, manual smoke check described in README.
2. AppHost: Keycloak + database (+ FakePaymentService placeholder).
   GATE: `aspire run` starts everything, dashboard shows healthy.
3. Unit tests (FakeItEasy). GATE: green.
4. Integration tests (Testcontainers, Keycloak). GATE: green, needs Docker.
5. E2E: first Reqnroll scenario with Playwright. GATE: green locally, trace on failure.
6. NUKE + GitVersion + GitHub Actions. GATE: green pipeline.
7. Helm via `aspire publish`. GATE: `helm lint` + `helm template` OK.

## Commands (fill in as they are verified)
- Run app: `dotnet tool restore` then `dotnet aspire run` (API/UI http://localhost:5189; smoke check in README).
  Plain `dotnet run --project src/Orders.AppHost` aborted (exit 134) in the cloud dev container.
- Build: `dotnet build` | Full pipeline (same as CI): `./build.sh --target Test Helm`
- Helm chart (needs Helm 3.22.1 on PATH): `./build.sh --target Helm [--environment dev]`
  runs `aspire publish -o k8s-artifacts` + `helm lint` + `helm template`. `k8s-artifacts/` is
  git-ignored because it contains generated passwords; never commit it.
- Container images: `./build.sh --target Images` builds `orders-api` and `orders-payment` into
  local Docker (.NET SDK container support, no Dockerfile). CI on main adds `--push-images true`
  and pushes to `ghcr.io/sanskikljuc123/<image>:<SemVer>` with `GITHUB_TOKEN`.
- Coverage: NUKE `CoverageGate` merges the unit + contract + integration reports (generated
  migrations excluded via `coverlet.runsettings`) and requires all production code >= 90% branches
  and each critical class (`CriticalClasses` in `build/Build.cs`) >= 80% lines and branches.
  Summary: `TestResults/coverage-summary.md` (and the GitHub job summary). Raise floors with real
  tests (`--total-branch-floor`, `--critical-branch-floor`); never write tests just to hit them.
- Mutation testing (Stryker.NET 5, local tool): `./build.sh --target Mutation` runs `stryker/api.json`
  (OrderService, SessionStore, CreateOrderValidator with the unit tests; break below 85%) and
  `stryker/shared.json` (payment client with unit + contract tests; break below 90%). Reports in
  `TestResults/mutation/*/reports/`. Stryker rejects thresholds unless break <= low <= high.
  Known surviving mutants (SessionStore expiry boundary and cache duration) are not observable:
  the cookie handler checks expiry itself.
- Test order is random (unit, contract, integration; `tests/TestSupport/RandomTestOrder.cs`). The
  seed is printed ("Random test order seed: N"); replay a failing order with `TEST_ORDER_SEED=N`.
- Flaky / order-dependent tests: `./build.sh --target Stability --repeat 3 [--stability-projects ...]`
  runs each project N times in a new order and lists tests that failed in some runs
  (`TestResults/stability-summary.md`). With Orders.E2E, run `InstallBrowser` first. Nightly
  (`.github/workflows/ci-nightly.yml`) runs Stability for all four projects plus Mutation.
- Every test target writes passed/failed/skipped and failed test names to the GitHub job summary.
- Tests: `dotnet test`
- Playwright browsers: `pwsh tests/Orders.E2E/bin/Debug/net10.0/playwright.ps1 install`
- Unit: `dotnet test tests/Orders.UnitTests` | Integration (Docker): `dotnet test tests/Orders.IntegrationTests`
  | E2E (Docker + browser): `dotnet test tests/Orders.E2E` (`--filter Category=smoke` for smoke only)
