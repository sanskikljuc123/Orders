# Testing principles and anti-patterns (C# + Gherkin)

The reasoning behind the workflow in SKILL.md.

## Contents
1. Mindset
2. Designing a test
3. Isolation and determinism
4. Assertions
5. Coverage
6. Structure and readability
7. Anti-patterns

## 1. Mindset

- **Reliability over quantity.** One trustworthy test beats ten flaky ones. A suite the team
  doesn't trust is worse than none: red builds get rubber-stamped and real failures slip through.
- **User-centric.** Write tests from the perspective of whoever consumes the code — a user in the
  browser, an API caller, another class. Scenarios mirror real user journeys.
- **Risk first.** Ask "what regression would actually hurt in production?" and test that first.
- **Tests are documentation.** A new team member should understand the rule from the test or
  scenario name and body alone.

## 2. Designing a test

- Read the code under test and its existing tests first. Match the repo's naming, assertion
  style, fixture patterns and folder layout.
- Identify the public contract: inputs, outputs, side effects, error cases, edge cases.
- Pick the level: pure logic → unit; class + collaborators (DB, HTTP pipeline) → integration;
  real user journey through the UI → E2E (Gherkin); cross-service API shape → contract (Pact).
- **Test behaviour, not implementation.** Assert what the user sees or the caller receives, not
  private fields, internal call order or class structure. Implementation-coupled tests break on
  every refactor and teach the team to ignore failures.
- **Don't over-mock.** If a test mostly asserts that a fake returned what you told it to, it tests
  nothing. Fake at the boundary (payment client, clock, external HTTP); keep real code in between.
- Mock external APIs and payment gateways; never hit third-party services from tests.

## 3. Isolation and determinism

- Each test sets up and tears down its own data; never rely on another test running first.
- No shared mutable state between tests (static fields, a shared DB row tests modify). Shared
  *expensive* setup (container, Aspire host, browser, saved login) is fine via fixtures/hooks.
- Clean up side effects — DB rows, files, a fake service's mode — in `DisposeAsync` /
  `[AfterScenario]` inside `finally`, so it runs even when an assertion fails.
- Unique data per test (`$"Book-{Guid.NewGuid():N}"`) avoids collisions in a shared database;
  assert `Contains`/`DoesNotContain` on your own data, not global counts.
- **Deterministic by default:** no real network in unit tests, `TimeProvider` instead of
  `DateTime.Now`, seeded `Random`, no ordering dependencies. Same input, same result, every run.
- xUnit creates a new class instance per test; keep it that way (no static state). If
  parallelism is disabled (shared browser/app host), say why next to the setting — and keep tests
  isolated anyway so it can be enabled later.

## 4. Assertions

- Assert specific values and error messages, not just truthy / not-null / "no throw":
  `Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode)` *and* the failing property/message.
- `Assert.Throws<SpecificException>` / `ThrowsAsync`, then assert on its message — never
  `catch { }`.
- For ambiguous checks (loops, theory rows), make the failure self-explaining: put case values in
  `[InlineData]`/`Examples` so the report names the case.
- UI: web-first `Assertions.Expect(locator).ToHaveTextAsync("Order Paid")` — retries until the
  timeout, so no sleeping first.

## 5. Coverage

- Coverage is a floor, not a goal. 100% line coverage with weak assertions is theater.
- Prefer branch coverage on critical code (payments, auth, ownership checks, pricing).
- Add a coverage gate in CI at the current level so it can't silently regress; ratchet it up as
  real tests are added. Never write meaningless tests to hit the number.
- Edge cases: empty, null, whitespace, boundary values (min-1, min, max, max+1), unicode/emoji,
  very long input, duplicates, and the failure/error path.

## 6. Structure and readability

- One test class per unit/endpoint group; one `.feature` per capability.
- Constructor / `Background` for common setup, but minimal — if a reader must scroll to find a
  test's preconditions, move them into the test.
- Share setup and authenticated state with fixtures (`IClassFixture`, `ICollectionFixture`,
  Reqnroll context injection, cached storage state), not copy-paste.
- Parameterize instead of duplicating: `[Theory]` + `[InlineData]`, `Scenario Outline` + `Examples`.
- Tag scenarios `@smoke` / `@regression` for selective runs.
- No dead, skipped or commented-out tests; no unused step definitions.

## 7. Anti-patterns (and the fix)

| Anti-pattern | Why it hurts | Instead |
|---|---|---|
| `Thread.Sleep`, `Task.Delay` as a wait | Flaky and slow | Auto-waiting, `Expect(...)`, `WaitForResponseAsync` |
| Shared mutable state between tests | Order-dependent, breaks in parallel | Each test creates its own data |
| Testing implementation details | Breaks on every refactor | Assert observable behaviour |
| Giant test classes / feature files | Hard to navigate, merge conflicts | One per capability |
| Ignoring isolation | Fails when order changes | Fresh browser context, unique data, teardown |
| Absolute URLs in steps/pages | Can't retarget environments | `BaseURL` on the context, relative `GotoAsync` |
| Selectors in step definitions | UI change breaks many steps | Selectors only in page objects |
| Imperative Gherkin ("click Submit") | Brittle, unreadable for business | Declarative behaviour steps |
| Weak assertions (`IsValid == false` only) | Passes for the wrong reason | Assert which rule/message failed |
| Calling real third-party services | Flaky, costly, side effects | FakeItEasy / fake service / `RouteAsync` |
| Not cleaning up | Polluted DB, follow-on failures | Teardown in `finally` / fixtures |
| Catch-all `try { } catch { }` | Hides real failures | Catch the specific exception and assert |
| Per-assertion timeouts everywhere | Masks slowness, inconsistent | Config-level timeouts |
