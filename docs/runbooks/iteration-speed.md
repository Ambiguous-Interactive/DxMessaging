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
The documented combined fast filter took 68.92 seconds end to end. Running behavior and retention
in separate processes passes the same 35,617 cases in 34.84, 34.86 and 33.50 seconds (median 34.84),
including warm restore, build and discovery. The combined control has one observation; cold setup
remains unmeasured. The local commands below retain this process split.
Three script-unit runs pass 110 cases with median 1.47 seconds wall time.
Three complete runs each pass 1,499 Node and 108 Python tests without skips. Node durations are
229.90, 220.85 and 219.58 seconds; adding the two Python suites gives a median 229.26 seconds
before npm startup overhead. That is about 86% shorter than the retained 1,595-second historical
run, whose different source revision and cache state limit a controlled speedup claim.

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

## Save to a focused test verdict

Target p95 below one second from a warm production save to its named assertion.
For a Node helper, start a watcher once and edit its imported production dependency.
Select the affected case by its name in the test file. For example:

```sh
node --test --watch --test-name-pattern="^normalizeToLf" scripts/lib/__tests__/lib.test.js
```

Confirm the expected test name and a positive executed count. Keep the watcher open across a broken
save and its fix. Use the whole file when multiple behaviors change. For runtime C#, restore once,
then run the affected test with compilation enabled on every edit:

```sh
dotnet test .net-tests/DxMessaging.Net.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~AotSelectionAttributeRejectsNullType'
```

