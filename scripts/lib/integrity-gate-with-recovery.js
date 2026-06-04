"use strict";

/**
 * integrity-gate-with-recovery.js
 *
 * Shared "probe node_modules integrity, attempt npm-ci recovery, re-probe"
 * gate. Consumed by every managed-tool wrapper (run-managed-jest,
 * run-managed-prettier, run-managed-cspell) so the auto-repair flow is
 * identical and not duplicated per wrapper.
 *
 * Behavior:
 *   1. Probe in-process via probeIntegrityFn. If ok -> return success.
 *   2. Consult isAutoRepairAllowedFn. If refused -> print banner via the
 *      decoder's PARTIAL_NODE_MODULES_INSTALL entry and return failure
 *      without invoking npm ci.
 *   3. Warn naming the missing files. Call attemptNpmCiRecoveryFn.
 *      If npm ci status non-zero -> print banner, return failure.
 *   4. Re-probe via probeIntegrityInSubprocessFn (defeats parent's stat
 *      cache). If subprocess probe ok -> return success with didRecover.
 *      Otherwise -> print banner, return failure.
 *
 * Opt-outs (the wrapper's caller threads these through):
 *   - DXMSG_HOOK_NO_AUTOREPAIR=1  -> isAutoRepairAllowedFn returns false.
 *   - DXMSG_HOOK_SKIP_INTEGRITY=1 -> the wrapper bypasses the gate entirely;
 *     this function is never called in that mode.
 *   - DXMSG_HOOK_AGGRESSIVE_RECOVERY=1 -> attemptNpmCiRecoveryFn rm-rf's
 *     node_modules before npm ci.
 */

const { isTruthyEnv } = require("./jest-error-decoder");
const {
  findZeroByteNativeBinaries,
  formatIntegrityFailure,
  probeResolverHealth
} = require("./node-modules-integrity");
const { toPosixPath } = require("./path-classifier");

const DEFAULT_REPAIR_LOCK_TIMEOUT_MS = 120000;
const DEFAULT_REPAIR_LOCK_STALE_MS = 600000;
const REPAIR_LOCK_NAME = "dxmsg-node-modules-repair.lock";

/**
 * Decide whether auto-repair (npm ci) is safe to run.
 *
 * Refusal cases are checked in this exact order (any one is enough); the
 * JSDoc bullets MUST mirror the code below so reviewers can diff the policy
 * top-to-bottom against the implementation:
 *   1. DXMSG_HOOK_NO_AUTOREPAIR=1            -> caller asked us not to touch
 *      node_modules. Operator override; cheapest check first.
 *   2. getNpmMajorVersionFn() returns null   -> npm is unavailable or broken;
 *      we have no way to repair.
 *   3. .git/rebase-merge or .git/rebase-apply exists -> we are mid-rebase;
 *      touching node_modules could clobber a partially-resolved state.
 *   4. `git diff --quiet -- package-lock.json` exits non-zero -> the
 *      lockfile has unstaged changes; `npm ci` would refuse anyway, but
 *      surfacing the refusal here gives a clearer error. A null/error
 *      result from the spawn is also treated as refusal (defense in depth).
 *
 * Deliberate non-decision: we do NOT special-case CI environments (CI,
 * GITHUB_ACTIONS, etc.). `npm ci` is the correct repair in CI too because the
 * lockfile is committed and reproducible. Operators who want CI to fail
 * rather than auto-repair set `DXMSG_HOOK_NO_AUTOREPAIR=1` on the runner.
 *
 * @param {object} options
 * @param {object} options.env Process env (process.env or fake).
 * @param {string} options.repoRoot Absolute path to repo root.
 * @param {Function} options.getNpmMajorVersionFn Returns npm major version or null.
 * @param {Function} [options.existsSyncFn] Override fs.existsSync (defaults to fs).
 * @param {Function} [options.spawnPlatformCommandSyncFn] Override
 *   spawnPlatformCommandSync (defaults to scripts/lib/shell-command).
 * @returns {{allowed: boolean, reason: string|null}}
 */
