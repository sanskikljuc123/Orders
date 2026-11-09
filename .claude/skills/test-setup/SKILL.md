---
name: test-setup
description: Set up or extend automated testing in C#/.NET projects — xUnit unit tests with FakeItEasy, integration tests with WebApplicationFactory + Testcontainers, and end-to-end tests written in Gherkin with Reqnroll + Playwright for .NET on an Aspire host — by matching the repo's existing conventions, scaffolding test projects/fixtures/hooks/CI gates, and writing a first set of trustworthy, user-centric tests. Use this whenever the user asks to "add tests", "set up testing", "write a feature file / scenario / step definitions", "add E2E or Playwright tests", "add integration tests", "test this endpoint/validator/service", "add a coverage gate" or "fix flaky tests" in a .NET codebase, even if they don't say "setup".
---

# Test setup (C# + Gherkin)

Goal: leave the repo with tests the team will *trust* — they run green, they go red when the
behaviour breaks, and a newcomer can read them as documentation. One trustworthy test beats
ten flaky ones; a suite people don't trust gets rubber-stamped, which is worse than no suite.

Work in small steps. After each step, build/run and look at the real output before moving on.

**Respect the project's phases.** If project instructions split work into phases or gates
(e.g. "unit → integration → E2E → CI, ask before the next"), they win over this skill's step
order: finish the current phase, run its gate, report, and ask before starting the next — even
if a later step below (CI, coverage gate) would normally follow.

## Step 1 — Read what is already there

Run the detector against the repo root. For a skill stored in the repo, `<skill-dir>` is
`.claude/skills/test-setup`:

```bash
python3 .claude/skills/test-setup/scripts/detect_stack.py .
```

It lists .NET projects, test packages (xUnit, FakeItEasy, Reqnroll, Playwright, Testcontainers…),
central version files, feature files, test folders and CI. Then read, in this order:

1. Project instructions (`CLAUDE.md`, `CONTRIBUTING.md`, `README`) — they override this skill.
2. The class / endpoint / page under test, and its existing tests.
3. 2–3 existing test files and one `.feature` end to end, to learn the house style: test naming,
   assertion style, fixture and fake patterns, step wording, page-object shape.
4. CI config and the build entry point (NUKE/`build.sh`), to see how tests run today.
5. Stale references: the solution, CI or build targets may still point at test projects that no
   longer exist — the build then fails before you start. Also check git history
   (`git log --diff-filter=D --stat -- tests/`) for deleted tests; ask the user whether to
   reuse them or start fresh instead of deciding silently.

**Match what exists.** If the repo uses xUnit + FakeItEasy, do not introduce NUnit + Moq. Reuse
existing steps and page objects before adding new ones. Only bring in a new package when nothing
covers the need, and say why.

Then load the references:

| Need | Read |
|---|---|
| Always — principles and anti-patterns | `references/principles.md` |
| Unit / integration / fakes / CI / reference stack | `references/dotnet.md` |
| E2E: feature files, steps, page objects, hooks, Playwright | `references/gherkin-e2e.md` |
| HTTP APIs and service-to-service contracts (Pact) | `references/api-and-contracts.md` |
| A test is flaky / "fix flaky tests" | `references/flaky-tests.md` |

## Step 2 — Identify the public contract and what would hurt

For the thing under test, note:

- **Inputs, outputs, side effects** (DB rows, messages, calls to external services), **error cases**.
- **Edge cases**: empty, null, whitespace, boundaries (min-1, min, max, max+1), unicode, very
  long input, duplicates.
- **Who uses it** and what they observe — a user in the browser, an API caller, another class.

Then ask: *"What regression would actually hurt in production?"* — a user seeing another user's
orders, a declined payment shown as paid, login broken, an order silently lost. Test those first.
Don't spend effort on trivial getters while a critical flow is untested.

## Step 3 — Choose the level for each behaviour

| Behaviour | Level | Project | External services |
|---|---|---|---|
| Pure logic, validation, calculations | **Unit** | `tests/<App>.UnitTests` | FakeItEasy |
| Endpoint + real DB / auth pipeline | **Integration** | `tests/<App>.IntegrationTests` | Testcontainers for infra; FakeItEasy via DI/Autofac for third parties |
| Real user journey through the UI | **E2E (Gherkin)** | `tests/<App>.E2E` | Fake service *process* in the Aspire AppHost; `Page.RouteAsync` for browser calls |
| Service A depends on service B's API | **Contract** | consumer + provider | PactNet |

Prefer the lowest level that catches the regression. Put the 20 validation permutations in
unit tests and one representative "invalid input shows an error" scenario in E2E.
Aspire runs services as separate processes, so DI replacement and FakeItEasy do not work in E2E.
Never call real third-party services (payment, email, SMS) from tests.

## Step 4 — Scaffold the infrastructure (only what is missing)

- Test project, added to the solution, using the repo's layout. E2E uses
  `Features/`, `Steps/`, `Pages/`, `Hooks/`.
