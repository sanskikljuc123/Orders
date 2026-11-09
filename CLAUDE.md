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
| Integration | tests/Orders.IntegrationTests | in-process (WebApplicationFactory) + Testcontainers | FakeItEasy via Autofac `ConfigureTestContainer` |
| E2E | tests/Orders.E2E | Aspire separate processes + Playwright | `FakePaymentService` project (real process) |
| Browser-side calls | any Playwright test | browser | `Page.RouteAsync` |

- Aspire tests run the app as separate processes: DI replacement / FakeItEasy does NOT work there.
- Testcontainers = single-service integration tests. Aspire = whole-system E2E and local dev.

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
- Deploy: `aspire publish -o <dir>` then `helm upgrade --install ... -f deploy/values.<env>.yaml`.
  Do NOT rely on `aspire deploy` for Kubernetes until it is proven stable here.
- Logging: Serilog structured logs; never log secrets/tokens.

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
- Build: `dotnet build`
- Tests: `dotnet test`
- Playwright browsers: `pwsh tests/Orders.E2E/bin/Debug/net10.0/playwright.ps1 install`
