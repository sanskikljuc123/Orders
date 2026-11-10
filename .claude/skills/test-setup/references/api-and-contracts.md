# API and contract testing (.NET)

## Per-endpoint coverage

For each endpoint that matters, cover what a caller depends on:

| Area | What to assert |
|---|---|
| Status codes | Success (200/201/204) and each documented error (400, 401, 403, 404, 409, 422) |
| Body shape | Specific field values, types, required fields present; validate against the OpenAPI/JSON schema if one exists |
| Auth | No token → 401; invalid/expired token → 401; valid token, wrong user/role → 403 (or 404 if the API hides existence) |
| Authorization / tenancy | User A cannot read, list, update or delete user B's resource |
| Validation | Each rule rejects with the specific field and message; boundaries (min-1, min, max, max+1), empty, null, unicode, oversized |
| Broken input | Cut-off or empty JSON, wrong JSON types, wrong content type (415), route values of the wrong type (`/orders/not-a-guid` → 404): never a 500, never a side effect |
| Pagination | First/last page, page size limits, stable ordering, `next` link/cursor, empty result |
| Idempotency | Repeating a PUT/DELETE, or a POST with the same idempotency key, has no extra side effect (e.g. one charge, one record); the same key with a *different* body is rejected (422 in the IETF Idempotency-Key draft), never answered with the old result |
| Side effects | The DB row / message / email actually exists, read back through a separate path |
| Failure of dependencies | Downstream 500/timeout → the API's documented behaviour, not a 500 with a stack trace |
| Headers | `Location` on 201, content type, caching/CORS where relevant |

Assert values, not just the status: `201` *and* `body.status == "Paid"` *and* the `Location`
header points to a fetchable resource.

### Make the checklist enforceable

A checklist that relies on memory leaves gaps: coverage and mutation tools measure *your* code,
so framework behaviour (a missing `RequireAuthorization`, bad JSON becoming a 500) stays green.

- **Discover endpoints, don't list them.** Read `EndpointDataSource` from the running app
  (`factory.Services`), call every endpoint without a token and expect 401, except an explicit
  `Public` allow-list (health, login). A new unprotected endpoint then fails the build without
  anyone writing a test. Add a positive test that the discovery finds the known endpoints, so the
  check cannot pass by finding nothing. Route groups report patterns with a trailing `/`; trim it.
- **Keep an endpoint × check matrix** in the risk map (`TESTING.md`): one row per endpoint, one
  column per area above. An empty cell is a visible gap; "n/a" is a decision.
- **Lock the response shape**: assert the exact set of field names, so adding, renaming or
  removing a field is a reviewed decision.
- **Expired tokens**: JwtBearer's lifetime check uses the system clock, not `TimeProvider`, so a
  fake clock does not expire a token. Rather than waiting out a real lifetime plus the 5-minute
  clock skew, guard the options (`ValidateLifetime`, `RequireExpirationTime`,
  `RequireSignedTokens`, `ClockSkew`) and say that the library enforces them.

## Consumer-driven contract tests (Pact)

Use when one service calls another **that your organisation owns and deploys independently**, so
a provider change can't silently break a consumer. Pact only works if the provider team runs the
verification in their CI — a third-party API (payment gateway, Keycloak, SaaS) never will.

| Provider | How to keep the consumer honest |
|---|---|
| Your own service | Pact: consumer writes the pact, provider verifies it, `can-i-deploy` gates release |
| Third-party API | Validate your fake's requests/responses against the provider's published OpenAPI spec or recorded real responses; optionally a small, scheduled (not per-PR) smoke test against the provider's sandbox |

1. **Consumer side** writes an interaction: "given order 42 exists, GET /orders/42 returns
   `{ id: 42, status: like("Paid") }`" using matchers (`like`, `eachLike`, regex), not exact
   incidental values. The test runs the consumer's client against the Pact mock server and
   produces a pact file.
2. Publish the pact (Pact Broker / PactFlow) tagged with the consumer version/branch.
3. **Provider side** verifies all consumer pacts against the real provider with provider states
   set up via a state-change endpoint/hook. Run it in the provider's CI.
4. Gate deploys with `can-i-deploy`.

Library: PactNet. Check the current major version's API — it changed significantly between majors.

Contract tests check *shape and semantics the consumer uses*, not provider business logic;
keep those in the provider's own tests.

## Fakes for third-party APIs

- Build them from the provider's OpenAPI spec or recorded real responses (HAR files,
  WireMock.Net recordings). Without either, keep the fake minimal and document it as an
  `UNVERIFIED ASSUMPTION`.
- In-process tests: FakeItEasy fakes registered via `ConfigureTestServices` / Autofac
  `ConfigureTestContainer`, or HTTP-level mocks (WireMock.Net) for typed `HttpClient`s.
- Out-of-process tests (E2E on Aspire): a fake service process with a test-only control
  endpoint (e.g. `POST /__admin/mode`), reset after each test.