function isAutoRepairAllowed(options) {
  const {
    env,
    repoRoot,
    getNpmMajorVersionFn,
    existsSyncFn = require("fs").existsSync,
    spawnPlatformCommandSyncFn = require("./shell-command").spawnPlatformCommandSync,
    path: pathModule = require("path")
  } = options;

  if (env && isTruthyEnv(env.DXMSG_HOOK_NO_AUTOREPAIR)) {
    return { allowed: false, reason: "DXMSG_HOOK_NO_AUTOREPAIR=1 set" };
  }

  const npmMajor = getNpmMajorVersionFn();
  if (npmMajor === null || typeof npmMajor !== "number") {
    return {
      allowed: false,
      reason: "npm executable unavailable (getNpmMajorVersion returned null)"
    };
  }

  const gitDir = pathModule.join(repoRoot, ".git");
  if (existsSyncFn(pathModule.join(gitDir, "rebase-merge"))) {
    return { allowed: false, reason: "mid-rebase: .git/rebase-merge exists" };
  }
  if (existsSyncFn(pathModule.join(gitDir, "rebase-apply"))) {
    return { allowed: false, reason: "mid-rebase: .git/rebase-apply exists" };
  }

  // Cheap "is package-lock.json dirty?" check. `git diff --quiet -- <path>`
  // exits 0 when there are no unstaged differences, 1 when there are, and
  // 128 when not a git repo. We treat any non-zero exit as "refuse" so the
  // operator's working copy is not silently overwritten by `npm ci`.
  const lockResult = spawnPlatformCommandSyncFn(
    "git",
    ["diff", "--quiet", "--", "package-lock.json"],
    {
      cwd: repoRoot,
      stdio: ["ignore", "pipe", "pipe"],
      encoding: "utf8"
    }
  );

  if (!lockResult || lockResult.status !== 0) {
    return {
      allowed: false,
      reason: "package-lock.json has unstaged changes (git diff --quiet returned non-zero)"
    };
  }

  return { allowed: true, reason: null };
}

/**
 * Module-level cache of the gate's "all probes happy" verdict, keyed by
 * normalized repoRoot. The resolver probe spawns a subprocess; the file
 * probe stats a fixed set of files. In a pre-push session a single hook
 * invocation calls into this gate from each of the managed wrappers
 * (run-managed-jest, run-managed-prettier, run-managed-cspell) AND in some
 * cases from validate-node-tooling — that adds up to four subprocess spawns
 * for the same answer.
 *
 * We cache ONLY the success verdict (`{ ok: true, didRecover: false }`) per
 * repoRoot. Failure verdicts are NOT cached: a failure carries side effects
 * (banner printed, npm ci attempted) that the next caller must observe
 * fresh; caching them would also defeat the post-`npm ci` re-probe.
 *
 * The cache is intentionally scoped to the lifetime of the parent Node
 * process (each hook invocation spawns its own Node, so the cache is
 * naturally per-hook). The exported `__clearIntegrityGateCacheForTests`
 * helper resets the cache between tests so injected fakes are exercised
 * end-to-end on each run.
 */
const INTEGRITY_GATE_CACHE = new Map();

/**
 * Normalize a repoRoot for cache keying. Same approach as path-classifier's
 * normalizeForPathComparison but lighter (no fs touch) — the cache key only
 * needs to be stable for a single Node process where the cwd does not
 * change, so a plain path.resolve + lowercase-on-Windows is sufficient.
 *
 * @param {string} repoRoot Absolute repository root.
 * @returns {string} Cache-stable form.
 */
function cacheKeyForRepoRoot(repoRoot) {
  const path = require("path");
  const resolved = path.resolve(repoRoot);
  return process.platform === "win32" ? resolved.toLowerCase() : resolved;
}

/**
 * Reset the in-process integrity-gate cache. Test-only helper; production
 * code should never need to call this.
 */
function __clearIntegrityGateCacheForTests() {
  INTEGRITY_GATE_CACHE.clear();
}

function sleepSync(ms) {
  if (!Number.isFinite(ms) || ms <= 0) {
    return;
  }
  const buffer = new SharedArrayBuffer(4);
  const view = new Int32Array(buffer);
  Atomics.wait(view, 0, 0, Math.floor(ms));
}

