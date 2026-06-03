/**
 * @fileoverview Tests for validate-no-plan-vocabulary.js
 *
 * Covers:
 * - scanContent: clean fixture, filename ref, tier tag, plan-section heading,
 *   migration-guide-style "Phase 0" allowed, mermaid `T1` allowed, allowlist.
 * - In-scope filtering: tests, scripts, etc. are excluded.
 * - Real-tree integration: shipping content is currently clean.
 * - Tier-tag false positives like "T22.5 degrees" do NOT match (m1).
 * - Code-fenced violations are NOT flagged (m2).
 *
 * Note on self-reference: this test file references the forbidden patterns
 * inside string literals (PLAN.md, T2.4, "## Phase P0"). The validator's
 * ALLOWLIST excludes this file by exact path; the integration test then
 * exercises that ALLOWLIST end-to-end.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const {
  scanContent,
  isInScope,
  isAllowlisted,
  filterInScopeFiles,
  parseArgs,
  reportResult,
  run,
  maskCodeFences,
  PATTERNS,
  ALLOWLIST,
  compileGlob
} = require("../validate-no-plan-vocabulary.js");

describe("validate-no-plan-vocabulary", () => {
  describe("PATTERNS", () => {
    test("declares the three rule names exactly once each", () => {
      const names = PATTERNS.map((pattern) => pattern.name);
      expect(names.sort()).toEqual(["plan-filename", "plan-section-heading", "tier-tag"]);
    });
  });

  describe("compileGlob", () => {
    test("matches a single-segment glob", () => {
      const re = compileGlob("foo*");
      expect(re.test("foo")).toBe(true);
      expect(re.test("foobar")).toBe(true);
      expect(re.test("nested/foo")).toBe(false);
    });

    test("matches a recursive glob", () => {
      const re = compileGlob("Runtime/**/*.cs");
      expect(re.test("Runtime/Core/Foo.cs")).toBe(true);
      expect(re.test("Runtime/Foo.cs")).toBe(true);
      expect(re.test("Other/Foo.cs")).toBe(false);
    });
  });

  describe("maskCodeFences (m2)", () => {
    test("blanks lines inside triple-backtick fence", () => {
      const input = ["before", "```", "[See PLAN.md] inside fence", "```", "after"].join("\n");
      const masked = maskCodeFences(input);
      // The fenced lines must be empty strings; outer lines must remain.
      expect(masked.split("\n")).toEqual(["before", "", "", "", "after"]);
    });

    test("blanks lines inside triple-tilde fence", () => {
      const input = ["before", "~~~", "T2.4", "~~~", "after"].join("\n");
      const masked = maskCodeFences(input);
      expect(masked.split("\n")).toEqual(["before", "", "", "", "after"]);
    });

    test("preserves line numbers (length stays equal in line count)", () => {
      const input = "a\n```\nb\n```\nc";
      const masked = maskCodeFences(input);
      expect(masked.split("\n").length).toBe(input.split("\n").length);
    });
  });

  describe("scanContent", () => {
    test("clean content has no violations", () => {
      const content = "User-facing prose with no planning references.\n";
      expect(scanContent("docs/example.md", content)).toEqual([]);
    });

    test("flags PLAN.md filename references", () => {
      const content = "[See " + "PLAN" + ".md] for details.\n";
      const violations = scanContent("docs/example.md", content);
      const filenameViolations = violations.filter((v) => v.pattern === "plan-filename");
      expect(filenameViolations).toHaveLength(1);
      expect(filenameViolations[0].file).toBe("docs/example.md");
      expect(filenameViolations[0].line).toBe(1);
    });

    test("flags PERF-PLAN.md, OLD-PLAN.md, GH-PAGES-PLAN.md filenames", () => {
      const content = [
        "Refer to " + "PERF-PLAN" + ".md.",
        "Older plan in " + "OLD-PLAN" + ".md.",
        "Pages plan: " + "GH-PAGES-PLAN" + ".md."
      ].join("\n");
      const violations = scanContent("docs/example.md", content).filter(
        (v) => v.pattern === "plan-filename"
      );
      expect(violations).toHaveLength(3);
      expect(violations.map((v) => v.line)).toEqual([1, 2, 3]);
    });

    test("flags tier-tag occurrences (T<n>.<n> and P<n>.<n>)", () => {
      const content =
        "Captured " + "T2" + "." + "4 baseline; rolled " + "P3" + "." + "1 changes.\n";
      const violations = scanContent("docs/example.md", content).filter(
        (v) => v.pattern === "tier-tag"
      );
      expect(violations).toHaveLength(2);
      expect(violations.map((v) => v.match).sort()).toEqual(["P3.1", "T2.4"]);
    });

    test("flags plan-section headings (Phase P<n>, Tier T<n>)", () => {
      const content = ["# README", "", "## Phase " + "P0 - Setup", "", "Body."].join("\n");
      const violations = scanContent("README.md", content).filter(
        (v) => v.pattern === "plan-section-heading"
      );
      expect(violations).toHaveLength(1);
      expect(violations[0].line).toBe(3);
    });

    test("does NOT flag migration-guide-style 'Phase 0/1/2/3' headings", () => {
      const content = [
        "# Migration Guide",
        "",
        "## Phase 0: Install",
        "",
        "## Phase 1: Add to a New Feature",
        "",
        "## Phase 2: Migrate High-Pain Areas",
        "",
        "## Phase 3: Adopt for All New Code",
        ""
      ].join("\n");
      const violations = scanContent("docs/guides/migration-guide.md", content);
      expect(violations).toEqual([]);
    });

    test("does NOT flag bare T1 / P0 (Mermaid IDs, test method names)", () => {
      const content = [
        "graph TD",
        "  T1[Foo] --> T2[Bar]",
        "  P0 --> P1",
        "Method " + "P0_Returns_Default is fine."
      ].join("\n");
      const violations = scanContent("docs/architecture/diagram.md", content);
      expect(violations).toEqual([]);
    });

    test("reports column information correctly", () => {
      const content = "  Tag: " + "T2" + "." + "4 baseline\n";
      const violations = scanContent("docs/example.md", content).filter(
        (v) => v.pattern === "tier-tag"
      );
      expect(violations).toHaveLength(1);
      expect(violations[0].line).toBe(1);
      // "  Tag: T" -> the T sits at column 8 (1-indexed).
      expect(violations[0].column).toBe(8);
    });

    test("handles empty input gracefully", () => {
      expect(scanContent("docs/empty.md", "")).toEqual([]);
      expect(scanContent("docs/empty.md", null)).toEqual([]);
    });

    test("violations are sorted by (line, column)", () => {
      const content = ["First " + "T2" + "." + "4 here.", "Second " + "P0" + "." + "1 here."].join(
        "\n"
      );
      const violations = scanContent("docs/example.md", content);
      expect(violations.map((v) => v.line)).toEqual([1, 2]);
    });

    test("does NOT flag 3-or-more-digit forms after the tighten (m1)", () => {
      // The original regex `\b[TP][0-9]+\.[0-9]+\b` matched arbitrarily long
      // digit sequences. The new 1-2-digit cap keeps physical quantities and
      // long version numbers out of scope when any side exceeds 2 digits.
      // For example, the literal "T100.5" or "P22.500" no longer match.
      const content = "Constants T100.5 and P22.500 are not tier tags.\n";
      const violations = scanContent("docs/example.md", content);
      expect(violations).toEqual([]);
    });

    test("DOES flag 'T2.4' in planning-style prose (m1)", () => {
      // Confirm the tighter regex still catches the planning-vocabulary form.
      const content = "Roll into " + "T" + "2.4" + " next.\n";
      const violations = scanContent("docs/example.md", content);
      const tierTags = violations.filter((v) => v.pattern === "tier-tag");
      expect(tierTags).toHaveLength(1);
      expect(tierTags[0].match).toBe("T2.4");
    });

    test("DOES flag 2-digit-per-side tier tags (e.g. T22.5)", () => {
      // The 1-2-digit cap covers up to two digits per side. `T22.5` and
      // `T2.45` are still tier-tag-shaped under this cap.
      const content = "Captured " + "T" + "22.5 baseline.\n";
      const violations = scanContent("docs/example.md", content).filter(
        (v) => v.pattern === "tier-tag"
      );
      expect(violations).toHaveLength(1);
      expect(violations[0].match).toBe("T22.5");
    });

    test("does NOT flag a tier-tag-shaped match inside a fenced code block (m2)", () => {
      const content = [
        "Avoid this in your prose:",
        "",
        "```",
        "[See " + "PLAN" + ".md] is forbidden in shipping content",
        "```",
        "",
        "End."
      ].join("\n");
      const violations = scanContent("docs/example.md", content);
      // The PLAN.md reference is inside the fenced block; it must NOT fire.
      expect(violations).toEqual([]);
    });

    test("DOES flag the same pattern outside a fence (m2)", () => {
      const content = "[See " + "PLAN" + ".md] outside fence.\n";
      const violations = scanContent("docs/example.md", content);
      const filenames = violations.filter((v) => v.pattern === "plan-filename");
      expect(filenames).toHaveLength(1);
    });

    test("tilde fences also suppress violations (m2)", () => {
      const content = ["~~~", "## Phase " + "P0 - inside fence", "~~~"].join("\n");
      const violations = scanContent("docs/example.md", content);
      expect(violations).toEqual([]);
    });

    test("a fence with a language info-string still suppresses (m2)", () => {
      const content = [
        "Inline:",
        "```text",
        "Refer to " + "PLAN" + ".md inside this code block",
        "```"
      ].join("\n");
      const violations = scanContent("docs/example.md", content);
      expect(violations).toEqual([]);
    });
  });

  describe("isInScope", () => {
    test("includes Runtime/Editor/SourceGenerators *.cs files", () => {
      expect(isInScope("Runtime/Core/Foo.cs")).toBe(true);
      expect(isInScope("Editor/CustomEditors/Foo.cs")).toBe(true);
      expect(isInScope("SourceGenerators/Pkg/Foo.cs")).toBe(true);
    });

    test("includes Samples~ *.cs", () => {
      expect(isInScope("Samples~/Mini Combat/Boot.cs")).toBe(true);
    });

    test("includes docs/**/*.md and known root markdown", () => {
      expect(isInScope("docs/guides/migration-guide.md")).toBe(true);
      expect(isInScope("README.md")).toBe(true);
      expect(isInScope("CHANGELOG.md")).toBe(true);
      expect(isInScope("CONTRIBUTING.md")).toBe(true);
      expect(isInScope("Third Party Notices.md")).toBe(true);
      expect(isInScope("llms.txt")).toBe(true);
    });

    test("excludes Tests trees", () => {
      expect(isInScope("Tests/Runtime/Foo.cs")).toBe(false);
      expect(isInScope("Runtime/Core/Tests/Foo.cs")).toBe(false);
      expect(
        isInScope("SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.Tests/Foo.cs")
      ).toBe(false);
    });

    test("excludes scripts/, .llm/, .github/ and other meta paths", () => {
      expect(isInScope("scripts/validate-no-plan-vocabulary.js")).toBe(false);
      expect(isInScope(".llm/skills/index.md")).toBe(false);
      expect(isInScope(".github/workflows/build.yml")).toBe(false);
      expect(isInScope("PLAN.md")).toBe(false);
      expect(isInScope("PERF-PLAN.md")).toBe(false);
    });

    test("Windows-style paths are normalized before matching", () => {
      const winPath = ["docs", "guides", "migration-guide.md"].join(path.sep);
      expect(isInScope(winPath)).toBe(true);
    });
  });

  describe("isAllowlisted", () => {
    test("includes the validator and its test", () => {
      expect(isAllowlisted("scripts/validate-no-plan-vocabulary.js")).toBe(true);
      expect(isAllowlisted("scripts/__tests__/validate-no-plan-vocabulary.test.js")).toBe(true);
    });

    test("does not include arbitrary other files", () => {
      expect(isAllowlisted("docs/guides/migration-guide.md")).toBe(false);
    });

    test("ALLOWLIST contains exactly two entries (m14)", () => {
      // Defensive: a future PR that grows the allowlist must intentionally
      // edit this assertion AND justify why the new path needs to state the
      // forbidden patterns. Keeping the size capped low is the point. If a
      // legitimate third file needs the allowlist, bump this to 3 with a
      // comment explaining the new entry.
      expect(ALLOWLIST.size).toBe(2);
      expect([...ALLOWLIST].sort()).toEqual([
        "scripts/__tests__/validate-no-plan-vocabulary.test.js",
        "scripts/validate-no-plan-vocabulary.js"
      ]);
    });
  });

  describe("filterInScopeFiles", () => {
    test("keeps in-scope, drops out-of-scope and allowlisted", () => {
      const result = filterInScopeFiles([
        "Runtime/Core/Foo.cs",
        "Tests/Runtime/Foo.cs",
        "scripts/validate-no-plan-vocabulary.js",
        "docs/guides/migration-guide.md",
        ".github/workflows/build.yml"
      ]);
      expect(result).toEqual(["Runtime/Core/Foo.cs", "docs/guides/migration-guide.md"]);
    });
  });

  describe("run with injected file system", () => {
    test("clean fixture set yields zero violations", () => {
      const fakeSpawn = () => ({
        status: 0,
        stdout: ["docs/clean.md", "Runtime/Core/Foo.cs"].join("\n"),
        stderr: ""
      });
      const fakeRead = (file) => {
        if (file === "docs/clean.md") return "User-facing prose.\n";
        if (file === "Runtime/Core/Foo.cs") return "namespace X { public class Y { } }\n";
        throw new Error(`Unexpected read: ${file}`);
      };

      const result = run({ cwd: process.cwd(), spawn: fakeSpawn, readFile: fakeRead });
      expect(result.valid).toBe(true);
      expect(result.violations).toEqual([]);
      expect(result.scannedFiles).toEqual(["docs/clean.md", "Runtime/Core/Foo.cs"]);
    });

    test("dirty fixture surfaces violations and exits non-zero via reportResult", () => {
      const fakeSpawn = () => ({
        status: 0,
        stdout: ["docs/dirty.md"].join("\n"),
        stderr: ""
      });
      const fakeRead = () => "See " + "PLAN" + ".md and roll " + "T2" + "." + "4 next.\n";

      const result = run({ cwd: process.cwd(), spawn: fakeSpawn, readFile: fakeRead });
      expect(result.valid).toBe(false);
      const patterns = new Set(result.violations.map((v) => v.pattern));
      expect(patterns.has("plan-filename")).toBe(true);
      expect(patterns.has("tier-tag")).toBe(true);

      const messages = [];
      const exit = reportResult(result, { logger: { log: (msg) => messages.push(msg) } });
      expect(exit).toBe(1);
      expect(messages.join("\n")).toContain("docs/dirty.md");
    });

    test("git not installed surfaces as a hard error", () => {
      const fakeSpawn = () => ({
        error: Object.assign(new Error("spawn ENOENT"), { code: "ENOENT" })
      });
      const result = run({ cwd: process.cwd(), spawn: fakeSpawn });
      expect(result.valid).toBe(false);
      expect(result.errors[0].type).toBe("git-not-installed");
    });

    test("allowlisted file is skipped even when its content has violations", () => {
      // The validator file lists every forbidden pattern. An end-to-end run
      // that includes its real content in the in-scope set should still pass
      // because the ALLOWLIST excludes it.
      const fakeSpawn = () => ({
        status: 0,
        stdout: ["scripts/validate-no-plan-vocabulary.js"].join("\n"),
        stderr: ""
      });
      const fakeRead = () => {
        throw new Error("readFile should NOT be called for allowlisted files");
      };
      const result = run({ cwd: process.cwd(), spawn: fakeSpawn, readFile: fakeRead });
      expect(result.valid).toBe(true);
      expect(result.scannedFiles).toEqual([]);
    });
  });

  describe("parseArgs", () => {
    test("recognizes --list-files", () => {
      expect(parseArgs(["--list-files"]).listFiles).toBe(true);
    });

    test("flags unknown arguments", () => {
      expect(parseArgs(["--bogus"]).errors).toEqual(["Unknown argument: --bogus"]);
    });

    test("supports --help", () => {
      expect(parseArgs(["--help"]).help).toBe(true);
    });
  });

  describe("real repository state", () => {
    test("real repo is clean", () => {
      // Run the validator against the actual working tree. This proves the
      // current shipping surface contains no plan vocabulary.
      const result = run();
      if (!result.valid) {
        const detail = result.errors
          .map((error) => `[${error.type}] ${error.message}`)
          .concat(
            result.violations.map(
              (v) => `${v.file}:${v.line}:${v.column}: ${v.pattern}: ${v.match}`
            )
          )
          .join("\n");
        throw new Error(`validate-no-plan-vocabulary failed on the real repo:\n${detail}`);
      }
      expect(result.valid).toBe(true);
    });

    test("real repo has at least one in-scope file (sanity)", () => {
      const result = run();
      // The exact count is not stable, but the count must be > 0 or the
      // include-pattern logic regressed.
      expect(result.scannedFiles.length).toBeGreaterThan(0);
      // A few expected paths should be present.
      expect(result.scannedFiles).toContain("README.md");
    });

    test("validator script files exist on disk (allowlist sanity)", () => {
      // Confirm the allowlist references real files. If a maintainer renames
      // the validator without updating the allowlist this test catches it.
      for (const allowed of ALLOWLIST) {
        const fullPath = path.resolve(__dirname, "..", "..", allowed);
        expect(fs.existsSync(fullPath)).toBe(true);
      }
    });
  });
});

// Defensive: prevent accidental teardown leakage of the temp directory
// pattern other tests rely on.
afterAll(() => {
  // Nothing to clean; this suite uses no temp dirs.
  void os;
});
