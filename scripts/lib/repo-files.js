"use strict";

/**
 * @fileoverview Shared recursive file walker.
 *
 * `walkFiles` is consumed by scripts/update-llms-txt.js and
 * scripts/unity/lib/asmdef-discovery.js. It replaces the near-identical
 * bespoke `walk` helpers those scripts used to carry, pinning one canonical
 * behavior (filtering, directory exclusion, unreadable-dir handling) in a
 * single tested home.
 */

const fs = require("fs");
const path = require("path");

/**
 * Recursively walk `dir`, returning absolute paths of files that match.
 *
 *   - `match(fullPath, dirent)` decides whether a file is included.
 *   - `excludeDir(fullPath, dirent)` decides whether a directory is skipped
 *     (its subtree is not descended into). It is consulted only for
 *     directories discovered during the walk, never for `dir` itself, so the
 *     caller is assumed to have chosen a root it wants walked.
 *   - `onError(error, dir)` is invoked when a directory cannot be read; by
 *     default unreadable directories are silently skipped (matching most
 *     callers). Pass a handler to warn, or rethrow inside it to fail hard.
 *
 * A missing root directory is treated like any other unreadable directory:
 * `onError` is invoked (if provided) and an empty array is returned.
 *
 * @param {string} dir Directory to walk (absolute or cwd-relative).
 * @param {{
 *   match?: (fullPath: string, dirent: import("fs").Dirent) => boolean,
 *   excludeDir?: (fullPath: string, dirent: import("fs").Dirent) => boolean,
 *   onError?: ((error: NodeJS.ErrnoException, dir: string) => void) | null
 * }} [options]
 * @returns {string[]} Absolute (or `dir`-relative if `dir` was relative)
 *   paths of matching files, in directory-entry order.
 */
function walkFiles(dir, options = {}) {
  const { match = () => true, excludeDir = () => false, onError = null } = options;

  const out = [];
  walkInto(dir);
  return out;

  /**
   * @param {string} current
   */
  function walkInto(current) {
    let entries;
    try {
      entries = fs.readdirSync(current, { withFileTypes: true });
    } catch (error) {
      if (onError) {
        onError(error, current);
      }
      return;
    }
    for (const entry of entries) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (!excludeDir(full, entry)) {
          walkInto(full);
        }
      } else if (entry.isFile()) {
        if (match(full, entry)) {
          out.push(full);
        }
      }
    }
  }
}

module.exports = {
  walkFiles
};
