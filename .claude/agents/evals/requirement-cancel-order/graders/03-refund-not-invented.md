---
type: llm
focus: trace
weight: 1
---

Identifies that the payment contract (src/Orders.Shared/Payments.cs and the fake) has no refund
operation and treats refunds as an open question / UNVERIFIED ASSUMPTION, instead of designing or
assuming a provider refund API.
