#!/usr/bin/env python3
"""Validate trusted-PR Unity admission, staleness guards, and skip policy."""

from __future__ import annotations

import base64
import json
import os
import re
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path


WORKFLOW = Path(".github/workflows/unity-tests.yml")
WATCHDOG = Path(".github/workflows/stuck-job-watchdog.yml")
LOCK_ACTION_PREFIX = "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/"
REGISTERED_UNITY_AUTOMATION = {
    ".github/actions/validate-unity-license/action.yml",
    ".github/workflows/perf-numbers.yml",
    ".github/workflows/release.yml",
    ".github/workflows/unity-benchmarks.yml",
    ".github/workflows/unity-tests.yml",
}
# SYNC: Keep scripts/__tests__/ci-aggregate-workflow.test.js UNITY_LOCK_WINDOWS aligned.
LICENSED_LOCK_WINDOWS = (
    (Path(".github/workflows/unity-tests.yml"), "unity-tests"),
    (Path(".github/workflows/unity-benchmarks.yml"), "benchmarks"),
    (Path(".github/workflows/perf-numbers.yml"), "perf-benchmarks"),
    (Path(".github/workflows/release.yml"), "unity-checks"),
    (Path(".github/workflows/release.yml"), "unitypackage"),
)
UNITY_LIFECYCLE_OVERHEAD_RESERVE_MINUTES = 60
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
# A pull request whose head has moved on must not schedule the licensed matrix:
# `cancel-in-progress: false` is deliberate, so a superseded run would otherwise
# hold the concurrency group through all nine legs while the current head waits.
SUPERSEDED_GUARD = re.compile(
    r"needs\.head-check\.outputs\.superseded\s*!=\s*'true'\s*&&"
)
WORKFLOW_EDITOR_MUTATION = re.compile(
    r"^\s*(?:"
    r"(?:Start-Process(?:\s+-FilePath)?|&|cmd(?:\.exe)?\s+/[ck])\s+"
    r"['\"]?(?:[^'\"\s]*[\\/])?unity(?:\.exe)?['\"]?(?=\s|$).*$"
    r"|['\"]?(?:[^'\"\s]*[\\/])?unity(?:\.exe)?['\"]?(?=\s|$)"
    r"[^\n]*\b(?:install|install-modules|uninstall)\b.*$"
    r"|(?:(?:Start-Process(?:\s+-FilePath)?|&|cmd(?:\.exe)?\s+/[ck])\s+)?"
    r"\$[A-Za-z_][A-Za-z0-9_]*(?=\s|$)[^\n]*"
    r"\b(?:install|install-modules|uninstall)\b.*$"
    r")",
    re.IGNORECASE | re.MULTILINE,
)
YAML_RUN_EXTRACTOR = r"""
const fs = require("fs");
const YAML = require("yaml");
const runs = [];
function resolved(node, document) {
  return YAML.isAlias(node) ? node.resolve(document) : node;
}
function visit(node, document) {
  node = resolved(node, document);
  if (YAML.isMap(node)) {
    for (const pair of node.items) {
      const value = resolved(pair.value, document);
      if (YAML.isScalar(pair.key) && pair.key.value === "run" && YAML.isScalar(value)) {
        runs.push(String(value.value ?? ""));
      }
      visit(value, document);
    }
  } else if (YAML.isSeq(node)) {
    for (const item of node.items) visit(item, document);
  }
}
for (const document of YAML.parseAllDocuments(fs.readFileSync(0, "utf8"), { uniqueKeys: false })) {
  if (document.errors.length) throw document.errors[0];
  visit(document.contents, document);
}
process.stdout.write(JSON.stringify(runs));
"""


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


def top_level_steps_through_cleanup_gate(job: str, label: str) -> list[str]:
    steps_start = job.find("    steps:\n")
    require(steps_start >= 0, f"{label}: missing steps")
    starts = [
        match.start()
        for match in re.finditer(r"^      -(?: |$)", job[steps_start:], re.MULTILINE)
    ]
    starts = [steps_start + start for start in starts]
    require(bool(starts), f"{label}: missing top-level steps")
    blocks = [
        job[start : starts[index + 1] if index + 1 < len(starts) else len(job)]
        for index, start in enumerate(starts)
    ]
    gate_indexes = [
        index
        for index, block in enumerate(blocks)
        if block.startswith("      - name: Require confirmed Unity cleanup\n")
    ]
    require(
        len(gate_indexes) == 1,
        f"{label}: expected one confirmed-cleanup gate, found {len(gate_indexes)}",
    )
    return blocks[: gate_indexes[0] + 1]


def positive_timeout(source: str, indentation: int, label: str) -> int:
    matches = re.findall(
        rf"^{' ' * indentation}timeout-minutes: ([1-9]\d*)[ \t]*$",
        source,
        re.MULTILINE,
    )
    require(
        len(matches) == 1,
        f"{label}: expected one positive integer timeout, found {matches}",
    )
    return int(matches[0])


def containing_named_step(source: str, offset: int) -> str:
    starts = [
        (match.start(), len(match.group("indent")))
        for match in re.finditer(
            r"^(?P<indent>[ \t]*)- name: .+$",
            source,
            re.MULTILINE,
        )
        if match.start() <= offset
    ]
    require(bool(starts), "automation call must be inside a named step")
    start, indentation = starts[-1]
    following = re.search(
        rf"^{' ' * indentation}- name: .+$",
        source[offset:],
        re.MULTILINE,
    )
    end = offset + following.start() if following else len(source)
    return source[start:end]


def executable_powershell(source: str) -> str:
    lines: list[str] = []
    block_comment = False
    here_terminator: str | None = None
    for line in source.splitlines():
        if here_terminator is not None:
            if line.strip() == here_terminator:
                here_terminator = None
            continue
        executable: list[str] = []
        quote: str | None = None
        index = 0
        while index < len(line):
            if block_comment:
                end = line.find("#>", index)
                if end < 0:
                    break
                block_comment = False
                index = end + 2
                continue
            character = line[index]
            if character == "`":
                executable.append(line[index : index + 2])
                index += 2
                continue
            if quote is not None:
                executable.append(character)
                if character == quote:
                    if (
                        quote == "'"
                        and index + 1 < len(line)
                        and line[index + 1] == "'"
                    ):
                        executable.append("'")
                        index += 2
                        continue
                    quote = None
                index += 1
                continue
            if line.startswith("<#", index):
                block_comment = True
                index += 2
                continue
            if character == "#":
                break
            executable.append(character)
            if character in {"'", '"'}:
                quote = character
            index += 1
        code = "".join(executable).rstrip()
        here_start = re.search(r"@(?P<quote>['\"])\s*$", code)
        if here_start is not None:
            here_terminator = here_start.group("quote") + "@"
            code = code[: here_start.start()] + "''"
        if code.strip():
            lines.append(code)
    return re.sub(r"`\s*\n\s*", " ", "\n".join(lines))


def powershell_syntax(source: str) -> str:
    """Remove string contents so diagnostics cannot satisfy command guards."""
    syntax: list[str] = []
    quote: str | None = None
    index = 0
    while index < len(source):
        character = source[index]
        if character == "`":
            syntax.extend("  ")
            index += 2
            continue
        if quote is not None:
            if character == quote:
                if (
                    quote == "'"
                    and index + 1 < len(source)
                    and source[index + 1] == "'"
                ):
                    syntax.extend("  ")
                    index += 2
                    continue
                quote = None
            syntax.append(" ")
            index += 1
            continue
        if character in {"'", '"'}:
            quote = character
            syntax.append(" ")
        else:
            syntax.append(character)
        index += 1
    return "".join(syntax)


def workflow_run_scripts(source: str) -> list[str]:
    """Return every YAML run scalar, including flow-style and duplicate keys."""
    result = subprocess.run(
        ["node", "-e", YAML_RUN_EXTRACTOR],
        input=source,
        text=True,
        capture_output=True,
        check=False,
    )
    require(
        result.returncode == 0,
        "could not parse workflow YAML while enforcing editor mutation policy: "
        + result.stderr.strip(),
    )
    scripts = json.loads(result.stdout)
    require(
        isinstance(scripts, list) and all(isinstance(item, str) for item in scripts),
        "workflow YAML run extractor returned an invalid payload",
    )
    return scripts


def has_positive_switch(invocation: str, switch: str) -> bool:
    syntax = powershell_syntax(invocation)
    return (
        re.search(
            rf"-{re.escape(switch)}(?:"
            rf"\s*:\s*\$true(?=\s|`|$)"
            rf"|(?=\s|`|$)"
            rf")",
            syntax,
            re.IGNORECASE,
        )
        is not None
        and re.search(
            rf"-{re.escape(switch)}\s*:\s*(?!\$true(?=\s|`|$))\S+",
            syntax,
            re.IGNORECASE,
        )
        is None
    )


def has_positive_detect_only(source: str) -> bool:
    return has_positive_switch(source, "DetectOnly") or (
        re.search(
            r"^\s*DetectOnly\s*=\s*\$true\s*$",
            source,
            re.IGNORECASE | re.MULTILINE,
        )
        is not None
    )


def has_nonpositive_detect_only(source: str) -> bool:
    return (
        re.search(
            r"(?:"
            r"-DetectOnly\s*:\s*(?!\$true(?=\s|$))\S+"
            r"|^\s*(?:"
            r"DetectOnly"
            r"|\$[A-Za-z_][A-Za-z0-9_]*\.DetectOnly"
            r"|\$[A-Za-z_][A-Za-z0-9_]*\[['\"]DetectOnly['\"]\]"
            r")\s*=\s*(?!\$true\s*$)\S.*$"
            r")",
            source,
            re.IGNORECASE | re.MULTILINE,
        )
        is not None
    )


