# Session 177 - safe editor capture proof and PR recovery

Date: 2026-07-30
Branch: `codex/issue-305-enrollment-remediation`
PR: **#316**

## Outcome

The remaining editor-tooling screenshot work is narrower than the prior plan
recorded. Unity 6000.4.6f1 can render a genuine Editor panel into an offscreen
`RenderTexture`, so the devcontainer does not need a desktop screenshot API. The
method produced visually inspectable staging evidence for both target surfaces:

- the real Message Monitor with shipped package UI and discovered component
  rows;
- a real Inspector containing `MessagingComponentEditor` and the current Message
  subscriptions section.

The host reports `EditorGUIUtility.isProSkin == true` and `UserSkin == 1`. The
proofs are therefore dark-theme experiments, not documentation replacements.
The manifest requests Unity 2022.3 LTS, while the configured host is 6000.4.6f1.
The Monitor mechanism completed without a capture diagnostic. The Inspector
render emitted an IMGUI cursor diagnostic, both proofs are RGBA rather than the
manifest's required 24-bit RGB, and no reusable capture helper is retained. The
remaining WS-7.3 work is to make the Inspector path repeatable and
diagnostic-free, emit RGB24, resolve the version choice, select Personal/light
through the Unity UI, capture the complete set, inspect it, and replace the
tracked images and manifest.

This session also closed issue #317 after auditing its attached archive. The
archive was the rejected branding prototype already covered by the plan: a
second package, demo-seeded Monitor, experimental GraphView, duplicate
`Window/DxMessaging/*` menus, and copies of the shipped icon and theme assets.
No source from the archive was adopted.

## Capture experiment

The public screenshot tools target cameras or the Scene view, and standard
Editor windows report the Windows offscreen sentinel position in this host. A
panel render avoids both constraints:

1. Create a temporary `HideAndDontSave` Editor window and attach the real shipped
   view, or use the real Inspector panel after selecting a temporary component.
1. Give the panel visual tree a fixed size and validate layout.
1. In this linear-color project, allocate the temporary target with
   `RenderTextureReadWrite.Linear`, make it active, set its viewport, clear it,
   set `GL.sRGBWrite` to `false`, repaint the panel, and invoke its render path.
1. Read the pixels into a linear `Texture2D`. An earlier sRGB target produced
   washed-out grays; the linear rerun visually matched the real dark panels and
   amber borders.
1. Close the temporary windows, destroy transient objects, restore selection and
   render state, and record any new Console diagnostics without clearing them.

This reads only the render target created for the experiment. It does not read
the desktop, call `ReadScreenPixel`, invoke `PrintWindow`, move a window onscreen,
or change the Unity skin.

The desktop-independent staging experiment produced two saved dark proof
artifacts:

| Surface | Artifact | Size | SHA-256 |
| --- | --- | ---: | --- |
| Message Monitor | `.artifacts/design-system-screens/session-177/monitor-rendertexture-linear-dark-proof.png` | 52,633 bytes | `05648b02b622abef71be100b1ca8e200d9dd4bb0ea71eba109468fb0ec1cca3d` |
| Inspector | `.artifacts/design-system-screens/session-177/inspector-rendertexture-linear-dark-proof.png` | 91,877 bytes | `2f32ef372817523014816b201eb083cef47181bc851d102a0f376e55082e1453` |

Both were visually inspected at native resolution. The 720x520 Monitor is
correctly oriented and shows the real package view and current component data.
The 1000x700 Inspector is correctly oriented and shows the real Enemy Drone
Inspector, `MessagingComponentEditor`, and current Message subscriptions section.
Neither contains desktop content or sensitive data. They remain dark-theme
mechanism evidence, not final documentation assets.

Two earlier dark screenshots were also visually inspected:

