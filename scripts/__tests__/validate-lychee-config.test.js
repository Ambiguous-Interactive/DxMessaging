/**
 * @fileoverview Tests for validate-lychee-config.js logic.
 *
 * These tests validate the TOML parsing and field validation logic
 * for the lychee configuration validator. Also validates the actual
 * .lychee.toml configuration file against lychee v0.23.0's valid fields.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const {
  parseTopLevelKeys,
  parseTopLevelKeyValues,
  validateFields,
  validateFieldValues,
  validateStrictLinkPolicy,
  VALID_FIELDS,
  VALID_VERBOSE_VALUES
} = require("../validate-lychee-config.js");

const LYCHEE_CONFIG_PATH = path.resolve(__dirname, "../../.lychee.toml");

describe("parseTopLevelKeys", () => {
  test("should parse simple key = value lines", () => {
    const content = ["verbose = true", "no_progress = true", "max_concurrency = 4"].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["verbose", "no_progress", "max_concurrency"]);
  });

  test("should skip comment lines", () => {
    const content = [
      "# This is a comment",
      "verbose = true",
      "# Another comment",
      "timeout = 20"
    ].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["verbose", "timeout"]);
  });

  test("should skip blank lines", () => {
    const content = ["verbose = true", "", "timeout = 20", ""].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["verbose", "timeout"]);
  });

  test("should parse TOML table headers as top-level keys", () => {
    const content = ["[section]", "verbose = true", "[[array_section]]", "timeout = 20"].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["section", "array_section"]);
  });

  test("should parse first segment of dotted table headers", () => {
    const content = [
      '[basic_auth."example.com"]',
      'username = "user"',
      'password = "pass"',
      "[[hosts.production]]",
      'url = "https://example.com"'
    ].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["basic_auth", "hosts"]);
  });

  test("should handle inline comments after values", () => {
    const content = [
      "timeout = 20            # seconds per request",
      "max_retries = 3         # retry transient failures"
    ].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["timeout", "max_retries"]);
  });

  test("should handle string values with equals signs", () => {
    const content = ['user_agent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"'].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["user_agent"]);
  });

  test("should handle array values", () => {
    const content = ['accept = ["200..=299", 429, 502]', 'scheme = ["https", "http"]'].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["accept", "scheme"]);
  });

  test("should handle multi-line array values", () => {
    const content = [
      "exclude = [",
      '  "^https?://localhost",',
      '  "^http://127\\\\.0\\\\.0\\\\.1",',
      "]"
    ].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["exclude"]);
  });

  test("should return empty array for empty content", () => {
    const keys = parseTopLevelKeys("");
    expect(keys).toEqual([]);
  });

  test("should return empty array for comment-only content", () => {
    const content = ["# Just comments", "# Nothing else"].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual([]);
  });

  test("should handle CRLF line endings", () => {
    const content = "verbose = true\r\ntimeout = 20\r\n";

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["verbose", "timeout"]);
  });

  test("should handle lone CR line endings", () => {
    const content = "verbose = true\rtimeout = 20\r";

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["verbose", "timeout"]);
  });

  test("should handle mixed line endings", () => {
    const content = "verbose = true\r\ntimeout = 20\rmax_retries = 3\n";

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["verbose", "timeout", "max_retries"]);
  });

  test("should handle keys with no spaces around equals", () => {
    const content = "verbose=true";

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["verbose"]);
  });

  test("should handle keys with extra spaces around equals", () => {
    const content = "verbose   =   true";

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["verbose"]);
  });

  test("should parse dotted, hyphenated, and quoted keys", () => {
    const content = [
      'header.Accept = "text/html"',
      'my-key = "value"',
      '"quoted.key" = "value"',
      "verbose = true"
    ].join("\n");

    const keys = parseTopLevelKeys(content);
    expect(keys).toEqual(["header", "my-key", "quoted.key", "verbose"]);
  });
});

describe("validateFields", () => {
  test("should accept all valid fields", () => {
    const validKeys = ["verbose", "no_progress", "max_concurrency", "timeout"];
    const { errors, warnings } = validateFields(validKeys);

    expect(errors).toEqual([]);
    expect(warnings).toEqual([]);
  });

  test("should reject invalid fields", () => {
    const keys = ["verbose", "invalid_field", "timeout"];
    const { errors } = validateFields(keys);

    expect(errors).toHaveLength(1);
    expect(errors[0]).toContain("invalid_field");
    expect(errors[0]).toContain("not a valid lychee v0.23.0 configuration option");
  });

  test("should reject multiple invalid fields", () => {
    const keys = ["deprecated_field", "also_bad", "verbose"];
    const { errors } = validateFields(keys);

    expect(errors).toHaveLength(2);
    expect(errors[0]).toContain("deprecated_field");
    expect(errors[1]).toContain("also_bad");
  });

  test("should warn about duplicate fields", () => {
    const keys = ["verbose", "timeout", "verbose"];
    const { errors, warnings } = validateFields(keys);

    expect(errors).toEqual([]);
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain("Duplicate field 'verbose'");
  });

  test("should handle empty key list", () => {
    const { errors, warnings } = validateFields([]);

    expect(errors).toEqual([]);
    expect(warnings).toEqual([]);
  });

  test("should report both errors and warnings together", () => {
    const keys = ["verbose", "verbose", "bad_field"];
    const { errors, warnings } = validateFields(keys);

    expect(errors).toHaveLength(1);
    expect(errors[0]).toContain("bad_field");
    expect(warnings).toHaveLength(1);
    expect(warnings[0]).toContain("Duplicate field 'verbose'");
  });
});

describe("parseTopLevelKeyValues", () => {
  test("should parse key-value pairs from simple lines", () => {
    const content = ['verbose = "info"', "no_progress = true", "max_concurrency = 4"].join("\n");

    const pairs = parseTopLevelKeyValues(content);
    expect(pairs).toEqual([
      { key: "verbose", value: '"info"' },
      { key: "no_progress", value: "true" },
      { key: "max_concurrency", value: "4" }
    ]);
  });

  test("should strip inline comments from unquoted values", () => {
    const content = "timeout = 20            # seconds per request";

    const pairs = parseTopLevelKeyValues(content);
    expect(pairs).toEqual([{ key: "timeout", value: "20" }]);
  });

  test("should preserve quoted string values", () => {
    const content = 'user_agent = "Mozilla/5.0 # not a comment"';

    const pairs = parseTopLevelKeyValues(content);
    expect(pairs).toEqual([{ key: "user_agent", value: '"Mozilla/5.0 # not a comment"' }]);
  });

  test("should parse keys in table context with keyPath metadata", () => {
    const content = ["# Comment", "", "[section]", 'verbose = "debug"'].join("\n");

    const pairs = parseTopLevelKeyValues(content);
    expect(pairs).toEqual([{ key: "verbose", value: '"debug"', keyPath: "section.verbose" }]);
  });

  test("should strip inline comments while preserving hashes in quoted values", () => {
    const content = 'description = "Feature #123" # trailing comment';

    const pairs = parseTopLevelKeyValues(content);
    expect(pairs).toEqual([{ key: "description", value: '"Feature #123"' }]);
  });

  test("should handle CRLF line endings", () => {
    const content = 'verbose = "info"\r\ntimeout = 20\r\n';

    const pairs = parseTopLevelKeyValues(content);
    expect(pairs).toEqual([
      { key: "verbose", value: '"info"' },
      { key: "timeout", value: "20" }
    ]);
  });

  test("should handle lone CR line endings", () => {
    const content = 'verbose = "info"\rtimeout = 20\r';

    const pairs = parseTopLevelKeyValues(content);
    expect(pairs).toEqual([
      { key: "verbose", value: '"info"' },
      { key: "timeout", value: "20" }
    ]);
  });

  test("should return empty array for empty content", () => {
    const pairs = parseTopLevelKeyValues("");
    expect(pairs).toEqual([]);
  });
});

describe("validateFieldValues", () => {
  describe("verbose field validation", () => {
    test.each(VALID_VERBOSE_VALUES)("should accept valid verbose value '%s'", (level) => {
      const keyValues = [{ key: "verbose", value: `"${level}"` }];
      const { errors } = validateFieldValues(keyValues);
      expect(errors).toEqual([]);
    });

    test("should reject boolean true (old format)", () => {
      const keyValues = [{ key: "verbose", value: "true" }];
      const { errors } = validateFieldValues(keyValues);

      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain("Invalid value for 'verbose'");
      expect(errors[0]).toContain("true");
      expect(errors[0]).toContain("must be a quoted string");
    });

    test("should reject boolean false", () => {
      const keyValues = [{ key: "verbose", value: "false" }];
      const { errors } = validateFieldValues(keyValues);

      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain("Invalid value for 'verbose'");
      expect(errors[0]).toContain("must be a quoted string");
    });

    test("should reject unquoted string value", () => {
      const keyValues = [{ key: "verbose", value: "info" }];
      const { errors } = validateFieldValues(keyValues);

      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain("must be a quoted string");
    });

    test("should reject invalid quoted string value", () => {
      const keyValues = [{ key: "verbose", value: '"verbose"' }];
      const { errors } = validateFieldValues(keyValues);

      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain("Invalid value for 'verbose'");
      expect(errors[0]).toContain("must be one of");
    });

    test("should reject numeric value", () => {
      const keyValues = [{ key: "verbose", value: "1" }];
      const { errors } = validateFieldValues(keyValues);

      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain("must be a quoted string");
    });

    test("should accept single-quoted valid verbose value", () => {
      const keyValues = [{ key: "verbose", value: "'info'" }];
      const { errors } = validateFieldValues(keyValues);
      expect(errors).toEqual([]);
    });

    test("should reject mismatched quote boundaries", () => {
      const keyValues = [{ key: "verbose", value: "\"info'" }];
      const { errors } = validateFieldValues(keyValues);

      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain("must be a quoted string");
    });

    test("should reject unclosed opening quote", () => {
      const keyValues = [{ key: "verbose", value: '"info' }];
      const { errors } = validateFieldValues(keyValues);

      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain("must be a quoted string");
    });

    test("should reject value with trailing quote only", () => {
      const keyValues = [{ key: "verbose", value: 'info"' }];
      const { errors } = validateFieldValues(keyValues);

      expect(errors).toHaveLength(1);
      expect(errors[0]).toContain("must be a quoted string");
    });
  });

  test("should not produce errors for non-verbose fields", () => {
    const keyValues = [
      { key: "timeout", value: "20" },
      { key: "no_progress", value: "true" },
      { key: "max_concurrency", value: "4" }
    ];
    const { errors, warnings } = validateFieldValues(keyValues);
    expect(errors).toEqual([]);
    expect(warnings).toEqual([]);
  });

  test("should handle empty key-value array", () => {
    const { errors, warnings } = validateFieldValues([]);
    expect(errors).toEqual([]);
    expect(warnings).toEqual([]);
  });

  test("should validate verbose among other fields", () => {
    const keyValues = [
      { key: "timeout", value: "20" },
      { key: "verbose", value: "true" },
      { key: "max_retries", value: "3" }
    ];
    const { errors } = validateFieldValues(keyValues);

    expect(errors).toHaveLength(1);
    expect(errors[0]).toContain("verbose");
  });

  test("should validate verbose value when defined inside a table", () => {
    const keyValues = [{ key: "verbose", value: "true", keyPath: "logging.verbose" }];
    const { errors } = validateFieldValues(keyValues);

    expect(errors).toHaveLength(1);
    expect(errors[0]).toContain("logging.verbose");
    expect(errors[0]).toContain("must be a quoted string");
  });
});

describe("validateStrictLinkPolicy", () => {
  test("accepts this repository's strict status and exclusion policy", () => {
    const content = [
      'accept = ["200..=299", 429, 502]',
      "exclude = [",
      '  "^https?://localhost",',
      '  "^https://support\\\\.unity\\\\.com/"',
      "]"
    ].join("\n");

    expect(validateStrictLinkPolicy(content)).toEqual({ errors: [], warnings: [] });
  });

  test.each(["403", '"403"', '"400..=499"', '"403..=403"'])(
    "rejects accept entry %s because it includes HTTP 403",
    (entry) => {
      const content = [`accept = ["200..=299", ${entry}, 502]`, "exclude = []"].join("\n");

      const { errors } = validateStrictLinkPolicy(content);
      expect(errors).toContain("Invalid value for 'accept': HTTP 403 must not be globally accepted.");
    }
  );

  test("rejects multiline accept arrays that include HTTP 403", () => {
    const content = [
      "accept = [",
      '  "200..=299",',
      "  403,",
      "  502",
      "]",
      "exclude = []"
    ].join("\n");

    const { errors } = validateStrictLinkPolicy(content);
    expect(errors).toContain("Invalid value for 'accept': HTTP 403 must not be globally accepted.");
  });

  test("rejects broad WebAIM exclusions", () => {
    const content = [
      'accept = ["200..=299", 429, 502]',
      "exclude = [",
      '  "^https://webaim\\\\.org/",',
      "]"
    ].join("\n");

    const { errors } = validateStrictLinkPolicy(content);
    expect(errors.join("\n")).toContain("broad WebAIM exclusions are not allowed");
  });
});

// SYNC: VALID_VERBOSE_VALUES is the source of truth defined in validate-lychee-config.js VALID_VERBOSE_VALUES constant
describe("VALID_VERBOSE_VALUES", () => {
  test("should be a non-empty array", () => {
    expect(Array.isArray(VALID_VERBOSE_VALUES)).toBe(true);
    expect(VALID_VERBOSE_VALUES.length).toBeGreaterThan(0);
  });

  test("should contain expected log levels", () => {
    expect(VALID_VERBOSE_VALUES).toContain("error");
    expect(VALID_VERBOSE_VALUES).toContain("warn");
    expect(VALID_VERBOSE_VALUES).toContain("info");
    expect(VALID_VERBOSE_VALUES).toContain("debug");
    expect(VALID_VERBOSE_VALUES).toContain("trace");
  });

  test("should contain exactly 5 values", () => {
    expect(VALID_VERBOSE_VALUES).toHaveLength(5);
  });
});

// SYNC: VALID_FIELDS is the source of truth defined in validate-lychee-config.js VALID_FIELDS constant
describe("VALID_FIELDS", () => {
  test("should be a non-empty Set", () => {
    expect(VALID_FIELDS).toBeInstanceOf(Set);
    expect(VALID_FIELDS.size).toBeGreaterThan(0);
  });

  test("should contain core lychee fields used in this repository", () => {
    // Fields used in .lychee.toml in this repository
    const repoFields = [
      "verbose",
      "no_progress",
      "max_concurrency",
      "include_mail",
      "timeout",
      "max_retries",
      "retry_wait_time",
      "max_redirects",
      "user_agent",
      "accept",
      "scheme",
      "exclude"
    ];

    for (const field of repoFields) {
      expect(VALID_FIELDS.has(field)).toBe(true);
    }
  });

  test("should contain commonly used lychee fields", () => {
    const commonFields = [
      "cache",
      "output",
      "format",
      "base_url",
      "include",
      "exclude_file",
      "exclude_path",
      "github_token",
      "method",
      "header"
    ];

    for (const field of commonFields) {
      expect(VALID_FIELDS.has(field)).toBe(true);
    }
  });

  test("should not contain obviously invalid field names", () => {
    const invalidNames = ["not_a_field", "deprecated", "invalid", ""];

    for (const name of invalidNames) {
      expect(VALID_FIELDS.has(name)).toBe(false);
    }
  });
});

describe("End-to-end validation", () => {
  test("should validate a configuration matching this repository's .lychee.toml", () => {
    const content = [
      'verbose = "info"',
      "no_progress = true",
      "max_concurrency = 4",
      "include_mail = false",
      "",
      "# Network tuning",
      "timeout = 20            # seconds per request",
      "max_retries = 3         # retry transient failures",
      "retry_wait_time = 2     # seconds between retries",
      "max_redirects = 10",
      'user_agent = "Mozilla/5.0"',
      "",
      'accept = ["200..=299", 429, 502]',
      "",
      'scheme = ["https", "http"]',
      "",
      "exclude = [",
      '  "^https?://localhost",',
      "]"
    ].join("\n");

    const keys = parseTopLevelKeys(content);
    const { errors, warnings } = validateFields(keys);

    expect(errors).toEqual([]);
    expect(warnings).toEqual([]);
  });

  test("should validate field values in a valid configuration", () => {
    const content = [
      'verbose = "info"',
      "no_progress = true",
      "max_concurrency = 4",
      "include_mail = false",
      "",
      "# Network tuning",
      "timeout = 20            # seconds per request",
      "max_retries = 3         # retry transient failures",
      "retry_wait_time = 2     # seconds between retries",
      "max_redirects = 10",
      'user_agent = "Mozilla/5.0"',
      "",
      'accept = ["200..=299", 429, 502]',
      "",
      'scheme = ["https", "http"]',
      "",
      "exclude = [",
      '  "^https?://localhost",',
      "]"
    ].join("\n");

    const keyValues = parseTopLevelKeyValues(content);
    const { errors, warnings } = validateFieldValues(keyValues);

    expect(errors).toEqual([]);
    expect(warnings).toEqual([]);
  });

  test("should catch a configuration with deprecated or invalid fields", () => {
    const content = ['verbose = "info"', "max_connections = 4", "exclude_mail = true"].join("\n");

    const keys = parseTopLevelKeys(content);
    const { errors } = validateFields(keys);

    expect(errors).toHaveLength(2);
    expect(errors.some((e) => e.includes("max_connections"))).toBe(true);
    expect(errors.some((e) => e.includes("exclude_mail"))).toBe(true);
  });

  test("should catch invalid verbose value via value validation", () => {
    const content = ["verbose = true", "timeout = 20"].join("\n");

    const keyValues = parseTopLevelKeyValues(content);
    const { errors } = validateFieldValues(keyValues);

    expect(errors).toHaveLength(1);
    expect(errors[0]).toContain("verbose");
    expect(errors[0]).toContain("must be a quoted string");
  });
});

describe("actual .lychee.toml config file validation", () => {
  let configContent;
  let configKeys;

  beforeAll(() => {
    if (!fs.existsSync(LYCHEE_CONFIG_PATH)) {
      throw new Error(
        `Expected .lychee.toml at ${LYCHEE_CONFIG_PATH}, but the file does not exist`
      );
    }

    configContent = fs.readFileSync(LYCHEE_CONFIG_PATH, "utf8");
    configKeys = parseTopLevelKeys(configContent);
  });

  test("config file should exist", () => {
    expect(fs.existsSync(LYCHEE_CONFIG_PATH)).toBe(true);
  });

  test("config file should not be empty", () => {
    expect(configContent.trim().length).toBeGreaterThan(0);
  });

  test("config should contain at least one top-level key", () => {
    expect(configKeys.length).toBeGreaterThan(0);
  });

  describe("all config keys are valid for lychee v0.23.0", () => {
    test("each parsed key should be a recognized lychee v0.23.0 field", () => {
      for (const key of configKeys) {
        expect(VALID_FIELDS.has(key)).toBe(true);
      }
    });
  });

  describe("no deprecated fields are present", () => {
    const deprecatedFieldMappings = [
      ["exclude_mail", "use 'include_mail = false' instead"],
      ["retries", "renamed to 'max_retries'"],
      ["verbosity", "removed; use 'verbose = \"info\"' instead"]
    ];

    test.each(deprecatedFieldMappings)(
      "deprecated field '%s' should not be present (%s)",
      (deprecatedField) => {
        expect(configKeys).not.toContain(deprecatedField);
      }
    );
  });

  describe("essential fields are present", () => {
    const essentialFields = [
      ["include_mail", "controls whether mailto: links are checked"],
      ["timeout", "prevents hanging on slow endpoints"],
      ["max_retries", "handles transient network failures"],
      ["exclude", "prevents checking known-bad URLs"],
      ["accept", "defines which HTTP status codes are acceptable"]
    ];

    test.each(essentialFields)("essential field '%s' should be present (%s)", (field) => {
      expect(configKeys).toContain(field);
    });
  });

  test("config should pass full validation with no errors", () => {
    const { errors } = validateFields(configKeys);
    expect(errors).toEqual([]);
  });

  test("config should pass value validation with no errors", () => {
    const configKeyValues = parseTopLevelKeyValues(configContent);
    const { errors } = validateFieldValues(configKeyValues);

    expect(errors).toEqual([], "actual .lychee.toml should have no field value errors");
  });

  test("config should pass value validation with no warnings", () => {
    const configKeyValues = parseTopLevelKeyValues(configContent);
    const { warnings } = validateFieldValues(configKeyValues);

    expect(warnings).toEqual([], "actual .lychee.toml should have no field value warnings");
  });

  test("config should not globally accept HTTP 403", () => {
    const { errors } = validateStrictLinkPolicy(configContent);

    expect(errors).not.toContain(
      "Invalid value for 'accept': HTTP 403 must not be globally accepted."
    );
  });

  test("config should not broadly exclude WebAIM links", () => {
    const { errors } = validateStrictLinkPolicy(configContent);

    expect(errors.join("\n")).not.toContain("broad WebAIM exclusions are not allowed");
  });
});
