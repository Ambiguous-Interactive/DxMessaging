# Repository coding and documentation conventions

Read the sections relevant to the changed language and subsystem.

## C# Conventions

- Use explicit types where practical; avoid unnecessary `var`.
- Keep braces explicit.
- Write relational comparisons in ascending order using `<`, `<=`, or `==`; swap operands instead
  of writing `>` or `>=`. Public operator declarations remain paired where C# requires them.
- Use an indented `/* ... */` block for comments that span multiple physical lines. Reserve `//`
  for a single physical line and `///` for XML documentation.
- Avoid regions.
- Route every shipped Unity object identity read and lookup through `Runtime/Core/InstanceId.cs`,
  including samples. Convert a `GameObject` or `Component` to `InstanceId` and use its `Id` or
  object reference; never add direct `GetInstanceID`, `GetEntityId`, `InstanceIDToObject`, or
  `EntityIdToObject` calls elsewhere. `InstanceIdSourceTests` enforces this across `Runtime/`,
  `Editor/`, and `Samples~/`.
- Editor `delayCall` callbacks that mutate assets must re-check editor idle state inside the callback and requeue until safe; prefer `DxMessagingEditorIdle.ScheduleAssetDatabaseMutation`.
- Passive Editor settings reads used during domain load must surface effective legacy-migrated values in memory while deferring durable asset migration/saves through `DxMessagingEditorIdle.ScheduleAssetDatabaseMutation`.
- Settings-dependent diagnostics skipped because a passive domain-load read found no settings asset must be re-evaluated after the deferred settings ensure/create callback realizes the asset.
- Editor callback catches that swallow an exception should log the full exception through `DxMessagingEditorLog`, not only `Exception.Message`.
- Generated Editor sidecars or config files consumed through `csc.rsp` must trigger the response-file sync from the producer path after deferred writes complete; do not rely only on `[InitializeOnLoad]` startup setup.
- Use PascalCase for all method names with no underscores (including test methods); auto-enforced by the `fix-csharp-underscore-methods` pre-commit hook.
- For base-call analyzer suppression parity, method-level `[DxIgnoreMissingBaseCall]` suppresses only the annotated guarded method; class-level attribute or project ignore list suppresses the entire type.
- Keep test names descriptive and readable.
- Keep public API changes intentional and backward-compatible unless planned otherwise.

## Script and Automation Conventions

