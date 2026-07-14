#!/usr/bin/env python3
"""Validate trusted-PR Unity admission and the aggregate skip truth table."""

from __future__ import annotations

import os
import re
import subprocess
from pathlib import Path


WORKFLOW = Path(".github/workflows/unity-tests.yml")
SAME_REPOSITORY_PR_GUARD = re.compile(
    r"github\.event_name\s*!=\s*'pull_request'\s*\|\|\s*"
    r"github\.event\.pull_request\.head\.repo\.full_name\s*==\s*github\.repository"
)
BLANKET_PR_REJECTION = re.compile(
    r"github\.event_name\s*!=\s*'pull_request'\s*&&"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def job_block(source: str, job_id: str) -> str:
    match = re.search(rf"^  {re.escape(job_id)}:\n", source, re.MULTILINE)
    require(match is not None, f"missing job: {job_id}")
    assert match is not None
    rest = source[match.end() :]
    next_job = re.search(r"^  [A-Za-z0-9_-]+:\n", rest, re.MULTILINE)
    end = match.end() + (next_job.start() if next_job else len(rest))
    return source[match.start() : end]


def step_block(job: str, name: str) -> str:
    marker = f"      - name: {name}\n"
    start = job.find(marker)
    require(start >= 0, f"missing step: {name}")
    following = job.find("\n      - name:", start + len(marker))
    return job[start : following if following >= 0 else len(job)]


def run_script(step: str) -> str:
    marker = "        run: |\n"
    start = step.find(marker)
    require(start >= 0, "aggregate step must contain a multiline run script")
    lines = []
    for line in step[start + len(marker) :].splitlines():
        if line and not line.startswith("          "):
            break
        lines.append(line[10:] if line else "")
    require(bool(lines), "aggregate run script must not be empty")
    return "\n".join(lines)


def validate() -> None:
    parser_fixture = """      - name: Fixture
        run: |
          echo first
          echo second
        shell: bash
"""
    require(
        run_script(parser_fixture) == "echo first\necho second",
        "run parser must stop at the next step key",
    )
    require(
        SAME_REPOSITORY_PR_GUARD.search(
            "github.event_name!='pull_request'||"
            "github.event.pull_request.head.repo.full_name==github.repository"
        )
        is not None,
        "same-repository guard parser must accept compact operators",
    )
    require(
        BLANKET_PR_REJECTION.search("github.event_name!='pull_request'&&(") is not None,
        "blanket rejection parser must detect compact operators",
    )

    source = WORKFLOW.read_text(encoding="utf-8")
    licensed = job_block(source, "unity-tests")
    require(
        SAME_REPOSITORY_PR_GUARD.search(licensed) is not None,
        "Unity job must admit same-repository PRs and reject forks",
    )
    require(
        BLANKET_PR_REJECTION.search(licensed) is None,
        "Unity job must not reject every pull request",
    )
    require("environment: unity-license" in licensed, "Unity job must use the protected environment")

    gate = job_block(source, "unity-ci-success")
    require("if: ${{ always() }}" in gate, "aggregate must always report")
    require("re-actors/alls-green" not in gate and "allowed-skips" not in gate, "skips must be typed")
    for variable in (
        "MATRIX_CONFIG_RESULT",
        "RUNNER_PREFLIGHT_RESULT",
        "UNITY_TESTS_RESULT",
        "FORK_PR",
        "DOCS_ONLY",
    ):
        require(re.search(rf"^          {variable}:", gate, re.MULTILINE) is not None, f"missing {variable}")

    script = run_script(step_block(gate, "Verify Unity CI result shape"))
    cases = (
        ("same-repository PR", "success", "success", "success", "false", "false", 0),
        ("fork PR", "success", "skipped", "skipped", "true", "false", 0),
        ("CI-owned docs-only PR", "success", "success", "skipped", "false", "true", 0),
        ("same-repository PR skipped Unity", "success", "success", "skipped", "false", "false", 1),
        ("fork unexpectedly ran Unity", "success", "skipped", "success", "true", "false", 1),
        ("matrix config failed", "failure", "success", "success", "false", "false", 1),
    )
    if os.name != "nt":
        for name, matrix, preflight, unity, fork, docs_only, expected in cases:
            environment = os.environ.copy()
            environment.update(
                {
                    "MATRIX_CONFIG_RESULT": matrix,
                    "RUNNER_PREFLIGHT_RESULT": preflight,
                    "UNITY_TESTS_RESULT": unity,
                    "FORK_PR": fork,
                    "DOCS_ONLY": docs_only,
                }
            )
            result = subprocess.run(
                ["bash", "-c", script],
                env=environment,
                capture_output=True,
                text=True,
                check=False,
            )
            require(
                result.returncode == expected,
                f"{name}: expected {expected}, got {result.returncode}\n{result.stdout}\n{result.stderr}",
            )

    print("Unity pull-request policy validation passed.")


if __name__ == "__main__":
    validate()
