# Repository Guidelines

This file is intentionally concise. It contains only critical, high-signal guidance for agentic work.

## Start Here

- Read the skill catalog first: [Skills Index](./skills/index.md)
- Prefer focused skills over adding large instruction blocks here.
- Keep this file under 300 lines at all times.

## Project Structure

- `Runtime/` - Core runtime and Unity-facing messaging components.
- `Editor/` - Editor tooling and analyzers.
- `SourceGenerators/` - Roslyn source generation.
- `Tests/` - Runtime and integration test coverage.
- `scripts/` - The small set of kept repository scripts (Unity perf/CI helpers, wiki sync, a few validators).
- `docs/` - User-facing package documentation and examples.

## Tooling Philosophy (read this before adding any script)

- JS tooling is intentionally minimal (a hard line budget is enforced by `scripts/validate-js-loc-budget.js` / `npm run validate:js-loc-budget`, which is the single source for the current ceiling and its changelog -- do not restate that number here or elsewhere); prefer off-the-shelf tools (prettier, cspell, markdownlint-cli2, csharpier, actionlint, lychee, yamllint, pre-commit built-ins); do not add bespoke validators, wrappers, preflight/doctor machinery, or custom git-hook plumbing.
- Git hooks are managed solely by the standard pre-commit framework: `pipx install pre-commit` (or pip), then `pre-commit install`. Do not set `core.hooksPath`, write hooks into `.git/hooks` by hand, or wrap pre-commit in Node scripts.
- Script tests use the built-in `node --test` runner (`npm test`); there is no jest. Pre-push runs only the fast script-test subset and excludes real subprocess/archive integration tests, so agents must run full `npm test` when changing `scripts/**/*.js` or GitHub composite action scripts.
- Run tools directly (`npx prettier`, `npx cspell`, `npx markdownlint-cli2`); never reintroduce "managed" runner wrappers.
- A script with both a fixer/generator mode and a `--check` mode must converge: running the fixer either makes `--check` pass or exits non-zero naming the file a human must fix. Share one validator between the modes, and either refuse to write an unfixable state or re-verify the post-write state; never report success while leaving a `--check`-failing state (the trap is a fixer that silently no-ops on input its own `--check` rejects). See `collectValidationErrors` (post-write re-verify) in `scripts/update-llms-txt.js` and `analyze` (refuse-when-unfixable) in `scripts/generate-skills-index.js`.

## Core Delivery Rules

- Implement complete solutions in one pass whenever feasible.
- When changing behavior, add or update tests in the same change.
- Prefer small focused edits over broad refactors unless required.
- Preserve existing naming and architectural patterns.
- Never commit repository settings that auto-approve chat-invoked terminal commands.
- Ensure fenced markdown examples are closed and do not swallow real sections (for example `## See Also`).
- Before committing, run the relevant formatters/linters yourself (`npm run format:check`, `npm run lint:markdown`, `npm run check:spelling`, `npm run validate:all` as applicable); hooks are the fast backstop, not the first signal.

## Build and Test Commands

- Restore .NET tools: `dotnet tool restore`
- Format C#: `dotnet tool run csharpier format .` (the trailing `.` is required; without a path, `csharpier format` reads stdin and formats nothing)
- Script tests (node --test): `npm test`
- Format markdown/JSON/YAML/asmdef: `npm run format` (check-only: `npm run format:check`)
- Markdown lint: `npm run lint:markdown`
- Spelling: `npm run check:spelling`
- Sync banner SVG version/test-count: `npm run sync:banner`
- Regenerate llms.txt: `npm run update:llms-txt` (check-only: `npm run check:llms-txt`)
- Regenerate the skills-index Lines column + counts: `npm run update:skills-index` (check-only: `npm run check:skills-index`, gated in `validate:all`)
- Analyzer payload: `npm run check:analyzers` (refresh: `npm run refresh:analyzers`)
- C# method naming auto-fix: `npm run fix:csharp-underscores`
- Unity asmdef reference integrity: `npm run validate:asmdef-references`
- Unity version matrix consistency: `npm run validate:unity-versions`
- JS LOC budget: `npm run validate:js-loc-budget`
- npm tarball hygiene + Unity .meta pairing + tracked C# `.meta` `MonoImporter` shape: `npm run validate:npm-meta`
- Everything: `npm run validate:all`
- Hooks (one-time setup): `pipx install pre-commit && pre-commit install`; normal hooks run on changed files. Use targeted `pre-commit run <hook-id> --files ...` during development. Reserve `pre-commit run --all-files` / `pre-commit run --all-files --hook-stage pre-push` for hook-config changes or release audits; they are whole-repo audits, not the routine agent loop. For release audits, run the direct heavy checks (`npm test`, `npm run check:spelling`, `npm run validate:all`) alongside the all-files hook audits. Pre-push is intentionally sub-second as hook body work and excludes heavier checks such as spelling and npm pack validation; agents must run those directly when relevant.

