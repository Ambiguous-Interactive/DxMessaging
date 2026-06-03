/**
 * @fileoverview Tests for generate-skills-index.js logic.
 *
 * These tests validate the core logic of the skills index generator:
 * - Brand name capitalization (BRAND_NAMES mapping)
 * - Category title formatting (categoryToTitle function)
 * - YAML frontmatter parsing
 * - Edge cases for various inputs
 */

"use strict";

const {
  applyBrandCapitalization,
  categoryToTitle,
  formatWithPrettier,
  getLineSizeIndicator,
  parseFrontmatter,
  BRAND_NAMES
} = require("../generate-skills-index.js");
const { normalizeToLf } = require("../lib/quote-parser");

describe("generate-skills-index", () => {
  describe("getLineSizeIndicator", () => {
    test.each([
      [119, "[draft]"],
      [120, "[ok]"],
      [260, "[ok]"],
      [261, "[warn]"],
      [300, "[warn]"],
      [301, "[over]"]
    ])("maps %i lines to %s", (lineCount, expected) => {
      expect(getLineSizeIndicator(lineCount)).toBe(expected);
    });
  });

  describe("BRAND_NAMES mapping", () => {
    test("should contain expected number of brand entries", () => {
      const brandCount = Object.keys(BRAND_NAMES).length;
      expect(brandCount).toBeGreaterThan(30);
    });

    test("should have all lowercase keys", () => {
      for (const key of Object.keys(BRAND_NAMES)) {
        expect(key).toBe(key.toLowerCase());
      }
    });

    test("should have non-empty values for all keys", () => {
      for (const value of Object.values(BRAND_NAMES)) {
        expect(value).toBeTruthy();
        expect(typeof value).toBe("string");
      }
    });
  });

  describe("applyBrandCapitalization", () => {
    describe("known brand names", () => {
      test("should return GitHub for github", () => {
        expect(applyBrandCapitalization("github")).toBe("GitHub");
      });

      test("should return npm for npm (lowercase brand)", () => {
        expect(applyBrandCapitalization("npm")).toBe("npm");
      });

      test("should return API for api", () => {
        expect(applyBrandCapitalization("api")).toBe("API");
      });

      test("should return JavaScript for javascript", () => {
        expect(applyBrandCapitalization("javascript")).toBe("JavaScript");
      });

      test("should return TypeScript for typescript", () => {
        expect(applyBrandCapitalization("typescript")).toBe("TypeScript");
      });

      test("should return Node.js for nodejs", () => {
        expect(applyBrandCapitalization("nodejs")).toBe("Node.js");
      });

      test("should return C# for csharp", () => {
        expect(applyBrandCapitalization("csharp")).toBe("C#");
      });

      test("should return .NET for dotnet", () => {
        expect(applyBrandCapitalization("dotnet")).toBe(".NET");
      });

      test("should return VS Code for vscode", () => {
        expect(applyBrandCapitalization("vscode")).toBe("VS Code");
      });

      test("should return macOS for macos", () => {
        expect(applyBrandCapitalization("macos")).toBe("macOS");
      });

      test("should return iOS for ios", () => {
        expect(applyBrandCapitalization("ios")).toBe("iOS");
      });

      test("should return GraphQL for graphql", () => {
        expect(applyBrandCapitalization("graphql")).toBe("GraphQL");
      });

      test("should return LLM for llm", () => {
        expect(applyBrandCapitalization("llm")).toBe("LLM");
      });

      test("should return CI for ci", () => {
        expect(applyBrandCapitalization("ci")).toBe("CI");
      });

      test("should return CD for cd", () => {
        expect(applyBrandCapitalization("cd")).toBe("CD");
      });
    });

    describe("case-insensitive input", () => {
      test("should handle GITHUB (all uppercase)", () => {
        expect(applyBrandCapitalization("GITHUB")).toBe("GitHub");
      });

      test("should handle GitHub (mixed case)", () => {
        expect(applyBrandCapitalization("GitHub")).toBe("GitHub");
      });

      test("should handle NPM (all uppercase)", () => {
        expect(applyBrandCapitalization("NPM")).toBe("npm");
      });

      test("should handle API (already uppercase)", () => {
        expect(applyBrandCapitalization("API")).toBe("API");
      });

      test("should handle JavaScript (mixed case)", () => {
        expect(applyBrandCapitalization("JavaScript")).toBe("JavaScript");
      });

      test("should handle VSCODE (all uppercase)", () => {
        expect(applyBrandCapitalization("VSCODE")).toBe("VS Code");
      });
    });

    describe("unknown words - standard title case", () => {
      test("should title-case unknown word", () => {
        expect(applyBrandCapitalization("unknown")).toBe("Unknown");
      });

      test("should title-case testing", () => {
        expect(applyBrandCapitalization("testing")).toBe("Testing");
      });

      test("should title-case documentation", () => {
        expect(applyBrandCapitalization("documentation")).toBe("Documentation");
      });

      test("should title-case scripting", () => {
        expect(applyBrandCapitalization("scripting")).toBe("Scripting");
      });

      test("should title-case actions", () => {
        expect(applyBrandCapitalization("actions")).toBe("Actions");
      });

      test("should convert UPPERCASE unknown to title case", () => {
        expect(applyBrandCapitalization("TESTING")).toBe("Testing");
      });

      test("should convert MixedCase unknown to title case", () => {
        expect(applyBrandCapitalization("TeStiNg")).toBe("Testing");
      });
    });

    describe("edge cases", () => {
      test("should handle empty string", () => {
        const result = applyBrandCapitalization("");
        expect(result).toBe("");
      });

      test("should handle single character lowercase", () => {
        expect(applyBrandCapitalization("a")).toBe("A");
      });

      test("should handle single character uppercase", () => {
        expect(applyBrandCapitalization("A")).toBe("A");
      });

      test("should handle two-letter word", () => {
        expect(applyBrandCapitalization("ab")).toBe("Ab");
      });

      test("should handle word with numbers", () => {
        expect(applyBrandCapitalization("test123")).toBe("Test123");
      });

      test("should handle number-only string", () => {
        expect(applyBrandCapitalization("123")).toBe("123");
      });

      test("should handle word starting with number", () => {
        expect(applyBrandCapitalization("3d")).toBe("3d");
      });

      test("should handle already-capitalized unknown word", () => {
        expect(applyBrandCapitalization("Already")).toBe("Already");
      });
    });
  });

  describe("categoryToTitle", () => {
    describe("single-word categories", () => {
      test("should format testing category", () => {
        expect(categoryToTitle("testing")).toBe("Testing");
      });

      test("should format documentation category", () => {
        expect(categoryToTitle("documentation")).toBe("Documentation");
      });

      test("should format scripting category", () => {
        expect(categoryToTitle("scripting")).toBe("Scripting");
      });

      test("should format github as brand name", () => {
        expect(categoryToTitle("github")).toBe("GitHub");
      });

      test("should format api as brand name", () => {
        expect(categoryToTitle("api")).toBe("API");
      });

      test("should format npm as brand name", () => {
        expect(categoryToTitle("npm")).toBe("npm");
      });

      test("should format javascript as brand name", () => {
        expect(categoryToTitle("javascript")).toBe("JavaScript");
      });
    });

    describe("multi-word categories (hyphenated)", () => {
      test("should format github-actions with brand capitalization", () => {
        expect(categoryToTitle("github-actions")).toBe("GitHub Actions");
      });

      test("should format api-design with brand capitalization", () => {
        expect(categoryToTitle("api-design")).toBe("API Design");
      });

      test("should format javascript-testing with brand capitalization", () => {
        expect(categoryToTitle("javascript-testing")).toBe("JavaScript Testing");
      });

      test("should format test-coverage with standard title case", () => {
        expect(categoryToTitle("test-coverage")).toBe("Test Coverage");
      });

      test("should format error-handling with standard title case", () => {
        expect(categoryToTitle("error-handling")).toBe("Error Handling");
      });

      test("should format cross-platform with standard title case", () => {
        expect(categoryToTitle("cross-platform")).toBe("Cross Platform");
      });

      test("should format ci-cd with brand names", () => {
        expect(categoryToTitle("ci-cd")).toBe("CI CD");
      });

      test("should format typescript-nodejs with multiple brands", () => {
        expect(categoryToTitle("typescript-nodejs")).toBe("TypeScript Node.js");
      });
    });

    describe("complex multi-word categories", () => {
      test("should format three-word category", () => {
        expect(categoryToTitle("test-error-handling")).toBe("Test Error Handling");
      });

      test("should format category with multiple brands", () => {
        expect(categoryToTitle("github-api-testing")).toBe("GitHub API Testing");
      });

      test("should format long category name", () => {
        expect(categoryToTitle("comprehensive-test-coverage")).toBe("Comprehensive Test Coverage");
      });
    });

    describe("edge cases", () => {
      test("should handle empty string", () => {
        expect(categoryToTitle("")).toBe("");
      });

      test("should handle single character", () => {
        expect(categoryToTitle("a")).toBe("A");
      });

      test("should handle category with numbers", () => {
        expect(categoryToTitle("test123")).toBe("Test123");
      });

      test("should handle category starting with number", () => {
        expect(categoryToTitle("3d-graphics")).toBe("3d Graphics");
      });

      test("should handle multiple consecutive hyphens as empty words", () => {
        // Edge case: multiple hyphens create empty strings
        const result = categoryToTitle("test--case");
        expect(result).toBe("Test  Case");
      });

      test("should handle leading hyphen", () => {
        const result = categoryToTitle("-testing");
        expect(result).toBe(" Testing");
      });

      test("should handle trailing hyphen", () => {
        const result = categoryToTitle("testing-");
        expect(result).toBe("Testing ");
      });

      test("should handle already-capitalized input", () => {
        expect(categoryToTitle("TESTING")).toBe("Testing");
      });

      test("should handle mixed-case input", () => {
        expect(categoryToTitle("GitHub-Actions")).toBe("GitHub Actions");
      });
    });
  });

  describe("brand name coverage", () => {
    test("should have all pure acronym brands in uppercase", () => {
      // Only single-form acronyms (not plurals like APIs/URLs which keep lowercase 's')
      const pureAcronymBrands = [
        "api",
        "cli",
        "json",
        "yaml",
        "xml",
        "html",
        "css",
        "sql",
        "url",
        "uri",
        "http",
        "https",
        "rest",
        "jwt",
        "sdk",
        "ide",
        "llm",
        "ai",
        "ml",
        "ci",
        "cd"
      ];
      for (const brand of pureAcronymBrands) {
        const result = BRAND_NAMES[brand];
        expect(result).toBeDefined();
        expect(result).toBe(result.toUpperCase());
      }
    });

    test("should have plural acronyms with lowercase s suffix", () => {
      const pluralAcronyms = {
        apis: "APIs",
        urls: "URLs",
        uris: "URIs"
      };
      for (const [input, expected] of Object.entries(pluralAcronyms)) {
        expect(BRAND_NAMES[input]).toBe(expected);
      }
    });

    test("should have proper camelCase brands", () => {
      const camelCaseBrands = {
        github: "GitHub",
        javascript: "JavaScript",
        typescript: "TypeScript",
        graphql: "GraphQL",
        webgl: "WebGL",
        opengl: "OpenGL",
        directx: "DirectX"
      };
      for (const [input, expected] of Object.entries(camelCaseBrands)) {
        expect(BRAND_NAMES[input]).toBe(expected);
      }
    });

    test("should have special case brands", () => {
      const specialCases = {
        npm: "npm", // all lowercase
        macos: "macOS", // lowercase first letter
        ios: "iOS", // lowercase first letter
        nodejs: "Node.js", // with dot
        csharp: "C#", // with symbol
        dotnet: ".NET", // starts with dot
        vscode: "VS Code", // with space
        visualstudio: "Visual Studio", // with space
        nuget: "NuGet", // mixed case
        oauth: "OAuth" // mixed case
      };
      for (const [input, expected] of Object.entries(specialCases)) {
        expect(BRAND_NAMES[input]).toBe(expected);
      }
    });
  });

  describe("formatWithPrettier", () => {
    test("prefers local in-process Prettier when available", async () => {
      const localPrettier = {
        resolveConfig: jest.fn(async () => ({ tabWidth: 4 })),
        getFileInfo: jest.fn(async () => ({ ignored: false })),
        format: jest.fn(async () => "formatted in-process")
      };
      const loadLocalPrettierFn = jest.fn(() => localPrettier);
      const runBundledNpxCommandFn = jest.fn();

      const result = await formatWithPrettier("raw input", {
        loadLocalPrettierFn,
        runBundledNpxCommandFn,
        indexPath: "/repo/.llm/skills/index.md"
      });

      expect(result).toBe("formatted in-process");
      // Use stringMatching with a regex that accepts both POSIX and
      // Windows separators so the test runs identically on every
      // platform. The production code uses platform-native path.join,
      // which emits "\" on Windows and "/" on POSIX.
      expect(loadLocalPrettierFn).toHaveBeenCalledWith(
        expect.objectContaining({
          localPrettierModulePath: expect.stringMatching(
            /node_modules[\\/]prettier[\\/]index\.cjs$/
          ),
          localPrettierBinPath: expect.stringMatching(
            /node_modules[\\/]prettier[\\/]bin[\\/]prettier\.cjs$/
          )
        })
      );
      expect(localPrettier.resolveConfig).toHaveBeenCalledWith("/repo/.llm/skills/index.md", {
        editorconfig: true
      });
      expect(localPrettier.getFileInfo).toHaveBeenCalledWith("/repo/.llm/skills/index.md", {
        resolveConfig: false
      });
      expect(localPrettier.format).toHaveBeenCalledWith(
        "raw input",
        expect.objectContaining({
          tabWidth: 4,
          filepath: "/repo/.llm/skills/index.md"
        })
      );
      expect(runBundledNpxCommandFn).not.toHaveBeenCalled();
    });

    test("returns original content when local Prettier marks index file ignored", async () => {
      const localPrettier = {
        resolveConfig: jest.fn(async () => ({})),
        getFileInfo: jest.fn(async () => ({ ignored: true })),
        format: jest.fn(async () => "should not run")
      };

      const result = await formatWithPrettier("raw input", {
        loadLocalPrettierFn: () => localPrettier,
        runBundledNpxCommandFn: jest.fn(),
        indexPath: "/repo/.llm/skills/index.md"
      });

      expect(result).toBe("raw input");
      expect(localPrettier.format).not.toHaveBeenCalled();
    });

    test("falls back to bundled npm npx-cli when local Prettier is unavailable", async () => {
      const runBundledNpxCommandFn = jest.fn(() => ({
        status: 0,
        stdout: "formatted fallback",
        stderr: ""
      }));

      const result = await formatWithPrettier("raw input", {
        loadLocalPrettierFn: () => null,
        runBundledNpxCommandFn,
        getPinnedPrettierSpecFn: () => "prettier@3.8.3",
        indexPath: "/repo/.llm/skills/index.md",
        repoRoot: "/repo"
      });

      expect(result).toBe("formatted fallback");
      expect(runBundledNpxCommandFn).toHaveBeenCalledWith(
        [
          "--yes",
          "--package=prettier@3.8.3",
          "prettier",
          "--stdin-filepath",
          "/repo/.llm/skills/index.md"
        ],
        {
          cwd: "/repo",
          input: "raw input",
          encoding: "utf8"
        }
      );
    });

    test("throws clear fallback error when bundled npx prettier exits non-zero", async () => {
      await expect(
        formatWithPrettier("raw", {
          loadLocalPrettierFn: () => null,
          runBundledNpxCommandFn: () => ({
            status: 1,
            stdout: "",
            stderr: "boom"
          })
        })
      ).rejects.toThrow("Prettier fallback via bundled npm npx-cli.js failed: boom");
    });

    test("surfaces actionable error when bundled npm npx-cli is missing", async () => {
      await expect(
        formatWithPrettier("raw", {
          loadLocalPrettierFn: () => null,
          runBundledNpxCommandFn: () => {
            throw new Error(
              "Unable to locate npm's npx-cli.js next to the active Node executable."
            );
          }
        })
      ).rejects.toThrow("Unable to locate npm's npx-cli.js");
    });

    test("rethrows child process launch errors from bundled npx fallback", async () => {
      const launchError = new Error("spawn failed");

      await expect(
        formatWithPrettier("raw", {
          loadLocalPrettierFn: () => null,
          runBundledNpxCommandFn: () => ({
            error: launchError,
            status: null,
            stdout: "",
            stderr: ""
          })
        })
      ).rejects.toThrow("spawn failed");
    });
  });

  describe("normalizeToLf", () => {
    describe("line ending conversions", () => {
      test.each([
        ["CRLF to LF", "hello\r\nworld\r\n", "hello\nworld\n"],
        ["standalone CR to LF", "hello\rworld\r", "hello\nworld\n"],
        [
          "mixed line endings (CRLF + CR + LF)",
          "line1\r\nline2\rline3\nline4",
          "line1\nline2\nline3\nline4"
        ],
        ["already LF content (no-op)", "hello\nworld\n", "hello\nworld\n"],
        ["no line endings at all", "hello world", "hello world"],
        ["empty string", "", ""],
        ["only CRLF line endings", "\r\n\r\n", "\n\n"],
        ["only CR line endings", "\r\r", "\n\n"],
        ["only LF line endings", "\n\n", "\n\n"]
      ])("%s", (_description, input, expected) => {
        expect(normalizeToLf(input)).toBe(expected);
      });
    });

    describe("CRLF not double-converted", () => {
      test("should convert CRLF to single LF, not double LF", () => {
        const input = "first\r\nsecond\r\nthird";
        const result = normalizeToLf(input);
        expect(result).toBe("first\nsecond\nthird");
        expect(result).not.toContain("\r");
        expect(result).not.toContain("\n\n");
      });

      test("should not produce extra newlines from consecutive CRLF pairs", () => {
        const input = "a\r\n\r\nb";
        const result = normalizeToLf(input);
        expect(result).toBe("a\n\nb");
      });
    });

    describe("integration", () => {
      test("should produce output containing no CR characters", () => {
        const content =
          "---\r\ntitle: Test\r\n---\r\n\r\n# Heading\r\n\rParagraph with mixed\nline endings\r\nand more\rcontent.\n";
        const result = normalizeToLf(content);
        expect(result).not.toContain("\r");
      });
    });
  });

  describe("parseFrontmatter quote boundary handling", () => {
    test("should strip properly paired quotes", () => {
      const content = ["---", 'title: "My Skill"', "---"].join("\n");

      const frontmatter = parseFrontmatter(content);
      expect(frontmatter.title).toBe("My Skill");
    });

    test("should keep mismatched quote boundaries unchanged", () => {
      const content = ["---", "title: \"My Skill'", "---"].join("\n");

      const frontmatter = parseFrontmatter(content);
      expect(frontmatter.title).toBe("\"My Skill'");
    });

    test("should keep unclosed quoted array values unchanged", () => {
      const content = ["---", "tags:", '  - "alpha', '  - "beta"', "---"].join("\n");

      const frontmatter = parseFrontmatter(content);
      expect(frontmatter.tags).toEqual(['"alpha', "beta"]);
    });

    test("should parse frontmatter with lone CR line endings", () => {
      const content = ["---", "title: Test Skill", "id: test-skill", "---", "# Content"].join("\r");

      const frontmatter = parseFrontmatter(content);
      expect(frontmatter).not.toBeNull();
      expect(frontmatter.title).toBe("Test Skill");
      expect(frontmatter.id).toBe("test-skill");
    });

    test("should parse frontmatter with mixed line endings", () => {
      const content = "---\r\ntitle: Mixed\rid: mixed-skill\n---\r\n# Content";

      const frontmatter = parseFrontmatter(content);
      expect(frontmatter).not.toBeNull();
      expect(frontmatter.title).toBe("Mixed");
      expect(frontmatter.id).toBe("mixed-skill");
    });
  });

  describe("integration scenarios", () => {
    test("should format realistic category names correctly", () => {
      const testCases = [
        ["testing", "Testing"],
        ["github-actions", "GitHub Actions"],
        ["documentation", "Documentation"],
        ["scripting", "Scripting"],
        ["api", "API"],
        ["javascript", "JavaScript"],
        ["typescript", "TypeScript"]
      ];
      for (const [input, expected] of testCases) {
        expect(categoryToTitle(input)).toBe(expected);
      }
    });

    test("should handle all single-word brands as categories", () => {
      for (const [slug, expected] of Object.entries(BRAND_NAMES)) {
        expect(categoryToTitle(slug)).toBe(expected);
      }
    });
  });
});