def find_workflow_editor_mutations(files: dict[str, str]) -> list[str]:
    violations: list[str] = []
    for file, source in files.items():
        for run_script_source in workflow_run_scripts(source):
            executable_source = executable_powershell(run_script_source)
            executable_syntax = powershell_syntax(executable_source)
            if re.search(
                r"\b(?:Microsoft\.PowerShell\.Utility\\)?"
                r"(?:Invoke-Expression|iex)\b",
                executable_syntax,
                re.IGNORECASE,
            ):
                violations.append(
                    f"{file}: Invoke-Expression is forbidden in workflow run bodies"
                )
            if re.search(
                r"\bGet-Command\b[^\n]*\bunity(?:\.exe)?\b[^\n]*"
                r"\b(?:install|install-modules|uninstall)\b",
                executable_syntax,
                re.IGNORECASE | re.MULTILINE,
            ):
                violations.append(
                    f"{file}: resolved Unity editor mutation command"
                )
            if re.search(
                r"(?:&|\.|Start-Process|saps|start)\s+"
                r"\((?:Get-Command|gcm)\b",
                executable_syntax,
                re.IGNORECASE,
            ):
                violations.append(
                    f"{file}: dynamic resolved-command invocation is forbidden"
                )
            literal_unity_mutation = re.search(
                r"\bunity(?:\.exe)?\b[^\n]*"
                r"\b(?:install|install-modules|uninstall)\b",
                executable_syntax,
                re.IGNORECASE | re.MULTILINE,
            ) is not None
            if re.search(
                r"\b(?:pwsh|powershell)(?:\.exe)?\b[^\n]*"
                r"-Command\s+['\"][^'\"]*\bunity(?:\.exe)?\b"
                r"[^'\"]*\b(?:install|install-modules|uninstall)\b",
                executable_source,
                re.IGNORECASE | re.MULTILINE,
            ):
                violations.append(
                    f"{file}: nested Unity editor mutation command"
                )
            if re.search(
                r"\b(?:Start-Process|saps|start)\b[^\n]*"
                r"\b(?:pwsh|powershell)(?:\.exe)?\b[^\n]*"
                r"\bunity(?:\.exe)?\b[^\n]*"
                r"\b(?:install|install-modules|uninstall)\b",
                executable_source,
                re.IGNORECASE | re.MULTILINE,
            ):
                violations.append(
                    f"{file}: nested Unity editor mutation process"
                )
            ensure_mentions = re.findall(
                r"ensure-editor\.ps1\b",
                executable_source,
                re.IGNORECASE,
            )
            if ensure_mentions:
                if len(ensure_mentions) != 1:
                    violations.append(
                        f"{file}: editor validation must reference "
                        "ensure-editor.ps1 exactly once per run body"
                    )
                ensure_line = next(
                    line
                    for line in executable_source.splitlines()
                    if re.search(
                        r"ensure-editor\.ps1\b",
                        line,
                        re.IGNORECASE,
                    )
                )
                ensure_syntax = powershell_syntax(ensure_line)
                for switch in ("RequireHealthyExisting", "CiManagedOnly"):
                    if not has_positive_switch(ensure_syntax, switch):
                        violations.append(
                            f"{file}: ensure-editor call missing positive -{switch}"
                        )
                if "-ProvisioningProfile" not in ensure_syntax:
                    violations.append(
                        f"{file}: ensure-editor call missing -ProvisioningProfile"
                    )
                if re.search(
                    r"-InstallRoot\s+\(\s*Join-Path\s+"
                    r"\$env:RUNNER_TOOL_CACHE\s+['\"]u6-v3['\"]\s*\)",
                    ensure_line,
                    re.IGNORECASE,
                ) is None:
                    violations.append(
                        f"{file}: ensure-editor call missing canonical "
                        "RUNNER_TOOL_CACHE/u6-v3 -InstallRoot"
                    )
            direct_mutation = (
                WORKFLOW_EDITOR_MUTATION.search(executable_source) is not None
                or literal_unity_mutation
            )
            if direct_mutation:
                violations.append(f"{file}: direct Unity editor mutation command")
            if not direct_mutation:
                for variable_call in re.finditer(
                    r"(?:"
                    r"(?P<operator>&|\.)"
                    r"|Start-Process"
                    r"|saps"
                    r"|start"
                    r"|cmd(?:\.exe)?\s+/[ck]"
                    r")\s+(?:-FilePath\s+)?\$(?:"
                    r"\{(?P<braced>[A-Za-z_][A-Za-z0-9_]*)\}"
                    r"|(?P<plain>[A-Za-z_][A-Za-z0-9_]*)"
                    r")\b(?P<arguments>.*)$",
                    executable_syntax,
                    re.IGNORECASE | re.MULTILINE,
                ):
                    variable = (
                        variable_call.group("braced")
                        or variable_call.group("plain")
                    )
                    assignment = re.search(
                        rf"^\s*\${re.escape(variable)}\s*=.*"
                        r"(?:maintain|bootstrap)-windows-runner\.ps1.*$",
                        executable_source,
                        re.IGNORECASE | re.MULTILINE,
                    )
                    dot_sources_validated_function = (
                        variable_call.group("operator") == "."
                        and assignment is not None
                        and re.search(
                            r"\bInvoke-WindowsRunner(?:Maintenance|Bootstrap)\b",
                            executable_source,
                            re.IGNORECASE,
                        )
                        is not None
                    )
                    approved_detect_only_call = (
                        assignment is not None
                        and has_positive_detect_only(
                            variable_call.group("arguments")
                        )
                        and not has_nonpositive_detect_only(
                            variable_call.group("arguments")
                        )
                    )
                    if not (
                        dot_sources_validated_function
                        or approved_detect_only_call
                    ):
                        violations.append(
                            f"{file}: variable command invocation is not an "
                            "approved detect-only runner audit"
                        )
            for script, function in (
                (
                    "maintain-windows-runner.ps1",
                    "Invoke-WindowsRunnerMaintenance",
                ),
                (
                    "bootstrap-windows-runner.ps1",
                    "Invoke-WindowsRunnerBootstrap",
                ),
            ):
                script_mentions = list(
                    re.finditer(
                        rf"{re.escape(script)}\b",
                        executable_source,
                        re.IGNORECASE,
                    )
                )
                function_mentions = list(
                    re.finditer(
                        rf"{re.escape(function)}\b",
                        executable_source,
                        re.IGNORECASE,
                    )
                )
                if not script_mentions and not function_mentions:
                    continue
                if len(script_mentions) > 1 or len(function_mentions) > 1:
                    violations.append(
                        f"{file}: {script} mutation surface appears more than once"
                    )
                    continue
                if (
                    not has_positive_detect_only(executable_source)
                    or has_nonpositive_detect_only(executable_source)
                ):
                    violations.append(
                        f"{file}: {script} call is not detect-only"
                    )
                    continue
                if function_mentions:
                    function_call = re.search(
                        rf"^\s*(?:\$[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?"
                        rf"{re.escape(function)}\b(?P<arguments>.*)$",
                        executable_source,
                        re.IGNORECASE | re.MULTILINE,
                    )
                    if function_call is None:
                        violations.append(
                            f"{file}: {function} is referenced but not invoked directly"
                        )
                        continue
                    arguments = function_call.group("arguments")
                    function_is_detect_only = (
                        has_positive_switch(arguments, "DetectOnly")
                        and not has_nonpositive_detect_only(arguments)
                    )
                    splat = re.search(
                        r"@(?P<variable>[A-Za-z_][A-Za-z0-9_]*)\b",
                        arguments,
                    )
                    if splat is not None:
                        variable = splat.group("variable")
                        hashtable = re.search(
                            rf"^\s*\${re.escape(variable)}\s*=\s*@\{{"
                            rf"(?P<body>.*?)^\s*\}}",
                            executable_source,
                            re.IGNORECASE | re.MULTILINE | re.DOTALL,
                        )
                        function_is_detect_only = (
                            hashtable is not None
                            and has_positive_detect_only(hashtable.group("body"))
                            and not has_nonpositive_detect_only(
                                hashtable.group("body")
                            )
                            and executable_source[
                                hashtable.end() : function_call.start()
                            ].strip()
                            == ""
                            and len(
                                re.findall(
                                    rf"\${re.escape(variable)}\b",
                                    executable_source,
                                    re.IGNORECASE,
                                )
                            )
                            == 1
                        )
                    if not function_is_detect_only:
                        violations.append(
                            f"{file}: {function} invocation is not detect-only"
                        )
                        continue
                assignment = re.search(
                    rf"^\s*\$(?P<variable>[A-Za-z_][A-Za-z0-9_]*)\s*=.*"
                    rf"{re.escape(script)}.*$",
                    executable_source,
                    re.IGNORECASE | re.MULTILINE,
                )
                if assignment is None:
                    continue
                variable = assignment.group("variable")
                calls = list(
                    re.finditer(
                        rf"^\s*(?:"
                        rf"&\s+\${re.escape(variable)}\b"
                        rf"|(?:pwsh|powershell)(?:\.exe)?\b"
                        rf"[^\n]*?-(?:File|Command)\b[^\n]*"
                        rf"\${re.escape(variable)}\b"
                        rf").*$",
                        executable_source,
                        re.IGNORECASE | re.MULTILINE,
                    )
                )
                if len(calls) > 1:
                    violations.append(
                        f"{file}: {script} assigned command is invoked more than once"
                    )
                for call in calls:
                    call_line = call.group(0)
                    if (
                        not has_positive_detect_only(call_line)
                        or has_nonpositive_detect_only(call_line)
                    ):
                        violations.append(
                            f"{file}: assigned {script} call is not detect-only"
                        )
    return violations


def validate_cleanup_gate_not_attempted_input(job: str, label: str) -> None:
    """Pin how a lock window tells the cleanup gate that acquisition never ran.

    The gate's contract is that `acquired: false` proves licensed cleanup was
    not required, and a bare `if: always()` is what catches a seat leak when
    acquisition fails part-way. Both have to hold at once (#327): a leg that
    aborts before `Acquire organization Unity lock` must report exactly one
    failure, and a leg whose acquire failed after taking the lock must still
    fail. The distinction is `outcome`, which is empty or `skipped` only when
    the step did not execute; gating on `acquired == 'true'` instead would
    skip the gate in precisely the case it must never miss.
    """
    gate = step_block(job, "Require confirmed Unity cleanup")
    require(
        "\n        if: always()\n" in gate,
        f"{label}: the cleanup gate must keep a bare `if: always()`",
    )
    acquired = re.search(r"\n          acquired: (.*)\n", gate)
    require(acquired is not None, f"{label}: the cleanup gate must pass `acquired`")
    expression = acquired.group(1)
    for fragment in (
        "steps.acquire_lock.outcome == 'skipped'",
        "steps.acquire_lock.outcome == ''",
        "&& 'false' ||",
        "steps.acquire_lock.outputs.acquired",
    ):
        require(
            fragment in expression,
            f"{label}: the cleanup gate's `acquired` input must map only a step that "
            f"never executed to 'false' and pass everything else through; missing {fragment!r}",
        )
    require(
        "outcome == 'failure'" not in expression and "outcome != " not in expression,
        f"{label}: a failed acquire must not be laundered into a not-attempted verdict",
    )


def validate_lock_window_timeout_budget(job: str, label: str) -> None:
    steps = top_level_steps_through_cleanup_gate(job, label)
    bounded_minutes = 0
    for index, step in enumerate(steps, start=1):
        name = re.search(r"^      - name: (.+)$", step, re.MULTILINE)
        step_label = name.group(1) if name else f"step {index}"
        bounded_minutes += positive_timeout(step, 8, f"{label}:{step_label}")

    acquire_steps = [step for step in steps if f"uses: {ACQUIRE_BUILD_LOCK}" in step]
    require(
        len(acquire_steps) == 1,
        f"{label}: expected one acquire step, found {len(acquire_steps)}",
    )
    acquire = acquire_steps[0]
    acquire_wait = re.findall(
        r'^          timeout-minutes: "([1-9]\d*)"[ \t]*$',
        acquire,
        re.MULTILINE,
    )
    require(
        len(acquire_wait) == 1,
        f"{label}: expected one positive acquire wait, found {acquire_wait}",
    )
    require(
        positive_timeout(acquire, 8, f"{label}:acquire step") > int(acquire_wait[0]),
        f"{label}: acquire step timeout must exceed its internal wait",
    )

    job_timeout = positive_timeout(job, 4, f"{label}:job")
    require(
        job_timeout
        >= bounded_minutes + UNITY_LIFECYCLE_OVERHEAD_RESERVE_MINUTES,
        f"{label}: job timeout {job_timeout} must reserve at least "
        f"{UNITY_LIFECYCLE_OVERHEAD_RESERVE_MINUTES} minutes beyond the "
        f"{bounded_minutes}-minute bounded lifecycle",
    )


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


def run_head_check(script: str, event: str, live_head: str) -> str:
    """Run the head-freshness script against a stub `gh` and return its decision."""
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        stub = root / "gh"
        # The stub answers only the live-head lookup, so a script that asked a
        # different endpoint gets nothing and the truth table below fails. An
        # empty live head stands for a lookup that failed, so the stub exits
        # non-zero the way the real CLI does rather than printing nothing.
        stub.write_text(
            "#!/bin/sh\n"
            'case "$*" in\n'
            "  *repos/Ambiguous-Interactive/DxMessaging/pulls/1*.head.sha*) ;;\n"
            '  *) echo "unexpected gh invocation: $*" >&2; exit 2 ;;\n'
            "esac\n"
            f'[ -n "{live_head}" ] || exit 1\necho "{live_head}"\n',
            encoding="utf-8",
        )
        stub.chmod(0o755)
        output = root / "output"
        output.touch()
        environment = os.environ.copy()
        environment.update(
            {
                "PATH": f"{root}{os.pathsep}{environment['PATH']}",
                "GH_TOKEN": "stub",
                "EVENT_NAME": event,
                "PR_NUMBER": "1",
                "EVENT_HEAD_SHA": "current",
                "GITHUB_REPOSITORY": "Ambiguous-Interactive/DxMessaging",
                "GITHUB_OUTPUT": str(output),
            }
        )
        result = subprocess.run(
            ["bash", "-c", script], env=environment, capture_output=True, text=True, check=False
        )
        require(result.returncode == 0, f"head-check script failed: {result.stderr}")
        written = output.read_text(encoding="utf-8").strip()
        require(
            written.startswith("superseded="), f"head-check wrote no decision: {written!r}"
        )
        return written[len("superseded=") :]


def watchdog_gh_stub(routes: dict[str, tuple[int, object]], cancel_ok: bool) -> str:
    """A `gh` that answers only the stubbed endpoints and rejects the rest.

    An unstubbed call exits 3 rather than printing nothing, so a watchdog that
    reaches for an endpoint this table does not model fails the case instead of
    quietly reading an empty body.
    """
    # A `str` payload is emitted verbatim rather than JSON-encoded. The audit
    # pipes some calls through `gh --jq`, which this stub does not implement, so
    # a route that feeds such a call has to supply the already-extracted value.
    cases = "".join(
        f"  *{needle}*)\n    cat <<'PAYLOAD'\n"
        f"{payload if isinstance(payload, str) else (json.dumps(payload) if payload is not None else '')}"
        f"\nPAYLOAD\n    exit {code} ;;\n"
        for needle, (code, payload) in routes.items()
    )
    # Organization endpoints answer ONLY when the call carries the reader App
    # token. A static grep can prove the token is minted; this proves it is the
    # one actually used, which is the credential shape #328 was about -- the
    # audit read inventory with a token that could never reach those endpoints.
    return (
        "#!/bin/bash\n"
        'if [ "$1" = "run" ] && [ "$2" = "cancel" ]; then\n'
        f'  echo "cancelled $3"; exit {0 if cancel_ok else 1}\n'
        "fi\n"
        'case "$*" in\n'
        "  *orgs/*)\n"
        '    if [ "${GH_TOKEN:-}" != "stub-reader" ]; then\n'
        '      echo "gh: Resource not accessible by integration (HTTP 403)" >&2\n'
        "      exit 1\n"
        "    fi ;;\n"
        "esac\n"
        f'case "$*" in\n{cases}  *) echo "unstubbed gh: $*" >&2; exit 3 ;;\nesac\n'
    )


