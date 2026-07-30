#!/usr/bin/env python3
"""Validate trusted-PR Unity admission, staleness guards, and skip policy."""

from __future__ import annotations

import os
import re
import subprocess
import tempfile
from pathlib import Path


WORKFLOW = Path(".github/workflows/unity-tests.yml")
LOCK_ACTION_PREFIX = "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/"
REGISTERED_UNITY_AUTOMATION = {
    ".github/actions/validate-unity-license/action.yml",
    ".github/workflows/perf-numbers.yml",
    ".github/workflows/release.yml",
    ".github/workflows/unity-benchmarks.yml",
    ".github/workflows/unity-tests.yml",
}
UNITY_CREDENTIAL_OR_ACTIVATION = re.compile(
    r"\bUNITY_(?:SERIAL|EMAIL|PASSWORD|LICENSE|LICENSING_SERVER)\b|"
    r"game-ci/unity-(?:test-runner|builder|activate)@",
    re.IGNORECASE,
)
SAME_REPOSITORY_PR_GUARD = re.compile(
    r"github\.event_name\s*!=\s*'pull_request'\s*\|\|\s*\(\s*"
    r"github\.event\.pull_request\.user\.login\s*!=\s*'dependabot\[bot\]'\s*&&\s*"
    r"github\.event\.pull_request\.head\.repo\.full_name\s*==\s*github\.repository"
)
# Dependabot jobs read from the separate Dependabot secret store, so the
# organization Unity serial and build-lock App secrets resolve empty there. The
# licensed jobs must exclude Dependabot the same way they exclude forks.
DEPENDABOT_PR_GUARD = re.compile(
    r"github\.event_name\s*!=\s*'pull_request'\s*\|\|\s*\(?\s*"
    r"github\.event\.pull_request\.user\.login\s*!=\s*'dependabot\[bot\]'"
)
BLANKET_PR_REJECTION = re.compile(
    r"github\.event_name\s*!=\s*'pull_request'\s*&&"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def resolve_action_use(action: str) -> str:
    """Return the one `<action>@<sha> # <version>` reference the workflow uses.

    The SHA is derived from the pristine workflow rather than written here.
    Dependabot bumps these pins, and a literal turned every dependency update
    into a policy failure unrelated to the change under review. What the policy
    needs is that the pin is immutable and identical at every call site, which
    is what this resolves and asserts.
    """
    source = WORKFLOW.read_text(encoding="utf-8")
    uses = {
        match.group(1).rstrip()
        for match in re.finditer(
            rf"uses: ({re.escape(LOCK_ACTION_PREFIX + action)}@[0-9a-f]{{40}}(?:[ \t]+#[^\n]*)?)",
            source,
        )
    }
    require(
        len(uses) == 1,
        f"{action}: pin must be immutable and identical everywhere; found {sorted(uses)}",
    )
    return uses.pop()


def mutate_pin_sha(use: str) -> str:
    """Flip one SHA character so a pinned reference stops matching the policy."""
    match = re.search(r"@([0-9a-f]{40})", use)
    require(match is not None, "pinned reference must carry a 40-character SHA")
    assert match is not None
    sha = match.group(1)
    flipped = sha[:-1] + ("0" if sha[-1] != "0" else "1")
    return use[: match.start(1)] + flipped + use[match.end(1) :]


CURRENT_PR_HEAD_GUARD = resolve_action_use("require-current-pr-head")
ACQUIRE_BUILD_LOCK = resolve_action_use("acquire-build-lock")


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
            rf"^        uses: {re.escape(ACQUIRE_BUILD_LOCK)}[ \t]*$",
            acquire,
            re.MULTILINE,
        )
        == [f"        uses: {ACQUIRE_BUILD_LOCK}"],
        "Unity acquire step must use the exact immutable action pin",
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
            "github.event_name!='pull_request'||("
            "github.event.pull_request.user.login!='dependabot[bot]'&&"
            "github.event.pull_request.head.repo.full_name==github.repository"
        )
        is not None,
        "same-repository guard parser must accept compact operators",
    )
    require(
        BLANKET_PR_REJECTION.search("github.event_name!='pull_request'&&(") is not None,
        "blanket rejection parser must detect compact operators",
    )
    require(
        DEPENDABOT_PR_GUARD.search(
            "github.event_name != 'pull_request' || "
            "(github.event.pull_request.user.login != 'dependabot[bot]' && trusted)"
        )
        is not None,
        "Dependabot guard parser must require PR-only exclusion",
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
        f"        uses: {ACQUIRE_BUILD_LOCK}\n",
        f"        uses: {mutate_pin_sha(ACQUIRE_BUILD_LOCK)}\n",
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
        DEPENDABOT_PR_GUARD.search(licensed) is not None,
        "Unity job must exclude Dependabot PRs, which cannot read the licensed secrets",
    )
    require(
        DEPENDABOT_PR_GUARD.search(job_block(source, "runner-preflight")) is not None,
        "runner preflight must exclude Dependabot PRs, which cannot read the reader App secrets",
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
    require(
        gate.count("\n      - name:") == 1,
        "aggregate must contain exactly one validation step",
    )
    aggregate_step = step_block(gate, "Verify Unity CI result shape")
    require("        shell: bash\n" in aggregate_step, "aggregate must use bash")
    expected_bindings = {
        "RUNNER_PREFLIGHT_RESULT": "${{ needs.runner-preflight.result }}",
        "UNITY_TESTS_RESULT": "${{ needs.unity-tests.result }}",
        "FORK_PR": (
            "${{ github.event_name == 'pull_request' && "
            "github.event.pull_request.head.repo.full_name != github.repository }}"
        ),
        "DEPENDABOT_PR": (
            "${{ github.event_name == 'pull_request' && "
            "github.event.pull_request.user.login == 'dependabot[bot]' }}"
        ),
    }
    for variable, value in expected_bindings.items():
        require(
            re.findall(rf"^          {variable}:.*$", aggregate_step, re.MULTILINE)
            == [f"          {variable}: {value}"],
            f"aggregate must bind exact {variable}",
        )

    script = run_script(aggregate_step)
    expected_script = """set -euo pipefail
if [ "${FORK_PR}" = "true" ] || [ "${DEPENDABOT_PR}" = "true" ]; then
  test "${RUNNER_PREFLIGHT_RESULT}" = skipped
  test "${UNITY_TESTS_RESULT}" = skipped
else
  test "${RUNNER_PREFLIGHT_RESULT}" = success
  test "${UNITY_TESTS_RESULT}" = success
fi"""
    require(script == expected_script, "aggregate result-shape script drifted")
    # name, preflight, unity, FORK_PR, DEPENDABOT_PR, exit code
    cases = (
        ("same-repository PR", "success", "success", "false", "false", 0),
        ("fork PR", "skipped", "skipped", "true", "false", 0),
        ("Dependabot PR", "skipped", "skipped", "false", "true", 0),
        ("same-repository PR skipped Unity", "success", "skipped", "false", "false", 1),
        ("fork unexpectedly ran Unity", "skipped", "success", "true", "false", 1),
        ("Dependabot unexpectedly ran Unity", "skipped", "success", "false", "true", 1),
        ("Dependabot unexpectedly ran preflight", "success", "skipped", "false", "true", 1),
    )
    if os.name != "nt":
        for name, preflight, unity, fork, dependabot, expected in cases:
            environment = os.environ.copy()
            environment.update(
                {
                    "RUNNER_PREFLIGHT_RESULT": preflight,
                    "UNITY_TESTS_RESULT": unity,
                    "FORK_PR": fork,
                    "DEPENDABOT_PR": dependabot,
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