function resolveGitControlDir(
  repoRoot,
  { fsModule = require("fs"), pathModule = require("path") } = {}
) {
  const dotGit = pathModule.join(repoRoot, ".git");
  try {
    const stat = fsModule.statSync(dotGit);
    if (stat && stat.isDirectory()) {
      return dotGit;
    }
  } catch {
    // Fall through to the gitfile form below.
  }

  try {
    const gitFile = fsModule.readFileSync(dotGit, "utf8").trim();
    const match = /^gitdir:\s*(.+)$/i.exec(gitFile);
    if (match) {
      const gitDir = match[1].trim();
      return pathModule.isAbsolute(gitDir) ? gitDir : pathModule.resolve(repoRoot, gitDir);
    }
  } catch {
    // Fall through to .git; mkdir below will surface an actionable error
    // if this repository has an unusual layout we cannot write to.
  }

  return dotGit;
}

function writeRepairLockOwner(lockDir, { writeFileSyncFn, pathModule, nowFn, ownerId }) {
  const owner = {
    ownerId,
    pid: process.pid,
    createdAt: new Date(nowFn()).toISOString(),
    platform: process.platform
  };
  const ownerPath = pathModule.join(lockDir, `owner-${ownerId}.json`);
  try {
    writeFileSyncFn(ownerPath, `${JSON.stringify(owner, null, 2)}\n`, "utf8");
    return ownerPath;
  } catch {
    return null;
  }
}

function isRepairLockStale(lockDir, { statSyncFn, nowFn, staleMs }) {
  try {
    const stat = statSyncFn(lockDir);
    const mtimeMs = typeof stat.mtimeMs === "number" ? stat.mtimeMs : stat.mtime.getTime();
    return nowFn() - mtimeMs > staleMs;
  } catch {
    return false;
  }
}

function acquireRepairLock(repoRoot, options = {}) {
  const fsModule = options.fsModule || require("fs");
  const pathModule = options.pathModule || require("path");
  const mkdirSyncFn = options.mkdirSyncFn || fsModule.mkdirSync.bind(fsModule);
  const rmSyncFn = options.rmSyncFn || fsModule.rmSync.bind(fsModule);
  const rmdirSyncFn = options.rmdirSyncFn || fsModule.rmdirSync.bind(fsModule);
  const statSyncFn = options.statSyncFn || fsModule.statSync.bind(fsModule);
  const readFileSyncFn = options.readFileSyncFn || fsModule.readFileSync.bind(fsModule);
  const writeFileSyncFn = options.writeFileSyncFn || fsModule.writeFileSync.bind(fsModule);
  const warnFn = options.warnFn || console.warn;
  const sleepFn = options.sleepFn || sleepSync;
  const nowFn = options.nowFn || Date.now;
  const timeoutMs = Number.isFinite(options.timeoutMs)
    ? options.timeoutMs
    : DEFAULT_REPAIR_LOCK_TIMEOUT_MS;
  const staleMs = Number.isFinite(options.staleMs) ? options.staleMs : DEFAULT_REPAIR_LOCK_STALE_MS;
  const retryDelayMs = Number.isFinite(options.retryDelayMs) ? options.retryDelayMs : 250;
  // Caller may supply a distinct lock NAME so independent repair domains (e.g.
  // the regenerable-cache heal vs node_modules npm-ci recovery) do not
  // serialize against each other. Defaults to REPAIR_LOCK_NAME so every
  // existing caller (runLockedNpmCiRecovery, repairNodeTooling) is unchanged.
  const lockName =
    typeof options.lockName === "string" && options.lockName ? options.lockName : REPAIR_LOCK_NAME;
  const gitControlDir = resolveGitControlDir(repoRoot, {
    fsModule: {
      statSync: statSyncFn,
      readFileSync: readFileSyncFn
    },
    pathModule
  });
  const lockDir = pathModule.join(gitControlDir, lockName);
  const startedAt = nowFn();
  const ownerId = `${process.pid}-${nowFn()}-${Math.random().toString(36).slice(2)}`;
  let warnedWait = false;

  while (true) {
    try {
      mkdirSyncFn(lockDir, { recursive: false });
      const ownerPath = writeRepairLockOwner(lockDir, {
        writeFileSyncFn,
        pathModule,
        nowFn,
        ownerId
      });
      if (!ownerPath) {
        rmSyncFn(lockDir, { recursive: true, force: true });
        return { acquired: false, lockDir, reason: "failed to write repair lock owner metadata" };
      }
      return {
        acquired: true,
        lockDir,
        release() {
          try {
            rmSyncFn(ownerPath, { force: true });
            rmdirSyncFn(lockDir);
          } catch (error) {
            if (error && (error.code === "ENOENT" || error.code === "ENOTEMPTY")) {
              return;
            }
            const detail = error && error.message ? error.message : String(error);
            warnFn(`WARNING: Failed to release node_modules repair lock: ${detail}`);
          }
        }
      };
    } catch (error) {
      const code = error && error.code;
      if (code !== "EEXIST") {
        const detail = error && error.message ? error.message : String(error);
        return { acquired: false, lockDir, reason: detail };
      }

      if (isRepairLockStale(lockDir, { statSyncFn, nowFn, staleMs })) {
        warnFn(`WARNING: Removing stale node_modules repair lock at ${toPosixPath(lockDir)}.`);
        try {
          rmSyncFn(lockDir, { recursive: true, force: true });
          continue;
        } catch (removeError) {
          const detail =
            removeError && removeError.message ? removeError.message : String(removeError);
          return { acquired: false, lockDir, reason: `stale lock removal failed: ${detail}` };
        }
      }

      if (nowFn() - startedAt >= timeoutMs) {
        return {
          acquired: false,
          lockDir,
          reason: `timed out waiting for node_modules repair lock after ${timeoutMs}ms`
        };
      }

      if (!warnedWait) {
        warnFn("WARNING: Waiting for another hook process to finish npm ci recovery...");
        warnedWait = true;
      }
      sleepFn(retryDelayMs);
    }
  }
}

