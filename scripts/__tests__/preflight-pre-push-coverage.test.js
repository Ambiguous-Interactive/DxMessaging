/**
 * @fileoverview Static enforcement that `npm run preflight:pre-push` covers
 * every hook declared at `stages: pre-push` in `.pre-commit-config.yaml`.
 *
 * Phase 5 of the doctor / preflight rollout. Phase 3 added the chained
 * `preflight:pre-commit` + `pre-commit run --hook-stage pre-push --all-files`
 * script. This test locks the coverage so that:
 *
 *   - If a contributor adds a new pre-push hook to .pre-commit-config.yaml
 *     and forgets to widen `preflight:pre-push`, the static test fails with
 *     a pointer to the missing hook id.
 *   - The "bulk" form (`--all-files`) is treated as covering everything --
 *     that is the canonical shape today, so the coverage assertion succeeds
 *     by recognizing that single substring.
 *   - The chain calls `preflight:pre-commit` first, so the fast preflight
 *     runs before the full one and aborts on the cheapest possible failure.
 *
 * If a hook truly must be skipped from the bulk run, add its id to
 * `PREFLIGHT_EXEMPT_HOOKS` below WITH a cited reason. Every exempt entry is
 * sanity-checked against `.pre-commit-config.yaml` to catch dead allow-list
 * entries (a hook that was renamed or removed must be removed here too).
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("../lib/quote-parser");
const { findAllHookBlocks, extractStagesFromHookBlock } = require("../lib/precommit-yaml");
const {
  STEPS: PREPUSH_PREFLIGHT_STEPS,
  main: runPrePushPreflight
} = require("../run-prepush-preflight");

/**
 * Allow-list of pre-push hook ids that are intentionally NOT invoked by
 * `npm run preflight:pre-push`. Each entry must cite a reason. Empty for
 * now -- the current preflight script covers every pre-push hook via the
 * bulk `--all-files` invocation, so no exemptions are needed.
 *
 * Format: { id: "<hook-id>", reason: "<why exempt>" }
 */
const PREFLIGHT_EXEMPT_HOOKS = [];

const REPO_ROOT = path.resolve(__dirname, "../..");
const CONFIG_PATH = path.join(REPO_ROOT, ".pre-commit-config.yaml");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");
const ALL_FILES_CSPELL_COMMAND = "npm run check:cspell:all";

const PRE_COMMIT_RUNNER_RE = /(?:\bpre-commit\b|\bnode\s+scripts\/ensure-pre-commit\.js)\s+run\b/;
const BULK_PRE_PUSH_TOKENS = [PRE_COMMIT_RUNNER_RE, /--hook-stage\s+pre-push\b/, /--all-files\b/];

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function scriptHasBulkPrePushInvocation(script) {
  return BULK_PRE_PUSH_TOKENS.every((rx) => rx.test(script));
}

function stepText(step) {
  const command = step.command === process.execPath ? "node" : step.command;
  return [command, ...step.args].join(" ");
}

function serialPrePushText() {
  return PREPUSH_PREFLIGHT_STEPS.map(stepText).join(" && ");
}

function loadConfigLines() {
  const raw = fs.readFileSync(CONFIG_PATH, "utf8");
  return normalizeToLf(raw).split("\n");
}

function loadPackageJson() {
  const raw = fs.readFileSync(PACKAGE_JSON_PATH, "utf8");
  return JSON.parse(raw);
}

function getPrePushHookIds(configLines) {
  const blocks = findAllHookBlocks(configLines);
  const ids = [];
  for (const block of blocks) {
    const stages = extractStagesFromHookBlock(block);
    if (stages.includes("pre-push")) {
      ids.push(block.id);
    }
  }
  return ids;
}

function getAllHookIds(configLines) {
  const blocks = findAllHookBlocks(configLines);
  return blocks.map((block) => block.id);
}

