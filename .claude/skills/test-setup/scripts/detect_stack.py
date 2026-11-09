#!/usr/bin/env python3
"""Detect a project's languages, test tooling, CI and existing test folders.

Usage: python3 detect_stack.py [project-root]

Reports only what it finds in files; it never guesses versions. Output is plain text so it can
be read directly. Exit code is always 0.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

SKIP_DIRS = {".git", "node_modules", "bin", "obj", "dist", "build", ".venv", "venv", "__pycache__",
             ".next", "target", ".tox", "coverage", "playwright-report", "test-results", ".idea", ".vs"}
MAX_FILES = 20000

# substring in a manifest -> label
DOTNET_PKGS = {
    "xunit": "xUnit", "nunit": "NUnit", "mstest": "MSTest", "fakeiteasy": "FakeItEasy", "moq": "Moq",
    "nsubstitute": "NSubstitute", "microsoft.aspnetcore.mvc.testing": "WebApplicationFactory",
    "testcontainers": "Testcontainers", "aspire.hosting.testing": "Aspire testing",
    "microsoft.playwright": "Playwright (.NET)", "reqnroll": "Reqnroll (Gherkin)", "specflow": "SpecFlow",
    "coverlet": "coverlet (coverage)", "pactnet": "PactNet", "wiremock": "WireMock.Net",
    "fluentassertions": "FluentAssertions", "shouldly": "Shouldly", "verify": "Verify (snapshots)",
    "autofac": "Autofac (DI)",
}
JS_PKGS = {
    "@playwright/test": "Playwright Test", "playwright": "Playwright", "vitest": "Vitest", "jest": "Jest",
    "mocha": "Mocha", "cypress": "Cypress", "@testing-library/react": "Testing Library (React)",
    "@testing-library/vue": "Testing Library (Vue)", "supertest": "Supertest", "msw": "MSW",
    "testcontainers": "Testcontainers", "@pact-foundation/pact": "Pact", "nock": "nock",
    "@vitest/coverage-v8": "Vitest coverage", "c8": "c8", "nyc": "nyc",
}
PY_PKGS = {
    "pytest": "pytest", "pytest-cov": "pytest-cov", "pytest-randomly": "pytest-randomly",
    "pytest-playwright": "pytest-playwright", "playwright": "Playwright (Python)",
    "testcontainers": "testcontainers-python", "pytest-mock": "pytest-mock", "responses": "responses",
    "respx": "respx", "freezegun": "freezegun", "hypothesis": "Hypothesis", "pact-python": "Pact",
    "pytest-django": "pytest-django", "httpx": "httpx", "fastapi": "FastAPI", "django": "Django",
    "flask": "Flask",
}
TEST_DIR_RE = re.compile(r"(^|[._-])(tests?|specs?|e2e|__tests__|integration(tests)?|unit(tests)?)([._-]|$)", re.I)
TEST_FILE_RE = re.compile(r"(\.(test|spec)\.[cm]?[jt]sx?$|^test_.*\.py$|_test\.(py|go)$|Tests?\.cs$|\.feature$)")


def walk(root: Path):
    count = 0
    stack = [root]
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
    files = list(walk(root))
    rel = lambda p: str(p.relative_to(root))

    langs: set[str] = set()
    tools: dict[str, set[str]] = {}
    manifests: list[str] = []
    central_versions: list[str] = []
    ci: list[str] = []
    test_dirs: set[str] = set()
    test_files: list[str] = []
    configs: list[str] = []
    instructions: list[str] = []

    def found(label: str, where: Path):
        tools.setdefault(label, set()).add(rel(where))

    for f in files:
        name = f.name
        low = name.lower()
        r = rel(f)

        if low in {"claude.md", "agents.md", "contributing.md"}:
            instructions.append(r)
        if ".github/workflows" in r.replace("\\", "/") or low in {".gitlab-ci.yml", "azure-pipelines.yml",
                                                                    "jenkinsfile", "bitbucket-pipelines.yml"} \
                or r.replace("\\", "/").startswith(".circleci/"):
            ci.append(r)
        if low in {"directory.packages.props", "global.json", "package-lock.json", "pnpm-lock.yaml",
                   "yarn.lock", "poetry.lock", "uv.lock", "go.sum"}:
            central_versions.append(r)
        if re.match(r"(playwright|vitest|jest|cypress)\.config\.[cm]?[jt]s$", low) or low in {
                "pytest.ini", "conftest.py", "tox.ini", "setup.cfg", "reqnroll.json", "xunit.runner.json",
                ".runsettings", "codecov.yml", ".nycrc"} or low.endswith(".runsettings"):
            configs.append(r)

        if low.endswith((".csproj", ".fsproj")) or low == "directory.packages.props":
            langs.add(".NET")
            manifests.append(r)
            text = read(f).lower()
            for key, label in DOTNET_PKGS.items():
                if f'include="{key}' in text:
                    found(label, f)
        elif low == "package.json":
            langs.add("JavaScript/TypeScript")
            manifests.append(r)
            try:
                data = json.loads(read(f) or "{}")
            except json.JSONDecodeError:
                data = {}
            deps = {**data.get("dependencies", {}), **data.get("devDependencies", {})}
            for key, label in JS_PKGS.items():
                if key in deps:
                    found(f"{label} ({deps[key]})", f)
            scripts = data.get("scripts", {})
            for s in ("test", "test:unit", "test:e2e", "e2e", "coverage"):
                if s in scripts:
                    found(f'npm script "{s}": {scripts[s]}', f)
        elif low in {"pyproject.toml", "requirements.txt", "requirements-dev.txt", "setup.py", "pipfile"}:
            langs.add("Python")
            manifests.append(r)
            text = read(f).lower()
            for key, label in PY_PKGS.items():
                if re.search(rf"(^|[\s\"'\[,]){re.escape(key)}([\s\"'<>=~!\],;\[]|$)", text, re.M):
                    found(label, f)
        elif low == "go.mod":
            langs.add("Go"); manifests.append(r)
        elif low in {"pom.xml", "build.gradle", "build.gradle.kts"}:
            langs.add("Java/Kotlin"); manifests.append(r)
            text = read(f).lower()
            for key in ("junit", "testng", "mockito", "testcontainers", "rest-assured", "pact"):
                if key in text:
                    found(key, f)
        elif low == "cargo.toml":
            langs.add("Rust"); manifests.append(r)
        elif low == "gemfile":
            langs.add("Ruby"); manifests.append(r)
        elif low == "composer.json":
            langs.add("PHP"); manifests.append(r)

        if TEST_FILE_RE.search(name):
            test_files.append(r)
            for part in f.relative_to(root).parts[:-1]:
                if TEST_DIR_RE.search(part):
                    test_dirs.add(str(Path(*f.relative_to(root).parts[: f.relative_to(root).parts.index(part) + 1])))
                    break

    def section(title: str, items):
        items = sorted(items)
        print(f"\n## {title}")
        print("\n".join(f"- {i}" for i in items) if items else "- (none found)")

    print(f"# Stack report for {root}")
    section("Project instructions (read first)", instructions)
    section("Languages", langs)
    section("Manifests", manifests)
    section("Version / lock files (put versions here, don't invent them)", central_versions)
    print("\n## Test tooling found")
    if tools:
        for label in sorted(tools):
            print(f"- {label}  <- {', '.join(sorted(tools[label])[:3])}")
    else:
        print("- (none found)")
    section("Test config files", configs)
    section("Test folders", test_dirs)
    print(f"\n## Test files: {len(test_files)}")
    for t in sorted(test_files)[:15]:
        print(f"- {t}")
    if len(test_files) > 15:
        print(f"- ... {len(test_files) - 15} more")
    section("CI", ci)
    if not test_files:
        print("\nNo tests yet: choose the stack's idiomatic runner and create the layout from the reference file.")


if __name__ == "__main__":
    main()
