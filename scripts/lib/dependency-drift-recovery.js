"use strict";

/**
 * dependency-drift-recovery.js
 *
 * Zero-touch recovery for the dependency-version-drift class detected by
 * dependency-version-parity.js. This is the piece the existing `npm ci`
 * integrity recovery structurally CANNOT provide: when package.json (the
 * committed source of truth) has advanced past the local, GITIGNORED
 * lockfile, `npm ci` faithfully reinstalls the stale lockfile and re-cements
 * the wrong version. The correct reconcile is `npm install`, which rewrites
 * the lockfile AND node_modules to satisfy package.json -- exactly what a
 * fresh CI checkout (with no lockfile) does.
 *
 * This runs FIRST in scripts/repair-node-tooling.js -- BEFORE the npm-ci
 * integrity gate -- so by the time `npm ci` could run, the lockfile already
 * agrees with the manifest and there is nothing stale to re-cement.
 *
 * Hot-path cost: on the happy path (no drift) this performs a handful of
 * synchronous JSON reads and returns immediately -- no lock, no spawn. The
 * `npm install` only runs when a real drift is detected, which happens once
 * after a dependency pin changes; subsequent runs see parity and are free.
 *
 * Cross-platform: Node + the platform-aware spawn helper only. No shell
 * syntax, no devcontainer assumptions.
 */

const fs = require("fs");
const path = require("path");
const { isTruthyEnv } = require("./jest-error-decoder");
const { spawnPlatformCommandSync, normalizeNodeColorEnv } = require("./shell-command");
const { runWithRepairLock, REPAIR_LOCK_NAME } = require("./integrity-gate-with-recovery");
const { probeDependencyVersionParity, formatDriftLines } = require("./dependency-version-parity");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

// Reconcile uses the SAME node_modules repair lock as the npm-ci integrity
// recovery so the two npm mutators never overlap (both touch node_modules).
// Imported (not mirrored) from integrity-gate-with-recovery so a rename stays
// compiler-/runtime-coupled rather than relying on a string-grep test.
const NODE_MODULES_REPAIR_LOCK_NAME = REPAIR_LOCK_NAME;

/**
 * Reconcile node_modules + the local lockfile to package.json when (and only
 * when) a version drift is detected.
 *
 * @param {object} [options]
 * @param {object} [options.env] Process env. Honors DXMSG_HOOK_NO_AUTOREPAIR.
 * @param {string} [options.repoRoot] Absolute repository root.
 * @param {Function} [options.probeFn] Drift probe (defaults to the real one).
 * @param {Function} [options.spawnFn] Platform spawn (defaults to
 *   spawnPlatformCommandSync).
 * @param {Function} [options.runWithRepairLockFn] Cross-process lock wrapper.
 * @param {Function} [options.removeDirSyncFn] Recursive directory remover used
 *   by the escalation pass (defaults to fs.rmSync recursive+force).
 * @param {Function} [options.warnFn] Logging sink (defaults to console.warn).
 * @returns {{ok: boolean, recovered: boolean, skipped: boolean, reason: string|null, drifted: Array<object>}}
 */
