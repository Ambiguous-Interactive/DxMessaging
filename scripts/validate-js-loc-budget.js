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
// Session 055 (+219, 10650 -> 10890): scripts/__tests__/ci-aggregate-workflow.test.js,
// a narrow guard for the CI aggregate migration. It prevents the required static
// gate from drifting back into twelve standalone workflows, dropping a job from
// `CI Success`, skipping skill-index validation on skill-only PRs, or losing the
// fail-closed shape on aggregate child jobs. Actionlint/yamllint validate syntax,
// but they do not enforce this repository-specific required-check topology.
//
// Session 056 (+295, 10890 -> 11185): hardening scripts/update-llms-txt.js against
// the recurring "skill count is out of date" failure class (runs 74913516377 and
// the long trail of `chore: update llms.txt` follow-ups). The skill-count claim is
// now a floored "at least N" promise validated for *no overstatement* instead of an
// exact match, so adding a skill never reddens CI; the same guard is extended to
// README.md (previously unchecked and stale at 140 vs 155), update mode keeps both
// docs in sync, and --check now prints the drifting lines. The bulk is the bespoke
// validator + its data-driven node:test coverage; no off-the-shelf tool enforces a
// repo-specific "docs may understate but never overstate the skill count" invariant.
//
// Session 057 (+153, tracked JS 11181 -> 11334; ceiling 11185 -> 11350): close the
// update/--check asymmetry in scripts/update-llms-txt.js that Copilot flagged.
// Update mode could exit 0 while leaving a README skill-count claim (missing or
// duplicated) that --check, the pre-commit hook, and the auto-commit bot all
// reject and the script cannot auto-fix -- a fixer reporting a false success.
// Update now shares one validator (collectValidationErrors) with --check and
// verifies the post-write state, failing loudly with the offending file instead.
// The bulk is that shared validator plus forked-process CLI regression tests
// (env-pointed fixtures) pinning fixer/checker convergence; no off-the-shelf tool
// enforces "a fixer must converge with its own --check or exit non-zero." A
// reviewed decision here.
const TOTAL_BUDGET = 11350;
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