# The watchdog never needs a real remote: `ls-remote` reporting an empty branch
# list sends it down the bootstrap path, and `push` succeeds. Everything else
# (init, checkout, add, commit) runs against the real git in a temp directory.
def watchdog_git_stub(
    push_ok: bool = True,
    ls_remote_ok: bool = True,
    existing_state: dict[int, int] | None = None,
    clone_ok: bool = True,
    state_age_seconds: int = 3600,
) -> str:
    """A `git` that passes through except for the three remote operations.

    `existing_state` makes `ls-remote` report the branch as PRESENT and has
    `clone` materialize it with the given per-run cancel counts. Without it the
    stub could only ever exercise the bootstrap path, which left both the
    clone-failure guard and the cancel cap unreachable from the suite.
    """
    if existing_state is None:
        ls_remote = f"exit {0 if ls_remote_ok else 1}"
        clone = "exit 1"
    else:
        ls_remote = (
            f"echo 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef\trefs/heads/state'; "
            f"exit {0 if ls_remote_ok else 1}"
        )
        if clone_ok:
            # `last_cancel` defaults to an hour ago, inside the 24h reset
            # window; `state_age_seconds` moves it outside so the reset itself
            # becomes reachable. A far-future stamp would make elapsed time
            # negative, and then no window size could change the verdict at all.
            writes = "".join(
                f"printf '{{\"cancels\": {n}, \"last_cancel\": "
                f"'$(( $(date -u +%s) - {state_age_seconds} ))'}}' "
                f'> "$target/.watchdog-state/{run_id}.json"; '
                for run_id, n in existing_state.items()
            )
            clone = (
                'target="${@: -1}"; /usr/bin/git init -q "$target"; '
                'mkdir -p "$target/.watchdog-state"; '
                f"{writes}"
                '/usr/bin/git -C "$target" add -A; '
                '/usr/bin/git -C "$target" -c user.email=t@t -c user.name=t commit -qm state; '
                "exit 0"
            )
        else:
            clone = "exit 1"
    return (
        "#!/bin/bash\n"
        'for a in "$@"; do\n'
        '  case "$a" in\n'
        f"    ls-remote) {ls_remote} ;;\n"
        "    fetch) exit 0 ;;\n"
        f"    clone) {clone} ;;\n"
        f'    push) echo "pushed"; exit {0 if push_ok else 1} ;;\n'
        "  esac\n"
        "done\n"
        'exec /usr/bin/git "$@"\n'
    )


# Values the runner supplies from the workflow context; everything else in the
# step's `env:` is a literal the workflow OWNS, and the harness must read those
# from the file rather than restate them. Restating them is what makes a case
# assert its own stub: a truth table that hardcodes
# `DEFAULT_EXCLUDED_WORKFLOWS: release.yml` still passes after the workflow
# stops excluding release.yml.
WATCHDOG_CONTEXT_ENV = {
    "GH_TOKEN": "stub",
    "RUNNER_INVENTORY_TOKEN": "stub-reader",
    "REPO": "Ambiguous-Interactive/DxMessaging",
    "OWNER": "Ambiguous-Interactive",
    "SELF_RUN_ID": "999",
    "EXTRA_EXCLUDED_WORKFLOWS": "",
}
# Literals the cases below actually depend on. Each is exercised by at least one
# case, so a rename or deletion fails rather than silently dropping the behavior
# it configures. Adding a name here without a case that depends on its VALUE
# would restore the false guarantee this list used to advertise.
WATCHDOG_REQUIRED_ENV_LITERALS = (
    "STATE_BRANCH",
    "STATE_DIR",
    "MAX_CANCELS_PER_DAY",
    "MIN_QUEUE_AGE_SECONDS",
    "DEFAULT_EXCLUDED_WORKFLOWS",
)


def step_env_literals(step: str) -> dict[str, str]:
    """Return the step's `env:` entries whose values are workflow literals."""
    start = step.find("        env:\n")
    require(start >= 0, "watchdog audit step must declare env")
    literals: dict[str, str] = {}
    for line in step[start + len("        env:\n") :].splitlines():
        entry = re.fullmatch(r"          ([A-Z0-9_]+): (.*)", line)
        if entry is None:
            break
        name, value = entry.group(1), entry.group(2).strip()
        if "${{" in value:
            continue
        literals[name] = value[1:-1] if len(value) >= 2 and value[0] == value[-1] == '"' else value
    return literals


class WatchdogOutput(str):
    """Combined job-log + step-summary text, with the summary kept separately.

    `log_summary` tees to stdout, and the harness used to hand back
    `summary + stdout + stderr` as one blob -- so every needle was satisfiable
    from the job log alone and NO case could prove a line actually reached
    `GITHUB_STEP_SUMMARY`. Three of the four step-summary buckets and the whole
    audit narrative were deletable with the suite green. Behaves as the same
    string it always did; `.summary` is the channel-specific view.
    """

    summary: str

    def __new__(cls, combined: str, summary: str) -> "WatchdogOutput":
        value = super().__new__(cls, combined)
        value.summary = summary
        return value


def run_watchdog(
    script: str,
    environment_literals: dict[str, str],
    routes: dict[str, tuple[int, object]],
    cancel_ok: bool = True,
    push_ok: bool = True,
    ls_remote_ok: bool = True,
    existing_state: dict[int, int] | None = None,
    clone_ok: bool = True,
    state_age_seconds: int = 3600,
    break_date: bool = False,
    extra_env: dict[str, str] | None = None,
) -> tuple[int, str]:
    """Execute the watchdog audit script against a stubbed `gh` and `git`.

    `break_date` fails the unguarded `now_epoch="$(date ...)"` assignment, which
    is the cheapest way to force an abort the script does NOT anticipate -- the
    only thing that exercises the EXIT trap rather than a `finish` call.
    """
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        stubs = [
            ("gh", watchdog_gh_stub(routes, cancel_ok)),
            (
                "git",
                watchdog_git_stub(
                    push_ok, ls_remote_ok, existing_state, clone_ok, state_age_seconds
                ),
            ),
        ]
        if break_date:
            stubs.append(("date", "#!/bin/bash\nexit 1\n"))
        for name, body in stubs:
            stub = root / name
            stub.write_text(body, encoding="utf-8")
            stub.chmod(0o755)
        summary = root / "summary.md"
        summary.touch()
        environment = os.environ.copy()
        environment.update(environment_literals)
        environment.update(WATCHDOG_CONTEXT_ENV)
        environment.update(extra_env or {})
        environment["PATH"] = f"{root}{os.pathsep}{environment['PATH']}"
        environment["GITHUB_STEP_SUMMARY"] = str(summary)
        # The audit calls `mktemp` freely, which is free on an ephemeral runner
        # but permanent in a developer's /tmp -- roughly 440 entries per run of
        # this validator. Point it at the case's own directory instead.
        environment["TMPDIR"] = str(root)
        result = subprocess.run(
            ["bash", "-c", script], env=environment, capture_output=True, text=True, check=False
        )
        summary_text = summary.read_text(encoding="utf-8")
        return result.returncode, WatchdogOutput(
            summary_text + result.stdout + result.stderr, summary_text
        )


def watchdog_queued_runs(*runs: dict[str, object]) -> tuple[int, object]:
    return 0, {"workflow_runs": list(runs)}


def watchdog_run(
    run_id: int,
    workflow: str = "perf-numbers.yml",
    event: str = "push",
    created_at: str = "2020-01-01T00:00:00Z",
) -> dict[str, object]:
    # Created in 2020, so it is unconditionally older than MIN_QUEUE_AGE_SECONDS.
    return {
        "id": run_id,
        "created_at": created_at,
        "path": f".github/workflows/{workflow}",
        "event": event,
        "workflow_id": 42,
        "head_branch": "master",
        "html_url": f"https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/{run_id}",
    }


def watchdog_jobs(*label_sets: list[str], extra: list[dict] | None = None) -> tuple[int, object]:
    """Queued jobs carrying `label_sets`, plus any raw jobs in `extra`.

    `extra` exists so a case can put a job in a NON-queued state. Without it the
    fixture could only ever produce queued jobs, and the two guards that read job
    status -- the in-progress early-continue and the zero-queued-jobs check --
    were unreachable from the whole suite.
    """
    jobs = [{"status": "queued", "labels": labels} for labels in label_sets]
    return 0, {"jobs": jobs + list(extra or [])}


def watchdog_runners(*specs: tuple[str, str, bool, list[str]]) -> tuple[int, object]:
    return 0, {
        "runners": [
            {
                "id": index,
                "name": name,
                "status": status,
                "busy": busy,
                "labels": [{"name": label} for label in labels],
            }
            for index, (name, status, busy, labels) in enumerate(specs, start=1)
        ]
    }


# The redispatch branch reads the workflow file through `gh --jq .content`, which
# is base64. Encoding here rather than embedding the literal keeps the fixture
# readable and keeps a meaningless base64 blob out of the spell checker.
STARVED_SECTION_EMPTY = "required labels)\n_(none)_"


DISPATCHABLE_WORKFLOW_BODY = base64.b64encode(b"on:\n  workflow_dispatch:\n").decode()
# A body that decodes cleanly but declares no `workflow_dispatch` trigger. It
# is the only input that separates "detection said no" from "the base64 decode
# failed", which is what an empty `content` actually exercises.
NON_DISPATCHABLE_WORKFLOW_BODY = base64.b64encode(b"on:\n  push:\n").decode()


