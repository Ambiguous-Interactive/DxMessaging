"use strict";

const fs = require("fs");
const path = require("path");

/**
 * Recursively walk `dir`, returning files that match. Missing or unreadable
 * directories call `onError` when provided and otherwise return no files.
 * @param {{
 *   match?: (fullPath: string, dirent: import("fs").Dirent) => boolean,
 *   excludeDir?: (fullPath: string, dirent: import("fs").Dirent) => boolean,
 *   onError?: ((error: NodeJS.ErrnoException, dir: string) => void) | null
 * }} [options]
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
