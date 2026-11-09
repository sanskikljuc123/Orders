# Testing principles and anti-patterns

The reasoning behind the workflow in SKILL.md. Apply them with the idioms of whatever stack the
repo uses.

## Contents
1. Mindset
2. Designing a test
3. Isolation and determinism
4. Assertions
5. Coverage
6. Structure and readability
7. E2E specifics
8. Anti-patterns

## 1. Mindset

- **Reliability over quantity.** One trustworthy test beats ten flaky ones. A suite the team
  doesn't trust is worse than none: red builds get rubber-stamped and real failures slip through.
- **User-centric.** Write tests from the perspective of whoever consumes the code — an end user
  in the UI, an API caller, another module. Tests should mirror real journeys and real calls.
- **Risk first.** Ask "what regression would actually hurt in production?" and test that first.
- **Tests are documentation.** A new team member should understand the rule from the test name
  and body alone.

## 2. Designing a test

- Read the code under test and its existing tests before writing anything. Match the repo's
  framework, naming, assertion style and folder layout.
- Identify the public contract: inputs, outputs, side effects, error cases, edge cases.
- Pick the level: pure logic → unit; module + collaborators (DB, HTTP) → integration; real user
  journey through the UI → E2E; cross-service API shape → contract test.
- Test behaviour, not implementation. Assert on what the user observes or the caller receives,
  not on private fields, internal call order or class structure. Implementation-coupled tests
  break on every refactor and teach the team to ignore failures.
- Don't over-mock. If the test mostly asserts that a mock returned what you told it to, it tests
  nothing. Mock at the boundary (network, clock, third-party SDK), keep real code in between.
- Mock external APIs and payment gateways; never hit third-party services from tests.

## 3. Isolation and determinism

- Each test sets up and tears down its own data; never rely on another test running first.
- No shared mutable state between tests (static fields, module-level variables, a shared DB row
  that tests modify). Shared *read-only* or *expensive* setup (a container, an app host, a saved
  login) is fine via fixtures.
- Clean up side effects: DB records, uploaded files, temp dirs, queues, a fake service's mode.
  Prefer fixtures with automatic teardown (`afterEach`, `DisposeAsync`, `yield` fixtures) over
  manual cleanup at the end of a test body, which is skipped when an assertion fails.
- Unique data per test (GUID/uuid suffix) avoids collisions when tests share a database.
- Deterministic by default: no real network in unit tests, no real clock (inject/freeze time),
  no random data without a fixed seed, no ordering dependencies. Same input, same result.
- Run in randomized order and in parallel (where the runner supports it) to expose hidden
  coupling. If parallelism must be disabled (shared browser, shared external process), say why
  in a comment next to the setting.

## 4. Assertions

- Assert specific values and error messages, not just truthy / not-null / "no throw".
  `status == 400` *and* `errors.quantity == "Quantity must be between 1 and 100"`.
- Add an assertion message when the failure would otherwise be ambiguous (e.g. inside a loop or
  parameterized case: include the case values).
- Soft assertions for non-blocking checks (several independent fields on one screen) so one
  run reports all mismatches — but keep a hard assertion on the thing the test is about.
- Wait with assertions, not with time: web-first/retrying assertions (`expect(locator).toHaveText`)
  instead of sleeping and then reading.

## 5. Coverage

- Coverage is a floor, not a goal. 100% line coverage with weak assertions is theater.
- Prefer branch coverage on critical modules (payments, auth, permissions, pricing).
- Add a coverage gate in CI at the current level so it can't silently regress; ratchet it up as
  real tests are added. Never write meaningless tests just to hit the number.
- Edge cases: empty, null/None, boundary values (min-1, min, max, max+1), unicode/emoji, very
  large input, duplicates, and the failure/error path.

## 6. Structure and readability

- Group related tests: one `describe` / test class per feature or unit.
- `beforeEach` / constructor for common setup, but keep it minimal — if a reader has to scroll to
  understand a test's preconditions, move that precondition into the test.
- Share setup and authenticated state with custom fixtures, not copy-paste or global variables.
- Parameterize instead of duplicating (`[Theory]`, `test.each`, `@pytest.mark.parametrize`, a
  `for` over a case table in Playwright) — and put the case values in the test name.
- Tag tests for selective runs: `@smoke`, `@critical`, `@regression`.
- Keep files focused on one feature/page/module. No dead, skipped or commented-out tests.
- Never swallow failures: no catch-all `try/catch {}` or `except: pass` in tests or helpers. Catch
  only the specific exception you expect, and assert on it (`assertThrows`, `pytest.raises`).

## 7. E2E specifics

- Steps and test names describe behaviour ("places an order"), not clicks ("clicks #submit").
- Selectors live only in page objects. Prefer `getByRole`, `getByLabel`, `getByTestId`; avoid
  brittle CSS/XPath. (Third-party pages you don't own — e.g. an identity provider's login form —
  may need IDs; isolate them in one page-object method with a comment.)
- Configure `baseURL` and use relative paths in `goto`.
- Fresh browser context per test; log in once and reuse saved storage state.
- Never `page.waitForTimeout()` / `Thread.Sleep` — rely on auto-waiting and explicit event waits
  (`waitForResponse`, `waitForURL`, web-first assertions).
- Timeouts, retries, workers, trace/screenshot/video settings belong in config, not in tests.
- Keep traces, screenshots and videos on failure and upload them as CI artifacts. Debug with the
  trace viewer (`npx playwright show-trace trace.zip` / `pwsh playwright.ps1 show-trace trace.zip`).
- Browser-side calls to third parties: route/mock them in the browser (`page.route`).
  Server-side calls in an out-of-process system: replace the dependency with a fake service.

## 8. Anti-patterns (and the fix)

| Anti-pattern | Why it hurts | Instead |
|---|---|---|
| Hardcoded waits (`waitForTimeout(3000)`, `Thread.Sleep`) | Flaky and slow | Auto-waiting, event waits, retrying assertions |
| Shared mutable state between tests | Order-dependent, breaks in parallel | Each test creates its own data |
| Testing implementation details | Breaks on every refactor | Assert observable behaviour |
| Giant test files | Hard to navigate, merge conflicts | One file per feature/page |
| Ignoring isolation | Fails in parallel / random order | Fresh context, unique data, teardown |
| No baseURL, absolute URLs in tests | Can't retarget environments | Configure baseURL, relative `goto` |
| Ambiguous assertions with no message | Failures are hard to diagnose | Specific values + context message |
| Calling real third-party services | Flaky, costly, side effects | Fakes / mocks / routed requests |
| Not cleaning up | Polluted DB, disk, follow-on failures | Fixtures with automatic teardown |
| Catch-all `try/except: pass` | Hides real failures | Catch specific exceptions and assert |
| Per-test timeouts everywhere | Masks slowness, inconsistent | Config-level timeouts |
| Weak assertions (`IsValid == false` only) | Passes for the wrong reason | Also assert *which* rule failed |
