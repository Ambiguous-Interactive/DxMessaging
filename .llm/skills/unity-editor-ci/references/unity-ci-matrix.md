<!-- trigger: unity, ci, matrix, il2cpp, lts, game-ci, version | Unity version matrix and IL2CPP-only failure patterns | Core -->

# Unity CI Matrix

> **One-line summary**: The active Unity workflows under `.github/workflows/` run `scripts/unity/run-ci-tests.ps1` on self-hosted Windows runners: `unity-tests.yml` is one unified matrix of four Unity versions x {editmode, playmode, standalone} = 12 jobs, where `standalone` builds and runs a `StandaloneWindows64` IL2CPP player from a runner-local project.

## When to Use

- Adding a new Unity LTS release to the supported set.
- Triaging an IL2CPP-only test failure that does not reproduce in EditMode.
- Investigating a Unity CI log that fails before any test prints output.
- Deciding whether to expand or contract the matrix to balance signal vs runtime.

## When NOT to Use

- Tweaking which assemblies run. That is the asmdef-discovery module's responsibility (see [unity-perf-test-isolation](../../benchmark-methodology/references/unity-perf-test-isolation.md)).
- Changing the project or package-cache layout without preserving separation between test, benchmark, and performance scopes.

## Current Matrix

`unity-tests.yml` (active; direct Unity on self-hosted Windows; one unified matrix):

| Axis            | Values                                                    |
| --------------- | --------------------------------------------------------- |
| `unity-version` | `2021.3.45f1`, `2022.3.45f1`, `6000.3.16f1`, `6000.5.2f1` |
| `test-mode`     | `editmode`, `playmode`, `standalone`                      |

Twelve matrix cells. `editmode`/`playmode` run in-editor on Mono; `standalone`
builds and runs a `StandaloneWindows64` IL2CPP player. The direct runner
generates a package host project under
`$RUNNER_WORKSPACE/dxm-u/t/<version>-<mode>/`, imports the repo package with a
`file:` dependency, sets `testables`, and configures IL2CPP before running
standalone tests. Dispatch runs use the same complete static matrix.

The Unity version list is canonical in `.github/unity-versions.json`;
`unity-tests.yml` carries a static literal mirror so the organization analyzer
can attest every matrix identity. Bump the canonical file and every validator
reported mirror together -- see
[Unity Version Single Source of Truth](./unity-version-single-source.md).

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
[unity-license-return-guarantee](../../unity-licensing/references/unity-license-return-guarantee.md).

## Capacity + Timeout Invariant

The Unity serial has two activation seats shared across the organization and no server-side reclaim. Two complementary controls keep that capacity safe and fair.

**Matrix serialization and organization admission.** `strategy.max-parallel: 1` serializes matrix cells WITHIN a single run. The external `ambiguous-organization-build-lock` action admits at most two distinct runners ACROSS runs, workflows, and repositories while accounting for cooldowns, quarantines, and account incidents.

- `max-parallel: 1` only: cannot prevent two separate runs (two pushes, `unity-tests` plus `unity-benchmarks`, or another org repo) from racing for the seat.
- The lock only: leaves all 12 cells spawning at once, so idle cells burn their job-timeout clocks, one repository can occupy both seats, and logs become noisy without useful per-run throughput.

With both controls, a run consumes at most one seat while another repository can use the second. This is `max-parallel: 1` ONLY -- it is NOT a native concurrency group. A native `concurrency.group: wallstop-organization-builds` is repository-scoped, serializes whole jobs, and is forbidden. Add `max-parallel: 1` under `strategy:` (sibling of `fail-fast`/`matrix`) on the matrix workflows (`unity-tests.yml`, `unity-benchmarks.yml`, `perf-numbers.yml`); single-job release workflows rely on the lock for cross-run admission.

**Timeout invariant.** GitHub counts the lock wait and every subsequent step against the job clock. A job-level timeout cancels the whole job, so later `if: always()` cleanup cannot run if an earlier uncapped step consumes the remaining clock. Every step before and including `Require confirmed Unity cleanup` therefore has an explicit positive timeout.

```text
job timeout-minutes >= sum(all capped steps through cleanup gate) + 60
```

Editor validation is capped at 10 minutes. The acquire input `timeout-minutes: "300"` is the internal lock-poll budget. Its enclosing step has `timeout-minutes: 305`, so the action can finish and report a timeout before GitHub terminates the step. Licensed work is capped at 120 to 180 minutes, and return/classify/release/gate at 5/2/5/2 minutes. The licensed jobs use `timeout-minutes: 900`. `scripts/validate-unity-pr-policy.py` sums every enforced step cap through the cleanup gate and requires at least 60 minutes of remaining job time.

