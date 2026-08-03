# Session 187 -- Flow Graph clarity and sample recovery

Date: 2026-08-02
Branch: `dev/wallstop/session-187-flow-graph-clarity`
PR: [#347](https://github.com/Ambiguous-Interactive/DxMessaging/pull/347)
Issue: [#345](https://github.com/Ambiguous-Interactive/DxMessaging/issues/345)

## Outcome

This session changed Flow Graph from a dense analytics report into an
interactive node-and-edge graph. Message nodes connect to receiver nodes through
live route edges on a pannable, zoomable, automatically framed canvas. Selection
opens focused details below the graph. Route reports, overflow rows, trace
activity, and raw topology now live inside one collapsed analysis section.

The canvas now expresses routing semantics without labeling every line. A
crossing-aware layered layout orders both columns and each node's ports, while
arrowheads carry direction. Message and receiver nodes show named metrics rather
than `+N` shorthand. Targeted and broadcast route inspectors expose recent
component- and registration-exact delivery call sites without claiming the
target-aware API also supplied a sender object. Untargeted messages read as
global, and
`GlobalAcceptAll` registrations render as `GLOBAL OBSERVER / ANY MESSAGE`
instead of a fake `IMessage` message node. Route selection now opens responsive
route, activity, emission-evidence, and trace-evidence cards; the dense
technical report starts collapsed.

An empty live capture now gives recovery steps instead of rendering component
and message rows whose route counts are all zero. The Diagnostics Tooling
Exerciser also rebuilds receiver registrations and restarts its deterministic
burst after the initial scene load and from `OnEnable`, including consecutive
Play entries when Unity disables domain and scene reload.

## Contract updates

- Every filtered message, receiver, and registration route appears on the
  primary graph canvas; message-type overflow is navigated by pan and zoom, not
  rendered as a text list.
- Message nodes stay in the left column and receiver nodes stay in the right.
  Alternating barycentric sweeps reduce crossings, and opposite-layer order
  determines each node's port order.
- Arrowheads carry direction. Lines have no text labels; small route-kind shapes
  provide 30-pixel mouse and keyboard-focus targets without covering the paths.
- Nodes use named metrics and ellipsis-safe values, never `+N` shorthand.
- Selected routes use responsive cards and rows. Dense technical diagnostics
  start inside a collapsed foldout.
- Recent diagnostics associate emitting script/method call sites with matching
  broadcast-source and targeted-recipient routes. The graph does not infer a
  sender object that targeted emission APIs do not carry.
- The textual route map, its overflow rows, Route Insights, Trace Activity, and
  Topology Details start inside the collapsed **Analysis and Raw Data** section.
- Components or messages without captured routes produce an actionable empty
  state and no zero-value topology wall.
- Each active receiver releases a token from the previous Play generation and
  creates a new one before replaying registrations. Active receivers force this
  replay after the initial scene load so a stale enabled flag cannot suppress
  recovery. A per-activation guard starts one sequence when `OnEnable` and the
  post-scene-load callback both observe the same activation.

## Validation

- Focused Flow Graph and sample contract fixtures: **145 passed / 0 failed**.
- Stale-token reset and re-registration fixture: **7 passed / 0 failed**.
- Full `WallstopStudios.DxMessaging.Tests.Editor` assembly: **582 passed / 0
  failed** twice back-to-back, then **583 passed / 0 failed** after the final
  degenerate-mesh regression test.
- Full `WallstopStudios.DxMessaging.Tests.Runtime` assembly: **984 passed / 0
  failed** twice back-to-back.
- Live sample science on Unity 6000.4.6f1 across two reload-disabled Play
  entries: sequence 3, 3 components, 4 messages, 15 routes, 33 trace paths, 99
  calls, concrete default selection, and all advanced analysis collapsed.
- Live graph layout science on Unity 6000.4.6f1 rendered 4 message nodes, 3
  receiver nodes, and all 15 connections. Routes carried 0 text labels; nodes
  rendered 24 named metrics and 0 `+N` summaries. Selecting a live route opened
  4 structured sections and 5 activity tiles with technical diagnostics
  collapsed. Each route retained a 30 x 30 canvas hit target; the 0.8 readable
  zoom floor keeps it at least 24 x 24 screen pixels.
- Constrained-window science at 640 x 900 measured 0 horizontal section
  overflows and 0 route-endpoint overflows inside the 603-pixel details pane.
- Same-Play deactivate/reactivate science restarted the canceled runner and
  repopulated all 15 routes and 33 trace paths. The following Play entry reset
  the sample to sequence 3 and 99 calls.
- Unity Console: **0 errors / 0 warnings** after the live verification.
- Full Node.js script suite: **406 passed / 0 failed**.
- Documentation sample compilation, CSharpier, Prettier, markdownlint,
  spelling, `npm run validate:all`, ASCII checks, and `git diff --check` passed.
- Adversarial review covered lifecycle recovery, reset semantics, foldout
  persistence, parallel-route geometry, viewport preservation, large-graph
  framing, complete borders, Unity 2021.3 compatibility, documentation,
  formatting, and bloat. The final readability-overhaul pass reported zero
  actionable findings.
- Pull request CI ran the standalone IL2CPP performance suite with **408 total / 346
  passed / 0 failed / 62 skipped** and reported no measured regression.
- Unity 2021.3 EditMode exposed a fixture teardown gap after the new viewport
  interaction test opened a host window: the test assertions passed, but closing
  the window under `-nographics` emitted Unity's benign no-graphics error in the
  separate teardown phase. The fixture now reapplies the shared headless-only log
  suppression at teardown start, matching the existing editor-window fixtures.
  The freshly compiled focused fixture passed **140 / 140**, and the full Editor
  assembly passed **583 / 583** after the correction.
- Final semantic-route fixture on Unity 6000.4.6f1: **143 passed / 0 failed**.
- Final full Editor assembly after the semantic-route changes: **586 passed / 0
  failed**.
- Readability-overhaul Flow Graph fixture on the freshly reloaded Unity
  6000.4.6f1 assembly: **150 passed / 0 failed**. This includes crossing-aware
  layer ordering, staggered crossing selectors, the 0.8 readable zoom floor,
  named and filter-scoped node metrics, mixed-kind filtering, structured route
  cards, visible keyboard focus, exact context identity, and component- and
  registration-exact target evidence isolation.
- Full `WallstopStudios.DxMessaging.Tests.Editor` assembly after the readability
  overhaul: **594 passed / 0 failed**.
- Full Node.js script suite: **406 passed / 0 failed**; `npm run validate:all`,
  all pre-commit hooks, markdownlint, and `git diff --check` passed.