function repairDependencyDrift(options = {}) {
  const {
    env = process.env,
    repoRoot = REPO_ROOT,
    probeFn = probeDependencyVersionParity,
    spawnFn = spawnPlatformCommandSync,
    runWithRepairLockFn = runWithRepairLock,
    removeDirSyncFn = (dir) => fs.rmSync(dir, { recursive: true, force: true }),
    warnFn = console.warn
  } = options;

  let probe;
  try {
    probe = probeFn({ repoRoot });
  } catch (error) {
    // A probe failure must never abort the bootstrap -- it is strictly worse
    // to crash the hook than to defer the (separately-detected) drift to the
    // downstream validator. Warn and continue.
    const detail = error && error.message ? error.message : String(error);
    warnFn(`WARNING: dependency-version-parity probe threw (best-effort, ignored): ${detail}`);
    return { ok: true, recovered: false, skipped: true, reason: "probe-threw", drifted: [] };
  }

  if (probe.ok) {
    return { ok: true, recovered: false, skipped: false, reason: null, drifted: [] };
  }

  for (const line of formatDriftLines(probe)) {
    warnFn(`WARNING: dependency version drift: ${line}`);
  }

  if (isTruthyEnv(env.DXMSG_HOOK_NO_AUTOREPAIR)) {
    warnFn(
      "WARNING: DXMSG_HOOK_NO_AUTOREPAIR=1 set; skipping `npm install` dependency reconcile. " +
        "Run `npm install` to align node_modules + the local lockfile with package.json."
    );
    return {
      ok: false,
      recovered: false,
      skipped: true,
      reason: "DXMSG_HOOK_NO_AUTOREPAIR=1 set",
      drifted: probe.drifted
    };
  }

  warnFn(
    "WARNING: reconciling dependencies to package.json via `npm install` (the lockfile is " +
      "gitignored and may be stale; `npm ci` cannot fix a manifest change)..."
  );

  const runInstall = () =>
    spawnFn("npm", ["install", "--no-audit", "--no-fund"], {
      cwd: repoRoot,
      stdio: "inherit",
      // Match the repo's other npm spawns (run-managed-jest runCommand):
      // normalize NO_COLOR/FORCE_COLOR so Node does not emit a process warning.
      env: normalizeNodeColorEnv(env)
    });

  // Read-only re-probe used between install passes; a throw degrades to
  // null (treated as "unknown -> escalate"), never aborts.
  const safeProbe = () => {
    try {
      return probeFn({ repoRoot });
    } catch (error) {
      const detail = error && error.message ? error.message : String(error);
      warnFn(`WARNING: post-install dependency-parity re-probe threw (ignored): ${detail}`);
      return null;
    }
  };

  // The whole reconcile runs inside ONE lock acquisition so the install ->
  // re-probe -> escalation sequence is single-writer across hook processes.
  const outcome = runWithRepairLockFn(
    repoRoot,
    () => {
      // Pass 1: plain `npm install` reconciles the common stale case.
      let install = runInstall();
      if (!install || install.status !== 0) {
        return { installOk: false, finalProbe: null };
      }
      const after = safeProbe();
      if (after && after.ok) {
        return { installOk: true, finalProbe: after };
      }

      // Pass 2 (escalation): `npm install` reported success but parity is
      // still off (e.g. a partial/wedged install npm considers "up to
      // date"). Remove the drifted package directories so npm is forced to
      // re-extract them, then reinstall. Mirrors attemptNpmCiRecovery's
      // rm-rf escalation, but scoped to the drifted packages only. A
      // `lockfile-stale` entry means the INSTALL is already correct (only the
      // lockfile lagged), so removing its node_modules dir would be pure
      // waste -- the reinstall below rewrites the lockfile regardless. Skip
      // those from the removal set.
      const driftSource = after && Array.isArray(after.drifted) ? after.drifted : probe.drifted;
      warnFn(
        "WARNING: drift persisted after `npm install`; removing drifted package directories " +
          "and reinstalling..."
      );
      for (const entry of driftSource) {
        if (!entry || typeof entry.name !== "string" || entry.reason === "lockfile-stale") {
          continue;
        }
        const dir = path.join(repoRoot, "node_modules", ...entry.name.split("/"));
        try {
          removeDirSyncFn(dir);
        } catch (error) {
          const detail = error && error.message ? error.message : String(error);
          warnFn(`WARNING: could not remove ${entry.name} for reinstall (${detail}); continuing.`);
        }
      }
      install = runInstall();
      if (!install || install.status !== 0) {
        return { installOk: false, finalProbe: null };
      }
      return { installOk: true, finalProbe: safeProbe() };
    },
    { warnFn, lockName: NODE_MODULES_REPAIR_LOCK_NAME }
  );

  if (!outcome || outcome.lockFailed) {
    warnFn("WARNING: could not acquire repair lock for dependency reconcile; deferring.");
    return {
      ok: false,
      recovered: false,
      skipped: false,
      reason: "repair lock unavailable",
      drifted: probe.drifted
    };
  }
  if (!outcome.installOk) {
    warnFn("WARNING: `npm install` dependency reconcile failed; deferring.");
    return {
      ok: false,
      recovered: false,
      skipped: false,
      reason: "npm install failed",
      drifted: probe.drifted
    };
  }

  // finalProbe null means the post-install re-probe threw; treat as recovered
  // (best-effort) since the authoritative gate is validate-node-tooling + the
  // jest suite, which run after this repair.
  if (!outcome.finalProbe || outcome.finalProbe.ok) {
    return { ok: true, recovered: true, skipped: false, reason: null, drifted: [] };
  }

  warnFn("WARNING: dependency version drift persisted after `npm install` + escalation.");
  return {
    ok: false,
    recovered: false,
    skipped: false,
    reason: "drift persisted after npm install",
    drifted: outcome.finalProbe.drifted
  };
}

module.exports = {
  REPO_ROOT,
  NODE_MODULES_REPAIR_LOCK_NAME,
  repairDependencyDrift
};
