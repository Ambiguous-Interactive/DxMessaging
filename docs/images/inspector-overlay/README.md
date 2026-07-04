# Inspector Overlay Screenshot Manifest

This directory holds the screenshots referenced from
[`docs/guides/inspector-overlay.md`](../../guides/inspector-overlay.md) and the
inspector-overlay sections of
[`docs/reference/analyzers.md`](../../reference/analyzers.md).

Every entry below has a PNG asset so `mkdocs build --strict` produces no image
warnings while final captures are pending. These PNGs are draft/stale capture
assets, not final publishable screenshots. Each entry tells the screenshot
author exactly what to capture (Unity version, scene state, component, expected
UI annotations, recommended dimensions). When you replace a draft PNG with the
final screenshot at the same filename, the docs pick up the artwork
automatically.

## Conventions

- **Format:** PNG (web-safe, 24-bit). No animated GIFs.
- **Width:** Aim for 960px-1200px for full-panel shots; 480px-720px for
  cropped warning-panel shots. Retina-quality (2x) renders are welcome but make
  sure the file size stays under 500 KB after compression.
- **Theme:** Capture using Unity's **Personal** (light) editor theme so the
  Inspector panel background matches the Material for MkDocs default site theme.
  If you also want a Pro (dark) variant, suffix it `-dark.png` and add a
  separate manifest entry; the docs do not currently consume dark variants.
- **Cropping:** Trim the OS chrome (Windows title bar, macOS traffic
  lights). Keep at least 16px of editor padding around the subject so the
  warning panel does not look squished against the image edge.
- **Annotations:** Avoid burned-in arrows or call-outs unless explicitly
  requested by the capture entry. The docs already explain each control
  in prose; redundant annotations clutter the screenshot.
- **Privacy:** Make sure no user-specific paths, Unity license badges, or
  third-party asset thumbnails leak into the frame.

## Automation status

Unity MCP scene/camera captures are suitable for scene-layout checks, but the
current host setup has not yet proven a complete editor-window screenshot path
for these docs targets. On 2026-07-03,
`UnityEditorInternal.InternalEditorUtility.ReadScreenPixel` probes captured the
visible VS Code desktop instead of Unity editor windows, even when MCP-reported
Unity window coordinates were used. A later synchronous Win32/GDI `PrintWindow`
probe captured a separate Unity utility window correctly after vertically
flipping the `GetDIBits` rows. The same synchronous path captured the actual
Project Settings window with correct content when rows used their original
order, but the host editor stayed in Pro/dark skin and the image still included
OS chrome. A live editor-skin switch later changed the host to
Personal/light-theme asynchronously, but the follow-up Project Settings capture
timed out without writing an artifact and left Unity MCP unresponsive. The
delayed callback attempt also stalled, and the path has not yet been proven
against the Inspector or menu cascade targets. Do not overwrite the tracked
Inspector overlay PNGs until the actual target artifact is Personal/light-theme,
cropped per this manifest, visually inspected, and Unity has a clean
post-capture console and editor-window list.

Do not switch editor skins as part of automation. Start from an editor that is
already in Personal/light theme, record `EditorGUIUtility.isProSkin` and the
`UserSkin` editor preference before capture, and abort if the editor is not
already in the expected skin.

## Capture target: Unity 2022 LTS

Unless an entry explicitly says otherwise, capture in **Unity 2022.3 LTS** with
the **Built-in render pipeline** and the DxMessaging package embedded under
`Packages/com.wallstop-studios.dxmessaging`. This mirrors the package's primary
supported configuration. If an entry calls out Unity 2021.3 explicitly, capture
that one in 2021.3 so the package-owned editor path is represented.

The warning-panel screenshots in this directory target DxMessaging's
package-owned UI Toolkit inspector path. A `MessageAwareComponent` with a
user-defined custom editor uses the header-hook IMGUI HelpBox path instead;
that compatibility surface is documented in prose but is not currently a
screenshot target.

