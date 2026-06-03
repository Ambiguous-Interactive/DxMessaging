#!/usr/bin/env node
/**
 * validate-skills.js
 *
 * Validates all skill files in .llm/skills/ for schema compliance and file size limits.
 * Checks YAML frontmatter for required fields, validates field values against allowed
 * enums, and enforces file size limits to ensure effective LLM context usage.
 *
 * @usage
 *   node scripts/validate-skills.js
 *
 * @exitcodes
 *   0 - Success (no errors; warnings may be present)
 *   1 - Validation failed (one or more errors found)
 *
 * @example
 *   # Run from repository root
 *   node scripts/validate-skills.js
 *
 *   # Run in CI pipeline
 *   node scripts/validate-skills.js || exit 1
 *
 *   # Run from any directory
 *   node /path/to/repo/scripts/validate-skills.js
 */

const fs = require("fs");
const path = require("path");
const { stripMatchingBoundaryQuotes, normalizeToLf } = require("./lib/quote-parser");

const LLM_DIR = path.join(__dirname, "..", ".llm");
const SKILLS_DIR = path.join(LLM_DIR, "skills");
const CONTEXT_FILE = path.join(LLM_DIR, "context.md");
const EXCLUDED_FILES = ["index.md", "specification.md"];
const EXCLUDED_DIRS = ["templates"];

// Files excluded from "short file" informational messages
const SHORT_FILE_EXCLUDES = ["context.md"];

// Required fields that must be present in all skill files
const REQUIRED_FIELDS = ["title", "id", "category", "version", "created", "updated", "status"];

// File size limits (in lines) for all markdown files under .llm/
const LINE_LIMIT_IDEAL_MIN = 120;
const LINE_LIMIT_IDEAL_MAX = 260;
const LINE_LIMIT_HARD_MAX = 300;

const CONTEXT_INDEX_LINK_FRAGMENT = "./skills/index.md";

const LOG_SYMBOLS = {
  folder: { unicode: "📁", ascii: "[llm]" },
  doc: { unicode: "📄", ascii: "[file]" },
  size: { unicode: "📊", ascii: "[size]" },
  error: { unicode: "❌", ascii: "[error]" },
  warn: { unicode: "⚠️", ascii: "[warn]" },
  ok: { unicode: "✅", ascii: "[ok]" },
  draft: { unicode: "📝", ascii: "[draft]" }
};

function shouldUseAsciiOutput(argv = process.argv, env = process.env, platform = process.platform) {
  return argv.includes("--ascii") || env.DXM_ASCII_LOGS === "1" || platform === "win32";
}

function logSymbol(name, options = {}) {
  const { argv = process.argv, env = process.env, platform = process.platform } = options;
  const symbol = LOG_SYMBOLS[name];
  if (!symbol) {
    return "";
  }

  return shouldUseAsciiOutput(argv, env, platform) ? symbol.ascii : symbol.unicode;
}

const VALID_CATEGORIES = [
  "performance",
  "testing",
  "solid",
  "messaging",
  "unity",
  "concurrency",
  "architecture",
  "error-handling",
  "code-generation",
  "documentation",
  "scripting",
  "github-actions",
  "packaging"
];

const VALID_COMPLEXITY_LEVELS = ["basic", "intermediate", "advanced", "expert"];
const VALID_STATUSES = ["draft", "review", "stable", "deprecated"];
const VALID_IMPACT_RATINGS = ["none", "low", "medium", "high", "critical"];
const VALID_IMPACT_TYPES = ["performance", "maintainability", "testability"];

class ValidationError {
  constructor(file, field, message) {
    this.file = file;
    this.field = field;
    this.message = message;
  }

  toString() {
    return `[${this.file}] ${this.field}: ${this.message}`;
  }
}

/**
 * Validates a single required field of a frontmatter object.
 *
 * @param {Object} frontmatter - The parsed frontmatter object
 * @param {string} field - The field name to validate
 * @param {string} relativePath - The relative path for error reporting
 * @returns {ValidationError[]} Array of validation errors
 */
