"use strict";

const fs = require("fs");
const path = require("path");

/**
 * Resolve a path for inside/outside comparisons. Missing leaves are appended
 * to the nearest existing realpath; Windows comparisons are case-insensitive.
 * @param {{
 *   pathImpl?: typeof path,
 *   realpathSync?: typeof fs.realpathSync,
 *   caseInsensitive?: boolean
 * }} [options]
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

function isPathInsideDirectory(filePath, directoryPath, options = {}) {
  const { pathImpl = path } = options;
  const normalizedFilePath = canonicalizePathForComparison(filePath, options);
  const normalizedDirectoryPath = canonicalizePathForComparison(directoryPath, options);
  const relativePath = pathImpl.relative(normalizedDirectoryPath, normalizedFilePath);
  return !isOutsideRelative(relativePath, pathImpl);
}

/**
 * Cross-drive-safe outside check. Use this instead of hand-rolled
 * `path.relative(...).startsWith("..")`, which misses Windows absolute
 * relative results when paths are on different drives.
 */
function isPathOutsideDirectory(filePath, directoryPath, options = {}) {
  return !isPathInsideDirectory(filePath, directoryPath, options);
}

/**
 * Low-level predicate for a `path.relative()` result. Absolute relative values
 * are outside too; that branch is required for Windows cross-drive paths.
 */
function isOutsideRelative(rel, pathImpl = path) {
  if (typeof rel !== "string" || rel === "") {
    return false;
  }
  return rel === ".." || rel.startsWith(".." + pathImpl.sep) || pathImpl.isAbsolute(rel);
}

/**
 * Convert path-like values to forward-slash strings for logs and assertions.
 * Nullish values become `""`; other non-strings are stringified.
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
 * Return repo-relative POSIX paths when possible, otherwise POSIX absolute.
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