## Running Unity Tests

Local Unity verification runs through the **unity-mcp-remote MCP server** (the host
editor), NOT inside the devcontainer. The container ships no local Unity build. See
[Unity MCP Test Loop](./skills/unity/mcp-test-loop.md) for the full loop.

- The devcontainer workspace IS the embedded package inside the host Unity project,
  so edits in-container are instantly visible to the editor.
- Compile: trigger `AssetDatabase.Refresh()` via `Unity_RunCommand`.
- Run tests: call the host bridge `DxMcpTestRunner.Run(testMode, assemblies, tests, categories, resultPath)` via `Unity_RunCommand`; poll the `.status` sidecar under `.artifacts/unity-mcp/` from the container.
- EditMode assemblies: `WallstopStudios.DxMessaging.Tests.Editor`, `...Tests.Editor.Allocations`, `...Tests.00.Editor.Benchmarks`. PlayMode: `...Tests.Runtime`, `...Tests.00.Runtime.Benchmarks` (category `PerfBench`), `...Tests.00.Runtime.Comparisons`, DI integrations (Reflex/VContainer/Zenject).
- Perf baselines: the benchmark CSV defaults to `.artifacts/perf-baseline.csv` (override env `DX_PERF_BASELINE`; `DX_PERF_COMMIT` stamps the commit column).
- Sandbox restriction: `using System.Reflection;` is rejected in `Unity_RunCommand` snippets -- fully qualify (`System.Reflection.Assembly`) instead.
- The published IL2CPP-Release headline comes from the CI leg (self-hosted Windows, `scripts/unity/run-ci-tests.ps1`), not the local MCP loop; the MCP loop is the local Mono/editor signal. The CI host project is generated under `.artifacts/unity/projects/<version>-<mode>/` -- see [UPM Test Harness](./skills/unity/upm-test-harness.md).
- License (CI only): see [Unity License Bootstrap](./skills/unity/unity-license-bootstrap.md). CI activates Unity with a classic serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`) and guarantees a `-returnlicense` on every exit path.
- For source-generator tests (no Unity), use `dotnet test SourceGenerators/...Tests`

## GitHub Actions / CI Runners

- Self-hosted runner topology (org-level, group "Default"):
  - `ELI-MACHINE`: `self-hosted, X64, RAM-64GB, Windows, fast`
  - `DAD-MACHINE`: `self-hosted, X64, RAM-64GB, Windows`
  - `box-linux`: `self-hosted, Linux, X64, RAM-64GB`
  - `mac-mini`: `self-hosted, RAM-16GB, macOS, ARM64`
  - `old-linux`: `self-hosted, Linux, X64, RAM-16GB, old`
  - `ubuntu-latest-large`: GitHub-hosted large runner
- Workflow YAML is linted by actionlint (CI) plus yamllint via pre-commit; there is no bespoke workflow validator.
- `actions/create-github-app-token@v3` requires `app-id` + `private-key`; do not use legacy `client-id` in workflows.
- Never use a single shared `concurrency.group` across multiple matrix entries without mitigation (expand the group with a `${{ matrix.* }}` token, declare `queue: max` with `cancel-in-progress: false`, or set `strategy.max-parallel: 1`).
- Do not use native GitHub `concurrency.group: wallstop-organization-builds`; the organization lock name belongs only in the central `Ambiguous-Interactive/ambiguous-organization-build-lock` acquire/release action inputs.
- Unity is activated with a classic serial (`UNITY_SERIAL` + `UNITY_EMAIL` + `UNITY_PASSWORD`); the floating licensing server is RETIRED (`UNITY_LICENSING_SERVER` removed). Every Unity-credential-using job must validate the serial secrets, provision the editor (`scripts/unity/ensure-editor.ps1 -CiManagedOnly` with an explicit `-ProvisioningProfile`) BEFORE the org lock, acquire `wallstop-organization-builds` immediately before `scripts/unity/run-ci-tests.ps1`, and release it with `if: always()`. The license is returned on every exit path (defensive return-at-start, `finally` return, `if: always()` `return-unity-license` step). See [Unity License Return Guarantee](./skills/unity/unity-license-return-guarantee.md) and [Unity CI Matrix](./skills/unity/unity-ci-matrix.md).
- Per-runner Unity-cache safety comes from each runner agent's exclusive workspace. CI caches the generated project's `Library` under `.artifacts/unity/projects/<version>-<mode>/Library` and Unity package caches under `.artifacts/unity/cache/<version>`; do not add broad restore keys for Unity `Library`.
- Unity diagnostic scanners must inspect every `*.log` under the results directory, with `unity.log` first but retry logs such as `unity.first-attempt.log` included. UPM retry failures often keep the actionable cancellation signal only in the preserved first-attempt log.
- Unity versions are single-sourced in `.github/unity-versions.json` (`all` = full CI set, `latest` = last `all` entry, `release` = pinned release version); bump ONLY that file. `npm run validate:unity-versions` enforces zero drift. See [Unity Version Single Source of Truth](./skills/github-actions/unity-version-single-source.md).
- Required status checks (branch protection / auto-merge gate): a required check must report (run or skip-to-success) on EVERY PR shape or auto-merge hangs on an absent check. Static correctness/style checks live in `.github/workflows/ci.yml` behind `CI Success`; Unity correctness lives behind `Unity CI Success`. Do not require individual static job names, Unity matrix legs, or path-filtered workflows after the aggregate ruleset switch. Path-sensitive gates keep an unfiltered `pull_request` trigger, use a `changes` job, and fail closed unless detection succeeds with explicit `true`/`false` outputs. Required-check names are literal strings, so renames silently break the gate. Required matrix jobs either need an aggregate gate or `if: always()` at the job level with expensive steps gated internally; skipped matrix jobs can report only the literal unevaluated matrix name. See [Required Status Checks runbook](../docs/runbooks/required-checks.md).
- Comparison-benchmark packages (OpenUPM registry + PINNED versions + required Unity built-in packages) are single-sourced in `.github/comparison-packages.json`; bump ONLY that file and keep the gated comparison asmdef `versionDefines` / `defineConstraints` plus `.unity-test-project/Packages/manifest.json` + `packages-lock.json` in sync. See [Comparison Parity and Package Single Source](./skills/testing/comparison-parity-and-package-single-source.md).

## Devcontainer Workflow

The agent runs from inside the slim devcontainer (.NET 9/10 base + Node + docs toolchain). It runs NO local Unity build: local Unity verification goes through the unity-mcp-remote MCP server (the host editor) -- see [Unity MCP Test Loop](./skills/unity/mcp-test-loop.md). CI uses `scripts/unity/run-ci-tests.ps1` on self-hosted Windows. See [Devcontainer Cache Contract](./skills/unity/devcontainer-cache-contract.md).

- `.devcontainer/cache-contract.sh` derives `CACHE_WORKSPACE_ROOT` from the script's own location (parent of `.devcontainer/`, equals `${containerWorkspaceFolder}`) when `WORKSPACE_FOLDER` is unset (e.g. during `postCreateCommand`); never hardcode an absolute fallback.

## C# Conventions

- Use explicit types where practical; avoid unnecessary `var`.
- Keep braces explicit.
- Avoid regions.
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

- Before writing any new script, re-read the Tooling Philosophy section; an off-the-shelf tool or a few lines in an existing kept script is almost always the right answer.
- Reuse shared helpers in `scripts/lib/` (`repo-files.js`, `path-classifier.js`, `line-endings.js`, `shell-command.js`) before duplicating parsing logic.
- For Node child-process calls in `scripts/*.js`, prefer argument-array invocations (`spawnSync` / `execFileSync`) and `stdio` options instead of shell redirection; for `npm`/`npx` calls use `spawnPlatformCommandSync()` from `scripts/lib/shell-command.js` (Windows shims must not be spawned directly).
- For local tar archive operands, never pass a raw absolute path to `tar -f`; Windows drive-letter paths are parsed as remote archive specs by GNU tar. Use `buildLocalTarArchiveSpec()` in `scripts/validate-npm-meta.js` or the same `cwd` + `./basename` pattern, and cover `path.win32` in tests.
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
- Tests that drive `scripts/unity/ensure-editor.ps1` against a fake `Unity.exe` stub MUST set `DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE=1` in the spawn env; Windows `CreateProcess()` rejects shebang `.exe` files. See [Cross-Platform Script Compatibility](./skills/scripting/cross-platform-compatibility.md#stub-executables-on-windows-pe-binary-requirement).
- For validators that depend on `git` metadata, treat `ENOENT`/missing-git failures as hard errors; never silently default to permissive behavior.

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
- Keep the Unity test legs fast without dropping coverage: enter-play-mode domain+scene reload stays DISABLED (`EnterPlayModeOptions: 3`), emitted into every CI ephemeral project by `run-ci-tests.ps1` (the committed source of truth; `.unity-test-project/ProjectSettings/*` is gitignored), teardown batches deferred destroys into a single frame rather than one per object, `[UnityTest]` is used only when a frame is actually yielded (a no-yield `[UnityTest]` must be a plain `[Test]`), and real-time waits are banned. The reload emit, the no-yield-`[UnityTest]` ban (with a shrinking per-file `pendingMigration` allowlist), and the real-time-wait ban are drift-guarded (`scripts/__tests__/run-ci-tests-enter-play-mode.test.js`, `TestAttributeContractTests.NoYieldUnityTestsMustBePlainTest`, `TestAttributeContractTests.TestSourcesAvoidRealTimeWaitAntiPatterns`). Disabling domain reload is safe because production resets statics on play-mode entry; a test that depends on a fresh domain is a latent isolation bug to fix, not a reason to re-enable reload. See [Fast Unity Tests](./skills/testing/fast-unity-tests.md).
- Tests that exercise dispatch across more than one of `Untargeted`/`Targeted`/`Broadcast` MUST be parameterized via `MessageScenarios.AllKinds`; see [Tests Must Be Parameterized by Message Kind](./skills/testing/tests-must-be-parameterized-by-message-kind.md).
- Bus dispatch-path changes must be covered by the canonical lifecycle edge-case set; see [Lifecycle Edge-Case Test Coverage](./skills/testing/lifecycle-edge-coverage.md).
- Tests that create and tear down message registrations should bracket the work in a `LeakWatcher`; see [LeakWatcher: Detecting Registration Leaks in Tests](./skills/testing/leak-watcher-usage.md).
- Token cleanup tests that enable diagnostics must assert token metadata, call counts, and emission history clear after successful `UnregisterAll()`/`Dispose()`; `LeakWatcher` covers bus counters only.
- Token deregistration-failure tests must cover a bus action that throws before cleanup and prove the token keeps the failed registration retryable.
- Token replay failure tests must assert partial registrations roll back for `Enable()` and that active `RetargetMessageBus()` failures restore previous-bus registrations unless rollback cleanup itself left that handle live and retryable on the failed new bus; recovery rollback must be scoped to deregistrations added by the current replay so pre-existing retryable cleanup is not consumed.
- Lease/component cleanup-failure tests should retry through the owning wrapper API (`lease.Deactivate()` / `lease.Dispose()` / `MessagingComponent.Release()`), not only through the raw token; include `lease.Activate()` replay failures and `ActivateOnBuild` failures that leave the token live after rollback cleanup failure.
- Tests for memory holders keyed by message type or `InstanceId` must prove forced trim, idle sweep, slot-count recovery, and stale deregistration behavior; see [Memory Reclaim Coverage](./skills/testing/memory-reclaim-coverage.md).
- Benchmark and performance/allocation tests stay isolated under `Tests/Runtime/Benchmarks` in asmdef `WallstopStudios.DxMessaging.Tests.00.Runtime.Benchmarks`; keep `BenchmarkAssemblyContractTests` green when adding or moving perf tests. Benchmarks warm up then measure ONE continuous window; the published headline is Standalone IL2CPP (Release player, Release C++ config), .NET Standard 2.1 + Release code optimization; PlayMode/EditMode stay as local/CI test scopes, not published numbers. See [Benchmark Methodology: Total Over One Window](./skills/performance/benchmark-methodology-total-over-window.md) and [Perf Config: IL2CPP Release, .NET Standard 2.1](./skills/performance/perf-config-il2cpp-release-netstandard21.md).
- When adding a `MessageCache<>` storage field to `MessageBus`, update `MessageBus.ExpectedMessageCacheFieldCount`, add the field to `MessageBus.SweepableTypeCaches`, and add reclamation coverage; see [DxMessaging Memory Reclamation](./skills/performance/memory-reclamation.md).

## Documentation Expectations

- Update relevant docs after user-visible behavior changes.
- Keep examples accurate and aligned with real usage.
- Update `CHANGELOG.md` only for user-facing DxMessaging changes, not developer-only tooling/process updates.
- For `## [Unreleased]` entries, mutate existing bullets as behavior evolves; do not stack separate `Added` then `Fixed` bullets for the same unreleased change.
- For edited Markdown files, run `npx prettier --write` and `npx markdownlint-cli2` before finishing.
- Ordered lists must follow MD029 `one` style (`1.` for each item).
- Internal fragment links must match GitHub/markdownlint heading slugs exactly (MD051).
- Documentation and `///` XML doc comments must be pure ASCII; see [ASCII-Only Documentation Policy](./skills/documentation/ascii-only-docs.md).
- Every C# code sample in docs - inline, fenced, and XML `<code>` blocks - must compile; see [Code Samples Must Compile](./skills/documentation/code-samples-must-compile.md) and keep the `DocsSnippetCompilationTests` suite green.
- Documentation prose must avoid LLM-style filler, marketing adjectives, hedge transitions, and vague quantifiers; see [Human-Prose Documentation Policy](./skills/documentation/human-prose-policy.md).
- Subclasses of `MessageAwareComponent` MUST call `base.<method>()` from every guarded lifecycle override; see [MessageAwareComponent Base-Call Contract](./skills/unity/base-call-contract.md).
- When editing `Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs` or its provider, update `docs/reference/runtime-settings.md` and `docs/guides/memory-reclamation.md` in the same change; see [Memory Reclamation Documentation Maintenance](./skills/documentation/memory-reclamation-docs.md).

## Skills to Prefer

Use the index above and then select the most relevant skill pages. Frequently useful entries include:

- Documentation and changelog guidance under `./skills/documentation/`
- Memory reclamation guidance under `./skills/performance/memory-reclamation.md`
- Script reliability and parsing guidance under `./skills/scripting/`
- Test quality and investigation guidance under `./skills/testing/`
- Workflow robustness under `./skills/github-actions/`
- Unity test workflow under `./skills/unity/` (see mcp-test-loop, unity-license-bootstrap, unity-license-return-guarantee, upm-test-harness, devcontainer-cache-contract, unity-ci-matrix, unity-perf-test-isolation)

## Split File Maintenance

- Split files (for example `*-part-1.md`) are regular human-maintained docs, not generated artifacts.
- Keep `.llm/**/*.md` files focused and reasonably small (roughly 120-260 lines); extract companion files when a base file grows past that.
- Keep base files as the canonical overview and cross-link companions via `## See Also`.