The step-level caps protect the in-use seat from a hung editor or ancillary action. They must remain strictly below the job timeout so the step fails first and the cleanup chain still runs. This matters because `stuck-job-watchdog.yml` ignores any `in_progress` job; without step caps, a wedged action can squat the seat until the whole job is cancelled.

Runner administrators manually install every exact editor and required module under `RUNNER_TOOL_CACHE/u6-v3`. Workflows validate that root with `-CiManagedOnly -RequireHealthyExisting` before acquiring the organization lock; they never install or repair editors. Release the lock only after the always-run return and cleanup-classification steps.

**Operator note (standalone IL2CPP):** the `standalone` cells require the Windows IL2CPP Unity module and the host build toolchain needed by Unity for Windows players. `scripts/unity/ensure-editor.ps1` must be called with an explicit provisioning profile: `EditorOnly` for editmode/playmode/benchmarks/release checks, and `StandaloneWindowsIl2Cpp` for standalone so `windows-il2cpp` is verified. See [unity-editor-cli-bootstrap](./unity-editor-cli-bootstrap.md) for manual maintenance details.

`unity-benchmarks.yml` (active; manual-only, NEVER on PRs):

| Axis            | Values                  |
| --------------- | ----------------------- |
| `unity-version` | every canonical version |
| `test-mode`     | `editmode`, `playmode`  |

The active `unity-benchmarks.yml` explicitly omits `pull_request` and `push` per the perf isolation rule.

## compute-unity-assemblies is-empty Gate

Every workflow that consumes `./.github/actions/compute-unity-assemblies` mirrors the canonical wiring in `unity-tests.yml`: the compute step carries `id: compute`, and editor validation plus Unity work skip when `is-empty == 'true'`. Lock acquisition remains unconditional because the static matrix is structurally non-empty and the organization analyzer must prove each acquisition. When asmdef discovery resolves no owned assemblies, verification treats the empty selection as an intentional skip while the terminal return/classify/release/gate chain still proves cleanup.

The `Verify tests actually ran` step keeps a cancellation-safe gate and must also require `steps.compute.outcome == 'success'` plus either `steps.compute.outputs.is-empty == 'true'` or a non-skipped Unity run step (never an is-empty gate alone). It receives `expected-empty: ${{ steps.compute.outputs.is-empty }}`, so an intentional skip reads as success rather than a "tests did not run" failure, while checkout/cache/setup/editor-validation/lock failures that prevent Unity from launching are not obscured by a generic missing-results annotation. The skip path does not fire for the current asmdef set; it is the robustness path for a target whose assemblies are all filtered out, such as a runtime-only standalone run when every DxMessaging test asmdef is editor-only.

When editing these workflows, keep every compute step carrying an `id` and every license-consuming step gated on `steps.<compute-id>.outputs.is-empty != 'true'`. Do not gate verify on is-empty, and do not remove the gated steps; mirror `unity-tests.yml`.

## When to Add a Unity Version

Add a version to the canonical `.github/unity-versions.json` `all` array when one of the following is true:

- A new LTS reaches general availability (e.g., when 2024.3 LTS or 7000.0 LTS ships) and the package's `package.json` `unity` field still permits it.
- A user files an issue reproducing only on a specific Editor version.
- Unity publishes a security patch on a currently-supported channel that the maintainer wants the gate to track.

## How to Add a Unity Version

1. Edit `.github/unity-versions.json` and append the new tag to the `all` array
   (keep it strictly ascending). Update the static matrices reported by
   `npm run validate:unity-versions`, including `unity-tests.yml` and
   `unity-benchmarks.yml`. Use the Unity tag format (for example,
   `2024.3.10f1`). See
   [Unity Version Single Source of Truth](./unity-version-single-source.md).

1. Have a runner administrator install the exact editor and required modules
   under that runner's `RUNNER_TOOL_CACHE/u6-v3/<version>/Editor/Unity.exe`
   path, then run the validation-only runner audit.

1. Validate the new version locally via the MCP loop against the host editor.
   The host editor must be running that exact Unity version; then run the EditMode
   suite through `DxMcpTestRunner.Run` over `Unity_RunCommand`. See
   [Unity MCP Test Loop](../../unity-mcp-test-loop/references/mcp-test-loop.md).

