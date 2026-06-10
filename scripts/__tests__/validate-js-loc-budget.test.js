"use strict";

/**
 * Tests for the JS LOC budget gate (`scripts/validate-js-loc-budget.js`).
 *
 * The most important tests are the LIVE CONTRACT pair: `evaluate()` against
 * the real repository must report zero violations and zero stale overrides,
 * and the set of files measured over the default cap must EQUAL the
 * `PER_FILE_OVERRIDES` key set (catching missing AND unnecessary entries).
 * Because this suite runs in the pre-push `script-tests` hook, those two
 * assertions make the budget push-enforced with no extra hook wiring. The
 * remaining tests pin the counting definition and the pure classification
 * logic with synthetic inputs so a regression is attributable.
 */

const fs = require("fs");
const path = require("path");

const {
  JS_FILE_PATTERNS,
  TOTAL_BUDGET,
  PER_FILE_DEFAULT_CAP,
  PER_FILE_OVERRIDES,
  countLines,
  measureTrackedJs,
  classifyMeasurements,
  evaluate
} = require("../validate-js-loc-budget");
const { listTrackedFiles, readUtf8 } = require("../lib/repo-files");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

describe("validate-js-loc-budget: live repository contract", () => {
  test("the real repository is within budget with no stale overrides", () => {
    const { totalViolations, fileViolations, staleOverrides } = evaluate();
    expect({ totalViolations, fileViolations, staleOverrides }).toEqual({
      totalViolations: [],
      fileViolations: [],
      staleOverrides: []
    });
  });

  test("files measured over the default cap are exactly the override keys", () => {
    const overCap = measureTrackedJs()
      .filter(({ lines }) => lines > PER_FILE_DEFAULT_CAP)
      .map(({ file }) => file)
      .sort();
    expect(overCap).toEqual(Object.keys(PER_FILE_OVERRIDES).sort());
  });
});

describe("validate-js-loc-budget: countLines counting definition", () => {
  test.each([
    ["", 0],
    ["a", 1],
    ["a\n", 1],
    ["a\nb", 2],
    ["a\nb\n", 2],
    ["\n", 1],
    ["\n\n", 2]
  ])("countLines(%j) === %i", (text, expected) => {
    expect(countLines(text)).toBe(expected);
  });

  describe("CRLF and BOM files measure identically via readUtf8", () => {
    let tempDir;

    beforeAll(() => {
      tempDir = makeTempDir("js-loc-budget");
    });

    afterAll(() => {
      cleanupDir(tempDir);
    });

    test("a CRLF-terminated two-line file counts 2", () => {
      const file = path.join(tempDir, "crlf.js");
      fs.writeFileSync(file, "a\r\nb\r\n", "utf8");
      expect(countLines(readUtf8(file))).toBe(2);
    });

    test("a BOM-prefixed LF two-line file counts 2", () => {
      const file = path.join(tempDir, "bom.js");
      fs.writeFileSync(file, "\uFEFFa\nb\n", "utf8");
      expect(countLines(readUtf8(file))).toBe(2);
    });
  });
});

