#!/usr/bin/env node
/**
 * run-staged-validators.js
 *
 * Single Node process that runs the three per-file documentation validators
 * (validate-docs-ascii, validate-doc-code-patterns, validate-docs-prose)
 * against the staged file list passed via argv. Consolidating these three
 * pre-commit hooks into one process eliminates ~2 Node spawns (~600-1200 ms
 * on Windows) per commit and matches the same exclusion rules that the
 * separate `.pre-commit-config.yaml` blocks used to apply.
 *
 * The individual validator scripts remain functional as standalone CLIs so
 * CI workflows and ad-hoc invocations are unchanged. This script only
 * `require()`s their public scanFile/scanContent surface.
 *
 * Exit codes:
 *   0  No violations across any validator, or argv was empty.
 *   1  At least one validator reported a violation.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const ROOT_DIR = path.resolve(__dirname, "..");

const ascii = require("./validate-docs-ascii");
const codePatterns = require("./validate-doc-code-patterns");
const prose = require("./validate-docs-prose");
const sharedFormatters = require("./lib/staged-doc-formatters");
const { toRepoRelative } = require("./lib/repo-files");

// Mirror the YAML-level files: '\.(md|cs)$' filter.
const ALLOWED_EXTS = new Set([".md", ".markdown", ".cs"]);

// Mirror the YAML-level
//   exclude: "^(Library/|Temp/|node_modules/|obj/|bin/|.*/(bin|obj)/)"
// from the per-validator hook blocks. These directories are checked at the
// repo-relative path level (forward slashes only).
const EXCLUDE_PREFIXES = ["Library/", "Temp/", "node_modules/", "obj/", "bin/"];
const EXCLUDE_SEGMENT_RE = /(?:^|\/)(?:bin|obj)\//;

function isExcluded(repoRelPath) {
  for (const prefix of EXCLUDE_PREFIXES) {
    if (repoRelPath.startsWith(prefix)) return true;
  }
  if (EXCLUDE_SEGMENT_RE.test(repoRelPath)) return true;
  return false;
}

function isApplicable(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  if (!ALLOWED_EXTS.has(ext)) return false;
  const rel = toRepoRelative(filePath);
  if (isExcluded(rel)) return false;
  return true;
}

function readFileSafe(filePath) {
  try {
    return fs.readFileSync(filePath, "utf8");
  } catch (error) {
    return null;
  }
}

function runAscii(absPath, content) {
  const isCsharp = absPath.endsWith(".cs");
  const { violations, warning } = ascii.scanContent(absPath, content, isCsharp);
  return { violations, warnings: warning ? [warning] : [] };
}

function runCodePatterns(absPath, content) {
  const violations = [];
  if (absPath.endsWith(".md") || absPath.endsWith(".markdown")) {
    codePatterns.scanMarkdown(absPath, content, violations);
  } else if (absPath.endsWith(".cs")) {
    codePatterns.scanCSharp(absPath, content, violations);
  }
  return { violations, warnings: [] };
}

function runProse(absPath, content) {
  const { violations } = prose.scanContent(absPath, content);
  return { violations, warnings: [] };
}

const VALIDATORS = [
  { id: "validate-docs-ascii", run: runAscii },
  { id: "validate-doc-code-patterns", run: runCodePatterns },
  { id: "validate-docs-prose", run: runProse }
];

// The four format functions are shared with run-staged-md-pipeline.js via
// scripts/lib/staged-doc-formatters.js so the two runners cannot drift in
// the per-line output shape that IDE problem matchers depend on. We re-
// export thin local wrappers that bind the runner-specific toRepoRelative
// implementation.
function formatAsciiViolation(v) {
  return sharedFormatters.formatAsciiViolation(v, toRepoRelative);
}

function formatAsciiWarning(w) {
  return sharedFormatters.formatAsciiWarning(w, toRepoRelative);
}

function formatCodePatternViolation(v) {
  return sharedFormatters.formatCodePatternViolation(v, toRepoRelative);
}

function formatProseViolation(v) {
  return sharedFormatters.formatProseViolation(v, toRepoRelative);
}