On a mounted checkout at `1362ebb1`, four Node helpers each received five broken/fixed pairs
with the test source held constant. All 40 saves produced the expected named verdict. Median
save-to-result was 0.311 seconds and nearest-rank p95 was 0.382 seconds, including the runner
summary. A single run without `--watch` also works for a one-off check. These observations cover
Node 26 in the Linux devcontainer. See the [Node watch documentation](https://nodejs.org/api/test.html#watch-mode).

C# results depend on rebuilding the changed dependency. On a Linux-local worktree at `864fc5f8`,
three matched broken/fixed pairs per command took a median 2.99 seconds per runtime edit
with the filtered command and 3.92 seconds with `dotnet watch test`. Generator edits took
2.68 and 3.27 seconds respectively. Each comparison alternated command order. The direct
command remains the measured choice for both. Preserve `/p:CopyAnalyzerPayload=false` on generator
runs. With fresh project outputs and private empty NuGet caches, one initial run took
6.89 seconds for runtime and 6.88 seconds for generators at `1362ebb1`. These six-edit screens
establish medians only. Local Unity Editor and desktop Test Explorer save times are unmeasured
in this editor-free checkout. For command syntax and trial details, see the
[.NET watch reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-watch) and the
[experiment record](https://github.com/Ambiguous-Interactive/DxMessaging/issues/817).

A fixed-input Attributes experiment on `864fc5f8` reused a dedicated warm Roslyn compiler server
with SDK 10.0.404 and ran the same named NUnit assertion through VSTest after each save. It
alternated 20 broken and 20 fixed saves per assertion per compiler mode. For the null-type check,
nearest-rank p95 fell from 1.019 to 0.784 seconds. For the exact-type check, it fell from 1.042 to
0.785 seconds. All 80 shared-compiler saves produced the expected verdict in under one second with
the expected fresh binary. The cold server needed 0.062 seconds to accept requests, then 0.983
seconds for its first compile and verdict.

These measurements cover two edits of one Attributes constructor with a fixed SDK response in
the Linux devcontainer. Use the filtered `dotnet test` command above when project inputs change.
[Attributes shared compiler study](https://github.com/Ambiguous-Interactive/DxMessaging/issues/817#issuecomment-7098607883).

A follow-up screened an edit to runtime registration-group cleanup and a generator diagnostic
edit. Six measured saves per compiler mode gave shared-compiler medians of 1.194 and 1.261 seconds,
down from 1.422 and 1.762 with the direct compiler. Both missed the one-second screen and stopped
before the 40-save confirmation. Every save produced the expected NUnit verdict and fresh binary.
The runtime, generator, and Editor targets remain open.

With six extra verified compile-and-verdict warmup requests, the medians fell from 1.344 to
1.131 seconds for the runtime edit and from 1.534 to 1.205 seconds for the generator edit.
All six measured saves per warmed lane exceeded one second. Median compiler time fell to
0.224 and 0.251 seconds, while VSTest still took 0.883 and 0.963 seconds. The first cold verdict
and six warmup requests cost 10.339 and 11.602 seconds before measurement. These screens
stopped before the 40-save confirmation.
[Compiler warmup results](https://github.com/Ambiguous-Interactive/DxMessaging/issues/817#issuecomment-7113483433).
[Runtime and generator screen](https://github.com/Ambiguous-Interactive/DxMessaging/issues/817#issuecomment-7098746835).

Twelve diagnostic and plain VSTest runs each executed one named test on an existing binary.
For the runtime case, the median time from the first trace marker to case start was 0.682 seconds.
Adapter discovery took 0.359 seconds, and the test case callbacks spanned 0.016 seconds.
For the generator case, the corresponding times were 0.406, 0.115, and 0.447 seconds.
Trace times include logging overhead. Switching to NUnit's Microsoft Testing Platform executable
mode with its test reporter was slower in all six matched invocation pairs.
[VSTest traces](https://github.com/Ambiguous-Interactive/DxMessaging/issues/817#issuecomment-7113553006).
[Executable runner study](https://github.com/Ambiguous-Interactive/DxMessaging/issues/817#issuecomment-7113645310).

NUnitLite ran the same named cases from the same binaries. In three pairs per case, its median
invocation took 0.327 seconds for runtime and 1.083 seconds for the generator, compared with 2.109
and 2.189 seconds for VSTest. The generator stopped at invocation timing. On six paired runtime
saves per runner, the median including compilation was 3.394 seconds for NUnitLite and 7.145
seconds for VSTest. The host load rose during the trials. All measured saves exceeded one second.
Use the filtered commands above for the supported runner. [NUnitLite results](https://github.com/Ambiguous-Interactive/DxMessaging/issues/817#issuecomment-7140441945).

In a separate repeat, the runtime NUnitLite arm passed all six screen saves. It finished
36 of 40 confirmation saves under one second, with a median of 0.533 seconds and a measured
p95 of 1.062 seconds. VSTest had a median of 1.504 seconds and a p95 of 2.530 seconds. All
four misses came in the final two pairs, when sampled host CPU pressure exceeded the
predeclared bound. The generator screen stopped after six NUnitLite saves. Its fastest
save took 1.219 seconds. Both studies used one named case per project with alternating
broken and repaired inputs. [Confirmation results](https://github.com/Ambiguous-Interactive/DxMessaging/issues/817#issuecomment-7141092214).

## Rolling PR latency

Target p95 below 420 seconds from a PR update to the last required verdict, including queue
and cleanup. Run the read-only report from the checkout. A read token in `GH_TOKEN` or `GITHUB_TOKEN`
provides enough API quota for the default 40-update window. The command uses Python 3.11 or
later. Its JSON includes each required verdict, prior attempts, native steps and the interval
from runner preflight to native setup. It reports sample sizes, missing observations and
nearest-rank p95 by validation lane.

```sh
python3 scripts/pr_latency.py --count 40
```

Use `--through 2026-11-23T01:09:59Z` to select the historical window below. The static and
devcontainer PR workflows identify updates. A head with neither workflow has no anchor in this
report. Match the head, PR and branch across workflows, including
the Unity companion gate. Use GitHub's recorded branch push or later PR open/reopen time and the
last of the three required job completions. Count failed updates alongside successful ones.
Inspect incomplete rows before interpreting percentiles. Require at least 40 representative
measured updates and report missing clocks, cancellations, superseded updates and retries.
A short docs-only lane cannot establish native PR latency.

The November 21-23 sample had 40 native updates across three PRs. All three contexts reported on
each update. Median time to the last verdict was 12m41s and p95 was 30m14s. Five finished in
under seven minutes, including three of the 33 green updates. In 17 of 39 observable native
starts, more than seven minutes elapsed between preflight completion and runner setup. Even
assigning zero wait to the missing observation, that interval alone had a p95 of 21m41s across
the 40 updates. Reaching seven minutes in a comparable window requires shorter waits for a
licensed runner. Five of those 17 waits overlapped a repository manual or nightly-dispatched
licensed workflow. Overlap alone does not identify the job occupying a runner.

A later ordinary native PR update took 5m28s through all three required contexts. Its Unity job
waited 2 seconds for a runner and took 5m06s from assignment through cleanup. This adds one
low-queue observation to the separate rolling-window results above.
[Exact-head check receipts](https://github.com/Ambiguous-Interactive/DxMessaging/issues/817#issuecomment-7138716948).

On one Windows runner, a warm standalone step took 106 seconds, including 6.74 seconds for the
final native build and 35.21 seconds for tests. After a compiled sample assembly definition was
renamed, a new project key selected an empty Library. The same phases took 507.22 and 34.92
seconds in a 686-second step. The rename belongs in the compiler-input key. Preserve its
invalidation and compare native build logs before changing test scope.
[Warm build](https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/45082346198/job/137393121072)
and [cold build](https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/45090872658/job/137419913878).

Windows artifact redaction took 44 seconds in a profiled run. The standalone worker spent
41.52 sampled seconds in structured text processing, including 22.60 seconds in contextual
matching. The corresponding PlayMode times were 27.51 and 11.83 seconds. The workers run in
parallel. A bounded matching experiment preserved all artifact bytes in a local comparison, but
one of three PlayMode pairs slowed from 9.93 to 10.14 seconds. The existing redactor passed
the original validation. See the [Windows profile](https://github.com/Ambiguous-Interactive/DxMessaging/actions/runs/45104084768/job/137461300418).

## Local commands

On a fresh checkout, run `dotnet restore .net-tests/DxMessaging.Net.Tests.csproj` for runtime
work or `dotnet restore SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests`
for generator and analyzer work. Repeat the restore after dependency changes. Choose a named
test for the behavior you changed. The commands below give examples for different edits.

| Change               | First feedback                                                                                                                                                                                                | Before delivery                                                                               |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| Script unit behavior | Named case in [the focused watch loop](#save-to-a-focused-test-verdict) or `node --test <changed-test>`                                                                                                       | `npm test`                                                                                    |
| Script integration   | `node --test <integration-test>`                                                                                                                                                                              | `npm run test:integration`, then the complete suite once                                      |
| Runtime C#           | Affected method or fixture using the filtered runtime command above                                                                                                                                           | Both fast lanes below; affected fixtures without tier filters for lifecycle or memory changes |
| Source generator     | `dotnet test SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests -c Release --no-restore /p:CopyAnalyzerPayload=false --filter FullyQualifiedName~ReportsNonPartialContainingType`            | Complete generator suite and `npm run check:analyzers`                                        |
| Analyzer diagnostic  | `dotnet test SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests -c Release --no-restore /p:CopyAnalyzerPayload=false --filter FullyQualifiedName~MissingForwardingReportsItsLifecycleMethod` | Complete generator suite and `npm run check:analyzers`                                        |
| Documentation C#     | `dotnet test .docs-tests/WallstopStudios.DxMessaging.Docs.Tests.csproj`                                                                                                                                       | Relevant prose checks and strict site build                                                   |
| Unity behavior       | Affected fixture through the host MCP bridge                                                                                                                                                                  | Affected Unity scopes; CI supplies Windows IL2CPP evidence                                    |

Run both managed fast lanes in separate test processes before delivery:

```sh
dotnet test .net-tests/DxMessaging.Net.Tests.csproj --configuration Release --filter 'TestCategory!=Exhaustive&TestCategory!=MemoryReclaim'
dotnet test .net-tests/DxMessaging.Net.Tests.csproj --configuration Release --filter 'TestCategory!=Exhaustive&TestCategory=MemoryReclaim'
```

Combining them under one `TestCategory!=Exhaustive` filter recreates the retained-result GC cost.
The two commands execute the same 35,617 fast cases, with retention starting in a fresh process.

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
- Dispatched performance retains the existing historical regression threshold before publication.
  Changed benchmark methodology or execution profiles retain an explicit non-comparable result;
  comparable throughput or allocation regressions fail the run. Tag qualification cannot suppress
  the default-branch nightly publication run.
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

## Editor-free devcontainer

Set `UNITY_MCP_ENABLED=false` in the checkout's ignored `.env.local`, run
`npm run unity:mcp:configure -- --offline`, and reconnect the MCP clients you have open.
Container creation, startup, and attachment honor this setting. It removes the Unity
connection from the seven generated client configs and skips host discovery and token creation.
A host outage alone leaves the selected mode in place.

Use the two managed fast lanes under [local commands](#local-commands) for runtime changes.
Use `npm run test:unit` for script feedback and `npm test` before delivering script changes.
Record counts, failures, and durations for the lanes you ran. Mark EditMode, PlayMode, and
IL2CPP checks pending until a licensed host produces results for that revision.

To reconnect, set `UNITY_MCP_ENABLED=true` or remove the setting. With the licensed host ready,
run `npm run unity:mcp:configure`, reconnect clients, and run `npm run unity:mcp:probe`.

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

Before opening the generated host, link its physical package directory to the shared checkout.
A manifest `file:` dependency alone does not create this filesystem entry. From the host checkout,
using PowerShell 7:

```powershell
$checkout = (Get-Location).Path
$packageLink = Join-Path $checkout '.artifacts/u/local-playmode/Packages/com.wallstop-studios.dxmessaging'
if (Test-Path -LiteralPath $packageLink) {
    throw 'Inspect the existing package entry before proceeding; do not replace it blindly.'
}
$linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
New-Item -ItemType $linkType -Path $packageLink -Target $checkout | Out-Null
```

This preserves the manifest while making the embedded package entry resolve to the same checkout.
Adjust the project directory if a different `-ProjectPath` was used. Do not run this setup during
an editor test or against an existing foreign package directory.

With that mapping, `Packages/com.wallstop-studios.dxmessaging/.artifacts/unity-mcp/<name>.json`
resolves to the checkout's `.artifacts/unity-mcp/<name>.json`, which the container can poll. Before
launching tests, write a unique probe file through that host path and confirm the container reads
the same bytes at the checkout path, then remove the probe. Do not start a run until both views
agree. An unresolved result path can otherwise create an unrelated directory inside the host.

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
and [configurable Play Mode](https://docs.unity.com/en-us/engine/6000.0/manual/programming-environment/code-reload-serialization/code-reloading-editor/configurable-enter-play-mode)
keep engine iteration focused; static-reset tests are required when reload is disabled.
