/**
 * @fileoverview Tests for validate-skills.js required field validation logic.
 *
 * These tests validate the required field detection in skill files:
 * - Missing required fields (undefined/null)
 * - Empty required fields (empty string)
 * - Valid required fields
 *
 * The validation logic ensures that skill files have all required frontmatter
 * fields properly populated.
 */

"use strict";

const {
  parseFrontmatter,
  validateRequiredField,
  validateRequiredFields,
  REQUIRED_FIELDS
} = require("../validate-skills.js");

describe("validate-skills required field validation", () => {
  const testPath = "testing/sample-skill.md";

  describe("missing required fields (undefined/null)", () => {
    test("should error when required field is undefined", () => {
      const frontmatter = {
        id: "sample-skill",
        category: "testing",
        version: "1.0.0",
        created: "2025-01-01",
        updated: "2025-01-01",
        status: "active"
        // title is undefined
      };

      const errors = validateRequiredField(frontmatter, "title", testPath);

      expect(errors).toHaveLength(1);
      expect(errors[0].field).toBe("title");
      expect(errors[0].message).toContain("Required field 'title' is missing");
    });

    test("should error when required field is null", () => {
      const frontmatter = {
        title: null,
        id: "sample-skill",
        category: "testing",
        version: "1.0.0",
        created: "2025-01-01",
        updated: "2025-01-01",
        status: "active"
      };

      const errors = validateRequiredField(frontmatter, "title", testPath);

      expect(errors).toHaveLength(1);
      expect(errors[0].field).toBe("title");
      expect(errors[0].message).toContain("Required field 'title' is missing");
    });
  });

  describe("empty required fields", () => {
    test("should error when required field is empty string", () => {
      const frontmatter = {
        title: "",
        id: "sample-skill",
        category: "testing",
        version: "1.0.0",
        created: "2025-01-01",
        updated: "2025-01-01",
        status: "active"
      };

      const errors = validateRequiredField(frontmatter, "title", testPath);

      expect(errors).toHaveLength(1);
      expect(errors[0].field).toBe("title");
      expect(errors[0].message).toContain("Required field 'title' is empty");
    });

    test("should distinguish between missing and empty in error messages", () => {
      const missingFrontmatter = {
        id: "sample-skill"
        // title is undefined
      };

      const emptyFrontmatter = {
        title: "",
        id: "sample-skill"
      };

      const missingErrors = validateRequiredField(missingFrontmatter, "title", testPath);
      const emptyErrors = validateRequiredField(emptyFrontmatter, "title", testPath);

      expect(missingErrors[0].message).toContain("is missing");
      expect(emptyErrors[0].message).toContain("is empty");
      expect(missingErrors[0].message).not.toEqual(emptyErrors[0].message);
    });
  });

  describe("valid required fields", () => {
    test("should not error when required field has valid string value", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        category: "testing",
        version: "1.0.0",
        created: "2025-01-01",
        updated: "2025-01-01",
        status: "active"
      };

      const errors = validateRequiredField(frontmatter, "title", testPath);

      expect(errors).toHaveLength(0);
    });

    test("should not error when all required fields are present", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: "sample-skill",
        category: "testing",
        version: "1.0.0",
        created: "2025-01-01",
        updated: "2025-01-01",
        status: "active"
      };

      const errors = validateRequiredFields(frontmatter, testPath);

      expect(errors).toHaveLength(0);
    });
  });

  describe("multiple missing fields", () => {
    test("should report errors for all missing required fields", () => {
      const frontmatter = {
        title: "Sample Skill",
        // id is undefined
        category: "testing",
        // version is undefined
        created: "2025-01-01",
        updated: "2025-01-01",
        status: "active"
      };

      const errors = validateRequiredFields(frontmatter, testPath);

      expect(errors).toHaveLength(2);
      expect(errors.map((e) => e.field)).toContain("id");
      expect(errors.map((e) => e.field)).toContain("version");
    });

    test("should report errors for mix of missing and empty fields", () => {
      const frontmatter = {
        title: "Sample Skill",
        id: null, // missing (null)
        category: "", // empty
        version: "1.0.0",
        created: "2025-01-01",
        updated: "2025-01-01",
        status: "active"
      };

      const errors = validateRequiredFields(frontmatter, testPath);

      expect(errors).toHaveLength(2);

      const idError = errors.find((e) => e.field === "id");
      const categoryError = errors.find((e) => e.field === "category");

      expect(idError.message).toContain("is missing");
      expect(categoryError.message).toContain("is empty");
    });
  });

  describe("each required field", () => {
    test.each(REQUIRED_FIELDS)("should validate '%s' as required field", (field) => {
      const frontmatter = {};

      const errors = validateRequiredField(frontmatter, field, testPath);

      expect(errors).toHaveLength(1);
      expect(errors[0].field).toBe(field);
      expect(errors[0].message).toContain(`Required field '${field}' is missing`);
    });
  });
});

describe("validate-skills parseFrontmatter quote boundary handling", () => {
  test("should strip properly paired quote boundaries", () => {
    const content = ["---", "title: 'Sample Skill'", "---"].join("\n");

    const frontmatter = parseFrontmatter(content);
    expect(frontmatter.title).toBe("Sample Skill");
  });

  test("should keep mismatched quote boundaries unchanged", () => {
    const content = ["---", "title: 'Sample Skill\"", "---"].join("\n");

    const frontmatter = parseFrontmatter(content);
    expect(frontmatter.title).toBe("'Sample Skill\"");
  });

  test("should keep unclosed array string quote unchanged", () => {
    const content = ["---", "tags:", "  - 'alpha", "  - 'beta'", "---"].join("\n");

    const frontmatter = parseFrontmatter(content);
    expect(frontmatter.tags).toEqual(["'alpha", "beta"]);
  });

  test("should parse frontmatter with lone CR line endings", () => {
    const content = ["---", "title: Sample Skill", "id: sample-skill", "---", "# Content"].join(
      "\r"
    );

    const frontmatter = parseFrontmatter(content);
    expect(frontmatter).not.toBeNull();
    expect(frontmatter.title).toBe("Sample Skill");
    expect(frontmatter.id).toBe("sample-skill");
  });

  test("should parse frontmatter with mixed line endings", () => {
    const content = "---\r\ntitle: Mixed\rid: mixed-skill\n---\r\n# Content";

    const frontmatter = parseFrontmatter(content);
    expect(frontmatter).not.toBeNull();
    expect(frontmatter.title).toBe("Mixed");
    expect(frontmatter.id).toBe("mixed-skill");
  });
});
