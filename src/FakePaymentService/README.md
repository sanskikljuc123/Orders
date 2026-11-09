# FakePaymentService

Stand-in for the real payment provider in local dev (`aspire run`) and E2E tests (Aspire runs it
as a separate process, so FakeItEasy cannot replace the payment client there).

## UNVERIFIED ASSUMPTION

There is no OpenAPI spec and no recorded real response for the payment provider in this repo.
The contract below is assumed from `HttpPaymentClient` in `src/Orders.Api/Program.cs` only:

- `POST /charge` with JSON `{ "amount": <decimal> }`
- `200` with `{ "approved": true|false }`; any non-2xx is treated by the API as "not paid".

Replace this assumption with the provider's spec or recorded responses before relying on it.

## Test-only control endpoints

| Endpoint | Body | Effect |
|---|---|---|
| `POST /__admin/mode` | `{ "mode": "approve" \| "decline" \| "error" }` | `error` answers `/charge` with 500 |
| `POST /__admin/reset` | none | back to `approve` (E2E calls this after every scenario) |
| `GET /` | none | shows the current mode (also the Aspire health check) |
