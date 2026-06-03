const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");
const {
  generateLlmsTxt,
  countSkillFiles,
  getSkillCategories,
  normalizeForComparison,
  hasValidLastUpdatedLine,
  normalizeToLf
} = require("../update-llms-txt");

const ROOT_DIR = path.resolve(__dirname, "../..");
const LLMS_TXT_PATH = path.join(ROOT_DIR, "llms.txt");
const PACKAGE_JSON_PATH = path.join(ROOT_DIR, "package.json");
const UPDATE_LLMS_TXT_SCRIPT_PATH = path.join(ROOT_DIR, "scripts", "update-llms-txt.js");
const LLM_SKILLS_DIR = path.join(ROOT_DIR, ".llm", "skills");

function countActualSkillFiles(dir) {
  let count = 0;
  const entries = fs.readdirSync(dir, { withFileTypes: true });

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);

    if (entry.isDirectory()) {
      if (entry.name !== "templates") {
        count += countActualSkillFiles(fullPath);
      }
      continue;
    }

    if (!entry.isFile() || !entry.name.endsWith(".md")) {
      continue;
    }

    if (entry.name === "index.md" || entry.name === "specification.md") {
      continue;
    }

    count++;
  }

  return count;
}

describe("update-llms-txt.js", () => {
  describe("countSkillFiles()", () => {
    test("should return a positive number of skill files", () => {
      const count = countSkillFiles();
      expect(count).toBeGreaterThan(0);
      expect(typeof count).toBe("number");
    });

    test("should exclude non-skill markdown files and templates", () => {
      expect(countSkillFiles()).toBe(countActualSkillFiles(LLM_SKILLS_DIR));
    });
  });

  describe("getSkillCategories()", () => {
    test("should return an array of category names", () => {
      const categories = getSkillCategories();
      expect(Array.isArray(categories)).toBe(true);
      expect(categories.length).toBeGreaterThan(0);
    });

    test("should return sorted category names", () => {
      const categories = getSkillCategories();
      const sorted = [...categories].sort();
      expect(categories).toEqual(sorted);
    });

    test("should include known categories", () => {
      const categories = getSkillCategories();
      const knownCategories = ["documentation", "github-actions", "testing", "scripting"];

      for (const known of knownCategories) {
        expect(categories).toContain(known);
      }
    });

    test("should exclude non-skill directories", () => {
      const categories = getSkillCategories();

      expect(categories).not.toContain("templates");
    });
  });

  describe("generateLlmsTxt()", () => {
    test("should generate markdown content", () => {
      const content = generateLlmsTxt();
      expect(typeof content).toBe("string");
      expect(content.length).toBeGreaterThan(0);
    });

    test("should include project name in header", () => {
      const content = generateLlmsTxt();
      expect(content).toMatch(/^# DxMessaging/);
    });

    test("should include version from package.json", () => {
      const pkg = JSON.parse(fs.readFileSync(PACKAGE_JSON_PATH, "utf8"));
      const content = generateLlmsTxt();
      expect(content).toContain(`**Version:** ${pkg.version}`);
    });

    test("should include repository URL", () => {
      const content = generateLlmsTxt();
      expect(content).toContain("https://github.com/Ambiguous-Interactive/DxMessaging");
    });

    test("should include documentation URL", () => {
      const content = generateLlmsTxt();
      expect(content).toContain("https://ambiguous-interactive.github.io/DxMessaging/");
    });

    test("should include skill count", () => {
      const count = countActualSkillFiles(LLM_SKILLS_DIR);
      const content = generateLlmsTxt();
      expect(content).toContain(`${count}+`);
    });

    test("should include skill categories", () => {
      const categories = getSkillCategories();
      const content = generateLlmsTxt();

      for (const category of categories) {
        expect(content).toContain(`**${category}/**`);
      }

      expect(content).not.toContain("**templates/**");
    });

    test("should include current date in Last Updated", () => {
      const content = generateLlmsTxt();
      const today = new Date().toISOString().split("T")[0];
      expect(content).toContain(`**Last Updated:** ${today}`);
    });

    test("should include all main sections", () => {
      const content = generateLlmsTxt();
      const requiredSections = [
        "## Overview",
        "## Quick Facts",
        "## Key Features",
        "## Core Concepts",
        "## Project Structure",
        "## Getting Started",
        "## Documentation Structure",
        "## Key Files",
        "## Development",
        "## AI Agent Context",
        "## Common Pitfalls & Solutions",
        "## Performance Characteristics",
        "## Examples",
        "## Support & Community",
        "## License"
      ];

      for (const section of requiredSections) {
        expect(content).toContain(section);
      }
    });

    test("should include code examples", () => {
      const content = generateLlmsTxt();
      expect(content).toContain("```csharp");
      expect(content).toContain("```bash");
    });

    test("should include message type documentation", () => {
      const content = generateLlmsTxt();
      expect(content).toContain("Untargeted Messages");
      expect(content).toContain("Targeted Messages");
      expect(content).toContain("Broadcast Messages");
    });

    test("should include installation instructions", () => {
      const content = generateLlmsTxt();
      expect(content).toContain("openupm add");
    });
  });

  describe("hasValidLastUpdatedLine()", () => {
    test("should accept full content with exactly one valid last updated line", () => {
      const content = [
        "# DxMessaging",
        "",
        "**Last Updated:** 2026-03-16",
        "**Generated by:** scripts/update-llms-txt.js"
      ].join("\n");

      expect(hasValidLastUpdatedLine(content)).toBe(true);
    });

    test("should reject content without a last updated line", () => {
      const content = ["# DxMessaging", "", "No metadata here"].join("\n");

      expect(hasValidLastUpdatedLine(content)).toBe(false);
    });

    test("should reject content with multiple last updated lines", () => {
      const content = ["**Last Updated:** 2026-03-16", "Body", "**Last Updated:** 2026-03-17"].join(
        "\n"
      );

      expect(hasValidLastUpdatedLine(content)).toBe(false);
    });

    test("should reject content with an invalid date format", () => {
      const content = ["# DxMessaging", "", "**Last Updated:** March 16, 2026"].join("\n");

      expect(hasValidLastUpdatedLine(content)).toBe(false);
    });

    test("should accept content with CRLF line endings", () => {
      const content = [
        "# DxMessaging",
        "",
        "**Last Updated:** 2026-03-16",
        "**Generated by:** scripts/update-llms-txt.js"
      ].join("\r\n");

      expect(hasValidLastUpdatedLine(content)).toBe(true);
    });

    test("should accept content with lone CR line endings", () => {
      const content = [
        "# DxMessaging",
        "",
        "**Last Updated:** 2026-03-16",
        "**Generated by:** scripts/update-llms-txt.js"
      ].join("\r");

      expect(hasValidLastUpdatedLine(content)).toBe(true);
    });
  });

  describe("normalizeToLf()", () => {
    test("should normalize CRLF and lone CR to LF", () => {
      const result = normalizeToLf("a\r\nb\rc\nd");

      expect(result).toBe("a\nb\nc\nd");
      expect(result).not.toContain("\r");
    });
  });

  describe("normalizeForComparison()", () => {
    test("should normalize CRLF to LF", () => {
      const input = ["# DxMessaging", "**Last Updated:** 2026-03-16", "Body"].join("\r\n");

      const result = normalizeForComparison(input);

      expect(result).not.toContain("\r");
      expect(result).toBe("# DxMessaging\n**Last Updated:** <DATE>\nBody");
    });

    test("should normalize lone CR to LF", () => {
      const input = ["# DxMessaging", "**Last Updated:** 2026-03-16", "Body"].join("\r");

      const result = normalizeForComparison(input);

      expect(result).not.toContain("\r");
      expect(result).toBe("# DxMessaging\n**Last Updated:** <DATE>\nBody");
    });

    test("should normalize mixed line endings without creating extra blank lines", () => {
      const input = "line1\r\nline2\rline3\n**Last Updated:** 2026-03-16";

      const result = normalizeForComparison(input);

      expect(result).not.toContain("\r");
      expect(result).toBe("line1\nline2\nline3\n**Last Updated:** <DATE>");
      expect(result).not.toContain("\n\n");
    });
  });

  describe("CLI --check", () => {
    test("should succeed when llms.txt is current", () => {
      const result = childProcess.spawnSync(
        process.execPath,
        [UPDATE_LLMS_TXT_SCRIPT_PATH, "--check"],
        {
          cwd: ROOT_DIR,
          encoding: "utf8"
        }
      );

      expect(result.status).toBe(0);
      expect(result.stderr).toBe("");
      expect(result.stdout).toContain("llms.txt is up to date");
    });
  });

  describe("File consistency", () => {
    test("llms.txt should exist", () => {
      expect(fs.existsSync(LLMS_TXT_PATH)).toBe(true);
    });

    test("llms.txt should be up to date", () => {
      const currentContent = fs.readFileSync(LLMS_TXT_PATH, "utf8");
      const lastUpdatedLines = normalizeToLf(currentContent)
        .split("\n")
        .filter((line) => line.startsWith("**Last Updated:**"));

      expect(lastUpdatedLines.length).toBe(1);
      expect(hasValidLastUpdatedLine(currentContent)).toBe(true);

      const expectedContent = generateLlmsTxt();
      expect(hasValidLastUpdatedLine(expectedContent)).toBe(true);
      expect(normalizeForComparison(currentContent)).toBe(normalizeForComparison(expectedContent));
    });
  });
});
