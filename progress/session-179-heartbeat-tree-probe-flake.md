# Session 179 - Heartbeat tree-probe flake

Date: 2026-07-31
Branch: `codex/fix-heartbeat-tree-probe-flake`

## Outcome

The Windows heartbeat test now synchronizes on a descendant PID before it
exercises the real process-tree termination helper. It no longer assumes that
a fresh PowerShell process can start, spawn its child, and flush that child's
PID within one second.

Production timeout, heartbeat, and process-tree behavior is unchanged.

## Root cause

Static CI run `30593187996` failed its first attempt because the one-second
heartbeat killed the test parent before that process had created and reported
its descendant. The test then had no PID to assert against. Re-running the
unchanged commit passed, which confirmed an intermittent test timing failure.

The tree contract is independent of heartbeat timing: once the descendant is
known to exist, `Confirm-UnityCliDirectChildExit` must request whole-tree
termination, confirm the direct child exit, and remove the descendant. The
probe now waits for a published descendant PID, checks those outcomes against
the production helper, and cleans up every process and temporary file on
failure.

## Review findings folded in

Cursor review found three defects in the first fix, and verifying the third
uncovered a fourth:

1. An exception during parent cleanup could skip the later descendant and
   PID-file cleanup. Nested `finally` blocks now attempt every cleanup stage
   and still propagate a cleanup failure.
1. The descendant's original 10-second lifetime matched the helper's two
   bounded 5-second waits, so a descendant that survived tree termination could
   exit on its own inside the observation window. The synthetic descendant now
   lives for 60 seconds against a 25-second worst-case probe.
1. A file-created event could precede completion of `WriteAllText`. The parent
   now writes to a sibling staging file and atomically moves it to the
   published PID path.
1. `FileSystemWatcher` cannot observe that publication at all. A
   same-directory `File.Move` raises `Renamed`, not `Created`, so
   `WaitForChanged([WatcherChangeTypes]::Created, 10000)` exhausted its full
   timeout every run and the probe fell through to an unsynchronized
   `Test-Path`. A standalone repro confirmed it: the file appeared at 300 ms
   and `WaitForChanged` still returned `TimedOut=True` after 3003 ms of a
   3000 ms budget.

The watcher is removed rather than repaired. `Wait-ForPublishedProcessId`
polls for a *parseable, positive* process id, which is what actually makes the
read safe - a partially written or empty file fails to parse and the wait
continues - and it has no platform-specific event semantics to get wrong.
Removing the watcher also deletes one layer of the cleanup pyramid.

The fourth finding exposed a fifth defect, in the cleanup guard itself:
`Stop-Process` only requests termination, so verifying it with an immediate
`Get-Process` races the kernel and can report a successful kill as a surviving
process. `Wait-ForProcessExit` polls to a 10-second bound instead. A sweep
found no other instance of this pattern; the one production `Stop-Process` in
`ensure-editor.ps1` reports its outcome and does not re-check.

Both publish sites (the tree probe and the detached-orphan probe) now use the
same atomic publish, validating read, and verified cleanup, so the
partial-read class is gone rather than patched at one call site.

Re-review then found a sixth defect, and it was a coverage regression the fix
itself introduced. Driving `Confirm-UnityCliDirectChildExit` directly is what
makes the probe independent of startup timing, but
`Invoke-UnityCliCaptureWithTimeout` has its OWN `Kill($true)` call sites for the
stall and wall-clock paths, and those are the ones CI actually runs. If either
regressed to a bare `Kill()`, the parent would exit before
`Confirm-UnityCliDirectChildExit` ran, that helper would see an already-exited
direct child and skip tree termination, and a reparented Unity installer would
be orphaned holding the editor tree -- with the direct probe still green.

Both wrapper paths are now covered end to end.

The first attempt at the stall probe was wrong in an instructive way, and the
re-review caught it: it claimed that emitting a line immediately after
publication started the stall countdown at publication. It does not.
`lastActivityMs` starts at 0, so the FIRST stall window runs from process
launch, and a slow nested `pwsh` start could kill the parent before publication
-- reintroducing the exact flake this branch exists to remove.

The stall clock cannot be made to start at publication; that is the wrapper's
contract and a test does not get to change it. What the probe does instead is
emit BEFORE spawning the descendant, which keeps the nested `Start-Process` and
the PID write out of the first window and leaves only pwsh's own startup inside
it, against an eight-second window. That is a wide margin, not a guarantee, so
the probe proves the margin held rather than assuming it: the wrapper must have
READ the published marker before it killed. A cold start that ever did overrun
the window fails a named assertion instead of silently voiding the tree check.

The wall clock runs from launch and has no equivalent trick available, so it
uses ten seconds against a sub-second publish and fails loudly on a missed
publish.

## Verification

Red evidence, on the pre-fix branch head:

- standalone `FileSystemWatcher` repro: `WaitForChanged(Created)` returned
  `TimedOut=True` after 3003 ms while the published file existed from 300 ms;
- focused heartbeat suite wall clock: 34.07 s.

Green evidence, after the fix:

- focused heartbeat suite: 44 passed, 0 failed. It runs in 40.8 s: the watcher
  fix took 34.07 s down to 21.4 s, and the two new wrapper probes spend that
  back plus 6 s on coverage CI did not previously have;
- former-flake loop: 20 consecutive focused-suite passes before the wrapper
  probes, 5 after;
- mutation - direct-child-only kill instead of tree kill: fails on
  `tree termination removes the descendant` (36 passed, 1 failed), so the
  assertion discriminates a real tree-termination regression;
- mutation - parent never publishes the descendant PID: fails on
  `tree probe captures the descendant process id` (35 passed, 2 failed)
  instead of throwing a cast error;
- mutation - the wrapper's stall-path `Kill($true)` weakened to `Kill()`: fails
  on `wrapper stall termination removes the descendant`, and on nothing else;
- mutation - the wrapper's wall-clock `Kill($true)` weakened to `Kill()`: fails
  on `wrapper wall-clock termination removes the descendant`, and on nothing
  else. Each assertion tracks its own kill site;
- simulation - a five-second delay before the stall probe's first emission,
  standing in for a cold start that overruns the window: fails
  `wrapper stall path killed only after publication` by name, so the margin
  failing is reported as itself rather than as a mysterious tree-check failure;
- full Node/script suite: 406 passed, 0 failed;
- `npm run validate:all` and spelling: passed;
- unchanged master rerun: static `CI Success` passed, confirming the original
  failure was intermittent;
- `git diff --check`: passed.
