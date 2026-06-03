#!/usr/bin/env node
"use strict";

/**
 * regenerable-cache-registry.js
 *
 * The failure-class -> automated-repair REGISTRY that generalizes the
 * "corrupt REGENERABLE cache hard-gated a push with manual-only recourse"
 * category so it cannot reappear as a one-off (see the memory note
 * regenerable-artifact-hard-gate / eol-fixer-checker-divergence).
 *
 * A "regenerable cache" is a derived artifact that lives OUTSIDE the worktree
 * (typically under os.tmpdir()) and that the tooling rebuilds on demand. When
 * such an artifact is corrupt/partial the correct response is ALWAYS to purge
 * it and let the next run rebuild it -- never to hard-FAIL with a manual rm
 * one-liner. This module wires that automated remediation into the shared
 * bootstrap (repair-node-tooling) and the agentic preflight (preflight.js
 * runRecovery), so it runs BEFORE the read-only doctor ever sees the cache and
 * BEFORE any native git hook fires.
 *
 * Design contracts:
 *   - PURE NODE. No spawn, no process.platform branch, no devcontainer path.
 *     fs + module.createRequire only. Cross-platform native by construction.
 *   - CHEAP ON THE HAPPY PATH; THE LOCK GUARDS MUTATION ONLY. Two happy shapes:
 *     (a) cache root ABSENT -> the healer returns immediately: no readdir, no
 *     resolve, NO lock. (b) cache root PRESENT-but-healthy (the common case on a
 *     machine that already has a fallback cache) -> a lock-FREE readdir + one
 *     resolve per install dir, then return: still NO lock. The cross-process
 *     lock exists ONLY to make the per-dir rm single-writer against a concurrent
 *     rebuild, so it is acquired ONLY once detection has found something to
 *     purge -- a healthy push never pays the lock acquire (nor risks the
 *     sub-second acquire-timeout stall under contention). The teardown + lock +
 *     EPERM/EBUSY retry sleeps are ALL paid only on the rare corrupt/stray
 *     branch, and the retry sleep is itself capped
 *     (REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS); no shape exceeds
 *     acquire-timeout + purge-budget.
 *   - SCOPE CONVERGENCE. The isolated-Jest healer iterates EVERY install dir
 *     under the cache root -- the SAME set scripts/doctor.js
 *     checkIsolatedJestCache walks -- and purges every dir the RUNNER
 *     (run-managed-jest's resolveIsolatedJestRunnerPath) deems unusable. This
 *     is the load-bearing closure contract that kills the fixer/checker
 *     divergence category: the healer clears every cache that would actually
 *     break a managed-Jest run. The doctor's exports-based resolve probe is
 *     STRICTER than the runner's usability predicate (the runner falls back to
 *     the legacy build/runner.js path; the doctor does not), so the doctor may
 *     emit a WARN on a state the healer leaves intact ONLY when that state is
 *     still fully usable by run-managed-jest -- a non-blocking, informational
 *     WARN, never a dead-end. The healer never targets only the pinned spec.
 *   - PATH-GUARDED DELETION. Every per-dir rm routes through the existing
 *     strict-descendant-of-cache-root guard in
 *     scripts/run-managed-jest.js attemptIsolatedCacheReset (which refuses to
 *     delete "..", ".", or any non-descendant). No new unguarded rmSync. The
 *     ONE non-descendant rm -- purging a stray FILE sitting AT the cache root
 *     (an ENOTDIR shape that the strict-descendant guard correctly refuses
 *     because path.relative(root,root) === "") -- is gated by an explicit
 *     strict-equality check against the KNOWN cache root (the production
 *     ISOLATED_JEST_CACHE_ROOT or the injected sandbox cacheRoot), never an
 *     arbitrary path.
 *   - BOUNDED RETRY. Windows %TEMP% / antivirus / Disk-Cleanup can hold a
 *     transient handle mid-write; the per-dir purge retries on EPERM/EBUSY
 *     with a bounded backoff, with ZERO sleeps on success or ENOENT.
 *   - SEPARATE LOCK, SUB-SECOND BOUND. The heal runs under runWithRepairLock
 *     with a DISTINCT lock name so it neither serializes against node_modules
 *     npm-ci recovery nor lets one stuck domain block the other, AND with a
 *     SHORT bounded acquire timeout (REGENERABLE_CACHE_HEAL_LOCK_TIMEOUT_MS)
 *     instead of the inherited 120s default: the heal is best-effort, so on
 *     a contended heal-lock it gives up fast and lets the other concurrent
 *     heal (or the reactive run-managed-jest tier) clear the cache.
 *   - TWO-PART WALL-TIME BOUND (lock-acquire AND in-lock purge). The acquire
 *     timeout bounds ONLY lock acquisition. The IN-LOCK per-dir purge retry
 *     (EPERM/EBUSY backoff) is bounded SEPARATELY by a cumulative purge-sleep
 *     budget (REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS) threaded as a single
 *     budgeted sleep shared across EVERY per-dir purge AND the stray-file
 *     branch in one heal pass: once the cumulative slept time would exceed the
 *     budget, further retry sleeps become no-ops, so N corrupt dirs under a
 *     persistent Windows %TEMP%/antivirus lock can NEVER stack into N*backoff
 *     seconds of synchronous Atomics.wait inside the held lock. The heal-
 *     specific backoff is also DISTINCT from (and tighter than) the npm-ci
 *     [750,2000] so even a single locked dir's full retry stays small. Worst
 *     case = acquire timeout + purge budget, kept inside the <1s git-hook
 *     budget; the reactive run-managed-jest tier remains the backstop for the
 *     rare dir abandoned when the budget is hit.
 *
 * Public surface:
 *   - REGENERABLE_CACHE_REPAIRS: frozen registry array.
 *   - healRegenerableCaches(deps): orchestrator over the registry.
 *   - healIsolatedJestCache(deps): the first concrete healer.
 *   - REGENERABLE_CACHE_HEAL_LOCK_NAME: the distinct lock name.
 *   - REGENERABLE_CACHE_HEAL_LOCK_TIMEOUT_MS: the sub-second acquire bound.
 *   - REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS: the cumulative in-lock retry-sleep
 *     bound (shared across all per-dir purges in one heal pass).
 */

