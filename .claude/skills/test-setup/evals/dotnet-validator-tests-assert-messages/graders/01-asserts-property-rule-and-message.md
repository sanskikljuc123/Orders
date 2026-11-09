---
type: llm
focus: trace
weight: 1
---

Every invalid-input test for CreateOrderValidator asserts the failing property name (ProductName or Quantity) and the exact error message, and either the error code or the specific rule; none of them checks only IsValid == false.
