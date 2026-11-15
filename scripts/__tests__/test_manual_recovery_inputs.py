"""Exercise actual workflow recovery decisions without live API calls."""

import json
import os
from pathlib import Path
import re
import subprocess
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]


def recovery_fragment(workflow):
    """Locate the bounded real Bash block; normal lint owns full YAML parsing."""
    source = (ROOT / ".github" / "workflows" / workflow).read_text(encoding="utf-8")
    marker = ("# Decide redispatch path based on event + workflow_dispatch trigger."
              if workflow == "stuck-job-watchdog.yml" else 'redispatched="no"')
    assert source.count(marker) == 1, f"{workflow}: locate one actual recovery decision"
    start = source.rfind("\n", 0, source.index(marker)) + 1
    if workflow == "unstick-run.yml":
        end = source.index("# 9. Rate-limit guidance", start)
        return textwrap.dedent(source[start:end])
    remaining = source[start:]
    branch = re.search(r'^( *)case "\$\{run_event\}" in$', remaining, re.MULTILINE)
    assert branch, f"{workflow}: retain the original event case"
    closing = re.search(r"^" + re.escape(branch[1]) + r"esac$",
                        remaining[branch.start():], re.MULTILINE)
    assert closing, f"{workflow}: retain the complete case"
    return textwrap.dedent(remaining[:branch.start() + closing.end()])


PRELUDE = r'''
set -euo pipefail
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT
export TMPDIR="$work_dir"
calls_file="$work_dir/calls"
summary_file="$work_dir/summary"
stuck_runs_file="$work_dir/stuck"
touch "$calls_file" "$summary_file" "$stuck_runs_file"
REPO="fixture/recovery"
run_id=12345
run_workflow_id=456
run_event="$TEST_EVENT"
run_head_branch="$TEST_BRANCH"
force_redispatch="$TEST_FORCE"
run_path=".github/workflows/unity-tests.yml"
run_path_base="unity-tests.yml"
run_html_url="https://github.com/fixture/recovery/actions/runs/12345"
log_summary() { printf '%s\n' "$*" >> "$summary_file"; }
gh() {
  printf '%s\n' "$*" >> "$calls_file"
  if [[ "$*" == *" -X POST "* ]]; then
    printf '{}\n'
  elif [[ "$*" == *"contents/"* ]]; then
    if [[ "$TEST_SUPPORT_DISPATCH" == "true" ]]; then
      printf 'name: Fixture\non:\n  workflow_dispatch:\n    inputs:\n      consumer_install_validation:\n        default: false\n' | base64
    else
      printf 'name: Fixture\non:\n  push:\n' | base64
    fi
  elif [[ "$*" == *"actions/workflows/"* ]]; then
    printf '{"path":".github/workflows/unity-tests.yml"}\n'
  else
    printf 'Unexpected fixture API call: %s\n' "$*" >&2
    return 99
  fi
}
'''
RESULT = r'''
printf 'RECOVERY_RESULT='
jq -nc --arg summary "$(cat "$summary_file")" --arg calls "$(cat "$calls_file")" \
  '{summary: $summary, calls: $calls}'
'''

SCENARIOS = {'stuck-job-watchdog.yml': [{'name': 'manual branch',
                             'event': 'workflow_dispatch',
                             'dispatches': 0},
                            {'name': 'manual without branch',
                             'event': 'workflow_dispatch',
                             'branch': '',
                             'dispatches': 0},
                            {'name': 'push branch', 'event': 'push', 'dispatches': 1},
                            {'name': 'schedule branch', 'event': 'schedule', 'dispatches': 1},
                            {'name': 'pull request', 'event': 'pull_request', 'dispatches': 0},
                            {'name': 'push without branch',
                             'event': 'push',
                             'branch': '',
                             'dispatches': 0},
                            {'name': 'push without dispatch trigger',
                             'event': 'push',
                             'supported': False,
                             'dispatches': 0}],
 'unstick-run.yml': [{'name': 'manual forced', 'event': 'workflow_dispatch', 'dispatches': 0},
                     {'name': 'manual default',
                      'event': 'workflow_dispatch',
                      'force': False,
                      'dispatches': 0},
                     {'name': 'push forced', 'event': 'push', 'dispatches': 1},
                     {'name': 'schedule forced', 'event': 'schedule', 'dispatches': 1},
                     {'name': 'push default', 'event': 'push', 'force': False, 'dispatches': 0},
                     {'name': 'pull request forced', 'event': 'pull_request', 'dispatches': 0},
                     {'name': 'push without branch',
                      'event': 'push',
                      'branch': '',
                      'dispatches': 0},
                     {'name': 'push without dispatch trigger',
                      'event': 'push',
                      'supported': False,
                      'dispatches': 0}]}


class ManualRecoveryInputs(unittest.TestCase):
    def check_decision(self, workflow, scenario):
        bash = (str(Path(os.environ["ProgramFiles"]) / "Git" / "bin" / "bash.exe")
                if os.name == "nt" else "bash")
        environment = dict(os.environ, TEST_EVENT=scenario["event"],
                           TEST_BRANCH=scenario.get("branch", "master"),
                           TEST_FORCE=str(scenario.get("force", True)).lower(),
                           TEST_SUPPORT_DISPATCH=str(scenario.get("supported", True)).lower())
        command = PRELUDE + "\n" + recovery_fragment(workflow) + "\n" + RESULT
        result = subprocess.run([bash, "-c", command], cwd=ROOT, env=environment,
                                capture_output=True, encoding="utf-8", timeout=30,
                                check=False)
        context = f"{workflow}: {scenario['name']}; {result.stderr}\n{result.stdout}"
        self.assertEqual(result.returncode, 0, context)
        lines = [line for line in result.stdout.splitlines()
                 if line.startswith("RECOVERY_RESULT=")]
        self.assertEqual(len(lines), 1, context)
        payload = json.loads(lines[0][len("RECOVERY_RESULT="):])
        posts = [call for call in payload["calls"].splitlines() if " -X POST " in call]
        self.assertEqual(len(posts), scenario["dispatches"], context)
        if scenario["dispatches"]:
            self.assertRegex(posts[0], r"/actions/workflows/456/dispatches -f ref=master$")
        if scenario["event"] == "workflow_dispatch":
            self.assertRegex(payload["summary"], r"inputs", context)
            self.assertRegex(payload["summary"], r"Re-run all jobs", context)
            self.assertNotRegex(payload["summary"], r"OR[^\n]*force_redispatch", context)


for workflow, cases in SCENARIOS.items():
    for scenario in cases:
        def check_case(self, workflow=workflow, scenario=scenario):
            self.check_decision(workflow, scenario)
        name = "test_" + re.sub(r"[^a-z0-9]+", "_", workflow + "_" + scenario["name"])
        setattr(ManualRecoveryInputs, name, check_case)


if __name__ == "__main__":
    unittest.main()
