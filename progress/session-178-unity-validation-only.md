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

## Live PR state

PR #316 has one current head, `3f28a5e4`. Static CI and Cursor Bugbot are green,
and all 10 review threads are resolved. Eight Unity matrix cells pass. The final
cell, Unity `6000.3.16f1` standalone on `DAD-MACHINE`, fails validation before
the organization lock because the existing editor lacks the
`windows-il2cpp` module.

That failure is the intended validation-only behavior. An administrator must
repair the editor directly on `DAD-MACHINE`. A direct module-add attempt
confirmed that this editor is not Hub/CLI-managed:

```text
Error: No modules found for this editor.
Module installation is only supported for editors installed with Unity Hub.
```

The administrator must therefore run the repository's bounded reinstall path
from an elevated host shell:

```powershell
$editorRoot = 'E:\actions-runner\_tool\u6-v3'
.\scripts\unity\maintain-windows-runner.ps1 `
  -UnityVersions @('6000.3.16f1') `
  -InstallRoot $editorRoot `
  -ProvisioningProfile StandaloneWindowsIl2Cpp `
  -Force
```

The script detects the unmanageable editor, tries an atomic `install -m`
reinstallation, verifies the module on disk, and falls back to a bounded
quarantine plus reinstall when necessary. No workflow should execute that
repair. After the host is repaired, rerun the failed Unity job, merge only when
every required check is green, and run the central organization audit against
the exact merged default-branch commit.

The latest trusted central audit completed against current `master` commit
`645cde0551e92ed2fe4fc8cc128dda807ec348ba`. Its sanitized artifact reports the
same 56 DxMessaging findings across the six paid-serial jobs, so issue #305 is
not cleared by repository-side tests alone. The post-merge audit must report
zero findings at the exact merged commit.

## Remaining delivery steps

- Install `windows-il2cpp` manually on `DAD-MACHINE`.
- Rerun Unity CI and require `Unity CI Success`.
- Reconfirm zero unresolved review threads and merge PR #316.
- Run the trusted central enrollment audit against the merged commit and
  require zero DxMessaging findings.
- Reduce the default-branch ruleset to the two aggregate contexts, `CI Success`
  and `Unity CI Success`, while preserving its existing conditions and bypass
  actor.