## Capture list

Each entry below corresponds to a `<filename>.png` asset already committed in
this directory. Overwrite the draft PNG with the final captured screenshot at
the same filename; the docs already reference the `.png` path.

### `dxmsg009-overlay.png`

The UI Toolkit Inspector warning panel illustrating DXMSG009 (implicit
hide / missing modifier), drawn at the very top of a
`MessageAwareComponent` subclass's Inspector. Capture this with a
throwaway component that has no user-defined `[CustomEditor]`, so Unity
selects DxMessaging's package-owned component editor instead of the
custom-editor IMGUI header-hook path. The component should declare
`private void OnEnable() {}` (missing both `override` and `new`), which
triggers DXMSG009 at compile time. The panel title should be
**Missing MessageAwareComponent base calls**. Its body should name the
FQN and state that lifecycle methods do not chain to
`MessageAwareComponent`; the method list should include `OnEnable`
and its runtime consequence. The overlay text matches DXMSG006/007
because the IL scanner classifies all three identically; see the
analyzers reference for the caveat. Beneath the panel, capture both
buttons: **Open Script** and **Ignore this type**. This image doubles as the generic
"warning state" illustration used at the top of the inspector-overlay
guide and the analyzers reference. Recommended frame: 720px wide, just
the panel plus the two buttons plus 12px of padding. Unity 2022.3
LTS, light theme.

### `inspector-actions.png`

A close-up of the warning-panel action row showing **Open Script** and
**Ignore this type** side by side, with no other Inspector chrome in
the frame. This is the "happy path" annotated reference image used in
the guide's "Three Inspector actions" section. Capture only the two
buttons plus ~6px padding above and below; roughly 480px wide.

### `inspector-ignored.png`

