#!/usr/bin/env node
/**
 * run-managed-jest.js
 *
 * Runs Jest in a robust, non-interactive way for hooks and local automation:
 * 1) Prefer local devDependency (node_modules/jest/bin/jest.js).
 * 2) If local Jest is missing, provision a pinned fallback via npm exec.
 * 3) If npm is too old for npm exec (or unavailable), fall back to npx.
 *
 * Discovery note: stderr-based self-heal and the actionable repair banner are
 * implemented in scripts/lib/jest-error-decoder.js. That module is pure (no
 * I/O), so its `PATTERNS` table is the single source of truth for what
 * failures we recognize and what repair we attempt.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");
const { createRequire } = require("module");
const {
  toShellCommand,
  isShellShimCommand,
  spawnPlatformCommandSync,
  normalizeNodeColorEnv
} = require("./lib/shell-command");
const jestErrorDecoderModule = require("./lib/jest-error-decoder");
const { decodeJestStderr, formatRepairBanner, isTruthyEnv } = jestErrorDecoderModule;
const {
  normalizeForPathComparison,
  isPathInsideDirectory,
  isOutsideRelative,
  classifyCapturedPath,
  PATH_CLASS_REPO,
  PATH_CLASS_ISOLATED,
  PATH_CLASS_UNKNOWN,
  toPosixPath,
  toRepoPosixRelative
} = require("./lib/path-classifier");
const {
  INTEGRITY_TARGETS,
  probeIntegrity,
  probeIntegrityInSubprocess,
  probeResolverHealth
} = require("./lib/node-modules-integrity");
const {
  isAutoRepairAllowed: defaultIsAutoRepairAllowed,
  runIntegrityGateWithRecovery,
  runWithRepairLock
} = require("./lib/integrity-gate-with-recovery");

const REPO_ROOT = path.join(__dirname, "..");
const REPO_NODE_MODULES = path.join(REPO_ROOT, "node_modules");
const PACKAGE_LOCK_PATH = path.join(REPO_ROOT, "package-lock.json");
const LOCAL_JEST_BIN = path.join(REPO_ROOT, "node_modules", "jest", "bin", "jest.js");
const FALLBACK_JEST_SPEC = "jest@30.3.0";
const ISOLATED_JEST_CACHE_ROOT = path.join(os.tmpdir(), "dxmessaging-managed-jest");
const REPO_REQUIRE = createRequire(path.join(REPO_ROOT, "package.json"));

function parseNpmMajorVersion(versionText) {
  if (typeof versionText !== "string") {
    return null;
  }

  const match = /^v?(\d+)/.exec(versionText.trim());
  if (!match) {
    return null;
  }

  return Number(match[1]);
}

function getNpmMajorVersion() {
  const result = spawnPlatformCommandSync("npm", ["--version"], {
    cwd: REPO_ROOT,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });

  if (result.error || result.status !== 0) {
    return null;
  }

  return parseNpmMajorVersion(result.stdout);
}

function runCommand(command, args, spawnOptions = {}) {
  const spawnSyncImpl = isShellShimCommand(command)
    ? spawnPlatformCommandSync
    : childProcess.spawnSync;

  const result = spawnSyncImpl(command, args, {
    cwd: REPO_ROOT,
    stdio: "inherit",
    ...spawnOptions,
    env: normalizeNodeColorEnv(spawnOptions.env || process.env)
  });

  return {
    status: result.status,
    error: result.error || null
  };
}

/**
 * Like runCommand() but captures stderr so the wrapper can decode the failure
 * mode after the child exits. stderr is BATCH-FORWARDED: spawnSync (which we
 * use to keep the rest of this module synchronous) buffers the entire stderr
 * stream until the child exits, and only then writes it through to the
 * parent's stderr. Output appears in one burst at the end rather than as it
 * is produced.
 *
 * All Jest invocations that need stderr decoding (local + isolated) use this
 * capturing wrapper. Batch-forwarding stderr is acceptable because:
 *   (1) successful Jest runs produce minimal stderr,
 *   (2) failed runs need the captured stderr for decoder-driven self-healing
 *       (e.g. the local-tier MISSING_TEST_RUNNER fall-through into the
 *       isolated fallback), and
 *   (3) `spawnSync` cannot truly stream stderr in a synchronous API; the
 *       alternative (async child_process.spawn with a `data` listener
 *       mirroring chunks) would require either a busy-wait loop or a worker
 *       thread to keep the call synchronous, both of which add complexity
 *       for marginal benefit on these low-volume paths.
 *
 * IMPORTANT: `stdio` is intentionally NOT overridable via spawnOptions.
 * Capture mode requires the stderr pipe; if a caller could pass
 * `stdio: "inherit"` we would silently lose decode capability and report an
 * empty `stderr` string, which is worse than the current behavior of "decode
 * is sometimes useful".
 *
 * @param {string} command Executable name.
 * @param {string[]} args Command arguments.
 * @param {object} spawnOptions Additional spawnSync options. `stdio` keys are
 *   ignored to protect capture-mode invariants.
 * @returns {{status: number|null, error: Error|null, stderr: string}}
 */
function runCommandCapturingStderr(command, args, spawnOptions = {}) {
  const spawnSyncImpl = isShellShimCommand(command)
    ? spawnPlatformCommandSync
    : childProcess.spawnSync;

  // Note the order: spawnOptions spread FIRST, then our defaults override.
  // This protects the stdio invariant against caller-supplied keys while
  // still letting callers pass cwd, env, encoding, etc.
  const merged = {
    cwd: REPO_ROOT,
    ...spawnOptions,
    env: normalizeNodeColorEnv(spawnOptions.env || process.env),
    // stdio is NEVER overridable by callers: capture mode requires this
    // exact pipe configuration for the stderr decoder to function.
    stdio: ["inherit", "inherit", "pipe"]
  };

  const result = spawnSyncImpl(command, args, merged);

  let stderrBuffer = result.stderr;
  let stderrText = "";
  if (stderrBuffer) {
    if (Buffer.isBuffer(stderrBuffer)) {
      // Forward captured bytes to the parent's stderr in a single batch.
      // This happens AFTER the child has exited (see JSDoc above); stderr
      // is not streamed as it is produced. Banner output (printed by
      // callers) lands after this burst so ordering is preserved.
      try {
        process.stderr.write(stderrBuffer);
      } catch {
        // Swallow write errors (e.g. EPIPE when stderr was closed)
        // so we always return the captured buffer to the caller.
      }
      stderrText = stderrBuffer.toString("utf8");
    } else if (typeof stderrBuffer === "string") {
      try {
        process.stderr.write(stderrBuffer);
      } catch {
        // Swallow write errors (see above).
      }
      stderrText = stderrBuffer;
    }
  }

  return {
    status: result.status,
    error: result.error || null,
    stderr: stderrText
  };
}

