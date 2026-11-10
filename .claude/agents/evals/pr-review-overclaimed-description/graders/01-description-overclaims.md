---
type: llm
focus: trace
weight: 1
---

Flags as BLOCKING that the description claims the provider idempotency was verified against the real
provider, while the code or the FakePaymentService README marks it FAKED / UNVERIFIED ASSUMPTION
(cites at least one of src/Orders.Shared/Payments.cs, src/FakePaymentService/Program.cs or
src/FakePaymentService/README.md).
