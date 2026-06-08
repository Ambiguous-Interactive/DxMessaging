#!/usr/bin/env node
"use strict";

/**
 * fix-yaml-block-scalar-line-length.js
 *
 * Companion to fix-yaml-comments-line-length.js. The comment fixer wraps long
 * `#` comment lines; this one targets the OTHER yamllint line-length offender
 * class that previously had no automated recovery: long code/string lines
 * inside GitHub Actions `run: |` block scalars (the canonical case being a long
 * PowerShell `Write-Output "<message>"`).
 *
 * Policy comes from the SAME source of truth (.yamllint.yaml via
 * scripts/lib/yaml-line-length.js), and over-length detection is the faithful
 * yamllint port -- so this never touches a line yamllint would exempt
 * (`allow-non-breakable-words` / `allow-non-breakable-inline-mappings`).
 *
 * Write mode rewrites every over-length line that is the provably
 * semantics-preserving PowerShell string-literal shape into a multi-line `+`
 * concatenation; lines that are too long but NOT safely rewritable are reported
 * as actionable failures (shorten or externalize the script). Check mode exits
 * non-zero when any real (rewritable or not) violation remains.
 *
 * CommonJS, zero runtime deps, cross-platform. Mirrors the existing fixer's CLI
 * surface: `--check`, `--all-files`, and explicit file paths.
 */

const fs = require("fs");
const path = require("path");
const { listTrackedFiles } = require("./lib/repo-files");
const { normalizeToLf } = require("./lib/quote-parser");
const {
  resolveYamlLineLengthPolicy,
  findLineLengthViolations,
  rewriteYamlBlockScalarLines,
  BLOCK_SCALAR_REMEDIATION
} = require("./lib/yaml-line-length");

function looksLikeYamlPath(candidate) {
  return /\.ya?ml$/i.test(candidate);
}

function resolveRepoRoot(cwd = process.cwd()) {
  return cwd;
}

function getAllTrackedYamlFiles(repoRoot) {
  return listTrackedFiles(["*.yml", "*.yaml"], { repoRoot, cwd: repoRoot }).map((line) =>
    path.resolve(repoRoot, line)
  );
}

function parseArgs(argv) {
  const options = {
    check: false,
    allFiles: false,
    files: []
  };

  for (const arg of argv) {
    if (arg === "--check") {
      options.check = true;
      continue;
    }

    if (arg === "--all-files") {
      options.allFiles = true;
      continue;
    }

    options.files.push(arg);
  }

  return options;
}

function uniqueExistingYamlFiles(files) {
  const unique = new Set();

  for (const file of files) {
    if (!looksLikeYamlPath(file)) {
      continue;
    }

    const absolute = path.resolve(file);
    if (!fs.existsSync(absolute)) {
      continue;
    }

    unique.add(absolute);
  }

  return Array.from(unique.values());
}

/**
 * Process a set of YAML files: detect over-length lines (faithful yamllint
 * port), safely rewrite the rewritable PowerShell string shape, and collect the
 * over-length lines that are NOT safely rewritable.
 *
 * In check mode no files are written; instead every remaining violation (both
 * the would-be-rewritten lines and the unsafe ones) is reported so the caller
 * can fail. In write mode the safe rewrites are applied; unsafe over-length
 * lines are left untouched and surfaced as failures (they need a human).
 *
 * @param {string[]} files Absolute YAML file paths.
 * @param {{check?:boolean, repoRoot?:string}} options Behavior options.
 * @returns {{changedFiles:Array, unsafe:Array, checkViolations:Array,
 *   policy:object}} Outcome.
 */
function processFiles(files, options = {}) {
  const repoRoot = options.repoRoot || resolveRepoRoot();
  const configPath = path.join(repoRoot, ".yamllint.yaml");
  const policy = resolveYamlLineLengthPolicy(configPath);

  const changedFiles = [];
  const unsafe = [];
  const checkViolations = [];

  for (const filePath of files) {
    const original = fs.readFileSync(filePath, "utf8");

    if (options.check === true) {
      // Check mode: report what is over-length and whether each line can be
      // safely auto-fixed (so the message can guide the author).
      const result = rewriteYamlBlockScalarLines(original, policy);
      const rewritable = result.changedLines;
      if (rewritable.length === 0 && result.unsafe.length === 0) {
        continue;
      }
      checkViolations.push({
        filePath,
        rewritableLines: rewritable,
        unsafe: result.unsafe
      });
      for (const entry of result.unsafe) {
        unsafe.push({ filePath, ...entry });
      }
      continue;
    }

    const result = rewriteYamlBlockScalarLines(original, policy);

    for (const entry of result.unsafe) {
      unsafe.push({ filePath, ...entry });
    }

    if (result.changedLines.length === 0) {
      continue;
    }

    // Preserve the repo EOL policy for YAML (LF). The engine normalizes to LF
    // internally; we write LF, matching .gitattributes and the comment fixer.
    const rewritten = normalizeToLf(result.content);
    fs.writeFileSync(filePath, rewritten, "utf8");
    changedFiles.push({
      filePath,
      changedLines: result.changedLines
    });
  }

  return {
    changedFiles,
    unsafe,
    checkViolations,
    policy
  };
}

