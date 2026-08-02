# Session 187 -- Flow Graph clarity and sample recovery

Date: 2026-08-02
Branch: `dev/wallstop/session-187-flow-graph-clarity`
PR: pending
Issue: [#345](https://github.com/Ambiguous-Interactive/DxMessaging/issues/345)

## Outcome

This session changed Flow Graph from a dense analytics report into a route-first
inspection surface. It now selects a concrete route on first open, keeps the
first eight routes visible, moves overflow routes into a collapsed section, and
puts route insights, trace activity, and raw topology behind collapsed sections.
The route header gives the immediate route, message, receiver, and call totals.

An empty live capture now gives recovery steps instead of rendering component
and message rows whose route counts are all zero. The Diagnostics Tooling
Exerciser also rebuilds receiver registrations and restarts its deterministic
burst after the initial scene load and from `OnEnable`, including consecutive
Play entries when Unity disables domain and scene reload.

## Contract updates

- Concrete registration routes sort before `GlobalAcceptAll` routes. Calls,
  recent traced deliveries, names, paths, ids, and registration type provide a
  deterministic order within that split.
- The route map shows eight routes before moving the remainder into a collapsed
  overflow section. Selecting an overflow route expands that section.
- Route Insights, Trace Activity, and Topology Details start collapsed.
- Components or messages without captured routes produce an actionable empty
  state and no zero-value topology wall.
- Each active receiver releases a token from the previous Play generation and
  creates a new one before replaying registrations. Active receivers force this
  replay after the initial scene load so a stale enabled flag cannot suppress
  recovery. A per-activation guard starts one sequence when `OnEnable` and the
  post-scene-load callback both observe the same activation.

## Validation

- Focused Flow Graph and sample contract fixtures: **143 passed / 0 failed**.
- Stale-token reset and re-registration fixture: **7 passed / 0 failed**.
- Full `WallstopStudios.DxMessaging.Tests.Editor` assembly: **580 passed / 0
  failed** twice back-to-back.
- Full `WallstopStudios.DxMessaging.Tests.Runtime` assembly: **984 passed / 0
  failed** twice back-to-back.
- Live sample science on Unity 6000.4.6f1 across two reload-disabled Play
  entries: sequence 3, 3 components, 4 messages, 15 routes, 33 trace paths, 99
  calls, concrete default selection, 7 collapsed overflow routes, and all three
  advanced sections collapsed.
- Same-Play deactivate/reactivate science restarted the canceled runner and
  repopulated all 15 routes and 33 trace paths. The following Play entry reset
  the sample to sequence 3 and 99 calls.
- Unity Console: **0 errors / 0 warnings** after the live verification.
- Full Node.js script suite: **406 passed / 0 failed**.
- Documentation sample compilation, CSharpier, Prettier, markdownlint,
  spelling, `npm run validate:all`, ASCII checks, and `git diff --check` passed.
- Four adversarial review rounds covered lifecycle recovery, reset semantics,
  foldout persistence, test cleanup, Unity 2021.3 compatibility, documentation,
  formatting, and bloat. The final review reported zero findings.
