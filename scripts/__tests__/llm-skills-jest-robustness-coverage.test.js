/**
 * @fileoverview Contract test for the Jest-hook-robustness skill coverage.
 *
 * Phase 4 adds two LLM skill pages (jest-hook-robustness.md and
 * let-tools-resolve-modules.md) plus context.md edits that link to them and
 * surface the new preflight:pre-push and doctor commands. We lock the file
 * paths, link presence, and underlying npm script shape here so any silent
 * rename, deletion, or accidental wipe fails loudly.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SKILLS_DIR = path.join(REPO_ROOT, ".llm", "skills");
const CONTEXT_PATH = path.join(REPO_ROOT, ".llm", "context.md");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");

const REQUIRED_SKILL_RELATIVE_PATHS = [
  "scripting/integrity-gate-robustness.md",
  "scripting/jest-hook-robustness.md",
  "scripting/let-tools-resolve-modules.md"
];

// Phrases that anchor each skill's content. If a skill is silently wiped or
// rewritten without these terms, the human-prose intent has been lost.
const SKILL_CONTENT_ANCHORS = {
  "scripting/integrity-gate-robustness.md": [
    "INTEGRITY_TARGETS",
    "findZeroByteNativeBinaries",
    "DXMSG_HOOK_NO_AUTOREPAIR"
  ],
  "scripting/jest-hook-robustness.md": ["testRunner", "os.tmpdir()", "isPathExcluded"],
  "scripting/let-tools-resolve-modules.md": ["resolve"]
};

describe(".llm/skills jest-hook-robustness coverage", () => {
  test.each(REQUIRED_SKILL_RELATIVE_PATHS.map((rel) => [rel]))(
    "skill page exists: %s",
    (relativePath) => {
      const absPath = path.join(SKILLS_DIR, relativePath);
      expect(fs.existsSync(absPath)).toBe(true);
    }
  );

  test.each(REQUIRED_SKILL_RELATIVE_PATHS.map((rel) => [rel]))(
    "%s is non-trivial (>1 KB)",
    (relativePath) => {
      const absPath = path.join(SKILLS_DIR, relativePath);
      if (!fs.existsSync(absPath)) {
        throw new Error(`Skill ${relativePath} missing -- Phase 4 must create it.`);
      }
      const stats = fs.statSync(absPath);
      expect(stats.size).toBeGreaterThan(1024);
    }
  );

  test.each(REQUIRED_SKILL_RELATIVE_PATHS.map((rel) => [rel]))(
    "%s begins with a `# ` heading or frontmatter on the first non-blank line",
    (relativePath) => {
      const absPath = path.join(SKILLS_DIR, relativePath);
      if (!fs.existsSync(absPath)) {
        throw new Error(`Skill ${relativePath} missing -- Phase 4 must create it.`);
      }
      const content = fs.readFileSync(absPath, "utf8");
      const firstNonBlank = content.split(/\r?\n/).find((line) => line.trim().length > 0);
      expect(firstNonBlank).toMatch(/^(#\s|---\s*$)/);
    }
  );

  test.each(REQUIRED_SKILL_RELATIVE_PATHS.map((rel) => [rel]))(
    "%s contains its content anchor phrases (content not silently wiped)",
    (relativePath) => {
      const absPath = path.join(SKILLS_DIR, relativePath);
      if (!fs.existsSync(absPath)) {
        throw new Error(`Skill ${relativePath} missing -- cannot check anchors.`);
      }
      const anchors = SKILL_CONTENT_ANCHORS[relativePath];
      if (!Array.isArray(anchors) || anchors.length === 0) {
        throw new Error(
          `No content anchors registered for ${relativePath}; add at least one to SKILL_CONTENT_ANCHORS.`
        );
      }
      const content = fs.readFileSync(absPath, "utf8");
      for (const anchor of anchors) {
        expect(content).toContain(anchor);
      }
    }
  );

  test(".llm/context.md links to each new skill by file path", () => {
    const context = fs.readFileSync(CONTEXT_PATH, "utf8");
    for (const relativePath of REQUIRED_SKILL_RELATIVE_PATHS) {
      const re = new RegExp(`skills/${relativePath.replace(/\./g, "\\.")}`);
      expect(context).toMatch(re);
    }
  });

  test(".llm/context.md references npm run preflight:pre-push", () => {
    const context = fs.readFileSync(CONTEXT_PATH, "utf8");
    expect(context).toContain("npm run preflight:pre-push");
  });

  test(".llm/context.md references npm run doctor", () => {
    const context = fs.readFileSync(CONTEXT_PATH, "utf8");
    expect(context).toContain("npm run doctor");
  });

  test("package.json scripts.preflight:pre-push is a non-empty string", () => {
    const pkg = JSON.parse(fs.readFileSync(PACKAGE_JSON_PATH, "utf8"));
    expect(pkg.scripts).toBeDefined();
    const value = pkg.scripts["preflight:pre-push"];
    expect(typeof value).toBe("string");
    expect(value.trim().length).toBeGreaterThan(0);
  });

  test("package.json scripts.doctor is a non-empty string", () => {
    const pkg = JSON.parse(fs.readFileSync(PACKAGE_JSON_PATH, "utf8"));
    expect(pkg.scripts).toBeDefined();
    const value = pkg.scripts.doctor;
    expect(typeof value).toBe("string");
    expect(value.trim().length).toBeGreaterThan(0);
  });
});