function validateRequiredField(frontmatter, field, relativePath) {
  const errors = [];

  if (frontmatter[field] === undefined || frontmatter[field] === null) {
    errors.push(new ValidationError(relativePath, field, `Required field '${field}' is missing`));
  } else if (frontmatter[field] === "") {
    errors.push(new ValidationError(relativePath, field, `Required field '${field}' is empty`));
  }

  return errors;
}

/**
 * Validates the tags field of a frontmatter object.
 *
 * @param {Object} frontmatter - The parsed frontmatter object
 * @param {string} relativePath - The relative path for error reporting
 * @returns {ValidationError[]} Array of validation warnings
 */
function validateTags(frontmatter, relativePath) {
  const warnings = [];

  if (frontmatter.tags == null) {
    warnings.push(
      new ValidationError(
        relativePath,
        "tags",
        `Missing 'tags' array - will show empty Tags column in skills index`
      )
    );
  } else if (!Array.isArray(frontmatter.tags)) {
    warnings.push(
      new ValidationError(
        relativePath,
        "tags",
        `'tags' must be an array, got ${typeof frontmatter.tags} - will show empty Tags column in skills index`
      )
    );
  } else if (frontmatter.tags.length === 0) {
    warnings.push(
      new ValidationError(
        relativePath,
        "tags",
        `Empty 'tags' array - will show empty Tags column in skills index`
      )
    );
  }

  return warnings;
}

/**
 * Validates the complexity.level field of a frontmatter object.
 *
 * @param {Object} frontmatter - The parsed frontmatter object
 * @param {string} relativePath - The relative path for error reporting
 * @returns {ValidationError[]} Array of validation warnings
 */
function validateComplexityLevel(frontmatter, relativePath) {
  const warnings = [];

  if (frontmatter.complexity == null || frontmatter.complexity.level == null) {
    warnings.push(
      new ValidationError(
        relativePath,
        "complexity.level",
        `Missing 'complexity.level' - will show '?' in Complexity column of skills index`
      )
    );
  } else if (frontmatter.complexity.level === "") {
    warnings.push(
      new ValidationError(
        relativePath,
        "complexity.level",
        `Empty 'complexity.level' - will show '?' in Complexity column of skills index`
      )
    );
  }

  return warnings;
}

/**
 * Validates the impact.performance.rating field of a frontmatter object.
 *
 * @param {Object} frontmatter - The parsed frontmatter object
 * @param {string} relativePath - The relative path for error reporting
 * @returns {ValidationError[]} Array of validation warnings
 */
function validatePerformanceRating(frontmatter, relativePath) {
  const warnings = [];

  if (
    frontmatter.impact == null ||
    frontmatter.impact.performance == null ||
    frontmatter.impact.performance.rating == null
  ) {
    warnings.push(
      new ValidationError(
        relativePath,
        "impact.performance.rating",
        `Missing 'impact.performance.rating' - will show '?' in Performance column of skills index`
      )
    );
  } else if (frontmatter.impact.performance.rating === "") {
    warnings.push(
      new ValidationError(
        relativePath,
        "impact.performance.rating",
        `Empty 'impact.performance.rating' - will show '?' in Performance column of skills index`
      )
    );
  }

  return warnings;
}

/**
 * Parse YAML frontmatter from markdown file content.
 * Uses a stack-based approach to handle arbitrary nesting depth.
 * Returns null if no valid frontmatter found.
 */
