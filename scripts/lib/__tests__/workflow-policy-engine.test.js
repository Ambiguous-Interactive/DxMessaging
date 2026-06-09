/**
 * @fileoverview Unit tests for scripts/lib/workflow-policy-engine.js.
 *
 * The engine holds the pure YAML / workflow structural parsing primitives that
 * were extracted out of scripts/validate-workflows.js so policy rules compose
 * over a single parsing layer. The large validate-workflows.test.js and
 * validate-workflows-concurrency-and-labels.test.js suites remain the
 * behavioral oracle (they exercise these primitives through the validator's
 * re-exports). This suite adds two guarantees the oracle does not:
 *
 *   1. SEAM INTEGRITY: every primitive the validator re-exports is the SAME
 *      reference the engine exports, so the extraction can never silently
 *      diverge into a stale second copy.
 *   2. SELF-CONTAINMENT: the engine loads and behaves correctly on its own,
 *      proving it carries no hidden dependency back on the validator.
 */

"use strict";

const engine = require("../workflow-policy-engine");
const validator = require("../../validate-workflows");

describe("module shape", () => {
  const EXPECTED_EXPORTS = [
    "Violation",
    "escapeRegexChar",
    "extractConcurrencyGroupFromBlock",
    "extractDefaultRunShellFromBlock",
    "extractJobConcurrencyGroup",
    "extractJobDefaultsShell",
    "extractJobMatrixMaxParallel",
    "extractJobMatrixValues",
    "extractJobNeeds",
    "extractJobOutputsSourceMap",
    "extractJobRunsOn",
    "extractJobSteps",
    "extractJobTimeoutMinutes",
    "extractJobs",
    "extractRunBlocks",
    "extractStaticJobLabels",
    "extractStepEnvMap",
    "extractStepIf",
    "extractStepName",
    "extractStepRun",
    "extractStepShell",
    "extractStepTimeoutMinutes",
    "extractStepUses",
    "extractStepWithMap",
    "extractWorkflowConcurrencyGroup",
    "extractWorkflowDefaultsShell",
    "extractWorkflowPathBlocks",
    "extractWorkflowPathEntries",
    "extractWorkflowPathFilterBlocks",
    "extractWorkflowPathIgnoreBlocks",
    "extractedWorkflowStepRunText",
    "getIndent",
    "jobHasMatrix",
    "jobTargetsWindows",
    "loadYamlModule",
    "normalizeUsesRef",
    "normalizeWorkflowPathPattern",
    "parseInlineLabelArray",
    "parseInlineYamlList",
    "parseLiteralMatrixScalar",
    "parseYamlBoolean",
    "stepRunText",
    "stepSourceText",
    "stringifyWorkflowScalar",
    "stripYamlScalarQuotes",
    "workflowPathGlobToRegex",
    "workflowScalarIsFalse",
    "workflowScalarIsTrue"
  ];

  test("exports exactly the expected primitive set", () => {
    expect(Object.keys(engine).sort()).toEqual([...EXPECTED_EXPORTS].sort());
  });

  test("every export is callable (Violation is a class, the rest functions)", () => {
    for (const name of EXPECTED_EXPORTS) {
      expect(typeof engine[name]).toBe("function");
    }
    // Violation is a constructible value type.
    const v = new engine.Violation("a.yml", 3, "pat", "msg");
    expect(v).toBeInstanceOf(engine.Violation);
  });
});

describe("seam integrity with validate-workflows", () => {
  // Names the validator re-exports that originate in the engine. (The validator
  // also exports its own policy functions; those are intentionally excluded.)
  const SHARED = [
    "Violation",
    "extractRunBlocks",
    "extractJobs",
    "extractWorkflowDefaultsShell",
    "extractJobDefaultsShell",
    "jobTargetsWindows",
    "extractJobSteps",
    "extractJobConcurrencyGroup",
    "extractWorkflowConcurrencyGroup",
    "extractConcurrencyGroupFromBlock",
    "jobHasMatrix",
    "extractJobMatrixMaxParallel",
    "extractJobTimeoutMinutes",
    "extractStepTimeoutMinutes",
    "extractJobRunsOn",
    "extractJobNeeds",
    "parseInlineLabelArray",
    "extractJobOutputsSourceMap",
    "extractWorkflowPathEntries",
    "extractWorkflowPathBlocks",
    "extractWorkflowPathIgnoreBlocks",
    "extractStaticJobLabels"
  ];

  test("the validator re-exports the engine's exact references (no stale copy)", () => {
    for (const name of SHARED) {
      expect(validator[name]).toBe(engine[name]);
    }
  });
});