The UI Toolkit warning panel in its **info** state for a type that is
currently in the project ignore list. Capture this with the package-owned
component editor path. The body text reads `<FQN> is excluded from
the DxMessaging base-call check.` The single button below it is
**Stop ignoring**. To reproduce: pick a `MessageAwareComponent`
subclass that actually emits a warning, then add its FQN to the ignore
list in `Assets/Editor/DxMessagingSettings.asset` (or click "Ignore
this type" once). The panel should use the info color rail, not the
warning color rail. Recommended dimensions: 720px wide.

### `project-settings-panel.png`

The **Project Settings > Wallstop Studios > DxMessaging** page, captured as it currently
renders. The provider exposes six controls across three sections (see
`Editor/Settings/DxMessagingSettingsProvider.cs`):

- **Diagnostics / Diagnostics Targets** -- `PropertyField` for the
  `DiagnosticsTarget` flags enum (`Off`, `Editor`, `Runtime`, `All`).
- **Diagnostics / Message Buffer Size** -- integer field. Default is
  `IMessageBus.DefaultMessageBufferSize`.
- **Editor Safety / Suppress Domain Reload Warning** -- boolean checkbox.
- **Inspector Checks / Base-Call Check Enabled** -- boolean toggle.
- **Inspector Checks / Use Console Bridge** -- boolean toggle.
- **Inspector Checks / Ignored Base-Call Types** -- editable list of fully-qualified
  `MessageAwareComponent` type names excluded from overlay/analyzer base-call
  warnings.

Capture the entire DxMessaging section of the Project Settings window
plus the breadcrumb that shows "DxMessaging" is selected in the left
sidebar. Recommended dimensions: 1024px-1200px wide. Unity 2022.3 LTS,
light theme. Include the Ignored Base-Call Types list even if it is empty.

Current asset status: the tracked PNG is a stale dark-theme capture of the old
three-control page and does not show the ignored-types list. Recapture this image
before treating the Inspector Overlay guide's screenshot set as publishable.

### `tools-menu-rescan.png`

The Unity menu bar dropdown showing **Tools > Wallstop Studios > DxMessaging > Rescan
Base-Call Warnings**. Open the menu, hover over **DxMessaging** so the
sub-menu is expanded with **Rescan Base-Call Warnings** highlighted.
Crop to just the menu cascade plus a sliver of the editor window
behind it for context. Recommended dimensions: 480px-640px wide.

### `worked-example-before.png`

The "before" screenshot for the guide's worked example: a
`MessageAwareComponent` subclass named `HealthComponent` whose
`OnEnable` override does not call `base.OnEnable()`, attached to an
empty GameObject in the Hierarchy. The Inspector should show the
warning panel at the top with a clear `OnEnable` callout in the missing-base
list. Frame both the GameObject Hierarchy entry on the left and the
Inspector pane on the right so the reader can see the offending
component is selected. Recommended dimensions: 1100px-1200px wide.

### `worked-example-after.png`

The "after" screenshot for the worked example, captured from the same
GameObject after the developer added the missing `base.OnEnable()`
call and recompiled. The warning panel is gone -- the Inspector renders the
component cleanly with no DxMessaging overlay present. Same framing
as `worked-example-before.png` so the side-by-side comparison reads
naturally. Recommended dimensions: 1100px-1200px wide.

### `dxmsg006-overlay.png`

Cropped UI Toolkit warning panel + buttons for a class that triggers
DXMSG006 on `Awake`. Use a subclass like
`MissingAwakeBase : MessageAwareComponent` with
`protected override void Awake() { /* missing base.Awake() */ }` and no
user-defined custom editor. Used in the analyzers reference page next to
the DXMSG006 section. Recommended dimensions: 720px wide.

### `dxmsg007-overlay.png`

Cropped UI Toolkit warning panel + buttons for a class that triggers
DXMSG007 by hiding `OnEnable` with `new`. Use a subclass like
`HidesWithNew : MessageAwareComponent` with `new void OnEnable() {}` and
no user-defined custom editor. The panel surfaces the same "lifecycle
methods that do not chain" message -- DXMSG007 and DXMSG009 are visually
indistinguishable in the overlay because the IL scanner classifies both
as DXMSG007. The annotation in the reference page calls this out; the
screenshot is just the panel. Recommended dimensions: 720px wide.

### `dxmsg010-overlay.png`

Cropped UI Toolkit warning panel + buttons for a class that triggers
DXMSG010 via a broken transitive base-call chain. Set up two subclasses:
a parent `BrokenIntermediate : MessageAwareComponent` with
`protected override void OnEnable() { }` (no base call) and a child
`LeafComponent : BrokenIntermediate` with
`protected override void OnEnable() => base.OnEnable();`. Do not add a
user-defined custom editor. Capture the Inspector for the **child**
GameObject -- its override looks correct in isolation, but DXMSG010 fires
because the chain dies on the parent. The panel surfaces the same
missing-method list the overlay always shows. Recommended dimensions:
720px wide.

## When you replace a screenshot

1. Save the captured PNG with the matching filename (e.g.
   `dxmsg009-overlay.png`) inside this directory, overwriting the draft PNG
   already present.
1. Run `mkdocs build --strict` locally to confirm no link warnings
   surface; the build should be silent because the docs already
   reference the `.png` filename.
1. Record the pre-capture and post-capture `EditorGUIUtility.isProSkin` and
   `UserSkin` values. Do not rely on live skin switching to produce the required
   Personal/light-theme artifact.
1. Update the sibling `.meta` file's GUID if Unity regenerates it on
   the next import. Every screenshot must have a matching `.meta`
   file (this is a hard requirement of the project's Unity-asset
   convention; see the existing `.meta` files in this directory for
   the format).

## Draft asset rationale

The PNGs committed here keep `mkdocs build --strict` quiet on the markdown
image references while the final Unity captures are still in progress. Replace
each draft image with a matching Personal/light-theme capture at the same
filename when the real artwork is ready; the docs already reference the `.png`
paths.
