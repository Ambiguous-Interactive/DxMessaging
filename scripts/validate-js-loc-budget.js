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

// Budget history:
// 047: 10600 for skills-index generation after zero-loss script cuts.
// 052: 10650 for the auto-commit force-refspec drift guard.
// 055: 10890 for CI aggregate-workflow topology guards.
// 056: 11185 for resilient llms.txt/README skill-count validation.
// 057: 11350 for update/check convergence validation.
// 058: 11820 for shared release notes, changelog extraction, and export staging
// coverage. Each increase was reviewed with the bespoke invariant it protects.
// 059: 11960 for cross-platform PowerShell project-path safety regression tests.
// 062: 12360 for the issue-template package-version dropdown generator + its
// hermetic red-green test (G5 / #230) and the self-healing auto-commit workflow's
// entry in the fetch-force-refspec drift-guard; the gate keeps the bug-report
// dropdown in lockstep with released versions without dropping shallow-clone
// history. The generator header was trimmed first to keep the increase minimal.
// 064: 12390 for the allocation-honesty fix -- the perf pipeline reports a real
// GC-allocation COUNT (AllocationProbe / GC.Alloc recorder) with an "Unmeasured"
// sentinel instead of the vacuous 0 the dead GC.GetAllocatedBytesForCurrentThread()
// byte counter produced under Unity's Boehm GC. Covers the renderer sentinel
// handling + the honesty regression test (sentinel never renders as 0/regression).
// Comments across the perf-render scripts were trimmed first to keep this minimal.
const TOTAL_BUDGET = 12390;
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
