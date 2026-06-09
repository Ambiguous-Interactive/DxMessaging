/**
 * @fileoverview Unit and integration tests for the agentic PostToolUse guard
 * scripts/hooks/yaml-line-length-guard.js.
 *
 * The guard auto-fixes YAML line-length violations during editing and reports
 * residual ones back to the agent in-loop. These tests pin both the pure core
 * (guardContent / buildContext / looksLikeYamlPath) and the real stdin->stdout
 * contract by spawning the script with a synthetic PostToolUse payload.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const GUARD_SCRIPT = path.resolve(__dirname, "..", "hooks", "yaml-line-length-guard.js");
const {
  looksLikeYamlPath,
  guardContent,
  buildContext,
  SKILL_POINTER
} = require("../hooks/yaml-line-length-guard.js");

const POLICY = {
  max: 200,
  allowNonBreakableWords: true,
  allowNonBreakableInlineMappings: true
};

// A long literal that, embedded in a pwsh `run:` double-quoted string, pushes
// the physical line past 200 columns but can be safely split on plain spaces.
const LONG_BODY =
  "this is a long powershell error message that keeps going and going well past " +
  "the two hundred character ceiling so that yamllint would certainly flag it as " +
  "a line length violation for sure indeed yes";

function fixablePwshYaml() {
  return [
    "steps:",
    "  - name: emit",
    "    shell: pwsh",
    "    run: |",
    `      Write-Output "${LONG_BODY}"`,
    ""
  ].join("\n");
}

function unfixableBashYaml() {
  // A bash step (not pwsh) with a long line: structurally ineligible for the
  // pwsh rewrite, so it must be left byte-identical and reported.
  return [
    "steps:",
    "  - name: run",
    "    shell: bash",
    "    run: |",
    `      echo "${LONG_BODY} plus even more trailing text to keep it well over the limit"`,
    ""
  ].join("\n");
}

function runGuard(payloadObject) {
  const result = spawnSync("node", [GUARD_SCRIPT], {
    input: JSON.stringify(payloadObject),
    encoding: "utf8"
  });
  return result;
}

describe("yaml-line-length-guard pure core", () => {
  test("looksLikeYamlPath matches .yml/.yaml case-insensitively only", () => {
    expect(looksLikeYamlPath("/a/b.yml")).toBe(true);
    expect(looksLikeYamlPath("/a/b.yaml")).toBe(true);
    expect(looksLikeYamlPath("/a/B.YML")).toBe(true);
    expect(looksLikeYamlPath("/a/b.txt")).toBe(false);
    expect(looksLikeYamlPath("/a/b.json")).toBe(false);
    expect(looksLikeYamlPath(undefined)).toBe(false);
  });

  test("guardContent auto-fixes a fixable pwsh line and reports nothing residual", () => {
    const result = guardContent(fixablePwshYaml(), POLICY);
    expect(result.changed).toBe(true);
    expect(result.remaining).toHaveLength(0);
    // The rewrite uses the parenthesized `+` concatenation form.
    expect(result.content).toContain('Write-Output ("');
    expect(result.content).toContain('" +');
  });

  test("guardContent leaves an unfixable line byte-identical and reports it", () => {
    const input = unfixableBashYaml();
    const result = guardContent(input, POLICY);
    expect(result.remaining.length).toBeGreaterThan(0);
    // The long echo line is preserved exactly.
    expect(result.content).toContain(`echo "${LONG_BODY}`);
  });

  test("guardContent is a no-op for clean YAML", () => {
    const clean = "name: clean\non:\n  push:\n";
    const result = guardContent(clean, POLICY);
    expect(result.changed).toBe(false);
    expect(result.remaining).toHaveLength(0);
  });

  test("buildContext returns null when nothing changed and nothing remains", () => {
    expect(buildContext("a.yml", false, [], 200)).toBeNull();
  });

  test("buildContext names remaining line numbers and the skill pointer", () => {
    const message = buildContext("a.yml", true, [{ line: 10, length: 250 }], 200);
    expect(message).toContain("a.yml");
    expect(message).toContain("10");
    expect(message).toContain(SKILL_POINTER);
  });
});

describe("yaml-line-length-guard PostToolUse integration", () => {
  let tempDir;

  beforeEach(() => {
    tempDir = makeTempDir("yaml-guard");
  });

  afterEach(() => {
    cleanupDir(tempDir);
  });

  test("auto-fixes (a), leaves (b), emits additionalContext naming (b) + skill, exit 0", () => {
    // Combine a fixable pwsh step and an unfixable bash step in one file.
    const filePath = path.join(tempDir, "sample.yml");
    const content = [
      "steps:",
      "  - name: emit",
      "    shell: pwsh",
      "    run: |",
      `      Write-Output "${LONG_BODY}"`,
      "  - name: run",
      "    shell: bash",
      "    run: |",
      `      echo "${LONG_BODY} plus even more trailing text to keep it well over the limit"`,
      ""
    ].join("\n");
    fs.writeFileSync(filePath, content, "utf8");

    const result = runGuard({ tool_name: "Edit", tool_input: { file_path: filePath } });
    expect(result.status).toBe(0);

    // (a) the pwsh line was rewritten on disk.
    const after = fs.readFileSync(filePath, "utf8");
    expect(after).toContain('Write-Output ("');
    // (b) the bash line is left byte-identical.
    expect(after).toContain(`echo "${LONG_BODY}`);

    // stdout is a single JSON object with hookSpecificOutput.additionalContext.
    const parsed = JSON.parse(result.stdout.trim());
    expect(parsed.hookSpecificOutput.hookEventName).toBe("PostToolUse");
    const ctx = parsed.hookSpecificOutput.additionalContext;
    expect(ctx).toContain("auto-reformatted");
    expect(ctx).toContain(SKILL_POINTER);
    // The residual line number for (b) is reported. After the pwsh rewrite the
    // bash echo line shifts; assert at least one numeric line is named.
    expect(ctx).toMatch(/line\(s\)\s+\d+/);
  });

  test("non-YAML path: exit 0, no output, file untouched", () => {
    const filePath = path.join(tempDir, "note.txt");
    const original = "plain text untouched\n";
    fs.writeFileSync(filePath, original, "utf8");

    const result = runGuard({ tool_name: "Write", tool_input: { file_path: filePath } });
    expect(result.status).toBe(0);
    expect(result.stdout.trim()).toBe("");
    expect(fs.readFileSync(filePath, "utf8")).toBe(original);
  });

  test("missing file: exit 0, no output", () => {
    const filePath = path.join(tempDir, "ghost.yml");
    const result = runGuard({ tool_name: "Edit", tool_input: { file_path: filePath } });
    expect(result.status).toBe(0);
    expect(result.stdout.trim()).toBe("");
  });

  test("clean YAML: exit 0, no output, file untouched", () => {
    const filePath = path.join(tempDir, "clean.yml");
    const original = "name: clean\non:\n  push:\n";
    fs.writeFileSync(filePath, original, "utf8");

    const result = runGuard({ tool_input: { file_path: filePath } });
    expect(result.status).toBe(0);
    expect(result.stdout.trim()).toBe("");
    expect(fs.readFileSync(filePath, "utf8")).toBe(original);
  });

  test("a fixable-only file emits a reformat notice with no residual violations", () => {
    const filePath = path.join(tempDir, "only-fixable.yml");
    fs.writeFileSync(filePath, fixablePwshYaml(), "utf8");

    const result = runGuard({ tool_input: { file_path: filePath } });
    expect(result.status).toBe(0);

    const parsed = JSON.parse(result.stdout.trim());
    const ctx = parsed.hookSpecificOutput.additionalContext;
    expect(ctx).toContain("auto-reformatted");
    expect(ctx).not.toContain("could not be auto-fixed");
  });
});
