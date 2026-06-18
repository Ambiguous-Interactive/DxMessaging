# Test-Suite Performance Runbook

This runbook is the developer/ops reference for keeping the Unity test legs fast
without dropping coverage. The agent-facing companion with the code patterns is
the [Fast Unity Tests](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/skills/testing/fast-unity-tests.md)
skill; this
runbook covers the budgets, the CI shape, the measurement protocol, and the
drift-guards.

## Per-mode budgets

The target is **each mode under 3 minutes of wall-clock** while coverage stays at
or above today and no anti-pattern is introduced.

| Mode       | Budget                           | Notes                                                                                                                                                                                                             |
| ---------- | -------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| EditMode   | < 3 min                          | Dominated by reflection walks + AssetDatabase/file I/O.                                                                                                                                                           |
| PlayMode   | < 3 min                          | Dominated by play-mode entry reload + per-test frame yields.                                                                                                                                                      |
| Standalone | < 3 min for the TEST-RUN portion | The IL2CPP native BUILD dominates the leg; the correctness leg compiles it with the Debug C++ config (the perf leg keeps Release). See [Standalone IL2CPP build wall-clock](#standalone-il2cpp-build-wall-clock). |

The `< 3 min` figure is a **CI** metric (cold ephemeral project, full compile,
domain reload, on self-hosted runners). The local MCP editor is warm and already
finishes PlayMode in tens of seconds, so locally you trust relative deltas, not
the absolute number.

## The modes (CI matrix)

`.github/workflows/unity-tests.yml` runs **9 legs** = 3 Unity versions (from
`.github/unity-versions.json`) x 3 modes (`editmode`, `playmode`, `standalone`),
`max-parallel: 1`. Each mode is a separate Unity invocation against a separate
ephemeral project under `.artifacts/unity/projects/<version>-<mode>/`. The
correctness legs exclude the heavy categories
(`Stress;Performance;Allocation;MemoryReclaim;UnityRuntime;PerfBench;PerfGate;PerfBaseline`),
which run in their own dedicated scopes so a perf change cannot hide in the
correctness number.

## The levers

See the [Fast Unity Tests](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/skills/testing/fast-unity-tests.md) skill for
the code. In short:

- **Disable enter-play-mode reload.** `EnterPlayModeOptions: 3`
  (`DisableDomainReload | DisableSceneReload`) is emitted into every CI ephemeral
  project by `Initialize-EphemeralProject` in `scripts/unity/run-ci-tests.ps1` (the
  committed source of truth). The local
  `.unity-test-project/ProjectSettings/EditorSettings.asset` carries the same fields
  but is gitignored, so it is a per-developer convenience, not committed. Only the
  PlayMode legs benefit (editmode and standalone never enter in-editor play mode);
  in batchmode CI each leg is a fresh project with one play-mode entry, so the
  saving is one reload per PlayMode leg, and the persistent-domain path the
  `MessageTypeIdStabilityTests` fix protects is a local back-to-back-run property,
  not a CI one.
- **One teardown frame, not one per object.** `MessagingTestBase.UnityCleanup`
  queues every tracked destroy, then yields a single drain frame.
- **`[UnityTest]` only when you yield.** A no-yield coroutine test is a synchronous
  test paying coroutine overhead; prefer `[Test]` where the lifecycle allows.
- **No real-time waits.** Blocking sleeps, awaited delays, real-seconds coroutine
  yields, and time-scale manipulation are banned (the cost here is frame-based, not
  wall-clock, so the "lower the time scale" tip does not apply).
- **Standalone: Debug C++ for the correctness leg.** The native C++ compile, not
  the test run, dominates the standalone wall-clock. The correctness leg compiles
  it Debug; only the published perf leg pays for Release. See the next section.

## Standalone IL2CPP build wall-clock

The standalone leg builds a real IL2CPP player: Unity transpiles the managed
assemblies to C++ (`il2cpp.exe`), then a native C++ compiler builds that into the
player. The **native C++ compile dominates** the leg's wall-clock, and a Release
(`-O2`-class) compile is far slower than a Debug (`-O0`-class) one.

`scripts/unity/run-ci-tests.ps1` is shared by two standalone-building workflows:

- `unity-tests.yml` -- the **correctness** leg. It excludes every perf category and
  publishes NO numbers; it exists to prove the code is correct under IL2CPP.
- `perf-numbers.yml` -- the **sole published Release-player** leg (the headline
  source).

So the correctness standalone leg passes `-Il2CppConfiguration Debug` (a far
faster native compile) while the perf leg pins `Il2CppConfiguration = 'Release'`.
The script parameter defaults to `Release`, so every other caller (release,
benchmarks) is unaffected; the editmode/playmode matrix entries never build a
player and do not receive the IL2CPP-only argument.

**Fidelity is preserved.** Debug vs Release changes ONLY the native C++ optimization
level -- NOT the managed->C++ transpilation, generic sharing, AOT compilation, or
managed stripping that the IL2CPP leg exists to verify. The published Release
headline still comes from `perf-numbers.yml`, so the Release native path stays
exercised in CI.

**Library cache (audited, intentionally conservative).** The per-`<version>-<mode>`
`Library` cache key in `unity-tests.yml` hashes `run-ci-tests.ps1`, so a harness
edit cold-busts the IL2CPP build cache. That is **by design**: the build-affecting
configurator (scripting backend, IL2CPP config, API level, stripping) is generated
by `run-ci-tests.ps1`, so the script genuinely affects build output, and a stale
`Library` is a correctness hazard. The repo rule bans broad Unity `Library` restore
keys (a fallback that would return a stale Library), so there is no safe narrowing.
The right mitigation is making the cold build cheap -- exactly what the Debug C++
config does -- not a riskier cache key. The correctness and perf legs cache the SAME
per-`<version>-<mode>` `Library` path, but under distinct key prefixes (`Library-`
vs `Library-perf-`) with no `restore-keys` and a clean checkout that wipes
`.artifacts/` before each restore -- so each leg restores only its own exactly-keyed
cache, and the Debug and Release players never share or contaminate one.

## Local measurement protocol (MCP loop)

Measure via the [Unity MCP Test Loop](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/skills/unity/mcp-test-loop.md):
`DxMcpTestRunner.Run(testMode, assemblies, null, null, resultPath)` writes
`durationSeconds` + pass/fail/skip counts. Baseline, change ONE lever, re-run the
SAME call, diff. Keep a change only if pass counts hold and no flake appears across
repeated runs. Two caveats: warm-editor frames are near-free (a structural fix can
show a near-zero LOCAL delta yet pay off on cold CI legs), and a script edit forces
one reload on the next play-mode entry (run twice back-to-back to exercise the true
persistent-domain path -- a test with a latent reload dependency fails only on the
SECOND, persistent run).

## Drift-guards

- `scripts/__tests__/run-ci-tests-enter-play-mode.test.js` (Node) asserts
  `run-ci-tests.ps1` emits the reload-disable into each CI ephemeral project. It
  guards the runner emit rather than `.unity-test-project` (whose `ProjectSettings`
  are gitignored, so that copy is absent in a fresh CI checkout).
- `TestAttributeContractTests.TestSourcesAvoidRealTimeWaitAntiPatterns` (Runtime)
  scans the whole `Tests/` tree and fails on any banned real-time-wait token.
- `SuiteWallClockBudgetTest` (Runtime) is the pre-existing speed backstop: it fails
  the default correctness suite when its wall clock exceeds a per-version hard
  ceiling (300 s on 2021.3, 180 s on 2022.3 / 6000.x) and warns past a 60 s soft
  budget, so a slowdown is unmissable regardless of which lever regressed.

## Status and follow-ups

Disabling enter-play-mode reload exposed (and we fixed) one latent reload
dependency: `MessageTypeIdStabilityTests` assumed a fresh message-type-id registry
each run, but that registry is intentionally process-stable (the design that makes
"Domain Reload disabled" safe). The test now asserts the persistent invariant.

Open follow-ups (tracked in the remaining-work plan):

- EditMode is the slower mode locally; de-I/O the EditMode hotspots (cache the
  reflection walks in `[OneTimeSetUp]`, prefer in-memory `ScriptableObject` over
  `AssetDatabase.CreateAsset`).
- Migrate no-yield `[UnityTest]` methods to `[Test]` file-by-file, and add the
  companion no-yield-`[UnityTest]` drift-guard alongside that migration (it is not
  built yet: it would otherwise be red against the many existing no-yield bodies).
- Standalone IL2CPP build: the Debug C++ config (above) is the landed win. The
  Library cache key was audited and intentionally left conservative (see
  [Standalone IL2CPP build wall-clock](#standalone-il2cpp-build-wall-clock)).
  Within-leg / cross-runner sharding stays open, gated on the org build lock + Unity
  license concurrency.

## See Also

- [Fast Unity Tests](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/skills/testing/fast-unity-tests.md)
- [Unity MCP Test Loop](https://github.com/Ambiguous-Interactive/DxMessaging/blob/master/.llm/skills/unity/mcp-test-loop.md)
- [Perf Benchmark Methodology](perf-benchmark-methodology.md)