function parseFrontmatter(content) {
  const normalizedContent = normalizeToLf(content);
  const match = normalizedContent.match(/^---\n([\s\S]*?)\n---/);
  if (!match) {
    return null;
  }

  const yaml = match[1];
  const result = {};
  const lines = yaml.split("\n");

  // Stack-based parser for arbitrary nesting depth
  // Each stack entry: { obj, key, indent, isArray }
  const contextStack = [{ obj: result, key: null, indent: -1, isArray: false }];

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    // Skip empty lines and comments
    if (!line.trim() || line.trim().startsWith("#")) {
      continue;
    }

    const trimmed = line.trim();
    const indent = line.search(/\S/);

    // Pop stack until we find the right context level
    while (contextStack.length > 1 && contextStack[contextStack.length - 1].indent >= indent) {
      contextStack.pop();
    }

    const currentContext = contextStack[contextStack.length - 1];

    // Handle array items
    if (trimmed.startsWith("- ")) {
      const arrayValue = trimmed.slice(2).trim();

      // Check if this is an object item (has nested content or is "key: value")
      const colonIndex = arrayValue.indexOf(":");
      if (colonIndex > 0 && !arrayValue.startsWith('"') && !arrayValue.startsWith("'")) {
        // Array of objects - parse as key: value
        const itemKey = arrayValue.slice(0, colonIndex).trim();
        let itemValue = stripMatchingBoundaryQuotes(arrayValue.slice(colonIndex + 1).trim());

        // Check if we need to create a new object in the array
        if (currentContext.isArray) {
          // Create new object and add to array
          const newObj = {};
          newObj[itemKey] = itemValue;
          currentContext.obj.push(newObj);
          // Push this object onto the stack for potential nested properties
          contextStack.push({ obj: newObj, key: itemKey, indent: indent + 2, isArray: false });
        }
      } else {
        // Simple array value
        const value = stripMatchingBoundaryQuotes(arrayValue);
        if (currentContext.isArray) {
          currentContext.obj.push(value);
        }
      }
      continue;
    }

    // Handle key: value pairs
    const colonIndex = trimmed.indexOf(":");
    if (colonIndex === -1) {
      continue;
    }

    const key = trimmed.slice(0, colonIndex).trim();
    let value = trimmed.slice(colonIndex + 1).trim();

    if (value === "" || value === "[]") {
      // Check if next line starts an array or is nested
      const nextLine = i < lines.length - 1 ? lines[i + 1] : "";
      const nextTrimmed = nextLine.trim();

      if (nextTrimmed.startsWith("- ")) {
        // This key starts an array
        currentContext.obj[key] = [];
        contextStack.push({
          obj: currentContext.obj[key],
          key: key,
          indent: indent,
          isArray: true
        });
      } else if (nextLine && nextLine.search(/\S/) > indent) {
        // This key starts a nested object
        currentContext.obj[key] = {};
        contextStack.push({
          obj: currentContext.obj[key],
          key: key,
          indent: indent,
          isArray: false
        });
      } else {
        // Empty value
        currentContext.obj[key] = value === "[]" ? [] : "";
      }
    } else {
      // Simple key: value
      value = stripMatchingBoundaryQuotes(value);
      currentContext.obj[key] = value;
    }
  }

  return result;
}

/**
 * Find all skill files recursively.
 */
function findSkillFiles(dir) {
  const skills = [];

  if (!fs.existsSync(dir)) {
    return skills;
  }

  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (error) {
    console.warn(`Warning: Unable to read directory ${dir}: ${error.message}`);
    return skills;
  }

  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);

    if (entry.isDirectory()) {
      if (!EXCLUDED_DIRS.includes(entry.name)) {
        skills.push(...findSkillFiles(fullPath));
      }
    } else if (entry.isFile() && entry.name.endsWith(".md")) {
      if (!EXCLUDED_FILES.includes(entry.name)) {
        const relativePath = path.relative(SKILLS_DIR, fullPath);
        const category = path.dirname(relativePath);

        if (category !== ".") {
          skills.push({
            path: fullPath,
            relativePath: relativePath,
            category: category,
            filename: entry.name,
            expectedId: entry.name.replace(/\.md$/, "")
          });
        }
      }
    }
  }

  return skills;
}

/**
 * Validates that impact is present and is an object (not a primitive or array).
 *
 * @param {Object} frontmatter - The parsed frontmatter object
 * @returns {boolean} True if impact is a valid object to iterate over
 */
function isValidImpactObject(frontmatter) {
  return (
    frontmatter.impact != null &&
    typeof frontmatter.impact === "object" &&
    !Array.isArray(frontmatter.impact)
  );
}

/**
 * Validates all required fields of a frontmatter object.
 *
 * @param {Object} frontmatter - The parsed frontmatter object
 * @param {string} relativePath - The relative path for error reporting
 * @returns {ValidationError[]} Array of validation errors
 */