function runWithRepairLock(repoRoot, callbackFn, options = {}) {
  const warnFn = options.warnFn || console.warn;
  const acquireRepairLockFn = options.acquireRepairLockFn || acquireRepairLock;
  // Spread carries options.lockName (and timeout/stale overrides) through to
  // acquireRepairLock so a caller can target a distinct, non-serializing lock.
  const lockOptions = { ...options, warnFn };
  delete lockOptions.acquireRepairLockFn;

  const lock = acquireRepairLockFn(repoRoot, lockOptions);
  if (!lock || !lock.acquired) {
    const reason = lock && lock.reason ? lock.reason : "no reason";
    // Name the ACTUAL lock so the contention message is accurate for every
    // caller, not just the default node_modules-repair one (the regenerable-
    // cache healer passes a distinct lockName). Falls back to the default
    // name when the caller did not override it.
    const reportedLockName =
      typeof options.lockName === "string" && options.lockName
        ? options.lockName
        : REPAIR_LOCK_NAME;
    warnFn(`WARNING: unable to acquire repair lock '${reportedLockName}' (${reason}).`);
    return { status: 1, error: null, lockFailed: true, reason };
  }

  try {
    return callbackFn();
  } finally {
    if (typeof lock.release === "function") {
      lock.release();
    }
  }
}

function runAugmentedIntegrityProbe({
  repoRoot,
  fileProbeFn,
  findZeroByteNativeBinariesFn,
  probeResolverHealthFn,
  platformFn
}) {
  const initial = fileProbeFn({ repoRoot });

  let zeroByteNative = [];
  try {
    zeroByteNative = findZeroByteNativeBinariesFn({
      repoRoot,
      platform: platformFn()
    });
  } catch {
    // Defensive: the scanner already swallows readdir/stat errors, so
    // this catch is only for a wholly broken injection. Treat as no
    // additional offenders.
    zeroByteNative = [];
  }

  const augmentedMissing = Array.isArray(initial && initial.missing) ? initial.missing.slice() : [];
  for (const relPath of zeroByteNative) {
    augmentedMissing.push({
      tool: "<native-binding>",
      relPath,
      reason: "zero-byte"
    });
  }

  let resolverFailures = [];
  try {
    const resolverResult = probeResolverHealthFn({ repoRoot });
    if (resolverResult && Array.isArray(resolverResult.failures)) {
      resolverFailures = resolverResult.failures;
    }
  } catch {
    resolverFailures = [];
  }
  for (const failure of resolverFailures) {
    augmentedMissing.push({
      tool: "<resolver>",
      relPath: failure.specifier,
      reason: "resolver-throw: " + failure.error
    });
  }

  return {
    ok: !!(initial && initial.ok) && zeroByteNative.length === 0 && resolverFailures.length === 0,
    missing: augmentedMissing
  };
}

