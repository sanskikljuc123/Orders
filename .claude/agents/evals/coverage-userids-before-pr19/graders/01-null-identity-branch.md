---
type: llm
focus: trace
weight: 1
---

Identifies that the case of a ClaimsPrincipal with no identity at all (Identity is null, e.g. new
ClaimsPrincipal()) is not covered for UserIds.Name, because the existing test uses an empty
ClaimsIdentity.
