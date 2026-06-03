/**
 * @fileoverview Unit tests for the pre-commit perf scorer. These tests
 * exercise each scoring rule against synthetic YAML strings; the integration
 * test in hook-perf-budget.test.js scores the real .pre-commit-config.yaml.
 */

"use strict";

const {
  scoreConfig,
  PERF_BUDGET,
  PER_HOOK_CEILING,
  SCORING_RULES,
  parseAllowDirective,
  isReasonSubstantive,
  MIN_REASON_LENGTH,
  formatReport
} = require("../lib/precommit-perf-score");

function buildConfig(hookYaml) {
  return ["repos:", "  - repo: local", "    hooks:", hookYaml].join("\n");
}

function expectFiredRules(result, expectedRuleIds) {
  expect(result.perHookScores).toHaveLength(1);
  const fired = result.perHookScores[0].reasons.map((r) => r.ruleId).sort();
  expect(fired).toEqual([...expectedRuleIds].sort());
}

const SUBSTANTIVE_REASON = "real reason explaining the deliberate trade-off in detail";

describe("precommit-perf-score", () => {
  test("PERF_BUDGET stays at 10 (whole-pipeline ceiling)", () => {
    expect(PERF_BUDGET).toBe(10);
  });

  test("SCORING_RULES exposes a stable, non-empty set", () => {
    expect(SCORING_RULES.length).toBeGreaterThan(0);
    for (const rule of SCORING_RULES) {
      expect(typeof rule.id).toBe("string");
      expect(typeof rule.score).toBe("number");
      expect(typeof rule.description).toBe("string");
    }
  });

  test("SCORING_RULES exposes the documented stable rule IDs", () => {
    const ids = new Set(SCORING_RULES.map((r) => r.id));
    for (const requiredId of [
      "scans-the-world",
      "scans-the-world-with-files",
      "always-run",
      "npm-spawn",
      "dotnet-no-batch",
      "jest-at-pre-commit",
      "npx-cold-start",
      "bash-login-shell",
      "node-double-spawn",
      "npm-run-at-hook"
    ]) {
      expect(ids.has(requiredId)).toBe(true);
    }
  });

  test("pass_filenames: false without files: scores 5 (scans the world)", () => {
    const config = buildConfig(
      [
        "      - id: scan-everything",
        "        entry: node scripts/scan.js",
        "        language: system",
        "        pass_filenames: false"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
    expectFiredRules(result, ["scans-the-world"]);
  });

  test("pass_filenames: false with files: scores 3 (still pays scan cost)", () => {
    const config = buildConfig(
      [
        "      - id: scan-filtered",
        "        entry: node scripts/scan.js",
        "        language: system",
        "        files: '\\.md$'",
        "        pass_filenames: false"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(3);
    expectFiredRules(result, ["scans-the-world-with-files"]);
  });

  test("always_run: true scores 5", () => {
    const config = buildConfig(
      [
        "      - id: always-on",
        "        entry: echo always",
        "        language: system",
        "        always_run: true",
        "        files: '\\.md$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
    expectFiredRules(result, ["always-run"]);
  });

  test("npm pack scores 5", () => {
    const config = buildConfig(
      [
        "      - id: npm-pack",
        "        entry: npm pack --dry-run",
        "        language: system",
        "        files: '\\.json$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
    expectFiredRules(result, ["npm-spawn"]);
  });

  test("npm install scores 5", () => {
    const config = buildConfig(
      [
        "      - id: npm-install",
        "        entry: npm install",
        "        language: system",
        "        files: '\\.json$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
    expectFiredRules(result, ["npm-spawn"]);
  });

  test("dotnet tool run without require_serial scores 5", () => {
    const config = buildConfig(
      [
        "      - id: csharpier-bad",
        "        entry: dotnet tool run csharpier format",
        "        language: system",
        "        types:",
        "          - c#"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
    expectFiredRules(result, ["dotnet-no-batch"]);
  });

  test("dotnet tool run WITH require_serial scores 0", () => {
    const config = buildConfig(
      [
        "      - id: csharpier-good",
        "        entry: dotnet tool run csharpier format",
        "        language: system",
        "        require_serial: true",
        "        types:",
        "          - c#"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(0);
    expect(result.perHookScores).toHaveLength(0);
  });

  test("run-managed-jest.js at pre-commit scores 5", () => {
    const config = buildConfig(
      [
        "      - id: jest-precommit",
        "        entry: node scripts/run-managed-jest.js",
        "        language: system",
        "        files: '\\.js$'",
        "        stages:",
        "          - pre-commit"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
    expectFiredRules(result, ["jest-at-pre-commit"]);
  });

  test("run-managed-jest.js at pre-push only scores 0 (out of scope)", () => {
    const config = buildConfig(
      [
        "      - id: jest-prepush",
        "        entry: node scripts/run-managed-jest.js",
        "        language: system",
        "        files: '\\.js$'",
        "        stages:",
        "          - pre-push"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(0);
    expect(result.perHookScores).toHaveLength(0);
  });

  test("npx --yes scores 2", () => {
    const config = buildConfig(
      [
        "      - id: cspell-like",
        "        entry: npx --yes cspell@9 --no-progress",
        "        language: system",
        "        files: '\\.md$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(2);
    expectFiredRules(result, ["npx-cold-start"]);
  });

  test("npx <pkg> --yes (suffix form) also scores 2", () => {
    const config = buildConfig(
      [
        "      - id: cspell-suffix",
        "        entry: npx cspell@9 --yes --no-progress",
        "        language: system",
        "        files: '\\.md$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(2);
    expectFiredRules(result, ["npx-cold-start"]);
  });

  test("bash -lc scores 3 (login shell loads profiles)", () => {
    const config = buildConfig(
      [
        "      - id: login-shell-bad",
        "        entry: bash -lc 'echo hi'",
        "        language: system",
        "        files: '\\.md$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(3);
    expectFiredRules(result, ["bash-login-shell"]);
  });

  test("bash --login -c scores 3 (long-form login shell)", () => {
    const config = buildConfig(
      [
        "      - id: login-shell-longform",
        "        entry: bash --login -c 'echo hi'",
        "        language: system",
        "        files: '\\.md$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(3);
    expectFiredRules(result, ["bash-login-shell"]);
  });

  test("plain bash -c scores 0 (no login profile cost)", () => {
    const config = buildConfig(
      [
        "      - id: plain-bash",
        "        entry: bash -c 'echo hi'",
        "        language: system",
        "        files: '\\.md$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(0);
  });

  test("node scripts/run-managed-foo.js scores 3 (double-spawn anti-pattern)", () => {
    const config = buildConfig(
      [
        "      - id: managed-wrapper-bad",
        "        entry: node scripts/run-managed-foo.js --flag",
        "        language: system",
        "        files: '\\.md$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(3);
    expectFiredRules(result, ["node-double-spawn"]);
  });

  test("run-managed-jest.js is exempt from the double-spawn rule (orchestration is justified)", () => {
    const config = buildConfig(
      [
        "      - id: managed-jest",
        "        entry: node scripts/run-managed-jest.js",
        "        language: system",
        "        files: '\\.js$'",
        "        stages:",
        "          - pre-push"
      ].join("\n")
    );
    const result = scoreConfig(config);
    // pre-push only -> not scored at all.
    expect(result.totalScore).toBe(0);
  });

  test("run-managed-prettier.js is NOT exempt from the double-spawn rule (round-4)", () => {
    // Round-4 removed the prettier carve-out: the prettier hook now uses
    // the inlined cspell/markdownlint pattern, and any future hook that
    // re-introduces the wrapper at hook entry should be flagged the same
    // way every other managed wrapper is. Only `run-managed-jest` retains
    // its exemption (and only because it lives at pre-push).
    const config = buildConfig(
      [
        "      - id: managed-prettier",
        "        entry: node scripts/run-managed-prettier.js --write",
        "        language: system",
        "        files: '\\.md$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(3);
    expectFiredRules(result, ["node-double-spawn"]);
  });

  test("npm run <script> scores 3 (npm wrapper adds startup cost)", () => {
    const config = buildConfig(
      [
        "      - id: npm-run-bad",
        "        entry: npm run lint:markdown",
        "        language: system",
        "        files: '\\.md$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(3);
    expectFiredRules(result, ["npm-run-at-hook"]);
  });

  test("npm run validate:npm-meta double-counts (npm-spawn AND npm-run-at-hook)", () => {
    // 'npm run validate:npm-meta' was a known regression vector -- both
    // the npm-spawn rule and the npm-run-at-hook rule should fire.
    const config = buildConfig(
      [
        "      - id: heavy-npm-run",
        "        entry: npm run validate:npm-meta",
        "        language: system",
        "        files: '\\.json$'"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(8);
    expectFiredRules(result, ["npm-spawn", "npm-run-at-hook"]);
  });

  test("hooks defaulting to pre-commit (no stages: declared) ARE scored", () => {
    const config = buildConfig(
      [
        "      - id: implicit-precommit",
        "        entry: node scripts/scan.js",
        "        language: system",
        "        pass_filenames: false"
      ].join("\n")
    );
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
  });

  test("multiple rules sum on one hook", () => {
    const config = buildConfig(
      [
        "      - id: bad-hook",
        "        entry: npm exec --yes some-tool",
        "        language: system",
        "        always_run: true",
        "        pass_filenames: false"
      ].join("\n")
    );
    const result = scoreConfig(config);
    // scans-the-world (5) + always-run (5) + npm-spawn (5) = 15
    expect(result.totalScore).toBe(15);
    expect(result.perHookScores[0].reasons).toHaveLength(3);
  });

  test("totalScore aggregates across hooks", () => {
    const config = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      - id: hook-a",
      "        entry: node scripts/a.js",
      "        language: system",
      "        files: '\\.js$'",
      "        pass_filenames: false",
      "      - id: hook-b",
      "        entry: node scripts/b.js",
      "        language: system",
      "        files: '\\.js$'",
      "        pass_filenames: false"
    ].join("\n");
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(6);
    expect(result.perHookScores).toHaveLength(2);
  });

  // --- Substantive-reason policy (CRITICAL #1) ---------------------------

  describe("isReasonSubstantive", () => {
    test("rejects empty reason", () => {
      expect(isReasonSubstantive("").valid).toBe(false);
      expect(isReasonSubstantive("   ").valid).toBe(false);
    });

    test("rejects single-character reason", () => {
      const result = isReasonSubstantive("x");
      expect(result.valid).toBe(false);
      expect(result.error).toMatch(/shorter than/);
    });

    test("rejects reason shorter than minimum", () => {
      const padded = "a".repeat(MIN_REASON_LENGTH - 1);
      expect(isReasonSubstantive(padded).valid).toBe(false);
    });

    test("rejects stop-words even if padded to minimum length by case", () => {
      for (const stopWord of [
        "x",
        "n/a",
        "todo",
        "tbd",
        "noop",
        "fixme",
        "idk",
        "meh",
        "ok",
        "legacy",
        "because"
      ]) {
        const result = isReasonSubstantive(stopWord);
        expect(result.valid).toBe(false);
      }
    });

    test("rejects pure punctuation/whitespace", () => {
      expect(isReasonSubstantive("!!!!!!").valid).toBe(false);
      expect(isReasonSubstantive("--------------------------").valid).toBe(false);
    });

    test("accepts a substantive reason", () => {
      expect(isReasonSubstantive(SUBSTANTIVE_REASON).valid).toBe(true);
    });
  });

  // --- Allow directive parser (CRITICAL #6) -----------------------------

  describe("parseAllowDirective (rule-ID waiver format)", () => {
    test("rejects legacy `# perf-allow: <reason>` (no brackets)", () => {
      const directive = parseAllowDirective("  # perf-allow: " + SUBSTANTIVE_REASON);
      expect(directive).not.toBeNull();
      expect(directive.valid).toBe(false);
      expect(directive.error).toMatch(/must declare which rule IDs/);
    });

    test("rejects empty bracket list", () => {
      const directive = parseAllowDirective("  # perf-allow[]: " + SUBSTANTIVE_REASON);
      expect(directive.valid).toBe(false);
      expect(directive.error).toMatch(/rule list is empty/);
    });

    test("rejects unknown rule IDs and lists valid IDs", () => {
      const directive = parseAllowDirective("  # perf-allow[bogus]: " + SUBSTANTIVE_REASON);
      expect(directive.valid).toBe(false);
      expect(directive.error).toMatch(/not recognized: bogus/);
      expect(directive.error).toMatch(/Valid IDs:/);
      expect(directive.error).toMatch(/scans-the-world/);
    });

    test("rejects substantive-failing reason even when rule IDs are valid", () => {
      const directive = parseAllowDirective("  # perf-allow[scans-the-world]: legacy");
      expect(directive.valid).toBe(false);
      expect(directive.error).toMatch(/reason rejected/);
    });

    test("accepts a valid waiver and returns the parsed rule list", () => {
      const directive = parseAllowDirective(
        "  # perf-allow[scans-the-world,always-run]: " + SUBSTANTIVE_REASON
      );
      expect(directive.valid).toBe(true);
      expect(directive.ruleIds).toEqual(["scans-the-world", "always-run"]);
      expect(directive.reason).toBe(SUBSTANTIVE_REASON);
    });
  });

  // --- Integration with scoreConfig -------------------------------------

  test("perf-allow waiver only suppresses the listed rule IDs", () => {
    // Hook fires both scans-the-world AND always-run; waiver only covers
    // scans-the-world. always-run still contributes its 5 points.
    const config = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      # perf-allow[scans-the-world]: " + SUBSTANTIVE_REASON,
      "      - id: partial-waive",
      "        entry: node scripts/scan.js",
      "        language: system",
      "        always_run: true",
      "        pass_filenames: false"
    ].join("\n");
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
    expect(result.perHookScores).toHaveLength(1);
    const hook = result.perHookScores[0];
    expect(hook.baseScore).toBe(10);
    expect(hook.finalScore).toBe(5);
    expect(hook.waivedRuleIds).toEqual(["scans-the-world"]);
    expect(hook.unwaivedReasons.map((r) => r.ruleId)).toEqual(["always-run"]);
  });

  test("perf-allow waiver fully suppresses when every fired rule is listed", () => {
    const config = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      # perf-allow[scans-the-world-with-files,npx-cold-start]: " + SUBSTANTIVE_REASON,
      "      - id: cspell-style",
      "        entry: npx --yes cspell@9",
      "        language: system",
      "        files: '\\.md$'",
      "        pass_filenames: false"
    ].join("\n");
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(0);
    expect(result.allowList).toHaveLength(1);
    expect(result.allowList[0]).toEqual(
      expect.objectContaining({
        id: "cspell-style",
        ruleIds: expect.arrayContaining(["scans-the-world-with-files", "npx-cold-start"]),
        reason: SUBSTANTIVE_REASON
      })
    );
  });

  test("rejected directive does NOT exempt the hook AND surfaces in the report", () => {
    const config = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      # perf-allow[scans-the-world]: x",
      "      - id: drive-by",
      "        entry: node scripts/scan.js",
      "        language: system",
      "        pass_filenames: false"
    ].join("\n");
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
    expect(result.rejections).toHaveLength(1);
    expect(result.rejections[0].id).toBe("drive-by");
    const report = formatReport(result);
    expect(report).toMatch(/rejected reason: 'x' on hook drive-by/);
  });

  test("rejected directive on hook with no anti-patterns is still surfaced", () => {
    // A waiver was added "drive-by" but the hook is clean; still fail loudly.
    const config = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      # perf-allow[scans-the-world]: x",
      "      - id: clean-hook",
      "        entry: node scripts/clean.js",
      "        language: system",
      "        files: '\\.js$'",
      "        stages:",
      "          - pre-commit"
    ].join("\n");
    const result = scoreConfig(config);
    // Score 0 since nothing fires, but rejection MUST be reported so the
    // bogus waiver can be removed.
    expect(result.totalScore).toBe(0);
    expect(result.rejections).toHaveLength(1);
    expect(result.rejections[0].id).toBe("clean-hook");
  });

  test("legacy `# perf-allow: <reason>` is rejected even with a substantive reason", () => {
    const config = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      # perf-allow: " + SUBSTANTIVE_REASON,
      "      - id: legacy",
      "        entry: node scripts/scan.js",
      "        language: system",
      "        pass_filenames: false"
    ].join("\n");
    const result = scoreConfig(config);
    expect(result.totalScore).toBe(5);
    expect(result.rejections).toHaveLength(1);
    expect(result.rejections[0].error).toMatch(/must declare which rule IDs/);
  });

  test("perf-allow with empty reason does NOT exempt the hook", () => {
    const config = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      # perf-allow[scans-the-world]: ",
      "      - id: empty-reason",
      "        entry: node scripts/scan.js",
      "        language: system",
      "        pass_filenames: false"
    ].join("\n");
    const result = scoreConfig(config);
    expect(result.totalScore).toBeGreaterThan(0);
    expect(result.allowList).toHaveLength(0);
    expect(result.rejections).toHaveLength(1);
  });

  test("perf-allow with whitespace-only reason does NOT exempt the hook", () => {
    const config = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      # perf-allow[scans-the-world]:    ",
      "      - id: whitespace-reason",
      "        entry: node scripts/scan.js",
      "        language: system",
      "        pass_filenames: false"
    ].join("\n");
    const result = scoreConfig(config);
    expect(result.totalScore).toBeGreaterThan(0);
    expect(result.allowList).toHaveLength(0);
    expect(result.rejections).toHaveLength(1);
  });

  // --- Per-hook ceiling (round-5) ---------------------------------------
  //
  // The total budget catches accumulated drift. The per-hook ceiling
  // catches single-rule regressions that would otherwise hide under the
  // cumulative slack: any one hook with a final score above
  // PER_HOOK_CEILING fails on its own.

  describe("per-hook ceiling", () => {
    test("PER_HOOK_CEILING is 3 (above any single =< 3 rule, below any single >= 5 rule)", () => {
      expect(PER_HOOK_CEILING).toBe(3);
      // The ceiling MUST stay strictly less than the total budget so
      // a single bad hook trips the per-hook test before the total
      // budget would notice it.
      expect(PER_HOOK_CEILING).toBeLessThan(PERF_BUDGET);
    });

    test("a single hook with `bash -lc` (5 points) violates the per-hook ceiling", () => {
      const config = buildConfig(
        [
          "      - id: bash-login-bad",
          "        entry: bash -lc 'echo hi'",
          "        language: system",
          "        files: '\\.md$'"
        ].join("\n")
      );
      const result = scoreConfig(config);
      // The single rule here is bash-login-shell at 3 points -- which
      // is == PER_HOOK_CEILING (3), so it does NOT violate. Verify
      // that boundary explicitly here and exercise an actually-over
      // case below.
      expect(result.totalScore).toBe(3);
      expect(result.perHookViolations).toEqual([]);

      // Now compose with a second rule on the same hook so the final
      // score is 5 (npm-spawn) -- strictly above the ceiling.
      const overConfig = buildConfig(
        [
          "      - id: bash-login-and-npm-spawn",
          "        entry: bash -lc 'npm install'",
          "        language: system",
          "        files: '\\.md$'"
        ].join("\n")
      );
      const overResult = scoreConfig(overConfig);
      expect(overResult.totalScore).toBe(8);
      expect(overResult.perHookViolations).toHaveLength(1);
      expect(overResult.perHookViolations[0]).toEqual(
        expect.objectContaining({
          id: "bash-login-and-npm-spawn",
          score: 8,
          ceiling: PER_HOOK_CEILING
        })
      );
      const ruleIds = overResult.perHookViolations[0].contributingRules.map((r) => r.ruleId);
      expect(ruleIds.sort()).toEqual(["bash-login-shell", "npm-spawn"].sort());
    });

    test("a single hook with multiple rules summing to 5 violates the per-hook ceiling", () => {
      // npm-run-at-hook (3) + npx-cold-start (2) = 5 on one entry.
      const config = buildConfig(
        [
          "      - id: stacked-medium-rules",
          "        entry: npm run lint && npx --yes cspell@9",
          "        language: system",
          "        files: '\\.md$'"
        ].join("\n")
      );
      const result = scoreConfig(config);
      expect(result.totalScore).toBe(5);
      expect(result.perHookViolations).toHaveLength(1);
      expect(result.perHookViolations[0].score).toBe(5);
      const ruleIds = result.perHookViolations[0].contributingRules.map((r) => r.ruleId);
      expect(ruleIds.sort()).toEqual(["npm-run-at-hook", "npx-cold-start"].sort());
    });

    test("a hook fully waived by perf-allow does NOT violate the per-hook ceiling", () => {
      // A waiver that covers every fired rule drops the final score
      // to 0; the ceiling check uses the post-waiver score.
      const config = [
        "repos:",
        "  - repo: local",
        "    hooks:",
        "      # perf-allow[scans-the-world,always-run]: " + SUBSTANTIVE_REASON,
        "      - id: fully-waived",
        "        entry: node scripts/scan.js",
        "        language: system",
        "        always_run: true",
        "        pass_filenames: false"
      ].join("\n");
      const result = scoreConfig(config);
      expect(result.totalScore).toBe(0);
      expect(result.perHookViolations).toEqual([]);
    });

    test("a hook with score == PER_HOOK_CEILING (3) does NOT violate (boundary)", () => {
      // bash-login-shell alone = 3 points, == ceiling, must NOT trip.
      const config = buildConfig(
        [
          "      - id: at-ceiling",
          "        entry: bash -lc 'echo hi'",
          "        language: system",
          "        files: '\\.md$'"
        ].join("\n")
      );
      const result = scoreConfig(config);
      expect(result.totalScore).toBe(3);
      expect(result.perHookViolations).toEqual([]);
    });

    test("a hook scoring above ceiling but below total budget still trips per-hook (catches single-rule regression)", () => {
      // npm-spawn alone = 5 points; under the total budget (10) but
      // strictly above per-hook ceiling (3). This is the canonical
      // regression case the per-hook ceiling exists to catch.
      const config = buildConfig(
        [
          "      - id: solo-npm-install",
          "        entry: npm install --no-save",
          "        language: system",
          "        files: '\\.json$'"
        ].join("\n")
      );
      const result = scoreConfig(config);
      expect(result.totalScore).toBe(5);
      expect(result.totalScore).toBeLessThanOrEqual(PERF_BUDGET);
      expect(result.perHookViolations).toHaveLength(1);
      expect(result.perHookViolations[0].id).toBe("solo-npm-install");
    });

    test("formatReport surfaces per-hook violations clearly with rule IDs", () => {
      const config = buildConfig(
        [
          "      - id: noisy",
          "        entry: bash -lc 'npm install'",
          "        language: system",
          "        files: '\\.md$'"
        ].join("\n")
      );
      const result = scoreConfig(config);
      const report = formatReport(result);
      expect(report).toMatch(/Per-hook ceiling violations/);
      expect(report).toMatch(/noisy.*score=8.*ceiling=3/);
      expect(report).toMatch(/bash-login-shell\(\+3\)/);
      expect(report).toMatch(/npm-spawn\(\+5\)/);
      expect(report).toMatch(/remediation:/);
    });

    test("scoreConfig always returns a perHookViolations array (clean config -> empty)", () => {
      const config = buildConfig(
        [
          "      - id: clean",
          "        entry: node scripts/clean.js",
          "        language: system",
          "        files: '\\.js$'"
        ].join("\n")
      );
      const result = scoreConfig(config);
      expect(Array.isArray(result.perHookViolations)).toBe(true);
      expect(result.perHookViolations).toEqual([]);
    });
  });
});
