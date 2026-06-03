/**
 * @fileoverview Enforces the pre-commit performance budget against the real
 * .pre-commit-config.yaml. See .llm/skills/performance/git-hook-performance.md.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const yaml = require("yaml");

const {
  scoreConfig,
  PERF_BUDGET,
  PER_HOOK_CEILING,
  formatReport
} = require("../lib/precommit-perf-score");
const { findAllHookBlocks, extractStagesFromHookBlock } = require("../lib/precommit-yaml");
const { normalizeToLf } = require("../lib/quote-parser");

const CONFIG_PATH = path.resolve(__dirname, "../../.pre-commit-config.yaml");
const SKILL_LINK = ".llm/skills/performance/git-hook-performance.md";

function readConfig() {
  return fs.readFileSync(CONFIG_PATH, "utf8");
}

describe("git hook performance budget", () => {
  test(".pre-commit-config.yaml parses as valid YAML", () => {
    const content = readConfig();
    let parsed;
    expect(() => {
      parsed = yaml.parse(content);
    }).not.toThrow();
    expect(parsed).toBeTruthy();
    expect(Array.isArray(parsed.repos)).toBe(true);
  });

  test(`pre-commit perf score stays under or at the total budget (${PERF_BUDGET}) AND no single hook exceeds the per-hook ceiling (${PER_HOOK_CEILING})`, () => {
    const content = readConfig();
    const result = scoreConfig(content);

    const totalOver = result.totalScore > PERF_BUDGET;
    const perHookOver = (result.perHookViolations || []).length > 0;

    if (totalOver || perHookOver) {
      const report = formatReport(result);
      const headline = totalOver
        ? `Pre-commit perf score ${result.totalScore} exceeds total budget ${PERF_BUDGET}.`
        : `Pre-commit per-hook ceiling (${PER_HOOK_CEILING}) exceeded by ${result.perHookViolations.length} hook(s).`;
      const message = [
        headline,
        "",
        "Two ceilings apply:",
        `  - total budget (${PERF_BUDGET}): cumulative across all pre-commit hooks; catches accumulated drift.`,
        `  - per-hook ceiling (${PER_HOOK_CEILING}): any single hook above this fails on its own;`,
        "    catches single-rule regressions (e.g. dropping a `bash -lc` (5 points) into one entry).",
        "",
        report,
        "",
        "Remediation options:",
        "  - Move the hook to pre-push (stages: [pre-push]).",
        "  - Add a files: filter and remove pass_filenames: false.",
        "  - For external-tool hooks, add require_serial: true to batch into one process.",
        "  - Split a hook that does too much in one entry across two narrower hooks.",
        "  - As a last resort, add a '# perf-allow[<rule-ids>]: <substantive reason of 25+ chars>'",
        "    comment immediately above the offending '- id:' line, with a real justification.",
        "    Valid rule IDs: scans-the-world, scans-the-world-with-files, always-run, npm-spawn,",
        "    dotnet-no-batch, jest-at-pre-commit, npx-cold-start, bash-login-shell, node-double-spawn,",
        "    npm-run-at-hook. The legacy '# perf-allow: <reason>' form is rejected.",
        "",
        `See ${SKILL_LINK} for the full anti-pattern list and remediation patterns.`
      ].join("\n");
      throw new Error(message);
    }

    expect(result.totalScore).toBeLessThanOrEqual(PERF_BUDGET);
    expect(result.perHookViolations).toEqual([]);
  });

  test("every hook declares stages explicitly OR is on the obvious-precommit allowlist", () => {
    // Hooks that are unambiguously cheap pre-commit-only validators may
    // omit `stages:` and inherit the pre-commit default. Anything else
    // must be explicit so reviewers see the intent.
    const obviousPreCommitOnly = new Set(["csharpier"]);

    const content = readConfig();
    const lines = normalizeToLf(content).split("\n");
    const blocks = findAllHookBlocks(lines);

    const offenders = [];
    for (const block of blocks) {
      const stages = extractStagesFromHookBlock(block);
      const explicit = stages.length > 0;
      if (explicit) {
        continue;
      }
      if (obviousPreCommitOnly.has(block.id)) {
        continue;
      }
      offenders.push(`${block.id} (line ${block.startLine})`);
    }

    if (offenders.length > 0) {
      throw new Error(
        [
          "The following hooks omit `stages:` and are NOT on the obvious-precommit allowlist:",
          ...offenders.map((o) => `  - ${o}`),
          "",
          "Either declare stages: explicitly or add the hook id to the allowlist with a comment.",
          `See ${SKILL_LINK}.`
        ].join("\n")
      );
    }
  });
});