describe("validate-js-loc-budget: classification logic", () => {
  const budgets = (overrides = {}, totalBudget = 1000, defaultCap = 100) => ({
    totalBudget,
    defaultCap,
    overrides
  });

  test("a total over TOTAL_BUDGET is a total violation even with every file under cap", () => {
    const measurements = [
      { file: "scripts/a.js", lines: 90 },
      { file: "scripts/b.js", lines: 95 }
    ];
    const result = classifyMeasurements(measurements, budgets({}, 180, 100));
    expect(result.fileCount).toBe(2);
    expect(result.totalLines).toBe(185);
    expect(result.fileViolations).toEqual([]);
    expect(result.staleOverrides).toEqual([]);
    expect(result.totalViolations).toHaveLength(1);
    expect(result.totalViolations[0]).toContain("185");
    expect(result.totalViolations[0]).toContain("TOTAL_BUDGET 180");
    expect(result.totalViolations[0]).toContain("raise the TOTAL_BUDGET constant");
  });

  test("a file over the default cap without an override is a per-file violation", () => {
    const measurements = [{ file: "scripts/new.js", lines: 101 }];
    const result = classifyMeasurements(measurements, budgets());
    expect(result.totalViolations).toEqual([]);
    expect(result.staleOverrides).toEqual([]);
    expect(result.fileViolations).toHaveLength(1);
    expect(result.fileViolations[0]).toContain("scripts/new.js: 101 lines");
    expect(result.fileViolations[0]).toContain("default per-file cap of 100");
    expect(result.fileViolations[0]).toContain("PER_FILE_OVERRIDES");
  });

  test("a file over its pinned override budget is a per-file violation", () => {
    const overrides = { "scripts/big.js": { budget: 200, reason: "pinned" } };
    const result = classifyMeasurements(
      [{ file: "scripts/big.js", lines: 201 }],
      budgets(overrides)
    );
    expect(result.fileViolations).toHaveLength(1);
    expect(result.fileViolations[0]).toContain("scripts/big.js: 201 lines");
    expect(result.fileViolations[0]).toContain("override budget of 200");
    expect(result.fileViolations[0]).toContain("raise its PER_FILE_OVERRIDES budget");
    expect(result.staleOverrides).toEqual([]);
  });

  test("a file between the default cap and its pin passes and the override is not stale", () => {
    const overrides = { "scripts/big.js": { budget: 200, reason: "pinned" } };
    const result = classifyMeasurements(
      [{ file: "scripts/big.js", lines: 150 }],
      budgets(overrides)
    );
    expect(result.totalViolations).toEqual([]);
    expect(result.fileViolations).toEqual([]);
    expect(result.staleOverrides).toEqual([]);
  });

  test("at-budget passes: a file AT its cap and a total AT the budget are both fine", () => {
    const overrides = { "scripts/big.js": { budget: 200, reason: "pinned" } };
    const measurements = [
      { file: "scripts/big.js", lines: 200 },
      { file: "scripts/a.js", lines: 100 }
    ];
    const result = classifyMeasurements(measurements, budgets(overrides, 300, 100));
    expect(result.totalLines).toBe(300);
    expect(result.totalViolations).toEqual([]);
    expect(result.fileViolations).toEqual([]);
    expect(result.staleOverrides).toEqual([]);
  });

  test("an override naming an untracked file is stale", () => {
    const overrides = { "scripts/gone.js": { budget: 200, reason: "removed" } };
    const result = classifyMeasurements([{ file: "scripts/a.js", lines: 10 }], budgets(overrides));
    expect(result.fileViolations).toEqual([]);
    expect(result.staleOverrides).toHaveLength(1);
    expect(result.staleOverrides[0]).toContain("scripts/gone.js");
    expect(result.staleOverrides[0]).toContain("untracked");
    expect(result.staleOverrides[0]).toContain("remove the");
  });

  test("an override whose file shrank to the default cap is stale", () => {
    const overrides = { "scripts/shrunk.js": { budget: 200, reason: "was big" } };
    const result = classifyMeasurements(
      [{ file: "scripts/shrunk.js", lines: 100 }],
      budgets(overrides)
    );
    expect(result.fileViolations).toEqual([]);
    expect(result.staleOverrides).toHaveLength(1);
    expect(result.staleOverrides[0]).toContain("scripts/shrunk.js");
    expect(result.staleOverrides[0]).toContain("100 lines");
    expect(result.staleOverrides[0]).toContain("Remove the stale entry");
  });

  test("a violation and a stale override are reported together", () => {
    const overrides = { "scripts/gone.js": { budget: 200, reason: "removed" } };
    const result = classifyMeasurements(
      [{ file: "scripts/new.js", lines: 101 }],
      budgets(overrides)
    );
    expect(result.fileViolations).toHaveLength(1);
    expect(result.fileViolations[0]).toContain("scripts/new.js");
    expect(result.staleOverrides).toHaveLength(1);
    expect(result.staleOverrides[0]).toContain("scripts/gone.js");
  });

  test("a new file is governed by the default cap, not by other files' overrides", () => {
    const overrides = { "scripts/big.js": { budget: 5000, reason: "pinned" } };
    const result = classifyMeasurements(
      [
        { file: "scripts/big.js", lines: 4000 },
        { file: "scripts/fresh.js", lines: 101 }
      ],
      budgets(overrides, 10000, 100)
    );
    expect(result.fileViolations).toHaveLength(1);
    expect(result.fileViolations[0]).toContain("scripts/fresh.js");
    expect(result.fileViolations[0]).toContain("default per-file cap of 100");
  });
});

describe("validate-js-loc-budget: PER_FILE_OVERRIDES well-formedness", () => {
  const trackedJs = new Set(listTrackedFiles(JS_FILE_PATTERNS));

  test("every override names a tracked JS file by repo-relative POSIX path", () => {
    for (const file of Object.keys(PER_FILE_OVERRIDES)) {
      expect(trackedJs.has(file)).toBe(true);
      expect(file).toBe(file.replace(/\\/g, "/"));
      expect(/\.(js|cjs|mjs)$/.test(file)).toBe(true);
    }
  });

  test("every override pins an integer budget above the default cap with a non-empty reason", () => {
    for (const [, entry] of Object.entries(PER_FILE_OVERRIDES)) {
      expect(Number.isInteger(entry.budget)).toBe(true);
      expect(entry.budget).toBeGreaterThan(PER_FILE_DEFAULT_CAP);
      expect(typeof entry.reason).toBe("string");
      expect(entry.reason.trim().length).toBeGreaterThan(0);
    }
  });

  test("the budget constants are positive integers", () => {
    expect(Number.isInteger(TOTAL_BUDGET)).toBe(true);
    expect(TOTAL_BUDGET).toBeGreaterThan(0);
    expect(Number.isInteger(PER_FILE_DEFAULT_CAP)).toBe(true);
    expect(PER_FILE_DEFAULT_CAP).toBeGreaterThan(0);
  });
});