/**
 * Run the integrity probe -> auto-repair -> re-probe flow.
 *
 * Caching: a successful verdict ({@link INTEGRITY_GATE_CACHE}) is memoized
 * per-repoRoot for the lifetime of the parent Node process so the resolver
 * subprocess spawn (the most expensive step) does not repeat across the
 * managed-wrapper chain in a single hook. Failures are not cached. See the
 * {@link INTEGRITY_GATE_CACHE} comment for the rationale.
 *
 * @param {object} options
 * @param {string} options.repoRoot Absolute path to repository root.
 * @param {Function} options.probeIntegrityFn Function returning { ok, missing }.
 * @param {Function} options.probeIntegrityInSubprocessFn Function returning
 *   { ok, missing } after re-probe in a child node.
 * @param {Function} options.attemptNpmCiRecoveryFn Function performing npm ci.
 * @param {Function} options.isAutoRepairAllowedFn Function returning
 *   { allowed, reason }.
 * @param {Function} options.printActionableRepairBannerFn Decoder-driven
 *   banner printer.
 * @param {object} options.decoder The jest-error-decoder module
 *   ({ PATTERNS, decodeJestStderr }) used to fetch the
 *   PARTIAL_NODE_MODULES_INSTALL entry.
 * @param {Function} [options.warnFn] Logging sink (defaults to console.warn).
 * @param {Function} [options.findZeroByteNativeBinariesFn] Windows-only
 *   scanner for zero-byte *.node binaries (defaults to the production
 *   implementation in node-modules-integrity).
 * @param {Function} [options.formatIntegrityFailureFn] Formatter for the
 *   single-line failure summary (defaults to the production formatter).
 * @param {Function} [options.probeResolverHealthFn] Resolver-health probe
 *   (defaults to the production implementation in node-modules-integrity).
 *   Augments the file-only probe with a runtime require.resolve check that
 *   catches the Windows `unrs-resolver` native-binding failure mode.
 * @param {Function} [options.platformFn] Returns the current platform string
 *   (defaults to () => process.platform). Injectable for tests because
 *   jest's standard mocking story cannot easily clobber process.platform.
 * @param {object} [options.env] Process env (defaults to process.env). Used
 *   to detect DXMSG_HOOK_NO_AUTOREPAIR for the banner hint.
 * @param {Function} [options.acquireRepairLockFn] Cross-process repair lock
 *   factory. Defaults to an atomic directory lock under .git.
 * @param {boolean} [options.bypassCache] When true, skip the
 *   {@link INTEGRITY_GATE_CACHE} lookup and force a fresh probe. Defaults to
 *   false. Tests inject this to make each call observe injected fakes.
 * @returns {{ok: boolean, didRecover: boolean, reason: string|null, cached?: boolean}}
 */
