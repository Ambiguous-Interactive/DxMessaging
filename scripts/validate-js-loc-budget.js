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

// Session 047 net: the skills-index generator + test (+642) less ~85 lines of
// zero-loss cuts to existing scripts (dead code, lib/walkFiles dedup), landing
// tracked JS near 10543. Budget set to 10600 to cover it. The generator is a
// sanctioned bespoke exception -- no off-the-shelf tool regenerates a markdown
// line-count column, and the index Lines/TOC counts had drifted on ~90% of
// rows. A reviewed decision in the change that needs it.
//
// Session 052 (+43, 10600 -> 10650): scripts/__tests__/auto-commit-fetch-force-
// refspec.test.js, a drift-guard pinning the `+` force prefix on the auto-commit /
// state-branch workflows' remote-tracking fetch refspecs. Without it the shallow
// non-fast-forward crash (run 74494500574) silently regresses the moment anyone
// drops a `+`; no off-the-shelf tool guards workflow-YAML refspec invariants. The
// corpus had only ~16 lines of headroom, so the guard could not be slimmed into
// budget without gutting its explanatory comment (the guard's whole point). A
// reviewed decision in the change that needs it.
//
// Session 054 (+121, 10650 -> 10780; corpus 10649 -> 10770): the 110-line
// scripts/__tests__/il2cpp-compiler-config-split.test.js plus this ~11-line comment
// block, a drift-guard pinning the IL2CPP C++ compiler-configuration split
// (Workstream L Task 10). run-ci-tests.ps1 is shared by the correctness standalone
// leg (Debug C++, far faster compile, publishes no numbers) and the published perf
// leg (Release C++); the guard fails if a future edit re-hardcodes the config,
// flips the correctness leg to Release, or lets the perf leg drift to Debug. No
// off-the-shelf tool pins a PowerShell/workflow build-config invariant, and the
// corpus had 1 line of headroom, so the guard could not be slimmed into budget. A
// reviewed decision in the change that needs it.
const TOTAL_BUDGET = 10780;
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
    encoding: "utf8",
  });
  const files = output.split("\n").filter(Boolean);
  let total = 0;
  for (const file of files) {
    total += countLines(path.join(REPO_ROOT, file));
  }
  if (total > TOTAL_BUDGET) {
    console.error(
      `validate-js-loc-budget: tracked JS is ${total} lines across ${files.length} files; ` +
        `budget is ${TOTAL_BUDGET}. Delete or slim JS instead of raising the budget.`
    );
    process.exit(1);
  }
  console.log(
    `validate-js-loc-budget: OK (${total}/${TOTAL_BUDGET} lines across ${files.length} files).`
  );
}

main();
