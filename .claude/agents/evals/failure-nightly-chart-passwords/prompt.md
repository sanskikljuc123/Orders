---
tags: [qa-agents, failure-analysis]
max_turns: 40
timeout_seconds: 1200
allowed_tools: [Read, Glob, Grep, Bash, Agent]
---

Use the failure-analysis agent. The nightly workflow `ci-nightly.yml` failed in job
`deployment-smoke` on commit 3876be9 (nightly run #1, 2026-11-10 07:34 UTC). The end of the job
log is in `.claude/agents/evals/failure-nightly-chart-passwords/fixtures/deploy-smoke.log`.
Analyse the failure as of that commit (read files with `git show 3876be9:<path>`) and say whether
it still applies to the current main.
