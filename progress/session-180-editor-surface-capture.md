# Session 180 - Retained editor surface capture (WS-7.3 mechanism)

Date: 2026-07-31
Branch: `dev/wallstop/editor-surface-capture`

## Outcome

PLAN.md WS-7.3 had two halves: a repeatable capture mechanism, and the final
Personal/light artwork. This session closed the mechanism half.

`Tests/Editor/EditorSurfaceCapture.cs` is the retained helper session 177
lacked. It renders a real shipped editor surface into an offscreen render target
and writes a cropped 24-bit PNG, never reading the desktop.
`Tests/Editor/EditorSurfaceCaptureTests.cs` pins its contract with nine tests.

The artwork half stays open, and is blocked on the host rather than on code:
the configured host is Pro/dark (`isProSkin=True`, `UserSkin=1`), the manifest
requires Personal/light, and switching skins programmatically is banned. The
manifest also requests Unity 2022.3 LTS while the configured host is 6000.4.6f1.
No tracked screenshot was replaced.

## What the mechanism does

1. Hosts the surface in a `HideAndDontSave` window from
   `EditorWindowTestUtility` and shows it.
1. Creates a linear `RenderTexture`, makes it active, disables `GL.sRGBWrite`.
1. Drives the panel through `ValidateLayout()`, `Repaint(Event)` with
   `EventType.Repaint`, then `Render()`, all reflected with instance, public,
   and non-public binding flags and without `DeclaredOnly`.
1. Reads back the surface's own `worldBound` rather than the whole canvas.
1. Encodes RGB24 and verifies PNG color type 2 before writing the file.
1. Restores the render target and `GL.sRGBWrite` and destroys the window and
   both textures, including on the failure path.

## Findings

Four things were wrong on the first attempt, and each is now either fixed in
code or recorded in the manifest so it is not rediscovered.

**A never-shown window has no panel.** `EditorWindow.rootVisualElement.panel` is
null until the window is shown, so there is nothing to lay out or render. This
also surfaced a real defect in the shared test host:
`EditorWindowTestUtility.CloseWindow` called `EditorWindow.Close()`
unconditionally, and `Close()` dereferences a null parent for a window that was
never shown. Teardown threw `NullReferenceException` and, because the throw came
from a `finally`, it replaced the original exception and hid the actual failure.
`CloseWindow` now destroys a parentless window instead. Five leaked host windows
from that first run had to be swept from the host.

**Omitting the repaint step yields a valid PNG of a blank frame.**
`ValidateLayout()` then `Render()` alone produces a correctly sized, correctly
typed, entirely empty image. The tests count distinct colors so a blank frame
fails instead of passing as a valid PNG.

**A full-canvas readback frames the host window's chrome.** The first visually
inspected artifact showed the "DxMessaging Test Host" tab strip with the surface
clipped underneath it. Cropping to the surface's `worldBound` removes the chrome
and is also what gives each image the tight frame the capture list asks for.
Render-target rows start at the bottom while UI Toolkit measures from the top,
so the vertical origin is `canvasHeight - worldBound.yMax`.

**The `AddCursorRect` diagnostic does not apply to the overlay crops.** Session
177 hit `EditorGUIUtility.AddCursorRect called outside an editor OnGUI` while
rendering a whole live Inspector with an IMGUI body. The overlay images the
manifest asks for are crops of the package-owned UI Toolkit view, which renders
directly and emits nothing. The Console gained no entry across this session's
captures.

## Review findings folded in

Cursor Bugbot found two defects in the first push, and one of them had already
been found independently by an adversarial pass over the same code.

**The crop clamped instead of refusing.** A surface that started inside the
canvas but extended past an edge was silently trimmed to fit, so the helper
built to prevent clipped screenshots could quietly produce one. It now refuses a
surface that does not fit and names both rects, and
`CaptureRefusesASurfaceLargerThanTheCanvas` pins that.

**The failure-path restore test proved nothing.** It triggered the failure by
passing a zero canvas width, which throws in argument validation *before* the
capture takes over the render target or creates its host window, so the whole
`finally` block could have been deleted and the test would still pass. It now
fails from inside the try, by way of an oversized surface, and additionally
asserts the host window was closed.

Verified by mutation: deleting the two restore lines from the `finally` fails
both restore tests, including the failure-path one (7 passed, 2 failed). Under
the old test body that mutation went undetected.

Reviewing the refusal predicate then turned up a third defect, in the rounding
it depends on. The crop rounded the origin and the size independently, which
lets the two drift a pixel apart: a surface at `x=10.5 w=10.5` rounds to
`x=10 w=10`, a right edge of 20 where the real edge is 21. In a helper whose
purpose is unclipped documentation images, a lost pixel is a clipped surface.
The crop now rounds the edges and derives the size from them.

## Verification

All against the live host, Unity 6000.4.6f1, Direct3D11:

- `EditorSurfaceCaptureTests`: 9 passed, 0 failed;
- full `WallstopStudios.DxMessaging.Tests.Editor` assembly: 558 passed, 0 failed
  (549 before this session's 9 new tests, so no regression from the
  `CloseWindow` change);
- visual inspection: `MessageAwareComponentInspectorView` renders to a 720x63
  RGB24 crop, 293 distinct colors, no window chrome, no clipping, complete
  4-side border;
- Console after capture: one pre-existing unrelated Hot Reload warning, no
  capture diagnostic, nothing cleared;
- host after capture: seven windows, all standard Unity, no test-host leak, no
  leaked GameObjects, skin unchanged at `isProSkin=True` / `UserSkin=1`;
- `scripts/__tests__/design-system-dumps.test.js` and
  `editor-window-test-host.test.js`: green, including the blocked-capture-
  primitive bans;
- full Node/script suite: 406 passed, 0 failed;
- `npm run validate:all`, spelling, prettier, markdownlint, csharpier: passed.

The red-green record is in the capture runs themselves: 3/5 pass-fail on the
first run (teardown NRE), 6/2 once the panel existed (blank frames), 8/0 once
the repaint step was added, 8/0 again after the crop change, and 9/0 after the
review fixes.

## Follow-up

Issue #314 stays open for the artwork. What it now needs is a host in
Personal/light and a decision on Unity 2022.3 versus 6000.4, not further
mechanism work.
