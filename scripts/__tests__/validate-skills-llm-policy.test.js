/**
 * @fileoverview Tests for .llm markdown policy checks in validate-skills.js.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");
const {
  validateAllLlmMarkdownFiles,
  validateDuplicateSeeAlsoHeadings,
  validateBalancedMarkdownFences,
  validateSeeAlsoHeadingPlacement,
  shouldUseAsciiOutput,
  logSymbol,
  LINE_LIMIT_HARD_MAX,
  LINE_LIMIT_IDEAL_MAX,
  LINE_LIMIT_IDEAL_MIN,
  CONTEXT_INDEX_LINK_FRAGMENT
} = require("../validate-skills.js");

function writeFile(filePath, content) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, content, "utf8");
}

function listSplitSkillFiles(rootDir) {
  const files = [];
  const entries = fs.readdirSync(rootDir, { withFileTypes: true });

  for (const entry of entries) {
    const fullPath = path.join(rootDir, entry.name);
    if (entry.isDirectory()) {
      files.push(...listSplitSkillFiles(fullPath));
      continue;
    }

    if (entry.isFile() && /-part-\d+\.md$/.test(entry.name)) {
      files.push(fullPath);
    }
  }

  return files;
}

describe("validate-skills .llm markdown policy", () => {
  let tempDir;

  beforeEach(() => {
    tempDir = makeTempDir("llm-policy");
  });

  afterEach(() => {
    cleanupDir(tempDir);
  });

  test("uses expected repository line-limit constants", () => {
    expect(LINE_LIMIT_IDEAL_MIN).toBe(120);
    expect(LINE_LIMIT_IDEAL_MAX).toBe(260);
    expect(LINE_LIMIT_HARD_MAX).toBe(300);
  });

  test("uses ASCII log markers on Windows or explicit opt-in", () => {
    expect(shouldUseAsciiOutput(["node", "script"], {}, "linux")).toBe(false);
    expect(shouldUseAsciiOutput(["node", "script"], {}, "win32")).toBe(true);
    expect(shouldUseAsciiOutput(["node", "script", "--ascii"], {}, "linux")).toBe(true);
    expect(shouldUseAsciiOutput(["node", "script"], { DXM_ASCII_LOGS: "1" }, "linux")).toBe(true);
    expect(logSymbol("error", { argv: [], env: {}, platform: "win32" })).toBe("[error]");
  });

  test("reports error when markdown file exceeds hard max lines", () => {
    const oversizedLines = new Array(LINE_LIMIT_HARD_MAX + 2).fill("line").join("\n");
    writeFile(path.join(tempDir, "skills", "testing", "too-long.md"), oversizedLines);
    writeFile(path.join(tempDir, "context.md"), `# Context\n\nSee ${CONTEXT_INDEX_LINK_FRAGMENT}`);

    const result = validateAllLlmMarkdownFiles(tempDir);
    const sizeError = result.errors.find((error) => error.field === "size");

    expect(sizeError).toBeDefined();
    expect(sizeError.file).toBe("skills/testing/too-long.md");
    expect(sizeError.message).toContain(`max: ${LINE_LIMIT_HARD_MAX}`);
  });

  test("reports error when context.md does not link skills index", () => {
    writeFile(path.join(tempDir, "context.md"), "# Context\n\nNo index link here.");
    writeFile(path.join(tempDir, "skills", "testing", "ok.md"), "# Skill\n");

    const result = validateAllLlmMarkdownFiles(tempDir);
    const linkError = result.errors.find(
      (error) => error.file === "context.md" && error.field === "links"
    );

    expect(linkError).toBeDefined();
    expect(linkError.message).toContain("./skills/index.md");
  });

  test("passes policy checks when sizes are valid and context links index", () => {
    writeFile(path.join(tempDir, "context.md"), `# Context\n\nSee ${CONTEXT_INDEX_LINK_FRAGMENT}`);
    writeFile(
      path.join(tempDir, "skills", "testing", "ok.md"),
      "# Skill\n\n## Overview\n\nShort\n"
    );

    const result = validateAllLlmMarkdownFiles(tempDir);

    expect(result.errors).toHaveLength(0);
  });

  test("repository .llm markdown files stay under the hard line limit", () => {
    const repoRoot = path.resolve(__dirname, "..", "..");
    const result = validateAllLlmMarkdownFiles(path.join(repoRoot, ".llm"));
    const sizeErrors = result.errors.filter((error) => error.field === "size");

    expect(sizeErrors).toHaveLength(0);
  });

  test.each([
    {
      name: "duplicate See Also headings are flagged",
      content: "# Skill\n\n## See Also\n- one\n\n## See Also\n- two\n",
      expectedCount: 1,
      expectedLine: "Line 6"
    },
    {
      name: "single See Also heading is valid",
      content: "# Skill\n\n## See Also\n- one\n\n### See Also\n- two\n",
      expectedCount: 0
    },
    {
      name: "repeated non-See-Also headings are ignored by this focused check",
      content: "# Skill\n\n## A\n\n### Notes\n- one\n\n## B\n\n### Notes\n- two\n",
      expectedCount: 0
    }
  ])("validateDuplicateSeeAlsoHeadings: $name", ({ content, expectedCount, expectedLine }) => {
    const errors = validateDuplicateSeeAlsoHeadings(content, "skills/testing/sample.md");

    expect(errors).toHaveLength(expectedCount);

    if (expectedCount > 0) {
      expect(errors[0].field).toBe("headings");
      expect(errors[0].message).toContain(expectedLine);
    }
  });

  test("validateAllLlmMarkdownFiles reports duplicate See Also diagnostics", () => {
    writeFile(path.join(tempDir, "context.md"), `# Context\n\nSee ${CONTEXT_INDEX_LINK_FRAGMENT}`);
    writeFile(
      path.join(tempDir, "skills", "testing", "broken.md"),
      "# Skill\n\n## Overview\n\nText\n\n## See Also\n- one\n\n## See Also\n- two\n"
    );

    const result = validateAllLlmMarkdownFiles(tempDir);
    const headingError = result.errors.find((error) => error.field === "headings");

    expect(headingError).toBeDefined();
    expect(headingError.message).toContain("Duplicate '## See Also' heading");
  });

  test("validateBalancedMarkdownFences flags unclosed fenced code blocks", () => {
    const content = ["# Skill", "", "```markdown", "## See Also", "- one"].join("\n");

    const errors = validateBalancedMarkdownFences(content, "skills/testing/unclosed.md");

    expect(errors).toHaveLength(1);
    expect(errors[0].field).toBe("markdown");
    expect(errors[0].message).toContain("Unclosed fenced code block");
  });

  test("validateSeeAlsoHeadingPlacement flags swallowed See Also heading when fence is unclosed", () => {
    const content = ["# Skill", "", "```markdown", "## See Also", "- one"].join("\n");

    const errors = validateSeeAlsoHeadingPlacement(content, "skills/testing/swallowed-see-also.md");

    expect(errors).toHaveLength(1);
    expect(errors[0].field).toBe("headings");
    expect(errors[0].message).toContain("appears only inside a code fence");
  });

  test("validateSeeAlsoHeadingPlacement allows fenced examples when fence is closed", () => {
    const content = ["# Skill", "", "```markdown", "## See Also", "- sample", "```"].join("\n");

    const errors = validateSeeAlsoHeadingPlacement(content, "skills/testing/closed-sample.md");

    expect(errors).toHaveLength(0);
  });

  test("validateSeeAlsoHeadingPlacement allows a real See Also section outside fences", () => {
    const content = [
      "# Skill",
      "",
      "```markdown",
      "## See Also",
      "- sample",
      "```",
      "",
      "## See Also",
      "- real link"
    ].join("\n");

    const errors = validateSeeAlsoHeadingPlacement(content, "skills/testing/real-see-also.md");

    expect(errors).toHaveLength(0);
  });

  test("repository split skill files do not contain duplicate See Also headings", () => {
    const repoRoot = path.resolve(__dirname, "..", "..");
    const splitFiles = listSplitSkillFiles(path.join(repoRoot, ".llm", "skills"));

    expect(splitFiles.length).toBeGreaterThan(0);

    for (const filePath of splitFiles) {
      const content = fs.readFileSync(filePath, "utf8");
      const errors = validateDuplicateSeeAlsoHeadings(content, path.relative(repoRoot, filePath));
      expect(errors).toHaveLength(0);
    }
  });
});
