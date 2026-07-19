#!/usr/bin/env python3
"""Validate trusted-PR Unity admission and the aggregate skip truth table."""

from __future__ import annotations

import os
import re
import subprocess
import tempfile
from pathlib import Path


WORKFLOW = Path(".github/workflows/unity-tests.yml")
REGISTERED_UNITY_AUTOMATION = {
    ".github/actions/return-unity-license/action.yml",
    ".github/actions/validate-unity-license/action.yml",
    ".github/workflows/perf-numbers.yml",
    ".github/workflows/release.yml",
    ".github/workflows/unity-benchmarks.yml",
    ".github/workflows/unity-gameci-experiment.yml",
    ".github/workflows/unity-tests.yml",
}
UNITY_CREDENTIAL_OR_ACTIVATION = re.compile(
    r"\bUNITY_(?:SERIAL|EMAIL|PASSWORD|LICENSE|LICENSING_SERVER)\b|"
    r"game-ci/unity-(?:test-runner|builder|activate)@",
    re.IGNORECASE,
)
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


def find_unregistered_unity_automation(files: dict[str, str]) -> list[str]:
    return sorted(
        path
        for path, source in files.items()
        if UNITY_CREDENTIAL_OR_ACTIVATION.search(source)
        and path not in REGISTERED_UNITY_AUTOMATION
    )


def repository_unity_automation(github: Path = Path(".github")) -> dict[str, str]:
    return {
        path.as_posix(): path.read_text(encoding="utf-8")
        for path in github.rglob("*")
        if path.is_file() and path.suffix.lower() in {".yml", ".yaml"}
    }


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
    marker_cases = (
        ("serial credential", "env: { UNITY_SERIAL: secret }"),
        ("email credential", "env: { UNITY_EMAIL: secret }"),
        ("password credential", "env: { UNITY_PASSWORD: secret }"),
        ("retired license payload", "env: { UNITY_LICENSE: secret }"),
        ("retired licensing server", "env: { UNITY_LICENSING_SERVER: secret }"),
        ("GameCI test runner", "uses: game-ci/unity-test-runner@v4"),
        ("GameCI builder", "uses: game-ci/unity-builder@v4"),
        ("GameCI activation action", "uses: game-ci/unity-activate@v2"),
    )
    for name, marker in marker_cases:
        path = f".github/workflows/{name.replace(' ', '-')}.yml"
        require(
            find_unregistered_unity_automation({path: marker}) == [path],
            f"{name}: Unity automation marker was not detected",
        )

    registration_cases = (
        (
            "registered active workflow",
            {".github/workflows/unity-tests.yml": "env: { UNITY_SERIAL: secret }"},
            [],
        ),
        (
            "registered cleanup action",
            {".github/actions/return-unity-license/action.yml": "env: { UNITY_EMAIL: secret }"},
            [],
        ),
        (
            "unregistered disabled workflow",
            {".github/workflows-disabled/unity-tests.yml": "env: { UNITY_PASSWORD: secret }"},
            [".github/workflows-disabled/unity-tests.yml"],
        ),
        (
            "unrelated workflow",
            {".github/workflows/docs.yml": "run: npm run docs"},
            [],
        ),
    )
    for name, files, expected in registration_cases:
        require(
            find_unregistered_unity_automation(files) == expected,
            f"{name}: unexpected Unity automation classification",
        )

    with tempfile.TemporaryDirectory() as temporary_directory:
        github = Path(temporary_directory) / ".github"
        workflows = github / "workflows"
        workflows.mkdir(parents=True)
        (workflows / "mixed.YmL").write_text("name: mixed", encoding="utf-8")
        (workflows / "upper.YAML").write_text("name: upper", encoding="utf-8")
        (workflows / "ignored.txt").write_text("name: ignored", encoding="utf-8")
        discovered = repository_unity_automation(github)
        require(
            set(discovered) == {
                (workflows / "mixed.YmL").as_posix(),
                (workflows / "upper.YAML").as_posix(),
            },
            "Unity automation discovery must treat YAML extensions case-insensitively",
        )

    unregistered = find_unregistered_unity_automation(repository_unity_automation())
    require(
        not unregistered,
        "unregistered credential-bearing or activation-capable Unity automation: "
        + ", ".join(unregistered),
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
    require(
        "environment:" not in licensed,
        "Unity job must use organization secrets without an environment approval gate",
    )

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
