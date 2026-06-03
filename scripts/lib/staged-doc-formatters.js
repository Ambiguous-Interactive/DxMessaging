"use strict";

/**
 * Shared formatters for the staged-doc validator output. Both
 * `scripts/run-staged-validators.js` and `scripts/run-staged-md-pipeline.js`
 * surface violations from the same three validators (validate-docs-ascii,
 * validate-doc-code-patterns, validate-docs-prose) and produce identical
 * line-oriented reports. Most formatters return a single line; the code-
 * pattern formatter intentionally returns a three-line block
 * (summary + why + fix). The formatters live here so the runners cannot
 * drift in their output shape -- a regression there silently breaks
 * IDE problem matchers and CI annotation parsers.
 *
 * Each format function takes a violation object plus a `toRepoRelative`
 * function so the runners can supply their own root-relative path
 * computation (the runners share an exclusion set but each owns its own
 * ROOT_DIR resolution).
 */

const path = require("path");

function defaultToRepoRelative(absPath) {
  return absPath.split(path.sep).join("/");
}

function escapeAsciiChar(char) {
  return String(char)
    .replace(/\\/g, "\\\\")
    .replace(/'/g, "\\'")
    .replace(/\r/g, "\\r")
    .replace(/\n/g, "\\n")
    .replace(/\t/g, "\\t");
}

function formatCodepointGlyph(char, codepoint) {
  const cpHex = codepoint.toString(16).toUpperCase().padStart(4, "0");
  if (codepoint >= 0x20 && codepoint <= 0x7e) {
    return `'${escapeAsciiChar(char)}'`;
  }
  return `\\u{${cpHex}}`;
}

function formatAsciiViolation(v, toRepoRelative = defaultToRepoRelative) {
  const rel = toRepoRelative(v.file);
  const cpHex = v.codepoint.toString(16).toUpperCase().padStart(4, "0");
  return `${rel}:${v.line}:${v.column}: U+${cpHex} ${formatCodepointGlyph(
    v.char,
    v.codepoint
  )} -- ${v.reason}`;
}

function formatAsciiWarning(w, toRepoRelative = defaultToRepoRelative) {
  const rel = toRepoRelative(w.file);
  return `WARN ${rel}: ${w.count} emoji(s) found, soft cap is ${w.cap}.`;
}

function formatCodePatternViolation(v, toRepoRelative = defaultToRepoRelative) {
  const rel = toRepoRelative(v.file);
  return [
    `${rel}:${v.line}:${v.column}: [${v.id}] ${v.sample}`,
    `    why: ${v.why}`,
    `    fix: ${v.fix}`
  ].join("\n");
}

function formatProseViolation(v, toRepoRelative = defaultToRepoRelative) {
  const rel = toRepoRelative(v.file);
  const term = v.term ? ` ${v.term}` : "";
  return `${rel}:${v.line}:${v.column}: [${v.rule}]${term}`;
}

module.exports = {
  formatCodepointGlyph,
  formatAsciiViolation,
  formatAsciiWarning,
  formatCodePatternViolation,
  formatProseViolation
};