def validate_stuck_job_watchdog() -> None:
    """Execute the watchdog's audit script across its verdict space.

    The watchdog is the automation that recovers the licensed Unity legs this
    file governs, so its failure modes belong to the same policy: #328 was a
    watchdog that could not read runner inventory on either endpoint it tried,
    reported `success` anyway, and let a `Performance Numbers` run sit queued
    for ten hours. The rule these cases pin is that a green watchdog run means
    the queue was evaluated -- never that evaluation was skipped.
    """
    source = WATCHDOG.read_text(encoding="utf-8")
    require(
        "actions/create-github-app-token@" in source
        and "app-id: ${{ secrets.BUILD_LOCK_READER_APP_ID }}" in source,
        "watchdog must read runner inventory with the organization reader App; "
        "the job GITHUB_TOKEN is repository-scoped and cannot list organization runners",
    )
    require(
        "repos/${REPO}/actions/runners" not in source,
        "watchdog must not fall back to repository-level runners: this repository "
        "registers none, so the call succeeds with an empty inventory that reads "
        "identically to 'no runner matches' (#328)",
    )
    audit_step = step_block(job_block(source, "audit-queue"), "Audit + cancel-and-redispatch")
    script = run_script(audit_step)
    environment_literals = step_env_literals(audit_step)
    for name in WATCHDOG_REQUIRED_ENV_LITERALS:
        require(name in environment_literals, f"watchdog env must declare {name} as a literal")
    require(
        "release.yml" in environment_literals["DEFAULT_EXCLUDED_WORKFLOWS"].split(),
        "watchdog must never cancel a queued release run",
    )
    if os.name == "nt":
        return

    self_hosted = ["self-hosted", "Windows", "RAM-64GB", "fast"]
    queued = "actions/runs?status=queued"
    inventory = "runner-groups?visible_to_repository"
    one_group = (0, {"runner_groups": [{"id": 7, "name": "Default"}]})

    # name, gh routes, expected exit, must appear, must NOT appear
    FRESH_TIMESTAMP = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    STUCK_ROUTES = {
        queued: watchdog_queued_runs(watchdog_run(1)),
        inventory: one_group,
        "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
        "actions/runs/1/jobs": watchdog_jobs(self_hosted),
        "actions/workflows/42": (0, {"path": ".github/workflows/perf-numbers.yml"}),
        "contents/.github/workflows/perf-numbers.yml": (0, {"content": ""}),
    }

    cases: tuple[tuple[str, dict[str, tuple[int, object]], int, tuple[str, ...], tuple[str, ...]], ...] = (
        (
            "clean queue",
            {queued: (0, {"workflow_runs": []})},
            0,
            ("Queue is clean",),
            ("::error::",),
        ),
        (
            "unreadable queue fails closed",
            {queued: (1, None)},
            1,
            ("failed to list queued runs",),
            (),
        ),
        (
            "unreadable runner inventory fails closed",
            {queued: watchdog_queued_runs(watchdog_run(1)), inventory: (1, None)},
            1,
            ("could not read the organization runner groups",),
            (),
        ),
        (
            "empty visible runner-group set fails closed",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: (0, {"runner_groups": []}),
            },
            1,
            ("no runner groups visible",),
            (),
        ),
        (
            "unreadable jobs fail closed",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": (1, None),
            },
            1,
            ("failed to list jobs for run 1",),
            (),
        ),
        (
            "idle matching runner is dispatcher-stuck",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(self_hosted),
                "actions/workflows/42": (0, {"path": ".github/workflows/perf-numbers.yml"}),
                "contents/.github/workflows/perf-numbers.yml": (0, {"content": ""}),
            },
            0,
            ("dispatcher-stuck", "cancelled 1", "does not support workflow_dispatch"),
            ("re-dispatching",),
        ),
        (
            "busy matching runner is healthy backpressure",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", True, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(self_hosted),
            },
            0,
            ("healthy backpressure",),
            ("cancelled 1", "::warning::"),
        ),
        (
            "registered but offline runner is starved, not stuck",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "offline", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(self_hosted),
            },
            0,
            ("starved", "registered but offline", "::warning::"),
            ("cancelled 1", STARVED_SECTION_EMPTY),
        ),
        (
            "no registered self-hosted runner is starved, not stuck",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("MAC", "online", False, ["self-hosted", "macOS"])),
                "actions/runs/1/jobs": watchdog_jobs(self_hosted),
            },
            0,
            ("starved", "no runner registered", "::warning::"),
            ("cancelled 1", STARVED_SECTION_EMPTY),
        ),
        (
            "GitHub-hosted run is not reported as starved",
            {
                queued: watchdog_queued_runs(watchdog_run(1, workflow="ci.yml")),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(["ubuntu-latest"]),
            },
            0,
            ("no queued job requests a self-hosted runner",),
            ("cancelled 1", "::warning::"),
        ),
        (
            "label match is case-insensitive in both directions",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", True, ["Self-Hosted", "WINDOWS"])),
                "actions/runs/1/jobs": watchdog_jobs(["self-hosted", "windows"]),
            },
            0,
            ("healthy backpressure",),
            ("::warning::",),
        ),
        (
            "a job reporting no labels is never cancelled",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs([]),
            },
            0,
            ("queued jobs report no labels",),
            ("cancelled 1",),
        ),
        (
            # Finding: `mapfile < <(jq ...)` discarded jq's exit status, so one
            # unparseable timestamp printed "Queue is clean" over a stuck queue.
            "an unparseable queued-run timestamp fails closed",
            {
                queued: (
                    0,
                    {
                        "workflow_runs": [
                            watchdog_run(1) | {"created_at": "not-a-date"},
                            watchdog_run(2),
                        ]
                    },
                )
            },
            1,
            ("could not parse the queued-run listing",),
            ("Queue is clean",),
        ),
        (
            # Finding: the starvation report latched on the FIRST label set while
            # the verdict accumulated across all of them, so a GitHub-hosted set
            # sorting first suppressed a real starvation warning outright.
            "a hosted label set never suppresses a self-hosted starvation",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                # "macos-latest" sorts before "self-hosted", so jq `unique` puts
                # the hosted set first -- the ordering that used to lose.
                "actions/runs/1/jobs": watchdog_jobs(
                    ["macos-latest"], ["self-hosted", "Windows", "unicorn"]
                ),
            },
            0,
            ("starved", "::warning::", "self-hosted, windows, unicorn"),
            (
                "no queued job requests a self-hosted runner",
                "cancelled 1",
                "macos-latest] is registered",
            ),
        ),
        (
            # Finding: the warning interpolated the first set's labels while
            # branching on the whole-run verdict, so it told the operator to
            # bring online a runner named by a GitHub-hosted label.
            "the starvation warning names the self-hosted labels, not a hosted one",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "offline", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(["macos-latest"], self_hosted),
            },
            0,
            ("registered but offline", "self-hosted, windows, ram-64gb, fast"),
            ("[macos-latest] is registered", "cancelled 1"),
        ),
        (
            # Finding: an empty workflow path is a hard bash error on the
            # exclusion-list subscript, which aborted with an EMPTY summary.
            "a run with no workflow path is reported, not crashed on",
            {
                queued: (
                    0,
                    {"workflow_runs": [watchdog_run(1) | {"path": None}]},
                ),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
            },
            0,
            ("no workflow path reported", "Watchdog summary"),
            ("bad array subscript", "cancelled 1"),
        ),
        (
            # THE guard that matters most. A matrix cell executing on one runner
            # while a sibling cell waits is not dispatcher-stuck -- it holds a
            # Unity licence seat. Cancelling it kills a live Unity session
            # mid-test, which is the seat leak `require-confirmed-unity-cleanup`
            # exists to catch. Nothing in the suite reached this branch before:
            # the job fixture could only produce QUEUED jobs.
            "a run with an in-progress job is never cancelled",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(
                    self_hosted, extra=[{"status": "in_progress", "labels": self_hosted}]
                ),
            },
            0,
            ("healthy queued", "1 in_progress"),
            ("cancelled 1", "queued for cancel"),
        ),
        (
            # A run whose jobs have all finished is in a transitional state, not
            # the dispatcher-stuck pattern.
            "a run with no queued jobs is never cancelled",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(
                    extra=[{"status": "completed", "labels": self_hosted}]
                ),
            },
            0,
            ("no queued jobs yet",),
            ("cancelled 1",),
        ),
        (
            # The audit must never cancel itself, however the exclusion list is
            # configured. SELF_RUN_ID is 999 in the harness, so the fixture run
            # has to carry that id for the guard to be exercised at all.
            "the watchdog never cancels its own run",
            {
                queued: watchdog_queued_runs(watchdog_run(999)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
            },
            0,
            ("this is the watchdog's own run",),
            ("cancelled 999",),
        ),
        (
            # The inventory is read in TWO calls. Only the runner-GROUP listing
            # was covered; a failure of the per-group RUNNER listing could be
            # downgraded to a log line and the audit would report exit 0 over an
            # empty inventory -- precisely the #328 shape.
            "an unreadable runner listing inside a group fails closed",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": (1, None),
            },
            1,
            ("could not read runners in organization runner group",),
            (),
        ),
        (
            # The redispatch branch had never executed: the fixture returned an
            # empty `content`, so `base64 -d` always failed and the workflow was
            # always treated as non-dispatchable.
            "a dispatchable push run is cancelled and re-dispatched",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(self_hosted),
                # Ordered BEFORE the plain workflow route: the stub matches
                # substrings in insertion order, so `actions/workflows/42` would
                # otherwise swallow the dispatches POST and answer it silently.
                "actions/workflows/42/dispatches": (0, "DISPATCH-REQUESTED"),
                "actions/workflows/42": (0, {"path": ".github/workflows/perf-numbers.yml"}),
                "contents/.github/workflows/perf-numbers.yml": (0, DISPATCHABLE_WORKFLOW_BODY),
            },
            0,
            # "cancelled 1" and the log line both precede the POST, so neither
            # proves it happened. Only the stub's response to the dispatches
            # call proves the request was actually made.
            ("cancelled 1", "re-dispatching workflow 42", "DISPATCH-REQUESTED"),
            (),
        ),
        (
            # A pull_request run must NEVER be re-dispatched: the dispatches
            # endpoint cannot re-trigger one, and the documented path is a
            # cancel plus an operator instruction. The workflow body here IS
            # dispatchable, so only the event check can hold the line.
            "a pull_request run is cancelled but never re-dispatched",
            {
                queued: watchdog_queued_runs(watchdog_run(1, event="pull_request")),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(self_hosted),
                "actions/workflows/42": (0, {"path": ".github/workflows/perf-numbers.yml"}),
                "contents/.github/workflows/perf-numbers.yml": (0, DISPATCHABLE_WORKFLOW_BODY),
            },
            0,
            ("cancelled 1", "pull_request-triggered", "Re-run all jobs"),
            ("re-dispatching",),
        ),
        (
            # A busy sibling must not hide a starved one. The run is legitimately
            # healthy (something can proceed) AND a second label set can never be
            # picked up; both are true and both have to be reported, or the
            # starvation stays invisible for as long as the busy leg runs.
            "a busy sibling does not suppress a co-resident starvation",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", True, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(
                    self_hosted, ["self-hosted", "Windows", "unicorn"]
                ),
            },
            0,
            ("healthy backpressure", "starved", "::warning::", "unicorn"),
            ("cancelled 1", STARVED_SECTION_EMPTY),
        ),
        (
            # The longest-lived form of the same blindness: once a runner picks
            # up the healthy cell the run is in-progress, and that state lasts
            # for the rest of the matrix. If the in-progress exit skips the
            # starvation report, the starved sibling is silent for hours.
            "an in-progress sibling does not suppress a co-resident starvation",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", True, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(
                    ["self-hosted", "Windows", "unicorn"],
                    extra=[{"status": "in_progress", "labels": self_hosted}],
                ),
            },
            0,
            ("healthy queued", "1 in_progress", "starved", "::warning::", "unicorn"),
            ("cancelled 1", STARVED_SECTION_EMPTY),
        ),
        (
            # The third and last shape of sibling blindness, and the one that
            # persists longest: while the dispatchable cell keeps matching idle,
            # the run is cancelled and re-dispatched over and over, and the
            # starved cell stays invisible across every cycle. `self_hosted`
            # sorts BEFORE the unicorn set under jq `unique`, so the idle match
            # is seen first -- exactly the ordering a `break` would lose.
            "a cancellable sibling does not suppress a co-resident starvation",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(
                    self_hosted, ["self-hosted", "Windows", "unicorn"]
                ),
                "actions/workflows/42": (0, {"path": ".github/workflows/perf-numbers.yml"}),
                "contents/.github/workflows/perf-numbers.yml": (0, {"content": ""}),
            },
            0,
            ("cancelled 1", "dispatcher-stuck", "starved", "::warning::", "unicorn"),
            (STARVED_SECTION_EMPTY,),
        ),
        (
            # Precedence: a later `busy` set must not demote an earlier `idle`
            # one. Without the break, that demotion is what would silently stop
            # the run being cancelled. The zebra set sorts AFTER self_hosted
            # under jq `unique`, so it is scanned second.
            "a later busy set does not demote an earlier idle match",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(
                    ("ELI", "online", False, self_hosted),
                    ("DAD", "online", True, ["self-hosted", "Windows", "zebra"]),
                ),
                "actions/runs/1/jobs": watchdog_jobs(
                    self_hosted, ["self-hosted", "Windows", "zebra"]
                ),
                "actions/workflows/42": (0, {"path": ".github/workflows/perf-numbers.yml"}),
                "contents/.github/workflows/perf-numbers.yml": (0, {"content": ""}),
            },
            0,
            ("cancelled 1", "dispatcher-stuck"),
            ("healthy backpressure",),
        ),
        (
            # Precedence: a later `offline` set must not demote an earlier
            # `busy` one. The run is still waiting on a real runner, so it stays
            # healthy -- while the offline set is still reported as starved.
            "a later offline set does not demote an earlier busy match",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(
                    ("ELI", "online", True, ["self-hosted", "Windows", "aaa"]),
                    ("DAD", "offline", False, ["self-hosted", "Windows", "zzz"]),
                ),
                "actions/runs/1/jobs": watchdog_jobs(
                    ["self-hosted", "Windows", "aaa"], ["self-hosted", "Windows", "zzz"]
                ),
            },
            0,
            ("healthy backpressure", "starved", "zzz"),
            ("cancelled 1", STARVED_SECTION_EMPTY),
        ),
        (
            # Pins the `workflow_dispatch:` grep itself. The dispatcher-stuck
            # case above uses an empty `content`, so `base64 -d` fails and the
            # grep never runs -- it reads as if it asserts detection but only
            # asserts the decode-failure path. A regression that dispatched
            # unconditionally would POST to a workflow with no such trigger, get
            # a 422, and leave the run cancelled with no recovery.
            "a run whose workflow declares no workflow_dispatch is not re-dispatched",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(self_hosted),
                "actions/workflows/42": (0, {"path": ".github/workflows/perf-numbers.yml"}),
                "contents/.github/workflows/perf-numbers.yml": (
                    0,
                    NON_DISPATCHABLE_WORKFLOW_BODY,
                ),
            },
            0,
            ("cancelled 1", "does not support workflow_dispatch", "Re-run all jobs"),
            ("re-dispatching",),
        ),
        (
            # Pins the starvation PRECEDENCE. Two self-hosted sets starve for
            # different reasons: one has a registered-but-offline runner, the
            # other has nothing at all. The unregistered set must win, because a
            # label nothing carries needs a human while an offline machine may
            # reconnect on its own. Reporting the offline one instead points the
            # operator at a machine that exists and will come back.
            "an unregistered label set outranks an offline one in the report",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                # `aaa` sorts BEFORE `zzz` under jq `unique`, so the OFFLINE set
                # is scanned first and plain first-wins would report it. Only the
                # upgrade clause promotes the unregistered set. With the sets the
                # other way round the clause never executes and its deletion
                # survives -- which is how this case originally passed.
                "runner-groups/7/runners": watchdog_runners(
                    ("DAD", "offline", False, ["self-hosted", "aaa"])
                ),
                "actions/runs/1/jobs": watchdog_jobs(
                    ["self-hosted", "aaa"], ["self-hosted", "zzz"]
                ),
            },
            0,
            ("starved", "no runner registered", "zzz"),
            ("registered but offline", "cancelled 1", STARVED_SECTION_EMPTY),
        ),
        (
            # Two dispatcher-stuck runs in one cycle: the cancel loop must handle
            # both, which is what exercises the `mapfile`-not-`while read` stdin
            # defense and per-run cap independence.
            "two dispatcher-stuck runs are both cancelled",
            {
                queued: watchdog_queued_runs(watchdog_run(1), watchdog_run(2)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(self_hosted),
                "actions/runs/2/jobs": watchdog_jobs(self_hosted),
                "actions/workflows/42": (0, {"path": ".github/workflows/perf-numbers.yml"}),
                "contents/.github/workflows/perf-numbers.yml": (0, {"content": ""}),
            },
            0,
            ("cancelled 1", "cancelled 2"),
            (),
        ),
        (
            # An unusable cancel-cap state branch must fail rather than cancel
            # blind: without the cap a run could be cancelled without limit. The
            # pending run must still be NAMED, or a fail-closed cap leaves the
            # operator nothing to act on.
            "an unusable state branch fails closed and names the pending run",
            STUCK_ROUTES,
            1,
            ("run 1", "queued for cancel"),
            ("cancelled 1",),
            {"push_ok": False},
        ),
        (
            # A transient `ls-remote` failure must not be read as "the branch does
            # not exist": bootstrapping on that would push-corrupt the real branch
            # by rewriting it as a fresh orphan, resetting every cancel cap.
            "an unreadable state branch fails closed",
            STUCK_ROUTES,
            1,
            (),
            ("cancelled 1",),
            {"ls_remote_ok": False},
        ),
        (
            "a state branch that cannot be cloned fails closed",
            STUCK_ROUTES,
            1,
            ("clone failed",),
            (),
            {"existing_state": {}, "clone_ok": False},
        ),
        (
            # The cap is what stops a run being cancelled without limit.
            "a run past its daily cancel cap is not cancelled again",
            STUCK_ROUTES,
            0,
            ("cap reached",),
            ("cancelled 1",),
            {"existing_state": {1: 2}},
        ),
        (
            # ...and the cap resets after 24h, which the fixture could not reach
            # while it hardcoded an age inside the window.
            "a cancel cap older than 24h resets",
            STUCK_ROUTES,
            0,
            ("cancelled 1",),
            ("cap reached",),
            {"existing_state": {1: 2}, "state_age_seconds": 90000},
        ),
        (
            # A rejected cancel must not increment the cap or re-dispatch: that
            # would duplicate a run that is still live.
            "a rejected cancel is not treated as a cancel",
            STUCK_ROUTES,
            0,
            ("will try again next cycle",),
            ("re-dispatching",),
            {"cancel_ok": False},
        ),
        (
            # RESTORED: deleted by the table fold in 98cd2094. Labels that are not
            # strings break `ascii_downcase` INSIDE the label parse, after the job
            # listing itself parsed cleanly -- a different guard from the listing
            # read, and the only one that can leave a run silently unevaluated.
            "job labels that are not strings fail closed",
            {
                queued: watchdog_queued_runs(watchdog_run(1)),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
                "actions/runs/1/jobs": watchdog_jobs(extra=[{"status": "queued", "labels": [17]}]),
            },
            1,
            ("could not parse the job labels",),
            (),
        ),
        (
            # RESTORED: deleted by the table fold. A queued listing whose ids do
            # not round-trip to their own metadata (here an id typed as a string)
            # leaves the run unclassifiable, and an unclassifiable run must fail
            # the audit rather than be skipped past.
            "a run whose id does not round-trip fails closed",
            {
                queued: (0, {"workflow_runs": [watchdog_run(1) | {"id": "1"}]}),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
            },
            1,
            ("carries no metadata",),
            (),
        ),
        (
            # RESTORED: deleted by the table fold. A run younger than
            # MIN_QUEUE_AGE_SECONDS is not a candidate at all. The timestamp is
            # derived from the workflow's own literal, so lowering that literal
            # to 0 -- or deleting the age filter -- changes the verdict here.
            "a just-created run is below the age gate",
            {queued: watchdog_queued_runs(watchdog_run(1, created_at=FRESH_TIMESTAMP))},
            0,
            ("Queue is clean",),
            ("cancelled 1",),
        ),
        (
            "the excluded release workflow is never cancelled",
            {
                queued: watchdog_queued_runs(watchdog_run(1, workflow="release.yml")),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
            },
            0,
            ("workflow is excluded",),
            ("cancelled 1",),
        ),
        (
            # Pins `base="${wf##*/}"`. `release.yml` is in the workflow's OWN
            # default list, so it stays excluded even with the strip removed --
            # only a workflow excluded SOLELY by a prefixed repo-variable entry
            # can prove the normalization runs.
            "a repo-variable exclusion given with its directory prefix still matches",
            {
                queued: watchdog_queued_runs(watchdog_run(1, workflow="ci.yml")),
                inventory: one_group,
                "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
            },
            0,
            ("workflow is excluded",),
            ("cancelled 1",),
            {"extra_env": {"EXTRA_EXCLUDED_WORKFLOWS": ".github/workflows/ci.yml"}},
        ),
    )


    # The exclusion list is built from a repo variable, so an operator entry
    # ending in `/` strips to an EMPTY associative-array subscript -- a hard
    # bash error that kills the audit before it reads anything, every five
    # minutes. Guarding only the read site left this reachable.
    code, text = run_watchdog(
        script,
        environment_literals,
        {queued: (0, {"workflow_runs": []})},
        extra_env={"EXTRA_EXCLUDED_WORKFLOWS": "some/dir/ release.yml"},
    )
    require(
        code == 0,
        f"watchdog exclusion-list normalization: a trailing-slash entry aborted the audit "
        f"(exit {code})\n{text}",
    )
    require(
        "bad array subscript" not in text,
        "watchdog exclusion-list normalization: empty subscript reached the array\n" + text,
    )

    # RESTORED. The fold that turned standalone cases into table rows deleted a
    # span that included this block and the summary-channel one below, and
    # nothing failed -- they only fail when the WORKFLOW is mutated, so the suite
    # stayed green while silently losing the coverage. That is the exact class
    # this file exists to catch, arriving via a cleanup. (Cursor Bugbot.)
    #
    # The EXIT trap. Without it, an abort the script does not anticipate exits
    # red with a COMPLETELY EMPTY step summary and no annotation -- the worst
    # signal for a job that runs 288 times a day. Forced through a failing
    # `date`, which is unguarded on purpose so this stays reachable.
    code, text = run_watchdog(script, environment_literals, {}, break_date=True)
    require(code != 0, f"watchdog unexpected abort: expected a non-zero exit, got {code}\n{text}")
    for needle in ("Watchdog summary", "aborted unexpectedly", "::error::"):
        require(
            needle in text,
            f"watchdog unexpected abort: the EXIT trap did not emit {needle!r}\n{text}",
        )

    # The step summary is the operator-facing artifact, and nothing else proves
    # anything reached it: `log_summary` tees to stdout, so every other needle is
    # satisfiable from the job log alone. These assert the SUMMARY channel.
    code, text = run_watchdog(
        script,
        environment_literals,
        {
            queued: watchdog_queued_runs(watchdog_run(1)),
            inventory: one_group,
            "runner-groups/7/runners": watchdog_runners(("ELI", "offline", False, self_hosted)),
            "actions/runs/1/jobs": watchdog_jobs(self_hosted),
        },
    )
    require(code == 0, f"watchdog summary channel: expected exit 0, got {code}\n{text}")
    for needle in (
        "## Watchdog summary",
        "### Healthy queued",
        "### Stuck (auto-cancelled)",
        "### Starved",
        "### Stuck but excluded",
        "Queued runs older than",
        "Runner groups visible to",
        "registered but offline",
    ):
        require(
            needle in text.summary,
            f"watchdog summary channel: {needle!r} never reached GITHUB_STEP_SUMMARY "
            f"(it may only be in the job log)\n{text.summary}",
        )

    # RESTORED: deleted by the table fold, and the most dangerous of the six.
    # State is pushed immediately after EACH successful cancel, not only in the
    # final sync. Deleting `state_dirty=1` makes `persist_state_changes` return
    # early on every call, so NO cancel count is ever committed: every cycle
    # reads `cancels=0` and cancels the same run again, without limit, against
    # live Unity runs holding licence seats. The git stub echoes "pushed" per
    # push, so the count separates bootstrap + one push per cancel (3) from
    # bootstrap + a single final sync (2).
    two_stuck = {
        queued: watchdog_queued_runs(watchdog_run(1), watchdog_run(2)),
        inventory: one_group,
        "runner-groups/7/runners": watchdog_runners(("ELI", "online", False, self_hosted)),
        "actions/runs/1/jobs": watchdog_jobs(self_hosted),
        "actions/runs/2/jobs": watchdog_jobs(self_hosted),
        "actions/workflows/42": (0, {"path": ".github/workflows/perf-numbers.yml"}),
        "contents/.github/workflows/perf-numbers.yml": (0, {"content": ""}),
    }
    code, text = run_watchdog(script, environment_literals, two_stuck)
    require(code == 0, f"watchdog per-cancel persist: expected exit 0, got {code}\n{text}")
    require(
        text.count("pushed") >= 3,
        "watchdog per-cancel persist: the cap was not pushed after each cancel "
        f"(saw {text.count('pushed')} pushes, expected at least 3)\n{text}",
    )

    # The summary is emitted exactly once. A second copy on the normal path
    # would mean `finish` and the trap both fired.
    code, text = run_watchdog(
        script, environment_literals, {queued: (0, {"workflow_runs": []})}
    )
    require(code == 0, f"watchdog emit-once: expected exit 0, got {code}\n{text}")
    require(
        text.count("### Stuck (auto-cancelled)") == 1,
        "watchdog emit-once: the step summary was written more than once\n" + text,
    )

    # The runner groups actually counted are named in the summary. That log line
    # is the only way a second, restricted group ever becomes visible, which is
    # what the visibility comment tells a future reader to watch for.
    code, text = run_watchdog(
        script,
        environment_literals,
        {
            queued: watchdog_queued_runs(watchdog_run(1)),
            inventory: (0, {"runner_groups": [{"id": 7, "name": "Default"}]}),
            "runner-groups/7/runners": watchdog_runners(("ELI", "online", True, self_hosted)),
            "actions/runs/1/jobs": watchdog_jobs(self_hosted),
        },
    )
    require(
        "Runner groups visible to" in text and "Default" in text,
        "watchdog inventory: the counted runner-group names were not reported\n" + text,
    )

    # Rows may carry a 6th element: kwargs for `run_watchdog`. That is what lets
    # a case needing a failing push, a pre-populated cap, or a repo variable be a
    # ROW rather than yet another hand-rolled run-and-assert block.
    for case in cases:
        name, routes, expected_code, expected, forbidden = case[:5]
        options = case[5] if len(case) > 5 else {}
        code, text = run_watchdog(script, environment_literals, routes, **options)
        require(
            code == expected_code,
            f"watchdog {name}: expected exit {expected_code}, got {code}\n{text}",
        )
        for needle in expected:
            require(needle in text, f"watchdog {name}: summary omitted {needle!r}\n{text}")
        for needle in forbidden:
            require(needle not in text, f"watchdog {name}: summary leaked {needle!r}\n{text}")


