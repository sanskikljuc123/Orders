---
name: test-setup
description: Set up or extend automated testing in any codebase — unit, integration, API, contract and end-to-end (Playwright) — by detecting the stack, matching the repo's existing conventions, scaffolding test projects/config/fixtures/CI gates, and writing a first set of trustworthy, user-centric tests. Use this whenever the user asks to "add tests", "set up testing", "write E2E/Playwright tests", "add integration tests", "test this endpoint/component/module", "add a coverage gate", "make tests less flaky", or bootstraps a test project — in any language (.NET/xUnit, JS/TS with Playwright/Vitest/Jest, Python/pytest, and others), even if they don't say "setup".
---

# Test setup

Goal: leave the repo with tests the team will *trust* — they run green, they go red when the
behaviour breaks, and a newcomer can read them as documentation. One trustworthy test beats
ten flaky ones; a suite people don't trust gets rubber-stamped, which is worse than no suite.

Work in small steps. After each step, run something and look at the real output before moving on.

## Step 1 — Detect the stack and read what is already there

Run the detector from the skill directory against the project root:

```bash
python3 <skill-dir>/scripts/detect_stack.py <project-root>
```

It lists languages, package manifests, test frameworks, mocking libs, E2E tools, CI files and
existing test folders, based only on files it finds. Then read, in this order:

1. Project instructions (`CLAUDE.md`, `CONTRIBUTING.md`, `README`) — they override this skill.
2. The module / route / component under test, and its existing tests.
3. 2–3 existing test files end to end, to learn the house style: framework, file naming,
   test naming, assertion style, folder layout, fixture/helper patterns, how fakes are built.
4. CI config, to see how tests are run today.

**Match what exists.** If the repo uses xUnit + FakeItEasy, do not introduce NUnit + Moq. If
tests live in `__tests__/`, put new ones there. Only bring in a new tool when nothing covers the
need, and say why.

Then load the matching reference file(s):

| Stack | Read |
|---|---|
| .NET (xUnit / NUnit / MSTest, WebApplicationFactory, Testcontainers, Aspire, Playwright .NET, Reqnroll) | `references/dotnet.md` |
| JS/TS E2E with Playwright Test | `references/playwright-ts.md` |
| JS/TS unit/integration (Vitest, Jest, Supertest, MSW) | `references/javascript.md` |
| Python (pytest, httpx/requests, testcontainers-python, playwright-python) | `references/python.md` |
| HTTP APIs and service-to-service contracts (any stack) | `references/api-and-contracts.md` |

Always read `references/principles.md` — it holds the full best-practice and anti-pattern list
this skill is built on. For another stack, apply the principles with that ecosystem's
idiomatic tools.

## Step 2 — Identify the public contract and what would hurt

For the thing under test, write down (briefly, in your head or a scratch note):

- **Inputs, outputs, side effects** (DB writes, messages, emails, files), **error cases**.
- **Edge cases**: empty, null/None/undefined, boundary values (min, max, max+1), unicode,
  large input, concurrency/duplicates where relevant.
- **Who uses it** and what they observe — a user in the UI, an API caller, another module.

Then ask: *"What regression would actually hurt in production?"* — money charged twice, a user
seeing another user's data, an order silently lost, login broken. Test those first. Do not
spend effort on trivial getters while a critical flow is untested.

## Step 3 — Choose the level for each behaviour

| Behaviour | Level | External collaborators |
|---|---|---|
| Pure logic, validation, calculations | **Unit** | Fake/mocked at the boundary |
| Module + real DB / HTTP pipeline / queue | **Integration** | Real infra in containers (Testcontainers); third-party services faked |
| Real user journey through the UI | **E2E** | Whole system running; third-party services replaced by fake *processes* or network routing |
| Service A depends on service B's API shape | **Contract** (Pact) | Consumer expectations verified against provider |

Prefer the lowest level that can catch the regression. E2E is for journeys, not for every
validation rule — put the 20 validation permutations in unit tests and one representative
"invalid input shows an error" in E2E.

Mocking depends on the level: in-process tests can swap DI registrations; out-of-process tests
(E2E, Aspire, docker-compose) cannot — there, fakes must be real services or routed network
calls. Never call real third-party services (payment gateways, email, SMS) from tests.

## Step 4 — Scaffold the infrastructure (only what is missing)

Create the smallest setup that lets the first test run:

- Test project/package, config file and runner script matching the repo's layout.
- **Versions:** never write a package version from memory. Look it up from the registry
  (`npm view <pkg> version`, `dotnet package search <pkg> --exact-match`, `pip index versions <pkg>`)
  or copy from the repo's existing lock/props file, and respect central version files
  (`Directory.Packages.props`, workspace `package.json`, `pyproject.toml`). If you can't verify
  a version, say so.
