---
tags: [skill-creator]
max_turns: 40
timeout_seconds: 1200
allowed_tools: [Read, Glob, Grep, Skill, Write, Edit, Bash]
---

We have a FastAPI service in app/main.py with POST /orders (requires bearer token, validates quantity 1-100) and GET /orders/{id}. It calls a payment provider over httpx. Add pytest integration tests for these endpoints.

Input files: pyproject.toml, app/__init__.py, app/main.py