function validateRequiredFields(frontmatter, relativePath) {
  const errors = [];

  for (const field of REQUIRED_FIELDS) {
    errors.push(...validateRequiredField(frontmatter, field, relativePath));
  }

  return errors;
}

/**
 * Validate a single skill file.
 * Returns validation results with errors, warnings, and line count.
 */
function validateSkill(skillFile) {
  const errors = [];
  const warnings = [];

  let content;
  try {
    content = fs.readFileSync(skillFile.path, "utf8");
  } catch (error) {
    errors.push(
      new ValidationError(skillFile.relativePath, "file", `Cannot read file: ${error.message}`)
    );
    return { errors, warnings, lineCount: 0 };
  }

  const lineCount = normalizeToLf(content).split("\n").length;
  const frontmatter = parseFrontmatter(content);

  // Store line count for reporting
  skillFile.lineCount = lineCount;

  if (!frontmatter) {
    errors.push(
      new ValidationError(
        skillFile.relativePath,
        "frontmatter",
        "Missing or invalid YAML frontmatter"
      )
    );
    return { errors, warnings, lineCount };
  }

  // Check required fields
  errors.push(...validateRequiredFields(frontmatter, skillFile.relativePath));

  // Validate id matches filename
  if (frontmatter.id != null && frontmatter.id !== "" && frontmatter.id !== skillFile.expectedId) {
    errors.push(
      new ValidationError(
        skillFile.relativePath,
        "id",
        `ID '${frontmatter.id}' does not match filename '${skillFile.expectedId}'`
      )
    );
  }

  // Validate category matches folder
  if (
    frontmatter.category != null &&
    frontmatter.category !== "" &&
    frontmatter.category !== skillFile.category
  ) {
    errors.push(
      new ValidationError(
        skillFile.relativePath,
        "category",
        `Category '${frontmatter.category}' does not match folder '${skillFile.category}'`
      )
    );
  }

  // Validate category is known
  if (
    frontmatter.category != null &&
    frontmatter.category !== "" &&
    !VALID_CATEGORIES.includes(frontmatter.category)
  ) {
    warnings.push(
      new ValidationError(
        skillFile.relativePath,
        "category",
        `Unknown category '${frontmatter.category}'. Valid: ${VALID_CATEGORIES.join(", ")}`
      )
    );
  }

  // Validate status
  if (
    frontmatter.status != null &&
    frontmatter.status !== "" &&
    !VALID_STATUSES.includes(frontmatter.status)
  ) {
    errors.push(
      new ValidationError(
        skillFile.relativePath,
        "status",
        `Invalid status '${frontmatter.status}'. Valid: ${VALID_STATUSES.join(", ")}`
      )
    );
  }

  // Validate complexity level
  if (
    frontmatter.complexity != null &&
    frontmatter.complexity.level != null &&
    frontmatter.complexity.level !== "" &&
    !VALID_COMPLEXITY_LEVELS.includes(frontmatter.complexity.level)
  ) {
    warnings.push(
      new ValidationError(
        skillFile.relativePath,
        "complexity.level",
        `Invalid complexity level '${frontmatter.complexity.level}'. Valid: ${VALID_COMPLEXITY_LEVELS.join(", ")}`
      )
    );
  }

  // Validate impact ratings
  if (isValidImpactObject(frontmatter)) {
    // Warn about unknown impact types
    for (const impactType of Object.keys(frontmatter.impact)) {
      if (!VALID_IMPACT_TYPES.includes(impactType)) {
        warnings.push(
          new ValidationError(
            skillFile.relativePath,
            `impact.${impactType}`,
            `Unknown impact type '${impactType}'. Valid: ${VALID_IMPACT_TYPES.join(", ")}`
          )
        );
      }
    }
    // Validate ratings for known impact types
    for (const impactType of VALID_IMPACT_TYPES) {
      if (
        frontmatter.impact[impactType] != null &&
        frontmatter.impact[impactType].rating != null &&
        frontmatter.impact[impactType].rating !== "" &&
        !VALID_IMPACT_RATINGS.includes(frontmatter.impact[impactType].rating)
      ) {
        warnings.push(
          new ValidationError(
            skillFile.relativePath,
            `impact.${impactType}.rating`,
            `Invalid rating '${frontmatter.impact[impactType].rating}'. Valid: ${VALID_IMPACT_RATINGS.join(", ")}`
          )
        );
      }
    }
  }

  // Validate version format (semver-like)
  if (frontmatter.version != null && frontmatter.version !== "") {
    // Coerce to string to handle numeric or other non-string types
    const versionStr = String(frontmatter.version);
    if (!versionStr.match(/^\d+\.\d+\.\d+$/)) {
      warnings.push(
        new ValidationError(
          skillFile.relativePath,
          "version",
          `Version '${versionStr}' should be in semver format (e.g., 1.0.0)`
        )
      );
    }
  }

  // Warn about missing optional fields that affect skills index display
  // These are not required but cause '?' placeholders in the generated index
  warnings.push(...validateComplexityLevel(frontmatter, skillFile.relativePath));
  warnings.push(...validatePerformanceRating(frontmatter, skillFile.relativePath));
  warnings.push(...validateTags(frontmatter, skillFile.relativePath));

  // Validate date format
  for (const dateField of ["created", "updated"]) {
    if (frontmatter[dateField] != null && frontmatter[dateField] !== "") {
      // Coerce to string to handle numeric or other non-string types
      const dateStr = String(frontmatter[dateField]);
      if (!dateStr.match(/^\d{4}-\d{2}-\d{2}$/)) {
        warnings.push(
          new ValidationError(
            skillFile.relativePath,
            dateField,
            `Date '${dateStr}' should be in ISO format (YYYY-MM-DD)`
          )
        );
      }
    }
  }

  // Check for required sections in body
  const requiredSections = ["## Overview", "## Solution"];
  for (const section of requiredSections) {
    if (!content.includes(section)) {
      warnings.push(
        new ValidationError(
          skillFile.relativePath,
          "body",
          `Missing recommended section: '${section}'`
        )
      );
    }
  }

  return { errors, warnings, lineCount };
}