/**
 * Delete `targetDir` only if it is a STRICT DESCENDANT of `cacheRoot`, with a
 * bounded EPERM/EBUSY retry. The single guarded-rm primitive shared by
 * attemptIsolatedCacheReset (per-pinned-spec) AND the regenerable-cache healer
 * (per-dir under an arbitrary -- possibly sandbox -- cache root). Centralizing
 * it guarantees BOTH callers enforce the same descendant guard against the
 * SAME root they intend to operate within (the healer passes the root it
 * walked, not a hardcoded one), so the deletion scope can never drift.
 *
 * Safety: refuses the cache root itself (""), bare ".." / any first-segment
 * ".." parent-traversal, and absolute relatives. A sanitized key like "..foo"
 * resolves to a legitimate descendant and is allowed (segment-boundary
 * semantics). This blocks `sanitizeCacheKey("..")` (which preserves "..") from
 * being weaponized into deleting the cache root's parent (the OS temp dir).
 *
 * Windows hardening: %TEMP% cleaners, antivirus, indexers, and Disk Cleanup can
 * hold a transient handle mid-write (EPERM/EBUSY); the rm retries on those with
 * a bounded backoff (mirroring attemptNpmCiRecovery's [750, 2000]). ENOENT is a
 * no-op via force:true; the happy path (first rm succeeds) pays ZERO sleeps; a
 * non-retryable error fails immediately.
 *
 * @returns {boolean} true on deletion (or no-op), false on refusal/failure.
 */
function removeDirIfStrictDescendant(
  cacheRoot,
  targetDir,
  {
    rmSyncFn = fs.rmSync,
    warnFn = console.warn,
    sleepFn = sleepSync,
    retryDelaysMs = [750, 2000]
  } = {}
) {
  const resolvedRoot = path.resolve(cacheRoot);
  const resolvedTarget = path.resolve(targetDir);
  const relativePath = path.relative(resolvedRoot, resolvedTarget);
  // Strict descendant: reject "" (the cache root itself) AND anything outside
  // it. `isOutsideRelative` (scripts/lib/path-classifier.js) is the shared,
  // cross-drive-safe predicate covering "..", "../...", and the absolute target
  // `path.relative` yields across Windows drives -- a sanitized key like
  // "..foo" resolves UNDER the root, is not a traversal, and is not rejected.
  if (relativePath === "" || isOutsideRelative(relativePath)) {
    warnFn(
      `WARNING: Refusing to reset isolated managed-Jest cache; resolved path is not a descendant of ${toPosixPath(resolvedRoot)}: ${toPosixPath(resolvedTarget)}`
    );
    return false;
  }

  const delays = Array.isArray(retryDelaysMs) ? retryDelaysMs : [];
  // Attempt 0 plus one retry per configured delay.
  for (let attempt = 0; attempt <= delays.length; attempt += 1) {
    try {
      rmSyncFn(resolvedTarget, { recursive: true, force: true });
      return true;
    } catch (error) {
      const code = error && error.code;
      const detail = error && error.message ? error.message : String(error);
      const retryable = code === "EPERM" || code === "EBUSY";
      if (!retryable || attempt >= delays.length) {
        warnFn(
          `WARNING: Failed to reset isolated managed-Jest cache at ${toPosixPath(resolvedTarget)}: ${detail}`
        );
        return false;
      }
      const delayMs = Number(delays[attempt]);
      if (delayMs > 0) {
        warnFn(
          `WARNING: Isolated managed-Jest cache reset hit ${code} at ${toPosixPath(resolvedTarget)}; retrying in ${delayMs}ms.`
        );
        sleepFn(delayMs);
      }
    }
  }

  return false;
}

/**
 * Delete the isolated managed-Jest install directory for the given spec so
 * the next invocation re-bootstraps from scratch. Returns true on success,
 * false on error (errors are logged via warnFn).
 *
 * Scoped to the isolated fallback path only; never touches local node_modules.
 * Delegates to the shared removeDirIfStrictDescendant guard against
 * ISOLATED_JEST_CACHE_ROOT.
 *
 * @param {string} jestSpec Pinned jest spec (e.g. "jest@30.3.0").
 * @param {object} options Dependency injection options.
 * @returns {boolean}
 */
function attemptIsolatedCacheReset(
  jestSpec,
  {
    rmSyncFn = fs.rmSync,
    warnFn = console.warn,
    sleepFn = sleepSync,
    retryDelaysMs = [750, 2000]
  } = {}
) {
  const { installDir } = getIsolatedJestPaths(jestSpec);
  return removeDirIfStrictDescendant(ISOLATED_JEST_CACHE_ROOT, installDir, {
    rmSyncFn,
    warnFn,
    sleepFn,
    retryDelaysMs
  });
}

/**
 * Run `npm ci --no-audit --no-fund` against the repository root in an attempt
 * to repair a partially-installed node_modules. Windows shared-worktree
 * installs can transiently fail with EPERM/EBUSY while antivirus, editors,
 * or a previous Node/npm process still has a handle open; retry in-process
 * and then try one automatic node_modules removal before the final attempt.
 *
 * @param {object} options Dependency injection options.
 * @returns {{status: number|null, error: Error|null}}
 */
