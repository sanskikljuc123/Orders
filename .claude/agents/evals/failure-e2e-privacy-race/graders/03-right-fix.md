---
type: llm
focus: trace
weight: 1
---

Recommends waiting for the order response (e.g. RunAndWaitForResponseAsync on the POST /orders, or
waiting for the visible result) before the next step and asserting it succeeded, and does not
recommend retries, Thread.Sleep/Task.Delay or larger timeouts as the fix.
