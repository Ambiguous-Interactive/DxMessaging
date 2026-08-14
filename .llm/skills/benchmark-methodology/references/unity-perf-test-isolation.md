<!-- trigger: unity, perf, benchmark, allocation, comparison, isolation, asmdef | Perf-asmdef classification and default-run exclusion contract | Core -->

# Unity Perf Test Isolation

> **One-line summary**: Asmdefs whose name matches `Benchmarks|Allocations` are classified as `perf`; `Comparisons` assemblies are a separate external-package opt-in. Both are excluded from default local Unity runs by `scripts/unity/lib/asmdef-discovery.js`.

## When to Use

- Adding a new benchmark, allocation-counting, or library-comparison test suite.
- Investigating why a perf-looking asmdef does or does not run on a PR.
- Debugging a "0 tests ran" CI failure when the suite name pattern is suspect.
- Verifying the default Unity run still excludes perf after a refactor.

## When NOT to Use

- Adding a regular correctness test. Those are `core` and run by default; no isolation work is needed.
- Adding a DI integration suite (VContainer / Zenject / Reflex). Those have their own classification (`integration`) and opt-in flag.

## The Rule

Source-of-truth is `.llm/context.md` line 114:

> Benchmark and performance/allocation tests must stay isolated from the standard test suite.

Operationally, this is enforced by classification in `scripts/unity/lib/asmdef-discovery.js`:

```js
const PERF_NAME_REGEX = /(?:Benchmarks|Allocations)/;
const COMPARISON_NAME_REGEX = /(?:Comparisons)/;
```

Any asmdef under `Tests/` whose `name` field contains `Benchmarks` or `Allocations` is classified as `perf`; `Comparisons` is classified as `comparison` because those suites depend on external comparison packages that are not in the default harness manifest. The generated CI manifest in `scripts/unity/run-ci-tests.ps1` includes `com.unity.test-framework.performance`, because benchmark and allocation asmdefs reference `Unity.PerformanceTesting`. Three things have to be true for the isolation to hold:

1. Perf assemblies live under `Tests/Editor/Benchmarks`, `Tests/Editor/Allocations`, `Tests/Runtime/Comparisons`, or `Tests/Runtime/Benchmarks`.
1. Their asmdef `name` field contains `Benchmarks`, `Allocations`, or `Comparisons` so classification matches.
1. They are NOT mentioned by name in any workflow's `customParameters`. The workflow reads its assembly list from `defaultIncludeAssemblies()`, never from a hand-edited list.

## How Exclusion Works

`scripts/unity/lib/asmdef-discovery.js` exports `defaultIncludeAssemblies(repoRoot, options)`. The behaviour:

| Asmdef Class  | Default Include? | Opt-in Flag                                                 |
| ------------- | ---------------- | ----------------------------------------------------------- |
| `core`        | Yes              | (always on)                                                 |
| `perf`        | No               | `{ includePerf: true }` or `--include-perf`                 |
| `comparison`  | No               | `{ includeComparisons: true }` or `--include-comparisons`   |
| `integration` | No               | `{ includeIntegrations: true }` or `--include-integrations` |

Consumers of this module:

- `scripts/unity/run-ci-tests.ps1` builds its assembly list at startup and passes it to Unity via `-assemblyNames`.
- The active workflows under `.github/workflows/unity-*.yml` resolve the list through the `.github/actions/compute-unity-assemblies` composite action, which calls the same asmdef-discovery module -- no hand-maintained lists.
- The active `unity-benchmarks.yml` passes `include-perf: "true"` to that composite (which calls `defaultIncludeAssemblies(process.cwd(), { includePerf: true })`) and skips integrations plus external comparisons. The `.github/workflows-disabled/*` files are the ubuntu reference mirrors of the active self-hosted Windows workflows.
- The local [Unity MCP Test Loop](../../unity-mcp-test-loop/references/mcp-test-loop.md) picks its `DxMcpTestRunner.Run` assembly filter by hand; keep that choice consistent with this module's classification.

Because every CI caller goes through the same module, adding a new perf asmdef requires no edits to the workflows or the CI runner script.

## Adding a New Perf Asmdef

1. Place the asmdef under `Tests/Editor/Benchmarks/`, `Tests/Editor/Allocations/`, or `Tests/Runtime/Benchmarks/`.
1. Set its `name` field to include one of the magic substrings. Examples that match:
   - `WallstopStudios.DxMessaging.Tests.Editor.Benchmarks.Dispatch`
   - `WallstopStudios.DxMessaging.Tests.Runtime.Allocations.Pooling`
1. Verify classification:

   ```bash
   node scripts/unity/lib/asmdef-discovery.js
   ```

   The output groups asmdefs by category. Confirm the new entry shows `[perf]`.