/**
 * Recursively find all markdown files in .llm/.
 */
function findAllLlmMarkdownFiles(dir, rootDir = dir) {
  const markdownFiles = [];

  if (!fs.existsSync(dir)) {
    return markdownFiles;
  }

  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (error) {
    console.warn(`Warning: Unable to read directory ${dir}: ${error.message}`);
    return markdownFiles;
  }
  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);

    if (entry.isDirectory()) {
      markdownFiles.push(...findAllLlmMarkdownFiles(fullPath, rootDir));
      continue;
    }

    if (entry.isFile() && entry.name.endsWith(".md")) {
      markdownFiles.push({
        path: fullPath,
        relativePath: path.relative(rootDir, fullPath).replace(/\\/g, "/")
      });
    }
  }

  return markdownFiles;
}

/**
 * Find duplicate `## See Also` headings in a single file.
 *
 * @param {string} content - Markdown content
 * @param {string} relativePath - File path for diagnostics
 * @returns {ValidationError[]} Array of duplicate-heading errors
 */
function validateDuplicateSeeAlsoHeadings(content, relativePath) {
  const errors = [];
  const { seeAlsoOutsideFenceLines } = analyzeMarkdownFenceState(content);
  const headingLines = seeAlsoOutsideFenceLines;

  for (let i = 1; i < headingLines.length; i++) {
    errors.push(
      new ValidationError(
        relativePath,
        "headings",
        `Line ${headingLines[i]}: Duplicate '## See Also' heading (first seen at line ${headingLines[0]}). Use a distinct heading for additional link sections.`
      )
    );
  }

  return errors;
}

/**
 * Track fenced-code state and See Also heading placement for markdown diagnostics.
 *
 * @param {string} content - Markdown content
 * @returns {{seeAlsoOutsideFenceLines:number[], seeAlsoInsideFenceLines:number[], unclosedFenceStartLine:number|null}}
 */
