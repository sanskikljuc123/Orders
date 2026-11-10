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
| Contract | tests/Orders.ContractTests | in-process: real `HttpPaymentClient` against real `FakePaymentService` | nothing (both sides real) |
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
  BEFORE writing selectors. Where it is not installed (e.g. the cloud dev container), read the
  markup (`src/Orders.Api/wwwroot/index.html`), treat selectors as unverified until a green run and
  a mutation check confirm them, and say so.

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

## How it works now (keep in sync when you change it)
- **Production realm:** `deploy/realms/orders-realm.prod.json` (no users, `sslRequired: all`, exact
  redirect and post-logout URIs, brute-force protection, code flow + PKCE only), guarded by
  `ProductionRealmTests`. Aspire's publisher cannot carry a realm import (it breaks
  `aspire publish`), so NUKE Publish adds `deploy/chart-extras`: a Helm post-install/upgrade Job
  that creates the realm with `kcadm.sh` if missing (never overwrites). Its Keycloak image must
  match the chart's (Publish fails otherwise).
- **Keycloak in the chart** runs `start` (production mode) with `KC_HTTP_ENABLED`,
  `KC_PROXY_HEADERS=xforwarded` and `KC_HOSTNAME` (behind a TLS-terminating ingress). Without them
  it exits with "Key material not provided to setup HTTPS".
- **API login:** secure by default (HTTPS metadata, `form_post`, Secure cookies). Plain-http
  settings only via `Auth:AllowInsecureHttp` in `appsettings.Development.json` (local runs,
  tests); guarded by `AuthSecurityOptionsTests`. Logout ends the app cookie and the Keycloak
  session. Sessions are stored server-side (`SessionStore`), so logout revokes the cookie.
- **Orders:** idempotent with an `Idempotency-Key` (the UI sends one per order attempt); a repeated
  key returns the first order (`Idempotent-Replayed: true`) without charging again. Same-key
  requests are serialized in-process; a unique (UserId, IdempotencyKey) index is the backstop.
- **Payment:** contract and client in `src/Orders.Shared/Payments.cs` (shared with the fake).
  Anything but a clear approval (HTTP error, unreadable body, unreachable, timeout after
  `Payment:TimeoutSeconds`, default 10) means not paid and is logged as a warning.
- **Validation messages** are explicit English text, independent of the server culture.
- What each critical behaviour is tested by: [`TESTING.md`](TESTING.md).

## Open points and known limitations
- PLACEHOLDER URLs to replace before a real deployment: `https://orders.example.com` (prod realm
  redirect/logout URIs), `https://auth.example.com` (`Auth__Authority`, `keycloak_hostname` in
  `deploy/values.dev.yaml`). Only `values.dev.yaml` exists; image tags are passed with `--set`.
- GHCR packages are private by default: make them public or give the cluster a pull secret.
- One API replica only: sessions and idempotency locks are in-memory (a restart logs users out).
  A distributed L2 cache would lift this.
- The chart has never been installed in a real cluster; `DeploySmoke` (nightly) is the first real
  install and cannot run in the cloud dev container (no nested Kubernetes).
- The payment provider contract is an UNVERIFIED ASSUMPTION (`src/FakePaymentService/README.md`).
- E2E where the Playwright download is blocked (cloud dev container): `E2E_CHROMIUM_PATH=<installed
  chrome>` and `E2E_VIDEO=0` are a local-only workaround; CI installs the matching browser.

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
**Run**
- App: `dotnet tool restore` then `dotnet aspire run` (API/UI http://localhost:5189; smoke check in
  README). Plain `dotnet run --project src/Orders.AppHost` aborted (exit 134) in the cloud dev container.
- Build: `dotnet build` | everything CI runs on a pull request: `./build.sh --target Test Helm Images`

**Tests** (what each needs)
- Unit: `dotnet test tests/Orders.UnitTests` (nothing) | Contract: `dotnet test tests/Orders.ContractTests` (nothing)
- Integration: `dotnet test tests/Orders.IntegrationTests` (Docker)
- E2E: `dotnet test tests/Orders.E2E` (Docker + Playwright browser:
  `pwsh tests/Orders.E2E/bin/Debug/net10.0/playwright.ps1 install`); `--filter Category=smoke` for smoke only
- `dotnet test` alone runs all four, so it needs Docker and the browser too.
- Test order is random (unit, contract, integration; `tests/TestSupport/RandomTestOrder.cs`). The
  seed is printed ("Random test order seed: N"); replay a failing order with `TEST_ORDER_SEED=N`.

**Quality gates**
- Coverage: `CoverageGate` merges the unit + contract + integration reports (generated migrations
  excluded via `coverlet.runsettings`): all production code >= 90% branches, each critical class
  (`CriticalClasses` in `build/Build.cs`) >= 80% lines and branches. Summary in
  `TestResults/coverage-summary.md` and the GitHub job summary. Raise floors with real tests
  (`--total-branch-floor`, `--critical-branch-floor`); never write tests just to hit them.
- Mutation (Stryker.NET 5, own tool manifest `stryker/.config` because restoring it with aspire.cli fails): `./build.sh --target Mutation` runs `stryker/api.json`
  (OrderService, SessionStore, CreateOrderValidator; break below 85%) and `stryker/shared.json`
  (payment client; break below 90%). Reports in `TestResults/mutation/*/reports/`. Stryker rejects
  thresholds unless break <= low <= high. Known survivors (SessionStore expiry boundary and cache
  duration) are not observable: the cookie handler checks expiry itself.
- Flaky / order-dependent: `./build.sh --target Stability --repeat 3 [--stability-projects ...]`
  runs each project N times in a new order and lists tests that failed in some runs
  (`TestResults/stability-summary.md`). With Orders.E2E, run `InstallBrowser` first.
- Every test target writes passed/failed/skipped and failed test names to the GitHub job summary.
- Nightly (`.github/workflows/ci-nightly.yml`): Stability for all four projects, Mutation, DeploySmoke.

**Deploy**
- Helm chart (Helm 3.22.1 on PATH): `./build.sh --target Helm [--environment dev]` runs
  `aspire publish -o k8s-artifacts` + `helm lint` + `helm template`. `k8s-artifacts/` is
  git-ignored because it contains generated passwords; never commit it.
- Images: `./build.sh --target Images` builds `orders-api` and `orders-payment` into local Docker
  (.NET SDK container support, no Dockerfile). CI on main adds `--push-images true` and pushes to
  `ghcr.io/sanskikljuc123/<image>:<SemVer>` with `GITHUB_TOKEN`.
- Deployment smoke (Docker + kind + kubectl + helm): `./build.sh --target DeploySmoke` installs the
  chart in a throwaway kind cluster and checks the realm Job (HTTPS-only, no users) and API /health.
