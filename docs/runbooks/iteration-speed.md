# Fast development and validation

Use the smallest check that can disprove the current change, then run the affected complete lane
before delivery. Keep exhaustive qualification available. Do not remove assertions, retry failures
into green, or present a partial lane as full release evidence.

## Measured causes

The investigation starts at `befd8c55` and is tracked in
[iteration-speed issue #757](https://github.com/Ambiguous-Interactive/DxMessaging/issues/757).
Retained evidence lives locally under `.artifacts/iteration-speed/`; earlier source data remains in
`progress/session264-ci-overhead.md` and `.artifacts/session506/`.

| Observation                    | Cost and implication                                                                                                                                                                                                     |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Session 506 local script suite | 1,595 seconds, including1,440 seconds in one PowerShell bundle. The directory entry point loaded all Node files into one process.                                                                                        |
| Retained managed TRX           | 36,703 cases,397 seconds summed test time;768 interceptor-retention cases consume277 seconds. Most large behavior matrices are cheap.                                                                                    |
| Recent PR workflow sample      | Latest 100 PR workflow records contain8 successful Unity runs, median 90.9 minutes;9 performance runs, median 64.2 minutes;10 static runs, median 13.3 minutes. These are descriptive samples, not population estimates. |
| Unity run 43568075301          | 189 minutes elapsed. Four jobs execute for 15.7-19.1 minutes; creation-to-start waits range 18.8-169.9 minutes. Those waits combine matrix serialization and runner availability.                                        |
| Environment                    | The exact SDK pin was absent while startup tolerated installation failure. An installer can exit successfully without making the SDK resolvable.                                                                         |

The expanded sample retains the latest 30 completed Unity PR runs plus their matching-head static
and performance workflows, including failures and cancellations. Among successful observations,
13 Unity runs have median 60.9 minutes,19 performance runs 46.2 minutes, and24 static runs 13.2 minutes.
The raw job timestamps and step durations are retained separately from these elapsed summaries.

The remedies target repeated work, serialized test discovery, redundant AST traversal and runner
contention. They do not establish improvements to runtime dispatch or game frame time.

## Local verification after the changes

The current-source managed inventory has 36,631 cases. Executed ordinary behavior 29,828,
representative retention 5,789, and exhaustive retention 1,014 are disjoint and exactly match the
unfiltered discovered case names; all passed without skips. Three fast-lane samples total
27.08, 24.67 and 24.56 seconds of test execution (median 24.67), excluding build, discovery and restore.
Three script-unit runs pass 110 cases with median 1.47 seconds wall time.

The remaining cache-test delay had a separate cause: 142,205 direct entries under `/tmp` made
PowerShell's case-correcting path provider repeatedly scan ancestor siblings. Three live stack
samples identify `GetCorrectCasedPath`. On the same retained inventory, original/candidate/candidate/
original inspection takes 25,264/15.82/2.69/25,315 milliseconds. The output records match after
excluding only elapsed time and observed drive free space. Standard `System.IO` calls avoid those
ancestor scans; all 36 existing safety cases pass in 48.50 seconds with the guarded deleter unchanged.
No unowned temporary directories were removed to obtain the result.

The pinned organization analyzer reports 21 findings for both the current default-branch baseline
and the integrated workflows, with zero added findings. Its literal-matrix acceptance and rejected
dynamic-axis controls pass. Those existing findings remain separate audit debt.

These local observations do not establish hosted PR queue time, cross-platform timings or a
cold-install result. The original interrupted diagnostic and failed stale-contract test are retained;
a later green run does not erase them.

## Local commands

| Change               | First feedback                                                                                                                                                           | Before delivery                                                                                            |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------- |
| Script unit behavior | `npm run test:unit` or `node --test <changed-test>`                                                                                                                      | `npm test`                                                                                                 |
| Script integration   | `node --test <integration-test>`                                                                                                                                         | `npm run test:integration`, then the complete suite once                                                   |
| Runtime C#           | `dotnet test .net-tests/DxMessaging.Net.Tests.csproj --configuration Release --filter 'TestCategory!=Exhaustive'`                                                        | Affected fixtures without a tier filter; complete managed validation for shared lifecycle or memory owners |
| Compiler/analyzer    | `dotnet test SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests/WallstopStudios.DxMessaging.SourceGenerators.Tests.csproj /p:CopyAnalyzerPayload=false` | Complete generator suite and `npm run check:analyzers`                                                     |
| Documentation C#     | `dotnet test .docs-tests/WallstopStudios.DxMessaging.Docs.Tests.csproj`                                                                                                  | Relevant prose checks and strict site build                                                                |
| Unity behavior       | Affected fixture through the host MCP bridge                                                                                                                             | Affected Unity scopes; CI supplies Windows IL2CPP evidence                                                 |

Repository scripts require Node 24 or later (the devcontainer and CI pin 26).

`test:unit` and `test:integration` select disjoint file sets whose union is the full Node inventory.
New files conservatively enter integration until classified. All commands use the standard Node
runner with four file processes. `npm test` retains all Node and Python checks.

The managed fast tier excludes only cases marked `Exhaustive`. CI runs ordinary behaviors and
representative `MemoryReclaim` cases in separate processes so GC probes do not repeatedly collect
the accumulated result graphs from tens of thousands of behavioral cases. `FastRetention` samples keep every
message kind and boundary axis represented, including the two reported interceptor-retention
failures. The complete case names, assertions and collection boundaries remain unchanged. Changes
to shared memory/lifecycle owners or their fixtures select the exhaustive remainder as well.
Nightly and manual static validation run both tiers and retain raw TRX counts.

Run changed-file Prettier, markdownlint, CSharpier and pre-commit hooks while editing. Do not run
whole-repository audits after every edit. Restore the exact SDK and local tools before work starts;
container creation and restart verify SDK resolution synchronously.

## CI lanes

- PRs retain the three literal required contexts: `CI Success`, `Unity CI Success`, and
  `Devcontainer CI Success`. No ruleset change is needed.
- Runtime-affecting trusted PRs run the newest supported Unity version in all three correctness
  modes. Forks and Dependabot retain their existing credential boundary.
- Nightly correctness dispatch runs the complete supported Unity matrix. Static CI separately runs
  both managed tiers nightly. Nightly performance uses a later slot and the existing exact-source
  publication pipeline. Ordinary PRs and merges do not launch performance work.
- Manual Unity dispatch retains full compatibility, shipping-fidelity and consumer-install modes.
  A release needs successful exact-revision full compatibility, all managed cases, shipping,
  consumer-install and performance evidence; a prior night's result is not qualification for a
  changed tag. The release workflow verifies its required evidence before publication.
- External URL liveness stays in the existing daily advisory scan. Internal links and anchors
  remain required. Devcontainer CI builds only for image or smoke inputs and runs environment
  smoke checks rather than repeating the complete script suite.

The central organization contract currently admits manual licensed workflows but does not enroll
DxMessaging schedules. A small hosted dispatcher invokes those supported manual entry points.
It does not create another license lock. The dispatcher checks active/successful source-revision
runs and propagates lookup or dispatch failures.

The existing documentation gate remains because the pinned central analyzer recognizes its
trusted-skip shape. Consolidating it with the four-version matrix would require a central contract
change or duplicated licensed jobs. Keep both gates' documentation allowlists synchronized until
that upstream capability exists. Unknown paths and incomplete listings must not skip validation.

## Dedicated Unity package host

From the host checkout, generate a package-only host with the existing runner. Container-generated
absolute package paths do not resolve on the host. Select an editor already installed on the
host; the following version matches the observed local editor, not a new support promise:

```sh
pwsh -NoProfile -File scripts/unity/run-ci-tests.ps1 \
  -UnityVersion 6000.4.6f1 -TestMode playmode \
  -AssemblyNames WallstopStudios.DxMessaging.Tests.Runtime \
  -ArtifactsPath .artifacts/local-host \
  -ProjectPath .artifacts/u/local-playmode -GenerateOnly -SkipCiAnalyzers
```

This prepares files without launching Unity, activating a license or touching the game project.
The generated correctness manifest contains only the test framework and the package. EditMode
hosts additionally compile pinned DI samples; comparison packages require `-IncludeComparisons`.
The existing committed comparison host remains available for parity checks.

On the host, open the generated project with its matching installed editor. Connect it through the
[existing MCP setup](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/scripts/mcp/README.md#prepare-the-host), using a separate port, token and
project path from the game project's connection. Pipeline setup is an explicit host operation;
project generation alone does not install that bridge or establish native execution.

Keep one integration owner for the host and its embedded checkout. Do not switch the shared
checkout, migrate foreign assemblies, refresh assets or launch a second run during active tests.
Verify loaded source identity, the run GUID, result and cleanup sidecars, clean scenes, and framework
inactivity. Mono/editor tests do not replace IL2CPP or stripping qualification.

## Coordinator experiment and ongoing measurement

The [task-workflow skill](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/skills/agent-task-workflow/SKILL.md) defines the bounded pilot:
one coordinator/integration owner and at most two independent workers in separate worktrees.
A task packet names its base revision, allowed files, RED/GREEN checks, stopping condition and
returned evidence. Workers do not push or operate the shared editor.

This implementation is one feasibility trial. Before making coordination the default, compare
three matched task pairs, alternate order, and retain completion time, agent effort, conflicts,
repeated checks and rework. Adopt only with at least 20% lower median completion time, at most 25%
more effort, and equivalent validation. Keep coupled changes under one owner.

For each speed change retain the old and new source revisions, tool versions, host, cold/warm cache
state and test inventory. Separate runner queue, lock wait, setup/import, compilation, actual test
time, redaction and upload. Use three warm samples plus one cold sample on the same host. Stop
when evidence does not support the claimed cause. Report unmeasured phases explicitly.

Initial targets are unit feedback under 30 seconds, focused managed feedback under 60 seconds, complete
scripts at least 75% faster than the retained baseline, ordinary static CI under 5 minutes, and latest
Unity execution under 20 minutes. Queue time is separate. These are acceptance targets, not measured
hosted results or reasons to weaken coverage.

The approach uses native isolation, short context entry points and optional parallel ownership:
[Node's execution model](https://nodejs.org/api/test.html#test-runner-execution-model),
[OpenAI's harness-engineering account](https://openai.com/index/harness-engineering/), and
[Anthropic's coordination findings](https://www.anthropic.com/research/multi-agent-research-system).
Unity's [assembly boundaries](https://docs.unity.com/en-us/engine/6000.0/manual/programming-environment/script-compilation/assembly-definition-files)
and [configurable Play Mode](https://docs.unity.com/en-us/engine/6000.0/manual/scripting/compilation-and-code-reload/code-reloading-editor/configurable-enter-play-mode)
keep engine iteration focused; static-reset tests are required when reload is disabled.
