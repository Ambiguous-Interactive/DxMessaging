#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");
// Single source of truth for the YAML line-length policy, parser, AND the
// comment-wrap functions. Hoisted into scripts/lib/yaml-line-length.js so this
// commit-time fixer and the agentic PostToolUse guard cannot silently diverge.
const {
  DEFAULT_MAX_LINE_LENGTH,
  parseYamlBoolean,
  resolveYamlLineLengthPolicy,
  splitWords,
  wrapCommentLine,
  wrapYamlCommentLines
} = require("./lib/yaml-line-length");

// Preserve the historical name/shape of this module's wrapping helper while
// sourcing the behavior from the lib (single source of truth).
const rewriteYamlCommentLines = wrapYamlCommentLines;

function looksLikeYamlPath(candidate) {
  return /\.ya?ml$/i.test(candidate);
}

function resolveRepoRoot(cwd = process.cwd()) {
  return cwd;
}

function getAllTrackedYamlFiles(repoRoot) {
  const output = execFileSync("git", ["ls-files", "--", "*.yml", "*.yaml"], {
    cwd: repoRoot,
    encoding: "utf8"
  });

  return output
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .map((line) => path.resolve(repoRoot, line));
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

function processFiles(files, options = {}) {
  const repoRoot = options.repoRoot || resolveRepoRoot();
  const configPath = path.join(repoRoot, ".yamllint.yaml");
  const policy = resolveYamlLineLengthPolicy(configPath);

  const changedFiles = [];
  const violations = [];

  for (const filePath of files) {
    const original = fs.readFileSync(filePath, "utf8");
    const rewritten = rewriteYamlCommentLines(original, policy);

    if (rewritten.changedLines.length === 0) {
      continue;
    }

    if (options.check === true) {
      violations.push({
        filePath,
        changedLines: rewritten.changedLines
      });
      continue;
    }

    fs.writeFileSync(filePath, rewritten.content, "utf8");
    changedFiles.push({
      filePath,
      changedLines: rewritten.changedLines
    });
  }

  return {
    changedFiles,
    violations,
    policy
  };
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
        `fix-yaml-comments-line-length: unable to list tracked YAML files via git: ${error.message}\n`
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
    if (result.violations.length === 0) {
      return 0;
    }

    process.stderr.write(
      `YAML comment line-length check failed (${result.violations.length} file(s)).\n`
    );
    for (const violation of result.violations) {
      const relative = path.relative(repoRoot, violation.filePath).replace(/\\/g, "/");
      process.stderr.write(`  - ${relative}: lines ${violation.changedLines.join(", ")}\n`);
    }
    process.stderr.write(`Run: node scripts/fix-yaml-comments-line-length.js --all-files\n`);
    return 1;
  }

  if (result.changedFiles.length === 0) {
    return 0;
  }

  for (const changed of result.changedFiles) {
    const relative = path.relative(repoRoot, changed.filePath).replace(/\\/g, "/");
    process.stdout.write(
      `Wrapped YAML comments in ${relative} (lines ${changed.changedLines.join(", ")}).\n`
    );
  }

  return 0;
}

module.exports = {
  DEFAULT_MAX_LINE_LENGTH,
  parseYamlBoolean,
  resolveYamlLineLengthPolicy,
  splitWords,
  wrapCommentLine,
  rewriteYamlCommentLines,
  parseArgs,
  uniqueExistingYamlFiles,
  getAllTrackedYamlFiles,
  processFiles,
  main
};

if (require.main === module) {
  process.exit(main());
}