- **Shared fixtures** for expensive setup (app factory, containers, authenticated sessions) with
  automatic teardown. Log in once, save the auth state, reuse it.
- **Determinism**: inject or freeze the clock, seed any randomness, no real network in unit
  tests, unique data per test (e.g. GUID suffix) so tests don't collide.
- **Config-level** timeouts, base URL, retries (CI only), trace/screenshot/video on failure.
- **Randomized order** where the runner supports it, to expose hidden coupling.
- **Fakes for external services** built from a real contract (OpenAPI spec, recorded
  responses). With neither, keep the fake minimal and mark it `UNVERIFIED ASSUMPTION` in a
  README next to it. Give stateful fakes a reset hook and call it in teardown.

## Step 5 — Write the first tests

Write a small, high-value set first (the "would hurt" list), get it green, then expand.

- **Name tests after the requirement**: `Declined_payment_marks_order_PaymentFailed`,
  `'user cannot see another user’s orders'`. A reader should learn the rule from the name.
- **Arrange / Act / Assert**, visibly separated. One behaviour per test.
- **Assert specific values and messages** — the status code *and* the body field *and* the
  error message — not just "truthy" or "didn't throw".
- **Test behaviour, not implementation.** Assert what the user sees or the caller receives.
  Verifying an interaction with a fake is fine when that interaction *is* the contract (e.g.
  "charges 30.00"), not as a substitute for checking the outcome.
- **User-centric E2E**: steps and test names describe what the user does and sees
  ("places an order", "sees Order Paid"), never clicks or selectors. Selectors live only in
  page objects; prefer role/label/test-id locators.
- **Cover the failure path**, not only the happy path: validation errors, 401/403/404,
  downstream failure, timeouts.
- **Each test owns its data**: creates what it needs, cleans up (or uses unique data inside a
  throwaway container). Never depend on another test running first.
- Group related tests (`describe` / test class per feature), keep files focused on one feature
  or page, tag critical journeys (`@smoke`, `@critical`, `@regression`) for selective runs.

## Step 6 — Prove the tests are real

1. Run them and show the real summary (passed / failed / skipped counts). Don't claim green
   without output.
2. **Mutation check**: for each new important test, briefly break the behaviour it covers
   (flip a condition, change a status code, remove a validation rule), run it, confirm it goes
   **red**, then restore. A test that stays green when the behaviour is broken is deleted or fixed.
3. Run the suite twice (and in random order / in parallel if supported) to catch flakiness and
   ordering dependencies.

If a test needs infrastructure you can't run here (Docker, a browser, a missing SDK), say so
plainly, give the exact command for the user, and don't report it as passing.

## Step 7 — Wire into CI

- Run the same commands CI will run (one entry point; if the repo has a build script, use it).
- Upload failure artifacts (Playwright traces, screenshots, videos, test result files).
- Add a **coverage gate** at the current level (a floor, so it can't silently regress), ideally
  on branch coverage for critical modules. Never write meaningless tests to hit a number.
- Keep browser/driver versions in CI aligned with the test package version.

## Step 8 — Self-review before saying "done"

Go through this list and fix anything that fails:

- [ ] Each test fails if I break the behaviour it claims to cover (mutation-checked).
- [ ] Every test has assertions on specific expected values / messages.
- [ ] No fixed sleeps (`Thread.Sleep`, `Task.Delay` used as a wait, `waitForTimeout`, `time.sleep`).
- [ ] No real external network in unit tests; no real third-party services anywhere.
- [ ] No catch-all `try/catch`/`except: pass` that swallows failures in tests or helpers.
- [ ] Edge and error cases are covered, not only the happy path.
- [ ] Tests are independent: pass alone, in random order, and in parallel where enabled.
- [ ] Test data and side effects (records, files, uploads, fake-service modes) are cleaned up.
- [ ] Names describe requirements; no dead, skipped or commented-out tests left behind.
- [ ] No over-mocking — the test exercises real code, not just the mock's behaviour.
- [ ] Timeouts, base URL and retries are set in config, not sprinkled in tests.
- [ ] Files are focused; no giant catch-all test file.

## Report back

Finish with a short summary:

1. **Stack & conventions detected** (one or two lines).
2. **What was added** — files, with one line each.
3. **What is tested and why** — the "would hurt" list, mapped to test names and levels.
4. **Run results** — real command + pass/fail counts, and the mutation check outcome.
5. **Gaps / next steps** — what is untested, what needs Docker/CI to run, any
   `UNVERIFIED ASSUMPTION`s.
