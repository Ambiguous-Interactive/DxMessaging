# Session 188 -- Flow Graph interaction and density follow-up

Date: 2026-08-03
Branch: `dev/wallstop/session-188-flow-graph-followup`
PR: pending
Issue: [#345](https://github.com/Ambiguous-Interactive/DxMessaging/issues/345)

## Outcome

Issue #345 reopened immediately after session 187 merged with concrete follow-up
requests around route selection, aliasing, large graphs, source navigation, and
the initial amount of text. This session keeps the interactive graph direction
and closes those gaps.

The entire source-to-destination curve now accepts clicks through a sampled hit
corridor instead of making the midpoint route glyph the only interaction
target. Feathered route meshes reduce jagged edges. A selected route remains
bright while unrelated paths dim, preserving context without hiding topology.
Explicit minus, Fit, plus, and percentage controls complement wheel zoom, and
automatic framing can reach 20 percent for a useful overview of dense graphs.

The window no longer chooses a default node or route. Details appear only after
an intentional selection, and route or message evidence starts collapsed below
the primary identity and activity information. Resolvable message selections
open the exact declaration line. Captured call sites expose an **Open call
site** action for the exact recorded file and line.

## Contract updates

- Clicking anywhere along a visible route selects its exact
  message-to-receiver edge; an empty-canvas click remains available for panning.
- A low-opacity feather preserves the route hue around the core mesh.
- Selection dims unrelated routes without removing them from the graph.
- Fit and explicit zoom controls remain usable in a 640-pixel-wide toolbar.
- Automatic framing supports a 20 percent overview before users pan or zoom in.
- No details inspector renders until the user selects a node or route.
- Emission, trace, and technical evidence start collapsed.
- Message source resolution scans Unity compilation inputs for the captured
  runtime type and finds the exact class, struct, interface, or record
  declaration line. It distinguishes namespaces and declaring-type nesting,
  strips generic arity, and ignores comments and multiline string literals.
- Captured Unity call-site strings open their exact asset line.
- A stress fixture renders 40 messages, 40 receivers, and 400 many-to-many
  crossing routes, and asserts that every connection stays selectable.

## Unity MCP science

- Final focused Flow Graph fixture: **159 passed / 0 failed** in 11.98 seconds
  on Unity 6000.4.6f1.
- Final full `WallstopStudios.DxMessaging.Tests.Editor` assembly: **603 passed /
  0 failed / 0 skipped** in 7.45 seconds.
- A live attached-panel click at 20 percent along a route curve opened its route
  details and selected its matching graph markers.
- A live zoom-out button click changed the graph from 63 percent to 50 percent
  and updated the visible percentage label.
- At 640 x 900, the wrapped toolbar measured all hint and zoom-control bounds
  inside the root width.
- Dark-skin captures show a quiet no-selection initial view and a focused route
  view with unrelated routes dimmed and evidence collapsed.

## Repository validation and review

- Documentation snippet compilation: **441 passed / 0 failed**.
- Node.js script suite: **406 passed / 0 failed** after the final source changes.
- CSharpier, Prettier, Markdownlint, spelling, `npm run validate:all`, ASCII
  changed-line checks, and `git diff --check` passed.
- The latest `master` Unity Tests, Performance Numbers, and static CI completed
  green before publication; this branch fast-forwarded over its automated
  performance-number update without overlap.
- Adversarial review found three defects: crossing draw/hit order, a marker-only
  stress assertion, and ambiguous source lookup. The corrections added reverse
  hit order, an attached 400-route mouse integration, and scope-aware source
  scanning. Follow-up then found multiline verbatim strings could corrupt the
  scanner; persisted literal state and a fake-declaration regression resolved
  it. The final reviewer pass reported no remaining actionable issue.

## Publication

- Commit, push, draft PR, automated review, and PR CI remain pending.
