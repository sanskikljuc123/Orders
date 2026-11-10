# Testing map

What could go wrong in production, and which tests would catch it. Use it to spot gaps: a risk
with no test, or a risk tested only at one slow level. When you add a behaviour or a test, update
the row. How to run each layer and the quality gates: [`CLAUDE.md`](CLAUDE.md#commands).

Layers: **U** unit (`tests/Orders.UnitTests`), **C** contract (`tests/Orders.ContractTests`),
**I** integration (`tests/Orders.IntegrationTests`, Docker), **E** E2E (`tests/Orders.E2E`, Docker +
browser), **N** nightly only (`.github/workflows/ci-nightly.yml`).

## Money and orders

| Risk | Tests |
|---|---|
| Wrong amount charged | U `OrderServiceTests.Charges_10_per_item`; I `OrdersApiTests.Order_charges_quantity_times_10` |
| Declined or failed payment shown as paid | U `Declined_payment_gives_PaymentFailed_order`; I `Declined_payment_returns_order_with_PaymentFailed_status`, `Declined_order_is_stored_as_PaymentFailed`; E "Order is marked as failed when payment is declined", "Order is never shown as paid when the payment service fails" |
| Double click or retry charges twice | U `Repeating_a_request_with_the_same_key_…`, `Concurrent_requests_with_the_same_key_charge_only_once`, `IdempotencyLocksTests`; I `IdempotencyTests` (same order, one row, one charge; unique index); E "An impatient double click places the order only once" |
| A reused key silently returns an old order for a new request | U `Reusing_a_key_for_a_different_order_is_rejected_without_charging` (other product, quantity, case); I `Same_key_for_a_different_order_returns_422_and_is_not_charged_or_stored` |
| Order lost (paid but not stored) | U `Declined_order_is_still_saved_for_the_user`; I `Placed_order_is_stored_with_owner_and_status`, `Created_order_can_be_fetched_from_its_Location` |
| Invalid order charged or stored | U `CreateOrderValidatorTests` (all boundaries, messages, any culture); I `Invalid_order_returns_400_with_field_errors_and_is_not_charged`, `Invalid_order_is_not_stored`; E "Order without a product name is rejected" |
| Validation text changes with the server language | U `Messages_are_the_same_whatever_the_server_culture` (and every validator case runs under de-DE) |

## Payment provider

| Risk | Tests |
|---|---|
| Client and fake disagree on route, body or meaning | C `PaymentContractTests` (approve, decline, error, reset, response shape) |
| Provider error, garbage answer, outage or slowness crashes ordering or hangs | U `HttpPaymentClientTests` (`Anything_but_a_clear_approval_means_not_paid`, `Unreachable_provider_…`, `Provider_that_does_not_answer_in_time_…`) |
| Unpaid orders with no trace for operators | U `Unpaid_outcome_is_logged_as_a_warning_with_its_reason` |
| Fake differs from the real provider | **Not covered**: the contract is an UNVERIFIED ASSUMPTION (`src/FakePaymentService/README.md`) |

## Accounts and security

| Risk | Tests |
|---|---|
| API contract changes unnoticed (route, status code, field) | I `OpenApiContractTests` (document equals `docs/api/openapi.v1.json`; key responses and `Order` fields anchored; not published in production) |
| Internal user ids leak to callers | I `Order_response_has_exactly_the_fields_callers_read` (no `userId`), `Key_is_not_returned_to_the_client` |
| Anonymous or forged requests accepted (incl. a new endpoint someone forgot to protect) | I `EndpointAuthTests` (every endpoint found in the app, except the public list, returns 401; an order without a token is not charged), `Request_without_token_returns_401`, `Request_with_garbage_token_returns_401` |
| Expired, unsigned, wrongly signed or foreign tokens accepted | I `TokenValidationTests` (minted tokens: expired, no expiry, other issuer, other audience, other key, plus a valid control), `AuthSecurityOptionsTests.Expired_unsigned_or_foreign_tokens_are_rejected_in_every_environment` (same settings in Production) |
| A user sees another user's orders | U `OrderStoreTests`; I `User_cannot_fetch_another_users_order`, `Users_list_contains_only_their_own_orders`; E "A user never sees another user's orders" (both directions) |
| Logout leaves the user signed in (app or Keycloak) | E "Customer who logs out is asked to log in again", "… can no longer place an order", "… must enter their password again" |
| A copied cookie still works after logout | U `SessionStoreTests`; I `Login_cookie_sessions_live_on_the_server_so_logout_can_revoke_them`; E "A copy of the session cookie stops working after logout" |
| A token issued to another application of the realm is accepted | I `Token_issued_to_another_client_of_the_realm_returns_401` (real `admin-cli` token), `AuthSecurityOptionsTests` (audience required); U `ProductionRealmTests.Web_client_tokens_carry_the_api_audience` |
| Tokens, passwords or idempotency keys end up in logs | I `LogSecretsTests` (every log event of the sensitive flows, incl. refused tokens; fails on any JWT) |
| A dependency with a known vulnerability ships | every restore: NuGet audit fails on High/Critical, direct or transitive (`Directory.Build.props`); Dependabot update PRs |
| Insecure code (weak crypto, disabled validation, injectable SQL) | every build: .NET security analyzers, findings are errors |
| A container image with a known fixable vulnerability is pushed | `Images` target: Trivy scan before any push (HIGH/CRITICAL with a fix fail) |
| Production runs with the plain-http dev login settings | I `AuthSecurityOptionsTests.Production_requires_https_and_secure_cookies` |
| Production realm gets test users, wildcards, plain HTTP or password login | U `ProductionRealmTests`; N `DeploySmoke` (realm in a real cluster: HTTPS-only, no users) |
| Test realm or generated passwords reach git or a deployment | `.gitignore` (`k8s-artifacts/`); AppHost imports the test realm only outside publish mode (not automated: reviewed) |

## API endpoints

What each caller depends on, per endpoint (all I unless marked). The machine-readable contract is
[`docs/api/openapi.v1.json`](docs/api/openapi.v1.json); `/login` and `/logout` are browser redirects, not part of it. A new or changed endpoint gets its
row filled in; an empty cell is a gap, "n/a" a decision.

| Endpoint | No / bad token | Other user's data | Success contract | Invalid / broken input | Not found / empty |
|---|---|---|---|---|---|
| `POST /orders` | 401, not charged (`EndpointAuthTests`) | n/a (always the caller's) | 201, `Location`, exact fields without `userId` (`Order_response_has_exactly_the_fields_callers_read`), Paid / PaymentFailed, charge, idempotency (`IdempotencyTests`) | 400 per field (`Invalid_order_…`), cut-off / empty / wrong-type JSON 400 (`Unreadable_order_body_…`), text/plain 415, key reused for another order 422 — none charged | n/a |
| `GET /orders` | 401 | only own orders (U I E) | oldest first (`Users_list_shows_orders_oldest_first`) | n/a | new user gets `[]` (`NewUserTests`) |
| `GET /orders/{id}` | 401 | 404 (`User_cannot_fetch_another_users_order`) | fetched from `Location` | not a Guid → 404 | unknown id → 404 |
| `GET /me` | 401 | n/a | name (`Me_returns_the_signed_in_users_name`) | n/a | n/a |
| `GET /health` | public on purpose | n/a | `Healthy`: E2E startup, N `DeploySmoke` | n/a | n/a |
| `GET /login`, `/logout` | public on purpose | n/a | E `Login.feature`, `Logout.feature` | n/a | n/a |
| `GET /openapi/v1.json` | public, Development only; 404 in production (`OpenApiContractTests`) | n/a | equals the committed document | n/a | n/a |

## Delivery

| Risk | Tests |
|---|---|
| Chart invalid | `Helm` target (`helm lint`, `helm template`) on every pull request |
| Chart installed without passwords (Keycloak exits, realm Job times out) | `Helm` target: the chart must refuse to render without them (`required-secrets.yaml`) |
| Chart valid but the system does not start (e.g. Keycloak without TLS settings) | N `DeploySmoke` (kind install, hook Job, API /health) |
| Images not built or not pushed | `Images` target on every pull request; push on main |
| Tests pass only in one order or are flaky | random order on every run, E2E included; N `Stability` (each project 3 times, a new order each time) |
| Tests pass but would not catch a bug | N `Mutation` (Stryker; break thresholds in `stryker/*.json`) |
| Coverage quietly drops on critical code | `CoverageGate` (merged report, per-class floors) on every pull request |

## Known gaps

- The real payment provider (see above).
- Multiple API replicas: sessions and idempotency locks are in-memory; only the database index
  protects against double orders across replicas, and no test runs two replicas.
- A real HTTPS deployment end to end (ingress, certificates, real domains) is not tested; the
  URLs in `deploy/` are placeholders.
