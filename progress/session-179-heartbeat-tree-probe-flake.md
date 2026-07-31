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

## Verification

- focused heartbeat suite: 37 passed, 0 failed;
- former-flake loop: 50 consecutive focused-suite passes;
- full Node/script suite: 406 passed, 0 failed;
- `npm run validate:all` and spelling: passed;
- unchanged master rerun: static `CI Success` passed, confirming the original
  failure was intermittent;
- `git diff --check`: passed.
