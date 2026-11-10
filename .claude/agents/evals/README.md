# QA agent evals

Known-answer cases for the agents in `.claude/agents/`, taken from real failures and gaps in this
repository's history. Same format as `.claude/skills/test-setup/evals` (`case.yaml`, `prompt.md`,
`graders/*.md`; failure cases bring their CI log excerpt in `fixtures/`). Each prompt names a commit
and has the agent read files with `git show <commit>:<path>`, so the cases stay valid as main moves on.

| Case | Agent | Known answer |
|---|---|---|
| `failure-nightly-chart-passwords` | failure-analysis | chart installed without passwords (nightly run #1); already fixed |
| `failure-trivy-base-image` | failure-analysis | apt CVE in the base image: a dependency, fixed with chiseled images |
| `failure-e2e-privacy-race` | failure-analysis | test race: navigating before the order POST finished |
| `coverage-api-before-pr13` | coverage-review | POST /orders 401 and broken-input tests missing |
| `coverage-userids-before-pr19` | coverage-review | the no-identity branch of UserIds |
| `requirement-idempotency-key-reuse` | requirement-analysis | reused key + different body must be rejected; 422 an open question |
| `requirement-cancel-order` | requirement-analysis | time boundaries, ownership, refund not invented |

Run them after changing an agent, in a session that has the agents loaded (agents are read at
session start). A case passes when every grader passes.
