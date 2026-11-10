# Project: Orders E2E / test platform (learning project)

Small Orders app (API + web UI, Keycloak, Postgres, fake payment service) used to learn a full
test platform. How to run it: [`README.md`](README.md). Which test covers which risk:
[`TESTING.md`](TESTING.md). Prefer small steps over big dumps.

## Golden rules
1. **Run before you trust.** Never claim something works until you ran `dotnet build` and the
   relevant tests and saw them pass. Show the real output summary.
2. **Do not invent.** Never invent package versions, APIs, fields or external-service behaviour.
   Check NuGet / official docs. If unsure, say so and ask.
3. **Fakes come from contracts.** An external-service fake is built ONLY from an attached
   OpenAPI spec or recorded real responses. No spec/recordings -> keep the fake minimal and
   write "UNVERIFIED ASSUMPTION" in its README.
4. **One step at a time.** Finish a step, run its gate, commit, then ask before starting the next.

## Definition of done
- [ ] `dotnet build` green; the affected test projects green, real summary shown.
- [ ] New behaviour has a test that goes red when the behaviour is broken (mutation-checked).
- [ ] `TESTING.md` row updated; [`docs/how-it-works.md`](docs/how-it-works.md) updated if behaviour changed.
- [ ] New or changed API endpoint: its row in the `TESTING.md` endpoint matrix is filled in, its
      responses are declared with `.Produces(...)` and `docs/api/openapi.v1.json` is updated
      (`EndpointAuthTests` already fails for any endpoint without a login unless it is listed as public).
- [ ] Committed (no AI attribution), then asked before pushing.

## Stack
.NET 10 (SDK in `global.json`) | .NET Aspire | xUnit | Reqnroll (Gherkin) + Microsoft.Playwright |
FakeItEasy | Testcontainers | Keycloak | Autofac | FusionCache | FluentValidation | Serilog |
GitVersion | NUKE | Stryker.NET | Helm + Kubernetes (via `aspire publish` + `helm`)

## Versions
- All NuGet versions ONLY in `Directory.Packages.props`. No `Version=` in .csproj files.
- .NET SDK ONLY in `global.json`. Local tools in `dotnet-tools.json` (Stryker: `stryker/.config`).
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

## QA agents (`.claude/agents/`)
One responsibility each; they analyse and report, they never edit files, commit or push.
- `requirement-analysis` (read-only): requirement -> business rules, validations, security,
  permissions, integration points, negative and boundary cases, risks, a lean test plan, open questions.
- `coverage-review` (may run tests): "would a regression make a test fail?" -> uncovered
  requirements, missing scenarios, weak/redundant/flaky tests, automation opportunities, regression suites.
- `failure-analysis` (may run tests, read CI logs): classification, root cause with evidence,
  reproduction, bug report.
- `pr-review` (may run tests): before a PR, what CI cannot judge: tests for every changed
  behaviour, nothing weakened, docs/contract in sync, no secrets or attribution, an honest
  description -> READY / NOT READY. Used by the `pr-ready` skill (`/pr-ready`).

**Workflow:** new or changed behaviour -> `requirement-analysis` -> write the tests (`test-setup`
skill) -> `coverage-review` on the diff -> `/pr-ready` (gates, `pr-review`, then push after an OK)
-> CI or nightly red -> `failure-analysis`.

**Shared rules (all three agents apply these; this is the only copy):**
- Prioritise by 1. user impact, 2. business impact (money, lost orders), 3. security impact,
  4. technical complexity.
- Risk: **HIGH** (money, privacy, security, data loss), **MEDIUM** (broken feature with a
  workaround), **LOW** (cosmetic, internal), always with the reason.
- Confidence for every finding: **high** (reproduced or shown in code/logs), **medium** (strong
  evidence, not reproduced), **low** (plausible, unverified). Never present low confidence as fact.
- Never propose unnecessary tests: each names the regression it catches and its level (lowest that
  catches it: unit < contract < integration/API < E2E, API before UI); say which were rejected and why.
- Never recommend skipping, quarantining, loosening or deleting a test to get green.
- Do not invent behaviour of Keycloak, the payment provider (an UNVERIFIED ASSUMPTION) or the UI;
  cite `path:line` or log lines for every claim; unknowns are open questions.
- Explain the reasoning behind each decision, briefly.
- Known-answer evals: `.claude/agents/evals/` (re-run after changing an agent).

## Gherkin / Playwright conventions
- Features in `tests/Orders.E2E/Features`, steps in `Steps`, page objects in `Pages`, hooks in `Hooks`.
- Steps describe behaviour, not clicks. Selectors live ONLY in page objects.
- Selectors: `GetByRole`, `GetByLabel`, `GetByTestId`. No brittle CSS/XPath.
- No `Thread.Sleep`; rely on Playwright auto-waiting and `Expect(...)`.
- Each scenario isolated: fresh browser context. Tags: `@smoke`, `@regression`.
- Keycloak login: log in once, save Playwright storage state, reuse it.
- On failure keep trace + screenshot + video as CI artifacts.
- Explore the live UI with `playwright-cli` (`playwright-cli install --skills`) BEFORE writing
  selectors. Without it (cloud container): read `src/Orders.Api/wwwroot/index.html` and treat
  selectors as unverified until a green run and a mutation check confirm them; say so.

## Git
- Commit messages and PR descriptions: no AI attribution. No `Co-Authored-By: Claude ...`,
  no `Claude-Session: ...` links, no "Generated with Claude Code" lines.
- Ask before pushing. This rule wins over the cloud environment's stop hook that asks to push
  unpushed commits.
- Enforced, not only written down: `.claude/settings.json` turns Claude Code's attribution off and
  a PreToolUse hook (`.claude/hooks/block-ai-attribution.py`) refuses such commits; the NUKE target
  `Conventions` (part of `Test`, so every CI run) fails on attribution in any commit after
  `33a83da`. Three commits from 2026-11-09, before the rule, keep theirs (history not rewritten).

## Build / CI / deploy
- NUKE (`build/`) is the single entry point; GitHub Actions only calls `./build.sh` / `./build.cmd`.
- Version comes from GitVersion into NUKE.
- Deploy: `aspire publish -o <dir>` then `helm upgrade --install ... -f deploy/values.<env>.yaml`.
  Do NOT rely on `aspire deploy` for Kubernetes until it is proven stable here.
- Logging: Serilog structured logs; never log secrets/tokens. Checked by `LogSecretsTests`
  (captures every event via a DI sink, `ReadFrom.Services`, and fails on any JWT, test password or
  `Idempotency-Key` value).
- Keycloak: the test realm (`src/Orders.AppHost/Realms`, users ana/bob with known passwords) is
  imported only for local runs and tests, never in `aspire publish`. A deployment needs its own
  realm: no test users, exact redirect URIs, HTTPS required, brute-force protection on.

## Reference (read when the task needs it)
- [`docs/how-it-works.md`](docs/how-it-works.md): current behaviour and design (realm, login, orders,
  payment, startup, security gates, API contract) and open points. Read before changing behaviour;
  update it when behaviour changes.
- [`docs/commands.md`](docs/commands.md): every test layer, quality gate (coverage, mutation,
  stability, conventions) and deploy command.
- [`docs/pitfalls.md`](docs/pitfalls.md): known pitfalls and cloud-container limits. Read when a
  build, test or CI run fails.
- Essentials: build `dotnet build`; tests `dotnet test tests/Orders.<Layer>Tests` (integration and
  E2E need Docker); everything a PR runs: `./build.sh --target Test Helm Images`; prepare a PR: `/pr-ready`.
