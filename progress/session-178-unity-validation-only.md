# Session 178 - Unity workflow validation-only correction

Date: 2026-07-30
Branch: `codex/issue-305-enrollment-remediation`
PR: **#316**

## Outcome

Unity editor and module installation now has a strict host/CI boundary:

- a runner administrator installs and repairs editors and modules directly on
  the Windows host under `RUNNER_TOOL_CACHE/u6-v3`;
- every workflow only validates the exact existing editor and required profile
  with `-CiManagedOnly -RequireHealthyExisting`;
- validation runs before the organization lock and fails with manual
  administrator remediation when an editor, module, or host prerequisite is
  absent;
- Actions never installs, repairs, uninstalls, or quarantines an editor and does
  not require administrator credentials.

The correction covers all five licensed lock windows in `unity-tests.yml`,
`unity-benchmarks.yml`, `perf-numbers.yml`, and both release jobs. The runner
audit and the host-prerequisite action are also detection-only. Manual
maintenance remains available through `maintain-windows-runner.ps1` and
`bootstrap-windows-runner.ps1` when an administrator runs them directly on the
host.

## Root cause and red-green evidence

The previous design reused the manual editor maintenance path from Actions. That
was the wrong ownership boundary: provisioning requires host administrator
access, while a workflow must be able to run without those credentials. It also
made a missing module look like an in-workflow repair problem and exposed the
shared editor tree to long-running mutation.

The focused workflow contracts failed after the workflows were changed to
validation-only because they still required provisioning. The contracts were
then rewritten to require literal validation-only switches, the canonical
managed root, and a pre-lock position. The Python policy validator also rejects
workflow attempts to reach install or repair behavior through YAML or
PowerShell indirection.

Local verification on commit `3f28a5e4`:

- full Node suite: 406 passed, 0 failed;
- focused workflow contracts: 17 passed, 0 failed;
- `npm run validate:all`, strict docs, spelling, Actionlint, Yamllint,
  PowerShell parsing, pre-commit, and `git diff --check`: passed;
- JavaScript LOC: 17,498 of 17,500;
- two adversarial repository audits: zero findings;
- full Unity Editor assembly on the prior runtime-equivalent head: 549 passed,
  0 failed. The correction changes workflows, scripts, tests, and documentation,
  not package C#.

## Delivery evidence

PR #316 passed all nine Unity matrix cells, including Unity `6000.3.16f1`
standalone IL2CPP. Static CI, Cursor Bugbot, and all 10 review threads were also
green. PR #316 merged as
`4d38854c2a67d4e97788d1a5baab6c515158531c`; the generated `llms.txt` refresh
then advanced `master` to `1efb73261333169a904a56f1b49e9e956e641309`.

Trusted organization audit run `30587754261` inspected that exact default-branch
commit. Its sanitized artifact reports `complete: true`, inventories every
DxMessaging Unity consumer, and contains zero DxMessaging findings.