function analyzeMarkdownFenceState(content) {
  const lines = normalizeToLf(content).split("\n");
  const seeAlsoOutsideFenceLines = [];
  const seeAlsoInsideFenceLines = [];

  let activeFenceMarker = null;
  let activeFenceLength = 0;
  let activeFenceStartLine = null;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    if (line.trim() === "## See Also") {
      if (activeFenceMarker === null) {
        seeAlsoOutsideFenceLines.push(i + 1);
      } else {
        seeAlsoInsideFenceLines.push(i + 1);
      }
    }

    if (activeFenceMarker === null) {
      const openMatch = line.match(/^\s{0,3}(`{3,}|~{3,})(.*)$/);
      if (!openMatch) {
        continue;
      }

      activeFenceMarker = openMatch[1][0];
      activeFenceLength = openMatch[1].length;
      activeFenceStartLine = i + 1;
      continue;
    }

    const closeRegex =
      activeFenceMarker === "`"
        ? new RegExp(`^\\s{0,3}\`{${activeFenceLength},}\\s*$`)
        : new RegExp(`^\\s{0,3}~{${activeFenceLength},}\\s*$`);

    if (closeRegex.test(line)) {
      activeFenceMarker = null;
      activeFenceLength = 0;
      activeFenceStartLine = null;
    }
  }

  return {
    seeAlsoOutsideFenceLines,
    seeAlsoInsideFenceLines,
    unclosedFenceStartLine: activeFenceStartLine
  };
}

/**
 * Detect unclosed fenced code blocks that swallow following markdown sections.
 *
 * @param {string} content - Markdown content
 * @param {string} relativePath - File path for diagnostics
 * @returns {ValidationError[]} Array of fence-balance errors
 */
function validateBalancedMarkdownFences(content, relativePath) {
  const errors = [];
  const { unclosedFenceStartLine } = analyzeMarkdownFenceState(content);

  if (unclosedFenceStartLine !== null) {
    errors.push(
      new ValidationError(
        relativePath,
        "markdown",
        `Line ${unclosedFenceStartLine}: Unclosed fenced code block. This can cause later headings to render as code.`
      )
    );
  }

  return errors;
}

/**
 * Detect See Also headings accidentally placed inside code fences with no real section heading.
 *
 * @param {string} content - Markdown content
 * @param {string} relativePath - File path for diagnostics
 * @returns {ValidationError[]} Array of heading-placement errors
 */
function validateSeeAlsoHeadingPlacement(content, relativePath) {
  const errors = [];
  const { seeAlsoInsideFenceLines, seeAlsoOutsideFenceLines, unclosedFenceStartLine } =
    analyzeMarkdownFenceState(content);

  if (
    unclosedFenceStartLine !== null &&
    seeAlsoInsideFenceLines.length > 0 &&
    seeAlsoOutsideFenceLines.length === 0
  ) {
    errors.push(
      new ValidationError(
        relativePath,
        "headings",
        `Line ${seeAlsoInsideFenceLines[0]}: '## See Also' appears only inside a code fence. Add a real section heading outside fenced blocks.`
      )
    );
  }

  return errors;
}

/**
 * Validate markdown line limits and context/index linkage for all .llm markdown files.
 */
function validateAllLlmMarkdownFiles(baseDir = LLM_DIR) {
  const errors = [];
  const warnings = [];
  const fileSizeReport = [];

  const markdownFiles = findAllLlmMarkdownFiles(baseDir);
  for (const markdownFile of markdownFiles) {
    let content;
    try {
      content = fs.readFileSync(markdownFile.path, "utf8");
    } catch (error) {
      errors.push(
        new ValidationError(markdownFile.relativePath, "file", `Cannot read file: ${error.message}`)
      );
      continue;
    }

    const lineCount = normalizeToLf(content).split("\n").length;
    fileSizeReport.push({ file: markdownFile.relativePath, lines: lineCount });

    if (lineCount > LINE_LIMIT_HARD_MAX) {
      errors.push(
        new ValidationError(
          markdownFile.relativePath,
          "size",
          `File has ${lineCount} lines (max: ${LINE_LIMIT_HARD_MAX}). Split into focused companion files.`
        )
      );
    } else if (lineCount > LINE_LIMIT_IDEAL_MAX) {
      warnings.push(
        new ValidationError(
          markdownFile.relativePath,
          "size",
          `File has ${lineCount} lines (ideal: ${LINE_LIMIT_IDEAL_MIN}-${LINE_LIMIT_IDEAL_MAX}). Consider splitting for tighter context.`
        )
      );
    }

    if (
      markdownFile.relativePath === "context.md" &&
      !content.includes(CONTEXT_INDEX_LINK_FRAGMENT)
    ) {
      errors.push(
        new ValidationError(
          markdownFile.relativePath,
          "links",
          "context.md must link to ./skills/index.md so the generated index is discoverable."
        )
      );
    }

    errors.push(...validateDuplicateSeeAlsoHeadings(content, markdownFile.relativePath));
    errors.push(...validateBalancedMarkdownFences(content, markdownFile.relativePath));

    if (
      markdownFile.relativePath.startsWith("skills/") &&
      !markdownFile.relativePath.includes("/templates/")
    ) {
      errors.push(...validateSeeAlsoHeadingPlacement(content, markdownFile.relativePath));
    }
  }

  return { errors, warnings, fileSizeReport };
}

/**
 * Validate context.md file for line count.
 * Returns validation results with errors, warnings, and line count.
 */
function validateContextFile() {
  const errors = [];
  const warnings = [];

  if (!fs.existsSync(CONTEXT_FILE)) {
    return { errors, warnings, lineCount: 0 };
  }

  let content;
  try {
    content = fs.readFileSync(CONTEXT_FILE, "utf8");
  } catch (error) {
    errors.push(new ValidationError("context.md", "file", `Cannot read file: ${error.message}`));
    return { errors, warnings, lineCount: 0 };
  }

  const lineCount = normalizeToLf(content).split("\n").length;

  return { errors, warnings, lineCount };
}

/**
 * Main entry point.
 */
function main() {
  console.log("Validating skills in", SKILLS_DIR);
  console.log();

  const llmFilesResult = validateAllLlmMarkdownFiles();
  let totalErrors = llmFilesResult.errors.length;
  let totalWarnings = llmFilesResult.warnings.length;
  const fileSizeReport = llmFilesResult.fileSizeReport.slice();

  if (llmFilesResult.errors.length > 0 || llmFilesResult.warnings.length > 0) {
    console.log(`${logSymbol("folder")} .llm markdown policy checks`);
    for (const error of llmFilesResult.errors) {
      console.log(`  ${logSymbol("error")} [${error.file}] ${error.field}: ${error.message}`);
    }
    for (const warning of llmFilesResult.warnings) {
      console.log(`  ${logSymbol("warn")} [${warning.file}] ${warning.field}: ${warning.message}`);
    }
    console.log();
  }

  const skillFiles = findSkillFiles(SKILLS_DIR);
  console.log(`Found ${skillFiles.length} skill files to validate`);
  console.log();

  // Validate context.md
  const contextResult = validateContextFile();
  for (const error of contextResult.errors) {
    console.log(`${logSymbol("doc")} context.md`);
    console.log(`  ${logSymbol("error")} ${error.field}: ${error.message}`);
    console.log();
  }
  for (const warning of contextResult.warnings) {
    console.log(`${logSymbol("doc")} context.md`);
    console.log(`  ${logSymbol("warn")} ${warning.field}: ${warning.message}`);
    console.log();
  }
  totalErrors += contextResult.errors.length;
  totalWarnings += contextResult.warnings.length;

  // Validate skill files
  for (const skillFile of skillFiles) {
    const { errors, warnings, lineCount } = validateSkill(skillFile);

    if (errors.length > 0 || warnings.length > 0) {
      console.log(`${logSymbol("doc")} ${skillFile.relativePath}`);

      for (const error of errors) {
        console.log(`  ${logSymbol("error")} ${error.field}: ${error.message}`);
      }

      for (const warning of warnings) {
        console.log(`  ${logSymbol("warn")} ${warning.field}: ${warning.message}`);
      }

      console.log();
    }

    totalErrors += errors.length;
    totalWarnings += warnings.length;
  }

  // Print file size summary
  console.log("---");
  console.log(`${logSymbol("size")} File Size Report:`);
  console.log();

  // Sort by line count descending
  fileSizeReport.sort((a, b) => b.lines - a.lines);

  const maxFileLen = Math.max(...fileSizeReport.map((f) => f.file.length));
  for (const { file, lines } of fileSizeReport) {
    let indicator = logSymbol("ok");
    if (lines > LINE_LIMIT_HARD_MAX) {
      indicator = logSymbol("error");
    } else if (lines > LINE_LIMIT_IDEAL_MAX) {
      indicator = logSymbol("warn");
    } else if (lines < LINE_LIMIT_IDEAL_MIN && !SHORT_FILE_EXCLUDES.includes(file)) {
      indicator = logSymbol("draft");
    }
    console.log(`  ${indicator} ${file.padEnd(maxFileLen)} : ${lines} lines`);
  }

  console.log();
  console.log(
    `  Legend: ${logSymbol("ok")} Ideal (${LINE_LIMIT_IDEAL_MIN}-${LINE_LIMIT_IDEAL_MAX}) | ` +
      `${logSymbol("warn")} Warning (${LINE_LIMIT_IDEAL_MAX + 1}-${LINE_LIMIT_HARD_MAX}) | ` +
      `${logSymbol("error")} Error (>${LINE_LIMIT_HARD_MAX}) | ` +
      `${logSymbol("draft")} Short (<${LINE_LIMIT_IDEAL_MIN})`
  );

  console.log();
  console.log("---");
  console.log(`Validation complete: ${totalErrors} errors, ${totalWarnings} warnings`);

  if (totalErrors > 0) {
    console.log(`\n${logSymbol("error")} Validation failed`);
    return 1;
  }

  if (totalWarnings > 0) {
    console.log(`\n${logSymbol("warn")} Validation passed with warnings`);
  } else {
    console.log(`\n${logSymbol("ok")} All skills valid`);
  }

  return 0;
}

/**
 * @module validate-skills
 * @description Validates skill files in .llm/skills/ for frontmatter correctness, naming conventions,
 * size limits, and cross-references. Used by pre-commit hooks and CI pipelines.
 *
 * @exports {Function} validateSkill - Validates a single skill file, returning errors and warnings
 * @exports {Function} parseFrontmatter - Extracts YAML frontmatter from markdown content
 * @exports {Function} validateRequiredField - Validates a single required frontmatter field
 * @exports {Function} validateRequiredFields - Validates all required frontmatter fields
 * @exports {Function} validateTags - Validates the tags array in frontmatter
 * @exports {Function} validateComplexityLevel - Validates the complexity.level field
 * @exports {Function} validatePerformanceRating - Validates presence of impact.performance.rating field
 * @exports {Function} isValidImpactObject - Checks if impact field is a non-null object (excludes arrays)
 * @exports {Class} ValidationError - Error class with file, field, and message properties
 * @exports {Array<string>} REQUIRED_FIELDS - List of required frontmatter field names
 * @exports {Array<string>} VALID_COMPLEXITY_LEVELS - Valid values: 'basic', 'intermediate', 'advanced', 'expert'
 * @exports {Array<string>} VALID_IMPACT_RATINGS - Valid rating values: 'none', 'low', 'medium', 'high', 'critical'
 */
if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    validateSkill,
    parseFrontmatter,
    ValidationError,
    REQUIRED_FIELDS,
    VALID_COMPLEXITY_LEVELS,
    VALID_IMPACT_RATINGS,
    validateRequiredField,
    validateRequiredFields,
    validateTags,
    validateComplexityLevel,
    validatePerformanceRating,
    isValidImpactObject,
    findAllLlmMarkdownFiles,
    validateAllLlmMarkdownFiles,
    validateDuplicateSeeAlsoHeadings,
    analyzeMarkdownFenceState,
    validateBalancedMarkdownFences,
    validateSeeAlsoHeadingPlacement,
    shouldUseAsciiOutput,
    logSymbol,
    LINE_LIMIT_HARD_MAX,
    LINE_LIMIT_IDEAL_MAX,
    LINE_LIMIT_IDEAL_MIN,
    CONTEXT_INDEX_LINK_FRAGMENT
  };
}

// Only run main when executed directly (not when required as a module)
if (require.main === module) {
  process.exit(main());
}
