# Commands

How to run every test layer, quality gate and deployment check. Running the app: [`README.md`](../README.md).


**Tests** (what each needs)
- Unit: `dotnet test tests/Orders.UnitTests` (nothing) | Contract: `dotnet test tests/Orders.ContractTests` (nothing)
- Integration: `dotnet test tests/Orders.IntegrationTests` (Docker)
- E2E: `dotnet test tests/Orders.E2E` (Docker + Playwright browser:
  `pwsh tests/Orders.E2E/bin/Debug/net10.0/playwright.ps1 install`); `--filter Category=smoke` for smoke only
- `dotnet test` alone runs all four, so it needs Docker and the browser too.
- `./build.sh --target Conventions` (also part of `Test`): every test name in `TESTING.md` exists
  (names with `_` or ending in `Tests`; a trailing `…`/`*` is a prefix; E2E scenario titles are not
  checked), and no commit since `33a83da` carries AI attribution.
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
  CreateOrderValidator, UserIds against the unit tests; break below 85%), `stryker/shared.json`
  (payment client; break below 90%) and `stryker/api-integration.json` (OrderEndpoints,
  EfOrderRepository against the integration tests; break below 85%; needs Docker, about 20 min).
  Reports in `TestResults/mutation/*/reports/`. Known survivors: SessionStore expiry boundary and
  cache duration (not observable: the cookie handler checks expiry itself) and the 503 problem
  `title` (nothing reads it; the `detail` is asserted).
  `api-integration` uses `"coverage-analysis": "off"`: endpoints are mapped while the shared test
  fixture starts, outside any test, so per-test coverage reported those mutants as survivors even
  though the tests catch them. Code to mutate goes in its own file: Stryker's `{a..b}` spans are
  character offsets, not lines.
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
