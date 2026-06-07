/**
 * @fileoverview Validates required hook stage and coverage policies in .pre-commit-config.yaml.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("../lib/quote-parser");
const { findHookBlock, extractStagesFromHookBlock } = require("../lib/precommit-yaml");

describe("pre-commit hook stage policy", () => {
  const configPath = path.resolve(__dirname, "../../.pre-commit-config.yaml");
  const configContent = normalizeToLf(fs.readFileSync(configPath, "utf8"));
  const configLines = configContent.split("\n");

  test("cspell hook runs at pre-push only (perf budget)", () => {
    // Round-3 move: spell-check is a guardrail, not a formatter, and the
    // single biggest wall-clock cost in the pre-commit pipeline (~5.5s
    // per invocation, ~3.6s of which is dictionary loading). Pushing the
    // cost to pre-push keeps single-file commits under budget; CI still
    // runs the full cspell sweep across the repo. See
    // .llm/skills/performance/git-hook-performance.md.
    const cspellBlock = findHookBlock(configLines, "cspell");
    expect(cspellBlock).not.toBeNull();

    const stages = extractStagesFromHookBlock(cspellBlock);
    // Exact equality (not arrayContaining) so accidental stage drift
    // surfaces; adding pre-commit back here would re-bust the budget.
    expect(stages).toEqual(["pre-push"]);
  });

  test("run-staged-validators hook covers staged C# at pre-commit and pre-push", () => {
    // Round-3 consolidation: validate-docs-ascii, validate-doc-code-patterns,
    // and validate-docs-prose share a single Node process to eliminate
    // ~2 redundant Node spawns per hook firing (~600-1200 ms on Windows).
    // Round-4 narrows this hook to .cs only because markdown now flows
    // through run-staged-md-pipeline (which calls the same validators
    // in-process alongside the formatters).
    const block = findHookBlock(configLines, "run-staged-validators");
    expect(block).not.toBeNull();

    const stages = extractStagesFromHookBlock(block);
    expect(stages).toEqual(expect.arrayContaining(["pre-commit", "pre-push"]));

    const blockText = block.lines.join("\n");
    expect(blockText).toContain("entry: node scripts/run-staged-validators.js");
    expect(blockText).toContain("pass_filenames: true");
    expect(blockText).toContain("files: '\\.cs$'");
    expect(blockText).toContain("exclude:");
  });

  test("run-staged-md-pipeline hook owns the consolidated markdown path at pre-commit", () => {
    // Round-4 consolidation: the previous .md path (ASCII normalization,
    // markdown-structure-fix, markdown-link-fragment-list-fix, prettier
    // for .md, markdownlint, and the shared run-staged-validators) is kept
    // inside one Node process to eliminate cold Node starts per commit.
    // The hook lives at pre-commit only because markdown is auto-fixed
    // there; the .cs validator path keeps pre-push redundancy via
    // run-staged-validators.
    const block = findHookBlock(configLines, "run-staged-md-pipeline");
    expect(block).not.toBeNull();

    const stages = extractStagesFromHookBlock(block);
    expect(stages).toEqual(["pre-commit"]);

    const blockText = block.lines.join("\n");
    expect(blockText).toContain("node scripts/run-and-restage.js");
    expect(blockText).toContain("node scripts/run-staged-md-pipeline.js --");
    expect(blockText).toContain("pass_filenames: true");
    expect(blockText).toContain("require_serial: true");
    expect(blockText).toContain("files: '(?i)\\.(md|markdown)$'");
    expect(blockText).toContain("exclude:");
  });

  test("legacy per-md hooks (markdown-structure-fix, markdown-link-fragment-list-fix, markdownlint) are gone after round-4", () => {
    // The three formatter/linter blocks below were folded into
    // run-staged-md-pipeline. If a future change re-introduces them as
    // standalone pre-commit hooks, they will silently re-add 4-5 Node
    // spawns per commit and bust the wall-clock budget. Surface the
    // regression here.
    for (const id of [
      "markdown-structure-fix",
      "markdown-link-fragment-list-fix",
      "markdownlint"
    ]) {
      expect(findHookBlock(configLines, id)).toBeNull();
    }
  });

  test("prettier hook narrows files filter to JSON/asmdef/asmref/YAML (markdown is owned by run-staged-md-pipeline)", () => {
    const block = findHookBlock(configLines, "prettier");
    expect(block).not.toBeNull();
    const blockText = block.lines.join("\n");
    // Round-4: markdown was removed from this filter so the in-process
    // markdown pipeline owns the .md/.markdown path end-to-end.
    expect(blockText).toContain("files: '(?i)\\.(json|asmdef|asmref|ya?ml)$'");
    expect(blockText).not.toMatch(/files:\s*'.*\bmd\b/);
  });

  test("validate-llms-txt hook is gone (moved to CI); freshness check stays at pre-push", () => {
    // Round-3 move: the previous validate-llms-txt hook ran the full
    // generator-contract Jest suite (~9s) at every push. CI workflow
    // .github/workflows/validate-llms-txt.yml runs the same check on
    // every PR. The hook here is just the cheap freshness diff.
    const stale = findHookBlock(configLines, "validate-llms-txt");
    expect(stale).toBeNull();

    const fresh = findHookBlock(configLines, "check-llms-txt-fresh");
    expect(fresh).not.toBeNull();
    const stages = extractStagesFromHookBlock(fresh);
    expect(stages).toEqual(["pre-push"]);
    const blockText = fresh.lines.join("\n");
    expect(blockText).toContain("node scripts/update-llms-txt.js --check");
  });

  test("conflict-markers entry uses shell-neutral Node implementation", () => {
    const block = findHookBlock(configLines, "conflict-markers");
    expect(block).not.toBeNull();
    const blockText = block.lines.join("\n");
    expect(blockText).toContain("entry: node scripts/check-conflict-markers.js");
    expect(blockText).not.toContain("bash");
  });

  test("PowerShell output assertion fixer runs as a pre-commit auto-repair hook", () => {
    const block = findHookBlock(configLines, "fix-pwsh-output-assertions");
    expect(block).not.toBeNull();

    const stages = extractStagesFromHookBlock(block);
    expect(stages).toEqual(["pre-commit"]);

    const blockText = block.lines.join("\n");
    expect(blockText).toContain("node scripts/run-and-restage.js");
    expect(blockText).toContain("node scripts/fix-pwsh-output-assertions.js --");
    expect(blockText).toContain("pass_filenames: true");
    expect(blockText).toContain("require_serial: true");
    expect(blockText).toContain("files: '^scripts/(?:__tests__|lib/__tests__)/.*\\.test\\.js$'");
  });

  test("validate-npm-meta hook runs at pre-push only (perf budget)", () => {
    // Moved off pre-commit because npm pack --dry-run is too slow for the
    // single-file commit budget. Artifact-shape checks are the right
    // cadence for pre-push. See .llm/skills/performance/git-hook-performance.md.
    const npmMetaBlock = findHookBlock(configLines, "validate-npm-meta");
    expect(npmMetaBlock).not.toBeNull();

    const stages = extractStagesFromHookBlock(npmMetaBlock);
    // Exact equality (not arrayContaining) so accidental stage drift fails
    // loudly. Adding pre-commit back here would re-introduce the budget bust.
    expect(stages).toEqual(["pre-push"]);
  });

  test("dotnet-tool-restore files: regex matches the manifest path exactly", () => {
    // The hook's only purpose is to react to .config/dotnet-tools.json.
    // A regression that drops the leading anchor or accepts arbitrary
    // .json files would silently re-trigger `dotnet tool restore` on
    // every JSON change, blowing the perf budget.
    const dotnetBlock = findHookBlock(configLines, "dotnet-tool-restore");
    expect(dotnetBlock).not.toBeNull();

    const filesLine = dotnetBlock.lines.find((line) => /^\s*files:/.test(line));
    expect(filesLine).toBeTruthy();
    const filesMatch = /files:\s*'([^']+)'/.exec(filesLine);
    expect(filesMatch).not.toBeNull();
    const regex = new RegExp(filesMatch[1]);

    // Positive match.
    expect(regex.test(".config/dotnet-tools.json")).toBe(true);

    // Negative cases that an over-broad regex would accept.
    expect(regex.test("dotnet-tools.json")).toBe(false);
    expect(regex.test("foo/.config/dotnet-tools.json")).toBe(false);
    expect(regex.test(".config/dotnet-tools.json.bak")).toBe(false);
    expect(regex.test(".config/dotnet-tools-json")).toBe(false);
    expect(regex.test("a.config/dotnet-tools.json")).toBe(false);
  });

  test("validate-changelog-policy hook runs at pre-commit/pre-push and excludes internal Editor code", () => {
    const changelogPolicyBlock = findHookBlock(configLines, "validate-changelog-policy");
    expect(changelogPolicyBlock).not.toBeNull();

    const stages = extractStagesFromHookBlock(changelogPolicyBlock);
    expect(stages).toEqual(expect.arrayContaining(["pre-commit", "pre-push"]));

    const blockText = changelogPolicyBlock.lines.join("\n");
    expect(blockText).toContain("entry: node scripts/validate-changelog.js --check-coverage");
    expect(blockText).toContain("pass_filenames: false");
    expect(blockText).toContain(
      "files: '^(CHANGELOG\\.md|Runtime/|SourceGenerators/|Samples~/|Editor/)'"
    );
    expect(blockText).toContain("exclude:");
    expect(blockText).toContain("Editor/(Analyzers|Testing)/");
    expect(blockText).toContain("SourceGenerators/.*\\\\.Tests/");
    expect(blockText).toContain("SourceGenerators/.*/(bin|obj)/");
  });

  test("fix-csharp-underscore-methods hook runs at pre-commit", () => {
    const fixerBlock = findHookBlock(configLines, "fix-csharp-underscore-methods");
    expect(fixerBlock).not.toBeNull();

    const stages = extractStagesFromHookBlock(fixerBlock);
    expect(stages).toEqual(expect.arrayContaining(["pre-commit"]));

    const blockText = fixerBlock.lines.join("\n");
    expect(blockText).toContain(
      "entry: node scripts/run-and-restage.js node scripts/fix-csharp-underscore-methods.js --"
    );
    expect(blockText).not.toContain("|| true");
    expect(blockText).not.toContain("|| echo");
  });

  test("local hook entries avoid shell-specific launchers", () => {
    const entryLines = configLines.filter((line) => /^\s*entry:\s*/.test(line));
    for (const line of entryLines) {
      expect(line).not.toMatch(/entry:\s*(bash|sh|pwsh|powershell)\b/);
    }
  });

  test("script-parser-tests includes npm-meta and shell-safety regressions", () => {
    const parserTestsBlock = findHookBlock(configLines, "script-parser-tests");
    expect(parserTestsBlock).not.toBeNull();

    const blockText = parserTestsBlock.lines.join("\n");
    expect(blockText).toContain("scripts/__tests__/validate-npm-meta.test.js");
    expect(blockText).toContain("scripts/__tests__/validate-comparison-packages.test.js");
    expect(blockText).toContain("scripts/__tests__/run-managed-prettier.test.js");
    expect(blockText).toContain("scripts/__tests__/prettier-version.test.js");
    expect(blockText).toContain("scripts/__tests__/shell-command.test.js");
    expect(blockText).toContain("scripts/__tests__/detect-shell-redirection-antipattern.test.js");
    expect(blockText).toContain("scripts/__tests__/spawn-invocation-policy.test.js");
    expect(blockText).toContain("scripts/__tests__/hermetic-host-env-policy.test.js");
    expect(blockText).toContain("scripts/__tests__/cross-platform-preflight-coverage.test.js");
    expect(blockText).toContain("scripts/__tests__/path-containment-policy.test.js");
    expect(blockText).toContain("scripts/lib/__tests__/spawn-env-sandbox.test.js");
    expect(blockText).toContain("scripts/__tests__/fix-csharp-underscore-methods.test.js");
    expect(blockText).toContain("scripts/__tests__/validate-changelog.test.js");
    expect(blockText).toContain("scripts/__tests__/validate-changed-docs.test.js");
    expect(blockText).toContain("scripts/__tests__/check-conflict-markers.test.js");
    expect(blockText).toContain("scripts/__tests__/native-git-hooks.test.js");
    expect(blockText).toContain("scripts/__tests__/fix-pwsh-output-assertions.test.js");
    expect(blockText).toContain("scripts/__tests__/pwsh-output-assertion-policy.test.js");
  });

  test("native tracked hooks cover pre-commit and pre-push", () => {
    const hooksDir = path.resolve(__dirname, "../../scripts/hooks");
    for (const hookName of ["pre-commit", "pre-push"]) {
      const hookPath = path.join(hooksDir, hookName);
      expect(fs.existsSync(hookPath)).toBe(true);
      const content = fs.readFileSync(hookPath, "utf8");
      expect(content.startsWith("#!/usr/bin/env node\n")).toBe(true);
      expect(content).not.toMatch(/\b(?:bash|sh|pwsh|powershell)\b/);
    }
  });
});
