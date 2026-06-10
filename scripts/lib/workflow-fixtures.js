"use strict";

/**
 * @fileoverview Shared GitHub Actions workflow-fixture scaffolding for the
 * `scripts/**` test suites.
 *
 * Consolidates the fixture boilerplate the workflow-validator oracle suites
 * repeat hundreds of times: the template-literal-to-lines conversion, the
 * single-job `jobs:` preamble, and the synthetic-workflow file write used by
 * `validateWorkflow` integration tests:
 *
 *   - `asLines`           - split a template-literal fixture into lines,
 *                           stripping exactly one leading newline.
 *   - `singleJobLines`    - line-array scaffold for a one-job workflow.
 *   - `singleJobWorkflow` - that scaffold joined into one content string.
 *   - `writeWorkflowFile` - write fixture content beneath a root directory,
 *                           creating parent directories as needed.
 *
 * Design rule (this is what makes folding existing literal fixtures onto the
 * builders provably byte-identical): the builders NEVER re-indent, quote,
 * escape, or otherwise transform caller content. `header`, `jobKeys`, and
 * `steps` lines are emitted verbatim, so callers pass fully-indented lines
 * exactly as they appear in the literal fixture being replaced.
 *
 * Deliberately excluded (they stay suite-local): step builders, multi-job
 * builders, runs-on label constants, and policy-specific parametric fixtures
 * such as `unityLockJob`/`gameCiLockJob` -- their shape is the subject under
 * test in the owning suites.
 *
 * This is test-support code (imported only from `scripts/**\/__tests__`), but
 * it lives under `scripts/lib` alongside the other shared libraries so these
 * names have one canonical home. `writeWorkflowFile` composes with
 * `jest-fixtures`' `makeTempDir`/`cleanupDir`; this module never creates or
 * removes temp directories itself. Dependencies are limited to the Node
 * built-ins `fs`/`path`, so the module stays a safe foundational dependency.
 */

const fs = require("fs");
const path = require("path");

/**
 * Split a template-literal workflow fixture into lines.
 *
 * Strips exactly ONE leading `"\n"` -- so fixtures can open their content on
 * the line after the backtick -- then splits on `"\n"`:
 *
 *   - two leading newlines leave one empty leading element;
 *   - a fixture ending in a newline before the closing backtick yields a
 *     trailing `""` element;
 *   - `"\r"` is NOT normalized (a CRLF fixture keeps `"\r"` at the end of
 *     each line).
 *
 * Byte-identical hoist of the local helper formerly defined in
 * `scripts/__tests__/validate-workflows-concurrency-and-labels.test.js`.
 *
 * @param {string} text Template-literal fixture text.
 * @returns {string[]} The fixture's lines.
 */
function asLines(text) {
  // Strip a single leading blank line so test fixtures can start with `\n`.
  const trimmed = text.replace(/^\n/, "");
  return trimmed.split("\n");
}

/**
 * Build the line array for a workflow declaring exactly one job.
 *
 * Returns a fresh array on every call (never a cached or shared reference)
 * and never mutates its inputs:
 *
 *   [...header, "jobs:", `  ${id}:`, `    runs-on: ${runsOn}`,
 *    ...jobKeys, "    steps:", ...steps]
 *
 * `header` lines precede `jobs:` (e.g. `name:`/`on:`/`permissions:` blocks),
 * `jobKeys` lines sit between `runs-on:` and `steps:` (e.g. `concurrency:` or
 * `strategy:` blocks), and `steps` lines follow `steps:`. All three are
 * emitted verbatim -- the builder never re-indents or quotes them -- so
 * callers supply fully-indented lines (job keys at 4 spaces, step lines at 6).
 *
 * @param {string} id Job id, emitted as `  ${id}:`.
 * @param {string[]} steps Fully-indented step lines emitted after `    steps:`.
 *   Pass `[]` for a scaffold that ends at the bare `    steps:` line.
 * @param {{ runsOn?: string, header?: string[], jobKeys?: string[] }} [options]
 *   `runsOn` (default `"ubuntu-latest"`) is the verbatim `runs-on:` value
 *   (inline label arrays like `"[self-hosted, Windows]"` work unchanged).
 *   `header` (default `[]`) and `jobKeys` (default `[]`) are verbatim lines.
 * @returns {string[]} A fresh array of workflow lines.
 * @throws {TypeError} When `steps` is not an array.
 */
function singleJobLines(id, steps, options = {}) {
  if (!Array.isArray(steps)) {
    throw new TypeError(
      `singleJobLines expected steps to be an array of step lines, got ${typeof steps}`
    );
  }
  const { runsOn = "ubuntu-latest", header = [], jobKeys = [] } = options;
  return [
    ...header,
    "jobs:",
    `  ${id}:`,
    `    runs-on: ${runsOn}`,
    ...jobKeys,
    "    steps:",
    ...steps
  ];
}

/**
 * Build a single-job workflow as one content string.
 *
 * Exactly `singleJobLines(id, steps, options).join("\n")`: embedded `"\n"`
 * separators and NO trailing newline (matching the literal content strings the
 * oracle suites previously built by hand).
 *
 * @param {string} id Job id, emitted as `  ${id}:`.
 * @param {string[]} steps Fully-indented step lines (see {@link singleJobLines}).
 * @param {{ runsOn?: string, header?: string[], jobKeys?: string[] }} [options]
 *   Same options as {@link singleJobLines}.
 * @returns {string} The workflow content with no trailing newline.
 * @throws {TypeError} When `steps` is not an array.
 */
function singleJobWorkflow(id, steps, options) {
  return singleJobLines(id, steps, options).join("\n");
}

/**
 * Write workflow fixture content beneath `rootDir` and return the file's
 * absolute path.
 *
 * An array `content` is joined with `"\n"` (no trailing newline appended); a
 * string `content` is written verbatim (no `"\r"`/EOL normalization, nothing
 * appended). Parent directories of `relPath` are created recursively. The
 * caller owns `rootDir` -- compose with `jest-fixtures`' `makeTempDir` and
 * `cleanupDir`; this helper never creates or removes temp directories itself.
 *
 * @param {string} rootDir Directory the relative path is resolved against.
 * @param {string} relPath Path of the file under `rootDir`,
 *   e.g. `".github/workflows/test.yml"`. Must be relative: an absolute path
 *   would silently escape `rootDir` (and its cleanup), so it is rejected.
 * @param {string[] | string} content Lines to join with `"\n"`, or the exact
 *   file content.
 * @returns {string} Absolute path of the written file.
 * @throws {TypeError} When `relPath` is absolute.
 */
function writeWorkflowFile(rootDir, relPath, content) {
  if (path.isAbsolute(relPath)) {
    throw new TypeError(
      `writeWorkflowFile expects a relative path under rootDir; got absolute "${relPath}"`
    );
  }
  const absPath = path.resolve(rootDir, relPath);
  const data = Array.isArray(content) ? content.join("\n") : content;
  fs.mkdirSync(path.dirname(absPath), { recursive: true });
  fs.writeFileSync(absPath, data, "utf8");
  return absPath;
}

module.exports = {
  asLines,
  singleJobLines,
  singleJobWorkflow,
  writeWorkflowFile
};
