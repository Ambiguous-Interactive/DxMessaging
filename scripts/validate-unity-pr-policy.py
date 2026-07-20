#!/usr/bin/env python3
"""Validate trusted-PR Unity admission, staleness guards, and skip policy."""

from __future__ import annotations

import os
import re
import subprocess
import tempfile
from pathlib import Path


WORKFLOW = Path(".github/workflows/unity-tests.yml")
CURRENT_PR_HEAD_GUARD = (
    "Ambiguous-Interactive/ambiguous-organization-build-lock/"
    ".github/actions/require-current-pr-head@a00614ace745152a659c5c2654f7cefb68a5a628"
)
ACQUIRE_BUILD_LOCK = (
    "Ambiguous-Interactive/ambiguous-organization-build-lock/"
    ".github/actions/acquire-build-lock@a00614ace745152a659c5c2654f7cefb68a5a628"
)
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


def validate_licensed_workflow_policy(source: str) -> str:
    concurrency = re.search(
        r"^concurrency:\n(?P<body>(?:^  .+\n)+)",
        source,
        re.MULTILINE,
    )
    require(concurrency is not None, "Unity workflow must declare top-level concurrency")
    assert concurrency is not None
    require(
        re.findall(r"^  cancel-in-progress: false[ \t]*$", concurrency.group("body"), re.MULTILINE)
        == ["  cancel-in-progress: false"],
        "Unity workflow concurrency must use one literal cancel-in-progress: false",
    )

    licensed = job_block(source, "unity-tests")
    require(
        re.findall(r"^      fail-fast: false[ \t]*$", licensed, re.MULTILINE)
        == ["      fail-fast: false"],
        "Unity matrix strategy must use one literal fail-fast: false",
    )

    acquire = step_block(licensed, "Acquire organization Unity lock")
    require(
        re.findall(
            rf"^        uses: {re.escape(ACQUIRE_BUILD_LOCK)} # v1\.9\.1[ \t]*$",
            acquire,
            re.MULTILINE,
        )
        == [f"        uses: {ACQUIRE_BUILD_LOCK} # v1.9.1"],
        "Unity acquire step must use the exact immutable v1.9.1 action pin",
    )
    for key, value in (
        ("github-token", "${{ github.token }}"),
        ("pull-request-number", "${{ github.event.pull_request.number }}"),
        ("expected-head-sha", "${{ github.event.pull_request.head.sha }}"),
    ):
        require(
            re.findall(rf"^          {re.escape(key)}:.*$", acquire, re.MULTILINE)
            == [f"          {key}: {value}"],
            f"Unity acquire step must bind exact {key} PR identity input",
        )

    return licensed


def require_policy_mutation_rejected(source: str, before: str, after: str, name: str) -> None:
    require(source.count(before) >= 1, f"{name}: mutation target missing")
    mutated = source.replace(before, after, 1)
    try:
        validate_licensed_workflow_policy(mutated)
    except AssertionError:
        return
    raise AssertionError(f"{name}: unsafe workflow mutation was accepted")


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
    licensed = validate_licensed_workflow_policy(source)
    require_policy_mutation_rejected(
        source,
        "  cancel-in-progress: false\n",
        "  cancel-in-progress: true\n",
        "top-level cancellation policy",
    )
    require_policy_mutation_rejected(
        source,
        "      fail-fast: false\n",
        "      fail-fast: true\n",
        "matrix fail-fast policy",
    )
    require_policy_mutation_rejected(
        source,
        f"        uses: {ACQUIRE_BUILD_LOCK} # v1.9.1\n",
        f"        uses: {ACQUIRE_BUILD_LOCK[:-1]}0 # v1.9.1\n",
        "acquire action pin",
    )
    acquire_step = step_block(licensed, "Acquire organization Unity lock")
    for key, value in (
        ("github-token", "${{ github.token }}"),
        ("pull-request-number", "${{ github.event.pull_request.number }}"),
        ("expected-head-sha", "${{ github.event.pull_request.head.sha }}"),
    ):
        expected_line = f"          {key}: {value}\n"
        mutated_acquire = acquire_step.replace(expected_line, "", 1)
        require(
            mutated_acquire != acquire_step,
            f"{key}: acquire mutation target missing",
        )
        require_policy_mutation_rejected(
            source,
            acquire_step,
            mutated_acquire,
            f"acquire {key} binding",
        )

    push = re.search(
        r"^  push:\n(?P<body>.*?)(?=^  [A-Za-z0-9_-]+:\n)",
        source,
        re.MULTILINE | re.DOTALL,
    )
    require(push is not None, "missing push trigger")
    assert push is not None
    paths_ignore = re.search(
        r"^    paths-ignore:\n(?P<body>.*?)(?=^    [A-Za-z0-9_-]+:|\Z)",
        push.group("body"),
        re.MULTILINE | re.DOTALL,
    )
    require(paths_ignore is not None, "push trigger must use paths-ignore")
    assert paths_ignore is not None
    require(
        paths_ignore.group("body")
        == '      - "docs/architecture/performance.md"\n'
        '      - "docs/architecture/perf-baseline.csv"\n',
        "Unity push trigger must ignore only the two CI-generated performance files",
    )
    pull_request = re.search(
        r"^  pull_request:\n(?P<body>.*?)(?=^  [A-Za-z0-9_-]+:\n)",
        source,
        re.MULTILINE | re.DOTALL,
    )
    require(pull_request is not None, "missing pull_request trigger")
    assert pull_request is not None
    require(
        re.search(r"^    paths(?:-ignore)?:", pull_request.group("body"), re.MULTILINE)
        is None,
        "pull_request trigger must remain unfiltered by paths",
    )

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
    setup_guard = licensed.find("      - name: Require current PR head before setup\n")
    lock_guard = licensed.find("      - name: Require current PR head before lock acquisition\n")
    acquire = licensed.find("      - name: Acquire organization Unity lock\n")
    require(
        setup_guard >= 0 and setup_guard == licensed.find("      - name:"),
        "head guard must be first",
    )
    require(lock_guard >= 0 and acquire >= 0, "licensed job must guard lock acquisition")
    require(
        licensed.find("      - name:", lock_guard + 1) == acquire,
        "head guard must run immediately before lock acquisition",
    )
    for guard_name in (
        "Require current PR head before setup",
        "Require current PR head before lock acquisition",
    ):
        guard = step_block(licensed, guard_name)
        require(f"uses: {CURRENT_PR_HEAD_GUARD}" in guard, f"{guard_name}: guard pin drifted")
        for expected_input in (
            "github-token: ${{ github.token }}",
            "pull-request-number: ${{ github.event.pull_request.number }}",
            "expected-head-sha: ${{ github.event.pull_request.head.sha }}",
        ):
            require(expected_input in guard, f"{guard_name}: missing {expected_input}")

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
