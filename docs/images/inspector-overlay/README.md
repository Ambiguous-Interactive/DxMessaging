# Inspector Overlay Screenshot Manifest

This directory holds the screenshots referenced from
[`docs/guides/inspector-overlay.md`](../../guides/inspector-overlay.md) and the
inspector-overlay sections of
[`docs/reference/analyzers.md`](../../reference/analyzers.md).

Every entry below currently ships as a 1x1 transparent placeholder PNG that
mkdocs accepts as a valid image so `mkdocs build --strict` produces no
warnings. Each entry tells the screenshot author exactly what to capture
(Unity version, scene state, component, expected UI annotations, recommended
dimensions). When you replace a placeholder PNG with the real screenshot at
the same filename, the docs pick up the real artwork automatically.

## Conventions

- **Format:** PNG (web-safe, 24-bit). No animated GIFs.
- **Width:** Aim for 960px-1200px for full-panel shots; 480px-720px for
  cropped HelpBox shots. Retina-quality (2x) renders are welcome but make
  sure the file size stays under 500 KB after compression.
- **Theme:** Capture using Unity's **Personal** (light) editor theme so the
  HelpBox background matches the Material for MkDocs default site theme.
  If you also want a Pro (dark) variant, suffix it `-dark.png` and add a
  separate placeholder; the docs do not currently consume dark variants.
- **Cropping:** Trim the OS chrome (Windows title bar, macOS traffic
  lights). Keep at least 16px of editor padding around the subject so the
  HelpBox does not look squished against the image edge.
- **Annotations:** Avoid burned-in arrows or call-outs unless explicitly
  requested by the placeholder. The docs already explain each control
  in prose; redundant annotations clutter the screenshot.
- **Privacy:** Make sure no user-specific paths, Unity license badges, or
  third-party asset thumbnails leak into the frame.

## Capture target: Unity 2022 LTS

Unless a placeholder explicitly says otherwise, capture in **Unity 2022.3
LTS** with the **Built-in render pipeline** and the DxMessaging package
embedded under `Packages/com.wallstop-studios.dxmessaging`. This mirrors
the package's primary supported configuration. If a placeholder calls out
Unity 2021.3 explicitly (because it depicts the fallback editor's distinct
behaviour), capture that one in 2021.3.

## Stub list

Each entry below corresponds to a `<filename>.png` 1x1 transparent
placeholder PNG already committed in this directory. Overwrite the
placeholder PNG with the captured screenshot at the same filename;
the docs already reference the `.png` path.

### `dxmsg009-overlay.png`

The Inspector HelpBox illustrating DXMSG009 (implicit hide / missing
modifier), drawn at the very top of a `MessageAwareComponent` subclass's
Inspector. The component should be a throwaway subclass that declares
`private void OnEnable() {}` (missing both `override` and `new`), which
triggers DXMSG009 at compile time. The HelpBox text should read along
the lines of `<FQN> has lifecycle methods that don't chain to
MessageAwareComponent (OnEnable) - DxMessaging will not function on
this component.` (the overlay text matches DXMSG006/007 because the IL
scanner classifies all three identically; see the analyzers reference
for the caveat). Beneath the HelpBox, capture both buttons: **Open
Script** and **Ignore this type**. This image doubles as the generic
"warning state" illustration used at the top of the inspector-overlay
guide and the analyzers reference. Recommended frame: 720px wide, just
the HelpBox plus the two buttons plus 12px of padding. Unity 2022.3
LTS, light theme.

### `inspector-actions.png`

A close-up of the HelpBox action row showing **Open Script** and
**Ignore this type** side by side, with no other Inspector chrome in
the frame. This is the "happy path" annotated reference image used in
the guide's "Three Inspector actions" section. Capture only the two
buttons plus ~6px padding above and below; roughly 480px wide.

### `inspector-ignored.png`

