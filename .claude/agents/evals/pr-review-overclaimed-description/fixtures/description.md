## A paid order is never lost, a retry never charges twice

- The order is stored as Pending before the charge and completed after it.
- 202 for a Pending order, 503 when it could not be placed.
- The payment provider's idempotency (`Idempotency-Key` header) was verified against the real
  provider's API, so a retry with the same key can never charge twice in production.

All tests green.