function toRelative(repoRoot, filePath) {
  return path.relative(repoRoot, filePath).replace(/\\/g, "/");
}

function main(argv = process.argv.slice(2)) {
  const args = parseArgs(argv);
  const repoRoot = resolveRepoRoot();

  let candidateFiles = args.files.map((filePath) => path.resolve(repoRoot, filePath));
  if (args.allFiles) {
    try {
      candidateFiles = [...candidateFiles, ...getAllTrackedYamlFiles(repoRoot)];
    } catch (error) {
      process.stderr.write(
        `fix-yaml-block-scalar-line-length: unable to list tracked YAML files via git: ${error.message}\n`
      );
      return 1;
    }
  }

  const files = uniqueExistingYamlFiles(candidateFiles);
  if (files.length === 0) {
    return 0;
  }

  const result = processFiles(files, {
    check: args.check,
    repoRoot
  });

  if (args.check) {
    if (result.checkViolations.length === 0) {
      return 0;
    }

    // Only SAFE-pwsh-rewritable violations justify recommending the auto-fix.
    // Every other violation needs a human; recommending --all-files for those
    // would recommend the (formerly corrupting) action, so we never do.
    let anyRewritable = false;
    let anyManual = false;

    process.stderr.write(
      `YAML block-scalar line-length check failed (${result.checkViolations.length} file(s)).\n`
    );
    for (const violation of result.checkViolations) {
      const relative = toRelative(repoRoot, violation.filePath);
      if (violation.rewritableLines.length > 0) {
        anyRewritable = true;
        process.stderr.write(
          `  - ${relative}: auto-fixable lines ${violation.rewritableLines.join(", ")}\n`
        );
      }
      for (const entry of violation.unsafe) {
        anyManual = true;
        process.stderr.write(
          `  - ${relative}:${entry.line}: line too long (${entry.length} chars), manual ` +
            `remediation required -- ${entry.reason}\n`
        );
      }
    }
    if (anyRewritable) {
      process.stderr.write(
        "Auto-fixable lines (safe pwsh `run:` string literals): run\n" +
          "  node scripts/fix-yaml-block-scalar-line-length.js --all-files\n"
      );
    }
    if (anyManual) {
      process.stderr.write(
        `Manual remediation required for the remaining lines: ${BLOCK_SCALAR_REMEDIATION}\n`
      );
    }
    return 1;
  }

  for (const changed of result.changedFiles) {
    const relative = toRelative(repoRoot, changed.filePath);
    process.stdout.write(
      `Rewrote long PowerShell strings in ${relative} (lines ${changed.changedLines.join(", ")}).\n`
    );
  }

  // Write mode still fails when over-length lines remain that we could not
  // safely rewrite -- the author must shorten or externalize them. These are
  // NEVER rewritten (left byte-identical); they require manual remediation.
  if (result.unsafe.length > 0) {
    process.stderr.write(
      `YAML block-scalar line-length: ${result.unsafe.length} line(s) too long and require ` +
        "manual remediation.\n"
    );
    for (const entry of result.unsafe) {
      const relative = toRelative(repoRoot, entry.filePath);
      process.stderr.write(
        `  - ${relative}:${entry.line}: line too long (${entry.length} chars) -- ${entry.reason}\n`
      );
    }
    process.stderr.write(`${BLOCK_SCALAR_REMEDIATION}\n`);
    return 1;
  }

  return 0;
}

module.exports = {
  looksLikeYamlPath,
  parseArgs,
  uniqueExistingYamlFiles,
  getAllTrackedYamlFiles,
  processFiles,
  findLineLengthViolations,
  rewriteYamlBlockScalarLines,
  main
};

if (require.main === module) {
  process.exit(main());
}
