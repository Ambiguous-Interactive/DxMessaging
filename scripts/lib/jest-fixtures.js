"use strict";

/**
 * @fileoverview Shared Jest test-fixture helpers for the `scripts/**` suites.
 *
 * Consolidates the scratch-directory, throwaway-git-repo, and platform-override
 * boilerplate that was copy-pasted across dozens of test files:
 *
 *   - `withPlatform`     - run a callback with `process.platform` overridden,
 *                          restoring the original descriptor afterward.
 *   - `makeTempDir`      - create a uniquely-named scratch directory.
 *   - `cleanupDir`       - best-effort recursive removal of a scratch directory.
 *   - `makeTempGitRepo`  - `makeTempDir` + `git init` (+ optional identity).
 *   - `tempDirTracker`   - collect scratch dirs created during a suite and remove
 *                          them all in one `afterAll`/`afterEach` call.
 *
 * Before this module each of these lived as a near-identical local helper:
 * `withPlatform` was byte-for-byte duplicated in four suites; the
 * `mkdtempSync(...)` + `rmSync(..., { recursive: true, force: true })` cleanup
 * idiom appeared in 50+ files; and the `git init` + `git config user.*` recipe
 * was repeated in every test that needs a real repository. This module is the
 * canonical home and behavior-under-test for those patterns; consumer suites
 * migrate onto it incrementally (the per-file dedup is ongoing, not yet
 * exhaustive).
 *
 * This is test-support code (imported only from `scripts/**\/__tests__`), but it
 * lives under `scripts/lib` alongside the other shared libraries so these names
 * have one canonical home should the shared-helper gate later adopt them. It
 * spawns only `git` (never an npm/npx/`.cmd`/`.bat` batch shim), so it satisfies
 * the spawn-invocation policy, which scans every `scripts/**` production script
 * (`__tests__` excluded) for direct npm/npx or `.cmd`/`.bat` spawns. The
 * hermetic-host-env policy scans only `__tests__` files, so it never inspects
 * this module; the module also performs no host-folder env mutation regardless.
 *
 * Dependencies are limited to Node built-ins, so this module stays a safe
 * foundational dependency.
 */

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

/**
 * Run `fn` with `process.platform` reporting `platform`, then restore it.
 *
 * Overrides the `process.platform` property descriptor for the synchronous
 * duration of `fn` and restores the original descriptor in a `finally`, so a
 * throwing callback never leaks the fake platform into sibling tests. (On
 * supported Node `process.platform` is always an own property; the restore
 * defensively deletes the override should that ever not hold.)
 *
 * @template T
 * @param {NodeJS.Platform} platform Value `process.platform` should report.
 * @param {() => T} fn Callback to run under the override.
 * @returns {T} Whatever `fn` returns.
 */
function withPlatform(platform, fn) {
  const original = Object.getOwnPropertyDescriptor(process, "platform");
  Object.defineProperty(process, "platform", { value: platform, configurable: true });
  try {
    return fn();
  } finally {
    if (original) {
      Object.defineProperty(process, "platform", original);
    } else {
      delete process.platform;
    }
  }
}

/**
 * Create a uniquely-named scratch directory and return its absolute path.
 *
 * Wraps `fs.mkdtempSync` so the random suffix and the canonical
 * `<prefix><label>-` naming are applied consistently. The directory is NOT
 * registered for cleanup; pair it with {@link cleanupDir} or
 * {@link tempDirTracker}.
 *
 * @param {string} label Human-readable middle segment of the directory name.
 * @param {{ root?: string, prefix?: string }} [options]
 *   `root` (default `os.tmpdir()`) is the parent directory; pass the repository
 *   root for the rare scanner whose exclusion list rejects the OS temp location.
 *   `prefix` (default `"dxmsg-"`) precedes `label`.
 * @returns {string} Absolute path to the freshly created directory.
 */
function makeTempDir(label, options = {}) {
  const { root = os.tmpdir(), prefix = "dxmsg-" } = options;
  return fs.mkdtempSync(path.join(root, `${prefix}${label}-`));
}

