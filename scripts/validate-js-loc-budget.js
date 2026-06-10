#!/usr/bin/env node
"use strict";

/**
 * @fileoverview Enforces the tracked-JavaScript lines-of-code budget pinned to
 * the consolidated end-of-session baseline of the JS consolidation (sessions
 * 037-044), so the duplication that consolidation removed cannot silently
 * regrow.
 *
 * Counting definition (EOL/BOM-stable by construction): a file's line count is
 * computed on `readUtf8` output (CRLF/CR -> LF normalized, leading BOM
 * stripped). An empty file counts 0; otherwise the count is
 * `text.split("\n").length`, minus 1 when the text ends with `"\n"`. This
 * equals `wc -l` for newline-terminated files and additionally counts a final
 * unterminated line.
 *
 * Three budget tiers, all compared with strict `>` (at-budget passes):
 *
 *   - `TOTAL_BUDGET`: every tracked `*.js` / `*.cjs` / `*.mjs` file summed
 *     (the .cjs/.mjs patterns close the rename dodge; none are tracked today).
 *     Pinned at the measured baseline plus a flat 400-line headroom --
 *     deliberately smaller than one new mid-size file, so growth is a
 *     reviewed decision, never a drift.
 *   - `PER_FILE_DEFAULT_CAP`: the cap for any file without an override.
 *   - `PER_FILE_OVERRIDES`: the files already over the default cap, each
 *     pinned at actual +5% (rounded up to 10) with a reason. A stale entry --
 *     the file is untracked, or measured at or under the default cap -- FAILS
 *     the gate, so the override table cannot rot.
 *
 * Ratchet policy: raising any constant is a reviewed bump in the SAME change
 * that needs it, with justification in that change; shrink sessions re-pin the
 * constants DOWN to the new baseline. Recompute (the pin is mechanical):
 *
 *   node -e "const g = require('./scripts/validate-js-loc-budget');
 *     const m = g.measureTrackedJs(); const t = m.reduce((a, x) => a + x.lines, 0);
 *     console.log('TOTAL_BUDGET', Math.ceil((t + 400) / 100) * 100, '(actual', t + ')');
 *     for (const x of m.filter((x) => x.lines > g.PER_FILE_DEFAULT_CAP)
 *       .sort((a, b) => b.lines - a.lines))
 *       console.log(x.file, x.lines, '->', Math.ceil((x.lines * 1.05) / 10) * 10);"
 *
 * This is the total/per-file LOC budget half of the JS size/policy budget
 * gate; the "ban duplicated local helpers" half is the sibling gate
 * `scripts/validate-shared-helper-usage.js` (`npm run validate:shared-helpers`).
 */

const path = require("path");

const { REPO_ROOT, listTrackedFiles, readUtf8 } = require("./lib/repo-files");
const { parseArgs } = require("./lib/cli-options");

/**
 * Git pathspecs selecting the tracked JavaScript files under budget.
 * @type {string[]}
 */
const JS_FILE_PATTERNS = ["*.js", "*.cjs", "*.mjs"];

/**
 * Repository-wide budget for the summed line count of all tracked JS.
 * Pinned: measured baseline 140122 + 400 headroom, rounded up to 100.
 * @type {number}
 */
const TOTAL_BUDGET = 140600;

/**
 * Per-file cap for any tracked JS file without a PER_FILE_OVERRIDES entry.
 * The knee of the current size distribution: 14 files sit above it (all
 * pinned below), versus 37 above 1000.
 * @type {number}
 */
const PER_FILE_DEFAULT_CAP = 1500;

/**
 * Files already over the default cap, each pinned at measured actual +5%
 * rounded up to 10 (see the @fileoverview recompute one-liner). Key:
 * repo-relative POSIX path. Value: the pinned budget plus the reason the file
 * is legitimately large. An entry whose file shrinks to the default cap (or
 * leaves the tree) is STALE and fails the gate -- remove it.
 *
 * @type {Record<string, { budget: number, reason: string }>}
 */