| Surface | Artifact | Size | SHA-256 |
| --- | --- | ---: | --- |
| Message Monitor | `.artifacts/design-system-screens/session-177/monitor-unity-screenshots-api-dark-proof.png` | 66,558 bytes | `353dea246bbb1b9f873d9a084c1b10b83cf48960e43f6e831aa7b9b6b87f04db` |
| Inspector | `.artifacts/design-system-screens/session-177/inspector-unity-screenshots-api-dark-proof.png` | 95,050 bytes | `48f3f2be6766b1139c21cd72920c4897a3861379b92556a2d5eb4d5e1bce005c` |

Both have the correct orientation and real shipped content, with no desktop,
VS Code, or sensitive data. They were created through Unity's internal
`ScreenShots` API before its source was audited. UnityCsReference shows that API
delegates to `InternalEditorUtility.ReadScreenPixel`, so these files are unsafe
historical staging evidence, not acceptance or RenderTexture evidence.

The render-target, `GL.sRGBWrite`, active-target, selection, and window state were
restored in cleanup. The Inspector render emitted
`EditorGUIUtility.AddCursorRect called outside an editor OnGUI` diagnostics while
painting its IMGUI body into the offscreen panel. The Console was cleared after
the experiment and a fresh error query returned zero entries, but clearing is
not acceptance evidence and must not be part of the final workflow. Unity was
idle, no temporary Monitor or Inspector remained, and selection was clear.
Exactly the seven standard windows remained: MainToolbar, Project, Inspector,
Hierarchy, Scene, Game, and Console.

## Screenshot automation guard

The plan and issue #314 prohibit both `ReadScreenPixel` and `PrintWindow`, but the
repository test guarded only the first API. Added `PrintWindow` to the blocked
capture primitives in `scripts/__tests__/design-system-dumps.test.js`.

The targeted test was green before the change because it did not yet check
`PrintWindow`. After adding the token, a temporary C# probe containing
`PrintWindow` made the test fail with the expected path and token; removing the
probe restored green:

```text
tests 3
pass 3
fail 0
```

The full Node suite also passed: 404 tests, 0 failures. `npm run
validate:all` passed every repository validation gate. The two Unity screenshot
surface fixtures then ran through `DxMcpTestRunner`:

```text
passCount: 64
failCount: 0
skipCount: 0
durationSeconds: 1.7148067
```

The result is retained at
`.artifacts/unity-mcp/session-177-screenshot-surfaces.json`. Post-test state
remained idle and clean, with empty selection, the seven standard windows, and
zero Console errors.

## GitHub and CI state

PR #316 remains the repository's only open PR and closes issue #305. Its static
CI passed. The original Unity 6000.3 standalone job failed before test execution
while repairing a missing Windows IL2CPP module: another process held the managed
Editor directory, three quarantine attempts failed, and the cleanup gate
correctly failed closed.

A controlled rerun of only that failed job completed before changing the PR
head. Retry job `90935151437` reproduced the defect:

- `unity install 6000.3.16f1 --accept-eula -m windows-il2cpp` emitted 7,029
  repeated progress lines at exactly 50 percent and made no progress-triple
  advance for 1,800 seconds;
- the heartbeat guard requested process-tree termination and returned sentinel
  exit 125 without separately proving that every descendant had exited;
- `Unity.exe` was resolvable afterward, but Windows IL2CPP was absent and the
  Unity CLI reported that the editor had no manageable modules;
- the atomic in-place reinstall reported that the editor was already installed,
  without supplying the module;
- uninstall plus all three bounded quarantine attempts then failed because a
  process still held
  `E:\actions-runner\_tool\u6-v3\6000.3.16f1\Editor`;
- the version-scoped stale-process sweep matched zero processes, so the script
  correctly surfaced the runner-operator `handle64.exe`/manual-delete
  remediation and the cleanup gate failed closed because the build lock had
  never been acquired.

The repeat is evidence of a persistent provisioning defect, not a transient
failure or a test failure. A read-only follow-up from the connected Unity host
could not inspect the failed cache directly because the host does not expose the
runner's `E:` tool-cache volume.

