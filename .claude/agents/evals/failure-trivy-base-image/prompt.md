---
tags: [qa-agents, failure-analysis]
max_turns: 40
timeout_seconds: 1200
allowed_tools: [Read, Glob, Grep, Bash, Agent]
---

Use the failure-analysis agent. CI job `build-and-test` failed on commit a4e97a4 (pull
request #16) in the `Images` target. The relevant part of the log is in
`.claude/agents/evals/failure-trivy-base-image/fixtures/images.log`. What failed, why, and what
should be done?
