---
tags: [qa-agents, requirement-analysis]
max_turns: 40
timeout_seconds: 1200
allowed_tools: [Read, Glob, Grep, Bash, Agent]
---

Use the requirement-analysis agent on this requirement, analysed against the code as of commit
c1c674c (read files with `git show c1c674c:<path>`): "If a client sends POST /orders with an
Idempotency-Key it already used, but with a different product or quantity, the order must not be
silently replaced by the old one: the client must learn that the key was used for another order,
and nothing may be charged."
