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

The tree contract is independent of heartbeat timing: after the descendant is
known to exist, `Confirm-UnityCliDirectChildExit` must request whole-tree
termination, confirm the direct child exit, and remove the descendant. The
test now waits for the PID-file creation event, checks those outcomes against
the production helper, and cleans up every process and temporary file on
failure.

Cursor review found that an exception during parent cleanup could skip the
later descendant and PID-file cleanup. Nested `finally` blocks now attempt
every cleanup stage and still propagate a cleanup failure.

The re-review also found that the descendant's original 10-second lifetime
matched the helper's two bounded 5-second waits. The synthetic descendant now
lives for 30 seconds, so only process-tree termination can make the assertion
pass inside the observation window.

The next review found that a file-created event could precede completion of
`WriteAllText`. The parent now writes to a sibling staging file and atomically
moves it to the watched PID path. The watcher can only observe the published
file after its content is complete.

## Verification

- focused heartbeat suite: 37 passed, 0 failed;
- former-flake loop: 50 consecutive focused-suite passes;
- full Node/script suite: 406 passed, 0 failed;
- `npm run validate:all` and spelling: passed;
- unchanged master rerun: static `CI Success` passed, confirming the original
  failure was intermittent;
- `git diff --check`: passed.