function attemptNpmCiRecovery({
  runCommandFn = runCommand,
  rmSyncFn = fs.rmSync,
  existsSyncFn = fs.existsSync,
  envFn = () => process.env,
  warnFn = console.warn,
  sleepFn = sleepSync,
  retryDelaysMs = [750, 2000]
} = {}) {
  const env = (envFn && envFn()) || process.env;
  const aggressive = isTruthyEnv(env.DXMSG_HOOK_AGGRESSIVE_RECOVERY);
  const nodeModulesPath = path.join(REPO_ROOT, "node_modules");
  const recoveryCommand = getNpmRecoveryCommand({ existsSyncFn });

  if (aggressive) {
    warnFn(
      `WARNING: DXMSG_HOOK_AGGRESSIVE_RECOVERY=1 -> removing ${toPosixPath(nodeModulesPath)} before ${recoveryCommand.label}.`
    );
    removeNodeModulesForRecovery(nodeModulesPath, { rmSyncFn, warnFn });
  }

  const runNpmRecovery = (attemptNumber) => {
    const suffix = attemptNumber > 1 ? ` (attempt ${attemptNumber})` : "";
    warnFn(
      `WARNING: Attempting \`${recoveryCommand.label}\` recovery to repair local node_modules${suffix}...`
    );
    return runCommandFn("npm", recoveryCommand.args, {
      cwd: REPO_ROOT
    });
  };

  let result = runNpmRecovery(1);
  if (isSuccessfulCommandResult(result)) {
    return result;
  }

  const delays = Array.isArray(retryDelaysMs) ? retryDelaysMs : [];
  for (let i = 0; i < delays.length; i++) {
    warnNpmCiFailure(result, warnFn, recoveryCommand.label);
    const delayMs = Number(delays[i]);
    if (delayMs > 0) {
      warnFn(`WARNING: Waiting ${delayMs}ms before retrying ${recoveryCommand.label} recovery.`);
      sleepFn(delayMs);
    }
    result = runNpmRecovery(i + 2);
    if (isSuccessfulCommandResult(result)) {
      return result;
    }
  }

  if (!aggressive) {
    warnNpmCiFailure(result, warnFn, recoveryCommand.label);
    warnFn(
      `WARNING: ${recoveryCommand.label} still failed; removing node_modules automatically before final recovery attempt.`
    );
    removeNodeModulesForRecovery(nodeModulesPath, { rmSyncFn, warnFn });
    result = runNpmRecovery(delays.length + 2);
  }

  if (!isSuccessfulCommandResult(result)) {
    warnNpmCiFailure(result, warnFn, recoveryCommand.label);
  }
  return result;
}

function getNpmRecoveryCommand({ existsSyncFn = fs.existsSync } = {}) {
  const hasLockfile =
    existsSyncFn(path.join(REPO_ROOT, "package-lock.json")) ||
    existsSyncFn(path.join(REPO_ROOT, "npm-shrinkwrap.json"));
  if (hasLockfile) {
    return {
      label: "npm ci",
      args: ["ci", "--no-audit", "--no-fund"]
    };
  }

  return {
    label: "npm install",
    args: ["install", "--no-audit", "--no-fund"]
  };
}

function isSuccessfulCommandResult(result) {
  return !!result && result.status === 0;
}

function warnNpmCiFailure(result, warnFn, commandLabel = "npm ci") {
  const detail =
    result && result.error && result.error.message
      ? result.error.message
      : `status=${result ? result.status : "null"}`;
  warnFn(`WARNING: \`${commandLabel}\` recovery did not succeed (${detail}).`);
}

function removeNodeModulesForRecovery(
  nodeModulesPath,
  { rmSyncFn = fs.rmSync, warnFn = console.warn } = {}
) {
  try {
    rmSyncFn(nodeModulesPath, { recursive: true, force: true });
    return true;
  } catch (error) {
    const detail = error && error.message ? error.message : String(error);
    warnFn(`WARNING: node_modules removal failed (${detail}); continuing with npm ci.`);
    return false;
  }
}

function runLockedNpmCiRecovery(
  attemptNpmCiRecoveryFn,
  { repoRoot = REPO_ROOT, runWithRepairLockFn = runWithRepairLock, warnFn = console.warn } = {}
) {
  return runWithRepairLockFn(repoRoot, () => attemptNpmCiRecoveryFn(), { warnFn });
}

function sleepSync(ms) {
  if (!Number.isFinite(ms) || ms <= 0) {
    return;
  }
  const buffer = new SharedArrayBuffer(4);
  const view = new Int32Array(buffer);
  Atomics.wait(view, 0, 0, Math.floor(ms));
}

/**
 * Print the actionable repair banner to stderr. No-op when decoded is null.
 *
 * The banner is preceded by a single blank line so it stands out from the
 * batch-forwarded Jest stderr that `runCommandCapturingStderr` writes
 * immediately before this function is called.
 *
 * @param {object|null} decoded Output of decodeJestStderr().
 * @param {object} [options] Dependency injection options.
 * @param {Function} [options.writeFn] Where to write the banner (defaults to
 *   process.stderr.write bound to stderr).
 * @param {string|null|undefined} [options.envCi] CI env value to pass through
 *   to the formatter. Defaults to process.env.CI. Accepts any truthy/falsy
 *   string per isTruthyEnv semantics.
 * @param {object} [options.env] Full env object to pass to the formatter,
 *   overriding envCi. Tests use this to inject deterministic env.
 * @param {boolean} [options.isTTY] Whether the destination stream is a TTY.
 *   Defaults to process.stderr.isTTY (banner is written to stderr).
 */
function printActionableRepairBanner(
  decoded,
  {
    writeFn = process.stderr.write.bind(process.stderr),
    envCi = process.env.CI,
    env = null,
    isTTY = undefined
  } = {}
) {
  if (!decoded) {
    return;
  }

  const formatterEnv = env || { CI: envCi };
  const formatterOptions = { color: true, env: formatterEnv };
  if (typeof isTTY === "boolean") {
    formatterOptions.isTTY = isTTY;
  }

  const banner = formatRepairBanner(decoded, formatterOptions);
  if (!banner) {
    return;
  }

  // Prefix with a blank line so the banner visually separates from the
  // batch-forwarded Jest stderr that arrived just before.
  const output = `\n${banner}`;
  try {
    writeFn(output);
  } catch {
    // Swallow write errors (e.g. EPIPE) so failure to print the banner
    // never masks the underlying Jest exit code. This is intentional.
  }
}

function tryLoadModule(moduleSpecifier, repoRequire = REPO_REQUIRE) {
  try {
    repoRequire(moduleSpecifier);
    return true;
  } catch {
    return false;
  }
}

