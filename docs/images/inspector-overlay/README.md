# Editor Tooling Screenshot Manifest

This directory holds the generated screenshots used by the Inspector, diagnostics,
and analyzer documentation. The images show package-owned UI only. Native Unity
window chrome, menus, Hierarchy panes, project names, and desktop content are outside
the capture boundary.

## Automated capture

`EditorToolingDocumentationCaptureTests.CaptureAllPublishedEditorTooling` builds the
real shipped UI trees with deterministic sample data and renders all nine images in one
explicit operation. Run that test through Unity MCP in a graphics-enabled Editor. The
ordinary `CaptureInventoryNamesEveryPublishedAutomatedSurfaceExactlyOnce` test keeps
the output inventory covered in normal CI without rewriting tracked documentation.

The writer uses `EditorSurfaceCapture` and follows this sequence:

1. Build the shipped Inspector, Project Settings, Message Monitor, and Flow Graph
   surfaces. Do not use prototype UXML or screenshot-only copies.
1. Show the tracked `HideAndDontSave` capture host as a popup. Popup mode supplies an
   attached panel without drawing the host's dock tab into the render target.
1. Settle UI Toolkit layout, repaint the panel, and render into a temporary linear
   `RenderTexture` with `GL.sRGBWrite` disabled.
1. Read only the package surface's laid-out bounds into an RGB24 `Texture2D` and require
   PNG color type 2.
1. Stage every image under `Temp/`. Copy the complete set into this directory only
   after all nine renders pass. If replacement fails, restore every prior image.
1. Restore render state and destroy the textures, window, settings object, and hidden
   component host on both success and failure.

The automation never reads desktop pixels, invokes native window capture, or changes
the Editor skin. The source guard in
`scripts/__tests__/design-system-dumps.test.js` keeps those primitives blocked.

## Published capture set

All images were generated and visually reviewed on 2026-08-27 on the configured macOS
host with Unity 6000.4.6f1 in Pro/dark skin. Host OS, skin, and Unity version are
capture metadata, not acceptance gates. Every file is RGB24 PNG color type 2.

| File                          | Package-owned subject                                   | Size     |
| ----------------------------- | ------------------------------------------------------- | -------- |
| `dxmsg006-overlay.png`        | Missing `Awake` base-call warning and actions           | 720x139  |
| `dxmsg007-overlay.png`        | Explicitly hidden `OnEnable` warning and actions        | 720x139  |
| `dxmsg009-overlay.png`        | Implicitly hidden `OnEnable` warning and actions        | 720x139  |
| `dxmsg010-overlay.png`        | Broken transitive `OnEnable` chain warning and actions  | 720x139  |
| `inspector-ignored.png`       | Ignored-type information state and **Stop ignoring**    | 720x88   |
| `inspector-subscriptions.png` | Edit-mode **Message subscriptions** state               | 720x86   |
| `project-settings-panel.png`  | All seven current DxMessaging Project Settings controls | 720x600  |
| `message-monitor.png`         | Snapshot Monitor ready-state with diagnostics enabled   | 1120x520 |
| `flow-graph.png`              | Two-message, two-receiver, four-route topology          | 1200x800 |

DXMSG007 and DXMSG009 are visually identical because the Editor's IL scanner cannot
distinguish an explicit `new` hide from an implicit hide. The compile-time analyzer
remains authoritative for the diagnostic ID. DXMSG010 uses the same warning layout but
represents a broken parent chain.

## Review contract

Inspect every refreshed image at native resolution before keeping it. Reject a set if
any image has:

- clipped or missing text, controls, rows, nodes, borders, or scroll content;
- capture-host tabs, native window chrome, duplicate windows, or prototype UI;
- inconsistent theme classes or incomplete semantic borders;
- user paths, project names, license data, third-party assets, or other sensitive data;
- an alpha channel, a blank frame, or dimensions different from the table above.

After capture, confirm the original scenes remain clean, the main stage is active, the
selection is unchanged, the skin is unchanged, and no capture error was added to the
Console. Do not clear existing Console entries as part of verification.

Native menu cascades and combined Hierarchy/Inspector frames are intentionally
documented in prose rather than screenshots. They contain Unity-owned chrome and cannot
be produced by the package's desktop-independent renderer. This keeps every published
tooling image on the same repeatable, non-desktop capture path.