const PER_FILE_OVERRIDES = {
  "scripts/validate-workflows.js": {
    budget: 5270,
    reason: "workflow policy validator: 30+ find*Violations checks over one shared engine"
  },
  "scripts/__tests__/validate-workflows.test.js": {
    budget: 4270,
    reason: "workflow-validator oracle suite 1; policy fixtures are deliberately literal"
  },
  "scripts/__tests__/validate-workflows-concurrency-and-labels.test.js": {
    budget: 3290,
    reason: "workflow-validator oracle suite 2 (concurrency/labels/licensing); fixtures literal"
  },
  "scripts/__tests__/run-managed-jest.test.js": {
    budget: 3220,
    reason: "managed-jest wrapper contract: spawn, fallback, and flag-handling matrices"
  },
  "scripts/__tests__/pwsh-output-assertion-policy.test.js": {
    budget: 2430,
    reason: "repo-wide pwsh assertion policy guard with a large self-test fixture corpus"
  },
  "scripts/fix-pwsh-output-assertions.js": {
    budget: 2330,
    reason: "pwsh assertion codemod: rewrite rules plus conservative parsing"
  },
  "scripts/__tests__/unity-workflow-shape.test.js": {
    budget: 2190,
    reason: "shape contract for every Unity-credential-using workflow job"
  },
  "scripts/__tests__/unity-runner-script-contract.test.js": {
    budget: 2160,
    reason: "run-ci-tests.ps1 contract suite spanning the runner's full flag surface"
  },
  "scripts/__tests__/doctor.test.js": {
    budget: 2130,
    reason: "doctor CLI behavior suite covering each independent environment probe"
  },
  "scripts/__tests__/validate-pre-commit-tooling.test.js": {
    budget: 2110,
    reason: "pre-commit tooling policy suite: hook inventory and stage contracts"
  },
  "scripts/__tests__/unity-ensure-editor-il2cpp-idempotency.test.js": {
    budget: 1740,
    reason: "ensure-editor IL2CPP module idempotency matrix (pwsh spawns per case)"
  },
  "scripts/lib/workflow-policy-engine.js": {
    budget: 1700,
    reason: "shared workflow parsing/policy engine the validator and suites consolidate onto"
  },
  "scripts/__tests__/unity-ensure-editor-install-resilience.test.js": {
    budget: 1690,
    reason: "ensure-editor install/repair/quarantine resilience matrix"
  },
  "scripts/doctor.js": {
    budget: 1640,
    reason: "environment doctor CLI: many independent probes with remediation text"
  }
};

/**
 * Count the lines of `text` (expected to be `readUtf8` output).
 *
 * `""` counts 0. Otherwise `text.split("\n").length`, minus 1 when the text
 * ends with `"\n"`: `wc -l` semantics for newline-terminated files, plus a
 * final unterminated line counts as a line.
 *
 * @param {string} text LF-normalized file contents.
 * @returns {number} The line count.
 */
function countLines(text) {
  if (text === "") {
    return 0;
  }
  const segments = text.split("\n").length;
  return text.endsWith("\n") ? segments - 1 : segments;
}

/**
 * Measure every tracked JS file in the repository.
 *
 * @param {{ repoRoot?: string }} [options]
 * @returns {Array<{ file: string, lines: number }>} Repo-relative POSIX paths
 *   (as git emits) with their line counts, in `git ls-files` order.
 */
function measureTrackedJs(options = {}) {
  const { repoRoot = REPO_ROOT } = options;
  return listTrackedFiles(JS_FILE_PATTERNS, { repoRoot }).map((file) => ({
    file,
    lines: countLines(readUtf8(path.join(repoRoot, file)))
  }));
}

/**
 * Pure classification of measurements against a budget set. Separated from
 * the filesystem scan so the violation- and stale-detection logic is
 * unit-testable with synthetic inputs (the same seam as the sibling gate's
 * `classifyFindings`).
 *
 * All comparisons are strict `>`: a file AT its cap and a total AT the budget
 * both pass. An override is stale when its file is not measured (untracked)
 * or measures at or under `defaultCap` (the override no longer earns its keep).
 *
 * @param {Array<{ file: string, lines: number }>} measurements
 * @param {{
 *   totalBudget: number,
 *   defaultCap: number,
 *   overrides: Record<string, { budget: number, reason: string }>
 * }} budgets
 * @returns {{
 *   fileCount: number,
 *   totalLines: number,
 *   totalViolations: string[],
 *   fileViolations: string[],
 *   staleOverrides: string[]
 * }}
 */
function classifyMeasurements(measurements, budgets) {
  const { totalBudget, defaultCap, overrides } = budgets;

  let totalLines = 0;
  const fileViolations = [];
  const measuredLines = new Map();
  for (const { file, lines } of measurements) {
    totalLines += lines;
    measuredLines.set(file, lines);
    const override = Object.prototype.hasOwnProperty.call(overrides, file) ? overrides[file] : null;
    if (override) {
      if (lines > override.budget) {
        fileViolations.push(
          `${file}: ${lines} lines exceeds its pinned override budget of ${override.budget}. ` +
            `Split the file, or raise its PER_FILE_OVERRIDES budget in the same change with a ` +
            `reviewed justification.`
        );
      }
    } else if (lines > defaultCap) {
      fileViolations.push(
        `${file}: ${lines} lines exceeds the default per-file cap of ${defaultCap}. ` +
          `Split the file, or add a documented PER_FILE_OVERRIDES entry.`
      );
    }
  }

  const totalViolations = [];
  if (totalLines > totalBudget) {
    totalViolations.push(
      `tracked JS totals ${totalLines} lines, exceeding TOTAL_BUDGET ${totalBudget}. ` +
        `Shrink or split the change, or raise the TOTAL_BUDGET constant in the same change ` +
        `with a reviewed justification.`
    );
  }

  const staleOverrides = [];
  for (const [file, override] of Object.entries(overrides)) {
    if (!measuredLines.has(file)) {
      staleOverrides.push(
        `${file}: override (budget ${override.budget}) names an untracked file -- remove the ` +
          `stale entry.`
      );
    } else if (measuredLines.get(file) <= defaultCap) {
      staleOverrides.push(
        `${file}: override (budget ${override.budget}) is unnecessary -- the file measures ` +
          `${measuredLines.get(file)} lines, within the default cap of ${defaultCap}. Remove ` +
          `the stale entry (re-pin DOWN).`
      );
    }
  }

  return {
    fileCount: measurements.length,
    totalLines,
    totalViolations,
    fileViolations,
    staleOverrides
  };
}

