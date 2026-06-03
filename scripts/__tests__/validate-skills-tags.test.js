/**
 * @fileoverview Tests for validate-skills.js tags validation logic.
 *
 * These tests validate the tags field validation in skill files:
 * - Missing tags (undefined/null)
 * - Wrong type (string, object, number instead of array)
 * - Empty tags array
 * - Valid tags array
 *
 * The validation logic ensures that skill files have properly formatted
 * tags arrays for the skills index generation.
 */

"use strict";

const { validateTags } = require("../validate-skills.js");

describe("validate-skills tags validation", () => {
  const testPath = "testing/sample-skill.md";

  describe("missing tags field", () => {
    test("should warn when tags is undefined", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill"
        // tags is undefined
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      expect(warnings[0].message).toContain("Missing 'tags' array");
    });

    test("should warn when tags is null", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: null
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      expect(warnings[0].message).toContain("Missing 'tags' array");
    });
  });

  describe("wrong type for tags", () => {
    test("should warn when tags is an empty string", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: ""
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      // Empty string is defined but wrong type
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got string");
    });

    test("should warn when tags is a zero", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: 0
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      // 0 is defined but wrong type
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got number");
    });

    test("should warn when tags is boolean false", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: false
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      // false is defined but wrong type
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got boolean");
    });

    test("should warn when tags is a string", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: "testing, validation"
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got string");
    });

    test("should warn when tags is a number", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: 42
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got number");
    });

    test("should warn when tags is an object", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: { tag1: "testing", tag2: "validation" }
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got object");
    });

    test("should warn when tags is boolean true", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: true
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got boolean");
    });

    test("should warn when tags is a function", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: () => ["test"]
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got function");
    });

    test("should warn when tags is a symbol", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: Symbol("tags")
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got symbol");
    });
  });

  describe("empty tags array", () => {
    test("should warn when tags is an empty array", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: []
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].field).toBe("tags");
      expect(warnings[0].message).toContain("Empty 'tags' array");
    });
  });

  describe("valid tags array", () => {
    test("should not warn when tags has one element", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: ["testing"]
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(0);
    });

    test("should not warn when tags has multiple elements", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: ["testing", "validation", "skills"]
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(0);
    });

    test("should not warn when tags contains empty string", () => {
      // Empty string in array is valid from validation perspective
      // (content validation would be separate)
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: [""]
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(0);
    });

    test("should not warn when tags contains mixed types", () => {
      // Mixed types in array is valid from type validation perspective
      // (content validation would be separate)
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: ["testing", 123, null]
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(0);
    });
  });

  describe("error message content", () => {
    test("should include skills index reference in missing tags message", () => {
      const frontmatter = { title: "Test" };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings[0].message).toContain("will show empty Tags column in skills index");
    });

    test("should include skills index reference in wrong type message", () => {
      const frontmatter = { title: "Test", tags: "string" };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings[0].message).toContain("will show empty Tags column in skills index");
    });

    test("should include skills index reference in empty array message", () => {
      const frontmatter = { title: "Test", tags: [] };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings[0].message).toContain("will show empty Tags column in skills index");
    });

    test("should include correct file path in warning", () => {
      const customPath = "custom/path/to/skill.md";
      const frontmatter = { title: "Test" };

      const warnings = validateTags(frontmatter, customPath);

      expect(warnings[0].file).toBe(customPath);
    });

    test("should report correct field name", () => {
      const frontmatter = { title: "Test" };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings[0].field).toBe("tags");
    });
  });

  describe("edge cases", () => {
    test("should handle frontmatter with no properties", () => {
      const frontmatter = {};

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].message).toContain("Missing 'tags' array");
    });

    test("should handle array-like objects", () => {
      // Array-like object (not a real array)
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: { 0: "test", 1: "tags", length: 2 }
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(1);
      expect(warnings[0].message).toContain("'tags' must be an array");
      expect(warnings[0].message).toContain("got object");
    });

    test("should accept array created with Array constructor", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: new Array("testing", "validation")
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(0);
    });

    test("should handle very long tags array", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        tags: Array.from({ length: 100 }, (_, i) => `tag${i}`)
      };

      const warnings = validateTags(frontmatter, testPath);

      expect(warnings).toHaveLength(0);
    });
  });
});
