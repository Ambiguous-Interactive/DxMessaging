/**
 * @fileoverview Locks the prevention layers that keep YAML line-length
 * violations (yamllint `line-length: max 200`) from ever reaching the
 * last-resort yamllint hook.
 *
 * Each test below pins one wiring deliverable so the class of failure cannot
 * silently regress:
 *   - The `fix-yaml-block-scalar-line-length` pre-commit hook exists, mirrors
 *     the comment fixer, and runs BEFORE yamllint.
 *   - The yaml-format-lint CI workflow gates on the Node check-mode fixers.
 *   - The workflow cspell glob covers `.github/actions/**` (the original gap).
 *   - The `.llm/context.md` YAML rule covers `.github/actions` (the agentic
 *     root cause).
 *   - `.claude/settings.json` carries the PostToolUse guard and NO permissions
 *     block (turns the no-auto-approve rule into automation).
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("../lib/quote-parser");
const {
  findHookBlock,
  findAllHookBlocks,
  extractStagesFromHookBlock
} = require("../lib/precommit-yaml");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const PRE_COMMIT_CONFIG = path.join(REPO_ROOT, ".pre-commit-config.yaml");
const CI_WORKFLOW = path.join(REPO_ROOT, ".github", "workflows", "yaml-format-lint.yml");
const PACKAGE_JSON = path.join(REPO_ROOT, "package.json");
const CONTEXT_MD = path.join(REPO_ROOT, ".llm", "context.md");
const CLAUDE_SETTINGS = path.join(REPO_ROOT, ".claude", "settings.json");
const GUARD_SKILL = path.join(REPO_ROOT, ".llm", "skills", "github-actions", "yaml-line-length.md");

function readConfigLines() {
  return normalizeToLf(fs.readFileSync(PRE_COMMIT_CONFIG, "utf8")).split("\n");
}

describe("YAML line-length prevention wiring", () => {
  describe("pre-commit: fix-yaml-block-scalar-line-length hook", () => {
    const configLines = readConfigLines();

    test("the hook exists with the correct entry/files/stage shape", () => {
      const block = findHookBlock(configLines, "fix-yaml-block-scalar-line-length");
      expect(block).not.toBeNull();

      const blockText = block.lines.join("\n");
      expect(blockText).toContain(
        "node scripts/run-and-restage.js\n          node scripts/fix-yaml-block-scalar-line-length.js --"
      );
      expect(blockText).toContain("language: system");
      expect(blockText).toContain("files: '(?i)\\.(ya?ml)$'");
      expect(blockText).toContain("pass_filenames: true");
      expect(blockText).toContain("require_serial: true");

      const stages = extractStagesFromHookBlock(block);
      expect(stages).toEqual(["pre-commit"]);
    });

    test("it mirrors the comment fixer (both restage staged YAML before yamllint)", () => {
      const commentBlock = findHookBlock(configLines, "fix-yaml-comments-line-length");
      const blockBlock = findHookBlock(configLines, "fix-yaml-block-scalar-line-length");
      expect(commentBlock).not.toBeNull();
      expect(blockBlock).not.toBeNull();

      for (const block of [commentBlock, blockBlock]) {
        const blockText = block.lines.join("\n");
        expect(blockText).toContain("node scripts/run-and-restage.js");
        expect(blockText).toContain("files: '(?i)\\.(ya?ml)$'");
        expect(blockText).toContain("require_serial: true");
        expect(extractStagesFromHookBlock(block)).toEqual(["pre-commit"]);
      }
    });

    test("the block-scalar fixer runs BEFORE yamllint in the pre-commit pass", () => {
      const allBlocks = findAllHookBlocks(configLines);
      const order = allBlocks.map((block) => block.id);
      const fixerIndex = order.indexOf("fix-yaml-block-scalar-line-length");
      const yamllintIndex = order.indexOf("yamllint");

      expect(fixerIndex).toBeGreaterThanOrEqual(0);
      expect(yamllintIndex).toBeGreaterThanOrEqual(0);
      expect(fixerIndex).toBeLessThan(yamllintIndex);
    });

    test("the comment fixer also precedes the new block-scalar fixer for stable ordering", () => {
      const allBlocks = findAllHookBlocks(configLines);
      const order = allBlocks.map((block) => block.id);
      expect(order.indexOf("fix-yaml-comments-line-length")).toBeLessThan(
        order.indexOf("fix-yaml-block-scalar-line-length")
      );
    });
  });

  describe("CI: yaml-format-lint workflow gates on the Node fixers", () => {
    const workflow = normalizeToLf(fs.readFileSync(CI_WORKFLOW, "utf8"));

    test("runs both check:yaml:comments and check:yaml:lines", () => {
      expect(workflow).toContain("npm run check:yaml:comments");
      expect(workflow).toContain("npm run check:yaml:lines");
    });

    test("keeps the yamllint backstop step", () => {
      expect(workflow).toContain("ibiqlik/action-yamllint");
    });

    test("the Node check step runs before yamllint", () => {
      const linesIndex = workflow.indexOf("npm run check:yaml:lines");
      const yamllintIndex = workflow.indexOf("ibiqlik/action-yamllint");
      expect(linesIndex).toBeGreaterThanOrEqual(0);
      expect(yamllintIndex).toBeGreaterThanOrEqual(0);
      expect(linesIndex).toBeLessThan(yamllintIndex);
    });
  });

  describe("cspell: workflow glob covers .github/actions/** (THE gap)", () => {
    const pkg = JSON.parse(fs.readFileSync(PACKAGE_JSON, "utf8"));
    const command = pkg.scripts["check:workflow-cspell"];

    test("includes .github/actions/** for both yml and yaml", () => {
      expect(command).toContain(".github/actions/**/*.yml");
      expect(command).toContain(".github/actions/**/*.yaml");
    });

    test("still includes .github/workflows/** for both yml and yaml", () => {
      expect(command).toContain(".github/workflows/**/*.yml");
      expect(command).toContain(".github/workflows/**/*.yaml");
    });

    test("uses managed cspell with the required flags", () => {
      expect(command).toContain("node scripts/run-managed-cspell.js");
      expect(command).toContain("--no-progress");
      expect(command).toContain("--no-summary");
    });
  });

  describe(".llm/context.md: YAML rule covers .github/actions (THE agentic gap)", () => {
    const context = normalizeToLf(fs.readFileSync(CONTEXT_MD, "utf8"));
    const yamlRuleLine = context
      .split("\n")
      .find((line) => line.includes("check:yaml") && line.includes(".yml"));

    test("the YAML-edit rule mentions .github/actions", () => {
      expect(yamlRuleLine).toBeTruthy();
      expect(yamlRuleLine).toContain(".github/actions");
    });

    test("the rule mentions the zero-touch auto-fixers", () => {
      expect(context).toContain("npm run format:yaml:comments");
      expect(context).toContain("npm run format:yaml:lines");
    });

    test("the rule references the new skill page", () => {
      expect(context).toContain("./skills/github-actions/yaml-line-length.md");
    });

    test("the new skill page exists", () => {
      expect(fs.existsSync(GUARD_SKILL)).toBe(true);
    });
  });

  describe(".claude/settings.json: hooks-only guard wiring", () => {
    test("exists and is valid JSON", () => {
      expect(fs.existsSync(CLAUDE_SETTINGS)).toBe(true);
      const parsed = JSON.parse(fs.readFileSync(CLAUDE_SETTINGS, "utf8"));
      expect(parsed).toEqual(expect.any(Object));
    });

    test("declares a PostToolUse hook matching Edit/Write/MultiEdit", () => {
      const parsed = JSON.parse(fs.readFileSync(CLAUDE_SETTINGS, "utf8"));
      expect(parsed.hooks).toBeDefined();
      expect(Array.isArray(parsed.hooks.PostToolUse)).toBe(true);

      const matcherEntry = parsed.hooks.PostToolUse.find(
        (entry) => entry.matcher === "Edit|Write|MultiEdit"
      );
      expect(matcherEntry).toBeDefined();

      const command = matcherEntry.hooks.find(
        (hook) => hook.type === "command" && /yaml-line-length-guard\.js/.test(hook.command)
      );
      expect(command).toBeDefined();
      expect(command.command).toContain("$CLAUDE_PROJECT_DIR");
      expect(command.command).toMatch(/^node\b/);
    });

    test("has NO permissions block (no committed auto-approval)", () => {
      const parsed = JSON.parse(fs.readFileSync(CLAUDE_SETTINGS, "utf8"));
      expect(parsed.permissions).toBeUndefined();
      expect(Object.keys(parsed)).toEqual(["hooks"]);
    });
  });
});