const fs = require("fs");
const path = require("path");
const { createRequire } = require("module");

const {
  ISOLATED_JEST_CACHE_ROOT,
  REPO_ROOT,
  removeDirIfStrictDescendant,
  resolveIsolatedJestRunnerPath,
  sleepSync
} = require("../run-managed-jest");
const { runWithRepairLock } = require("./integrity-gate-with-recovery");
const { isTruthyEnv } = require("./jest-error-decoder");
const { normalizeForPathComparison, toPosixPath } = require("./path-classifier");

// Distinct from REPAIR_LOCK_NAME (node_modules npm-ci recovery) so a stuck
// isolated-cache rebuild cannot block npm-ci recovery and vice-versa.
const REGENERABLE_CACHE_HEAL_LOCK_NAME = "dxmsg-regenerable-cache-heal.lock";

// SUB-SECOND HEAL LOCK TIMEOUT. The heal is BEST-EFFORT: on contention,
// skipping it (lockFailed) is safe -- the OTHER concurrent heal (or the
// reactive run-managed-jest tier) clears the corrupt cache. So we must NOT
// inherit acquireRepairLock's DEFAULT_REPAIR_LOCK_TIMEOUT_MS (120000ms): a
// 120s block to win a lock for a ~ms fs purge would blow the <1s git-hook
// budget on the rare cache-PRESENT-and-contended path. The critical section
// (readdir + resolve + rm) is sub-millisecond, so 250ms is far more than a
// genuine concurrent heal needs to finish; a clearly-wedged peer is abandoned
// fast. Kept well under half the <1s budget so even acquire + the in-lock
// purge budget below stays inside the hook target.
const REGENERABLE_CACHE_HEAL_LOCK_TIMEOUT_MS = 250;
const REGENERABLE_CACHE_HEAL_LOCK_RETRY_DELAY_MS = 50;