describe("preflight:pre-push static coverage", () => {
  test("preflight:pre-push npm script exists and is a non-empty string", () => {
    const pkg = loadPackageJson();
    expect(pkg.scripts).toBeDefined();
    const value = pkg.scripts["preflight:pre-push"];
    expect(value).toBe("node scripts/run-prepush-preflight.js");
  });

  test("preflight:pre-push runner calls preflight:pre-commit first", () => {
    // The fast preflight gate is intentionally first in the chain so a
    // cheap failure (e.g., yamllint, formatter, a sub-second validator)
    // surfaces before the multi-minute pre-push sweep begins. If a
    // future refactor reorders these, the agent loses its fast-feedback
    // signal and pre-push doctor reports become misleading.
    const script = serialPrePushText();
    expect(script).toContain("npm run preflight:pre-commit");

    const preCommitIndex = script.indexOf("npm run preflight:pre-commit");
    const prePushIndex = script.indexOf("run --hook-stage pre-push");
    expect(preCommitIndex).toBeGreaterThanOrEqual(0);
    expect(prePushIndex).toBeGreaterThan(preCommitIndex);
  });

  test("preflight:pre-push runs all-file cspell before hook parity", () => {
    // The pre-push cspell hook is intentionally pre-push-only for commit
    // latency, but agentic workflows still need an explicit non-hook command
    // before the bulk hook sweep. This catches spelling vocabulary drift with
    // the managed cspell runner before pre-commit delegates to the hook.
    const script = serialPrePushText();
    expect(script).toContain(ALL_FILES_CSPELL_COMMAND);

    const cspellIndex = script.indexOf(ALL_FILES_CSPELL_COMMAND);
    const preCommitIndex = script.indexOf("npm run preflight:pre-commit");
    const prePushIndex = script.indexOf("run --hook-stage pre-push");
    expect(cspellIndex).toBeGreaterThan(preCommitIndex);
    expect(prePushIndex).toBeGreaterThan(cspellIndex);
  });

  test("preflight:pre-push writes the pre-push skip stamp only after hook parity", () => {
    const calls = [];
    const spawnFn = jest.fn((command, args) => {
      calls.push(stepText({ command, args }));
      return { status: 0 };
    });
    const writeHookValidationStampFn = jest.fn();

    const status = runPrePushPreflight({ spawnFn, writeHookValidationStampFn });

    expect(status).toBe(0);
    expect(calls).toEqual(PREPUSH_PREFLIGHT_STEPS.map(stepText));
    expect(writeHookValidationStampFn).toHaveBeenCalledWith(REPO_ROOT, "pre-push");
  });

  test("preflight:pre-push does not write a skip stamp when any validation step fails", () => {
    const spawnFn = jest.fn((_command, args) => ({
      status: args.includes("check:cspell:all") ? 1 : 0
    }));
    const writeHookValidationStampFn = jest.fn();

    const status = runPrePushPreflight({ spawnFn, writeHookValidationStampFn });

    expect(status).toBe(1);
    expect(writeHookValidationStampFn).not.toHaveBeenCalled();
    expect(spawnFn).toHaveBeenCalledTimes(2);
  });

  test("preflight:pre-push clears caller SKIP before authoritative validation steps", () => {
    const originalSkip = process.env.SKIP;
    const originalLowerSkip = process.env.skip;
    const originalMixedSkip = process.env.SkIp;
    process.env.SKIP = "script-tests,cspell";
    process.env.skip = "validate-untracked-policy";
    process.env.SkIp = "yamllint";
    try {
      const envs = [];
      const spawnFn = jest.fn((_command, _args, options) => {
        envs.push(options.env);
        return { status: 0 };
      });
      const writeHookValidationStampFn = jest.fn();

      const status = runPrePushPreflight({ spawnFn, writeHookValidationStampFn });

      expect(status).toBe(0);
      expect(envs).toHaveLength(PREPUSH_PREFLIGHT_STEPS.length);
      for (const env of envs) {
        expect(env.SKIP).toBeUndefined();
        expect(env.skip).toBeUndefined();
        expect(env.SkIp).toBeUndefined();
      }
      expect(writeHookValidationStampFn).toHaveBeenCalledWith(REPO_ROOT, "pre-push");
    } finally {
      if (originalSkip === undefined) {
        delete process.env.SKIP;
      } else {
        process.env.SKIP = originalSkip;
      }
      if (originalLowerSkip === undefined) {
        delete process.env.skip;
      } else {
        process.env.skip = originalLowerSkip;
      }
      if (originalMixedSkip === undefined) {
        delete process.env.SkIp;
      } else {
        process.env.SkIp = originalMixedSkip;
      }
    }
  });

  test("preflight:pre-push covers every pre-push hook in .pre-commit-config.yaml", () => {
    const configLines = loadConfigLines();
    const prePushIds = getPrePushHookIds(configLines);
    expect(prePushIds.length).toBeGreaterThan(0);

    const script = serialPrePushText();

    // Bulk form: `pre-commit run --hook-stage pre-push --all-files`
    // (or the auto-repair wrapper `node scripts/ensure-pre-commit.js run ...`)
    // runs every hook whose stages: includes pre-push, so coverage is
    // automatic. This is the canonical shape today. The detector matches
    // the three required tokens independently so flag-order variations
    // (e.g., `--show-diff-on-failure` inserted anywhere) still resolve.
    if (scriptHasBulkPrePushInvocation(script)) {
      // Bulk form covers everything; nothing further to assert here.
      // The bulk form is the canonical shape and the explicit-form
      // branch below only fires if a future refactor switches to
      // per-hook invocations.
      return;
    }

    const exemptIds = new Set(PREFLIGHT_EXEMPT_HOOKS.map((entry) => entry.id));
    const missing = [];

    for (const hookId of prePushIds) {
      if (exemptIds.has(hookId)) {
        continue;
      }

      // Match `pre-commit run --hook-stage pre-push <hookId>` or
      // `node scripts/ensure-pre-commit.js run --hook-stage pre-push <hookId>`,
      // allowing
      // optional flags (e.g., --all-files, --files <paths>) between
      // the stage flag and the hook id, but the hook id must appear
      // as a whole token following the stage flag.
      // Escape the hook id so any regex metacharacters (`.`, `+`, etc.)
      // that future hook ids might use are matched literally rather
      // than as regex syntax. Today's ids are kebab/alnum, but the
      // protection is the whole point of this audit fence.
      const escapedId = escapeRegex(hookId);
      const explicitPattern = new RegExp(
        `(?:pre-commit|node\\s+scripts/ensure-pre-commit\\.js)\\s+run\\s+(?:--[\\w-]+(?:\\s+\\S+)?\\s+)*--hook-stage\\s+pre-push\\s+(?:--[\\w-]+(?:\\s+\\S+)?\\s+)*${escapedId}(?:\\s|$|&|;)`
      );
      if (!explicitPattern.test(script)) {
        missing.push(hookId);
      }
    }

    expect(missing).toEqual([]);
  });

  test("PREFLIGHT_EXEMPT_HOOKS entries reference real hook ids", () => {
    const configLines = loadConfigLines();
    const allIds = new Set(getAllHookIds(configLines));

    for (const entry of PREFLIGHT_EXEMPT_HOOKS) {
      expect(typeof entry.id).toBe("string");
      expect(entry.id.length).toBeGreaterThan(0);
      expect(typeof entry.reason).toBe("string");
      expect(entry.reason.trim().length).toBeGreaterThan(0);
      // Dead-entry guard: if a hook was renamed or removed, force the
      // allow-list to be updated rather than silently masking a real
      // gap in coverage.
      expect(allIds.has(entry.id)).toBe(true);
    }
  });

  test("at least one pre-push hook exists (sanity)", () => {
    // Defensive: if the parser silently returns zero hooks (e.g., a
    // breaking change to precommit-yaml.js), the coverage assertion
    // above would vacuously pass. Keep an explicit floor here.
    const configLines = loadConfigLines();
    const prePushIds = getPrePushHookIds(configLines);
    expect(prePushIds.length).toBeGreaterThanOrEqual(5);
  });
});