The provisioning guard used an invalid classification signal. The Unity beta
CLI was still emitting one progress line about every quarter-second when the
guard killed it, and run `26701943540` had also emitted the same unchanged
50-percent triple while laying down a resolvable editor. An unchanged
`(pct, phase, msg)` therefore cannot distinguish a hung install from a long,
active operation.

`Invoke-UnityCliCaptureWithTimeout` now resets its heartbeat on every
stdout/stderr line and retains the progress triple only for human-readable
notices. A genuinely silent child still receives sentinel exit 125 after the
profile-aware idle threshold; a child that remains noisy forever is still
bounded by the independent 2,700-second wall-clock sentinel 124. Both guards use
a monotonic `Stopwatch`. Before returning either sentinel, the wrapper requests
tree termination and confirms the direct child exited. A direct-child fallback
is not treated as proof of tree termination. If the direct child exited before
the tree request, if `Kill(true)` is unavailable or fails, or if the child cannot
be reaped after a second bounded termination attempt, the wrapper throws a
marked, non-retryable process-safety error. Provisioning cannot start another
attempt while a descendant may still hold the editor tree. A real rerun is
still required to learn whether the 6000.3 install completes under the new
policy; the prior logs do not prove that the termination request caused the
partial editor or the unidentified directory lock.

A hermetic AST-extracted PowerShell probe supplied the initial red-green
evidence. Before the fix, a fake installer emitting eight identical progress
lines at 200 ms intervals was killed after one second with exit 125 after only
six lines. After the fix, all eight lines completed with exit 0 and
`StallKilled == false`; a second fake installer that emitted no output was still
killed after one second with exit 125 and `StallKilled == true`.

The experiment is now retained in
`scripts/__tests__/test-unity-editor-heartbeat.ps1`. Its 32 assertions exercise
stdout and stderr activity, the environment override, monotonic periodic
notices, direct-child exit confirmation, descendant tree termination, the noisy
wall deadline, both outcomes of the second bounded reap, and the actual
fail-closed producer/consumer path for a quick-exit parent whose live orphan
holds inherited pipes. CI executes the test on Linux, macOS, and Windows.

The branch was then merged with current `master` at `645cde05`, bringing in the
session 176 prototype-exclusion guard and the latest performance-number update.
That integration raised JavaScript source LOC to 17,565 against the 17,500
budget. A table-driven consolidation in
`scripts/__tests__/ci-aggregate-workflow.test.js` preserves the workflow
contracts while removing repeated assertion scaffolding and reduces the total
to 17,485 after synchronizing the three newer PR-head commits. The focused
aggregate-workflow suite passes all 17 data-driven tests, and the
heartbeat probe passes all 32 assertions. The final full Node and repository
validation reruns remain pending after these changes. The full merged
`WallstopStudios.DxMessaging.Tests.Editor` assembly then passed 549 tests with
zero failures in 12.3873838 seconds through `DxMcpTestRunner`; the result is
retained at
`.artifacts/unity-mcp/session-177-full-editmode-after-merge.json`.

## Remaining work

- Commit the current provisioning, regression-test, documentation, and
  integration compaction changes; validate, push, and obtain current reviews and
  required checks.
- Switch the host to Personal/light through the Unity UI.
- Resolve the manifest's Unity 2022.3 LTS requirement versus the configured
  6000.4.6f1 host.
- Retain a reusable render-target capture helper, eliminate the Inspector IMGUI
  cursor diagnostic without clearing the Console, and emit RGB24 PNGs.
- Validate the helper on Project Settings and Flow Graph in addition to the
  Monitor and Inspector targets.
- Repeat the offscreen Inspector and Message Monitor captures. Capture the native
  menu cascade and combined Hierarchy/Inspector frames manually, or explicitly
  revise their scope.
- Inspect every final image, then update the documentation screenshots and
  `docs/guides/inspector-overlay.md` and
  `docs/images/inspector-overlay/README.md` manifest together. Convert the
  guide's three legacy `!!!` admonitions when editing it.