// CUMULATIVE IN-LOCK PURGE-SLEEP BUDGET. The acquire timeout above bounds ONLY
// lock acquisition; this bounds the TOTAL retry-sleep paid INSIDE the held lock
// across EVERY per-dir purge AND the stray-file branch in one heal pass. Without
// it, a persistent EPERM/EBUSY (a real Windows %TEMP%/antivirus/Disk-Cleanup
// handle) on each of N corrupt dirs would stack into N * sum(retryDelaysMs) of
// synchronous Atomics.wait while holding the lock. A single shared budgeted
// sleep (makeBudgetedSleepFn) caps that: once the cumulative slept time would
// exceed the budget, further retry sleeps become no-ops (the dir is left for the
// reactive run-managed-jest tier). Sized to allow ONE locked dir its full
// backoff (200 + 400 = 600ms) while capping a multi-dir storm, so worst-case
// heal wall time ~= acquire timeout (250ms) + purge budget (600ms) < 1s.
const REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS = 600;

// EPERM/EBUSY retry backoff threaded into the per-dir purge. DISTINCT from (and
// tighter than) attemptNpmCiRecovery's [750, 2000]: npm-ci is a heavyweight,
// once-per-hook recovery where a long backoff is worth it, whereas the
// isolated-cache purge is a ~ms fs op that must stay inside the <1s git-hook
// budget even on a locked-file day. [200, 400] gives a transient lock two
// chances to clear (total 600ms for one dir, matching the purge budget) without
// risking the multi-second blocking the [750, 2000] backoff could incur.
const DEFAULT_HEAL_RETRY_DELAYS_MS = [200, 400];

/**
 * Build a SLEEP function that enforces a cumulative wall-time budget across all
 * the per-dir purge retries (and the stray-file branch) in a single heal pass.
 * Each call sleeps the requested ms via the real sleepFn UNTIL the running total
 * would exceed budgetMs; thereafter every call is a no-op. This converts the
 * per-dir backoff from "unbounded across N dirs" into "bounded in aggregate",
 * so a persistent OS lock on many corrupt dirs can never stack into seconds of
 * in-lock Atomics.wait. The underlying rmSync retries still RUN when the sleep
 * is skipped (they just fail fast against the still-locked file with no delay),
 * leaving the dir for the reactive run-managed-jest tier -- the documented
 * best-effort backstop.
 *
 * @param {Function} sleepFn The real bounded-blocking sleep (sleepSync).
 * @param {number} budgetMs Cumulative sleep budget in ms.
 * @returns {Function} A budgeted sleep(ms) closure.
 */
function makeBudgetedSleepFn(sleepFn, budgetMs) {
  let slept = 0;
  const budget = Number.isFinite(budgetMs) && budgetMs > 0 ? budgetMs : 0;
  return function budgetedSleep(ms) {
    const requested = Number(ms);
    if (!Number.isFinite(requested) || requested <= 0) {
      return;
    }
    if (slept >= budget) {
      return;
    }
    // Clamp the final sleep so we never overshoot the budget by a full delay.
    const remaining = budget - slept;
    const toSleep = Math.min(requested, remaining);
    slept += toSleep;
    sleepFn(toSleep);
  };
}

/**
 * Purge a single corrupt install dir via the shared strict-descendant guard,
 * scoped to the SAME cacheRoot the healer walked (NOT a hardcoded root). The
 * guard refuses any non-descendant (so a poisoned ".."-style dir name is
 * refused, never rms outside the root) AND owns the bounded EPERM/EBUSY retry
 * (transient Windows %TEMP% locks; ZERO sleeps on the happy path / ENOENT). We
 * sniff the refusal warn only to report `refused` vs `could-not-purge` for the
 * caller's warn/ok bit.
 *
 * @returns {{ purged: boolean, refused: boolean }}
 */
