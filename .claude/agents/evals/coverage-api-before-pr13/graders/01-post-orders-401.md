---
type: llm
focus: trace
weight: 1
---

Reports that POST /orders has no test for a request without a token (401, and nothing charged),
while only GET endpoints (/orders, /me) were checked, and rates it HIGH because it is the endpoint
that charges money.
