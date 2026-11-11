# Known pitfalls

Problems this project has already hit, and the cloud dev container's limits. Check here before inventing a new cause for a failure.

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
- No .NET SDK preinstalled and `builds.dotnet.microsoft.com` blocked (dotnet-install.sh fails):
  `apt-get install -y dotnet-sdk-10.0` (Ubuntu's package, same 10.0.1xx band as `global.json`).
- No nested Kubernetes: `DeploySmoke` runs only in CI (nightly).
- Plain `dotnet run --project src/Orders.AppHost` aborts (exit 134); use `dotnet aspire run`.
- Playwright download blocked: `E2E_CHROMIUM_PATH=<installed chrome>` and `E2E_VIDEO=0` (local-only;
  CI installs the matching browser). No `playwright-cli` (see Gherkin conventions).
