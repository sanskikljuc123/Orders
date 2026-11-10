---
tags: [qa-agents, coverage-review]
max_turns: 40
timeout_seconds: 1200
allowed_tools: [Read, Glob, Grep, Bash, Agent]
---

Use the coverage-review agent to review the API test coverage of the Orders API as of commit
8b5d978 (read files with `git show 8b5d978:<path>`, do not check it out). Focus on what callers of
the HTTP endpoints depend on.
