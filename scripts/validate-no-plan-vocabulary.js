#!/usr/bin/env node
/**
 * @fileoverview Fails CI if shipping content references the project's
 * internal planning vocabulary.
 *
 * The project tracks long-running work in PLAN.md files and uses milestone
 * tags of the shape `T<number>.<number>` and `P<number>.<number>` plus
 * "Phase P<n>" / "Tier T<n>" headings. None of that vocabulary is meant for
 * users; if it leaks into shipping content the user-facing surface starts
 * to look like a project tracker. This validator enforces the boundary.
 *
 * In-scope content (only):
 *   - Runtime/, Editor/, SourceGenerators/  *.cs files (non-test)
 *   - Samples~/                              *.cs files
 *   - docs/                                  *.md files
 *   - README.md, CHANGELOG.md, CONTRIBUTING.md, "Third Party Notices.md"
 *   - llms.txt
 *
 * Tests, build outputs, and project-internal docs (PLAN.md / PERF-PLAN.md /
 * OLD-PLAN.md / GH-PAGES-PLAN.md, scripts/, .llm/, .github/) are explicitly
 * out of scope: those are where the planning vocabulary lives by design.
 *
 * Forbidden patterns:
 *   1. Filename references: PLAN.md, PERF-PLAN.md, OLD-PLAN.md, GH-PAGES-PLAN.md
 *      (case-sensitive; the names of the actual planning files in the repo).
 *   2. Tier tags: T<1-2 digits>.<1-2 digits> and P<1-2 digits>.<1-2 digits>
 *      (case-sensitive). Bare T1 / P0 are NOT forbidden because Mermaid
 *      diagram node IDs and test method names use them legitimately. The
 *      digit-count cap also keeps unrelated quantities like "T22.5 degrees"
 *      out of the match.
 *   3. Plan-section headings: lines starting with `# Phase P<n>` or
 *      `# Tier T<n>` (any heading depth). Migration-guide style
 *      "Phase 0/1/2/3" headings without the `P` prefix are intentionally
 *      allowed; see docs/guides/migration-guide.md.
 *
 * Markdown code-fence handling (m2):
 *   Documentation must be able to show "what NOT to do" inside code blocks.
 *   The scanner detects fenced code blocks (lines starting with ``` ` ``` or
 *   ` ``` ` plus the closing fence) and skips everything between fences.
 *   Inline code spans on a single line are NOT specially handled; if a line
 *   that contains a violation is also entirely an inline code span, the
 *   violation still fires by design.
 *
 * Allowlist:
 *   The validator itself and its test must STATE the patterns to enforce
 *   them, so they are excluded by exact path. New legitimate exceptions
 *   require an explicit edit of `ALLOWLIST` plus a comment justifying it.
 *
 * Exit codes:
 *   0  No violations.
 *   1  Violations found (or unrecoverable error).
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnPlatformCommandSync } = require("./lib/shell-command");
const { parseArgs: parseCliArgs } = require("./lib/cli-options");

const REPO_ROOT = path.resolve(__dirname, "..");

// In-scope file patterns (matched against repo-relative POSIX paths).
const INCLUDE_PATTERNS = [
  "Runtime/**/*.cs",
  "Editor/**/*.cs",
  "SourceGenerators/**/*.cs",
  "Samples~/**/*.cs",
  "docs/**/*.md",
  "README.md",
  "CHANGELOG.md",
  "CONTRIBUTING.md",
  "Third Party Notices.md",
  "llms.txt"
];

// Patterns whose match means "out of scope even if INCLUDE_PATTERNS matched".
// Test source trees are excluded because tests reference plan vocabulary in
// fixtures and method names.
const EXCLUDE_PATTERNS = [
  "**/Tests/**",
  "SourceGenerators/**/*.Tests/**",
  "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests/**"
];

// Files where the patterns must be STATED to be enforced. New additions must
// include a comment justifying the exception.
const ALLOWLIST = new Set([
  // The validator script lists every forbidden pattern in its source.
  "scripts/validate-no-plan-vocabulary.js",
  // The test exercises every forbidden pattern in fixtures.
  "scripts/__tests__/validate-no-plan-vocabulary.test.js"
]);

/**
 * Pattern definitions. `regex` is a global, multi-line regex used for
 * line-by-line scanning; `name` is the diagnostic label.
 *
 * Patterns are intentionally string-literal so this file's own scan does
 * not match the patterns inside its own source. (The validator script is
 * additionally on the ALLOWLIST as defense in depth.)
 */