function runIntegrityGateWithRecovery(options) {
  const {
    repoRoot,
    probeIntegrityFn,
    probeIntegrityInSubprocessFn,
    attemptNpmCiRecoveryFn,
    isAutoRepairAllowedFn,
    printActionableRepairBannerFn,
    decoder,
    warnFn = console.warn,
    findZeroByteNativeBinariesFn = findZeroByteNativeBinaries,
    formatIntegrityFailureFn = formatIntegrityFailure,
    probeResolverHealthFn = probeResolverHealth,
    platformFn = () => process.platform,
    env = process.env,
    acquireRepairLockFn = acquireRepairLock,
    bypassCache = false
  } = options;

  if (typeof repoRoot !== "string" || repoRoot.length === 0) {
    throw new TypeError("runIntegrityGateWithRecovery requires options.repoRoot");
  }
  if (typeof probeIntegrityFn !== "function") {
    throw new TypeError("runIntegrityGateWithRecovery requires options.probeIntegrityFn");
  }

  // Cache fast-path: if a previous invocation in THIS Node process already
  // confirmed the gate is clean for this repoRoot, skip the probe entirely.
  // See the INTEGRITY_GATE_CACHE doc-block for the rationale (managed
  // wrappers chain through the gate up to 4x per hook). Only the success
  // verdict is cached; failures are re-probed.
  const cacheKey = cacheKeyForRepoRoot(repoRoot);
  if (!bypassCache && INTEGRITY_GATE_CACHE.has(cacheKey)) {
    return { ok: true, didRecover: false, reason: null, cached: true };
  }

  // 1. Probe in-process.
  // 1a. On Windows, supplement the standard probe with a scan for
  // zero-byte *.node native binaries -- the canonical AV-mid-write
  // failure mode where the JS probe passes but `require()` of a native
  // module would still crash. On non-Windows, the helper returns []
  // immediately without walking. We merge the offenders into the
  // missing[] list so the same downstream banner formatting applies.
  // 1b. Resolver-health probe. The Windows `unrs-resolver` failure mode
  // leaves jest-circus/build/runner.js present on disk (file probe OK)
  // but throws from `require.resolve('jest-circus/runner')` because the
  // platform-specific native binding (e.g.
  // @unrs/resolver-binding-win32-x64-msvc) is missing/broken. The file
  // probe cannot see this; the resolver probe can. We treat resolver
  // failures as integrity failures and let the same auto-repair path
  // (npm ci) attempt recovery.
  const augmentedResult = runAugmentedIntegrityProbe({
    repoRoot,
    fileProbeFn: probeIntegrityFn,
    findZeroByteNativeBinariesFn,
    probeResolverHealthFn,
    platformFn
  });

  if (augmentedResult.ok) {
    INTEGRITY_GATE_CACHE.set(cacheKey, true);
    return { ok: true, didRecover: false, reason: null };
  }

  warnFn(`WARNING: ${formatIntegrityFailureFn(augmentedResult)}`);

  // 2. Auto-repair allowed?
  const repairDecision = isAutoRepairAllowedFn();
  if (!repairDecision || !repairDecision.allowed) {
    warnFn(
      `WARNING: auto-repair refused (${repairDecision && repairDecision.reason ? repairDecision.reason : "no reason"}); skipping npm ci.`
    );
    printIntegrityGateBanner(printActionableRepairBannerFn, decoder, { env });
    return { ok: false, didRecover: false, reason: repairDecision && repairDecision.reason };
  }

  // 3. Acquire the cross-process repair lock. pre-commit may partition a
  // filename hook into multiple Node processes; this lock makes npm ci
  // repair single-writer across those processes and across host OSes that
  // share the same checkout.
  const lock = acquireRepairLockFn(repoRoot, { warnFn });
  if (!lock || !lock.acquired) {
    warnFn(
      `WARNING: unable to acquire node_modules repair lock (${lock && lock.reason ? lock.reason : "no reason"}); integrity gate failing.`
    );
    printIntegrityGateBanner(printActionableRepairBannerFn, decoder, { env });
    return { ok: false, didRecover: false, reason: "node_modules repair lock unavailable" };
  }

  try {
    const lockedReprobe = runAugmentedIntegrityProbe({
      repoRoot,
      fileProbeFn: probeIntegrityInSubprocessFn,
      findZeroByteNativeBinariesFn,
      probeResolverHealthFn,
      platformFn
    });
    if (lockedReprobe.ok) {
      INTEGRITY_GATE_CACHE.set(cacheKey, true);
      return { ok: true, didRecover: false, reason: null };
    }

    // 4. Run npm ci while holding the lock.
    const recoveryResult = attemptNpmCiRecoveryFn();
    if (!recoveryResult || recoveryResult.status !== 0) {
      warnFn("WARNING: npm ci recovery did not succeed; integrity gate failing.");
      printIntegrityGateBanner(printActionableRepairBannerFn, decoder, { env });
      return { ok: false, didRecover: false, reason: "npm ci recovery failed" };
    }

    // 5. Re-probe after npm ci. Both probes own their own subprocess-
    //    freshness contract: `probeIntegrityInSubprocess` spawns a fresh
    //    Node to defeat the parent's fs.stat cache, and `probeResolverHealth`
    //    spawns its own fresh Node to defeat any cached native-binding load
    //    failure from the parent. Calling them again here is therefore safe
    //    and gives us a clean view of the post-`npm ci` filesystem and the
    //    just-reinstalled native binding. The subprocess-freshness invariant
    //    is owned by those functions; this caller only needs to invoke them.
    const reprobe = runAugmentedIntegrityProbe({
      repoRoot,
      fileProbeFn: probeIntegrityInSubprocessFn,
      findZeroByteNativeBinariesFn,
      probeResolverHealthFn,
      platformFn
    });
    if (reprobe.ok) {
      INTEGRITY_GATE_CACHE.set(cacheKey, true);
      return { ok: true, didRecover: true, reason: null };
    }

    warnFn("WARNING: node_modules integrity probe still failed after automatic npm ci recovery.");
    printIntegrityGateBanner(printActionableRepairBannerFn, decoder, { env });
    return {
      ok: false,
      didRecover: false,
      reason: "subprocess re-probe still failed after npm ci"
    };
  } finally {
    if (typeof lock.release === "function") {
      lock.release();
    }
  }
}

