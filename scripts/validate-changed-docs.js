#!/usr/bin/env node
"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");
const { spawnPlatformCommandSync } = require("./lib/shell-command");
const {
  createEmptyResults,
  isApplicable,
  reportResults,
  runValidatorsForContent
} = require("./run-staged-validators");

const DOC_EXT_RE = /\.(?:md|markdown|cs)$/i;

function runGit(args, options = {}) {
  return spawnPlatformCommandSync(
    "git",
    args,
    {
      cwd: path.resolve(__dirname, ".."),
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      ...options
    },
    childProcess.spawnSync
  );
}

function lines(stdout) {
  return String(stdout || "")
    .replace(/\r\n/g, "\n")
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
}

function assertGitOk(result, description) {
  if (!result || result.error) {
    const detail = result && result.error ? result.error.message : "no result";
    throw new Error(`git ${description} failed: ${detail}`);
  }
  if (result.status !== 0) {
    const stderr = result.stderr ? String(result.stderr).trim() : "no stderr";
    throw new Error(`git ${description} exited with status ${result.status}: ${stderr}`);
  }
}

function getTrackedChangeSets({ runGitFn = runGit } = {}) {
  const pathspec = ["--", "*.md", "*.markdown", "*.cs"];
  const staged = runGitFn(["diff", "--cached", "--name-only", "--diff-filter=ACMR", ...pathspec]);
  assertGitOk(staged, "diff --cached --name-only");

  const worktree = runGitFn(["diff", "--name-only", "--diff-filter=ACMR", ...pathspec]);
  assertGitOk(worktree, "diff --name-only");

  const untracked = runGitFn(["ls-files", "--others", "--exclude-standard", ...pathspec]);
  assertGitOk(untracked, "ls-files --others");

  return {
    staged: lines(staged.stdout).filter((file) => DOC_EXT_RE.test(file)),
    worktree: lines(worktree.stdout).filter((file) => DOC_EXT_RE.test(file)),
    untracked: lines(untracked.stdout).filter((file) => DOC_EXT_RE.test(file))
  };
}

function getChangedDocFiles({ runGitFn = runGit } = {}) {
  const changes = getTrackedChangeSets({ runGitFn });
  return [...new Set([...changes.staged, ...changes.worktree, ...changes.untracked])].sort();
}

function readWorktreeContent(repoRoot, file) {
  try {
    const absPath = path.isAbsolute(file) ? file : path.join(repoRoot, file);
    return fs.readFileSync(absPath, "utf8");
  } catch {
    return null;
  }
}

function readStagedContent(file, runGitFn) {
  const result = runGitFn(["show", `:${file}`]);
  assertGitOk(result, `show :${file}`);
  return result.stdout || "";
}

function getChangedDocEntries(options = {}) {
  const { runGitFn = runGit, repoRoot = path.resolve(__dirname, "..") } = options;
  const changes = getTrackedChangeSets({ runGitFn });
  const entries = [];

  for (const file of changes.staged) {
    entries.push({
      file,
      source: "staged",
      content: readStagedContent(file, runGitFn)
    });
  }

  for (const file of [...new Set([...changes.worktree, ...changes.untracked])]) {
    const content = readWorktreeContent(repoRoot, file);
    if (content !== null) {
      entries.push({ file, source: "worktree", content });
    }
  }

  return entries;
}

function runEntries(entries) {
  const applicable = [];
  const results = createEmptyResults();

  for (const entry of entries) {
    const absPath = path.isAbsolute(entry.file)
      ? entry.file
      : path.resolve(process.cwd(), entry.file);
    if (!isApplicable(absPath)) {
      continue;
    }
    applicable.push(entry.file);
    runValidatorsForContent(absPath, entry.content, results);
  }

  return { applicable, results };
}

function runChangedDocValidators(options = {}) {
  const entries = options.files
    ? options.files
        .map((file) => ({
          file,
          source: "worktree",
          content: readWorktreeContent(process.cwd(), file)
        }))
        .filter((entry) => entry.content !== null)
    : getChangedDocEntries(options);
  const files = [...new Set(entries.map((entry) => entry.file))].sort();
  const result = runEntries(entries);
  const report = options.silent
    ? summarizeResults(result.results)
    : reportResults(result.applicable, result.results, {
        quietWhenEmpty: true,
        label: "validate-changed-docs"
      });
  return {
    files,
    ...result,
    ...report
  };
}

function summarizeResults(results) {
  let totalViolations = 0;
  let totalWarnings = 0;
  for (const slot of results.values()) {
    totalViolations += slot.violations.length;
    totalWarnings += slot.warnings.length;
  }
  return { totalViolations, totalWarnings };
}

function main() {
  const result = runChangedDocValidators();
  if (result.files.length === 0) {
    process.stdout.write("validate-changed-docs: no changed markdown or C# files to inspect.\n");
  }
  return result.totalViolations === 0 ? 0 : 1;
}

module.exports = {
  DOC_EXT_RE,
  assertGitOk,
  getTrackedChangeSets,
  getChangedDocFiles,
  getChangedDocEntries,
  summarizeResults,
  runChangedDocValidators,
  main
};

if (require.main === module) {
  process.exit(main());
}