## See Also

- [Documentation Updates and Maintenance](./skills/documentation/documentation-updates.md)
- [ASCII-Only Documentation Policy](./skills/documentation/ascii-only-docs.md)
- [Code Samples Must Compile](./skills/documentation/code-samples-must-compile.md)
- [Human-Prose Documentation Policy](./skills/documentation/human-prose-policy.md)
- [Cross-Platform Script Compatibility](./skills/scripting/cross-platform-compatibility.md)
- [Test Failure Investigation and Zero-Flaky Policy](./skills/testing/test-failure-investigation.md)
- [Lifecycle Edge-Case Test Coverage](./skills/testing/lifecycle-edge-coverage.md)
- [LeakWatcher: Detecting Registration Leaks in Tests](./skills/testing/leak-watcher-usage.md)
- [Memory Reclaim Coverage](./skills/testing/memory-reclaim-coverage.md)
- [DxMessaging Memory Reclamation](./skills/performance/memory-reclamation.md)
- [MessageAwareComponent Base-Call Contract](./skills/unity/base-call-contract.md)
- [Unity MCP Test Loop](./skills/unity/mcp-test-loop.md)
- [Unity License Bootstrap](./skills/unity/unity-license-bootstrap.md)
- [Unity License Return Guarantee](./skills/unity/unity-license-return-guarantee.md)
- [UPM Test Harness](./skills/unity/upm-test-harness.md)
- [Devcontainer Cache Contract](./skills/unity/devcontainer-cache-contract.md)
- [Unity CI Matrix](./skills/unity/unity-ci-matrix.md)
- [Unity Perf Test Isolation](./skills/unity/unity-perf-test-isolation.md)
- [CI/CD Devcontainer Workflows](./skills/github-actions/cicd-devcontainer-workflows.md)