function purgeCorruptInstallDir(
  cacheRoot,
  installDir,
  {
    removeDirIfStrictDescendantFn = removeDirIfStrictDescendant,
    rmSyncFn = fs.rmSync,
    sleepFn = sleepSync,
    retryDelaysMs = DEFAULT_HEAL_RETRY_DELAYS_MS,
    warnFn = console.warn
  } = {}
) {
  let refused = false;
  const ok = removeDirIfStrictDescendantFn(cacheRoot, installDir, {
    rmSyncFn,
    sleepFn,
    retryDelaysMs,
    warnFn: (message) => {
      if (typeof message === "string" && message.includes("not a descendant")) {
        refused = true;
      }
      warnFn(message);
    }
  });

  if (ok) {
    return { purged: true, refused: false };
  }
  return { purged: false, refused };
}

/**
 * Purge a stray FILE sitting AT the cache root itself (the ENOTDIR shape: a
 * botched extract, a stray `>` redirect, or another tool wrote a file where the
 * cache DIR belongs). This is genuine regenerable-artifact corruption -- the
 * next prepareIsolatedFallbackJest does mkdir -p the dir and rebuild -- BUT it
 * is a true dead-end without this branch, because both readdir AND
 * prepareIsolatedFallbackJest's mkdirSync(recursive) throw ENOTDIR against a
 * file-root, so the reactive run-managed-jest fallback cannot self-heal it
 * either.
 *
 * The strict-descendant guard CANNOT clear it (path.relative(root, root) === ""
 * -> refused), so we rm it directly -- but ONLY after an identity check that
 * the target IS the known cache root (the production ISOLATED_JEST_CACHE_ROOT
 * or the injected sandbox cacheRoot). This is the sole non-descendant rm in the
 * module and it can never target an arbitrary path: the caller passes exactly
 * the cacheRoot it walked, and we re-confirm the two spellings are the SAME
 * file before deleting. The identity check uses path-classifier's
 * `normalizeForPathComparison` -- the SAME comparator every other path compare
 * in the repo uses -- so it case-folds on win32 and realpath-resolves symlinks/
 * junctions; a differently-cased or symlinked spelling of the same root (e.g.
 * Windows C:\\Temp vs C:\\temp, or a junction) is still recognized as the cache
 * root and purged, instead of hitting a safe-but-surprising refusal that would
 * leave a genuinely-regenerable stray file un-healed on case-insensitive hosts.
 * Reuses the same bounded EPERM/EBUSY retry as the per-dir purge (transient
 * Windows %TEMP% locks); ZERO sleeps on the happy path / ENOENT.
 *
 * @returns {{ purged: boolean }}
 */
function removeFileIfIsKnownCacheRoot(
  cacheRoot,
  strayPath,
  {
    rmSyncFn = fs.rmSync,
    sleepFn = sleepSync,
    retryDelaysMs = DEFAULT_HEAL_RETRY_DELAYS_MS,
    warnFn = console.warn
  } = {}
) {
  // Resolve once for the rm target (no realpath: the stray FILE is what we
  // delete by its handed path), and compare via the shared platform-aware
  // comparator so the identity check cannot diverge from every other path
  // compare in the repo (case-folds on win32, follows symlinks/junctions).
  const resolvedStray = path.resolve(strayPath);
  if (normalizeForPathComparison(cacheRoot) !== normalizeForPathComparison(strayPath)) {
    warnFn(
      `WARNING: Refusing to remove stray isolated managed-Jest cache file; resolved path is not the known cache root ${toPosixPath(path.resolve(cacheRoot))}: ${toPosixPath(resolvedStray)}`
    );
    return { purged: false };
  }

  const delays = Array.isArray(retryDelaysMs) ? retryDelaysMs : [];
  for (let attempt = 0; attempt <= delays.length; attempt += 1) {
    try {
      rmSyncFn(resolvedStray, { recursive: true, force: true });
      return { purged: true };
    } catch (error) {
      const code = error && error.code;
      const detail = error && error.message ? error.message : String(error);
      const retryable = code === "EPERM" || code === "EBUSY";
      if (!retryable || attempt >= delays.length) {
        warnFn(
          `WARNING: Failed to remove stray isolated managed-Jest cache file at ${toPosixPath(resolvedStray)}: ${detail}`
        );
        return { purged: false };
      }
      const delayMs = Number(delays[attempt]);
      if (delayMs > 0) {
        warnFn(
          `WARNING: Stray isolated managed-Jest cache file removal hit ${code} at ${toPosixPath(resolvedStray)}; retrying in ${delayMs}ms.`
        );
        sleepFn(delayMs);
      }
    }
  }

  return { purged: false };
}

