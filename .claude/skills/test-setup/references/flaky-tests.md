# Diagnosing and fixing flaky tests

A flaky test passes and fails on the same code. Treat it as a bug in the test (or the app),
never as noise: once the team learns to re-run red builds, real failures slip through.

## 1. Reproduce

Run the single test many times; one red is enough to confirm.

```bash
dotnet build
for i in $(seq 1 30); do
  dotnet test tests/<Project> --no-build --filter "FullyQualifiedName~<TestOrScenarioName>" || { echo "failed on run $i"; break; }
done
```

- If it only fails together with other tests, run the whole class/feature in a loop — that
  points to shared state or ordering.
- E2E: run headed (`HEADED=1`) and open the trace of a failing run
  (`pwsh tests/<App>.E2E/bin/Debug/net10.0/playwright.ps1 show-trace <file>.zip`).
- Read the evidence before changing anything: trace, screenshot, video, server logs.

## 2. Find the cause

| Symptom | Usual cause | Fix |
|---|---|---|
| Fails only after another test | Shared fake/static/DB row; leftover fake-service mode | Reset fakes per test, unique data, teardown in `finally` |
| Fails only in CI / under load | Timing: a sleep, a too-short per-assertion timeout, reading before the UI settled | Auto-waiting `Expect(...)`, wait for the specific response/URL, config-level timeouts |
| "Element not found / detached" | Selector races with re-render; brittle CSS | `GetByRole`/`GetByTestId`, act on the locator (auto-waits), not a stale handle |
| Different data each run | `DateTime.Now`, unseeded `Random`, `Guid` used in assertions, unordered query results | `TimeProvider`, seeds, assert on own data, `OrderBy` in the query under test |
| Fails at startup sometimes | Service not ready; polling with fixed delays | Wait for Aspire resource health; health checks on resources |
| Count assertions drift | Other tests' data in the same DB | `Contains`/`DoesNotContain` on own data, or reset DB between tests |
| Real external call | Network/third-party flakiness | Fake it (FakeItEasy, fake service, `Page.RouteAsync`) |

## 3. Fix, then prove it

- Fix the cause, not the symptom. Adding `Thread.Sleep`, raising one assertion's timeout, or
  wrapping in try/catch are not fixes.
- Re-run the reproduction loop: it must pass every run.
- Run the mutation check again: the test must still go red when the behaviour breaks.

## 4. Policy

- **Retries**: allowed only in CI, at most one; a test that passes only on retry is still
  reported as flaky and gets a ticket.
- **Quarantine** only with a ticket and an owner, and visibly: `[Fact(Skip = "FLAKY: <ticket>")]`
  or the `@ignore` tag in Gherkin with the ticket in a comment. Never silently delete or
  comment out. Review quarantined tests regularly — a quarantine is a deadline, not a home.
