/**
 * @fileoverview Direct unit tests for the line-based pre-commit YAML helpers.
 *
 * The helpers in scripts/lib/precommit-yaml.js are the foundation that both
 * the perf-budget scorer and the stage-policy validator depend on. They walk
 * the raw YAML rather than parsing it semantically, so edge cases around
 * indentation, comments, blank lines, and trailing hooks must be covered
 * directly rather than only as a side effect of integration tests.
 */

"use strict";

const {
  getIndent,
  findHookBlock,
  extractStagesFromHookBlock,
  findAllHookBlocks
} = require("../lib/precommit-yaml");

describe("precommit-yaml", () => {
  describe("getIndent", () => {
    test("returns 0 for unindented lines", () => {
      expect(getIndent("foo")).toBe(0);
      expect(getIndent("- id: hook")).toBe(0);
    });

    test("returns count of leading spaces", () => {
      expect(getIndent("  foo")).toBe(2);
      expect(getIndent("        - id: hook")).toBe(8);
    });

    test("counts tab characters as one indent unit each", () => {
      // Tabs are length 1 each in the underlying string, and pre-commit
      // YAML must use spaces; if a tab sneaks in, getIndent should still
      // produce a stable, line-length-derived value.
      expect(getIndent("\tfoo")).toBe(1);
      expect(getIndent("\t\tfoo")).toBe(2);
      expect(getIndent("  \tfoo")).toBe(3);
    });

    test("returns 0 for empty lines and full length for whitespace-only lines", () => {
      // An empty string trims to itself, so length-trimmed-length is 0.
      expect(getIndent("")).toBe(0);
      // A whitespace-only line trims to empty, so the entire length
      // counts as indent. This is the documented behavior; callers that
      // care about blank lines must check `.trim().length === 0` first
      // (as both findAllHookBlocks and extractStagesFromHookBlock do).
      expect(getIndent("    ")).toBe(4);
    });
  });

  describe("findHookBlock", () => {
    const lines = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      - id: alpha",
      "        entry: node scripts/alpha.js",
      "        language: system",
      "        files: '\\.md$'",
      "      - id: beta",
      "        entry: node scripts/beta.js",
      "        language: system",
      "        stages:",
      "          - pre-commit",
      "      - id: gamma",
      "        entry: node scripts/gamma.js",
      "        language: system"
    ];

    test("finds an existing hook and returns 1-based start line", () => {
      const block = findHookBlock(lines, "beta");
      expect(block).not.toBeNull();
      expect(block.startLine).toBe(8);
      expect(block.lines[0]).toContain("- id: beta");
    });

    test("returns null for a missing hook", () => {
      expect(findHookBlock(lines, "delta")).toBeNull();
    });

    test("returns the trailing block all the way to end-of-file", () => {
      const block = findHookBlock(lines, "gamma");
      expect(block).not.toBeNull();
      expect(block.lines[block.lines.length - 1]).toContain("language: system");
    });

    test("respects matching indent when delimiting blocks", () => {
      // The block for `alpha` must end where the next sibling `- id:` at
      // the same indent appears, not at a nested key.
      const block = findHookBlock(lines, "alpha");
      expect(block).not.toBeNull();
      expect(block.lines.some((line) => line.includes("- id: beta"))).toBe(false);
      expect(block.lines.some((line) => line.includes("files: '\\.md$'"))).toBe(true);
    });
  });

  describe("extractStagesFromHookBlock", () => {
    test("returns empty array when block has no stages: key", () => {
      const block = {
        startLine: 1,
        lines: ["      - id: alpha", "        entry: node scripts/alpha.js"]
      };
      expect(extractStagesFromHookBlock(block)).toEqual([]);
    });

    test("returns empty array for null block", () => {
      expect(extractStagesFromHookBlock(null)).toEqual([]);
      expect(extractStagesFromHookBlock(undefined)).toEqual([]);
    });

    test("returns explicit stages from a list block", () => {
      const block = {
        startLine: 1,
        lines: [
          "      - id: alpha",
          "        stages:",
          "          - pre-commit",
          "          - pre-push"
        ]
      };
      expect(extractStagesFromHookBlock(block)).toEqual(["pre-commit", "pre-push"]);
    });

    test("ignores blank lines inside the stages list", () => {
      const block = {
        startLine: 1,
        lines: [
          "      - id: alpha",
          "        stages:",
          "          - pre-commit",
          "",
          "          - pre-push"
        ]
      };
      expect(extractStagesFromHookBlock(block)).toEqual(["pre-commit", "pre-push"]);
    });

    test("stops at the next sibling key (matching or shallower indent)", () => {
      const block = {
        startLine: 1,
        lines: [
          "      - id: alpha",
          "        stages:",
          "          - pre-commit",
          "        files: '\\.md$'",
          "          - this-should-not-leak"
        ]
      };
      expect(extractStagesFromHookBlock(block)).toEqual(["pre-commit"]);
    });
  });

  describe("findAllHookBlocks", () => {
    test("captures every hook id and assigns correct start lines", () => {
      const lines = [
        "repos:",
        "  - repo: local",
        "    hooks:",
        "      - id: alpha",
        "        entry: node a.js",
        "      - id: beta",
        "        entry: node b.js",
        "  - repo: https://example.com/foo",
        "    hooks:",
        "      - id: gamma"
      ];
      const blocks = findAllHookBlocks(lines);
      expect(blocks.map((b) => b.id)).toEqual(["alpha", "beta", "gamma"]);
      expect(blocks[0].startLine).toBe(4);
      expect(blocks[1].startLine).toBe(6);
      expect(blocks[2].startLine).toBe(10);
    });

    test("ignores comment lines and blank lines between hooks", () => {
      const lines = [
        "repos:",
        "  - repo: local",
        "    hooks:",
        "",
        "      # perf-allow[scans-the-world]: a substantive reason longer than required",
        "      - id: alpha",
        "        entry: node a.js",
        "",
        "      # another comment",
        "      - id: beta",
        "        entry: node b.js"
      ];
      const blocks = findAllHookBlocks(lines);
      expect(blocks.map((b) => b.id)).toEqual(["alpha", "beta"]);
      // The block for alpha should NOT include beta's id line.
      expect(blocks[0].lines.some((line) => line.includes("- id: beta"))).toBe(false);
    });

    test("trailing hook block extends to end of file", () => {
      const lines = [
        "repos:",
        "  - repo: local",
        "    hooks:",
        "      - id: only",
        "        entry: node only.js",
        "        language: system"
      ];
      const blocks = findAllHookBlocks(lines);
      expect(blocks).toHaveLength(1);
      expect(blocks[0].lines.length).toBe(3);
      expect(blocks[0].lines[blocks[0].lines.length - 1]).toContain("language: system");
    });

    test("does not match `id:` keys nested inside other structures", () => {
      // Only top-level hook entries (`- id: <name>`) should count. A
      // bare `id:` field inside an args list or env block is not a hook.
      const lines = [
        "repos:",
        "  - repo: local",
        "    hooks:",
        "      - id: alpha",
        "        args:",
        "          - --id",
        "          - hookname",
        "        entry: node a.js"
      ];
      const blocks = findAllHookBlocks(lines);
      expect(blocks.map((b) => b.id)).toEqual(["alpha"]);
    });
  });
});
