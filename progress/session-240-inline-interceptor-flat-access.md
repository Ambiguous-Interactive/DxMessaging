# Session 240 - Inline interceptor flat access

Date: 2026-08-26
Branch: `perf/session-240-inline-interceptor-flat-access`
Status: in progress - candidate evidence pending

## Priority audit

The session started from `6d230b2e`, matching `origin/master`. GitHub had seven open issues and no
open or draft pull requests. Issue #414 remained the only gameplay-path issue. No dependency alert
or pull request required incorporation. Current-main static CI passed; the newly started Unity,
performance, and devcontainer runs had no failures when inspected.

PR #467 established the required Windows execution profile and brought all seven paired comparison
spreads below the fixed 3% limit. That evidence opened the gate for the next independent runtime
candidate.

## Candidate

The candidate adds `MethodImplOptions.AggressiveInlining` to
`InterceptorCache<TValue>.EnsureFlat`. Session 236 native evidence showed that this call survives
inside the interceptor loop after the rejected outer-loop inlining experiment. The method's
steady-state branch only reads the dirty flag, cached array, and count. Its cold dirty branch still
calls the larger `RebuildFlat` method.

The candidate does not change interceptor ordering, frozen-array ownership, mutation visibility,
or reset behavior. Existing interceptor, lifecycle, reentrancy, and allocation contracts cover
those semantics.

## Acceptance protocol

The change is retained only if a fresh Standalone IL2CPP Release candidate/control/candidate bracket
meets every predeclared gate:

- all three runs use the execution profile accepted by PR #467;
- both candidate arms and the control retain every paired raw cycle;
- the two candidate `Filtered` ratios improve by a geometric combination strictly greater than 3%
  over control;
- both outer same-code candidate ratios and every run's raw-cycle spread stay within 3%;
- `FilteredPostProcess` does not regress;
- the allocation probe remains at zero calls;
- native disassembly proves the `EnsureFlat` call disappeared while `RebuildFlat` stayed outlined.

## Local evidence

`npm run validate:all` passed on the candidate. CSharpier formatted the changed source without a
residual diff. The Unity MCP probe reported that no editor-backed endpoint advertised
`Unity_RunCommand`, so no local Unity result has been accepted yet.
