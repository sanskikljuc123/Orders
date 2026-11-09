#!/usr/bin/env python3
"""Report a .NET repo's test setup: projects, test packages, version files, features, CI.

Usage: python3 detect_stack.py [repo-root]

Reports only what it finds in files; it never guesses versions. Exit code is always 0.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

SKIP_DIRS = {".git", "bin", "obj", "node_modules", "TestResults", "playwright-artifacts",
             ".vs", ".idea", ".nuke"}
MAX_FILES = 20000

PACKAGES = {
    "xunit": "xUnit", "nunit": "NUnit", "mstest": "MSTest",
    "fakeiteasy": "FakeItEasy", "moq": "Moq", "nsubstitute": "NSubstitute",
    "microsoft.aspnetcore.mvc.testing": "WebApplicationFactory",
    "testcontainers": "Testcontainers", "aspire.hosting.testing": "Aspire testing",
    "microsoft.playwright": "Playwright (.NET)", "reqnroll": "Reqnroll (Gherkin)",
    "specflow": "SpecFlow (legacy Gherkin)", "coverlet": "coverlet (coverage)",
    "pactnet": "PactNet", "wiremock": "WireMock.Net", "fluentvalidation": "FluentValidation",
    "fluentassertions": "FluentAssertions", "shouldly": "Shouldly", "verify": "Verify (snapshots)",
    "autofac": "Autofac (DI)", "fusioncache": "FusionCache", "serilog": "Serilog",
    "nuke.common": "NUKE", "microsoft.extensions.timeprovider.testing": "FakeTimeProvider",
}
VERSION_RE = re.compile(r'<PackageVersion\s+Include="([^"]+)"\s+Version="([^"]+)"', re.I)


def walk(root: Path):
    count, stack = 0, [root]
    while stack:
        d = stack.pop()
        try:
            entries = list(d.iterdir())
        except OSError:
            continue
        for e in entries:
            if e.is_dir():
                if e.name not in SKIP_DIRS:
                    stack.append(e)
            else:
                count += 1
                if count > MAX_FILES:
                    return
                yield e


def read(p: Path) -> str:
    try:
        return p.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return ""


def main() -> None:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    rel = lambda p: p.relative_to(root).as_posix()

    instructions, version_files, ci, configs = [], [], [], []
    src_projects, test_projects, features, test_classes = [], [], [], []
    tools: dict[str, set[str]] = {}
    versions: dict[str, str] = {}
    e2e_dirs: set[str] = set()

    for f in walk(root):
        low, r = f.name.lower(), rel(f)
        if low in {"claude.md", "agents.md", "contributing.md"}:
            instructions.append(r)
        if low in {"global.json", "directory.packages.props", "directory.build.props", "gitversion.yml"}:
            version_files.append(r)
        if r.startswith(".github/workflows/") or low in {"azure-pipelines.yml", ".gitlab-ci.yml"} \
                or low in {"build.sh", "build.cmd", "build.ps1"}:
            ci.append(r)
        if low in {"reqnroll.json", "xunit.runner.json", "specflow.json"} or low.endswith(".runsettings"):
            configs.append(r)
        if low == "directory.packages.props":
            versions.update({k: v for k, v in VERSION_RE.findall(read(f))})
        if low.endswith((".csproj", ".fsproj")):
            text = read(f).lower()
            is_test = "microsoft.net.test.sdk" in text or "<istestproject>true" in text
            (test_projects if is_test else src_projects).append(r)
            for key, label in PACKAGES.items():
                if f'include="{key}' in text:
                    tools.setdefault(label, set()).add(r)
        if low.endswith(".feature"):
            features.append(r)
        if low.endswith("tests.cs") or low.endswith("steps.cs"):
            test_classes.append(r)
        parts = f.relative_to(root).parts
        for i, part in enumerate(parts[:-1]):
            if part in {"Features", "Steps", "Pages", "Hooks"}:
                e2e_dirs.add("/".join(parts[: i + 1]))

    def section(title, items):
        items = sorted(items)
        print(f"\n## {title}")
        print("\n".join(f"- {i}" for i in items) if items else "- (none found)")

    print(f"# .NET test report for {root}")
    section("Project instructions (read first)", instructions)
    section("SDK / version / build files (versions go here, never in .csproj)", version_files)
    section("Source projects", src_projects)
    section("Test projects", test_projects)
    print("\n## Packages in use")
    if tools:
        for label in sorted(tools):
            print(f"- {label}  <- {', '.join(sorted(tools[label])[:3])}")
    else:
        print("- (none found)")
    if versions:
        print("\n## Central package versions (Directory.Packages.props)")
        for k in sorted(versions, key=str.lower):
            print(f"- {k} {versions[k]}")
    section("Test config files", configs)
    section("E2E folders (Features/Steps/Pages/Hooks)", e2e_dirs)
    section("Feature files", features)
    section("Test / step classes", test_classes)
    section("CI and build entry points", ci)
    if not test_projects:
        print("\nNo test projects yet: create the layout from references/dotnet.md.")


if __name__ == "__main__":
    main()