MAINTENANCE = Path(".github/workflows/post-merge-maintenance.yml")


def run_maintenance_push_loop(
    script: str,
    root: Path,
    failing_check_calls: int,
    failing_pushes: int,
    failing: str = "issue-template",
    advance_before_run: bool = False,
    push_fails_without_advance: bool = False,
    quiet_other: bool = False,
) -> tuple[int, dict[str, str]]:
    """Execute the push loop against a real local remote; return exit + pushed files.

    The loop is the only place in this repository where generator failure,
    concurrent-merge retry, and the job's exit status interact, and it has now
    produced two defects in review: a `regenerate` that always returned 0 (so an
    not-yet-converged generator shipped green) and then a failure flag that never
    cleared (so a transient failure reddened a job that had converged). Both are
    invisible to a text assertion, so this runs the real thing.

    `failing_pushes` forces the concurrent-merge retry: each failed push also
    advances the remote, which is what makes the loop regenerate on a new head.
    """
    bin_dir, origin, work, other = (root / n for n in ("bin", "origin.git", "work", "other"))
    bin_dir.mkdir()
    subprocess.run(["git", "init", "-q", "--bare", str(origin)], check=True)
    for clone in (work, other):
        subprocess.run(
            ["git", "clone", "-q", str(origin), str(clone)], check=True, capture_output=True
        )
        for key, value in (("user.email", "t@t"), ("user.name", "t")):
            subprocess.run(["git", "-C", str(clone), "config", key, value], check=True)
    (work / ".github" / "ISSUE_TEMPLATE").mkdir(parents=True)
    for name, body in (("llms.txt", "a"), ("README.md", "b"), (".github/ISSUE_TEMPLATE/bug_report.yml", "c")):
        (work / name).write_text(body, encoding="utf-8")
    subprocess.run(["git", "-C", str(work), "add", "-A"], check=True)
    subprocess.run(["git", "-C", str(work), "commit", "-qm", "seed"], check=True)
    subprocess.run(["git", "-C", str(work), "branch", "-M", "master"], check=True)
    subprocess.run(["git", "-C", str(work), "push", "-q", "origin", "master"], check=True)

    calls, pushes = root / "check-calls", root / "push-calls"
    calls.write_text("0", encoding="utf-8")
    pushes.write_text("0", encoding="utf-8")
    npm = bin_dir / "npm"
    # Whichever generator is under test counts its own calls and fails the first
    # `failing_check_calls` of them; the other always converges. Both branches of
    # `regenerate` set the failure flag, so both have to be exercised.
    #
    # `quiet_other` stops the OTHER generator writing anything. Without it every
    # regeneration left a pending change, so the loop always exited through the
    # commit-and-push branch and the "already current; nothing to commit" exit
    # was unreachable -- one of two sites where the regenerate verdict could be
    # dropped undetected.
    flaky = "check:llms-txt" if failing == "llms" else "check:issue-template-versions"
    steady = "check:issue-template-versions" if failing == "llms" else "check:llms-txt"
    writes_template = "exit 0" if quiet_other else (
        'echo "GEN-$(date +%s%N)" > .github/ISSUE_TEMPLATE/bug_report.yml; exit 0'
    )
    writes_llms = "exit 0" if (quiet_other and failing != "llms") else (
        'echo "GEN-$(date +%s%N)" > llms.txt; exit 0'
    )
    npm.write_text(
        "#!/bin/bash\n"
        'case "$*" in\n'
        f'  "run update:llms-txt") {writes_llms} ;;\n'
        f'  "run update:issue-template-versions") {writes_template} ;;\n'
        f'  "run {steady}") exit 0 ;;\n'
        f'  "run {flaky}")\n'
        f"     n=$(cat {calls}); n=$((n+1)); echo $n > {calls}\n"
        f'     [ "$n" -le {failing_check_calls} ] && exit 1\n'
        "     exit 0 ;;\n"
        "esac\nexit 0\n",
        encoding="utf-8",
    )
    git = bin_dir / "git"
    # A failed push normally ALSO advances the remote, which is what drives the
    # concurrent-merge retry. `push_fails_without_advance` withholds that, which
    # is the genuinely different case: a push rejected while master stood still
    # (branch protection, a bad token) is a real failure with no retry to make,
    # and the loop must surface it rather than exit 0 having pushed nothing.
    if push_fails_without_advance:
        on_push_failure = "      exit 1\n"
    else:
        on_push_failure = (
            f"      (cd {other} && /usr/bin/git pull -q --rebase; echo $n > z$n.txt; "
            "/usr/bin/git add -A; /usr/bin/git commit -qm concurrent; "
            "/usr/bin/git push -q origin master) > /dev/null 2>&1\n"
            "      exit 1\n"
        )
    git.write_text(
        "#!/bin/bash\n"
        'for a in "$@"; do\n'
        '  if [ "$a" = "push" ]; then\n'
        f"    n=$(cat {pushes}); n=$((n+1)); echo $n > {pushes}\n"
        f'    if [ "$n" -le {failing_pushes} ]; then\n'
        f"{on_push_failure}"
        "    fi\n  fi\ndone\n"
        'exec /usr/bin/git "$@"\n',
        encoding="utf-8",
    )
    for stub in (npm, git):
        stub.chmod(0o755)

    (work / "llms.txt").write_text("modified", encoding="utf-8")
    if advance_before_run:
        # Master advances between the generator steps and the commit step. That
        # drives the TOP-of-loop regeneration, which the fixture could not reach
        # before -- only the bottom-of-loop one after a failed push. It is the
        # branch the whole stale-head design exists for: without it the loop
        # commits the old head's generated output onto a new head.
        # `other` was cloned from the still-empty bare repo, so it has to catch
        # up to the seed commit before it can advance master.
        subprocess.run(
            ["git", "-C", str(other), "fetch", "-q", "origin"], check=True, capture_output=True
        )
        subprocess.run(
            ["git", "-C", str(other), "checkout", "-q", "-B", "master", "origin/master"],
            check=True,
            capture_output=True,
        )
        (other / "concurrent.txt").write_text("x", encoding="utf-8")
        subprocess.run(["git", "-C", str(other), "add", "-A"], check=True)
        subprocess.run(["git", "-C", str(other), "commit", "-qm", "advance"], check=True)
        subprocess.run(["git", "-C", str(other), "push", "-q", "origin", "master"], check=True)
    environment = os.environ.copy()
    environment.update(
        {
            "PATH": f"{bin_dir}{os.pathsep}{environment['PATH']}",
            "LLMS_PATHS": "llms.txt README.md",
            "ISSUE_TEMPLATE_PATHS": ".github/ISSUE_TEMPLATE/bug_report.yml",
            "GH_PUSH_TOKEN": "stub",
            "GITHUB_REF_NAME": "master",
        }
    )
    code = subprocess.run(
        ["bash", "-c", script], cwd=work, env=environment, capture_output=True, text=True, check=False
    ).returncode
    # What actually reached the remote matters as much as the exit code: a
    # generator whose own checker rejected its output must never have that
    # output pushed. Asserting only the exit code let the revert be deleted.
    # Per-file, not concatenated: the generator that SUCCEEDS legitimately writes
    # its marker, so a combined blob cannot tell whose output was reverted.
    pushed = {}
    for path in ("llms.txt", ".github/ISSUE_TEMPLATE/bug_report.yml"):
        shown = subprocess.run(
            ["git", "-C", str(origin), "show", f"master:{path}"],
            capture_output=True,
            text=True,
            check=False,
        )
        pushed[path] = shown.stdout if shown.returncode == 0 else ""
    return code, pushed


