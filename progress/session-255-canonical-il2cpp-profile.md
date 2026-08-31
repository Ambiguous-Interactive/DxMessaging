# Session 255 - Canonical IL2CPP profile

Date: 2026-08-31
Branch: `perf/session-255-canonical-profile`
Status: pull request open
Issue: https://github.com/Ambiguous-Interactive/DxMessaging/issues/506
Pull request: https://github.com/Ambiguous-Interactive/DxMessaging/pull/514

## Audit and scope

Audited the open pull requests, open issues, dependency graph, default-branch workflows, and recent
merged sessions. No pull request was open, and every workflow at the audited default-branch head
was green. Issues #506, #508, and #509 are independent foundation blockers for the #414/#497
campaign. Each full issue is larger than one reviewable pull request.

Selected the first #506 slice: freeze a machine-readable canonical IL2CPP verdict profile, bind
every evidence checkpoint to its SHA-256, and reject editor, build, or runtime drift. The stripped
shipping-fidelity player, AOT-root proof, and complete build provenance remain later #506 slices.

## Implementation

- Added `.github/perf/canonical-il2cpp-profile.v1.json` as the reviewed source of truth for the
  standalone IL2CPP verdict player.
- Pinned speed-optimized IL2CPP code generation, incremental GC, engine-code stripping, Release
  compilation, .NET Standard, managed stripping, and the final build-option bitset.
- Archived the exact profile bytes and SHA-256 with every canonical run.
- Embedded the profile ID and SHA-256 into generated editor, build-modifier, and player code.
- Recorded effective configuration after the editor configurator and inside the actual build
  process before and after the build, final options from Unity's post-build `BuildReport`, and
  `Debug.isDebugBuild` inside the player.
- Added a fail-closed validator for missing, extra, mistyped, different, or hash-mismatched values.
- Marked canonical-profile changes as benchmark-methodology changes so historical deltas are not
  treated as comparable.

## Verification

- The PowerShell mutation suite changes every configuration, build-option, and runtime property
  and proves that each difference fails validation. It also covers malformed JSON, schema drift,
  extra fields, missing fields, and hash mismatch.
- The generated-source contract proves that all three C# templates embed the same profile identity
  and hash. It also guards the exact archive copy and all workflow compatibility paths.
- A read-only Unity MCP probe found one clean scene on the main stage in an idle Unity 6000.4.6f1
  editor. A compile probe confirmed the generated code's IL2CPP code-generation, build-report,
  build-option, incremental-GC, and engine-stripping API surface. The live values reported
  `OptimizeSpeed`, incremental GC enabled, and engine stripping enabled.
- The full script suite passed 518 tests. Repository validation, formatting, spelling, Markdown,
  actionlint, yamllint, and pre-commit hooks passed. Vale was unavailable in the container.
- Three adversarial review rounds found and closed build-process attestation, field-type,
  assertion-flag, mutation-honesty, workflow-classification, and wording gaps. Both final reviews
  reported zero findings.

## Deferred #506 work

- Build a second stripped shipping-fidelity player without test assemblies.
- Prove benchmark and callback roots under IL2CPP stripping.
- Capture privacy-safe build provenance and content-addressed native outputs.
- Connect those outputs to the durable evidence bundle tracked by #508.