- **Versions:** never write a package version from memory. Look it up
  (`dotnet package search <Id> --exact-match`) or reuse what's already in
  `Directory.Packages.props`. No `Version=` in .csproj files; the SDK lives only in `global.json`.
  If you can't verify a version, say so.
- **Shared fixtures** for expensive setup (`WebApplicationFactory`, containers, Aspire host,
  browser, saved Keycloak login) with automatic teardown (`IAsyncLifetime`, `[AfterTestRun]`).
- **Determinism**: inject `TimeProvider` (fake in tests), seed any `Random`, no real network in
  unit tests, unique data per test (`Guid` suffix).
- **Config-level** timeouts, `BaseURL`, and trace + screenshot + video on failure — set once in
  the hooks/test environment, not per test.
- **Fakes for external services** built from a real contract (OpenAPI spec, recorded responses).
  With neither, keep the fake minimal and mark it `UNVERIFIED ASSUMPTION` in a README. Give
  stateful fakes a reset endpoint and call it after every scenario.

## Step 5 — Write the first tests

Write a small, high-value set first (the "would hurt" list), get it green, then expand.

- **Names state the requirement**: `Declined_payment_marks_order_PaymentFailed`,
  `Scenario: Users see only their own orders`. A reader learns the rule from the name.
- **Arrange / Act / Assert** (or Given / When / Then), visibly separated; one behaviour per test.
- **Assert specific values and messages** — status code *and* body field *and* error message/
  property name — not just `IsValid == false` or "didn't throw".
- **Test behaviour, not implementation.** Assert what the user sees or the caller receives.
  `MustHaveHappened` on a fake only when that call *is* the contract (e.g. "charges 30.00").
- **Gherkin is user language**: steps describe behaviour (`When I order 2 of "Book"`), never
  clicks or selectors. Selectors live only in page objects (`GetByRole`, `GetByLabel`,
  `GetByTestId`). Explore the live UI (see `gherkin-e2e.md` §4) *before* writing the page object.
- **Parameterize** with `[Theory]`/`[InlineData]` and `Scenario Outline`/`Examples`.
- **Cover the failure path**: validation errors, 401/403/404, payment declined/error.
- **Each test owns its data** and cleans up; never depend on another test running first.
- **Tags** `@smoke` (critical journeys) and `@regression`; one feature file / test class per
  capability.

## Step 6 — Prove the tests are real

1. `dotnet build`, then `dotnet test <project>` — show the real summary (passed/failed/skipped).
   Never claim green without output.
2. **Mutation check**: for each important new test, briefly break the behaviour (flip a
   condition, change a status code, remove a validation rule), run it, confirm it goes **red**,
   restore. A test that stays green when the behaviour is broken gets fixed or deleted.
3. Run the suite twice to catch flakiness and hidden ordering dependencies.

If something can't run here (no .NET SDK, no Docker for Testcontainers/Aspire, no browser), say so
plainly, give the exact command, and don't report it as passing.

## Step 7 — Wire into CI

- Add targets to the build entry point (NUKE); CI only calls `./build.sh` / `./build.cmd`.
  Check `git diff` after the first NUKE run — it can rewrite `build/_build.csproj`; revert
  changes you didn't intend.
- Upload `.trx` results and Playwright traces, screenshots and videos with `if: always()`.
- Install Playwright browsers matching the `Microsoft.Playwright` package version.
- Add a **coverage gate** (coverlet) at the current level, branch coverage on critical code, as a
  floor that can't silently regress. Never write meaningless tests to hit a number.

## Step 8 — Self-review before saying "done"

- [ ] Each test fails if I break the behaviour it claims to cover (mutation-checked).
- [ ] Every test asserts specific expected values / messages.
- [ ] No `Thread.Sleep` / `Task.Delay` waits; Playwright auto-waiting and `Expect(...)` only.
- [ ] No real external network in unit tests; no real third-party services anywhere.
- [ ] No catch-all `try { } catch { }` that swallows failures in tests, steps or hooks.
- [ ] Edge and error cases covered, not only the happy path.
- [ ] No negative-only assertions: every "does not contain / is not visible" is paired with a
      positive check proving the data actually loaded.
- [ ] Tests are independent: pass alone and in any order; fresh browser context per scenario.
- [ ] Test data and side effects (rows, files, fake-service modes) are cleaned up.
- [ ] Names describe requirements; no dead, skipped or commented-out tests or unused steps.
- [ ] No over-mocking — real code is exercised, not just the fake's behaviour.
- [ ] Timeouts and `BaseURL` set once, not sprinkled in tests or steps.
- [ ] Steps contain no selectors; feature files contain no UI mechanics.
- [ ] Files are focused; no giant catch-all test class or feature.

## Report back

1. **Conventions detected** (one or two lines).
2. **What was added** — files, one line each.
3. **What is tested and why** — the "would hurt" list mapped to test/scenario names and levels.
4. **Run results** — real command + pass/fail counts, and the mutation-check outcome.
5. **Gaps / next steps** — untested areas, what needs Docker/CI, any `UNVERIFIED ASSUMPTION`s.
