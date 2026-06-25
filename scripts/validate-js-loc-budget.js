#!/usr/bin/env node
"use strict";

/**
 * Fails when tracked JavaScript (*.js / *.cjs / *.mjs) exceeds the repo-wide
 * line budget. This repo is a Unity C# package; JS exists only as thin CI/docs
 * support and must stay small. Raising the budget is a reviewed decision in
 * the same change that needs it.
 */

const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");

// Budget history, newest last:
// 047 skills-index generation after zero-loss script cuts: 10600.
// 052 auto-commit force-refspec drift guard: 10650.
// 055 CI aggregate-workflow topology guards: 10890.
// 056 llms.txt/README skill-count validation: 11185.
// 057 update/check convergence validation: 11350.
// 058 release notes, changelog extraction, export staging: 11820.
// 059 cross-platform PowerShell project-path safety tests: 11960.
// 062 issue-template version generator and fetch-refspec guard: 12360.
// 064 allocation-honesty perf sentinel handling: 12390.
// 065 PlayMode allocation leg and perf-scenario sharing: 12664.
// 066 banner --check diagnostics: 12795.
// 067 package-script contract guard and validate:all issue-template gate: 12890.
const TOTAL_BUDGET = 12890;
const LARGEST_FILE_COUNT = 10;
const REPO_ROOT = path.resolve(__dirname, "..");

function countLines(filePath) {
  const text = fs.readFileSync(filePath, "utf8");
  if (text.length === 0) {
    return 0;
  }
  const lines = text.split("\n").length;
  return text.endsWith("\n") ? lines - 1 : lines;
}

function main() {
  const output = execFileSync("git", ["ls-files", "*.js", "*.cjs", "*.mjs"], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  const files = output.split("\n").filter(Boolean);
  let total = 0;
  const counts = [];
  for (const file of files) {
    const lines = countLines(path.join(REPO_ROOT, file));
    total += lines;
    counts.push({ file, lines });
  }
  if (total > TOTAL_BUDGET) {
    const largest = counts
      .sort((a, b) => b.lines - a.lines || a.file.localeCompare(b.file))
      .slice(0, LARGEST_FILE_COUNT)
      .map(({ file, lines }) => `  ${lines.toString().padStart(5)} ${file}`)
      .join("\n");
    console.error(
      `validate-js-loc-budget: tracked JS is ${total} lines across ${files.length} files; ` +
        `budget is ${TOTAL_BUDGET} (${total - TOTAL_BUDGET} over). ` +
        "Delete or slim JS instead of raising the budget.\n" +
        `Largest tracked JS files:\n${largest}`
    );
    process.exit(1);
  }
  console.log(
    `validate-js-loc-budget: OK (${total}/${TOTAL_BUDGET} lines across ${files.length} files).`
  );
}

main();