const PATTERNS = [
  {
    name: "plan-filename",
    // PLAN.md / PERF-PLAN.md / OLD-PLAN.md / GH-PAGES-PLAN.md
    // The leading boundary excludes things like SOMEPLAN.md that aren't real
    // filename refs.
    regex: /\b(?:PLAN|PERF-PLAN|OLD-PLAN|GH-PAGES-PLAN)\.md\b/g
  },
  {
    name: "tier-tag",
    // T<1-2 digits>.<1-2 digits> and P<1-2 digits>.<1-2 digits> milestone
    // tags. The 1-2-digit cap keeps unrelated quantities like "T22.5 degrees"
    // out of the match (m1). The `\b` boundary keeps it out of identifiers.
    regex: /\b[TP][0-9]{1,2}\.[0-9]{1,2}\b/g
  },
  {
    name: "plan-section-heading",
    // `^` is line-start because we run with the `m` flag. Matches headings
    // like "## Phase P0 - Setup" or "### Tier T2: rollout". The bare
    // "Phase 0/1/2/3" form (no `P` prefix) is deliberately allowed.
    regex: /^#+\s+(?:Phase\s+P[0-9]+|Tier\s+T[0-9]+)\b/gm
  }
];

/**
 * Compile a glob pattern into a `RegExp`. Supports `*` (matches a single path
 * segment without separators) and `**` (matches across separators including
 * empty segments). The matcher is intentionally minimal so the in-scope
 * patterns we use (`Runtime/**\/*.cs`, `**\/Tests/**`, `*.md`) Just Work
 * without pulling in `minimatch` as a dependency.
 *
 * @param {string} pattern
 * @returns {RegExp}
 */
function compileGlob(pattern) {
  const specials = /[.+?^${}()|[\]\\]/g;
  let regex = "";
  let i = 0;
  while (i < pattern.length) {
    const ch = pattern[i];
    if (ch === "*") {
      if (pattern[i + 1] === "*") {
        // `**/` matches zero or more leading path segments. Standalone `**`
        // matches across separators including empty.
        if (pattern[i + 2] === "/") {
          regex += "(?:.*/)?";
          i += 3;
        } else {
          regex += ".*";
          i += 2;
        }
        continue;
      }
      regex += "[^/]*";
      i += 1;
      continue;
    }
    if (ch === "?") {
      regex += "[^/]";
      i += 1;
      continue;
    }
    regex += ch.replace(specials, "\\$&");
    i += 1;
  }
  return new RegExp(`^${regex}$`);
}

/**
 * Strip a UTF-8 BOM at the start of a string.
 *
 * @param {string} content
 * @returns {string}
 */
function stripBom(content) {
  if (typeof content !== "string") {
    return "";
  }
  return content.charCodeAt(0) === 0xfeff ? content.slice(1) : content;
}

/**
 * Normalize line endings so line-number coordinates are consistent across
 * Windows-authored and POSIX-authored files.
 *
 * @param {string} content
 * @returns {string}
 */
function normalizeLineEndings(content) {
  return stripBom(content).replace(/\r\n/g, "\n").replace(/\r/g, "\n");
}

/**
 * Convert a Windows or mixed-separator path to a POSIX form so glob matching
 * is deterministic.
 *
 * @param {string} value
 * @returns {string}
 */
function toPosixPath(value) {
  return String(value || "")
    .split(path.sep)
    .join("/")
    .replace(/\\/g, "/");
}

/**
 * Decide whether a repo-relative path is in scope for scanning.
 *
 * @param {string} relativePath
 * @returns {boolean}
 */
function isInScope(relativePath) {
  const posix = toPosixPath(relativePath);
  if (!INCLUDE_PATTERNS.some((pattern) => compileGlob(pattern).test(posix))) {
    return false;
  }
  if (EXCLUDE_PATTERNS.some((pattern) => compileGlob(pattern).test(posix))) {
    return false;
  }
  return true;
}

/**
 * Decide whether a repo-relative path is on the allowlist (forbidden patterns
 * may legitimately appear there).
 *
 * @param {string} relativePath
 * @returns {boolean}
 */
function isAllowlisted(relativePath) {
  return ALLOWLIST.has(toPosixPath(relativePath));
}

/**
 * Replace every line that lies inside a fenced markdown code block with a
 * blank line of equal length. Preserves line numbers so violation
 * coordinates outside the fence remain accurate.
 *
 * Recognized fences:
 *   - Triple backtick: ``` followed by an optional language token
 *   - Triple tilde:    ~~~ followed by an optional language token
 *
 * The opening and closing fence lines themselves are blanked because a
 * violating identifier in a fence info-string would otherwise leak through.
 *
 * @param {string} content - LF-normalized file content
 * @returns {string}
 */
