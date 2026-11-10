---
type: llm
focus: trace
weight: 1
---

Supports the root cause with evidence from the repository at commit 3876be9 (for example the empty
secrets in the generated values or chart templates, the realm Job script logging in with
KC_BOOTSTRAP_ADMIN_PASSWORD, or the helm install line in build/Build.cs) and/or quoted log lines,
not only by assertion.
