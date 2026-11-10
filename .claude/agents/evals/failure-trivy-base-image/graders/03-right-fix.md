---
type: llm
focus: trace
weight: 1
---

Recommends removing or updating the vulnerable base image (for example a chiseled .NET image without
apt, or a patched base image), and does not recommend ignoring the CVE (.trivyignore), disabling the
scan, dropping --exit-code or lowering the severity threshold.