function maskCodeFences(content) {
  if (typeof content !== "string" || content.length === 0) {
    return content || "";
  }
  const lines = content.split("\n");
  const fenceRegex = /^\s*(?:`{3,}|~{3,})/;
  let inFence = false;
  let openFence = null; // The fence character we're matching against.

  for (let i = 0; i < lines.length; i++) {
    const trimmed = lines[i].trim();
    if (!inFence) {
      if (fenceRegex.test(lines[i])) {
        inFence = true;
        openFence = trimmed.startsWith("`") ? "`" : "~";
        // Blank the opening fence so a forbidden token in the info-string
        // (e.g. ```` ```PLAN.md ```` ) does not slip past.
        lines[i] = "";
      }
    } else {
      // Inside a fence. The fence closes on a line consisting only of the
      // same fence character (3 or more), optionally indented.
      const closingRegex = openFence === "`" ? /^\s*`{3,}\s*$/ : /^\s*~{3,}\s*$/;
      if (closingRegex.test(lines[i])) {
        inFence = false;
        openFence = null;
        lines[i] = "";
      } else {
        // Blank the line content while preserving the line position.
        lines[i] = "";
      }
    }
  }
  return lines.join("\n");
}

/**
 * Scan a single string buffer against the forbidden patterns.
 *
 * @param {string} relativePath - Repo-relative path used in diagnostics
 * @param {string} content      - File contents to scan
 * @returns {Array<{file: string, line: number, column: number, pattern: string, match: string}>}
 */
function scanContent(relativePath, content) {
  if (typeof content !== "string" || content.length === 0) {
    return [];
  }

  const normalized = normalizeLineEndings(content);
  // Mask out fenced code blocks so docs can show "what NOT to do" without
  // tripping the validator (m2).
  const masked = maskCodeFences(normalized);

  // Pre-compute a line-start index so a regex match offset can be converted
  // to (line, column) without re-scanning the buffer for every match.
  const lineStarts = [0];
  for (let i = 0; i < masked.length; i++) {
    if (masked.charCodeAt(i) === 0x0a) {
      lineStarts.push(i + 1);
    }
  }

  function offsetToLineColumn(offset) {
    // Binary search for the largest line-start index that does not exceed
    // the match offset; that line is where the match begins.
    let lo = 0;
    let hi = lineStarts.length - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >>> 1;
      if (lineStarts[mid] <= offset) {
        lo = mid;
      } else {
        hi = mid - 1;
      }
    }
    return { line: lo + 1, column: offset - lineStarts[lo] + 1 };
  }

  const violations = [];
  for (const { name, regex } of PATTERNS) {
    // Reset lastIndex defensively in case the same regex is reused across
    // calls (we instantiate fresh objects in PATTERNS, but better safe).
    regex.lastIndex = 0;
    let match;
    while ((match = regex.exec(masked)) !== null) {
      const { line, column } = offsetToLineColumn(match.index);
      violations.push({
        file: relativePath,
        line,
        column,
        pattern: name,
        match: match[0]
      });
      // Avoid infinite loops on zero-length matches (none of our patterns
      // actually produce empty matches today, but defense in depth).
      if (match.index === regex.lastIndex) {
        regex.lastIndex += 1;
      }
    }
  }

  // Sort by (line, column) so the output is deterministic regardless of the
  // order patterns are evaluated.
  violations.sort((a, b) => a.line - b.line || a.column - b.column);
  return violations;
}

/**
 * Enumerate the in-scope tracked files via `git ls-files`. Falls through to
 * a hard error if git is unavailable; the project's policy forbids silent
 * permissive defaults when git metadata is missing.
 *
 * @param {object} [options]
 * @param {string} [options.cwd]
 * @param {Function} [options.spawn]
 * @returns {{ok: true, files: string[]} | {ok: false, type: string, message: string}}
 */
function listTrackedFiles(options = {}) {
  const cwd = options.cwd || REPO_ROOT;
  const spawn = options.spawn || spawnPlatformCommandSync;

  const result = spawn("git", ["ls-files"], {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });

  if (result && result.error) {
    if (result.error.code === "ENOENT") {
      return {
        ok: false,
        type: "git-not-installed",
        message: "git was not found on PATH; cannot enumerate tracked shipping files."
      };
    }
    return {
      ok: false,
      type: "git-spawn-error",
      message: `Failed to spawn git: ${result.error.message || result.error}`
    };
  }

  if (!result || typeof result.status !== "number" || result.status !== 0) {
    const stderr = (result && typeof result.stderr === "string" && result.stderr) || "";
    return {
      ok: false,
      type: "git-exit-error",
      message: `git ls-files exited with status ${result && result.status}: ${stderr.trim() || "no stderr"}`
    };
  }

  const stdout = typeof result.stdout === "string" ? result.stdout : "";
  const files = stdout
    .replace(/\r\n/g, "\n")
    .replace(/\r/g, "\n")
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);

  return { ok: true, files };
}

/**
 * Filter tracked files to the in-scope, non-allowlisted set.
 *
 * @param {string[]} trackedFiles
 * @returns {string[]}
 */
function filterInScopeFiles(trackedFiles) {
  return trackedFiles.filter((file) => isInScope(file) && !isAllowlisted(file));
}

/**
 * Run the validator against the real working tree (or any tree provided via
 * `options.cwd`).
 *
 * @param {object} [options]
 * @param {string} [options.cwd]
 * @param {Function} [options.spawn]
 * @param {Function} [options.readFile] - Inject a reader for tests
 * @returns {{
 *   valid: boolean,
 *   errors: Array<{type: string, message: string}>,
 *   violations: ReturnType<typeof scanContent>,
 *   scannedFiles: string[]
 * }}
 */
function run(options = {}) {
  const cwd = options.cwd || REPO_ROOT;
  const readFile = options.readFile || ((file) => fs.readFileSync(path.join(cwd, file), "utf8"));

  const list = listTrackedFiles({ cwd, spawn: options.spawn });
  if (!list.ok) {
    return {
      valid: false,
      errors: [{ type: list.type, message: list.message }],
      violations: [],
      scannedFiles: []
    };
  }

  const scannedFiles = filterInScopeFiles(list.files);
  const violations = [];

  for (const file of scannedFiles) {
    let content;
    try {
      content = readFile(file);
    } catch (error) {
      return {
        valid: false,
        errors: [
          {
            type: "read-error",
            message: `Unable to read ${file}: ${(error && error.message) || error}`
          }
        ],
        violations,
        scannedFiles
      };
    }
    violations.push(...scanContent(file, content));
  }

  return {
    valid: violations.length === 0,
    errors: [],
    violations,
    scannedFiles
  };
}

/**
 * Pretty-print the run result. Returns the process exit code.
 *
 * @param {ReturnType<typeof run>} result
 * @param {{logger?: typeof console}} [options]
 * @returns {number}
 */
function reportResult(result, options = {}) {
  const logger = options.logger || console;

  if (result.errors.length > 0) {
    logger.log("validate-no-plan-vocabulary: FAILED");
    for (const error of result.errors) {
      logger.log(`  - [${error.type}] ${error.message}`);
    }
    return 1;
  }

  if (result.valid) {
    logger.log(
      `validate-no-plan-vocabulary: OK (${result.scannedFiles.length} files scanned, no violations)`
    );
    return 0;
  }

  logger.log(`validate-no-plan-vocabulary: FAILED (${result.violations.length} violation(s))`);
  for (const violation of result.violations) {
    logger.log(
      `  ${violation.file}:${violation.line}:${violation.column}: ${violation.pattern}: ${violation.match}`
    );
  }
  logger.log(
    "Remediation: shipping content must not reference internal planning " +
      "vocabulary. Replace plan filenames with stable user docs, replace " +
      "milestone tags with descriptive prose, and replace plan-section " +
      "headings with user-facing section titles."
  );
  return 1;
}

/**
 * Parse CLI arguments.
 *
 * @param {string[]} argv
 * @returns {{listFiles: boolean, help: boolean, errors: string[]}}
 */
function parseArgs(argv) {
  // This parser accepts only flags; every other token (a bare word, a lone `-`,
  // or `--`) was reported as an unknown argument by the original loop, in argv
  // order. Collecting all non-flag tokens as positionals (separator and `=` form
  // disabled) and mapping them to that same error in order reproduces it exactly.
  const { values, positionals } = parseCliArgs(argv, {
    options: {
      listFiles: { type: "boolean", aliases: ["--list-files"] },
      help: { type: "boolean", aliases: ["--help", "-h"] }
    },
    allowEquals: false,
    endOfOptions: false,
    unknownOption: "collect"
  });
  const errors = positionals.map((extra) => `Unknown argument: ${extra}`);
  return { listFiles: values.listFiles, help: values.help, errors };
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    console.log(
      "Usage: node scripts/validate-no-plan-vocabulary.js [--list-files]\n" +
        "  --list-files  Print the in-scope file list and exit (debugging)."
    );
    return 0;
  }
  if (args.errors.length > 0) {
    for (const message of args.errors) {
      console.error(message);
    }
    return 1;
  }

  if (args.listFiles) {
    const list = listTrackedFiles();
    if (!list.ok) {
      console.error(`[${list.type}] ${list.message}`);
      return 1;
    }
    for (const file of filterInScopeFiles(list.files)) {
      console.log(file);
    }
    return 0;
  }

  const result = run();
  return reportResult(result);
}

if (require.main === module) {
  process.exitCode = main();
}

module.exports = {
  REPO_ROOT,
  INCLUDE_PATTERNS,
  EXCLUDE_PATTERNS,
  ALLOWLIST,
  PATTERNS,
  compileGlob,
  stripBom,
  normalizeLineEndings,
  toPosixPath,
  isInScope,
  isAllowlisted,
  maskCodeFences,
  scanContent,
  listTrackedFiles,
  filterInScopeFiles,
  run,
  reportResult,
  parseArgs,
  main
};
