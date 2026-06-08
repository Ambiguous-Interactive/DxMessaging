"use strict";

/**
 * @fileoverview Shared repository file-discovery and text-reading helpers.
 *
 * Consolidates five patterns that were copy-pasted across dozens of scripts
 * and tests:
 *
 *   - `readUtf8`      - read a file as UTF-8, LF-normalized, BOM-stripped.
 *   - `lineNumberAt`  - 1-based line number of a character offset in text.
 *   - `walkFiles`     - recursive directory walk returning matching files.
 *   - `toRepoRelative`- POSIX repo-relative form of a path (log-friendly).
 *   - `listTrackedFiles` - `git ls-files` wrapper returning tracked paths.
 *
 * Before this module each of these lived as a near-identical local helper in
 * many files (`readUtf8` alone had a dozen-plus copies with subtly different
 * BOM/EOL handling). Centralizing them removes the duplication, pins one
 * canonical behavior under test, and gives the JS size-budget gate a single
 * allowlisted home for these names.
 *
 * Dependencies are limited to Node built-ins plus two existing shared libs
 * (`quote-parser` for `normalizeToLf`, `path-classifier` for POSIX/relative
 * path logic) so this module stays a safe foundational dependency.
 */

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const { normalizeToLf } = require("./quote-parser");
const { toPosixPath, isOutsideRelative } = require("./path-classifier");

/**
 * Absolute path to the repository root (two levels above `scripts/lib`).
 * @type {string}
 */
const REPO_ROOT = path.resolve(__dirname, "..", "..");

/**
 * Read a file as UTF-8 text with canonical normalization.
 *
 * By default the result is LF-normalized (CRLF/CR -> LF) and has a leading
 * UTF-8 BOM stripped, which is what every line-based scanner in this repo
 * wants. Both behaviors can be disabled for the rare caller that needs the
 * raw bytes-as-text.
 *
 * @param {string} absPath Path to the file (absolute or cwd-relative).
 * @param {{ normalizeEol?: boolean, stripBom?: boolean }} [options]
 *   `normalizeEol` (default true) converts CRLF/CR to LF.
 *   `stripBom` (default true) removes a single leading U+FEFF.
 * @returns {string} The file contents as text.
 */
function readUtf8(absPath, options = {}) {
  const { normalizeEol = true, stripBom = true } = options;
  let text = fs.readFileSync(absPath, "utf8");
  if (normalizeEol) {
    text = normalizeToLf(text);
  }
  if (stripBom) {
    text = text.replace(/^\uFEFF/, "");
  }
  return text;
}

/**
 * 1-based line number of a character offset within `text`.
 *
 * Counts the `\n` characters in `text[0..index)`. A negative `index` is
 * clamped to 0 (line 1) so an offset derived from a failed `indexOf` never
 * counts from the end of the string. Callers that need column-accurate
 * reporting should LF-normalize first (see {@link readUtf8}); this helper
 * counts only `\n` so a stray `\r\n` would otherwise read as one line.
 *
 * @param {string} text The full source text.
 * @param {number} index Zero-based character offset.
 * @returns {number} The 1-based line number containing `index`.
 */
function lineNumberAt(text, index) {
  return text.slice(0, Math.max(0, index)).split("\n").length;
}

/**
 * Recursively walk `dir`, returning absolute paths of files that match.
 *
 * This subsumes the several bespoke `walk` helpers that differed only in how
 * they filtered files, excluded directories, and handled unreadable dirs:
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

/**
 * POSIX repo-relative form of a path, suitable for log lines.
 *
 * Relative inputs are first resolved against `cwd`. Paths inside `repoRoot`
 * become a forward-slash relative path; paths outside the repo (including the
 * absolute target `path.relative` yields on Windows for cross-drive inputs)
 * fall back to the POSIX-normalized absolute form, so log scrapers never see
 * backslashes. Non-string inputs are returned unchanged.
 *
 * @param {string} targetPath Path to convert.
 * @param {{ repoRoot?: string, cwd?: string }} [options]
 *   `repoRoot` defaults to {@link REPO_ROOT}; `cwd` defaults to
 *   `process.cwd()` and is used only to resolve relative inputs.
 * @returns {string} POSIX repo-relative path, or POSIX-absolute fallback.
 */
function toRepoRelative(targetPath, options = {}) {
  if (typeof targetPath !== "string") {
    return targetPath;
  }
  const { repoRoot = REPO_ROOT, cwd = process.cwd() } = options;
  const abs = path.isAbsolute(targetPath) ? targetPath : path.resolve(cwd, targetPath);
  const rel = path.relative(repoRoot, abs);
  if (rel === "" || isOutsideRelative(rel)) {
    return toPosixPath(abs);
  }
  return toPosixPath(rel);
}

/**
 * List repository files via `git ls-files`.
 *
 * Returns tracked paths by default; pass `extraArgs` for the less common
 * path-listing forms (e.g. `["--others", "--exclude-standard"]` for
 * untracked-not-ignored paths). `extraArgs` must keep the output a plain list
 * of NUL-delimited paths: flags like `--eol` that prepend attribute columns
 * break the path contract and are not supported here. Paths are read NUL-
 * delimited (`-z`) so filenames containing newlines are handled correctly.
 *
 * Missing `git` or a non-zero exit throws, per the repository convention that
 * git-metadata failures are hard errors rather than a silent permissive
 * default.
 *
 * @param {string[]} [patterns] Pathspecs passed after `--`. Empty means all.
 * @param {{ repoRoot?: string, cwd?: string, extraArgs?: string[] }} [options]
 *   `cwd` defaults to `repoRoot` (default {@link REPO_ROOT}). `extraArgs` are
 *   inserted before the `--` pathspec separator.
 * @returns {string[]} Repo-relative (POSIX, as git emits) file paths.
 */
function listTrackedFiles(patterns = [], options = {}) {
  const { repoRoot = REPO_ROOT, cwd = repoRoot, extraArgs = [] } = options;
  const args = ["ls-files", "-z", ...extraArgs];
  if (patterns.length > 0) {
    args.push("--", ...patterns);
  }
  const result = childProcess.spawnSync("git", args, {
    cwd,
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024
  });
  if (result.error) {
    throw new Error(`Failed to run "git ls-files": ${result.error.message}`);
  }
  if (result.status !== 0) {
    const stderr = (result.stderr || "").trim();
    const how =
      result.signal !== null && result.signal !== undefined
        ? `was terminated by signal ${result.signal}`
        : `exited with status ${result.status}`;
    throw new Error(`"git ls-files" ${how}` + (stderr ? `: ${stderr}` : ""));
  }
  return result.stdout.split("\0").filter((entry) => entry.length > 0);
}

module.exports = {
  REPO_ROOT,
  readUtf8,
  lineNumberAt,
  walkFiles,
  toRepoRelative,
  listTrackedFiles
};
