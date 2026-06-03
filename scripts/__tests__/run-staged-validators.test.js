/**
 * @fileoverview Tests for run-staged-validators.js. The script delegates the
 * actual scanning to the three underlying validators; this suite verifies the
 * batching layer (file filtering, exclude rules, error aggregation).
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  isApplicable,
  isExcluded,
  runStagedValidators,
  main,
  VALIDATORS,
  ALLOWED_EXTS
} = require("../run-staged-validators");

describe("run-staged-validators", () => {
  test("ALLOWED_EXTS gates the documentation/source extensions the script can validate", () => {
    // The YAML hook filter is narrower than this set after round-4
    // (the hook now passes only .cs files; .md flows through
    // run-staged-md-pipeline). The underlying script still accepts .md
    // and .markdown so ad-hoc invocations like
    // `node scripts/run-staged-validators.js README.md` keep working.
    expect(ALLOWED_EXTS.has(".md")).toBe(true);
    expect(ALLOWED_EXTS.has(".markdown")).toBe(true);
    expect(ALLOWED_EXTS.has(".cs")).toBe(true);
    expect(ALLOWED_EXTS.has(".js")).toBe(false);
    expect(ALLOWED_EXTS.has(".json")).toBe(false);
    expect(ALLOWED_EXTS.has(".yaml")).toBe(false);
  });

  test("VALIDATORS enumerates the three consolidated checks in stable order", () => {
    const ids = VALIDATORS.map((v) => v.id);
    expect(ids).toEqual([
      "validate-docs-ascii",
      "validate-doc-code-patterns",
      "validate-docs-prose"
    ]);
  });

  test("isExcluded matches the YAML exclude regex on common generated paths", () => {
    expect(isExcluded("Library/PackageCache/foo.md")).toBe(true);
    expect(isExcluded("Temp/scratch.md")).toBe(true);
    expect(isExcluded("node_modules/x/README.md")).toBe(true);
    expect(isExcluded("obj/Debug/foo.cs")).toBe(true);
    expect(isExcluded("bin/Release/bar.cs")).toBe(true);
    expect(isExcluded("Runtime/Foo/bin/Bar.cs")).toBe(true);
    expect(isExcluded("Editor/Bar/obj/Baz.cs")).toBe(true);
  });

  test("isExcluded does not match legitimate doc/source paths", () => {
    expect(isExcluded("Runtime/Core/MessageBus/MessageBus.cs")).toBe(false);
    expect(isExcluded(".llm/skills/performance/git-hook-performance.md")).toBe(false);
    expect(isExcluded("README.md")).toBe(false);
  });

  test("isApplicable filters the input list by extension AND exclude rules", () => {
    const repoRoot = path.resolve(__dirname, "../..");
    expect(isApplicable(path.join(repoRoot, "README.md"))).toBe(true);
    expect(isApplicable(path.join(repoRoot, "Runtime/Core/MessageBus/MessageBus.cs"))).toBe(true);
    // Excluded by directory rule.
    expect(isApplicable(path.join(repoRoot, "node_modules/foo/README.md"))).toBe(false);
    // Excluded by extension.
    expect(isApplicable(path.join(repoRoot, "package.json"))).toBe(false);
  });

  test("runStagedValidators returns zero violations for a clean known-good file", () => {
    // README.md is part of the published surface and must always pass
    // the documentation gates.
    const repoRoot = path.resolve(__dirname, "../..");
    const target = path.join(repoRoot, "README.md");
    const { applicable, results } = runStagedValidators([target]);

    expect(applicable).toEqual([target]);
    for (const validator of VALIDATORS) {
      expect(results.get(validator.id).violations).toEqual([]);
    }
  });

  test("runStagedValidators surfaces ASCII violations from a non-ASCII fixture", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "stagedvalidators-"));
    const target = path.join(tempDir, "fixture.md");
    try {
      // U+2705 (white heavy check mark) is a dingbat the ASCII validator
      // bans outright when it appears outside a callout line.
      fs.writeFileSync(target, "Hello ✅ world\n", "utf8");
      const { results } = runStagedValidators([target]);
      const asciiSlot = results.get("validate-docs-ascii");
      expect(asciiSlot.violations.length).toBeGreaterThan(0);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("runStagedValidators surfaces banned struct-emit pattern violations", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "stagedvalidators-"));
    const target = path.join(tempDir, "fixture.md");
    try {
      fs.writeFileSync(target, "```cs\nnew DamageMessage(10).Emit();\n```\n", "utf8");
      const { results } = runStagedValidators([target]);
      const codeSlot = results.get("validate-doc-code-patterns");
      expect(codeSlot.violations.length).toBeGreaterThan(0);
      expect(codeSlot.violations[0].id).toBe("struct-emit-temporary");
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("runStagedValidators surfaces prose-policy violations", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "stagedvalidators-"));
    const target = path.join(tempDir, "fixture.md");
    try {
      // The marketing word "seamless" is on the prose-policy ban list.
      fs.writeFileSync(target, "Our framework offers a seamless experience.\n", "utf8");
      const { results } = runStagedValidators([target]);
      const proseSlot = results.get("validate-docs-prose");
      expect(proseSlot.violations.length).toBeGreaterThan(0);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("runStagedValidators silently drops files that do not match the YAML filter", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "stagedvalidators-"));
    const jsFixture = path.join(tempDir, "fixture.js");
    try {
      fs.writeFileSync(jsFixture, "// nothing to validate\n", "utf8");
      const { applicable, results } = runStagedValidators([jsFixture]);
      expect(applicable).toEqual([]);
      for (const validator of VALIDATORS) {
        expect(results.get(validator.id).violations).toEqual([]);
      }
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("main exits 0 when given an empty argv", () => {
    const writeSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
    try {
      expect(main([])).toBe(0);
    } finally {
      writeSpy.mockRestore();
    }
  });
});
