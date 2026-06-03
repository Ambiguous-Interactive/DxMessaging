#!/usr/bin/env node
"use strict";

/**
 * yaml-line-length-guard.js
 *
 * Claude Code PostToolUse hook. When an agent edits a YAML file (Edit / Write /
 * MultiEdit), this guard auto-fixes the line-length violations that have a
 * provably-safe automated recovery and reports any residual ones back in-loop,
 * so the failure class never has to wait for the last-resort yamllint hook at
 * commit time.
 *
 * Contract:
 *   - Reads the PostToolUse JSON event from stdin.
 *   - Resolves `tool_input.file_path` (Claude Code always supplies an ABSOLUTE
 *     path). If it is not `*.yml`/`*.yaml`, or the file is missing, exit 0
 *     silently (fast early-exit for the common non-YAML case).
 *   - Reads the file FROM DISK (never trusts `tool_input.content`; an Edit only
 *     provides old_string/new_string, so disk is the single source of truth).
 *   - Runs the core-engine auto-fixers in WRITE mode (comment wrap + pwsh
 *     block-scalar rewrite) for zero-touch recovery, then recomputes remaining
 *     violations with `findLineLengthViolations`.
 *   - Emits a single JSON object on stdout (exit 0) with hookSpecificOutput.
 *     additionalContext that (i) tells the agent the file was reformatted (so it
 *     re-reads) and (ii) lists any REMAINING violations with line numbers plus
 *     the remediation pointer. If there is nothing to report, exit 0 with no
 *     output.
 *
 * Pure Node, no dependencies, no shell. Cross-platform (Linux/macOS/Windows):
 * it reuses scripts/lib/yaml-line-length.js (single source of truth) and never
 * spawns a child process.
 */

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("../lib/quote-parser");
const { isOutsideRelative } = require("../lib/path-classifier");
// Comment-wrap behavior is sourced from the lib (single source of truth) so the
// agentic guard cannot diverge from the commit-time CLI fixer.
const {
  resolveYamlLineLengthPolicy,
  findLineLengthViolations,
  rewriteYamlBlockScalarLines,
  wrapCommentLine,
  wrapYamlCommentLines
} = require("../lib/yaml-line-length");

const SKILL_POINTER = ".llm/skills/github-actions/yaml-line-length.md";

/**
 * True when a path looks like a YAML file by extension (case-insensitive).
 *
 * @param {string} candidate File path.
 * @returns {boolean} True for `*.yml` / `*.yaml`.
 */
function looksLikeYamlPath(candidate) {
  return typeof candidate === "string" && /\.ya?ml$/i.test(candidate);
}

/**
 * Wrap long `#` comment lines to the policy ceiling, sourcing the wrap behavior
 * from the lib (single source of truth) so this guard cannot diverge from the
 * commit-time CLI fixer. Adapts the lib's `{content, changedLines}` result to
 * this guard's historical `{content, changed}` shape.
 *
 * @param {string} content Full file content (any EOL).
 * @param {{max:number, allowNonBreakableWords:boolean}} policy Resolved policy.
 * @returns {{content:string, changed:boolean}} Wrapped content and whether any
 *   line changed.
 */
function rewriteCommentLines(content, policy) {
  const result = wrapYamlCommentLines(content, policy);
  return { content: result.content, changed: result.changedLines.length > 0 };
}

/**
 * Read stdin to completion as a UTF-8 string.
 *
 * @returns {Promise<string>} The full stdin payload.
 */
function readStdin() {
  return new Promise((resolve) => {
    let data = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => {
      data += chunk;
    });
    process.stdin.on("end", () => resolve(data));
    process.stdin.on("error", () => resolve(data));
  });
}

/**
 * Resolve the repository root for policy resolution. Prefers the Claude Code
 * project dir env var; falls back to the repo root relative to this script.
 *
 * @returns {string} Absolute repository root.
 */
function resolveRepoRoot() {
  if (process.env.CLAUDE_PROJECT_DIR && process.env.CLAUDE_PROJECT_DIR.trim().length > 0) {
    return process.env.CLAUDE_PROJECT_DIR;
  }
  return path.resolve(__dirname, "..", "..");
}

/**
 * Pure core: given a YAML file's content and the policy, run both auto-fixers in
 * write mode and compute the remaining violations. No filesystem access.
 *
 * @param {string} content Full file content (any EOL).
 * @param {{max:number, allowNonBreakableWords:boolean,
 *   allowNonBreakableInlineMappings:boolean}} policy Resolved policy.
 * @returns {{content:string, changed:boolean, remaining:Array<{line:number,
 *   length:number}>}} The reformatted content (LF), whether anything changed,
 *   and the violations that survived auto-fix.
 */
