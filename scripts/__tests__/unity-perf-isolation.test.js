/**
 * @fileoverview Keystone contract test for the Unity perf-isolation rule.
 *
 * The .llm/context.md "perf isolation" line (114) requires that the
 * Benchmarks/Allocations asmdefs run ONLY on the scheduled
 * benchmarks workflow template, never on the default local run. The single source of truth for
 * that decision is scripts/unity/lib/asmdef-discovery.js — both the run-tests
 * scripts and the Unity Tests workflow shell out to it via `node -e`. This
 * test locks the contract end-to-end:
 *   1. Asmdef classification is correct for every Tests/ asmdef (no drift).
 *   2. defaultIncludeAssemblies returns exactly the core assemblies by
 *      default, exactly core+perf with includePerf, exactly core+comparison
 *      with includeComparisons, and exactly core+integration with
 *      includeIntegrations.
 *   3. The CI workflow consumes the discovery module rather than hardcoding
 *      a string list (which would silently rot the moment a new asmdef
 *      lands).
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const {
  enumerateTestAsmdefs,
  classifyAsmdef,
  isAsmdefCompatibleWithTarget,
  defaultIncludeAssemblies
} = require("../../scripts/unity/lib/asmdef-discovery.js");

describe("unity perf-isolation contract", () => {
  let entries;

  beforeAll(() => {
    entries = enumerateTestAsmdefs(REPO_ROOT);
  });

  test("enumerateTestAsmdefs discovers exactly 11 asmdefs under Tests/", () => {
    // 11 = 2 core (Editor, Runtime) + 3 perf (00.Editor.Benchmarks,
    // 00.Runtime.Benchmarks, Editor.Allocations) + 3 comparison
    // (00.Runtime.Comparisons, 00.Runtime.Comparisons.External,
    // 00.Runtime.Comparisons.UnityAtoms) + 3 integration (Reflex, VContainer,
    // Zenject). If a new asmdef is intentionally added, update this number AND
    // add it to one of the buckets below.
    expect(entries).toHaveLength(11);
  });

  test("every Benchmarks/Allocations asmdef is classified as `perf`", () => {
    const perfAsmdefs = entries.filter((e) => /Benchmarks|Allocations/.test(e.name));
    expect(perfAsmdefs.length).toBeGreaterThan(0);
    for (const entry of perfAsmdefs) {
      expect(classifyAsmdef(entry.name)).toBe("perf");
      expect(entry.isPerf).toBe(true);
      expect(entry.isInteg).toBe(false);
    }
  });

  test("every Comparisons asmdef is classified as `comparison`", () => {
    const comparisonAsmdefs = entries.filter((e) => /Comparisons/.test(e.name));
    expect(comparisonAsmdefs.length).toBeGreaterThan(0);
    for (const entry of comparisonAsmdefs) {
      expect(classifyAsmdef(entry.name)).toBe("comparison");
      expect(entry.isComparison).toBe(true);
      expect(entry.isPerf).toBe(false);
      expect(entry.isInteg).toBe(false);
    }
  });

  test("every Reflex/Zenject/VContainer asmdef is classified as `integration`", () => {
    const integAsmdefs = entries.filter((e) => /Reflex|Zenject|VContainer/.test(e.name));
    expect(integAsmdefs.length).toBeGreaterThan(0);
    for (const entry of integAsmdefs) {
      expect(classifyAsmdef(entry.name)).toBe("integration");
      expect(entry.isInteg).toBe(true);
      expect(entry.isPerf).toBe(false);
    }
  });

  test("remaining asmdefs (Editor, Runtime) are classified as `core`", () => {
    const coreAsmdefs = entries.filter(
      (e) => !/Benchmarks|Allocations|Comparisons|Reflex|Zenject|VContainer/.test(e.name)
    );
    expect(coreAsmdefs.length).toBeGreaterThan(0);
    for (const entry of coreAsmdefs) {
      expect(classifyAsmdef(entry.name)).toBe("core");
      expect(entry.isPerf).toBe(false);
      expect(entry.isInteg).toBe(false);
    }
  });

  test("defaultIncludeAssemblies(repoRoot) returns exactly 2 core assemblies", () => {
    const included = defaultIncludeAssemblies(REPO_ROOT);
    expect(included).toHaveLength(2);
    expect(included).toEqual(
      expect.arrayContaining([
        "WallstopStudios.DxMessaging.Tests.Editor",
        "WallstopStudios.DxMessaging.Tests.Runtime"
      ])
    );
  });

  test("defaultIncludeAssemblies({ includePerf: true }) adds the 3 perf assemblies (total 5)", () => {
    const included = defaultIncludeAssemblies(REPO_ROOT, { includePerf: true });
    expect(included).toHaveLength(5);
    // Verify the perf names show up.
    for (const expected of [
      "WallstopStudios.DxMessaging.Tests.00.Editor.Benchmarks",
      "WallstopStudios.DxMessaging.Tests.00.Runtime.Benchmarks",
      "WallstopStudios.DxMessaging.Tests.Editor.Allocations"
    ]) {
      expect(included).toContain(expected);
    }
  });

  test("defaultIncludeAssemblies({ includeComparisons: true }) adds the 3 comparison assemblies (total 5)", () => {
    const included = defaultIncludeAssemblies(REPO_ROOT, {
      includeComparisons: true
    });
    // 5 = 2 core + 3 comparison (zero-dependency, External package bridges,
    // Unity Atoms bridge). The External and UnityAtoms assemblies are gated by
    // their own defineConstraints, so they only compile when those packages are
    // present in the harness, but they are still discovered/classified here.
    expect(included).toHaveLength(5);
    for (const expected of [
      "WallstopStudios.DxMessaging.Tests.00.Runtime.Comparisons",
      "WallstopStudios.DxMessaging.Tests.00.Runtime.Comparisons.External",
      "WallstopStudios.DxMessaging.Tests.00.Runtime.Comparisons.UnityAtoms"
    ]) {
      expect(included).toContain(expected);
    }
  });

  test("defaultIncludeAssemblies({ includeIntegrations: true }) adds the 3 integration assemblies (total 5)", () => {
    const included = defaultIncludeAssemblies(REPO_ROOT, {
      includeIntegrations: true
    });
    expect(included).toHaveLength(5);
    for (const expected of [
      "WallstopStudios.DxMessaging.Tests.Runtime.Reflex",
      "WallstopStudios.DxMessaging.Tests.Runtime.VContainer",
      "WallstopStudios.DxMessaging.Tests.Runtime.Zenject"
    ]) {
      expect(included).toContain(expected);
    }
  });

  test("defaultIncludeAssemblies({ runtimeOnly: true }) drops editor-only asmdefs", () => {
    // standalone runs the IL2CPP player, where EditMode/editor-only asmdefs
    // cannot run. runtimeOnly removes every asmdef whose includePlatforms is
    // exactly ["Editor"], leaving only the runtime suite.
    const included = defaultIncludeAssemblies(REPO_ROOT, { runtimeOnly: true });
    expect(included).toEqual(["WallstopStudios.DxMessaging.Tests.Runtime"]);
  });

  test("defaultIncludeAssemblies target option is explicit and backwards-compatible", () => {
    expect(defaultIncludeAssemblies(REPO_ROOT, { target: "editmode" })).toEqual(
      defaultIncludeAssemblies(REPO_ROOT)
    );
    expect(defaultIncludeAssemblies(REPO_ROOT, { target: "playmode" })).toEqual([
      "WallstopStudios.DxMessaging.Tests.Runtime"
    ]);
    expect(defaultIncludeAssemblies(REPO_ROOT, { target: "standalone" })).toEqual(
      defaultIncludeAssemblies(REPO_ROOT, { runtimeOnly: true })
    );
  });

  test.each([
    [[], [], "standalone", true],
    [["Editor"], [], "standalone", false],
    [["Standalone"], [], "standalone", true],
    [["WindowsStandalone64"], [], "standalone", true],
    [[], ["Standalone"], "standalone", false],
    [[], ["WindowsStandalone64"], "standalone", false],
    [[], [], "editmode", true],
    [["Editor"], [], "editmode", true],
    [["Standalone"], [], "editmode", false],
    [[], ["Editor"], "editmode", false],
    [[], [], "playmode", true],
    [["Editor"], [], "playmode", false],
    [["Standalone"], [], "playmode", false],
    [[], ["Editor"], "playmode", false]
  ])(
    "platform compatibility include=%j exclude=%j target=%s => %s",
    (includePlatforms, excludePlatforms, target, expected) => {
      expect(
        isAsmdefCompatibleWithTarget(includePlatforms, excludePlatforms, target)
      ).toBe(expected);
    }
  );

  test("defaultIncludeAssemblies({ runtimeOnly: true, includePerf: true }) adds the runtime benchmark", () => {
    // The runtime gate composes with the perf opt-in: only runtime asmdefs
    // survive, so the runtime benchmark joins the runtime suite.
    const included = defaultIncludeAssemblies(REPO_ROOT, {
      runtimeOnly: true,
      includePerf: true
    });
    expect(included).toHaveLength(2);
    expect(included).toEqual(
      expect.arrayContaining([
        "WallstopStudios.DxMessaging.Tests.Runtime",
        "WallstopStudios.DxMessaging.Tests.00.Runtime.Benchmarks"
      ])
    );
  });

  test("disabled unity-tests.yml template shells out to defaultIncludeAssemblies (no hardcoded asmdef list)", () => {
    const workflowPath = path.join(REPO_ROOT, ".github", "workflows-disabled", "unity-tests.yml");
    const workflow = fs.readFileSync(workflowPath, "utf8");

    // The single source of truth contract: the workflow must shell out to
    // the JS module via `node -e`. Tolerate whitespace and quoting
    // variation but require both the require() and the
    // defaultIncludeAssemblies(...) call.
    expect(workflow).toMatch(/node\s+-e/);
    expect(workflow).toMatch(/require\(['"]\.\/scripts\/unity\/lib\/asmdef-discovery\.js['"]\)/);
    expect(workflow).toMatch(/defaultIncludeAssemblies\s*\(/);
  });
});