def run_maintenance_gate(script: str, llms: str, issue_template: str) -> int:
    """Execute the terminal `Require every generator to have converged` step."""
    environment = os.environ.copy()
    environment.update({"LLMS_OUTCOME": llms, "ISSUE_TEMPLATE_OUTCOME": issue_template})
    return subprocess.run(
        ["bash", "-c", script], env=environment, capture_output=True, text=True, check=False
    ).returncode


def validate_post_merge_terminal_gate() -> None:
    """Pin the step that decides the job's verdict.

    The commit step deliberately runs after a failed generator, so the job's own
    verdict comes from this gate alone -- its comment says "without this a
    generator that never converged would be reported green". It had no coverage
    at all: neither its condition nor its outcome table was executed or asserted.
    """
    source = MAINTENANCE.read_text(encoding="utf-8")
    script = run_script(
        step_block(job_block(source, "regenerate"), "Require every generator to have converged")
    )
    if os.name == "nt":
        return
    # llms outcome, issue-template outcome, expected exit
    for llms, issue_template, expected in (
        ("success", "success", 0),
        ("success", "skipped", 0),
        ("skipped", "skipped", 0),
        ("failure", "success", 1),
        ("success", "failure", 1),
        ("failure", "failure", 1),
        ("cancelled", "success", 1),
        ("", "success", 1),
    ):
        code = run_maintenance_gate(script, llms, issue_template)
        require(
            code == expected,
            f"post-merge terminal gate ({llms!r}, {issue_template!r}): "
            f"expected exit {expected}, got {code}",
        )


def validate_post_merge_cancellation_policy() -> None:
    """No step that writes or pushes may survive a cancellation.

    `always()` includes CANCELLED, and this workflow sets
    `cancel-in-progress: true`, so a second push kills the first mid-flight as a
    matter of routine. A generator killed mid-write never reaches its own revert,
    so any step that keeps generating, probing, or pushing after a cancel can
    commit a half-written file to the default branch. The two workflows this one
    replaced had no `always()` at all and simply stopped. (Cursor Bugbot, high.)
    """
    source = MAINTENANCE.read_text(encoding="utf-8")
    require(
        "cancel-in-progress: true" in source,
        "post-merge cancellation: the concurrency policy changed; re-check whether "
        "cancellation is still a routine path before relaxing the guards below",
    )
    job = job_block(source, "regenerate")
    for step_name in (
        "Regenerate the issue-template version dropdown",
        "Check for changes",
        "Commit and push changes",
        "Require every generator to have converged",
    ):
        step = step_block(job, step_name)
        condition = re.search(r"\n        if: (?:>-\n\s+)?(.*?)\n        \w", step, re.S)
        require(condition is not None, f"post-merge cancellation: {step_name} has no `if:`")
        text = condition.group(1)
        require(
            "!cancelled()" in text,
            f"post-merge cancellation: {step_name} must be gated on `!cancelled()`; "
            f"got {text!r}",
        )
        require(
            "always()" not in text,
            f"post-merge cancellation: {step_name} uses `always()`, which runs after a "
            f"cancel and can push a half-written tree; got {text!r}",
        )