function runLocalJest(args, options = {}) {
  const {
    moduleResolver = resolveLocalModule,
    tryLoadModuleFn = tryLoadModule,
    hasCliOptionFn = hasCliOption,
    // Deliberate tradeoff: local Jest uses the stderr-capturing wrapper
    // (not a stdio:"inherit" passthrough) because the MISSING_TEST_RUNNER
    // decode + fall-through into the isolated tier requires the decoder
    // to see the child's stderr text. The cost is that stderr is
    // batch-forwarded after the child exits rather than streamed; see
    // the JSDoc on runCommandCapturingStderr for the full rationale.
    runCommandFn = runCommandCapturingStderr,
    existsSyncFn = fs.existsSync,
    decodeJestStderrFn = decodeJestStderr,
    warnFn = console.warn
  } = options;

  // Validate local jest-circus is healthy as a precondition. We deliberately
  // DO NOT inject `--testRunner` with an absolute path: Jest 27+ defaults to
  // jest-circus and its own internal resolver finds the bundled runner more
  // reliably than we can second-guess from outside (notably on Windows, where
  // jest-config's runner validator has rejected absolute paths that
  // require.resolve + fs.existsSync both report as valid). If the caller
  // explicitly passes `--testRunner`, we forward it unchanged.
  const callerProvidedTestRunner = hasCliOptionFn(args, "--testRunner");

  if (!callerProvidedTestRunner) {
    const resolvedRunnerPath = moduleResolver("jest-circus/runner");
    if (
      !resolvedRunnerPath ||
      !existsSyncFn(resolvedRunnerPath) ||
      !isPathInsideDirectory(resolvedRunnerPath, REPO_NODE_MODULES) ||
      !tryLoadModuleFn("jest-circus/runner")
    ) {
      warnFn(
        "WARNING: Local jest-circus/runner failed load validation; falling back to managed Jest."
      );
      return null;
    }
  }

  const invocationArgs = [LOCAL_JEST_BIN, ...args];
  const result = runCommandFn(process.execPath, invocationArgs);

  // If local Jest emitted MISSING_TEST_RUNNER stderr, the local install is
  // effectively unhealthy: the isolated-fallback tier (which CAN reset its
  // cache) is the correct place to recover. Return null so `runManagedJest`
  // falls through to the isolated tier rather than printing a banner and
  // exiting. We intentionally only fall through for MISSING_TEST_RUNNER;
  // MISSING_LOCAL_JEST is handled in `runManagedJest` because it can be
  // self-healed in-place via `npm ci`.
  if (result && result.status !== 0) {
    const decoded = decodeJestStderrFn(result.stderr);
    if (decoded && decoded.kind === "MISSING_TEST_RUNNER") {
      warnFn(
        "WARNING: Local Jest reported MISSING_TEST_RUNNER; treating local tier as unhealthy and falling through to isolated fallback."
      );
      return null;
    }
  }

  return result;
}

function getPinnedFallbackJestSpec(
  readFileSyncFn = fs.readFileSync,
  fallbackSpec = FALLBACK_JEST_SPEC
) {
  try {
    const packageLock = JSON.parse(readFileSyncFn(PACKAGE_LOCK_PATH, "utf8"));
    const lockedVersion =
      packageLock &&
      packageLock.packages &&
      packageLock.packages["node_modules/jest"] &&
      packageLock.packages["node_modules/jest"].version;
    if (typeof lockedVersion === "string" && /^\d+\.\d+\.\d+$/.test(lockedVersion)) {
      return `jest@${lockedVersion}`;
    }
  } catch {
    // Fall through to static fallback when lockfile is unavailable or malformed.
  }

  return fallbackSpec;
}

function runNpmExecJest(args) {
  const jestSpec = getPinnedFallbackJestSpec();
  return runCommandCapturingStderr("npm", [
    "exec",
    "--yes",
    `--package=${jestSpec}`,
    "--",
    "jest",
    ...args
  ]);
}

function runNpxJest(args) {
  const jestSpec = getPinnedFallbackJestSpec();
  return runCommandCapturingStderr("npx", ["--yes", `--package=${jestSpec}`, "jest", ...args]);
}

function sanitizeCacheKey(value) {
  return String(value).replace(/[^a-zA-Z0-9._-]+/g, "_");
}

function getDefaultIsolatedJestRunnerPath(installDir) {
  return path.join(installDir, "node_modules", "jest-circus", "build", "runner.js");
}

function getIsolatedJestPaths(jestSpec) {
  const cacheKey = sanitizeCacheKey(jestSpec);
  const installDir = path.join(ISOLATED_JEST_CACHE_ROOT, cacheKey);
  return {
    installDir,
    packageJsonPath: path.join(installDir, "package.json"),
    jestBinPath: path.join(installDir, "node_modules", "jest", "bin", "jest.js"),
    jestRunnerPath: getDefaultIsolatedJestRunnerPath(installDir)
  };
}

/**
 * A runner path is USABLE only if it exists AND is non-empty. A zero-byte
 * runner.js is the antivirus/Disk-Cleanup mid-write failure class that
 * node-modules-integrity.js already special-cases for the repo tree (empty-file
 * detection): the file is present so existsSync passes, but `require()` would
 * load an empty module and Jest would crash with an un-decoded error. Treating
 * size 0 as a miss keeps the isolated-cache corruption predicate identical to
 * the repo-tree one, so a zero-byte runner is proactively rebuilt (the resolver
 * returns null -> the managed-Jest fallback re-bootstraps) and the
 * regenerable-cache healer purges it instead of surfacing later as a different,
 * un-decoded Jest crash. A statSync throw (raced-away file) is also a miss.
 */
function isUsableRunnerFile(runnerPath, existsSyncFn, statSyncFn) {
  if (!existsSyncFn(runnerPath)) {
    return false;
  }
  try {
    return statSyncFn(runnerPath).size > 0;
  } catch {
    return false;
  }
}

function resolveIsolatedJestRunnerPath(
  installDir,
  { existsSyncFn = fs.existsSync, statSyncFn = fs.statSync, createRequireFn = createRequire } = {}
) {
  const packageJsonPath = path.join(installDir, "package.json");
  const defaultRunnerPath = getDefaultIsolatedJestRunnerPath(installDir);

  try {
    if (existsSyncFn(packageJsonPath)) {
      const isolatedRequire = createRequireFn(packageJsonPath);
      const resolvedRunnerPath = isolatedRequire.resolve("jest-circus/runner");
      if (isUsableRunnerFile(resolvedRunnerPath, existsSyncFn, statSyncFn)) {
        return resolvedRunnerPath;
      }
    }
  } catch {
    // Fall back to the legacy internal path for compatibility with older layouts.
  }

  if (isUsableRunnerFile(defaultRunnerPath, existsSyncFn, statSyncFn)) {
    return defaultRunnerPath;
  }

  return null;
}

function hasCliOption(args, optionName) {
  const normalizedOption = optionName.startsWith("--") ? optionName : `--${optionName}`;

  return args.some((arg) => arg === normalizedOption || arg.startsWith(`${normalizedOption}=`));
}

function buildNodePathEnv(isolatedNodeModulesPath, baseEnv = process.env) {
  const existingNodePath = baseEnv.NODE_PATH;
  const nextNodePath = existingNodePath
    ? `${isolatedNodeModulesPath}${path.delimiter}${existingNodePath}`
    : isolatedNodeModulesPath;

  return {
    ...normalizeNodeColorEnv(baseEnv),
    NODE_PATH: nextNodePath
  };
}