describe("self-contained structural parsing", () => {
  const WORKFLOW = [
    "name: demo",
    "on: push",
    "jobs:",
    "  build:",
    "    runs-on: ubuntu-latest",
    "    steps:",
    "      - name: Checkout",
    "        uses: actions/checkout@v4",
    "      - name: Run",
    "        shell: bash",
    "        run: echo hello",
    "  windows-job:",
    "    runs-on: windows-latest",
    "    steps:",
    "      - run: echo win"
  ];

  test("extractJobs finds each top-level job with 1-indexed bounds", () => {
    const jobs = engine.extractJobs(WORKFLOW);
    expect(jobs.map((j) => j.id)).toEqual(["build", "windows-job"]);
    expect(jobs[0].startLine).toBe(4);
  });

  test("extractJobSteps parses name/uses/shell/run for a job", () => {
    const [build] = engine.extractJobs(WORKFLOW);
    const steps = engine.extractJobSteps(WORKFLOW, build);
    expect(steps).toHaveLength(2);
    expect(steps[0].name).toBe("Checkout");
    expect(steps[0].uses).toBe("actions/checkout@v4");
    expect(steps[1].shell).toBe("bash");
    expect(steps[1].run.text).toBe("echo hello");
  });

  test("jobTargetsWindows distinguishes hosted-OS jobs", () => {
    const jobs = engine.extractJobs(WORKFLOW);
    expect(engine.jobTargetsWindows(WORKFLOW, jobs[0])).toBe(false);
    expect(engine.jobTargetsWindows(WORKFLOW, jobs[1])).toBe(true);
  });

  test("getIndent counts leading spaces", () => {
    expect(engine.getIndent("    foo")).toBe(4);
    expect(engine.getIndent("nope")).toBe(0);
  });
});

describe("scalar, list, and pattern primitives", () => {
  test("parseYamlBoolean recognizes only literal true/false", () => {
    expect(engine.parseYamlBoolean("true")).toBe(true);
    expect(engine.parseYamlBoolean(" FALSE ")).toBe(false);
    expect(engine.parseYamlBoolean("yes")).toBeNull();
    expect(engine.parseYamlBoolean(42)).toBeNull();
  });

  test("stripYamlScalarQuotes and parseInlineYamlList", () => {
    expect(engine.stripYamlScalarQuotes('"hi"')).toBe("hi");
    expect(engine.parseInlineYamlList("[a, 'b', \"c\"]")).toEqual(["a", "b", "c"]);
    expect(engine.parseInlineYamlList("not-a-list")).toBeNull();
  });

  test("workflowScalar truthiness helpers", () => {
    expect(engine.workflowScalarIsTrue("true")).toBe(true);
    expect(engine.workflowScalarIsTrue(true)).toBe(true);
    expect(engine.workflowScalarIsFalse("false")).toBe(true);
    expect(engine.workflowScalarIsTrue("maybe")).toBe(false);
  });

  test("workflowPathGlobToRegex matches glob semantics", () => {
    const re = engine.workflowPathGlobToRegex("docs/**");
    expect(re.test("docs/a/b.md")).toBe(true);
    expect(re.test("src/a.md")).toBe(false);
  });

  test("normalizeUsesRef lowercases and strips leading ./", () => {
    expect(engine.normalizeUsesRef("./.github/actions/Foo")).toBe(".github/actions/foo");
    expect(engine.normalizeUsesRef(42)).toBe("");
  });
});

describe("Violation value type", () => {
  test("toString renders severity-prefixed, file:line + pattern", () => {
    const err = new engine.Violation("wf.yml", 7, "npm install", "use npm ci");
    expect(err.toString()).toBe("[ERROR] wf.yml:7: use npm ci\n  Pattern: npm install");
    const warn = new engine.Violation("wf.yml", 1, "p", "m", "warning");
    expect(warn.toString().startsWith("[WARN] ")).toBe(true);
  });
});