1. Push the workflow change. The first run on each fixed runner starts with a cold project; later runs reuse that runner's `Library` in place.

The test, benchmark, and performance workflows use separate `t`, `b`, and `p` roots. Unity versions and modes also have separate projects. This keeps incompatible package graphs apart while each fixed runner retains its own warm `Library`; a new version starts cold on every runner.

## IL2CPP-Only Failure Patterns

IL2CPP exercises an AOT-compiled path that EditMode/PlayMode under Mono cannot. These regressions historically slip past the Mono gate and only surface when a downstream consumer builds a player. The catalog:

| Pattern                                       | Signature in log                                                                   | Remediation                                                                                       |
| --------------------------------------------- | ---------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| Generic virtual method (GVM) call             | `ExecutionEngineException: Attempting to call method 'X' for which no AOT code...` | Add a non-generic forwarder, mark with `[Preserve]`, or instantiate the generic at compile time.  |
| Code stripping                                | `MissingMethodException` or `TypeLoadException` for a reflected type               | Add the type to `link.xml`, or annotate with `[Preserve]`. See Unity managed-code-stripping docs. |
| Reflection over open generics                 | Tests pass under Mono, fail under IL2CPP with reflection-related null returns      | Avoid open-generic reflection on the hot path; use the source generator instead.                  |
| Incremental Mono / IL2CPP serialization drift | `Library/` is stale and the build hangs at "Domain Reload"                         | Delete only the affected runner-local project; rebuild.                                           |
| PInvoke / native-callable mismatch            | `EntryPointNotFoundException` or `MarshalAs` complaints unique to IL2CPP           | Audit `[DllImport]` signatures; verify calling convention.                                        |

The `avoid-reflection-on-hot-paths` skill (see Performance section of the index) covers reflection-related cases in detail. The DxMessaging codebase uses the source generator precisely to avoid most reflection at runtime.

## Reading Unity CI Logs

A direct Windows Unity job log is structured. To diagnose a failure, scan in this order:

1. **Pre-Unity setup**: `Setup Node.js`, `Compute test assembly list`, and the `LibraryState` diagnostic. Failures here are infrastructure, not test logic.
1. **License activation**: search for `LICENSE SYSTEM` or `Failed to activate`. The serial is activated (and returned) per run. See [unity-license-bootstrap](../../unity-licensing/references/unity-license-bootstrap.md) and [unity-license-return-guarantee](../../unity-licensing/references/unity-license-return-guarantee.md).
1. **Editor startup**: search for `[Licensing]` or `Loading native plugins`. A timeout here usually means a corrupted runner-local project `Library`.
1. **Domain reload**: search for `Reloading assemblies`. A hang here typically means a circular asmdef reference or a missing dependency.
1. **Test execution**: search for `Run tests on platform`. NUnit failures appear as `[Test Failed]` lines with stack traces.
1. **Result emission**: search for `Test results saved at`. Missing results XML almost always means the player crashed before tests completed.

For `standalone` runs, the direct runner first configures the generated project for `StandaloneWindows64` IL2CPP, then runs Unity Test Framework with `-testPlatform StandaloneWindows64`. Build-stage failures are AOT or stripping; run-stage failures are runtime AOT or test-logic. The shared `verify-unity-results` composite asserts `total > 0` for every mode, so a crash mid-run that emits no results cannot look green.

## See Also

- [Unity Editor CLI Bootstrap](./unity-editor-cli-bootstrap.md)
- [Unity Version Single Source of Truth](./unity-version-single-source.md)
- [Unity MCP Test Loop](../../unity-mcp-test-loop/references/mcp-test-loop.md)
- [Unity License Bootstrap](../../unity-licensing/references/unity-license-bootstrap.md)
- [Unity Perf Test Isolation](../../benchmark-methodology/references/unity-perf-test-isolation.md)
- [CI/CD Devcontainer Workflows](../../github-workflow-consistency/references/cicd-devcontainer-workflows.md)

## References

- Unity CLI docs: https://docs.unity.com/en-us/hub/unity-cli
- Unity LTS roadmap: https://unity.com/releases/lts
- Unity managed code stripping: https://docs.unity3d.com/Manual/ManagedCodeStripping.html
- Active workflows: `.github/workflows/unity-tests.yml` (direct Unity on self-hosted Windows; editmode/playmode/standalone), `.github/workflows/unity-benchmarks.yml`, `.github/workflows/perf-numbers.yml`, and `.github/workflows/release.yml`; ubuntu reference mirrors: `.github/workflows-disabled/`