function guardContent(content, policy) {
  const commentResult = rewriteCommentLines(content, policy);
  const blockResult = rewriteYamlBlockScalarLines(commentResult.content, policy);
  const finalContent = normalizeToLf(blockResult.content);
  const changed = finalContent !== normalizeToLf(content);
  const remaining = findLineLengthViolations(finalContent, policy);
  return { content: finalContent, changed, remaining };
}

/**
 * Build the additionalContext message for the agent. Returns null when there is
 * nothing to report (no reformat, no remaining violations).
 *
 * @param {string} relativePath Repo-relative file path for display.
 * @param {boolean} changed Whether the guard reformatted the file.
 * @param {Array<{line:number, length:number}>} remaining Surviving violations.
 * @param {number} maxLength Policy max for the message.
 * @returns {string|null} The message, or null when silent.
 */
function buildContext(relativePath, changed, remaining, maxLength) {
  if (!changed && remaining.length === 0) {
    return null;
  }

  const parts = [];
  if (changed) {
    parts.push(
      `yaml-line-length-guard auto-reformatted ${relativePath} to the ` +
        `${maxLength}-char line-length policy (wrapped comments and/or rewrote ` +
        "long pwsh run block-scalar strings). Re-read the file before editing it again."
    );
  }

  if (remaining.length > 0) {
    const lineList = remaining.map((violation) => violation.line).join(", ");
    parts.push(
      `${relativePath} still has ${remaining.length} line(s) over ${maxLength} ` +
        `characters that could not be auto-fixed (line(s) ${lineList}). Shorten ` +
        `the line(s), or externalize the script per ${SKILL_POINTER}.`
    );
  }

  return parts.join(" ");
}

/**
 * Main hook entry. Reads the PostToolUse payload, applies the guard, writes any
 * reformatted content back to disk, and emits the agent-facing JSON. Always
 * exits 0 (a guard must not block tool use; it reports advisory context).
 *
 * @param {string} stdinPayload Raw PostToolUse JSON from stdin.
 * @returns {number} Process exit code (always 0).
 */
function run(stdinPayload) {
  let event;
  try {
    event = JSON.parse(stdinPayload);
  } catch (_error) {
    // Not a parseable payload: nothing actionable, stay silent.
    return 0;
  }

  const toolInput = event && event.tool_input ? event.tool_input : {};
  const filePath = toolInput.file_path;

  if (!looksLikeYamlPath(filePath)) {
    return 0;
  }

  let exists = false;
  try {
    exists = fs.statSync(filePath).isFile();
  } catch (_error) {
    exists = false;
  }
  if (!exists) {
    return 0;
  }

  let original;
  try {
    original = fs.readFileSync(filePath, "utf8");
  } catch (_error) {
    return 0;
  }

  const repoRoot = resolveRepoRoot();
  const policy = resolveYamlLineLengthPolicy(path.join(repoRoot, ".yamllint.yaml"));

  const result = guardContent(original, policy);

  if (result.changed) {
    try {
      fs.writeFileSync(filePath, result.content, "utf8");
    } catch (_error) {
      // If the write fails we still report remaining violations below; the
      // agent can re-run the fixers manually.
    }
  }

  const rawRel = path.relative(repoRoot, filePath);
  // Cross-drive-safe: fall back to the raw path when the file is the repo root
  // itself or lives outside the repo (on Windows cross-drive, `path.relative`
  // returns an absolute target, which `isOutsideRelative` detects via
  // `path.isAbsolute` -- a bare `startsWith("..")` would mislabel it as inside).
  let relativePath = rawRel.replace(/\\/g, "/");
  if (rawRel.length === 0 || isOutsideRelative(rawRel)) {
    relativePath = filePath;
  }

  const message = buildContext(relativePath, result.changed, result.remaining, policy.max);
  if (message === null) {
    return 0;
  }

  const output = {
    hookSpecificOutput: {
      hookEventName: "PostToolUse",
      additionalContext: message
    }
  };
  if (result.remaining.length > 0) {
    output.systemMessage = message;
  }

  process.stdout.write(`${JSON.stringify(output)}\n`);
  return 0;
}

module.exports = {
  looksLikeYamlPath,
  wrapCommentLine,
  rewriteCommentLines,
  guardContent,
  buildContext,
  resolveRepoRoot,
  run,
  SKILL_POINTER
};

if (require.main === module) {
  readStdin().then((payload) => {
    process.exit(run(payload));
  });
}