The HelpBox in its **info** state for a type that is currently in the
project ignore list. The HelpBox text reads `<FQN> is excluded from
the DxMessaging base-call check.` The single button below it is
**Stop ignoring**. To reproduce: pick a `MessageAwareComponent`
subclass that actually emits a warning, then add its FQN to the ignore
list in `Assets/Editor/DxMessagingSettings.asset` (or click "Ignore
this type" once). The HelpBox icon should be the blue info glyph, not
the yellow warning glyph. Recommended dimensions: 720px wide.

### `project-settings-panel.png`

The **Project Settings > Wallstop Studios > DxMessaging** page, captured as it currently
renders. The provider exposes exactly three controls (see
`Editor/Settings/DxMessagingSettingsProvider.cs`):

- **Diagnostics Targets** -- `EnumFlagsField` for the `DiagnosticsTarget`
  flags enum (`Off`, `Editor`, `Runtime`, `All`).
- **Message Buffer Size** -- integer field. Default is
  `IMessageBus.DefaultMessageBufferSize`.
- **Suppress Domain Reload Warning** -- boolean checkbox.

Capture the entire DxMessaging section of the Project Settings window
plus the breadcrumb that shows "DxMessaging" is selected in the left
sidebar. Recommended dimensions: 1024px-1200px wide. Unity 2022.3 LTS,
light theme. Recapture if/when more controls are wired into the
provider -- for now the additional fields (`BaseCallCheckEnabled`,
`BaseCallIgnoredTypes`, `UseConsoleBridge`) live on the asset
Inspector at `Assets/Editor/DxMessagingSettings.asset`, not here.

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
HelpBox at the top with a clear `OnEnable` callout in the missing-base
list. Frame both the GameObject Hierarchy entry on the left and the
Inspector pane on the right so the reader can see the offending
component is selected. Recommended dimensions: 1100px-1200px wide.

### `worked-example-after.png`

The "after" screenshot for the worked example, captured from the same
GameObject after the developer added the missing `base.OnEnable()`
call and recompiled. The HelpBox is gone -- the Inspector renders the
component cleanly with no DxMessaging overlay present. Same framing
as `worked-example-before.png` so the side-by-side comparison reads
naturally. Recommended dimensions: 1100px-1200px wide.

### `dxmsg006-overlay.png`

Cropped HelpBox + buttons for a class that triggers DXMSG006 on
`Awake`. Use a subclass like `MissingAwakeBase : MessageAwareComponent`
with `protected override void Awake() { /* missing base.Awake() */ }`.
Used in the analyzers reference page next to the DXMSG006 section.
Recommended dimensions: 720px wide.

### `dxmsg007-overlay.png`

Cropped HelpBox + buttons for a class that triggers DXMSG007 by
hiding `OnEnable` with `new`. Use a subclass like
`HidesWithNew : MessageAwareComponent` with `new void OnEnable() {}`.
The HelpBox surfaces the same "lifecycle methods that don't chain"
message -- DXMSG007 and DXMSG009 are visually indistinguishable in
the overlay because the IL scanner classifies both as DXMSG007. The
annotation in the reference page calls this out; the screenshot is
just the HelpBox. Recommended dimensions: 720px wide.

### `dxmsg010-overlay.png`

Cropped HelpBox + buttons for a class that triggers DXMSG010 via a
broken transitive base-call chain. Set up two subclasses: a parent
`BrokenIntermediate : MessageAwareComponent` with
`protected override void OnEnable() { }` (no base call) and a child
`LeafComponent : BrokenIntermediate` with
`protected override void OnEnable() => base.OnEnable();`. Capture
the Inspector for the **child** GameObject -- its override looks
correct in isolation, but DXMSG010 fires because the chain dies on
the parent. The HelpBox surfaces the same missing-method list the
overlay always shows. Recommended dimensions: 720px wide.

## When you replace a placeholder

1. Save the captured PNG with the matching filename (e.g.
   `dxmsg009-overlay.png`) inside this directory, overwriting the
   1x1 placeholder PNG already present.
1. Run `mkdocs build --strict` locally to confirm no link warnings
   surface; the build should be silent because the docs already
   reference the `.png` filename.
1. Update the sibling `.meta` file's GUID if Unity regenerates it on
   the next import. Every screenshot must have a matching `.meta`
   file (this is a hard requirement of the project's Unity-asset
   convention; see the existing `.meta` files in this directory for
   the format).

## Placeholder rationale

The PNGs committed here are 1x1 transparent placeholders -- the smallest
valid PNG payload that keeps `mkdocs build --strict` quiet on the markdown
image references. Replace each one with the captured screenshot at the
same filename when the real artwork is ready; the docs already reference
the `.png` paths.