/**
 * Measure the repository and classify against the live budget constants.
 *
 * @param {{ repoRoot?: string }} [options]
 * @returns {ReturnType<typeof classifyMeasurements>}
 */
function evaluate(options = {}) {
  return classifyMeasurements(measureTrackedJs(options), {
    totalBudget: TOTAL_BUDGET,
    defaultCap: PER_FILE_DEFAULT_CAP,
    overrides: PER_FILE_OVERRIDES
  });
}

/**
 * Print the `--verbose` tables: every override with its actual/budget/headroom,
 * then the ten non-override files nearest the default cap.
 *
 * @param {Array<{ file: string, lines: number }>} measurements
 * @returns {void}
 */
function printVerboseTables(measurements) {
  const measuredLines = new Map(measurements.map(({ file, lines }) => [file, lines]));

  console.log("Per-file overrides (actual/budget, headroom):");
  const overrideRows = Object.keys(PER_FILE_OVERRIDES)
    .map((file) => ({ file, lines: measuredLines.get(file) || 0 }))
    .sort((a, b) => b.lines - a.lines);
  for (const { file, lines } of overrideRows) {
    const budget = PER_FILE_OVERRIDES[file].budget;
    console.log(`  ${file}: ${lines}/${budget} (${budget - lines} headroom)`);
  }

  const nearest = measurements
    .filter(({ file }) => !Object.prototype.hasOwnProperty.call(PER_FILE_OVERRIDES, file))
    .sort((a, b) => b.lines - a.lines)
    .slice(0, 10);
  console.log(`Top ${nearest.length} non-override files nearest the default cap:`);
  for (const { file, lines } of nearest) {
    console.log(
      `  ${file}: ${lines}/${PER_FILE_DEFAULT_CAP} (${PER_FILE_DEFAULT_CAP - lines} headroom)`
    );
  }
}

function main() {
  const { values, errors } = parseArgs(process.argv.slice(2), {
    options: { verbose: { type: "boolean", aliases: ["--verbose"] } },
    unknownOption: "error"
  });
  if (errors.length > 0) {
    for (const message of errors) {
      console.error(message);
    }
    console.error("Usage: node scripts/validate-js-loc-budget.js [--verbose]");
    return 1;
  }

  const measurements = measureTrackedJs();
  const { fileCount, totalLines, totalViolations, fileViolations, staleOverrides } =
    classifyMeasurements(measurements, {
      totalBudget: TOTAL_BUDGET,
      defaultCap: PER_FILE_DEFAULT_CAP,
      overrides: PER_FILE_OVERRIDES
    });

  if (totalViolations.length === 0 && fileViolations.length === 0 && staleOverrides.length === 0) {
    if (values.verbose) {
      printVerboseTables(measurements);
    }
    console.log(
      `validate:js-loc-budget OK -- ${fileCount} tracked JS files, ` +
        `${totalLines}/${TOTAL_BUDGET} total lines (${TOTAL_BUDGET - totalLines} headroom), ` +
        `per-file cap ${PER_FILE_DEFAULT_CAP} (${Object.keys(PER_FILE_OVERRIDES).length} ` +
        `overrides).`
    );
    return 0;
  }

  if (totalViolations.length > 0) {
    console.error("Tracked-JS total LOC budget exceeded:");
    for (const message of totalViolations) {
      console.error(`  ${message}`);
    }
  }
  if (fileViolations.length > 0) {
    console.error("Per-file LOC budget exceeded:");
    for (const message of fileViolations) {
      console.error(`  ${message}`);
    }
  }
  if (staleOverrides.length > 0) {
    console.error("Stale PER_FILE_OVERRIDES entries in scripts/validate-js-loc-budget.js:");
    for (const message of staleOverrides) {
      console.error(`  ${message}`);
    }
  }
  return 1;
}

if (require.main === module) {
  process.exitCode = main();
}

module.exports = {
  JS_FILE_PATTERNS,
  TOTAL_BUDGET,
  PER_FILE_DEFAULT_CAP,
  PER_FILE_OVERRIDES,
  countLines,
  measureTrackedJs,
  classifyMeasurements,
  evaluate,
  main
};