function formatViolation(validatorId, v) {
  if (validatorId === "validate-docs-ascii") return formatAsciiViolation(v);
  if (validatorId === "validate-doc-code-patterns") return formatCodePatternViolation(v);
  if (validatorId === "validate-docs-prose") return formatProseViolation(v);
  return JSON.stringify(v);
}

function createEmptyResults() {
  const results = new Map();
  for (const validator of VALIDATORS) {
    results.set(validator.id, { violations: [], warnings: [] });
  }
  return results;
}

function runValidatorsForContent(absPath, content, results = createEmptyResults()) {
  for (const validator of VALIDATORS) {
    try {
      const { violations, warnings } = validator.run(absPath, content);
      const slot = results.get(validator.id);
      if (violations && violations.length > 0) {
        slot.violations.push(...violations);
      }
      if (warnings && warnings.length > 0) {
        slot.warnings.push(...warnings);
      }
    } catch (error) {
      results.get(validator.id).violations.push({
        file: absPath,
        line: 0,
        column: 0,
        codepoint: 0,
        char: "",
        reason: `validator threw: ${error.message}`,
        id: "validator-error",
        why: error.stack || error.message,
        fix: "Investigate the validator or the input file.",
        sample: ""
      });
    }
  }
  return results;
}

function runStagedValidators(filePaths) {
  const applicable = [];
  for (const raw of filePaths) {
    if (isApplicable(raw)) {
      applicable.push(path.isAbsolute(raw) ? raw : path.resolve(process.cwd(), raw));
    }
  }

  const results = createEmptyResults();

  for (const absPath of applicable) {
    const content = readFileSafe(absPath);
    if (content === null) continue;
    runValidatorsForContent(absPath, content, results);
  }

  return { applicable, results };
}

function emit(line, useStderr) {
  const stream = useStderr ? process.stderr : process.stdout;
  stream.write(`${line}\n`);
}

function reportResults(applicable, results, options = {}) {
  const label = options.label || "run-staged-validators";
  const quietWhenEmpty = !!options.quietWhenEmpty;
  let totalViolations = 0;
  let totalWarnings = 0;

  for (const validator of VALIDATORS) {
    const slot = results.get(validator.id);
    for (const w of slot.warnings) {
      emit(formatAsciiWarning(w), true);
      totalWarnings++;
    }
  }

  for (const validator of VALIDATORS) {
    const slot = results.get(validator.id);
    if (slot.violations.length === 0) continue;
    emit(`-- ${validator.id} --`, true);
    for (const v of slot.violations) {
      emit(formatViolation(validator.id, v), true);
    }
    totalViolations += slot.violations.length;
  }

  const filesNote = `${applicable.length} file(s) inspected`;
  if (totalViolations === 0) {
    if (!(quietWhenEmpty && applicable.length === 0)) {
      emit(`${label}: 0 violations (${filesNote}).`, false);
    }
  } else {
    emit(
      `${label}: ${totalViolations} violation(s) across ${VALIDATORS.length} validator(s) (${filesNote}).`,
      true
    );
  }
  return { totalViolations, totalWarnings };
}

function main(argv) {
  if (argv.length === 0) {
    process.stdout.write("run-staged-validators: no file paths given (nothing to do).\n");
    return 0;
  }
  const { applicable, results } = runStagedValidators(argv);
  const { totalViolations } = reportResults(applicable, results);
  return totalViolations === 0 ? 0 : 1;
}

module.exports = {
  ROOT_DIR,
  ALLOWED_EXTS,
  EXCLUDE_PREFIXES,
  EXCLUDE_SEGMENT_RE,
  VALIDATORS,
  toRepoRelative,
  isExcluded,
  isApplicable,
  createEmptyResults,
  runValidatorsForContent,
  runStagedValidators,
  reportResults,
  formatAsciiViolation,
  formatAsciiWarning,
  formatCodePatternViolation,
  formatProseViolation,
  formatViolation,
  main
};

if (require.main === module) {
  process.exit(main(process.argv.slice(2)));
}
