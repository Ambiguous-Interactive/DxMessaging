"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  parseYamlBoolean,
  resolveYamlLineLengthPolicy,
  parseArgs,
  wrapCommentLine,
  rewriteYamlCommentLines,
  uniqueExistingYamlFiles,
  processFiles
} = require("../fix-yaml-comments-line-length");

const lib = require("../lib/yaml-line-length");
const guard = require("../hooks/yaml-line-length-guard");

describe("fix-yaml-comments-line-length", () => {
  test("parseYamlBoolean handles true/false and rejects other values", () => {
    expect(parseYamlBoolean("true")).toBe(true);
    expect(parseYamlBoolean("FALSE")).toBe(false);
    expect(parseYamlBoolean("maybe")).toBeNull();
    expect(parseYamlBoolean(undefined)).toBeNull();
  });

  test("parseArgs parses flags and files", () => {
    const parsed = parseArgs(["--check", "--all-files", "a.yml", "b.yaml", "README.md"]);

    expect(parsed).toEqual({
      check: true,
      allFiles: true,
      files: ["a.yml", "b.yaml", "README.md"]
    });
  });

  test("wrapCommentLine wraps breakable comment lines", () => {
    const line =
      "      # perf-allow[scans-the-world-with-files]: validator walks .devcontainer/*.sh to enforce a JSONC-aware extraction policy and staged argv cannot narrow it";

    const wrapped = wrapCommentLine(line, 100, {
      allowNonBreakableWords: true
    });

    expect(wrapped.length).toBeGreaterThan(1);
    expect(wrapped.every((candidate) => candidate.startsWith("      # "))).toBe(true);
    expect(wrapped.every((candidate) => candidate.length <= 100)).toBe(true);
  });

  test("wrapCommentLine preserves non-comment lines", () => {
    const line = "      files: '^(.+)$'";

    expect(wrapCommentLine(line, 20, { allowNonBreakableWords: true })).toEqual([line]);
  });

  test("wrapCommentLine preserves non-breakable overflows when policy allows", () => {
    const line =
      "# https://example.com/very/very/very/very/very/very/very/very/very/long/url/segment";

    expect(wrapCommentLine(line, 60, { allowNonBreakableWords: true })).toEqual([line]);
  });

  test("rewriteYamlCommentLines returns changed line numbers for wrapped comments", () => {
    const content = [
      "repos:",
      "  - repo: local",
      "    hooks:",
      "      # this comment is intentionally long and should be wrapped by the fixer before yamllint runs",
      "      - id: sample"
    ].join("\n");

    const rewritten = rewriteYamlCommentLines(content, {
      max: 70,
      allowNonBreakableWords: true
    });

    expect(rewritten.changedLines).toEqual([4]);
    const rewrittenLines = rewritten.content.split("\n");
    expect(rewrittenLines[3].startsWith("      # ")).toBe(true);
    expect(rewrittenLines[4].startsWith("      # ")).toBe(true);
  });

  test("uniqueExistingYamlFiles filters non-yaml and missing files", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "yaml-comment-filter-"));
    try {
      const yamlFile = path.join(tempDir, "a.yaml");
      const markdownFile = path.join(tempDir, "README.md");
      fs.writeFileSync(yamlFile, "key: value\n", "utf8");
      fs.writeFileSync(markdownFile, "# title\n", "utf8");

      const filtered = uniqueExistingYamlFiles([
        yamlFile,
        markdownFile,
        path.join(tempDir, "missing.yml")
      ]);
      expect(filtered).toEqual([yamlFile]);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("processFiles check mode reports violations without writing files", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "yaml-comment-check-"));
    try {
      fs.writeFileSync(
        path.join(tempDir, ".yamllint.yaml"),
        ["rules:", "  line-length:", "    max: 80", "    allow-non-breakable-words: true"].join(
          "\n"
        ),
        "utf8"
      );

      const targetPath = path.join(tempDir, "target.yml");
      const original =
        "# this comment line is intentionally very long and should be wrapped when not in check mode\nkey: value\n";
      fs.writeFileSync(targetPath, original, "utf8");

      const result = processFiles([targetPath], {
        check: true,
        repoRoot: tempDir
      });

      expect(result.violations).toHaveLength(1);
      expect(result.changedFiles).toHaveLength(0);
      expect(fs.readFileSync(targetPath, "utf8")).toBe(original);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  test("processFiles write mode rewrites wrapped comments", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "yaml-comment-write-"));
    try {
      fs.writeFileSync(
        path.join(tempDir, ".yamllint.yaml"),
        ["rules:", "  line-length:", "    max: 72", "    allow-non-breakable-words: true"].join(
          "\n"
        ),
        "utf8"
      );

      const targetPath = path.join(tempDir, "target.yaml");
      fs.writeFileSync(
        targetPath,
        "# this comment line is intentionally very long and should be wrapped in write mode\nkey: value\n",
        "utf8"
      );

      const result = processFiles([targetPath], {
        check: false,
        repoRoot: tempDir
      });

      expect(result.changedFiles).toHaveLength(1);
      const rewrittenLines = fs.readFileSync(targetPath, "utf8").split("\n");
      expect(rewrittenLines[0].length).toBeLessThanOrEqual(72);
      expect(rewrittenLines[1].length).toBeLessThanOrEqual(72);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  // Parity lock (Fix 1): the comment-wrap logic must be sourced from the single
  // source of truth (scripts/lib/yaml-line-length.js). A future change to the
  // wrap policy cannot diverge the commit-time CLI from the agentic guard
  // without failing here, because all three resolve to the SAME function.
  describe("comment-wrap parity: CLI, guard, and lib share one source of truth", () => {
    test("wrapCommentLine is the SAME function reference across lib/CLI/guard", () => {
      expect(typeof lib.wrapCommentLine).toBe("function");
      expect(wrapCommentLine).toBe(lib.wrapCommentLine);
      expect(guard.wrapCommentLine).toBe(lib.wrapCommentLine);
    });

    test("splitWords is the SAME function reference across lib and CLI", () => {
      const cli = require("../fix-yaml-comments-line-length");
      expect(typeof lib.splitWords).toBe("function");
      expect(cli.splitWords).toBe(lib.splitWords);
    });

    test("rewriteYamlCommentLines is sourced from the lib's wrapYamlCommentLines", () => {
      expect(typeof lib.wrapYamlCommentLines).toBe("function");
      expect(rewriteYamlCommentLines).toBe(lib.wrapYamlCommentLines);
    });

    test("behavioral parity: the guard's wrap output equals the CLI's on a sample", () => {
      const policy = { max: 60, allowNonBreakableWords: true };
      const content = [
        "# this is an intentionally long comment line that must be wrapped to fit",
        "key: value"
      ].join("\n");
      const viaCli = rewriteYamlCommentLines(content, policy).content;
      const viaGuard = guard.guardContent(
        content,
        Object.assign({ allowNonBreakableInlineMappings: false }, policy)
      ).content;
      expect(viaGuard).toBe(viaCli);
    });
  });

  test("resolveYamlLineLengthPolicy loads max and booleans from .yamllint.yaml", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "yaml-comment-policy-"));
    try {
      const configPath = path.join(tempDir, ".yamllint.yaml");
      fs.writeFileSync(
        configPath,
        [
          "rules:",
          "  line-length:",
          "    max: 123",
          "    allow-non-breakable-words: false",
          "    allow-non-breakable-inline-mappings: true"
        ].join("\n"),
        "utf8"
      );

      const policy = resolveYamlLineLengthPolicy(configPath);
      expect(policy.max).toBe(123);
      expect(policy.allowNonBreakableInlineMappings).toBe(true);
      expect(policy.allowNonBreakableWords).toBe(true);
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });
});
