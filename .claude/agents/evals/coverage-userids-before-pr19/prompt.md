---
tags: [qa-agents, coverage-review]
max_turns: 40
timeout_seconds: 1200
allowed_tools: [Read, Glob, Grep, Bash, Agent]
---

Use the coverage-review agent to review the unit test coverage of the `UserIds` helper
(src/Orders.Api/Program.cs) as of commit c2b2f6e (read files with `git show c2b2f6e:<path>`).