1. Confirm the default include list excludes it:

   ```bash
   node scripts/unity/lib/asmdef-discovery.js
   ```

   The new perf asmdef should be grouped under `[perf]`, NOT in the default
   `core` include set. To run it locally, drive the benchmark assemblies through
   `DxMcpTestRunner.Run` over the MCP loop (see
   [Unity MCP Test Loop](../../unity-mcp-test-loop/references/mcp-test-loop.md)); the default loop run does not pull
   perf assemblies unless you name them explicitly.

If the asmdef ends up in the `core` bucket instead, the most common cause is the `name` field missing the magic substring. Rename the asmdef (and its file) so the substring is present.

## Where Perf Actually Runs

| Workflow               | Triggers                            | Includes Perf? |
| ---------------------- | ----------------------------------- | -------------- |
| `unity-tests.yml`      | PR / default-branch push / dispatch | NO             |
| `unity-benchmarks.yml` | dispatch                            | YES            |

The active `.github/workflows/unity-*.yml` workflows run Unity directly on
self-hosted Windows runners through `scripts/unity/run-ci-tests.ps1` (benchmarks
included). The `.github/workflows-disabled/*` files are the ubuntu reference
mirrors kept for parity, not the live templates. Note: each editor-scoped
`unity-tests.yml` job runs `standalone` after EditMode and PlayMode; the direct
runner maps it to `StandaloneWindows64` and configures IL2CPP in the generated project.
Verify the active workflows still exist any time you edit them:

```bash
test -e .github/workflows/unity-tests.yml
test -e .github/workflows/unity-benchmarks.yml
```

## Comparison Suites

Comparison asmdefs live under `Tests/Runtime/Comparisons/` (with bridges under `Tests/Runtime/Comparisons/External/` and `Tests/Runtime/Comparisons/UnityAtoms/`) and benchmark against external libraries such as MessagePipe, UniRx, UniTask, Zenject, and Unity Atoms. They are a separate opt-in from `--include-perf`: enable them via `includeComparisons` (the `compute-unity-assemblies` action's `include-comparisons` input, or `run-ci-tests.ps1 -IncludeComparisons`).

The external comparison packages and their required Unity built-in packages are installed from the single source `.github/comparison-packages.json` (registry scopes + PINNED versions + Unity built-ins). `run-ci-tests.ps1 -IncludeComparisons` injects that registry, those pins, and those built-ins into the ephemeral comparison manifest; the committed `.unity-test-project/Packages/manifest.json` and `.unity-test-project/Packages/packages-lock.json` mirror them for local parity. Bump versions/modules in the JSON only; see [Comparison Parity and Package Single Source](../../test-code-quality/references/comparison-parity-and-package-single-source.md).

Comparisons RUN in CI, not only as a manual local manifest step: `perf-numbers.yml` runs isolated internal and comparison matrix entries on every eligible pull request and push, and the comparison entry alone passes `include-comparisons: true` to the composite. To run them locally, the host project's manifest must already include the external comparison packages; then drive the comparison assemblies through `DxMcpTestRunner.Run` over the MCP loop (see [Unity MCP Test Loop](../../unity-mcp-test-loop/references/mcp-test-loop.md)). Unity compiles each external bridge only when its package is present, because the asmdef `versionDefines` (sourced from `.github/comparison-packages.json`) guard each bridge; the zero-dependency baselines always compile.

## Classification Invariants

When adding or moving test asmdefs, keep these invariants honest (the exclusion list is computed by `scripts/unity/lib/asmdef-discovery.js`, not hand-maintained):

- Every asmdef matching the perf regex is classified as `perf`.
- Every asmdef NOT matching the perf or integration regex is classified as `core` and appears in `defaultIncludeAssemblies(repo)`.
- Workflows resolve their assembly lists via `defaultIncludeAssemblies` rather than hand-rolled YAML; benchmark workflows opt into perf via `{ includePerf: true }`.

## See Also

- [Unity MCP Test Loop](../../unity-mcp-test-loop/references/mcp-test-loop.md)
- [Unity CI Matrix](../../unity-editor-ci/references/unity-ci-matrix.md)
- [UPM Test Harness](../../unity-test-execution/references/upm-test-harness.md)
- [Devcontainer Cache Contract](../../unity-editor-conventions/references/devcontainer-cache-contract.md)

## References

- Source: `scripts/unity/lib/asmdef-discovery.js`
- Source-of-truth: `.llm/context.md`
- Active workflows: `.github/workflows/unity-tests.yml`, `.github/workflows/unity-benchmarks.yml` (direct Unity on self-hosted Windows)
- Shared composite: `.github/actions/compute-unity-assemblies/action.yml`
- Ubuntu reference mirrors: `.github/workflows-disabled/unity-tests.yml`, `.github/workflows-disabled/unity-benchmarks.yml`