/**
 * Classify the isolated-cache root WITHOUT mutating anything and WITHOUT taking
 * a lock. This is the lock-free detection pass: the healer only takes the
 * cross-process lock (and only pays its acquire cost / contention risk) when
 * this returns a shape that needs mutation. Returns exactly one of:
 *   { kind: "clean" }                         present, every install dir healthy
 *   { kind: "read-error", error }             readdir host fault (EACCES/EIO)
 *   { kind: "stray-file" }                    the root is a FILE (ENOTDIR)
 *   { kind: "corrupt-dirs", corruptDirNames } >=1 install dir the runner rejects
 *
 * Corruption is decided by resolveRunnerFn -- the SAME resolver run-managed-jest
 * uses to decide whether a cache dir is runnable. A null result means a real
 * managed-Jest run would FAIL on that dir, so the healer clears exactly the set
 * the RUNNER deems unusable (the closure contract). The doctor's exports-only
 * resolve probe is stricter, so it may WARN on a dir this resolver still deems
 * usable; that WARN is non-blocking and the dir is genuinely fine for
 * run-managed-jest, so leaving it intact does not reintroduce a dead-end. A
 * wholly-broken injected resolver that throws is treated as corruption
 * defensively.
 *
 * @returns {{kind: string, corruptDirNames?: string[], error?: Error}}
 */
function scanCacheRoot(
  cacheRoot,
  { readdirSyncFn, existsSyncFn, statSyncFn, createRequireFn, resolveRunnerFn }
) {
  let entries;
  try {
    entries = readdirSyncFn(cacheRoot, { withFileTypes: true });
  } catch (error) {
    // ENOTDIR: the cache ROOT is a stray FILE, not a directory.
    if (error && error.code === "ENOTDIR") {
      return { kind: "stray-file" };
    }
    return { kind: "read-error", error };
  }

  const corruptDirNames = [];
  for (const entry of entries) {
    if (!entry.isDirectory()) {
      continue;
    }
    const installDir = path.join(cacheRoot, entry.name);
    let corrupt;
    try {
      corrupt = !resolveRunnerFn(installDir, { existsSyncFn, statSyncFn, createRequireFn });
    } catch {
      corrupt = true;
    }
    if (corrupt) {
      corruptDirNames.push(entry.name);
    }
  }

  if (corruptDirNames.length === 0) {
    return { kind: "clean" };
  }
  return { kind: "corrupt-dirs", corruptDirNames };
}

/**
 * The FIRST concrete healer: the isolated managed-Jest fallback cache under
 * os.tmpdir()/dxmessaging-managed-jest. Detects corruption LOCK-FREE, and only
 * when there is a corrupt install dir (or a stray-file root) to clear does it
 * take a distinct cross-process lock, RE-scan under the lock (TOCTOU), and purge
 * just the corrupt entries (scope convergence + minimal deletion) with bounded
 * EPERM/EBUSY retry. A present-but-healthy cache pays only a readdir + resolve.
 *
 * @param {object} deps Dependency-injection bag (every external touched is
 *   overridable so the unit + closure tests can drive deterministic fakes and
 *   point at an mkdtemp sandbox cacheRoot, never the real shared tmpdir root).
 * @returns {{ id: string, healed: boolean, ok: boolean, purgedDirs: string[] }}
 */
