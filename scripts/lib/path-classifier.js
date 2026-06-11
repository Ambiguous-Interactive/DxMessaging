"use strict";

/**
 * path-classifier.js
 *
 * Pure path helpers shared by the kept repo scripts:
 *
 *   - update-llms-txt.js uses `isPathOutsideDirectory` to refuse writes
 *     outside the repository root.
 *   - validate-asmdef-references.js uses `toPosixPath` for log-friendly,
 *     platform-agnostic path output.
 *   - analyzers/verify-analyzer-payload.js uses `toRepoPosixRelative` for
 *     repo-relative log lines.
 *
 * No side effects at module load; every function is pure modulo the
 * fs.realpathSync probe inside `canonicalizePathForComparison`.
 */

const fs = require("fs");
const path = require("path");

/**
 * Resolve a path to an absolute, OS-canonical, symlink-followed form suitable
 * for prefix/inside-of comparison. Missing leaf paths are normalized by
 * resolving the nearest existing ancestor with realpath and appending the
 * unresolved tail, which keeps an existing base directory and a nonexistent
 * child comparable.
 *
 * On Windows, the comparison is case-insensitive (lowercased). On POSIX, the
 * comparison is case-sensitive.
 *
 * If no existing ancestor can be resolved with realpath, the resolved path is returned
 * without realpath resolution; callers handle existence separately.
 *
 * @param {string} targetPath Path to normalize.
 * @param {{
 *   pathImpl?: typeof path,
 *   realpathSync?: typeof fs.realpathSync,
 *   caseInsensitive?: boolean
 * }} [options] Dependency injection for tests and callers that classify
 *   non-host path flavors.
 * @returns {string} Normalized absolute path.
 */
function canonicalizePathForComparison(
  targetPath,
  {
    pathImpl = path,
    realpathSync = fs.realpathSync,
    caseInsensitive = process.platform === "win32"
  } = {}
) {
  const resolved = pathImpl.resolve(targetPath);
  const missingSegments = [];
  let currentPath = resolved;

  while (true) {
    try {
      const realpath =
        typeof realpathSync.native === "function"
          ? realpathSync.native(currentPath)
          : realpathSync(currentPath);
      const canonicalPath =
        missingSegments.length === 0
          ? realpath
          : pathImpl.join(realpath, ...missingSegments.reverse());
      return caseInsensitive ? canonicalPath.toLowerCase() : canonicalPath;
    } catch {
      const parentPath = pathImpl.dirname(currentPath);
      if (parentPath === currentPath) {
        return caseInsensitive ? resolved.toLowerCase() : resolved;
      }

      missingSegments.push(pathImpl.basename(currentPath));
      currentPath = parentPath;
    }
  }
}

/**
 * Return true when `filePath` is `directoryPath` itself or a descendant of it.
 * Comparison is symlink-resolved and case-folded on Windows (see
 * `canonicalizePathForComparison`).
 *
 * @param {string} filePath Path under test.
 * @param {string} directoryPath Candidate parent directory.
 * @param {{
 *   pathImpl?: typeof path,
 *   realpathSync?: typeof fs.realpathSync,
 *   caseInsensitive?: boolean
 * }} [options] Dependency injection for tests and callers that classify
 *   non-host path flavors.
 * @returns {boolean}
 */
function isPathInsideDirectory(filePath, directoryPath, options = {}) {
  const { pathImpl = path } = options;
  const normalizedFilePath = canonicalizePathForComparison(filePath, options);
  const normalizedDirectoryPath = canonicalizePathForComparison(directoryPath, options);
  const relativePath = pathImpl.relative(normalizedDirectoryPath, normalizedFilePath);
  return !isOutsideRelative(relativePath, pathImpl);
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
 * @param {{
 *   pathImpl?: typeof path,
 *   realpathSync?: typeof fs.realpathSync,
 *   caseInsensitive?: boolean
 * }} [options] Dependency injection for tests and callers that classify
 *   non-host path flavors.
 * @returns {boolean} True when `filePath` is outside `directoryPath`.
 */
function isPathOutsideDirectory(filePath, directoryPath, options = {}) {
  return !isPathInsideDirectory(filePath, directoryPath, options);
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
 *   match.
 * @returns {boolean} True when `rel` escapes the base directory.
 */
function isOutsideRelative(rel, pathImpl = path) {
  if (typeof rel !== "string" || rel === "") {
    return false;
  }
  return rel === ".." || rel.startsWith(".." + pathImpl.sep) || pathImpl.isAbsolute(rel);
}

/**
 * Convert any path-like string to POSIX (forward-slash) separators.
 *
 * Idempotent on POSIX input. Does NOT resolve or normalize; pure separator
 * swap. Use for user-facing display strings and for cross-platform string
 * assertions where the comparison value is known in POSIX form.
 *
 * Null / undefined map to the empty string (`""`) so callers can use this
 * helper inside template literals without leaking the strings `"null"` /
 * `"undefined"` into log output when the upstream value was unset. Non-null
 * primitives (number, boolean) are coerced via `String(value)` and then
 * separator-swapped.
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
  canonicalizePathForComparison,
  isPathOutsideDirectory,
  isOutsideRelative,
  toPosixPath,
  toRepoPosixRelative
};