- Before writing any new script, re-read [Tooling](../../../context.md#tooling); an off-the-shelf tool or a few lines in an existing kept script is almost always the right answer.
- Reuse shared helpers in `scripts/lib/` (`repo-files.js`, `path-classifier.js`, `line-endings.js`, `shell-command.js`) before duplicating parsing logic.
- For Node child-process calls in `scripts/*.js`, prefer argument-array invocations (`spawnSync` / `execFileSync`) and `stdio` options instead of shell redirection; for `npm`/`npx` calls use `spawnPlatformCommandSync()` from `scripts/lib/shell-command.js` (Windows shims must not be spawned directly).
- For local tar archive operands, never pass a raw absolute path to `tar -f`; Windows drive-letter paths are parsed as remote archive specs by GNU tar. Use `buildLocalTarArchiveSpec()` in `scripts/validate-npm-meta.js` or the same `cwd` + `./basename` pattern, and cover `path.win32` in tests.
- Scripts that recursively delete generated project/cache directories must guard user-supplied paths before `Remove-Item -Recurse` / `rm -rf`: block repo roots, artifact/output roots, filesystem roots, and repo-contained paths outside the managed generated area; compare protected paths case-insensitively because Windows/macOS filesystems are often case-insensitive; require an ownership marker for existing external temp directories.
- `scripts/unity/export-unitypackage.ps1 -ProjectPath` is treated as an ephemeral export project path: repo-contained values must live under `.artifacts/unity/projects/` and end with `-unitypackage`, and existing external directories must carry `.dxmessaging-unitypackage-project` before the exporter will delete/rebuild them.
- `scripts/unity/run-ci-tests.ps1 -ProjectPath` is treated as an ephemeral CI project path: repo-contained values must live under `.artifacts/u/` (the local default), `.artifacts/unity/projects/`, or `.artifacts/unity/game-ci-projects/`, and existing external directories must carry `.dxmessaging-ci-project` before the harness will reuse them. Active CI passes runner-local external project and cache paths explicitly.
- Script tests must create temporary directories under `os.tmpdir()` with `path.join(os.tmpdir(), "<prefix>-")`; never pass a relative prefix directly to `fs.mkdtempSync()`, because that writes into the current checkout.
- If a script test may call `t.skip()` after creating temporary files or directories, remove those resources before skipping; do not rely on `t.after()` cleanup on skipped paths.
- For script tests that assert path-derived child-process options, do not hardcode a foreign absolute path shape like `/tmp/...` or `C:\...` against the host `path` module. Build fixtures with `os.tmpdir()` / `path.join()`, derive expected `cwd` / basename operands through the production helper, and add explicit `path.win32` / `path.posix` coverage for flavor-specific behavior.
- For dynamic `import()` in `scripts/*.js`, convert filesystem paths with `pathToFileURL(...).href` before importing (raw Windows drive-letter paths fail Node's ESM loader).
- For "is this path inside/outside directory X" decisions, use the helpers in `scripts/lib/path-classifier.js`; never hand-roll `path.relative(dir, file).startsWith("..")` (cross-drive Windows breaks it).
- Normalize multiline text handling before line-based parsing; add tests for parser changes and malformed input edge cases.
- For platform-divergent behavior, test linux AND win32 (and darwin where relevant) regardless of host OS by overriding `process.platform` or passing an explicit platform argument.
- Keep JS and PowerShell behavior synchronized when dual implementations exist.
- For PowerShell paths exported into Docker or Unity containers, pass repo-relative paths with `/` separators; keep platform-native absolute paths only for local filesystem display and validation.
- Tests that spawn host-sensitive scripts (e.g. `scripts/unity/ensure-editor.ps1`) must sandbox host-default folder env vars by SETTING them to empty sandbox dirs, never `delete env.X` (Windows env names are case-insensitive; JS `delete` is not).
- Tests that drive `scripts/unity/ensure-editor.ps1` against a fake `Unity.exe` stub MUST set `DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE=1` in the spawn env; Windows `CreateProcess()` rejects shebang `.exe` files. See [Cross-Platform Script Compatibility](../../shell-and-powershell/references/cross-platform-compatibility.md#stub-executables-on-windows-pe-binary-requirement).
- For validators that depend on `git` metadata, treat `ENOENT`/missing-git failures as hard errors; never silently default to permissive behavior.

## GitHub Access

Follow [GitHub Access](../../github-access/SKILL.md). Its access order is mandatory: use the
VS Code/Codex connector or GitHub extension first, local `git` second, and `gh` only as the final
fallback. A failed `gh auth status` is not a blocker while either earlier path works.

## Line Ending Policy

- Mixed policy is required.
- CRLF: `.cs`, `.csproj`, `.sln`, `.props`
- LF: all other text files
- Source of truth: `.gitattributes`; the pre-commit `mixed-line-ending` hook is the enforcement.

## Testing Expectations

- Treat failing tests as real defects until proven otherwise.
- Prefer direct testing of production code rather than re-implementation in tests.
- Prefer internals exposed through existing `InternalsVisibleTo` test assemblies over reflection when checking package-internal state.
- Cover normal, negative, and edge-case scenarios for new behavior.
- Keep the Unity test legs fast without dropping coverage: enter-play-mode domain+scene reload stays DISABLED (`EnterPlayModeOptions: 3`), emitted into every CI ephemeral project by `run-ci-tests.ps1` (the committed source of truth; `.unity-test-project/ProjectSettings/*` is gitignored), teardown batches deferred destroys into a single frame rather than one per object, `[UnityTest]` is used only when a frame is actually yielded (a no-yield `[UnityTest]` must be a plain `[Test]`), and real-time waits are banned. The reload emit, the no-yield-`[UnityTest]` ban (whose `pendingMigration` allowlist is now empty -- the migration is complete, so the ban covers the whole `Tests/` tree), and the real-time-wait ban are drift-guarded (`scripts/__tests__/run-ci-tests-enter-play-mode.test.js`, `TestAttributeContractTests.NoYieldUnityTestsMustBePlainTest`, `TestAttributeContractTests.TestSourcesAvoidRealTimeWaitAntiPatterns`). Disabling domain reload is safe because production resets statics on play-mode entry; a test that depends on a fresh domain is a latent isolation bug to fix, not a reason to re-enable reload. See [Fast Unity Tests](../../unity-test-execution/references/fast-unity-tests.md).
- Tests that exercise dispatch across more than one of `Untargeted`/`Targeted`/`Broadcast` MUST be parameterized via `MessageScenarios.AllKinds`; see [Tests Must Be Parameterized by Message Kind](../../test-coverage-design/references/tests-must-be-parameterized-by-message-kind.md).
- Bus dispatch-path changes must be covered by the canonical lifecycle edge-case set; see [Lifecycle Edge-Case Test Coverage](../../test-coverage-design/references/lifecycle-edge-coverage.md).
- Tests that create and tear down message registrations should bracket the work in a `LeakWatcher`; see [LeakWatcher: Detecting Registration Leaks in Tests](../../allocation-and-leak-coverage/references/leak-watcher-usage.md).
- Token cleanup tests that enable diagnostics must assert token metadata, call counts, and emission history clear after successful `UnregisterAll()`/`Dispose()`; `LeakWatcher` covers bus counters only.
- Token deregistration-failure tests must cover a bus action that throws before cleanup and prove the token keeps the failed registration retryable.
- Token replay failure tests must assert partial registrations roll back for `Enable()` and that active `RetargetMessageBus()` failures restore previous-bus registrations unless rollback cleanup itself left that handle live and retryable on the failed new bus; recovery rollback must be scoped to deregistrations added by the current replay so pre-existing retryable cleanup is not consumed.
- Lease/component cleanup-failure tests should retry through the owning wrapper API (`lease.Deactivate()` / `lease.Dispose()` / `MessagingComponent.Release()`), not only through the raw token; include `lease.Activate()` replay failures and `ActivateOnBuild` failures that leave the token live after rollback cleanup failure.
- Tests for memory holders keyed by message type or `InstanceId` must prove forced trim, idle sweep, slot-count recovery, and stale deregistration behavior; see [Memory Reclaim Coverage](../../memory-reclamation/references/memory-reclaim-coverage.md).
- Benchmark and performance/allocation tests stay isolated under `Tests/Runtime/Benchmarks` in asmdef `WallstopStudios.DxMessaging.Tests.00.Runtime.Benchmarks`; keep `BenchmarkAssemblyContractTests` green when adding or moving perf tests. Benchmarks warm up then measure ONE continuous window; the published headline is Standalone IL2CPP (Release player, Release C++ config), .NET Standard 2.1 + Release code optimization; PlayMode/EditMode stay as local/CI test scopes, not published numbers. See [Benchmark Methodology: Total Over One Window](../../benchmark-methodology/references/benchmark-methodology-total-over-window.md) and [Perf Config: IL2CPP Release, .NET Standard 2.1](../../il2cpp-build-configuration/references/perf-config-il2cpp-release-netstandard21.md).
- When adding a `MessageCache<>` storage field to `MessageBus`, update `MessageBusInvariantTests.ExpectedMessageCacheFieldCount`, add the field to `MessageBus.SweepableTypeCacheRegistry`, and add reclamation coverage; see [DxMessaging Memory Reclamation](../../memory-reclamation/references/memory-reclamation.md).

## Documentation Expectations

- Update relevant docs after user-visible behavior changes.
- Keep examples accurate and aligned with real usage.
- Update `CHANGELOG.md` only for library behavior a package consumer experiences in Unity
  (runtime, editor, public API, samples, shipped settings). Documentation-site content or layout,
  README text, brand artwork, CI, and repository tooling never earn entries, even when the files
  ship in the package. See the changelog-management skill.
- For `## [Unreleased]` entries, mutate existing bullets as behavior evolves; do not stack separate `Added` then `Fixed` bullets for the same unreleased change.
- For edited Markdown files, run `npx prettier --write` and `npx --no-install markdownlint-cli2` before finishing.
- Ordered lists must follow MD029 `one` style (`1.` for each item).
- Internal fragment links must match GitHub/markdownlint heading slugs exactly (MD051).
- Documentation and `///` XML doc comments must be pure ASCII; see [ASCII-Only Documentation Policy](../../documentation-style/references/ascii-only-docs.md).
- Every C# code sample in docs - inline, fenced, template-embedded, and XML `<code>` blocks - must compile; see [Code Samples Must Compile](../../documentation-code-samples/references/code-samples-must-compile.md) and keep `.docs-tests` green.
- Documentation prose must avoid LLM-style filler, marketing adjectives, hedge transitions, and vague quantifiers; see [Human-Prose Documentation Policy](../../documentation-style/references/human-prose-policy.md).
- Subclasses of `MessageAwareComponent` MUST call `base.<method>()` from every guarded lifecycle override; see [MessageAwareComponent Base-Call Contract](../../unity-editor-conventions/references/base-call-contract.md).
- When editing `Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs` or its provider, update `docs/reference/runtime-settings.md` and `docs/guides/memory-reclamation.md` in the same change; see [Memory Reclamation Documentation Maintenance](../../memory-reclamation/references/memory-reclamation-docs.md).

## Skill Maintenance

- A skill is a directory: `.llm/skills/<name>/SKILL.md` plus an optional `references/` directory.
- `SKILL.md` frontmatter must satisfy the Agent Skills spec: `name` (lowercase, hyphen-separated, matching the directory) and `description` are required. Repository-specific fields belong under `metadata`; unknown top-level keys are rejected.
- The `description` is the only text an agent sees before activating a skill, so it must say both what the skill covers and when to reach for it, using the words someone would actually type.
- `SKILL.md` targets 150 lines and fails above 200. It is a router: state the rules that must always hold, then link the reference that carries the detail. Reference files fail above 500 lines.
- Run `npm run llm:index` after adding, renaming, or removing a skill; it regenerates `.llm/index.md` and the registry block above. `npm run llm:check` gates drift in `validate:all`.
- Never hand-edit `.llm/index.md` or the generated registry block. All skill content and references belong under `.llm/skills/`.
- Do not commit machine-readable skill manifests whose fields can be derived from `.llm/skills/**`. A consumer that needs structured skill metadata must parse the canonical `SKILL.md` frontmatter and directory contents.
- The harness rejects content or links under client-specific skill roots without deleting them. Empty directory trees left by Git are harmless. Move hand-authored work into `.llm/skills/`; retain unrelated client settings and agent entry points.
