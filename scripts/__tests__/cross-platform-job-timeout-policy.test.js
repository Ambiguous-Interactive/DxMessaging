/**
 * @fileoverview Regression guard: matrix CI jobs that run the full
 * `preflight:pre-push` on the (slower) windows-latest runner must have enough
 * job-timeout headroom AND a per-step timeout on the heavy preflight step.
 *
 * THE CLASS THIS GUARDS. The hosted windows-latest runner is ~2x slower than
 * ubuntu/macos at Node-heavy work (npm install, repeated Jest runs, the full
 * preflight parity sweep + an isolated `npm install jest` fallback). A single
 * tight job `timeout-minutes` silently cancels ONLY the Windows leg of a matrix
 * mid-step ("The operation was canceled"), while ubuntu/macos finish well under
 * the cap -- a confusing, flaky failure with no clear signal.
 * pre-commit-tooling-check.yml hit exactly this at `timeout-minutes: 10`
 * (windows ~11-12 min; ubuntu/macos ~5.5 min).
 *
 * The fix (and this guard): every job that (a) includes windows-latest in its OS
 * matrix and (b) runs `npm run preflight:pre-push` must
 *   1. set job `timeout-minutes` >= MIN_PREFLIGHT_JOB_TIMEOUT, sized for the
 *      slowest OS with headroom; and
 *   2. give each preflight step its own `timeout-minutes` (a true subset of the
 *      job budget), so a genuine wedge is reported as a clear "step timed out"
 *      rather than an opaque job-level cancel.
 *
 * Lighter windows matrix jobs (ones that only run `test:scripts`, npm meta
 * validation, etc.) are intentionally NOT covered -- they finish comfortably and
 * a tighter timeout is appropriate. The guard keys on the `preflight:pre-push`
 * marker so it tracks the genuinely-heavy jobs and cannot rot as workflows move.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const yaml = require("yaml");

const WORKFLOWS_DIR = path.resolve(__dirname, "..", "..", ".github", "workflows");
const MIN_PREFLIGHT_JOB_TIMEOUT = 20;
// Load-bearing marker: the guard keys on the `npm run preflight:pre-push`
// invocation to find the genuinely-heavy jobs. If the preflight is ever invoked
// a different way (renamed script, composite action), update this marker. The
// npm-script chain itself is separately pinned by preflight-pre-push coverage.
const PREFLIGHT_MARKER = "preflight:pre-push";
// Match any pinned Windows runner (windows-latest, windows-2022, windows-2025,
// ...), not just the moving `-latest` tag, so pinning the runner cannot quietly
// drop a heavy job out of this policy.
const WINDOWS_RUNNER_REGEX = /^windows-/i;

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function matrixOsList(job) {
  const os = job && job.strategy && job.strategy.matrix && job.strategy.matrix.os;
  return asArray(os).map(String);
}

function stepRun(step) {
  return step && typeof step.run === "string" ? step.run : "";
}

/**
 * Discover every job across .github/workflows that has windows-latest in its OS
 * matrix AND runs `preflight:pre-push` in at least one step.
 * @returns {Array<{id:string,file:string,jobId:string,jobTimeout:*,preflightSteps:object[]}>}
 */
function discoverPreflightWindowsJobs() {
  const found = [];
  for (const file of fs.readdirSync(WORKFLOWS_DIR).sort()) {
    if (!/\.ya?ml$/.test(file)) {
      continue;
    }
    let doc;
    try {
      doc = yaml.parse(fs.readFileSync(path.join(WORKFLOWS_DIR, file), "utf8"));
    } catch {
      // A non-parseable workflow is another validator's concern.
      continue;
    }
    const jobs = (doc && doc.jobs) || {};
    for (const jobId of Object.keys(jobs)) {
      const job = jobs[jobId];
      if (!job || typeof job !== "object") {
        continue;
      }
      if (!matrixOsList(job).some((os) => WINDOWS_RUNNER_REGEX.test(os))) {
        continue;
      }
      const preflightSteps = asArray(job.steps).filter((step) =>
        stepRun(step).includes(PREFLIGHT_MARKER)
      );
      if (preflightSteps.length === 0) {
        continue;
      }
      found.push({
        id: `${file}:${jobId}`,
        file,
        jobId,
        jobTimeout: job["timeout-minutes"],
        preflightSteps
      });
    }
  }
  return found;
}

describe("cross-platform preflight job timeout policy", () => {
  const jobs = discoverPreflightWindowsJobs();

  test("the known windows + preflight jobs are discovered (guard is not vacuous)", () => {
    expect(jobs.length).toBeGreaterThanOrEqual(3);
    expect(jobs.map((job) => job.file)).toEqual(
      expect.arrayContaining([
        "pre-commit-tooling-check.yml",
        "cross-platform-preflight.yml",
        "script-tests.yml"
      ])
    );
  });

  test.each(jobs.map((job) => [job.id, job]))(
    `%s: job timeout-minutes >= ${MIN_PREFLIGHT_JOB_TIMEOUT} (windows is ~2x slower)`,
    (_id, job) => {
      const timeout = Number(job.jobTimeout);
      expect(Number.isFinite(timeout)).toBe(true);
      expect(timeout).toBeGreaterThanOrEqual(MIN_PREFLIGHT_JOB_TIMEOUT);
    }
  );

  test.each(jobs.map((job) => [job.id, job]))(
    "%s: each preflight:pre-push step has its own timeout-minutes (clear wedge signal)",
    (_id, job) => {
      for (const step of job.preflightSteps) {
        const stepTimeout = Number(step["timeout-minutes"]);
        expect(Number.isFinite(stepTimeout)).toBe(true);
        expect(stepTimeout).toBeGreaterThan(0);
        // A true subset of the job budget so the per-step timeout fires FIRST on
        // a wedge (clear "step timed out") before the job-level cancel.
        expect(stepTimeout).toBeLessThan(Number(job.jobTimeout));
      }
    }
  );
});
