"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

// Drift-guard for the auto-commit / state-branch workflows' remote-tracking fetches.
//
// These workflows shallow-fetch into an explicit refs/remotes/origin/<branch>
// destination to detect whether the branch advanced while a slow job ran. Without a
// leading `+`, once the branch HAS advanced the shallow local tracking ref shares no
// history with the fetched tip, so git rejects the ref update as non-fast-forward
// ("! [rejected] ... (non-fast-forward)", exit 1) -- aborting the step under
// `set -euo pipefail` in the very case the guard exists to detect (run 74494500574).
// The fix is git's own default-fetch idiom: force-update the tracking ref with `+` so
// the update succeeds, the subsequent SHA comparison runs, and the step proceeds (or
// skips) deliberately. The invariant is uniform across the full ("refs/heads/x:...")
// and short ("${BRANCH}:...") source forms, and whether the shallow boundary comes
// from `clone --depth 1` (stuck-job-watchdog) or `fetch --depth=1` (perf/llms).
const WORKFLOW_DIR = path.resolve(__dirname, "..", "..", ".github", "workflows");
const WORKFLOWS = ["perf-numbers.yml", "update-llms-txt.yml", "stuck-job-watchdog.yml"];

// Group 1 is the optional `+`. Bare "refs/remotes/origin/<branch>" tokens (checkout /
// rev-parse args, no `src:` colon) do not match: the `"` boundary stops `[^":]+`.
const REMOTE_TRACKING_REFSPEC = /"(\+?)(?:refs\/heads\/)?[^":]+:refs\/remotes\/origin\/[^"]+"/g;

for (const workflow of WORKFLOWS) {
  test(`${workflow}: remote-tracking fetch refspecs are force-prefixed ('+')`, () => {
    const source = fs.readFileSync(path.join(WORKFLOW_DIR, workflow), "utf8");
    const matches = [...source.matchAll(REMOTE_TRACKING_REFSPEC)];
    assert.ok(
      matches.length >= 1,
      `${workflow}: expected at least one remote-tracking fetch refspec (did the pattern change?)`
    );
    const unforced = matches.filter((m) => m[1] !== "+").map((m) => m[0]);
    assert.deepEqual(
      unforced,
      [],
      `${workflow} has unforced fetch refspec(s) -- prefix each with '+': ${unforced.join("; ")}`
    );
  });
}
