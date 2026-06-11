---
title: "Unity CI Matrix"
id: "unity-ci-matrix"
category: "unity"
version: "1.1.0"
created: "2026-05-05"
updated: "2026-05-20"

source:
  repository: "Ambiguous-Interactive/DxMessaging"
  files:
    - path: ".github/workflows-disabled/unity-tests.yml"
    - path: ".github/workflows-disabled/unity-benchmarks.yml"
  url: "https://github.com/Ambiguous-Interactive/DxMessaging"

tags:
  - "unity"
  - "ci"
  - "matrix"
  - "il2cpp"
  - "lts"
  - "game-ci"

complexity:
  level: "intermediate"
  reasoning: "Requires understanding game-ci action shapes, IL2CPP-specific failure modes, and Unity LTS cadence."

impact:
  performance:
    rating: "low"
    details: "Each matrix cell costs about 5 minutes of runner time"
  maintainability:
    rating: "high"
    details: "One workflow per concern; the matrix is computed from a single dispatch input"
  testability:
    rating: "high"
    details: "Workflow shape and asmdef discovery source-of-truth are conventions reviewed manually; no automated contract test pins them"

prerequisites:
  - "Familiarity with GitHub Actions matrix expansion"
  - "Awareness of Unity LTS release cadence"

dependencies:
  packages: []
  skills:
    - "headless-test-runner"
    - "unity-perf-test-isolation"

applies_to:
  languages:
    - "YAML"
  frameworks:
    - "GitHub Actions"
    - "Unity"
  versions:
    unity: ">=2021.3"

aliases:
  - "Unity matrix"
  - "IL2CPP gate"

related:
  - "headless-test-runner"
  - "unity-license-bootstrap"
  - "unity-license-return-guarantee"
  - "unity-perf-test-isolation"
  - "cicd-devcontainer-workflows"

status: "stable"
---

<!-- trigger: unity, ci, matrix, il2cpp, lts, game-ci, version | Unity version matrix and IL2CPP-only failure patterns | Core -->

# Unity CI Matrix

> **One-line summary**: The active Unity workflows under `.github/workflows/` run `scripts/unity/run-ci-tests.ps1` on self-hosted Windows runners: `unity-tests.yml` is one unified matrix of three Unity versions x {editmode, playmode, standalone} = 9 jobs, where `standalone` builds and runs a `StandaloneWindows64` IL2CPP player from an ephemeral Unity project generated under `.artifacts/`.

## When to Use

- Adding a new Unity LTS release to the supported set.
- Triaging an IL2CPP-only test failure that does not reproduce in EditMode.
- Investigating a Unity CI log that fails before any test prints output.
- Deciding whether to expand or contract the matrix to balance signal vs runtime.

## When NOT to Use

- Tweaking which assemblies run. That is the asmdef-discovery module's responsibility (see [unity-perf-test-isolation](./unity-perf-test-isolation.md)).
- Adjusting cache keys. Those live in the workflow's `actions/cache@v4` block; they hash package/test inputs plus the direct CI runner script and include OS, architecture, Unity version, and mode.

## Current Matrix

`unity-tests.yml` (active; direct Unity on self-hosted Windows; one unified matrix):

| Axis            | Values                                      |
| --------------- | ------------------------------------------- |
| `unity-version` | `2021.3.45f1`, `2022.3.45f1`, `6000.3.16f1` |
| `test-mode`     | `editmode`, `playmode`, `standalone`        |

Nine matrix cells. `editmode`/`playmode` run in-editor on Mono; `standalone` builds and runs a `StandaloneWindows64` IL2CPP player. The direct runner generates a temporary package host project under `.artifacts/unity/projects/<version>-<mode>/`, imports the repo package with a `file:` dependency, sets `testables`, and configures IL2CPP before running standalone tests. Workflow_dispatch inputs let you pin a single version or single mode for triage.

The Unity version list is canonical in `.github/unity-versions.json`; `unity-tests.yml` reads it at runtime and carries no version literal. Bump versions only in that file -- see [Unity Version Single Source of Truth](../github-actions/unity-version-single-source.md).

