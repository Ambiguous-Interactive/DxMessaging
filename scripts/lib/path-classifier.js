"use strict";

/**
 * path-classifier.js
 *
 * Pure helpers for resolving and classifying filesystem paths during managed
 * Jest/Prettier/cspell self-heal flows. The integrity gate (and several
 * tier-level decisions) need to answer two questions cheaply:
 *
 *   1. "Is this resolved path inside a particular directory?" Used to refuse
 *      cache-reset against the repo's node_modules, and to refuse repo-wide
 *      repair against an isolated cache subtree.
 *   2. "Does a captured runner path belong to the repo, the isolated cache,
 *      or neither?" Used to choose between npm-ci and isolated-cache-reset
 *      recoveries.
 *
 * No side effects at module load; every function is pure modulo the
 * fs.realpathSync probe inside `normalizeForPathComparison` (which is the
 * existing production behavior preserved verbatim).
 */

const fs = require("fs");
const path = require("path");

const PATH_CLASS_REPO = "repo";
const PATH_CLASS_ISOLATED = "isolated";
const PATH_CLASS_UNKNOWN = "unknown";

/**
 * Resolve a path to an absolute, OS-canonical, symlink-followed form suitable
 * for prefix/inside-of comparison. On Windows, the comparison is
 * case-insensitive (lowercased). On POSIX, the comparison is case-sensitive.
 *
 * If `fs.realpathSync` fails (e.g. the target does not exist), the resolved
 * path is returned without realpath resolution; callers handle existence
 * separately. This mirrors the original `run-managed-jest.js` implementation.
 *
 * @param {string} targetPath Path to normalize.
 * @returns {string} Normalized absolute path.
 */
function normalizeForPathComparison(targetPath) {
  let resolved = path.resolve(targetPath);
  try {
    resolved = fs.realpathSync.native
      ? fs.realpathSync.native(resolved)
      : fs.realpathSync(resolved);
  } catch {
    // Keep resolved path when target is unavailable; callers handle existence separately.
  }
  return process.platform === "win32" ? resolved.toLowerCase() : resolved;
}

/**
 * Return true when `filePath` is `directoryPath` itself or a descendant of it.
 * Comparison is symlink-resolved and case-folded on Windows (see
 * `normalizeForPathComparison`).
 *
 * @param {string} filePath Path under test.
 * @param {string} directoryPath Candidate parent directory.
 * @returns {boolean}
 */
function isPathInsideDirectory(filePath, directoryPath) {
  const normalizedFilePath = normalizeForPathComparison(filePath);
  const normalizedDirectoryPath = normalizeForPathComparison(directoryPath);
  const relativePath = path.relative(normalizedDirectoryPath, normalizedFilePath);
  return !isOutsideRelative(relativePath);
}

/**
 * Return true when `filePath` is OUTSIDE `directoryPath` -- i.e. it is neither
 * `directoryPath` itself nor a descendant of it. This is the cross-drive-safe
 * inverse of {@link isPathInsideDirectory} and is THE sanctioned way to answer
 * "is this path outside X".
 *
 * Why a named helper instead of `path.relative(dir, file).startsWith("..")`:
 * on Windows when `file` and `dir` live on DIFFERENT drives (e.g. a D:\ repo
 * and a C:\ os.tmpdir() cache root), `path.relative` cannot express a relative
 * traversal and returns the ABSOLUTE target (`C:\Users\...`). That string does
 * NOT start with `".."`, so a bare `startsWith("..")` reports the path as
 * INSIDE the directory even though it is on another drive entirely. Routing
 * through {@link isPathInsideDirectory} (which guards with `path.isAbsolute`,
 * symlink-resolves, and case-folds on Windows) is correct on Linux, macOS,
 * Windows same-drive, AND Windows cross-drive.
 *
 * @param {string} filePath Path under test.
 * @param {string} directoryPath Candidate parent directory.
 * @returns {boolean} True when `filePath` is outside `directoryPath`.
 */
function isPathOutsideDirectory(filePath, directoryPath) {
  return !isPathInsideDirectory(filePath, directoryPath);
}

/**
 * Low-level companion to {@link isPathOutsideDirectory} for call sites that
 * ALREADY hold a `path.relative(dir, file)` result and only need to know
 * whether that relative path escapes the directory. Returns true when `rel`
 * names something outside (or above) the base directory:
 *   - `".."` exactly (the parent itself),
 *   - a `".." + path.sep` prefix (genuine upward traversal), OR
 *   - an ABSOLUTE path (cross-drive Windows / UNC, where `path.relative`
 *     returns a drive-qualified absolute target rather than a `..` chain).
 *
 * An empty string means `rel` IS the base directory (a descendant-or-self), so
 * it is NOT outside. This is the canonical predicate for the bare
 * `rel.startsWith("..")` anti-pattern: that shortcut omits the
 * `path.isAbsolute(rel)` branch and therefore mislabels cross-drive paths.
 *
 * @param {string} rel A `path.relative()` result.
 * @param {{sep: string, isAbsolute: (p: string) => boolean}} [pathImpl]
 *   Path implementation to evaluate separators and absoluteness against.
 *   Defaults to the host `path`. Tests inject `path.win32` (or `path.posix`)
 *   so the cross-drive/UNC absolute branch can be exercised on EITHER host OS
 *   rather than only on the one whose `path.sep`/`path.isAbsolute` happens to
 *   match -- the same platform-divergence discipline the repo applies to
 *   spawn-shape and EOL tests.
 * @returns {boolean} True when `rel` escapes the base directory.
 */
