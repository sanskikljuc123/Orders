#!/usr/bin/env python3
"""PreToolUse hook: refuse `git commit` commands that carry AI attribution lines.

CLAUDE.md, Git: "Commit messages and PR descriptions: no AI attribution." Instructions can be
forgotten; this hook makes the rule hold. It reads the Bash tool call (JSON on stdin), and if the
command is a `git commit` whose message (inline, or a file given with -F/--file) contains an
attribution line, it exits with code 2: Claude Code then blocks the call and shows the reason.
"""
import json
import re
import sys

PATTERNS = [
    r"Co-Authored-By:\s*Claude",
    r"Claude-Session:",
    r"Generated with \[?Claude Code",
    r"claude\.ai/code/session",
]


def message_files(command: str) -> list[str]:
    return re.findall(r"(?:-F|--file)[ =](\"[^\"]+\"|'[^']+'|\S+)", command)


def main() -> int:
    try:
        call = json.load(sys.stdin)
    except json.JSONDecodeError:
        return 0  # not a tool call we understand: never block by accident
    command = (call.get("tool_input") or {}).get("command") or ""
    if not re.search(r"\bgit\b(?:\s+-\S+(?:\s+\S+)?)*\s+commit\b", command):
        return 0

    text = command
    for name in message_files(command):
        try:
            with open(name.strip("\"'"), encoding="utf-8") as f:
                text += "\n" + f.read()
        except OSError:
            pass

    found = [p for p in PATTERNS if re.search(p, text, re.IGNORECASE)]
    if not found:
        return 0
    print("Blocked by .claude/hooks/block-ai-attribution.py: the commit message contains AI "
          "attribution, which CLAUDE.md forbids (Git section). Remove these lines and commit "
          f"again: {', '.join(found)}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
