---
tags: [qa-agents, failure-analysis]
max_turns: 40
timeout_seconds: 1200
allowed_tools: [Read, Glob, Grep, Bash, Agent]
---

Use the failure-analysis agent. On commit c7e063f the E2E scenario "A user never sees another
user's orders" fails sometimes (it passes most runs, also on CI): step "Then my order list contains
my order" fails with `Microsoft.Playwright.PlaywrightException : Locator expected to have count
'1' But was: '0'`. Analyse it as of that commit (read files with `git show c7e063f:<path>`).