function isOutsideRelative(rel, pathImpl = path) {
  if (typeof rel !== "string" || rel === "") {
    return false;
  }
  return rel === ".." || rel.startsWith(".." + pathImpl.sep) || pathImpl.isAbsolute(rel);
}

/**
 * Classify a captured runner/module path into one of three buckets:
 *   - "repo"     - the path lives under the repository node_modules tree.
 *   - "isolated" - the path lives under the isolated managed-Jest cache root.
 *   - "unknown"  - the path is null/undefined, empty, non-string, or lives
 *                  outside both trees.
 *
 * The repo path takes precedence over isolated when both options exist (the
 * repo's node_modules is rarely placed under the isolated cache root in
 * practice, but defense-in-depth: if it ever is, the repo-tier recovery is
 * the correct first attempt).
 *
 * @param {string|null|undefined} capturedPath Path observed in stderr (or
 *   resolved by the wrapper).
 * @param {object} bounds Bucket boundaries.
 * @param {string} bounds.repoNodeModules Absolute path to the repo's
 *   node_modules directory.
 * @param {string} bounds.isolatedCacheRoot Absolute path to the isolated
 *   managed-Jest cache root.
 * @returns {"repo"|"isolated"|"unknown"}
 */
function classifyCapturedPath(capturedPath, { repoNodeModules, isolatedCacheRoot } = {}) {
  if (typeof capturedPath !== "string" || capturedPath.length === 0) {
    return PATH_CLASS_UNKNOWN;
  }
  if (typeof repoNodeModules === "string" && repoNodeModules.length > 0) {
    if (isPathInsideDirectory(capturedPath, repoNodeModules)) {
      return PATH_CLASS_REPO;
    }
  }
  if (typeof isolatedCacheRoot === "string" && isolatedCacheRoot.length > 0) {
    if (isPathInsideDirectory(capturedPath, isolatedCacheRoot)) {
      return PATH_CLASS_ISOLATED;
    }
  }
  return PATH_CLASS_UNKNOWN;
}

/**
 * Convert any path-like string to POSIX (forward-slash) separators.
 *
 * Idempotent on POSIX input. Does NOT resolve or normalize; pure separator
 * swap. Use for user-facing display strings and for cross-platform string
 * assertions where the comparison value is known in POSIX form.
 *
 * Null / undefined map to the empty string (`""`) so callers can use this
 * helper inside template literals without paying for runtime type narrowing
 * AND without leaking the strings `"null"` / `"undefined"` into log output
 * when the upstream value was unset. Non-null primitives (number, boolean)
 * are coerced via `String(value)` and then separator-swapped; this keeps
 * the helper resilient when a caller accidentally hands it a non-string.
 *
 * @param {*} value Path-like value (typically a string).
 * @returns {string} POSIX-separator form; `""` for null / undefined; the
 *   stringified-and-swapped form for other non-string inputs.
 */
function toPosixPath(value) {
  if (value === null || value === undefined) {
    return "";
  }
  if (typeof value !== "string") {
    return String(value).replace(/\\/g, "/");
  }
  return value.replace(/\\/g, "/");
}

/**
 * Repo-relative POSIX form of `absPath`.
 *
 * Falls back to the POSIX absolute form (via {@link toPosixPath}) when the
 * path lives outside `repoRoot` (i.e. `path.relative` returns a parent-
 * traversal or an absolute path on Windows for cross-drive inputs). Non-
 * string inputs are returned unchanged.
 *
 * Use this helper anywhere a user-facing log line names a path that is
 * "usually" inside the repo: the relative form is shorter and platform-
 * agnostic; the absolute fallback is still POSIX-normalized so log scrapers
 * never see backslashes.
 *
 * @param {*} absPath Absolute path-like value (typically a string).
 * @param {*} repoRoot Absolute repository root path.
 * @returns {*} POSIX-relative path when inside repo, POSIX-absolute fallback
 *   otherwise; original value when either input is not a string.
 */
function toRepoPosixRelative(absPath, repoRoot) {
  if (typeof absPath !== "string" || typeof repoRoot !== "string") {
    return absPath;
  }
  const rel = path.relative(repoRoot, absPath);
  if (rel === "" || isOutsideRelative(rel)) {
    return toPosixPath(absPath);
  }
  return toPosixPath(rel);
}

module.exports = {
  PATH_CLASS_REPO,
  PATH_CLASS_ISOLATED,
  PATH_CLASS_UNKNOWN,
  normalizeForPathComparison,
  isPathInsideDirectory,
  isPathOutsideDirectory,
  isOutsideRelative,
  classifyCapturedPath,
  toPosixPath,
  toRepoPosixRelative
};
