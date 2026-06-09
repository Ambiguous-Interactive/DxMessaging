/**
 * @fileoverview Tests for validate-skills.js optional field validation logic.
 *
 * These tests validate the missing optional field detection in skill files:
 * - complexity.level - affects Complexity column in skills index
 * - impact.performance.rating - affects Performance column in skills index
 *
 * The validation logic detects missing optional fields that cause '?'
 * placeholders in the generated skills index.
 */

"use strict";

const {
  validateComplexityLevel,
  validatePerformanceRating,
  isValidImpactObject
} = require("../validate-skills.js");

describe("validate-skills optional field validation", () => {
  const testPath = "testing/sample-skill.md";

  describe("complexity.level validation", () => {
    describe("missing complexity field", () => {
      test("should warn when complexity is undefined", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill"
          // complexity is undefined
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("complexity.level");
        expect(warnings[0].message).toContain("Missing 'complexity.level'");
        expect(warnings[0].message).toContain("Complexity column");
      });

      test("should warn when complexity is null", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: null
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("complexity.level");
        expect(warnings[0].message).toContain("Missing 'complexity.level'");
        expect(warnings[0].message).toContain("Complexity column");
      });

      test("should warn when complexity is an empty object", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: {}
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("complexity.level");
        expect(warnings[0].message).toContain("Missing 'complexity.level'");
      });

      test("should warn when complexity.level is undefined", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: {
            // level is undefined
            description: "Some description"
          }
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("complexity.level");
        expect(warnings[0].message).toContain("Missing 'complexity.level'");
      });

      test("should warn when complexity.level is null", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: {
            level: null
          }
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("complexity.level");
        expect(warnings[0].message).toContain("Missing 'complexity.level'");
      });

      test("should warn when complexity.level is an empty string", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: {
            level: ""
          }
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("complexity.level");
        expect(warnings[0].message).toContain("Empty 'complexity.level'");
      });
    });

    describe("valid complexity.level", () => {
      test("should not warn when complexity.level is a valid string", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: {
            level: "intermediate"
          }
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings).toHaveLength(0);
      });

      test("should not warn when complexity.level is a number", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: {
            level: 3
          }
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings).toHaveLength(0);
      });
    });

    describe("warning message format", () => {
      test("should include correct column name in warning message", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill"
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings[0].message).toBe(
          "Missing 'complexity.level' - will show '?' in Complexity column of skills index"
        );
      });

      test("should include correct field name in warning", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill"
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        expect(warnings[0].field).toBe("complexity.level");
      });
    });
  });

  describe("impact.performance.rating validation", () => {
    describe("missing impact field", () => {
      test("should warn when impact is undefined", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill"
          // impact is undefined
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("impact.performance.rating");
        expect(warnings[0].message).toContain("Missing 'impact.performance.rating'");
        expect(warnings[0].message).toContain("Performance column");
      });

      test("should warn when impact is null", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: null
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("impact.performance.rating");
        expect(warnings[0].message).toContain("Missing 'impact.performance.rating'");
        expect(warnings[0].message).toContain("Performance column");
      });

      test("should warn when impact is an empty object", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {}
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("impact.performance.rating");
        expect(warnings[0].message).toContain("Missing 'impact.performance.rating'");
      });
    });

    describe("missing impact.performance field", () => {
      test("should warn when impact.performance is undefined", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            // performance is undefined
            reliability: { rating: "high" }
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("impact.performance.rating");
        expect(warnings[0].message).toContain("Missing 'impact.performance.rating'");
      });

      test("should warn when impact.performance is null", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: null
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("impact.performance.rating");
        expect(warnings[0].message).toContain("Missing 'impact.performance.rating'");
      });

      test("should warn when impact.performance is an empty object", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: {}
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("impact.performance.rating");
        expect(warnings[0].message).toContain("Missing 'impact.performance.rating'");
      });
    });

    describe("missing impact.performance.rating field", () => {
      test("should warn when impact.performance.rating is undefined", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: {
              // rating is undefined
              description: "Some description"
            }
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("impact.performance.rating");
        expect(warnings[0].message).toContain("Missing 'impact.performance.rating'");
      });

      test("should warn when impact.performance.rating is null", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: {
              rating: null
            }
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("impact.performance.rating");
        expect(warnings[0].message).toContain("Missing 'impact.performance.rating'");
      });

      test("should warn when impact.performance.rating is an empty string", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: {
              rating: ""
            }
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(1);
        expect(warnings[0].field).toBe("impact.performance.rating");
        expect(warnings[0].message).toContain("Empty 'impact.performance.rating'");
      });
    });

    describe("valid impact.performance.rating", () => {
      test("should not warn when impact.performance.rating is a valid string", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: {
              rating: "high"
            }
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(0);
      });

      test("should not warn when impact.performance.rating is a number", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: {
              rating: 5
            }
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings).toHaveLength(0);
      });
    });

    describe("warning message format", () => {
      test("should include correct column name in warning message", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill"
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings[0].message).toBe(
          "Missing 'impact.performance.rating' - will show '?' in Performance column of skills index"
        );
      });

      test("should include correct field name in warning", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill"
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        expect(warnings[0].field).toBe("impact.performance.rating");
      });
    });
  });

  describe("edge cases", () => {
    test("should handle frontmatter with both fields missing", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill"
      };

      const complexityWarnings = validateComplexityLevel(frontmatter, testPath);
      const performanceWarnings = validatePerformanceRating(frontmatter, testPath);

      expect(complexityWarnings).toHaveLength(1);
      expect(performanceWarnings).toHaveLength(1);
    });

    test("should handle frontmatter with both fields present", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        complexity: {
          level: "intermediate"
        },
        impact: {
          performance: {
            rating: "high"
          }
        }
      };

      const complexityWarnings = validateComplexityLevel(frontmatter, testPath);
      const performanceWarnings = validatePerformanceRating(frontmatter, testPath);

      expect(complexityWarnings).toHaveLength(0);
      expect(performanceWarnings).toHaveLength(0);
    });

    test("should not warn for complexity.level value of zero (uses explicit null check)", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        complexity: {
          level: 0 // 0 is present (not null/undefined), so not "missing"
        }
      };

      const warnings = validateComplexityLevel(frontmatter, testPath);

      // Uses explicit null check, so 0 is treated as present (not missing)
      expect(warnings).toHaveLength(0);
    });

    test("should not warn for impact.performance.rating value of zero (uses explicit null check)", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        impact: {
          performance: {
            rating: 0 // 0 is present (not null/undefined), so not "missing"
          }
        }
      };

      const warnings = validatePerformanceRating(frontmatter, testPath);

      // Uses explicit null check, so 0 is treated as present (not missing)
      expect(warnings).toHaveLength(0);
    });

    test("should not warn for complexity.level value of false (uses explicit null check)", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        complexity: {
          level: false // false is present (not null/undefined), so not "missing"
        }
      };

      const warnings = validateComplexityLevel(frontmatter, testPath);

      // Uses explicit null check, so false is treated as present (not missing)
      expect(warnings).toHaveLength(0);
    });

    test("should not warn for impact.performance.rating value of false (uses explicit null check)", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        impact: {
          performance: {
            rating: false // false is present (not null/undefined), so not "missing"
          }
        }
      };

      const warnings = validatePerformanceRating(frontmatter, testPath);

      // Uses explicit null check, so false is treated as present (not missing)
      expect(warnings).toHaveLength(0);
    });
  });

  describe("exotic value types", () => {
    describe("NaN values", () => {
      test("should not warn for complexity.level value of NaN (is present, not null/undefined)", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: {
            level: NaN // NaN is present (not null/undefined), so not "missing"
          }
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        // Uses explicit null check, so NaN is treated as present
        expect(warnings).toHaveLength(0);
      });

      test("should not warn for impact.performance.rating value of NaN (is present, not null/undefined)", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: {
              rating: NaN // NaN is present (not null/undefined), so not "missing"
            }
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        // Uses explicit null check, so NaN is treated as present
        expect(warnings).toHaveLength(0);
      });
    });

    describe("empty array values for scalar fields", () => {
      test("should not warn for complexity.level as empty array (is present, not null/undefined)", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: {
            level: [] // Empty array is present (not null/undefined)
          }
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        // String([]) === "", but the local function only checks for null/undefined
        // The actual validateSkill will produce an invalid enum warning
        expect(warnings).toHaveLength(0);
      });

      test("should not warn for impact.performance.rating as empty array (is present, not null/undefined)", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: {
              rating: [] // Empty array is present (not null/undefined)
            }
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        // String([]) === "", but the local function only checks for null/undefined
        expect(warnings).toHaveLength(0);
      });
    });

    describe("object values for scalar fields", () => {
      test("should not warn for complexity.level as object (is present, not null/undefined)", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          complexity: {
            level: { nested: "value" } // Object is present (not null/undefined)
          }
        };

        const warnings = validateComplexityLevel(frontmatter, testPath);

        // String({}) === "[object Object]", but the local function only checks for null/undefined
        expect(warnings).toHaveLength(0);
      });

      test("should not warn for impact.performance.rating as object (is present, not null/undefined)", () => {
        const frontmatter = {
          title: "Sample Skill",
          id: "sample-skill",
          impact: {
            performance: {
              rating: { nested: "value" } // Object is present (not null/undefined)
            }
          }
        };

        const warnings = validatePerformanceRating(frontmatter, testPath);

        // String({}) === "[object Object]", but the local function only checks for null/undefined
        expect(warnings).toHaveLength(0);
      });
    });
  });

  describe("impact object type validation", () => {
    test("should return true for valid impact object", () => {
      const frontmatter = {
        impact: { performance: { rating: "high" } }
      };

      expect(isValidImpactObject(frontmatter)).toBe(true);
    });

    test("should return false for null impact", () => {
      const frontmatter = {
        impact: null
      };

      expect(isValidImpactObject(frontmatter)).toBe(false);
    });

    test("should return false for undefined impact", () => {
      const frontmatter = {};

      expect(isValidImpactObject(frontmatter)).toBe(false);
    });

    test("should return false for string impact (typeof string !== object)", () => {
      const frontmatter = {
        impact: "not-an-object"
      };

      expect(isValidImpactObject(frontmatter)).toBe(false);
    });

    test("should return false for number impact (typeof number !== object)", () => {
      const frontmatter = {
        impact: 42
      };

      expect(isValidImpactObject(frontmatter)).toBe(false);
    });

    test("should return false for boolean impact (typeof boolean !== object)", () => {
      const frontmatter = {
        impact: true
      };

      expect(isValidImpactObject(frontmatter)).toBe(false);
    });

    test("should return true for empty object impact (is valid object, just empty)", () => {
      const frontmatter = {
        impact: {}
      };

      // Empty object is still a valid object to iterate over (Object.keys returns [])
      expect(isValidImpactObject(frontmatter)).toBe(true);
    });

    test("should return false for array impact (arrays should not be valid impact objects)", () => {
      const frontmatter = {
        impact: []
      };

      // Arrays are explicitly excluded since they would cause confusing validation
      // (array indices would be treated as unknown impact types)
      expect(isValidImpactObject(frontmatter)).toBe(false);
    });
  });
});