function healIsolatedJestCache(deps = {}) {
  const {
    cacheRoot = ISOLATED_JEST_CACHE_ROOT,
    repoRoot = REPO_ROOT,
    existsSyncFn = fs.existsSync,
    readdirSyncFn = fs.readdirSync,
    statSyncFn = fs.statSync,
    createRequireFn = createRequire,
    resolveRunnerFn = resolveIsolatedJestRunnerPath,
    removeDirIfStrictDescendantFn = removeDirIfStrictDescendant,
    removeFileIfIsKnownCacheRootFn = removeFileIfIsKnownCacheRoot,
    rmSyncFn = fs.rmSync,
    sleepFn = sleepSync,
    runWithRepairLockFn = runWithRepairLock,
    retryDelaysMs = DEFAULT_HEAL_RETRY_DELAYS_MS,
    purgeBudgetMs = REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS,
    lockName = REGENERABLE_CACHE_HEAL_LOCK_NAME,
    warnFn = console.warn
  } = deps;

  const id = "isolated-managed-jest-cache";

  // HAPPY PATH (a): cache root ABSENT -> no readdir, no resolve, NO LOCK. Sub-ms.
  if (!existsSyncFn(cacheRoot)) {
    return { id, healed: false, ok: true, purgedDirs: [] };
  }

  const scanDeps = { readdirSyncFn, existsSyncFn, statSyncFn, createRequireFn, resolveRunnerFn };

  // LOCK-FREE DETECTION. The cross-process lock exists ONLY to make the per-dir
  // rm single-writer against a concurrent rebuild, so a present-but-healthy
  // cache (nothing to mutate) must NOT pay the lock acquire (nor risk the
  // sub-second acquire-timeout stall under contention). Classify first,
  // lock-free, and only take the lock when there is genuinely something to purge.
  const scan = scanCacheRoot(cacheRoot, scanDeps);

  // HAPPY PATH (b): cache root PRESENT but every install dir is healthy ->
  // a readdir + one resolve per dir, still NO LOCK.
  if (scan.kind === "clean") {
    return { id, healed: false, ok: true, purgedDirs: [] };
  }

  // A readdir read-error is a host fault (EACCES/EIO), NOT a regenerable
  // corruption: nothing is auto-deletable and a lock would not help. Surface it
  // lock-free (the doctor keeps this case at FAIL when the fallback is relevant)
  // without throwing out of the heal.
  if (scan.kind === "read-error") {
    const detail = scan.error && scan.error.message ? scan.error.message : String(scan.error);
    warnFn(
      `WARNING: Could not read isolated managed-Jest cache root ${toPosixPath(cacheRoot)}: ${detail}`
    );
    return { id, healed: false, ok: false, purgedDirs: [] };
  }

  // scan.kind is "stray-file" or "corrupt-dirs": there IS something to mutate, so
  // now (and only now) take the lock. The lock prevents two concurrent hooks on
  // one host from rm-ing a dir mid-rebuild of another, while a SEPARATE lock name
  // keeps this off the npm-ci critical path. Inside the lock we RE-scan (TOCTOU:
  // a concurrent heal / run-managed-jest rebuild may have changed the tree
  // between the lock-free decision and acquiring the lock) and mutate off the
  // in-lock view.
  const locked = runWithRepairLockFn(
    repoRoot,
    () => {
      // ONE budgeted sleep shared across the stray-file branch AND every per-dir
      // purge in THIS heal pass: caps the cumulative in-lock retry-sleep so a
      // persistent OS lock on N corrupt dirs can never stack into N*backoff of
      // synchronous Atomics.wait while we hold the lock. Built INSIDE the lock
      // callback so the budget is fresh per heal pass.
      const budgetedSleepFn = makeBudgetedSleepFn(sleepFn, purgeBudgetMs);

      const inLock = scanCacheRoot(cacheRoot, scanDeps);

      // A concurrent process already healed it between our decision and the lock.
      if (inLock.kind === "clean") {
        return { ok: true, purgedDirs: [] };
      }

      // Race re-surfaced a read-error (EACCES/EIO): not auto-deletable.
      if (inLock.kind === "read-error") {
        const detail =
          inLock.error && inLock.error.message ? inLock.error.message : String(inLock.error);
        warnFn(
          `WARNING: Could not read isolated managed-Jest cache root ${toPosixPath(cacheRoot)}: ${detail}`
        );
        return { ok: false, purgedDirs: [] };
      }

      // ENOTDIR: the cache ROOT is a stray FILE, not a directory (a botched
      // extract, a `>` redirect, or another tool). This IS regenerable
      // corruption -- deleting the file fully restores correctness (the next
      // prepareIsolatedFallbackJest mkdir -p the dir and rebuilds), and it is
      // otherwise a true dead-end (prepareIsolatedFallbackJest's
      // mkdirSync(recursive) ALSO throws ENOTDIR against a file-root). The
      // strict-descendant guard cannot clear it (relative(root,root)===""), so
      // route through the strict-equality-guarded file purge of the KNOWN root
      // only. On success the next run rebuilds the dir.
      if (inLock.kind === "stray-file") {
        const result = removeFileIfIsKnownCacheRootFn(cacheRoot, cacheRoot, {
          rmSyncFn,
          sleepFn: budgetedSleepFn,
          retryDelaysMs,
          warnFn
        });
        if (result.purged) {
          warnFn(
            `WARNING: Auto-cleared stray file at the isolated managed-Jest cache root ${toPosixPath(cacheRoot)} (regenerable; the next managed-Jest run rebuilds the cache directory).`
          );
          return { ok: true, purgedDirs: [cacheRoot] };
        }
        // Could not remove the stray file (e.g. persistent EPERM): surface it but
        // do not throw. The doctor downgrade keeps this WARN, not a gate.
        return { ok: false, purgedDirs: [] };
      }

      // inLock.kind === "corrupt-dirs": purge exactly the dirs the runner deems
      // unusable (scope convergence + minimal deletion).
      const purgedDirs = [];
      let ok = true;
      for (const name of inLock.corruptDirNames) {
        const installDir = path.join(cacheRoot, name);
        const result = purgeCorruptInstallDir(cacheRoot, installDir, {
          removeDirIfStrictDescendantFn,
          rmSyncFn,
          sleepFn: budgetedSleepFn,
          retryDelaysMs,
          warnFn
        });

        if (result.purged) {
          purgedDirs.push(name);
          warnFn(
            `WARNING: Auto-cleared corrupt/partial isolated managed-Jest cache dir ${toPosixPath(installDir)} (regenerable; the next managed-Jest run rebuilds it).`
          );
        } else {
          // Refused (non-descendant) or could-not-purge: not healed; surface but
          // do not fail the bootstrap (the doctor downgrade + reactive
          // run-managed-jest tier remain the backstop).
          ok = false;
        }
      }

      return { ok, purgedDirs };
    },
    {
      lockName,
      warnFn,
      // SUB-SECOND bound (NOT the inherited 120s default): the heal is
      // best-effort, so on contention we give up fast and let the other
      // concurrent heal (or the reactive run-managed-jest tier) clear the
      // cache, keeping the git-hook inside its <1s budget. timeoutMs/
      // retryDelayMs are forwarded through runWithRepairLock's spread to
      // acquireRepairLock.
      timeoutMs: REGENERABLE_CACHE_HEAL_LOCK_TIMEOUT_MS,
      retryDelayMs: REGENERABLE_CACHE_HEAL_LOCK_RETRY_DELAY_MS
    }
  );

  // runWithRepairLock returns { status: 1, lockFailed: true } when it could not
  // acquire the lock; treat that as "not healed, best-effort" rather than a
  // throw (another process likely holds it and is doing the same work).
  if (locked && locked.lockFailed) {
    return { id, healed: false, ok: true, purgedDirs: [] };
  }

  const purgedDirs = (locked && locked.purgedDirs) || [];
  const ok = !(locked && locked.ok === false);
  return { id, healed: purgedDirs.length > 0, ok, purgedDirs };
}