/**
 * Recursively remove a scratch directory, swallowing any error.
 *
 * Teardown must never fail a suite over a directory the OS already reclaimed or
 * is holding open, so removal is best-effort (`{ recursive: true, force: true }`
 * and a swallowed error), matching the established `afterAll` cleanup idiom.
 *
 * @param {string} dir Directory to remove.
 * @returns {void}
 */
function cleanupDir(dir) {
  try {
    fs.rmSync(dir, { recursive: true, force: true });
  } catch (_error) {
    // Best-effort cleanup; a leaked temp dir must never fail the suite.
  }
}

/**
 * Create a scratch directory initialized as a Git repository.
 *
 * Runs `git init` in a fresh {@link makeTempDir}; a non-zero exit (or a missing
 * `git`) throws, per the repository convention that git-metadata failures are
 * hard errors. When `user` is supplied, a local `user.email`/`user.name`
 * identity is configured so later `git commit` calls do not fail on identity
 * lookup; the `git config` exit status is not inspected (it does not fail in
 * practice and no caller depended on it).
 *
 * @param {string} label Middle segment of the directory name.
 * @param {{
 *   root?: string,
 *   prefix?: string,
 *   quiet?: boolean,
 *   user?: { email: string, name: string }
 * }} [options]
 *   `root`/`prefix` are forwarded to {@link makeTempDir}. `quiet` (default
 *   `true`) passes `-q` to `git init`. `user`, when present, configures a local
 *   commit identity.
 * @returns {string} Absolute path to the initialized repository.
 */
function makeTempGitRepo(label, options = {}) {
  const { root, prefix, quiet = true, user } = options;
  // `undefined` root/prefix fall through to makeTempDir's own defaults.
  const dir = makeTempDir(label, { root, prefix });

  const initArgs = quiet ? ["init", "-q"] : ["init"];
  const initResult = childProcess.spawnSync("git", initArgs, { cwd: dir, encoding: "utf8" });
  if (initResult.error) {
    throw new Error(`Failed to run "git init" in temp repo: ${initResult.error.message}`);
  }
  if (initResult.status !== 0) {
    const stderr = (initResult.stderr || "").trim();
    throw new Error(
      `"git init" exited with status ${initResult.status}` + (stderr ? `: ${stderr}` : "")
    );
  }

  if (user) {
    childProcess.spawnSync("git", ["config", "user.email", user.email], {
      cwd: dir,
      encoding: "utf8"
    });
    childProcess.spawnSync("git", ["config", "user.name", user.name], {
      cwd: dir,
      encoding: "utf8"
    });
  }

  return dir;
}

/**
 * Collect scratch directories created during a suite for one-shot teardown.
 *
 * Replaces the `const tempDirs = []; afterAll(() => { for (...) rmSync(...) })`
 * idiom. `make`/`makeGitRepo` create a directory (forwarding to
 * {@link makeTempDir}/{@link makeTempGitRepo}, with the tracker's `defaults`
 * applied first) and register it; `cleanup` removes every registered directory
 * best-effort and resets the list, so it is safe to wire into both `afterEach`
 * and `afterAll`.
 *
 * @param {{ root?: string, prefix?: string }} [defaults] Default options merged
 *   under each `make`/`makeGitRepo` call's own options.
 * @returns {{
 *   make: (label: string, options?: object) => string,
 *   makeGitRepo: (label: string, options?: object) => string,
 *   cleanup: () => void
 * }} A tracker that owns its scratch directories until `cleanup`.
 */
function tempDirTracker(defaults = {}) {
  const dirs = [];
  return {
    make(label, options = {}) {
      const dir = makeTempDir(label, { ...defaults, ...options });
      dirs.push(dir);
      return dir;
    },
    makeGitRepo(label, options = {}) {
      const dir = makeTempGitRepo(label, { ...defaults, ...options });
      dirs.push(dir);
      return dir;
    },
    cleanup() {
      for (const dir of dirs) {
        cleanupDir(dir);
      }
      dirs.length = 0;
    }
  };
}

module.exports = {
  withPlatform,
  makeTempDir,
  cleanupDir,
  makeTempGitRepo,
  tempDirTracker
};
