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
- [ ] `TESTING.md` row updated; "How it works now" below updated if behaviour changed.
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

**Workflow:** new or changed behaviour -> `requirement-analysis` -> write the tests (`test-setup`
skill) -> `coverage-review` on the diff before the PR -> CI or nightly red -> `failure-analysis`.

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
  tests); guarded by `AuthSecurityOptionsTests`. Bearer tokens must carry the audience
  `orders-api` (audience mapper on `orders-web` in both realms); tokens for other clients of the
  realm are refused (CA5404 found this). Logout ends the app cookie and the Keycloak
  session. Sessions are stored server-side (`SessionStore`), so logout revokes the cookie.
- **Orders:** idempotent with an `Idempotency-Key` (the UI sends one per order attempt); a repeated
  key returns the first order (`Idempotent-Replayed: true`) without charging again; the same key
  with a different product or quantity is rejected with 422 (IETF Idempotency-Key draft), nothing
  charged. Same-key requests are serialized in-process; a unique (UserId, IdempotencyKey) index is
  the backstop. Responses never contain `userId` (Keycloak's internal subject) or the key.
- **Payment:** contract and client in `src/Orders.Shared/Payments.cs` (shared with the fake).
  Anything but a clear approval (HTTP error, unreadable body, unreachable, timeout after
  `Payment:TimeoutSeconds`, default 10) means not paid and is logged as a warning.
- **Validation messages** are explicit English text, independent of the server culture.
- **Security gates:** every restore fails on a High/Critical NuGet advisory, direct or transitive
  (`Directory.Build.props`, NuGet audit); every build fails on a .NET security analyzer finding
  (`AnalysisModeSecurity=All`, analyzer warnings are errors; suppress only in code with a reason).
  The `Images` target scans each image with Trivy (pinned in `build/Build.cs`, checksum-verified)
  before any push; fixable HIGH/CRITICAL fail it. Images use the chiseled .NET base
  (`ContainerFamily=noble-chiseled`: no apt, no shell, non-root user 1654, port 8080); the full
  Ubuntu base shipped apt with a fixable HIGH CVE that the scan refused on its first run. Dependabot (`.github/dependabot.yml`) opens weekly
  update PRs. No CodeQL: it needs a paid licence for private repositories.
- **API contract:** `docs/api/openapi.v1.json` (OpenAPI 3.1, generated by `Microsoft.AspNetCore.OpenApi`
  from the endpoints' `.Produces(...)` declarations). Served at `/openapi/v1.json` in Development
  only. `OpenApiContractTests` fails when the API's document differs from the committed one;
  `/login` and `/logout` are browser redirects and excluded from it.

## Commands
Run the app and the manual smoke check: [`README.md`](README.md).

**Tests** (what each needs)
- Unit: `dotnet test tests/Orders.UnitTests` (nothing) | Contract: `dotnet test tests/Orders.ContractTests` (nothing)
- Integration: `dotnet test tests/Orders.IntegrationTests` (Docker)
- E2E: `dotnet test tests/Orders.E2E` (Docker + Playwright browser:
  `pwsh tests/Orders.E2E/bin/Debug/net10.0/playwright.ps1 install`); `--filter Category=smoke` for smoke only
- `dotnet test` alone runs all four, so it needs Docker and the browser too.
- Everything CI runs on a pull request: `./build.sh --target Test Helm Images` (Linux), plus
  `.\build.cmd --target UnitTests ContractTests` on Windows (job `windows`).
- API contract changed on purpose: `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/Orders.IntegrationTests
  --filter FullyQualifiedName~OpenApiContractTests`, review and commit `docs/api/openapi.v1.json`.
- Test order is random in all four projects (`tests/TestSupport/RandomTestOrder.cs`; E2E scenarios
  still run one at a time, see `tests/Orders.E2E/AssemblyInfo.cs`). The
  seed is printed ("Random test order seed: N"); replay a failing order with `TEST_ORDER_SEED=N`.

**Quality gates**
- Coverage: `CoverageGate` merges the unit + contract + integration reports (generated migrations
  excluded via `coverlet.runsettings`): all production code >= 90% branches, each critical class
  (`CriticalClasses` in `build/Build.cs`) >= 80% lines and branches. Summary in
  `TestResults/coverage-summary.md` and the GitHub job summary. Raise floors with real tests
  (`--total-branch-floor`, `--critical-branch-floor`); never write tests just to hit them.
- Mutation: `./build.sh --target Mutation` runs `stryker/api.json` (OrderService, SessionStore,
  CreateOrderValidator; break below 85%) and `stryker/shared.json` (payment client; break below
  90%). Reports in `TestResults/mutation/*/reports/`. Known survivors (SessionStore expiry boundary
  and cache duration) are not observable: the cookie handler checks expiry itself.
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
  (.NET SDK container support, no Dockerfile) and scans them with Trivy (Linux x64; elsewhere, or
  with `--scan-images false`, it warns and skips, and `--push-images` then refuses). CI on main adds `--push-images true` and pushes to
  `ghcr.io/sanskikljuc123/<image>:<SemVer>` with `GITHUB_TOKEN`. Test the push path with
  `./build.sh --target Images --push-images true --image-registry localhost:5000` and a local `registry:2`.
- Deployment smoke (Docker + kind + kubectl + helm): `./build.sh --target DeploySmoke` installs the
  chart in a throwaway kind cluster and checks the realm Job (HTTPS-only, no users) and API /health.

## Known pitfalls
- **NUKE quoting:** never interpolate a value that contains spaces (e.g. `$" --flag"`) into
  `DotNet($"...")` / tool calls; NUKE quotes it as ONE argument. Write the full command per case.
  (Broke `InstallBrowser` and the ghcr.io push.)
- **Tool restore and the package cache:** a cached package hides restore failures. Before trusting
  a `dotnet-tools.json` change, delete `~/.nuget/packages/<tool>` and restore again. (dotnet-stryker
  next to aspire.cli failed only on clean machines: "package contains aspire".)
- **Stryker** silently exits with code 1 unless break <= low <= high.
- **Coverage gate dropped** after new code: add the missing test, never lower the floor.
- **Chart without passwords:** the generated chart's secrets are empty; Keycloak then exits
  (code 2) and the realm Job times out. `deploy/chart-extras/templates/required-secrets.yaml` now
  stops any install/render without them (or with different Postgres/API passwords); the Helm
  target checks that guard. DeploySmoke and Helm generate throwaway ones; a real deploy passes
  them with `--set`, see `deploy/values.dev.yaml`.
- **OpenAPI and record defaults:** an optional constructor parameter like `DateTimeOffset CreatedAt
  = default` makes the OpenAPI document fail with a 500 ("could not be converted to
  System.DateTimeOffset"). Records used in responses take all parameters explicitly.
- **Analyzer severity:** `AnalysisModeSecurity=All` sets each rule's severity itself, so an
  `.editorconfig` category severity does not raise it, and a category severity alone does not turn
  on rules that are off by default. Use `CodeAnalysisTreatWarningsAsErrors` (checked with MD5, a
  disabled audience check and `new Random()`: all three fail the build).
- **First NUKE run** can rewrite `build/_build.csproj`; check `git diff` and revert what you didn't intend.

## Cloud dev container (limitations)
- Docker: after a container restart, delete a stale `/var/run/docker.pid` before starting dockerd.
  If dockerd then fails with "timeout waiting for containerd to start", kill the leftover
  `containerd` and remove `/var/run/docker/containerd/containerd.pid` first.
- Downloaded tool binaries (e.g. Trivy) may not be run here; the image scan is proven by CI.
  Docker Hub can rate-limit (429) and ghcr.io blob downloads are blocked.
- No nested Kubernetes: `DeploySmoke` runs only in CI (nightly).
- Plain `dotnet run --project src/Orders.AppHost` aborts (exit 134); use `dotnet aspire run`.
- Playwright download blocked: `E2E_CHROMIUM_PATH=<installed chrome>` and `E2E_VIDEO=0` (local-only;
  CI installs the matching browser). No `playwright-cli` (see Gherkin conventions).

## Open points and known limitations
- PLACEHOLDER URLs to replace before a real deployment: `https://orders.example.com` (prod realm
  redirect/logout URIs), `https://auth.example.com` (`Auth__Authority`, `keycloak_hostname` in
  `deploy/values.dev.yaml`). Only `values.dev.yaml` exists; image tags are passed with `--set`.
- GHCR packages are private by default: make them public or give the cluster a pull secret.
- One API replica only: sessions and idempotency locks are in-memory (a restart logs users out).
  A distributed L2 cache would lift this.
- The chart installs and starts in a throwaway kind cluster (`DeploySmoke`, nightly, green since
  2026-11-10) but has never run in a real cluster with TLS ingress, real domains and certificates.
- The payment provider contract is an UNVERIFIED ASSUMPTION (`src/FakePaymentService/README.md`).