/**
 * The REGENERABLE failure-class -> automated-repair registry. Frozen so a new
 * entry must be an explicit, reviewed code change, and so the completeness
 * guard (regenerable-cache-registry-completeness.test.js) can cross-check it in
 * both directions against the doctor's section names.
 *
 * `doctorSectionName` is the stable registry KEY and MUST equal the matching
 * doctor section's `name` verbatim so the guard can verify
 * what-the-checker-flags == what-the-healer-clears.
 */
const REGENERABLE_CACHE_REPAIRS = Object.freeze([
  Object.freeze({
    id: "isolated-managed-jest-cache",
    doctorSectionName: "isolated managed-Jest cache",
    describe: "isolated managed-Jest fallback cache (os.tmpdir())",
    probeAndHeal: healIsolatedJestCache
  })
]);

/**
 * Orchestrate every registered regenerable-cache healer. Best-effort: a healer
 * that throws is caught and reported as { healed: false, ok: false } so one
 * broken healer can never abort the bootstrap. Near-zero on the happy path
 * because each healer returns immediately when its cache root is absent.
 *
 * @param {object} deps
 * @param {Array} [deps.registry] Override registry (tests).
 * @param {object} [deps.env] Process env (for the opt-out, threaded by callers).
 * @param {Function} [deps.warnFn] Logger.
 * @param {object} [deps.healDeps] Extra deps forwarded to each probeAndHeal
 *   (e.g. a sandbox cacheRoot for the closure test).
 * @returns {{ healed: boolean, perEntry: Array<{id, healed, ok}> }}
 */