def validate_post_merge_push_loop() -> None:
    """Pin how the post-merge push loop turns generator outcomes into an exit code."""
    source = MAINTENANCE.read_text(encoding="utf-8")
    script = run_script(step_block(job_block(source, "regenerate"), "Commit and push changes"))
    if os.name == "nt":
        return
    # `failing_pushes` is what forces the loop to regenerate on a refreshed head:
    # each failed push also advances the remote. With zero failed pushes the loop
    # never calls `regenerate` at all, so a generator failure there belongs to the
    # step that ran it, not to this loop -- which is why the no-retry row expects 0.
    #
    # name, failing check calls, failing pushes, which generator, expected exit
    # A head that advanced BEFORE the commit step forces the top-of-loop
    # regeneration; a generator that fails there must still fail the job.
    with tempfile.TemporaryDirectory() as directory:
        code, pushed = run_maintenance_push_loop(
            script, Path(directory), 99, 0, "issue-template", advance_before_run=True
        )
    require(
        code == 1,
        f"post-merge push loop stale head: a generator failing on the refreshed head "
        f"must fail the job, got exit {code}",
    )
    with tempfile.TemporaryDirectory() as directory:
        code, pushed = run_maintenance_push_loop(
            script, Path(directory), 0, 0, "issue-template", advance_before_run=True
        )
    require(
        code == 0,
        f"post-merge push loop stale head: a clean regeneration on the refreshed head "
        f"must succeed, got exit {code}",
    )

    # The "already current" early exit must also carry the regenerate verdict.
    # A failing llms generator reverts its own file, which removes the only
    # pending change, so the loop leaves through that branch -- and it was one of
    # two exit sites where `exit "${regenerate_failed}"` could become `exit 0`
    # undetected, reinstating the always-green bug 764540b1 claims to have fixed.
    with tempfile.TemporaryDirectory() as directory:
        code, pushed = run_maintenance_push_loop(
            script, Path(directory), 99, 1, "llms", quiet_other=True
        )
    require(
        code == 1,
        f"post-merge push loop nothing-to-commit exit: a failed generator that "
        f"leaves no change must still fail the job, got exit {code}",
    )

    # The "No staged changes remain" exit is the second of the two sites where
    # the regenerate verdict could be dropped undetected. It is reached when a
    # change exists when the loop starts but is gone by staging time, which is
    # what a failing generator's revert does on the refreshed head.
    with tempfile.TemporaryDirectory() as directory:
        code, pushed = run_maintenance_push_loop(
            script,
            Path(directory),
            99,
            1,
            "llms",
            quiet_other=True,
            advance_before_run=True,
        )
    require(
        code == 1,
        f"post-merge push loop no-staged-changes exit: a failed generator must fail "
        f"the job through this branch too, got exit {code}",
    )

    # A push rejected while master stood still is a genuine failure with no
    # retry to make -- branch protection, a bad token, a lost credential. The
    # loop must surface it. The stub previously always advanced the remote on a
    # failed push, so this branch was unreachable and `exit "${push_status}"`
    # could be replaced with `exit 0`: a job reporting green having pushed
    # nothing at all.
    with tempfile.TemporaryDirectory() as directory:
        code, pushed = run_maintenance_push_loop(
            script, Path(directory), 0, 1, "issue-template", push_fails_without_advance=True
        )
    require(
        code != 0,
        f"post-merge push loop rejected push: a push rejected with master unchanged "
        f"must fail the job, got exit {code}",
    )
    require(
        "GEN-" not in pushed["llms.txt"],
        "post-merge push loop rejected push: reported a push that never landed\n"
        + repr(pushed["llms.txt"]),
    )

    for name, failing_checks, failing_pushes, failing, expected in (
        ("a clean run exits 0", 0, 0, "issue-template", 0),
        ("a straight retry with no generator failure exits 0", 0, 2, "issue-template", 0),
        ("an issue-template generator that never converges fails the job", 99, 1, "issue-template", 1),
        ("an llms.txt generator that never converges fails the job", 99, 1, "llms", 1),
        ("a transient issue-template failure that later converges exits 0", 1, 2, "issue-template", 0),
        # No transient-llms row on purpose. llms.txt carries the only change in
        # these fixtures, so reverting it on failure leaves nothing to commit and
        # the loop exits at the "already current" branch before a second
        # regeneration can occur. The failure still propagates (the row above),
        # which is the property that matters; a row asserting a path the loop
        # cannot reach would only look like coverage.
    ):
        with tempfile.TemporaryDirectory() as directory:
            code, pushed = run_maintenance_push_loop(
                script, Path(directory), failing_checks, failing_pushes, failing
            )
        require(code == expected, f"post-merge push loop {name}: expected exit {expected}, got {code}")
        # A generator whose checker rejected its output must have that output
        # REVERTED, so the marker its update step wrote can never reach master.
        # Only the FAILING generator's file is checked; the other one converged
        # and its marker is expected there.
        # Only when the generator NEVER converged (which is why the job fails).
        # A transient failure that later succeeds legitimately pushes its output.
        if expected == 1:
            owned = (
                "llms.txt" if failing == "llms" else ".github/ISSUE_TEMPLATE/bug_report.yml"
            )
            require(
                "GEN-" not in pushed[owned],
                f"post-merge push loop {name}: pushed {owned} from a generator that "
                f"never converged\n{pushed[owned]!r}",
            )


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
    timeout_fixture = f"""  fixture:
    timeout-minutes: 70
    steps:
      - name: Acquire organization Unity lock
        timeout-minutes: 5
        uses: {ACQUIRE_BUILD_LOCK}
        with:
          timeout-minutes: "4"
      - name: Return Unity license
        timeout-minutes: 1
      - name: Require confirmed Unity cleanup
        timeout-minutes: 2
"""
    validate_lock_window_timeout_budget(timeout_fixture, "timeout fixture")
    for name, mutation in (
        (
            "duplicate named step",
            timeout_fixture.replace(
                "      - name: Require confirmed Unity cleanup\n",
                "      - name: Return Unity license\n"
                "      - name: Require confirmed Unity cleanup\n",
            ),
        ),
        (
            "inserted cleanup step",
            timeout_fixture.replace(
                "      - name: Require confirmed Unity cleanup\n",
                "      - uses: example/unbounded@immutable\n"
                "      - name: Require confirmed Unity cleanup\n",
            ),
        ),
        (
            "dash-only cleanup step",
            timeout_fixture.replace(
                "      - name: Require confirmed Unity cleanup\n",
                "      -\n"
                "        id: unbounded\n"
                "        run: exit 0\n"
                "      - name: Require confirmed Unity cleanup\n",
            ),
        ),
    ):
        try:
            validate_lock_window_timeout_budget(mutation, name)
        except AssertionError:
            continue
        raise AssertionError(f"{name}: unbounded step was accepted")

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
    active_automation_paths = [
        *Path(".github/workflows").glob("*.yml"),
        *Path(".github/workflows").glob("*.yaml"),
        *Path(".github/actions").glob("*/action.yml"),
        *Path(".github/actions").glob("*/action.yaml"),
    ]
    active_automation = {
        path.as_posix(): path.read_text(encoding="utf-8")
        for path in active_automation_paths
    }
    workflow_editor_mutations = find_workflow_editor_mutations(active_automation)
    require(
        not workflow_editor_mutations,
        "workflow editor mutation policy failed: "
        + "; ".join(workflow_editor_mutations),
    )
    validation_fixture = """steps:
  - name: Validate installed Unity Editor
    run: |
      ./scripts/unity/ensure-editor.ps1 -InstallRoot (Join-Path $env:RUNNER_TOOL_CACHE 'u6-v3') -CiManagedOnly -ProvisioningProfile EditorOnly -RequireHealthyExisting
"""
    missing_guard_fixture = validation_fixture.replace(
        " -RequireHealthyExisting",
        "",
    )
    direct_install_fixture = """steps:
  - name: Install editor
    run: |
      unity install 6000.3.16f1
"""
    maintenance_fixture = """steps:
  - name: Maintain runner
    run: ./scripts/unity/maintain-windows-runner.ps1
"""
    require(
        find_workflow_editor_mutations({"valid.yml": validation_fixture}) == [],
        "validation-only editor fixture must pass",
    )
    require(
        find_workflow_editor_mutations({"missing.yml": missing_guard_fixture})
        == [
            "missing.yml: ensure-editor call missing positive -RequireHealthyExisting"
        ],
        "missing healthy-existing guard fixture must fail",
    )
    false_switch_fixture = validation_fixture.replace(
        "-CiManagedOnly",
        "-CiManagedOnly:$false",
    ).replace(
        "-RequireHealthyExisting",
        "-RequireHealthyExisting:$false",
    )
    require(
        find_workflow_editor_mutations({"false.yml": false_switch_fixture})
        == [
            "false.yml: ensure-editor call missing positive -RequireHealthyExisting",
            "false.yml: ensure-editor call missing positive -CiManagedOnly",
        ],
        "false-valued validation switches must fail",
    )
    inline_comment_fixture = """steps:
  - name: Unsafe validation
    run: ./scripts/unity/ensure-editor.ps1 -UnityVersion 6000.3.16f1 # -InstallRoot (Join-Path $env:RUNNER_TOOL_CACHE 'u6-v3') -CiManagedOnly -ProvisioningProfile EditorOnly -RequireHealthyExisting
"""
    inline_comment_violations = find_workflow_editor_mutations(
        {"inline-comment.yml": inline_comment_fixture}
    )
    require(
        len(inline_comment_violations) == 4
        and all(
            "ensure-editor call missing" in item
            for item in inline_comment_violations
        ),
        "inline PowerShell comments must not satisfy editor validation guards",
    )
    duplicate_call_fixture = """steps:
  - name: Validate installed Unity Editor
    run: |
      ./scripts/unity/ensure-editor.ps1 -InstallRoot (Join-Path $env:RUNNER_TOOL_CACHE 'u6-v3') -CiManagedOnly -ProvisioningProfile EditorOnly -RequireHealthyExisting
      ./scripts/unity/ensure-editor.ps1 -UnityVersion 6000.3.16f1
"""
    duplicate_violations = find_workflow_editor_mutations(
        {"duplicate.yml": duplicate_call_fixture}
    )
    require(
        duplicate_violations
        == [
            "duplicate.yml: editor validation must reference "
            "ensure-editor.ps1 exactly once per run body"
        ],
        "each editor-validation run body must contain exactly one ensure-editor reference",
    )
    prose_fixture = """description: unity install is forbidden in CI
steps:
  - name: Explain policy
    run: Write-Output 'validation only'
"""
    require(
        find_workflow_editor_mutations({"prose.yml": prose_fixture}) == [],
        "non-executable YAML prose must not be treated as an editor mutation",
    )
    require(
        find_workflow_editor_mutations({"install.yml": direct_install_fixture})
        == ["install.yml: direct Unity editor mutation command"],
        "direct editor install fixture must fail",
    )
    for name, command in (
        ("start-process", "Start-Process unity -ArgumentList 'install','6000.3.16f1'"),
        (
            "start-process-file-path",
            "Start-Process -FilePath unity -ArgumentList 'install','6000.3.16f1'",
        ),
        ("quoted", "& 'unity' install 6000.3.16f1"),
        ("variable", "& $unity install 6000.3.16f1"),
        ("arbitrary-variable", "& $cli install 6000.3.16f1"),
        ("absolute", "& 'C:\\Tools\\Unity.exe' install 6000.3.16f1"),
        ("cmd", "cmd /c unity install 6000.3.16f1"),
    ):
        mutation_fixture = (
            "steps:\n  - name: mutate\n    run: |\n"
            f"      {command}\n"
        )
        require(
            find_workflow_editor_mutations({f"{name}.yml": mutation_fixture})
            == [f"{name}.yml: direct Unity editor mutation command"],
            f"{name} editor mutation fixture must fail",
        )
    invoke_expression_fixture = """steps:
  - name: mutate
    run: Invoke-Expression 'unity install 6000.3.16f1'
"""
    require(
        any(
            "Invoke-Expression is forbidden" in violation
            for violation in find_workflow_editor_mutations(
                {"invoke-expression.yml": invoke_expression_fixture}
            )
        ),
        "Invoke-Expression editor mutation fixture must fail",
    )
    folded_install_fixture = """steps:
  - name: mutate
    run: >
      unity
      install 6000.3.16f1
"""
    require(
        find_workflow_editor_mutations({"folded.yml": folded_install_fixture})
        == ["folded.yml: direct Unity editor mutation command"],
        "folded YAML editor mutation fixture must fail",
    )
    require(
        find_workflow_editor_mutations({"maintenance.yml": maintenance_fixture})
        == [
            "maintenance.yml: maintain-windows-runner.ps1 call is not detect-only"
        ],
        "workflow runner maintenance fixture must fail",
    )
    indirect_maintenance_fixture = """steps:
  - name: Maintain runner
    run: |
      $script = Join-Path scripts unity/maintain-windows-runner.ps1
      & $script
"""
    require(
        any(
            "maintain-windows-runner.ps1 call is not detect-only" in violation
            for violation in find_workflow_editor_mutations(
                {"indirect-maintenance.yml": indirect_maintenance_fixture}
            )
        ),
        "indirect workflow runner maintenance fixture must fail",
    )
    invocation_bypass_fixtures = {
        "dot-source-ensure": (
            ". ./scripts/unity/ensure-editor.ps1 -UnityVersion 6000.3.16f1",
            "ensure-editor call missing positive -RequireHealthyExisting",
        ),
        "command-ensure": (
            "pwsh -Command ./scripts/unity/ensure-editor.ps1 -UnityVersion 6000.3.16f1",
            "ensure-editor call missing positive -RequireHealthyExisting",
        ),
        "workspace-ensure": (
            '& "$env:GITHUB_WORKSPACE/scripts/unity/ensure-editor.ps1" '
            "-UnityVersion 6000.3.16f1",
            "ensure-editor call missing positive -RequireHealthyExisting",
        ),
        "command-maintenance": (
            "pwsh -Command ./scripts/unity/maintain-windows-runner.ps1",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
    }
    for name, (command, expected_fragment) in invocation_bypass_fixtures.items():
        fixture = (
            "steps:\n  - name: unsafe\n    run: |\n"
            f"      {command}\n"
        )
        require(
            any(
                expected_fragment in violation
                for violation in find_workflow_editor_mutations(
                    {f"{name}.yml": fixture}
                )
            ),
            f"{name} invocation bypass fixture must fail",
        )
    false_function_fixture = """steps:
  - name: Unsafe maintenance
    run: |
      . ./scripts/unity/maintain-windows-runner.ps1
      Invoke-WindowsRunnerMaintenance -DetectOnly:$false
"""
    require(
        find_workflow_editor_mutations(
            {"false-function.yml": false_function_fixture}
        )
        == [
            "false-function.yml: "
            "maintain-windows-runner.ps1 call is not detect-only"
        ],
        "false-valued maintenance function invocation must fail",
    )
    repeated_maintenance_fixture = """steps:
  - name: Unsafe repeated maintenance
    run: |
      ./scripts/unity/maintain-windows-runner.ps1 -DetectOnly
      ./scripts/unity/maintain-windows-runner.ps1
"""
    require(
        find_workflow_editor_mutations(
            {"repeated-maintenance.yml": repeated_maintenance_fixture}
        )
        == [
            "repeated-maintenance.yml: "
            "maintain-windows-runner.ps1 mutation surface appears more than once"
        ],
        "one safe maintenance call must not mask a second unsafe call",
    )
    null_detect_only_fixture = """steps:
  - name: Unsafe null switch
    run: |
      ./scripts/unity/bootstrap-windows-runner.ps1 -DetectOnly
      Invoke-WindowsRunnerBootstrap -DetectOnly:$null
"""
    require(
        find_workflow_editor_mutations(
            {"null-detect-only.yml": null_detect_only_fixture}
        )
        == [
            "null-detect-only.yml: "
            "bootstrap-windows-runner.ps1 call is not detect-only"
        ],
        "a null DetectOnly binding must fail",
    )
    parser_bypass_fixtures = {
        "block-header-comment": (
            "run: | # valid YAML comment\n      unity install 6000.3.16f1",
            "direct Unity editor mutation command",
        ),
        "block-indent-indicator": (
            "run: |2\n      unity install 6000.3.16f1",
            "direct Unity editor mutation command",
        ),
        "quoted-run-key": (
            '"run": |\n      unity install 6000.3.16f1',
            "direct Unity editor mutation command",
        ),
        "spaced-run-key": (
            "run : |\n      unity install 6000.3.16f1",
            "direct Unity editor mutation command",
        ),
        "resolved-unity-command": (
            "run: '& (Get-Command unity).Source install 6000.3.16f1'",
            "resolved Unity editor mutation command",
        ),
        "block-comment-evidence": (
            "run: |\n      <#\n      -DetectOnly\n      #>\n"
            "      ./scripts/unity/maintain-windows-runner.ps1",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
        "here-string-evidence": (
            "run: |\n      $text = @'\n      -DetectOnly\n      '@\n"
            "      ./scripts/unity/maintain-windows-runner.ps1",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
        "unused-splat": (
            "run: |\n"
            "      . ./scripts/unity/maintain-windows-runner.ps1\n"
            "      $maintenanceArgs = @{ DetectOnly = $true }\n"
            "      Invoke-WindowsRunnerMaintenance",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
        "nested-detect-only": (
            "run: |\n"
            "      . ./scripts/unity/maintain-windows-runner.ps1\n"
            "      $maintenanceArgs = @{\n"
            "        UnityVersions = @(@{ DetectOnly = $true })\n"
            "      }\n"
            "      Invoke-WindowsRunnerMaintenance @maintenanceArgs",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
        "false-detect-expression": (
            "run: |\n"
            "      . ./scripts/unity/maintain-windows-runner.ps1\n"
            "      $maintenanceArgs = @{\n"
            "        DetectOnly = $true -and $false\n"
            "      }\n"
            "      Invoke-WindowsRunnerMaintenance @maintenanceArgs",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
        "diagnostic-ensure-guards": (
            "run: |\n"
            "      Write-Host \"Expected -CiManagedOnly "
            "-RequireHealthyExisting -ProvisioningProfile "
            "with RUNNER_TOOL_CACHE 'u6-v3'\"\n"
            "      ./scripts/unity/ensure-editor.ps1 "
            "-UnityVersion 6000.3.16f1",
            "ensure-editor call missing positive -RequireHealthyExisting",
        ),
        "mutated-splat-property": (
            "run: |\n"
            "      . ./scripts/unity/maintain-windows-runner.ps1\n"
            "      $maintenanceArgs = @{ DetectOnly = $true }\n"
            "      $maintenanceArgs.DetectOnly = $false\n"
            "      Invoke-WindowsRunnerMaintenance @maintenanceArgs",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
        "mutated-splat-index": (
            "run: |\n"
            "      . ./scripts/unity/maintain-windows-runner.ps1\n"
            "      $maintenanceArgs = @{ DetectOnly = $true }\n"
            "      $maintenanceArgs['DetectOnly'] = $false\n"
            "      Invoke-WindowsRunnerMaintenance @maintenanceArgs",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
        "removed-splat-key": (
            "run: |\n"
            "      . ./scripts/unity/maintain-windows-runner.ps1\n"
            "      $maintenanceArgs = @{\n"
            "        DetectOnly = $true\n"
            "      }\n"
            "      [void]$maintenanceArgs.Remove('DetectOnly')\n"
            "      Invoke-WindowsRunnerMaintenance @maintenanceArgs",
            "Invoke-WindowsRunnerMaintenance invocation is not detect-only",
        ),
        "aliased-splat-mutation": (
            "run: |\n"
            "      . ./scripts/unity/maintain-windows-runner.ps1\n"
            "      $maintenanceArgs = @{\n"
            "        DetectOnly = $true\n"
            "      }\n"
            "      $alias = $maintenanceArgs\n"
            "      $alias.DetectOnly = $false\n"
            "      Invoke-WindowsRunnerMaintenance @maintenanceArgs",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
        "repeated-file-dispatch": (
            "run: |\n"
            "      $script = Join-Path scripts unity/maintain-windows-runner.ps1\n"
            "      pwsh -File $script -DetectOnly\n"
            "      pwsh -File $script",
            "assigned command is invoked more than once",
        ),
    }
    for name, (run_yaml, expected_fragment) in parser_bypass_fixtures.items():
        fixture = f"steps:\n  - name: unsafe\n    {run_yaml}\n"
        require(
            any(
                expected_fragment in violation
                for violation in find_workflow_editor_mutations(
                    {f"{name}.yml": fixture}
                )
            ),
            f"{name} parser bypass fixture must fail",
        )
    yaml_and_binding_fixtures = {
        "flow-step": (
            'steps:\n  - { name: unsafe, run: "unity install 6000.3.16f1" }\n',
            "direct Unity editor mutation command",
        ),
        "false-direct-switch": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      . ./scripts/unity/maintain-windows-runner.ps1\n"
            "      Invoke-WindowsRunnerMaintenance "
            "-DetectOnly:$true.Equals($false)\n",
            "maintain-windows-runner.ps1 call is not detect-only",
        ),
        "module-invoke-expression": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      Microsoft.PowerShell.Utility\\Invoke-Expression "
            "'unity install 6000.3.16f1'\n",
            "Invoke-Expression is forbidden",
        ),
        "invoke-expression-alias": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      iex 'unity install 6000.3.16f1'\n",
            "Invoke-Expression is forbidden",
        ),
        "parameterized-get-command": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      & (Get-Command -Name unity).Source install 6000.3.16f1\n",
            "resolved Unity editor mutation command",
        ),
        "variable-install-verb": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      $verb='install'\n"
            "      & unity $verb 6000.3.16f1\n",
            "direct Unity editor mutation command",
        ),
        "two-variable-indirection": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      $cli='unity'\n"
            "      $verb='install'\n"
            "      & $cli $verb 6000.3.16f1\n",
            "variable command invocation is not an approved detect-only runner audit",
        ),
        "nested-powershell-command": (
            "steps:\n  - name: unsafe\n"
            '    run: powershell -Command "unity install 6000.3.16f1"\n',
            "nested Unity editor mutation command",
        ),
        "nested-start-process": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      Start-Process powershell -ArgumentList "
            "'-Command', 'unity install 6000.3.16f1' -Wait\n",
            "nested Unity editor mutation process",
        ),
        "braced-variable-command": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      $unity='unity'\n"
            "      & ${unity} install 6000.3.16f1\n",
            "direct Unity editor mutation command",
        ),
        "script-block-command": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      Start-Job { unity install 6000.3.16f1 } "
            "| Wait-Job | Receive-Job\n",
            "direct Unity editor mutation command",
        ),
        "dot-variable-command": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      $cli='unity'\n"
            "      $verb='install'\n"
            "      . $cli $verb 6000.3.16f1\n",
            "variable command invocation is not an approved detect-only runner audit",
        ),
        "aliased-run": (
            "x-command: &unsafe |\n"
            "  unity install 6000.3.16f1\n"
            "steps:\n"
            "  - name: unsafe\n"
            "    run: *unsafe\n",
            "direct Unity editor mutation command",
        ),
        "start-process-alias": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      $cli='unity'\n"
            "      $verb='install'\n"
            "      saps $cli -ArgumentList $verb,'6000.3.16f1' -Wait\n",
            "variable command invocation is not an approved detect-only runner audit",
        ),
        "nested-variable-command": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      $cli='unity'\n"
            "      $verb='install'\n"
            "      1 | ForEach-Object { & $cli $verb 6000.3.16f1 }\n",
            "variable command invocation is not an approved detect-only runner audit",
        ),
        "resolved-variable-command": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      $cli='unity'\n"
            "      $verb='install'\n"
            "      & (Get-Command $cli) $verb 6000.3.16f1\n",
            "dynamic resolved-command invocation is forbidden",
        ),
        "resolved-start-process": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      $cli='unity'\n"
            "      $verb='install'\n"
            "      Start-Process (Get-Command $cli).Source "
            "-ArgumentList $verb,'6000.3.16f1' -Wait\n",
            "dynamic resolved-command invocation is forbidden",
        ),
        "unbound-canonical-root": (
            "steps:\n  - name: unsafe\n    run: |\n"
            "      Write-Host $env:RUNNER_TOOL_CACHE 'u6-v3'\n"
            "      ./scripts/unity/ensure-editor.ps1 "
            "-InstallRoot 'C:\\Other' -CiManagedOnly "
            "-ProvisioningProfile EditorOnly -RequireHealthyExisting\n",
            "ensure-editor call missing canonical "
            "RUNNER_TOOL_CACHE/u6-v3 -InstallRoot",
        ),
    }
    for name, (fixture, expected_fragment) in yaml_and_binding_fixtures.items():
        require(
            any(
                expected_fragment in violation
                for violation in find_workflow_editor_mutations(
                    {f"{name}.yml": fixture}
                )
            ),
            f"{name} YAML or binding bypass fixture must fail",
        )
    commented_maintenance_fixture = """steps:
  - name: Maintain runner
    run: ./scripts/unity/maintain-windows-runner.ps1 # -DetectOnly
"""
    require(
        find_workflow_editor_mutations(
            {"commented-maintenance.yml": commented_maintenance_fixture}
        )
        == [
            "commented-maintenance.yml: "
            "maintain-windows-runner.ps1 call is not detect-only"
        ],
        "inline comments must not make runner maintenance detect-only",
    )
    maintain_source = Path("scripts/unity/maintain-windows-runner.ps1").read_text(
        encoding="utf-8"
    )
    require(
        "$busyProcesses = @($busyProcesses | Where-Object { $_.name -ne 'Runner.Worker' })"
        in maintain_source,
        "detect-only runner audit must ignore its own Runner.Worker",
    )
    require(
        "& $bootstrap -DetectOnly -UnityInstallRoot $InstallRoot"
        in maintain_source,
        "runner audit must validate host prerequisites against the canonical editor root",
    )
    prereq_action_source = Path(
        ".github/actions/assert-unity-host-prereqs/action.yml"
    ).read_text(encoding="utf-8")
    require(
        "& $scriptPath -DetectOnly -UnityInstallRoot $unityInstallRoot"
        in prereq_action_source
        and "Join-Path $env:RUNNER_TOOL_CACHE 'u6-v3'"
        in prereq_action_source,
        "per-job host prerequisite checks must be detect-only at the canonical editor root",
    )
    runner_audit_source = Path(".github/workflows/runner-bootstrap.yml").read_text(
        encoding="utf-8"
    )
    require(
        "DetectOnly = $true" in runner_audit_source
        and "Join-Path $env:RUNNER_TOOL_CACHE 'u6-v3'" in runner_audit_source,
        "runner audit workflow must be validation-only at the canonical editor root",
    )
    ensure_editor_source = Path("scripts/unity/ensure-editor.ps1").read_text(
        encoding="utf-8"
    )
    require(
        "$RequireHealthyExisting -and $CiManagedOnly" in ensure_editor_source
        and '"$UnityVersion\\Editor\\Unity.exe"' in ensure_editor_source,
        "healthy-existing CI validation must require the central return action's canonical editor leaf",
    )
    for workflow, job_id in LICENSED_LOCK_WINDOWS:
        window = job_block(workflow.read_text(encoding="utf-8"), job_id)
        validate_lock_window_timeout_budget(window, f"{workflow}:{job_id}")
        validate_cleanup_gate_not_attempted_input(window, f"{workflow}:{job_id}")

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
    head_check = job_block(source, "head-check")
    require(
        re.findall(r"^    runs-on:.*$", head_check, re.MULTILINE)
        == ["    runs-on: ubuntu-latest"]
        and LOCK_ACTION_PREFIX not in head_check,
        "superseded decision must never reach a self-hosted runner or the build lock",
    )
    require(
        "      superseded: ${{ steps.head.outputs.superseded }}\n" in head_check,
        "head-check must publish the superseded decision",
    )
    for job_id in ("runner-preflight", "unity-tests"):
        require(
            SUPERSEDED_GUARD.search(job_block(source, job_id)) is not None,
            f"{job_id} must not schedule work for a superseded head",
        )
    head_script = run_script(
        step_block(head_check, "Compare the event head against the live pull-request head")
    )
    if os.name != "nt":
        # The event head is always "current"; the live head is what moves.
        # name, event, live head, expected superseded decision
        for name, event, live, expected in (
            ("push", "push", "", "false"),
            ("current PR head", "pull_request", "current", "false"),
            ("superseded PR head", "pull_request", "moved-on", "true"),
            ("failed live lookup", "pull_request", "", "false"),
        ):
            require(
                run_head_check(head_script, event, live) == expected,
                f"head-check {name}: expected superseded={expected}",
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
        "HEAD_CHECK_RESULT": "${{ needs.head-check.result }}",
        "RUNNER_PREFLIGHT_RESULT": "${{ needs.runner-preflight.result }}",
        "UNITY_TESTS_RESULT": "${{ needs.unity-tests.result }}",
        "SUPERSEDED": "${{ needs.head-check.outputs.superseded }}",
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
test "${HEAD_CHECK_RESULT}" = success
if [ "${SUPERSEDED}" = "true" ] || [ "${FORK_PR}" = "true" ] || [ "${DEPENDABOT_PR}" = "true" ]; then
  test "${RUNNER_PREFLIGHT_RESULT}" = skipped
  test "${UNITY_TESTS_RESULT}" = skipped
else
  test "${RUNNER_PREFLIGHT_RESULT}" = success
  test "${UNITY_TESTS_RESULT}" = success
fi"""
    require(script == expected_script, "aggregate result-shape script drifted")
    # name, head-check, preflight, unity, SUPERSEDED, FORK_PR, DEPENDABOT_PR, exit code
    cases = (
        ("same-repository PR", "success", "success", "success", "false", "false", "false", 0),
        ("fork PR", "success", "skipped", "skipped", "false", "true", "false", 0),
        ("Dependabot PR", "success", "skipped", "skipped", "false", "false", "true", 0),
        ("superseded PR", "success", "skipped", "skipped", "true", "false", "false", 0),
        ("same-repository PR skipped Unity", "success", "success", "skipped", "false", "false", "false", 1),
        ("fork unexpectedly ran Unity", "success", "skipped", "success", "false", "true", "false", 1),
        ("Dependabot unexpectedly ran Unity", "success", "skipped", "success", "false", "false", "true", 1),
        ("Dependabot unexpectedly ran preflight", "success", "success", "skipped", "false", "false", "true", 1),
        ("superseded run still ran Unity", "success", "skipped", "success", "true", "false", "false", 1),
        ("current head skipped Unity after a failed decision", "failure", "skipped", "skipped", "", "false", "false", 1),
    )
    if os.name != "nt":
        for name, head, preflight, unity, superseded, fork, dependabot, expected in cases:
            environment = os.environ.copy()
            environment.update(
                {
                    "HEAD_CHECK_RESULT": head,
                    "RUNNER_PREFLIGHT_RESULT": preflight,
                    "UNITY_TESTS_RESULT": unity,
                    "SUPERSEDED": superseded,
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

    validate_stuck_job_watchdog()
    validate_post_merge_push_loop()
    validate_post_merge_terminal_gate()
    validate_post_merge_cancellation_policy()

    print("Unity pull-request policy validation passed.")


if __name__ == "__main__":
    validate()