/**
 * Find the PARTIAL_NODE_MODULES_INSTALL decoder entry and pass a synthetic
 * "decoded" object to the banner printer. This is the closed-loop way to
 * reuse the same repair-banner formatter from the jest stderr path without
 * baking a special branch into the decoder.
 *
 * When the operator has set DXMSG_HOOK_NO_AUTOREPAIR=1, append a hint
 * explaining what the env var did (suppressed auto-repair) and how to
 * unset it on POSIX + PowerShell. The hint is conveyed by augmenting the
 * synthetic `rootCauses` and `repairCommands` so it lands in the same
 * banner the operator already sees — no new code path is introduced for
 * the operator to discover.
 *
 * @param {Function} printActionableRepairBannerFn
 * @param {object} decoder Imported jest-error-decoder module.
 * @param {object} [opts]
 * @param {object} [opts.env] Process env (defaults to process.env). Used to
 *   detect DXMSG_HOOK_NO_AUTOREPAIR for the hint augmentation.
 */
function printIntegrityGateBanner(printActionableRepairBannerFn, decoder, opts = {}) {
  if (typeof printActionableRepairBannerFn !== "function" || !decoder) {
    return;
  }
  const patterns = Array.isArray(decoder.PATTERNS) ? decoder.PATTERNS : [];
  const entry = patterns.find((p) => p && p.kind === "PARTIAL_NODE_MODULES_INSTALL");
  if (!entry) {
    return;
  }

  const env = (opts && opts.env) || process.env;
  const optedOut = isTruthyEnv(env && env.DXMSG_HOOK_NO_AUTOREPAIR);

  // Snapshot the decoder entry's immutable arrays so we can append the
  // hint without mutating the frozen PATTERNS table.
  const rootCauses = Array.isArray(entry.rootCauses) ? entry.rootCauses.slice() : [];
  const repairCommands = Array.isArray(entry.repairCommands) ? entry.repairCommands.slice() : [];

  if (optedOut) {
    rootCauses.push("auto-repair disabled by DXMSG_HOOK_NO_AUTOREPAIR=1 (operator override)");
    // The banner numbers repair commands as sequential steps (1., 2., 3.,
    // ...). The two unset commands below are POSIX-or-PowerShell
    // ALTERNATIVES (pick the one matching the operator's shell), not
    // sequential steps. We prefix each with "Either:" so the rendered
    // banner reads correctly even with the leading numeral, and the
    // operator immediately sees they are not meant to be run in series.
    repairCommands.push("Either: unset DXMSG_HOOK_NO_AUTOREPAIR  # POSIX: re-enable auto-repair");
    repairCommands.push(
      "Either: Remove-Item Env:\\DXMSG_HOOK_NO_AUTOREPAIR  # PowerShell: re-enable auto-repair"
    );
  }

  const decoded = {
    kind: entry.kind,
    summary: entry.summary,
    rootCauses,
    repairCommands,
    skillRef: entry.skillRef,
    selfHeal: entry.selfHeal,
    capturedMatch: null
  };
  printActionableRepairBannerFn(decoded);
}

module.exports = {
  REPAIR_LOCK_NAME,
  isAutoRepairAllowed,
  acquireRepairLock,
  runWithRepairLock,
  resolveGitControlDir,
  runIntegrityGateWithRecovery,
  printIntegrityGateBanner,
  __clearIntegrityGateCacheForTests
};