function healRegenerableCaches(deps = {}) {
  const {
    registry = REGENERABLE_CACHE_REPAIRS,
    env = process.env,
    warnFn = console.warn,
    healDeps = {}
  } = deps;

  // Operator override symmetry with DXMSG_HOOK_NO_AUTOREPAIR: a dedicated
  // opt-out so a host can disable the regenerable-cache heal independently.
  if (isTruthyEnv(env && env.DXMSG_HOOK_NO_REGENERABLE_HEAL)) {
    warnFn("WARNING: DXMSG_HOOK_NO_REGENERABLE_HEAL=1 set; skipping regenerable-cache auto-heal.");
    return { healed: false, perEntry: [] };
  }

  const perEntry = [];
  let anyHealed = false;

  for (const entry of registry) {
    let result;
    try {
      result = entry.probeAndHeal({ warnFn, ...healDeps });
    } catch (error) {
      const detail = error && error.message ? error.message : String(error);
      warnFn(
        `WARNING: Regenerable-cache healer '${entry.id}' threw (best-effort, ignored): ${detail}`
      );
      result = { id: entry.id, healed: false, ok: false };
    }
    const healed = Boolean(result && result.healed);
    const ok = !(result && result.ok === false);
    if (healed) {
      anyHealed = true;
    }
    perEntry.push({ id: entry.id, healed, ok });
  }

  return { healed: anyHealed, perEntry };
}

module.exports = {
  REGENERABLE_CACHE_HEAL_LOCK_NAME,
  REGENERABLE_CACHE_HEAL_LOCK_TIMEOUT_MS,
  REGENERABLE_CACHE_HEAL_LOCK_RETRY_DELAY_MS,
  REGENERABLE_CACHE_HEAL_PURGE_BUDGET_MS,
  DEFAULT_HEAL_RETRY_DELAYS_MS,
  REGENERABLE_CACHE_REPAIRS,
  healRegenerableCaches,
  healIsolatedJestCache,
  makeBudgetedSleepFn,
  purgeCorruptInstallDir,
  removeFileIfIsKnownCacheRoot
};