/**
 * Integration tests using the actual validateSkill function from validate-skills.js.
 *
 * These tests ensure that empty string values in optional fields produce exactly ONE warning,
 * not duplicates from both "invalid enum" and "missing/empty" validation paths.
 */
describe("validate-skills integration tests for optional fields", () => {
  const fs = require("fs");
  const path = require("path");
  const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");
  const { validateSkill } = require("../validate-skills");

  let tempDir;

  beforeEach(() => {
    tempDir = makeTempDir("validate-skills-test");
  });

  afterEach(() => {
    if (tempDir) {
      cleanupDir(tempDir);
    }
  });

  /**
   * Creates a skill file object with all required fields present and valid.
   * @param {string} tempDir - The temporary directory path
   * @param {string} content - The markdown content with frontmatter
   * @returns {Object} Skill file object compatible with validateSkill()
   */
  function createMockSkillFile(tempDir, content) {
    const fileName = "test-skill.md";
    const filePath = path.join(tempDir, fileName);
    fs.writeFileSync(filePath, content, "utf8");
    return {
      path: filePath,
      relativePath: "testing/test-skill.md",
      expectedId: "test-skill",
      category: "testing"
    };
  }

  /**
   * Creates valid frontmatter with all required fields set.
   * Override specific fields as needed.
   * @param {Object} overrides - Fields to override in the frontmatter
   * @returns {string} The YAML frontmatter as a string
   */
  function createValidFrontmatter(overrides = {}) {
    const base = {
      title: "Test Skill",
      id: "test-skill",
      category: "testing",
      version: "1.0.0",
      created: "2026-01-30",
      updated: "2026-01-30",
      status: "stable",
      ...overrides
    };

    let yaml = "---\n";
    for (const [key, value] of Object.entries(base)) {
      if (typeof value === "object" && value !== null) {
        yaml += `${key}:\n`;
        for (const [subKey, subValue] of Object.entries(value)) {
          if (typeof subValue === "object" && subValue !== null) {
            yaml += `  ${subKey}:\n`;
            for (const [subSubKey, subSubValue] of Object.entries(subValue)) {
              yaml += `    ${subSubKey}: "${subSubValue}"\n`;
            }
          } else {
            yaml += `  ${subKey}: "${subValue}"\n`;
          }
        }
      } else {
        yaml += `${key}: "${value}"\n`;
      }
    }
    yaml += "---\n\n## Overview\n\nTest content.\n\n## Solution\n\nTest solution.\n";
    return yaml;
  }

  describe("empty string complexity.level produces exactly one warning", () => {
    test("should produce exactly 1 warning for complexity.level: '' (no duplicate from invalid enum check)", () => {
      const content = createValidFrontmatter({
        complexity: { level: "" }
      });
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Filter warnings for complexity.level field only
      const complexityWarnings = result.warnings.filter((w) => w.field === "complexity.level");

      // Should have exactly 1 warning about empty complexity.level
      expect(complexityWarnings).toHaveLength(1);
      expect(complexityWarnings[0].message).toContain("Empty 'complexity.level'");
      expect(complexityWarnings[0].message).toContain("Complexity column");
    });
  });

  describe("whitespace-only complexity.level produces invalid enum warning", () => {
    test("should produce invalid enum warning for complexity.level: '   ' (whitespace not treated as empty)", () => {
      const content = createValidFrontmatter({
        complexity: { level: "   " }
      });
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Filter warnings for complexity.level field only
      const complexityWarnings = result.warnings.filter((w) => w.field === "complexity.level");

      // Should have exactly 1 warning about invalid enum value
      // Whitespace-only strings are not trimmed, so treated as an invalid enum value
      expect(complexityWarnings).toHaveLength(1);
      expect(complexityWarnings[0].message).toContain("Invalid complexity level");
      expect(complexityWarnings[0].message).toContain("   ");
    });
  });

  describe("empty string impact.performance.rating produces exactly one warning", () => {
    test("should produce exactly 1 warning for impact.performance.rating: '' (no duplicate from invalid enum check)", () => {
      const content = createValidFrontmatter({
        impact: { performance: { rating: "" } }
      });
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Filter warnings for impact.performance.rating field only
      const performanceWarnings = result.warnings.filter(
        (w) => w.field === "impact.performance.rating"
      );

      // Should have exactly 1 warning about empty impact.performance.rating
      expect(performanceWarnings).toHaveLength(1);
      expect(performanceWarnings[0].message).toContain("Empty 'impact.performance.rating'");
      expect(performanceWarnings[0].message).toContain("Performance column");
    });
  });

  describe("whitespace-only impact.performance.rating produces invalid enum warning", () => {
    test("should produce invalid enum warning for impact.performance.rating: '   ' (whitespace not treated as empty)", () => {
      const content = createValidFrontmatter({
        impact: { performance: { rating: "   " } }
      });
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Filter warnings for impact.performance.rating field only
      const performanceWarnings = result.warnings.filter(
        (w) => w.field === "impact.performance.rating"
      );

      // Should have exactly 1 warning about invalid rating
      // Whitespace-only strings are not trimmed, so treated as an invalid enum value
      expect(performanceWarnings).toHaveLength(1);
      expect(performanceWarnings[0].message).toContain("Invalid rating");
      expect(performanceWarnings[0].message).toContain("   ");
    });
  });

  describe("valid enum values produce no warnings for those fields", () => {
    test("should produce no warnings for valid complexity.level value", () => {
      const content = createValidFrontmatter({
        complexity: { level: "intermediate" },
        impact: { performance: { rating: "high" } }
      });
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Should have no warnings for complexity.level or performance.rating
      const relevantWarnings = result.warnings.filter(
        (w) => w.field === "complexity.level" || w.field === "impact.performance.rating"
      );
      expect(relevantWarnings).toHaveLength(0);
    });
  });

  describe("invalid enum values produce exactly one warning", () => {
    test("should produce exactly 1 warning for invalid complexity.level enum value", () => {
      const content = createValidFrontmatter({
        complexity: { level: "super-hard" }
      });
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Filter warnings for complexity.level field only
      const complexityWarnings = result.warnings.filter((w) => w.field === "complexity.level");

      // Should have exactly 1 warning about invalid enum value (not 2)
      expect(complexityWarnings).toHaveLength(1);
      expect(complexityWarnings[0].message).toContain("Invalid complexity level");
      expect(complexityWarnings[0].message).toContain("super-hard");
    });

    test("should produce exactly 1 warning for invalid impact.performance.rating enum value", () => {
      const content = createValidFrontmatter({
        impact: { performance: { rating: "super-high" } }
      });
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Filter warnings for impact.performance.rating field only
      const performanceWarnings = result.warnings.filter(
        (w) => w.field === "impact.performance.rating"
      );

      // Should have exactly 1 warning about invalid rating (not 2)
      expect(performanceWarnings).toHaveLength(1);
      expect(performanceWarnings[0].message).toContain("Invalid rating");
      expect(performanceWarnings[0].message).toContain("super-high");
    });
  });

  describe("exotic value types produce meaningful warnings via String coercion", () => {
    test("should produce warning for version as NaN (String(NaN) === 'NaN' does not match semver)", () => {
      // NaN cannot be directly serialized in YAML, but we test the validation logic
      const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: ".nan"
created: "2026-01-30"
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Filter warnings for version field only
      const versionWarnings = result.warnings.filter((w) => w.field === "version");

      // String(".nan") does not match semver pattern
      expect(versionWarnings).toHaveLength(1);
      expect(versionWarnings[0].message).toContain("should be in semver format");
    });

    test("should produce warning for version as object-like string (simulating String({}) coercion)", () => {
      const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "[object Object]"
created: "2026-01-30"
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Filter warnings for version field only
      const versionWarnings = result.warnings.filter((w) => w.field === "version");

      // String({}) === "[object Object]" does not match semver pattern
      expect(versionWarnings).toHaveLength(1);
      expect(versionWarnings[0].message).toContain("[object Object]");
      expect(versionWarnings[0].message).toContain("should be in semver format");
    });

    test("should produce warning for created date as invalid format (simulating String([]) coercion)", () => {
      // YAML parses [] as an empty array, which when stringified becomes ""
      // For testing, we use a string that shows the behavior
      const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: "[]"
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
      const skillFile = createMockSkillFile(tempDir, content);

      const result = validateSkill(skillFile);

      // Filter warnings for created field only
      const createdWarnings = result.warnings.filter((w) => w.field === "created");

      // "[]" does not match ISO date pattern YYYY-MM-DD
      expect(createdWarnings).toHaveLength(1);
      expect(createdWarnings[0].message).toContain("should be in ISO format");
    });

    test("should handle impact as non-object type gracefully (no crash)", () => {
      // When impact is a string, the typeof check should prevent Object.keys from crashing
      const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: "2026-01-30"
updated: "2026-01-30"
status: "stable"
impact: "not-an-object"
---

## Overview

Test content.

## Solution

Test solution.
`;
      const skillFile = createMockSkillFile(tempDir, content);

      // Should not throw - the typeof check prevents iteration over non-objects
      const result = validateSkill(skillFile);

      // Should have warning about missing impact.performance.rating (since impact is not an object)
      const impactWarnings = result.warnings.filter((w) => w.field === "impact.performance.rating");
      expect(impactWarnings).toHaveLength(1);
      expect(impactWarnings[0].message).toContain("Missing 'impact.performance.rating'");
    });

    test("should handle impact as number gracefully (no crash)", () => {
      const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: "2026-01-30"
updated: "2026-01-30"
status: "stable"
impact: 42
---

## Overview

Test content.

## Solution

Test solution.
`;
      const skillFile = createMockSkillFile(tempDir, content);

      // Should not throw - the typeof check prevents iteration over non-objects
      const result = validateSkill(skillFile);

      // Should have warning about missing impact.performance.rating (since impact is not an object)
      const impactWarnings = result.warnings.filter((w) => w.field === "impact.performance.rating");
      expect(impactWarnings).toHaveLength(1);
    });

    test("should handle impact as boolean gracefully (no crash)", () => {
      const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: "2026-01-30"
updated: "2026-01-30"
status: "stable"
impact: true
---

## Overview

Test content.

## Solution

Test solution.
`;
      const skillFile = createMockSkillFile(tempDir, content);

      // Should not throw - the typeof check prevents iteration over non-objects
      const result = validateSkill(skillFile);

      // Should have warning about missing impact.performance.rating (since impact is not an object)
      const impactWarnings = result.warnings.filter((w) => w.field === "impact.performance.rating");
      expect(impactWarnings).toHaveLength(1);
    });
  });

  describe("exotic value types for version and date fields", () => {
    describe("NaN values", () => {
      test("should produce warning for version field containing YAML NaN (.nan)", () => {
        // YAML parses .nan as NaN, which String() converts to "NaN"
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: .nan
created: "2026-01-30"
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const versionWarnings = result.warnings.filter((w) => w.field === "version");

        // String(NaN) === "NaN" does not match semver pattern
        expect(versionWarnings).toHaveLength(1);
        expect(versionWarnings[0].message).toContain("should be in semver format");
      });

      test("should produce warning for created date field containing YAML NaN (.nan)", () => {
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: .nan
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const createdWarnings = result.warnings.filter((w) => w.field === "created");

        // String(NaN) === "NaN" does not match ISO date pattern
        expect(createdWarnings).toHaveLength(1);
        expect(createdWarnings[0].message).toContain("should be in ISO format");
      });

      test("should produce warning for updated date field containing YAML NaN (.nan)", () => {
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: "2026-01-30"
updated: .nan
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const updatedWarnings = result.warnings.filter((w) => w.field === "updated");

        // String(NaN) === "NaN" does not match ISO date pattern
        expect(updatedWarnings).toHaveLength(1);
        expect(updatedWarnings[0].message).toContain("should be in ISO format");
      });
    });

    describe("empty array values (YAML parses [] as empty array, String([]) is empty)", () => {
      test("should produce warning for version as empty array (empty string is invalid semver)", () => {
        // YAML parses [] as an empty array; String([]) === ""
        // The value is truthy (not null/undefined), so validation runs
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: []
created: "2026-01-30"
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const versionWarnings = result.warnings.filter((w) => w.field === "version");

        // YAML parses [] as empty array, which is truthy (not null/undefined)
        // String([]) === "" produces invalid semver format warning
        expect(versionWarnings).toHaveLength(1);
        expect(versionWarnings[0].message).toContain("should be in semver format");
      });

      test("should produce warning for created date as empty array (empty string is invalid date format)", () => {
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: []
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const createdWarnings = result.warnings.filter((w) => w.field === "created");

        // YAML parses [] as empty array which is truthy (not null/undefined)
        // The validation runs and String([]) === "" produces a warning
        expect(createdWarnings).toHaveLength(1);
        expect(createdWarnings[0].message).toContain("should be in ISO format");
      });

      test("should produce warning for updated date as empty array (empty string is invalid date format)", () => {
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: "2026-01-30"
updated: []
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const updatedWarnings = result.warnings.filter((w) => w.field === "updated");

        // YAML parses [] as empty array which is truthy (not null/undefined)
        // The validation runs and String([]) === "" produces a warning
        expect(updatedWarnings).toHaveLength(1);
        expect(updatedWarnings[0].message).toContain("should be in ISO format");
      });
    });

    describe("object values (YAML parses {} as empty object)", () => {
      test("should produce warning for version as empty object (invalid semver)", () => {
        // YAML parses {} as an empty object
        // String({}) in JavaScript is "[object Object]" but YAML/gray-matter may stringify differently
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: {}
created: "2026-01-30"
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const versionWarnings = result.warnings.filter((w) => w.field === "version");

        // Empty object does not match semver pattern
        expect(versionWarnings).toHaveLength(1);
        expect(versionWarnings[0].message).toContain("should be in semver format");
      });

      test("should produce warning for created date as empty object (invalid date format)", () => {
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: {}
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const createdWarnings = result.warnings.filter((w) => w.field === "created");

        // Empty object does not match ISO date pattern
        expect(createdWarnings).toHaveLength(1);
        expect(createdWarnings[0].message).toContain("should be in ISO format");
      });

      test("should produce warning for updated date as empty object (invalid date format)", () => {
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: "2026-01-30"
updated: {}
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const updatedWarnings = result.warnings.filter((w) => w.field === "updated");

        // Empty object does not match ISO date pattern
        expect(updatedWarnings).toHaveLength(1);
        expect(updatedWarnings[0].message).toContain("should be in ISO format");
      });
    });

    describe("non-empty array values (String([1,2]) coerces to '1,2')", () => {
      test("should produce warning for version as non-empty array (invalid semver)", () => {
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version:
  - 1
  - 0
  - 0
created: "2026-01-30"
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const versionWarnings = result.warnings.filter((w) => w.field === "version");

        // String([1, 0, 0]) === "1,0,0" does not match semver pattern (needs dots, not commas)
        expect(versionWarnings).toHaveLength(1);
        expect(versionWarnings[0].message).toContain("should be in semver format");
      });

      test("should produce warning for created date as non-empty array (invalid date)", () => {
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created:
  - 2026
  - 01
  - 30
updated: "2026-01-30"
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const createdWarnings = result.warnings.filter((w) => w.field === "created");

        // String([2026, 1, 30]) === "2026,1,30" does not match ISO date pattern
        expect(createdWarnings).toHaveLength(1);
        expect(createdWarnings[0].message).toContain("should be in ISO format");
      });

      test("should produce warning for updated date as non-empty array (invalid date)", () => {
        const content = `---
title: "Test Skill"
id: "test-skill"
category: "testing"
version: "1.0.0"
created: "2026-01-30"
updated:
  - 2026
  - 01
  - 30
status: "stable"
---

## Overview

Test content.

## Solution

Test solution.
`;
        const skillFile = createMockSkillFile(tempDir, content);

        const result = validateSkill(skillFile);

        const updatedWarnings = result.warnings.filter((w) => w.field === "updated");

        // String([2026, 1, 30]) === "2026,1,30" does not match ISO date pattern
        expect(updatedWarnings).toHaveLength(1);
        expect(updatedWarnings[0].message).toContain("should be in ISO format");
      });
    });
  });
});