function writeIsolatedJestCacheManifest(packageJsonPath, writeFileSyncFn = fs.writeFileSync) {
  const manifest = {
    name: "dxmessaging-managed-jest-fallback-cache",
    private: true
  };
  writeFileSyncFn(packageJsonPath, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
}

function prepareIsolatedFallbackJest(
  jestSpec = getPinnedFallbackJestSpec(),
  {
    existsSyncFn = fs.existsSync,
    statSyncFn = fs.statSync,
    mkdirSyncFn = fs.mkdirSync,
    writeFileSyncFn = fs.writeFileSync,
    rmSyncFn = fs.rmSync,
    runCommandFn = runCommand,
    resolveIsolatedJestRunnerPathFn = resolveIsolatedJestRunnerPath,
    warnFn = console.warn
  } = {}
) {
  const {
    installDir,
    packageJsonPath,
    jestBinPath,
    jestRunnerPath: defaultJestRunnerPath
  } = getIsolatedJestPaths(jestSpec);

  const hasCachedJestBin = existsSyncFn(jestBinPath);
  // Thread statSyncFn so the runner's zero-byte (empty-file) check uses the same
  // injected stat as the rest of the builder; in production this is fs.statSync.
  const cachedJestRunnerPath = resolveIsolatedJestRunnerPathFn(installDir, {
    existsSyncFn,
    statSyncFn
  });
  const hasCachedJestRunner = typeof cachedJestRunnerPath === "string";

  if (hasCachedJestBin && hasCachedJestRunner) {
    return {
      jestBinPath,
      jestRunnerPath: cachedJestRunnerPath,
      cacheHit: true
    };
  }

  if (hasCachedJestBin && !hasCachedJestRunner) {
    warnFn(
      `⚠️ Isolated fallback cache is missing Jest runner; reinstalling fallback: ${toPosixPath(defaultJestRunnerPath)}`
    );
  }

  // Idempotency fix (Step 3): when the install dir already exists but the
  // cached runner is missing (or the bin is missing), `npm install` against
  // a half-populated tree can short-circuit with "up to date" without ever
  // re-extracting. Tear the dir down first and re-create from scratch.
  //
  // Safety: mirror the path-traversal validation from
  // `attemptIsolatedCacheReset`. We refuse to rm anything that does not
  // resolve to a strict descendant of ISOLATED_JEST_CACHE_ROOT, so a
  // poisoned `jestSpec` cannot weaponize this code path into deleting an
  // unrelated directory.
  if (existsSyncFn(installDir) && !(hasCachedJestBin && hasCachedJestRunner)) {
    const cacheRoot = path.resolve(ISOLATED_JEST_CACHE_ROOT);
    const resolvedInstallDir = path.resolve(installDir);
    const relativePath = path.relative(cacheRoot, resolvedInstallDir);
    // Same strict-descendant guard as `attemptIsolatedCacheReset`, routed
    // through the shared cross-drive-safe `isOutsideRelative` predicate
    // (scripts/lib/path-classifier.js): reject "" (the cache root itself) and
    // anything outside it ("..", "../...", or the absolute target produced
    // across Windows drives). A sanitized key like "..foo" resolves UNDER the
    // cache root, is NOT a traversal, and must not trip the guard.
    const isStrictDescendant = relativePath !== "" && !isOutsideRelative(relativePath);

    if (!isStrictDescendant) {
      warnFn(
        `WARNING: Refusing to rm partial isolated fallback install dir; resolved path is not a descendant of ${toPosixPath(cacheRoot)}: ${toPosixPath(resolvedInstallDir)}`
      );
    } else {
      try {
        rmSyncFn(resolvedInstallDir, { recursive: true, force: true });
      } catch (error) {
        const detail = error && error.message ? error.message : String(error);
        warnFn(
          `WARNING: Failed to rm partial isolated fallback install dir at ${toPosixPath(resolvedInstallDir)}: ${detail}`
        );
      }
    }
  }

  mkdirSyncFn(installDir, { recursive: true });

  if (!existsSyncFn(packageJsonPath)) {
    writeIsolatedJestCacheManifest(packageJsonPath, writeFileSyncFn);
  }

  warnFn(`⚠️ Installing isolated fallback Jest (${jestSpec}).`);
  const installResult = runCommandFn(
    "npm",
    ["install", "--no-audit", "--no-fund", "--no-package-lock", "--no-save", jestSpec],
    {
      cwd: installDir
    }
  );

  if (installResult.error || installResult.status !== 0) {
    const detail =
      installResult.error && installResult.error.message
        ? installResult.error.message
        : `status=${installResult.status}`;
    warnFn(`⚠️ Isolated fallback Jest install failed (${detail}).`);
    return {
      jestBinPath: null,
      jestRunnerPath: null,
      cacheHit: false
    };
  }

  if (!existsSyncFn(jestBinPath)) {
    warnFn(`⚠️ Isolated fallback Jest binary missing after install: ${toPosixPath(jestBinPath)}`);
    return {
      jestBinPath: null,
      jestRunnerPath: null,
      cacheHit: false
    };
  }

  const installedJestRunnerPath = resolveIsolatedJestRunnerPathFn(installDir, {
    existsSyncFn,
    statSyncFn
  });

  if (!installedJestRunnerPath) {
    warnFn(
      `⚠️ Isolated fallback Jest runner missing after install (legacy fallback path: ${toPosixPath(defaultJestRunnerPath)}).`
    );
    return {
      jestBinPath: null,
      jestRunnerPath: null,
      cacheHit: false
    };
  }

  return {
    jestBinPath,
    jestRunnerPath: installedJestRunnerPath,
    cacheHit: false
  };
}

function printIsolatedFallbackSelection(
  jestBinPath,
  cacheHit,
  { callerProvidedTestRunner = false, nodePathOverride = null } = {}
) {
  const cacheLabel = cacheHit ? "cache hit" : "fresh install";
  console.warn(`⚠️ Using isolated fallback Jest (${cacheLabel}): ${toPosixPath(jestBinPath)}`);

  if (callerProvidedTestRunner) {
    console.warn("⚠️ Caller provided --testRunner; managed runner did not override it.");
  }

  if (nodePathOverride) {
    console.warn(`⚠️ Injected NODE_PATH for isolated fallback: ${toPosixPath(nodePathOverride)}`);
  }
}

function runIsolatedFallbackJest(
  args,
  {
    getPinnedFallbackJestSpecFn = getPinnedFallbackJestSpec,
    prepareIsolatedFallbackJestFn = prepareIsolatedFallbackJest,
    runCommandFn = runCommandCapturingStderr,
    printIsolatedFallbackSelectionFn = printIsolatedFallbackSelection,
    existsSyncFn = fs.existsSync,
    hasCliOptionFn = hasCliOption,
    warnFn = console.warn
  } = {}
) {
  const jestSpec = getPinnedFallbackJestSpecFn();
  const prepared = prepareIsolatedFallbackJestFn(jestSpec);

  if (!prepared || !prepared.jestBinPath) {
    return null;
  }

  // Validate the isolated jest-circus runner exists as a healthy-install
  // precondition, but DO NOT inject `--testRunner` with an absolute path.
  // Jest 27+ defaults to jest-circus and its own resolver, walking up from
  // the isolated jest binary, reliably finds the sibling jest-circus we just
  // installed. Passing an absolute path triggers a separate jest-config
  // validator path that has been observed to reject otherwise-valid paths on
  // Windows. If the caller explicitly passes `--testRunner`, forward it.
  const callerProvidedTestRunner = hasCliOptionFn(args, "--testRunner");

  if (!callerProvidedTestRunner) {
    if (!prepared.jestRunnerPath || !existsSyncFn(prepared.jestRunnerPath)) {
      warnFn(
        `⚠️ Isolated fallback Jest runner unavailable at expected path: ${toPosixPath(prepared.jestRunnerPath)}`
      );
      return null;
    }
  }

  const invocationArgs = [prepared.jestBinPath, ...args];

  const isolatedNodeModulesPath = path.dirname(path.dirname(path.dirname(prepared.jestBinPath)));
  const isolatedNodePathEnv = buildNodePathEnv(isolatedNodeModulesPath);

  printIsolatedFallbackSelectionFn(prepared.jestBinPath, prepared.cacheHit, {
    callerProvidedTestRunner,
    nodePathOverride: isolatedNodePathEnv.NODE_PATH
  });

  return runCommandFn(process.execPath, invocationArgs, {
    env: isolatedNodePathEnv
  });
}

function isCommandUnavailable(result) {
  if (!result) {
    return true;
  }

  if (result.error && ["ENOENT", "EACCES"].includes(result.error.code)) {
    return true;
  }

  return result.status === 127;
}

function resolveLocalModule(moduleSpecifier) {
  try {
    return REPO_REQUIRE.resolve(moduleSpecifier);
  } catch {
    return null;
  }
}

function hasHealthyLocalJestInstall(
  moduleResolver = resolveLocalModule,
  existsSyncFn = fs.existsSync,
  tryLoadModuleFn = tryLoadModule
) {
  if (!existsSyncFn(LOCAL_JEST_BIN)) {
    return false;
  }

  const circusRunnerPath = moduleResolver("jest-circus/runner");
  if (!circusRunnerPath) {
    return false;
  }

  if (!existsSyncFn(circusRunnerPath)) {
    return false;
  }

  if (!isPathInsideDirectory(circusRunnerPath, REPO_NODE_MODULES)) {
    return false;
  }

  return tryLoadModuleFn("jest-circus/runner");
}

function printLocalJestFallbackWarning() {
  console.warn("⚠️ Local Jest install appears incomplete; falling back to managed Jest.");
  if (process.platform === "win32") {
    console.warn("Windows tip: run npm install/npm ci in the same shell used by git hooks.");
  }
}

function runManagedJest(args, options = {}) {
  const {
    hasHealthyLocalJestInstallFn = hasHealthyLocalJestInstall,
    getNpmMajorVersionFn = getNpmMajorVersion,
    printLocalJestFallbackWarningFn = printLocalJestFallbackWarning,
    runLocalJestFn = runLocalJest,
    runIsolatedFallbackJestFn = runIsolatedFallbackJest,
    runNpmExecJestFn = runNpmExecJest,
    runNpxJestFn = runNpxJest,
    decodeJestStderrFn = decodeJestStderr,
    printActionableRepairBannerFn = printActionableRepairBanner,
    attemptIsolatedCacheResetFn = attemptIsolatedCacheReset,
    attemptNpmCiRecoveryFn = attemptNpmCiRecovery,
    runLockedNpmCiRecoveryFn = runLockedNpmCiRecovery,
    getPinnedFallbackJestSpecFn = getPinnedFallbackJestSpec,
    // Integrity gate dependencies (Step 5). Tests inject deterministic
    // fakes; the production defaults wire through to the real probe and
    // the real npm-ci recovery.
    runIntegrityGateWithRecoveryFn = runIntegrityGateWithRecovery,
    probeIntegrityFn = probeIntegrity,
    probeIntegrityInSubprocessFn = probeIntegrityInSubprocess,
    probeResolverHealthFn = probeResolverHealth,
    isAutoRepairAllowedFn = null,
    // Used by the isolated-tier post-Jest MISSING_TEST_RUNNER routing.
    // Injected for testability; defaults to the production classifier.
    classifyCapturedPathFn = classifyCapturedPath,
    envFn = () => process.env,
    warnFn = console.warn
  } = options;

  // ---- Integrity gate (runs BEFORE any tier) ----
  //
  // The gate confirms the on-disk node_modules tree has the critical files
  // every managed tool depends on. The Windows failure mode that motivated
  // this rewrite (`testRunner option was not found`) was caused by a
  // partial extract that the existing tier-level self-heal could not see.
  //
  // Opt-outs (use isTruthyEnv for shell-script-style truthiness):
  //   - DXMSG_HOOK_SKIP_INTEGRITY=1 -> bypass the gate entirely.
  //   - DXMSG_HOOK_NO_AUTOREPAIR=1 -> still probe, but if integrity fails,
  //     skip npm ci and proceed to Jest invocation with a degraded gate.
  const env = (envFn && envFn()) || process.env;
  if (!isTruthyEnv(env.DXMSG_HOOK_SKIP_INTEGRITY)) {
    const resolvedIsAutoRepairAllowed =
      isAutoRepairAllowedFn !== null
        ? isAutoRepairAllowedFn
        : () =>
            defaultIsAutoRepairAllowed({
              env,
              repoRoot: REPO_ROOT,
              getNpmMajorVersionFn
            });

    const gateResult = runIntegrityGateWithRecoveryFn({
      repoRoot: REPO_ROOT,
      probeIntegrityFn,
      probeIntegrityInSubprocessFn,
      probeResolverHealthFn,
      attemptNpmCiRecoveryFn,
      isAutoRepairAllowedFn: resolvedIsAutoRepairAllowed,
      printActionableRepairBannerFn,
      decoder: jestErrorDecoderModule,
      warnFn,
      env
    });

    if (!gateResult || !gateResult.ok) {
      if (isTruthyEnv(env.DXMSG_HOOK_NO_AUTOREPAIR)) {
        // Degraded mode: proceed to tier dispatch even though
        // integrity is bad. The operator has explicitly asked us not
        // to repair; banner is already printed by the gate.
        warnFn(
          "WARNING: integrity gate failed but DXMSG_HOOK_NO_AUTOREPAIR=1 -> proceeding to Jest invocation with degraded gate."
        );
      } else {
        return { status: 1, error: null };
      }
    }
  }

  // ---- Local Jest path ----
  //
  // Self-heal scope: this tier may invoke `npm ci` ONCE when stderr signals
  // a missing local jest binary. We deliberately do NOT call
  // attemptIsolatedCacheReset here; the isolated cache lives under
  // os.tmpdir() and has no causal relationship to a corrupted local
  // node_modules. Cache reset is scoped to the isolated-fallback tier only.
  if (hasHealthyLocalJestInstallFn()) {
    const localResult = runLocalJestFn(args);

    if (localResult !== null) {
      if (localResult.status === 0) {
        return localResult;
      }

      const decoded = decodeJestStderrFn(localResult.stderr);

      if (decoded && decoded.kind === "MISSING_TEST_RUNNER") {
        // Local Jest reports its testRunner is invalid. The local tier
        // has no in-place repair for this (cache reset is scoped to the
        // isolated tier, by design). Fall through to the isolated
        // fallback tier instead of giving up here so that path's
        // self-heal can attempt a recovery.
        //
        // Banner is intentionally NOT printed here; whichever later
        // tier produces the final error will surface it (or self-heal
        // will succeed and there is nothing to surface).
        //
        // Mirrors the production `runLocalJest` behavior of returning
        // null when its own runCommandCapturingStderr observes the
        // same stderr signature — this branch covers the case where a
        // caller-injected `runLocalJestFn` mock returns the failing
        // result directly.
        //
        // Note: MISSING_TEST_RUNNER's selfHeal now includes
        // `npmCi: true` so the integrity gate can choose npm-ci
        // recovery for repo-tree partial extracts. The tier-level
        // dispatcher must NOT attempt npm ci here; by the time the
        // local tier runs, the integrity gate has already verified
        // node_modules integrity (or aborted upstream).
      } else if (
        decoded &&
        decoded.selfHeal &&
        decoded.selfHeal.npmCi &&
        decoded.selfHeal.retryOnce
      ) {
        const recovery = runLockedNpmCiRecoveryFn(attemptNpmCiRecoveryFn, { warnFn });
        if (recovery && recovery.status === 0) {
          const retryResult = runLocalJestFn(args);
          if (retryResult !== null) {
            if (retryResult.status !== 0) {
              const retryDecoded = decodeJestStderrFn(retryResult.stderr);
              printActionableRepairBannerFn(retryDecoded);
            }
            return retryResult;
          }
          // Retry path failed-through (returned null); fall through
          // to the managed fallback chain below. Banner is deferred
          // to whichever later tier produces the final error.
        } else {
          printActionableRepairBannerFn(decoded);
          return localResult;
        }
      } else if (decoded) {
        printActionableRepairBannerFn(decoded);
        return localResult;
      } else {
        return localResult;
      }
    }
    // Local Jest could not be safely prepared (or returned MISSING_TEST_RUNNER);
    // fall through to the managed fallback chain.
  }

  const hasLocalJestBinary = fs.existsSync(LOCAL_JEST_BIN);

  // ---- Isolated fallback path ----
  //
  // Self-heal scope: this tier may delete the isolated cache directory ONCE
  // when stderr signals a corrupt cache or missing test runner. The reset
  // never touches the repo's node_modules.
  if (hasLocalJestBinary) {
    printLocalJestFallbackWarningFn();

    const isolatedResult = runIsolatedFallbackJestFn(args);
    if (isolatedResult) {
      if (isolatedResult.status === 0) {
        return isolatedResult;
      }

      const decoded = decodeJestStderrFn(isolatedResult.stderr);
      if (
        decoded &&
        decoded.selfHeal &&
        decoded.selfHeal.isolatedCacheReset &&
        decoded.selfHeal.retryOnce
      ) {
        // MISSING_TEST_RUNNER carries BOTH npmCi and
        // isolatedCacheReset self-heal flags because the same
        // stderr can come from either a partial repo
        // node_modules extract or a corrupt isolated cache.
        // Classify the captured runner path to choose the right
        // recovery:
        //   - "repo"     -> attemptNpmCiRecovery (gated by the
        //                   production isAutoRepairAllowed check).
        //                   On success, retry the isolated tier.
        //   - "isolated" -> existing behavior: cache reset + retry.
        //   - "unknown"  -> refuse to auto-repair; print banner only.
        // CORRUPT_ISOLATED_CACHE carries only isolatedCacheReset
        // and falls through to the legacy isolated reset branch.
        const supportsNpmCi = Boolean(decoded.selfHeal && decoded.selfHeal.npmCi);
        const capturedPath =
          decoded.capturedMatch && typeof decoded.capturedMatch[1] === "string"
            ? decoded.capturedMatch[1]
            : null;
        const classification = supportsNpmCi
          ? classifyCapturedPathFn(capturedPath, {
              repoNodeModules: REPO_NODE_MODULES,
              isolatedCacheRoot: ISOLATED_JEST_CACHE_ROOT
            })
          : PATH_CLASS_ISOLATED;

        if (classification === PATH_CLASS_REPO) {
          // Repo-tier partial extract surfacing as MISSING_TEST_RUNNER.
          // Run the production auto-repair gate, then npm ci, then
          // a subprocess re-probe before retrying the isolated tier.
          const resolvedIsAutoRepairAllowed =
            isAutoRepairAllowedFn !== null
              ? isAutoRepairAllowedFn
              : () =>
                  defaultIsAutoRepairAllowed({
                    env,
                    repoRoot: REPO_ROOT,
                    getNpmMajorVersionFn
                  });
          const repairDecision = resolvedIsAutoRepairAllowed();
          if (!repairDecision || !repairDecision.allowed) {
            warnFn(
              `WARNING: post-Jest MISSING_TEST_RUNNER classified as repo, but auto-repair refused (${repairDecision && repairDecision.reason ? repairDecision.reason : "no reason"}); printing banner only.`
            );
            printActionableRepairBannerFn(decoded);
            return isolatedResult;
          }
          const recovery = runLockedNpmCiRecoveryFn(attemptNpmCiRecoveryFn, { warnFn });
          if (recovery && recovery.status === 0) {
            // Re-probe to make sure the partial extract is
            // actually fixed; if so, retry the isolated tier
            // (the local install was unhealthy enough to fall
            // through here, so we retry isolated rather than
            // ricocheting back to runLocalJestFn).
            const reprobe = probeIntegrityInSubprocessFn({ repoRoot: REPO_ROOT });
            if (reprobe && reprobe.ok) {
              const retryResult = runIsolatedFallbackJestFn(args);
              if (retryResult) {
                if (retryResult.status !== 0) {
                  const retryDecoded = decodeJestStderrFn(retryResult.stderr);
                  printActionableRepairBannerFn(retryDecoded);
                }
                return retryResult;
              }
              // Retry returned null; fall through to npm exec/npx.
            } else {
              printActionableRepairBannerFn(decoded);
              return isolatedResult;
            }
          } else {
            printActionableRepairBannerFn(decoded);
            return isolatedResult;
          }
        } else if (classification === PATH_CLASS_UNKNOWN) {
          // Path lives outside both the repo node_modules and the
          // isolated cache. We have no safe recovery to attempt;
          // surface the banner and return.
          //
          // We distinguish null/undefined ("(null)") from the empty
          // string ("(empty)") in the operator-facing warning so a
          // stderr regression that captures "" instead of dropping
          // the field entirely is debuggable from log triage.
          let pathLabel;
          if (capturedPath === null || capturedPath === undefined) {
            pathLabel = "(null)";
          } else if (capturedPath === "") {
            pathLabel = "(empty)";
          } else {
            pathLabel = toPosixPath(capturedPath);
          }
          warnFn(
            `WARNING: post-Jest MISSING_TEST_RUNNER captured path ${pathLabel} is outside both the repo and isolated trees; refusing to auto-repair.`
          );
          printActionableRepairBannerFn(decoded);
          return isolatedResult;
        } else {
          // PATH_CLASS_ISOLATED (or CORRUPT_ISOLATED_CACHE which
          // never opts into npmCi): existing behavior.
          const jestSpec = getPinnedFallbackJestSpecFn();
          const resetOk = attemptIsolatedCacheResetFn(jestSpec);
          if (resetOk) {
            const retryResult = runIsolatedFallbackJestFn(args);
            if (retryResult) {
              if (retryResult.status !== 0) {
                const retryDecoded = decodeJestStderrFn(retryResult.stderr);
                printActionableRepairBannerFn(retryDecoded);
              }
              return retryResult;
            }
            // Retry returned null; fall through to npm exec/npx.
          } else {
            printActionableRepairBannerFn(decoded);
            return isolatedResult;
          }
        }
      } else if (decoded) {
        printActionableRepairBannerFn(decoded);
        return isolatedResult;
      } else {
        return isolatedResult;
      }
    }

    console.warn("⚠️ Isolated fallback Jest was unavailable; trying npm exec/npx fallback.");
  }

  // ---- npm exec / npx fallback paths ----
  //
  // No self-healing here: these are last-resort, network-bound, and slow.
  // We only decode stderr and print a banner on failure so the user knows
  // what to repair manually.
  const npmMajor = getNpmMajorVersionFn();

  let finalResult;
  if (npmMajor === null || npmMajor < 7) {
    finalResult = runNpxJestFn(args);
  } else {
    const npmExecResult = runNpmExecJestFn(args);
    if (isCommandUnavailable(npmExecResult)) {
      finalResult = runNpxJestFn(args);
    } else {
      finalResult = npmExecResult;
    }
  }

  if (finalResult && finalResult.status !== 0) {
    const decoded = decodeJestStderrFn(finalResult.stderr);
    printActionableRepairBannerFn(decoded);
  }

  return finalResult;
}

function printManagedJestLaunchError(error) {
  const detail = error && error.message ? ` (${error.message})` : "";
  console.error(`❌ Failed to launch managed Jest${detail}.`);
  console.error("Ensure Node.js/npm are available in this shell, or run npm install.");
  if (process.platform === "win32") {
    console.error(
      "Windows tip: if you use nvm/fnm, open PowerShell or Git Bash with Node initialized and verify npm --version."
    );
  }
}

function main() {
  const result = runManagedJest(process.argv.slice(2));

  if (result.error) {
    printManagedJestLaunchError(result.error);
    process.exit(1);
  }

  const status = typeof result.status === "number" ? result.status : 1;
  process.exit(status);
}

module.exports = {
  REPO_ROOT,
  REPO_NODE_MODULES,
  PACKAGE_LOCK_PATH,
  LOCAL_JEST_BIN,
  FALLBACK_JEST_SPEC,
  ISOLATED_JEST_CACHE_ROOT,
  normalizeForPathComparison,
  isPathInsideDirectory,
  classifyCapturedPath,
  PATH_CLASS_REPO,
  PATH_CLASS_ISOLATED,
  PATH_CLASS_UNKNOWN,
  toPosixPath,
  toRepoPosixRelative,
  resolveLocalModule,
  tryLoadModule,
  hasHealthyLocalJestInstall,
  printLocalJestFallbackWarning,
  sanitizeCacheKey,
  getDefaultIsolatedJestRunnerPath,
  getIsolatedJestPaths,
  resolveIsolatedJestRunnerPath,
  hasCliOption,
  buildNodePathEnv,
  writeIsolatedJestCacheManifest,
  prepareIsolatedFallbackJest,
  printIsolatedFallbackSelection,
  runIsolatedFallbackJest,
  toShellCommand,
  parseNpmMajorVersion,
  getNpmMajorVersion,
  getPinnedFallbackJestSpec,
  normalizeNodeColorEnv,
  runCommand,
  runCommandCapturingStderr,
  runLocalJest,
  runNpmExecJest,
  runNpxJest,
  isShellShimCommand,
  spawnPlatformCommandSync,
  isCommandUnavailable,
  runManagedJest,
  printManagedJestLaunchError,
  attemptIsolatedCacheReset,
  removeDirIfStrictDescendant,
  attemptNpmCiRecovery,
  getNpmRecoveryCommand,
  runLockedNpmCiRecovery,
  isSuccessfulCommandResult,
  warnNpmCiFailure,
  removeNodeModulesForRecovery,
  sleepSync,
  printActionableRepairBanner,
  decodeJestStderr,
  formatRepairBanner,
  isTruthyEnv,
  INTEGRITY_TARGETS,
  probeIntegrity,
  probeIntegrityInSubprocess,
  probeResolverHealth,
  runIntegrityGateWithRecovery,
  isAutoRepairAllowed: defaultIsAutoRepairAllowed
};

if (require.main === module) {
  main();
}