Licensed Unity execution is serialized by the central
`Ambiguous-Interactive/ambiguous-organization-build-lock` actions. The workflows
validate the three Unity serial secrets, acquire `wallstop-organization-builds`
immediately before `scripts/unity/run-ci-tests.ps1`, then release it with `if: always()`. Keep runner
labels broad enough for both Windows machines; the lock protects only the Unity
seat, not checkout, cache setup, or secret-shape validation. The licensed section
activates a classic serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`)
and returns the license on every exit path through four redundant layers
(return-at-start, PowerShell `try`/`finally`, an `if: always()` return step
inside the org-lock window, and the next run's return-at-start) -- see
[unity-license-return-guarantee](./unity-license-return-guarantee.md).

## Serialization + Timeout Invariant

The Unity serial has only a small activation-seat pool (typically ~2 seats) shared across the whole org and no server-side reclaim, so Unity-licensed jobs are serialized to one-at-a-time in TWO complementary layers. Neither layer alone is sufficient.

**Two-layer serialization.** `strategy.max-parallel: 1` serializes the matrix cells WITHIN a single run, and the external `ambiguous-organization-build-lock` action serializes ACROSS runs, workflows, and repositories.

- `max-parallel: 1` only: cannot prevent two separate runs (two pushes, `unity-tests` plus `unity-benchmarks`, or another org repo) from racing for the seat.
- The lock only: leaves all 9 cells spawning at once, so 8 idle cells burn their job-timeout clocks waiting, race for the seat, and clutter logs. Since the lock already forces one-Unity-at-a-time, that parallelism buys ZERO throughput.

With both layers, the within-run lock wait collapses to near-zero, so the cross-run lock poll budget can stay generous. This is `max-parallel: 1` ONLY -- it is NOT a native concurrency group. A native `concurrency.group: wallstop-organization-builds` is repository-scoped, serializes whole jobs, and is forbidden. Add `max-parallel: 1` under `strategy:` (sibling of `fail-fast`/`matrix`) on the matrix workflows only (`unity-tests.yml`, `unity-benchmarks.yml`); single-job workflows (`release.unity-checks`, the GameCI experiment) have no matrix and rely solely on the lock for across-run serialization.

**Timeout invariant.** GitHub counts the lock-wait against the job clock, so a job at the back of the serialized queue is killed before its lock wait can finish unless:

```text
job timeout-minutes >= acquire timeout-minutes + RUN_BUDGET (120)
```

The acquire input `timeout-minutes` is the lock POLL budget (how long the action waits for the seat), counted against the job clock. The current magnitudes are: acquire `timeout-minutes: "300"`, job `timeout-minutes: 420`, and a step-level `timeout-minutes: 120` on the Unity run step. Keep these magnitudes aligned across the Unity workflows when editing timeouts.

The step-level `timeout-minutes: 120` protects the in-use seat from a hung editor: it must be `>= 120` and STRICTLY below the job timeout so the step fails first and releases the lock instead of the whole job being cancelled with the seat still held. This step guard matters because `stuck-job-watchdog.yml` ignores any `in_progress` job -- nothing else bounds a post-acquire hang, so without the step timeout a wedged editor would squat the seat for the full job timeout.

**Operator note (standalone IL2CPP):** the `standalone` cells require the Windows IL2CPP Unity module and the host build toolchain needed by Unity for Windows players. `scripts/unity/ensure-editor.ps1` must be called with an explicit provisioning profile: `EditorOnly` for editmode/playmode/benchmarks/release checks, and `StandaloneWindowsIl2Cpp` for standalone so only `windows-il2cpp` is installed or verified. That script treats the beta standalone Unity CLI as a moving surface: it discovers the install root through the 0-arg `unity install-path` GETTER and sets the path best-effort, so an uncertain flag never aborts the matrix. See [unity-editor-cli-bootstrap](./unity-editor-cli-bootstrap.md) for the getter-vs-setter detail.

`unity-gameci-experiment.yml` is manual-only and non-required. It generates the same ephemeral project and then calls `game-ci/unity-test-runner@v4` in normal project mode (`packageMode: false`). Do not promote GameCI back to required Windows CI unless the full matrix produces real NUnit XML repeatedly.

`unity-benchmarks.yml` (active; manual/nightly, NEVER on PRs):

| Axis            | Values                 |
| --------------- | ---------------------- |
| `unity-version` | `2022.3.45f1`          |
| `test-mode`     | `editmode`, `playmode` |

The active `unity-benchmarks.yml` explicitly omits `pull_request` and `push` per the perf isolation rule.

## compute-unity-assemblies is-empty Gate

Every workflow that consumes `./.github/actions/compute-unity-assemblies` mirrors the canonical wiring in `unity-tests.yml`: the compute step carries `id: compute`, and every license-consuming step in the same job is gated with `if: ${{ steps.compute.outputs.is-empty != 'true' }}`. License-consuming means the three steps that spend the paid Unity seat: Provision (`scripts/unity/ensure-editor.ps1`), Acquire organization Unity lock (`acquire-build-lock`), and Run (`scripts/unity/run-ci-tests.ps1`). When asmdef discovery resolves no DxMessaging-owned assemblies for a target, the action sets `is-empty=true` and exits 0 -- a skip, not a failure. Without the gate that empty list still provisions the editor, takes the org lock, and runs Unity before failing late at verify, burning paid license and lock time.

The `Verify tests actually ran` step keeps a cancellation-safe gate and must also require `steps.compute.outcome == 'success'` plus either `steps.compute.outputs.is-empty == 'true'` or a non-skipped Unity run step (never an is-empty gate alone). It receives `expected-empty: ${{ steps.compute.outputs.is-empty }}`, so an intentional skip reads as success rather than a "tests did not run" failure, while checkout/cache/setup/provisioning/lock failures that prevent Unity from launching are not obscured by a generic missing-results annotation. The skip path does not fire for the current asmdef set; it is the robustness path for a target whose assemblies are all filtered out, such as a runtime-only standalone run when every DxMessaging test asmdef is editor-only.

When editing these workflows, keep every compute step carrying an `id` and every license-consuming step gated on `steps.<compute-id>.outputs.is-empty != 'true'`. Do not gate verify on is-empty, and do not remove the gated steps; mirror `unity-tests.yml`.

## When to Add a Unity Version

Add a version to the canonical `.github/unity-versions.json` `all` array when one of the following is true:

- A new LTS reaches general availability (e.g., when 2024.3 LTS or 7000.0 LTS ships) and the package's `package.json` `unity` field still permits it.
- A user files an issue reproducing only on a specific Editor version.
- Unity publishes a security patch on a currently-supported channel that the maintainer wants the gate to track.

## How to Add a Unity Version

1. Edit `.github/unity-versions.json` and append the new tag to the `all` array (keep it strictly ascending). `unity-tests.yml` reads this file at runtime, so the workflow itself needs no edit. Use the `unityci/editor` tag format (e.g., `2024.3.10f1`), then run `npm run validate:unity-versions`. See [Unity Version Single Source of Truth](../github-actions/unity-version-single-source.md).

1. Verify the Unity standalone CLI can install the requested version on the self-hosted Windows runner, or that the version already exists under `UNITY_EDITOR_INSTALL_ROOT` / `C:\Unity\Editors` / Unity Hub's install path.

1. Run the runner locally to validate the new version:

   ```bash
   bash scripts/unity/run-tests.sh --platform editmode --unity-version <new-version>
   ```

1. Push the workflow change. The first CI run will pull the new image (slow); subsequent runs hit the cache.

The `actions/cache@v4` keys include `${{ matrix.unity-version }}`, mode, OS, architecture, and hashes for package/test inputs plus `scripts/unity/run-ci-tests.ps1`. Do not add broad `restore-keys` for `Library/`; restoring a Library from a different Unity version or package graph can corrupt domain reloads and make failures nondeterministic. A new version should start cold and warm on the next exact-key run.

## IL2CPP-Only Failure Patterns

IL2CPP exercises an AOT-compiled path that EditMode/PlayMode under Mono cannot. These regressions historically slip past the Mono gate and only surface when a downstream consumer builds a player. The catalog:

| Pattern                                       | Signature in log                                                                   | Remediation                                                                                       |
| --------------------------------------------- | ---------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| Generic virtual method (GVM) call             | `ExecutionEngineException: Attempting to call method 'X' for which no AOT code...` | Add a non-generic forwarder, mark with `[Preserve]`, or instantiate the generic at compile time.  |
| Code stripping                                | `MissingMethodException` or `TypeLoadException` for a reflected type               | Add the type to `link.xml`, or annotate with `[Preserve]`. See Unity managed-code-stripping docs. |
| Reflection over open generics                 | Tests pass under Mono, fail under IL2CPP with reflection-related null returns      | Avoid open-generic reflection on the hot path; use the source generator instead.                  |
| Incremental Mono / IL2CPP serialization drift | `Library/` cache is stale and the build hangs at "Domain Reload"                   | Delete the Library cache (or bump the cache key prefix in the workflow); rebuild.                 |
| PInvoke / native-callable mismatch            | `EntryPointNotFoundException` or `MarshalAs` complaints unique to IL2CPP           | Audit `[DllImport]` signatures; verify calling convention.                                        |

The `avoid-reflection-on-hot-paths` skill (see Performance section of the index) covers reflection-related cases in detail. The DxMessaging codebase uses the source generator precisely to avoid most reflection at runtime.

## Reading Unity CI Logs

A direct Windows Unity job log is structured. To diagnose a failure, scan in this order:

1. **Pre-Unity setup**: `Setup Node.js`, `Cache Unity Library`, `Compute test assembly list`. Failures here are infrastructure, not test logic.
1. **License activation**: search for `LICENSE SYSTEM` or `Failed to activate`. The serial is activated (and returned) per run. See [unity-license-bootstrap](./unity-license-bootstrap.md) and [unity-license-return-guarantee](./unity-license-return-guarantee.md).
1. **Editor startup**: search for `[Licensing]` or `Loading native plugins`. A timeout here usually means a corrupted Library cache.
1. **Domain reload**: search for `Reloading assemblies`. A hang here typically means a circular asmdef reference or a missing dependency.
1. **Test execution**: search for `Run tests on platform`. NUnit failures appear as `[Test Failed]` lines with stack traces.
1. **Result emission**: search for `Test results saved at`. Missing results XML almost always means the player crashed before tests completed.

For `standalone` runs, the direct runner first configures the generated project for `StandaloneWindows64` IL2CPP, then runs Unity Test Framework with `-testPlatform StandaloneWindows64`. Build-stage failures are AOT or stripping; run-stage failures are runtime AOT or test-logic. The shared `verify-unity-results` composite asserts `total > 0` for every mode, so a crash mid-run that emits no results cannot look green.

## See Also

- [Unity Editor CLI Bootstrap](./unity-editor-cli-bootstrap.md)
- [Unity Version Single Source of Truth](../github-actions/unity-version-single-source.md)
- [Headless Test Runner](./headless-test-runner.md)
- [Unity License Bootstrap](./unity-license-bootstrap.md)
- [Unity Perf Test Isolation](./unity-perf-test-isolation.md)
- [CI/CD Devcontainer Workflows](../github-actions/cicd-devcontainer-workflows.md)

## References

- Unity CLI docs: https://docs.unity.com/en-us/hub/unity-cli
- game-ci docs (experiment only): https://game.ci/docs/
- Unity LTS roadmap: https://unity.com/releases/lts
- Unity managed code stripping: https://docs.unity3d.com/Manual/ManagedCodeStripping.html
- Active workflows: `.github/workflows/unity-tests.yml` (direct Unity on self-hosted Windows; editmode/playmode/standalone), `.github/workflows/unity-benchmarks.yml`, `.github/workflows/unity-gameci-experiment.yml` (manual-only experiment); ubuntu reference mirrors: `.github/workflows-disabled/`
