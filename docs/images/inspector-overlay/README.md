# Editor Tooling Screenshot Manifest

This directory holds the generated screenshots used by the Inspector, diagnostics,
and analyzer documentation. The images show package-owned UI or a labeled compiler
diagnostic specimen built from the published diagnostic text and triggering C#.
Native Unity window chrome, menus, Hierarchy panes, project names, and desktop content
are outside the capture boundary.

## Automated capture

`EditorToolingDocumentationCaptureTests.CaptureAllPublishedEditorTooling` builds the
real shipped UI trees and labeled compiler specimens with deterministic sample data,
then renders all 18 images in one explicit operation. Run that test through Unity MCP
in a graphics-enabled Editor. The
ordinary `CaptureInventoryNamesEveryPublishedAutomatedSurfaceExactlyOnce` test keeps
the output inventory covered in normal CI without rewriting tracked documentation.

The writer uses `EditorSurfaceCapture` and follows this sequence:

1. Build the shipped Inspector, Project Settings, Message Monitor, and Flow Graph
   surfaces, plus the three labeled compiler diagnostic specimens. Do not use
   prototype UXML or screenshot-only copies of package tooling.
1. Show the tracked `HideAndDontSave` capture host as a popup. Popup mode supplies an
   attached panel without drawing the host's dock tab into the render target.
1. Settle UI Toolkit layout, then repaint and render the panel three times into a
   temporary linear `RenderTexture` with `GL.sRGBWrite` disabled. The later passes
   paint scroll content and dynamic-font glyphs realized by the earlier passes.
1. Read only the package surface's laid-out bounds into an RGB24 `Texture2D` and require
   PNG color type 2.
1. Stage every image under `Temp/`. Copy the complete set into this directory only
   after all 18 renders pass. If replacement fails, restore every prior image.
1. Restore render state and destroy the textures, window, settings object, and hidden
   component host on both success and failure.

The automation never reads desktop pixels, invokes native window capture, or changes
the Editor skin. The source guard in
`scripts/__tests__/design-system-dumps.test.js` keeps those primitives blocked.

## Published capture set

All images were generated and visually reviewed on 2026-08-28 on the configured macOS
host with Unity 6000.4.6f1 in Pro/dark skin. Host OS, skin, and Unity version are
capture metadata, not acceptance gates. Every file is RGB24 PNG color type 2.

| File                                | Subject                                                 | Size      |
| ----------------------------------- | ------------------------------------------------------- | --------- |
| `dxmsg002-compiler-diagnostic.png`  | DXMSG002 error and its triggering C# declaration        | 900x440   |
| `dxmsg003-compiler-diagnostic.png`  | DXMSG003 warning and its triggering C# declaration      | 900x440   |
| `dxmsg004-compiler-diagnostic.png`  | DXMSG004 info and its triggering C# declaration         | 900x440   |
| `dxmsg006-overlay.png`              | DXMSG006 missing `Awake` base call                      | 720x139   |
| `dxmsg007-overlay.png`              | DXMSG007 explicit `OnEnable` hide                       | 720x139   |
| `dxmsg008-overlay.png`              | DXMSG008 opt-out state and **Stop ignoring**            | 720x88    |
| `dxmsg009-overlay.png`              | DXMSG009 implicit `OnDisable` hide                      | 720x139   |
| `dxmsg010-overlay.png`              | DXMSG010 broken transitive `OnDestroy` chain            | 720x154   |
| `inspector-subscriptions.png`       | Edit-mode **Message subscriptions** state               | 720x86    |
| `project-settings-panel.png`        | All seven current DxMessaging Project Settings controls | 720x600   |
| `message-monitor.png`               | Three route kinds with newest-message details           | 1120x740  |
| `message-monitor-components.png`    | Expanded diagnostics for two loaded components          | 1120x1020 |
| `message-monitor-filtered.png`      | Typed facets plus Broadcast-only route filtering        | 1120x820  |
| `message-monitor-selected.png`      | Selected Broadcast row with expanded stack              | 1120x860  |
| `flow-graph.png`                    | Two-message, two-receiver, four-route topology          | 1200x800  |
| `flow-graph-message-selected.png`   | Selected message/source node and evidence               | 1200x1100 |
| `flow-graph-component-selected.png` | Selected receiver/destination node and evidence         | 1200x1200 |
| `flow-graph-route-selected.png`     | Selected route and evidence                             | 1200x1300 |

The current catalog has no DXMSG001. DXMSG002 through DXMSG005 are compiler-only
source-generator diagnostics and do not own an Inspector or EditorWindow surface.
The DXMSG002 through DXMSG004 images are labeled documentation specimens that pair
their exact output with triggering code without imitating Unity Console or IDE chrome.
The Inspector gallery starts at DXMSG006 and includes the DXMSG008 opt-out state.
Diagnostic IDs appear in the shipped overlay title, and the ordinary capture contract
rejects byte-identical Inspector diagnostic images.

The Message Monitor capture uses the shipped rows and detail cards. Its static capture
surface replaces the two nested `ScrollView` wrappers with clipped containers because
an offscreen popup does not receive the native docked-window geometry event that makes
those nested bodies paint. The production window keeps its interactive scroll views.

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
