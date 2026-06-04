#!/usr/bin/env node
/**
 * validate-lychee-config.js
 *
 * Validates .lychee.toml configuration against lychee v0.24.2's valid field list.
 * Catches deprecated or misspelled fields before they break CI.
 *
 * @usage
 *   node scripts/validate-lychee-config.js
 *
 * @exitcodes
 *   0 - Success (all fields are valid)
 *   1 - Validation failed (invalid fields found)
 *
 * @example
 *   # Run from repository root
 *   node scripts/validate-lychee-config.js
 *
 *   # Run in CI pipeline
 *   node scripts/validate-lychee-config.js || exit 1
 */

"use strict";

const fs = require("fs");
const path = require("path");
const {
  hasMatchingBoundaryQuotes,
  stripMatchingBoundaryQuotes,
  normalizeToLf
} = require("./lib/quote-parser");

const CONFIG_PATH = path.join(__dirname, "..", ".lychee.toml");

// SYNC: Keep in sync with lychee v0.24.2 valid configuration fields.
// Source: https://github.com/lycheeverse/lychee/blob/lychee-v0.24.2/lychee.example.toml
// SYNC: Tests in validate-lychee-config.test.js VALID_FIELDS describe block reference this constant
const VALID_FIELDS = new Set([
  "accept",
  "accept_timeouts",
  "archive",
  "base_url",
  "basic_auth",
  "cache",
  "cache_exclude_status",
  "cookie_jar",
  "default_extension",
  "dump",
  "dump_inputs",
  "exclude",
  "exclude_all_private",
  "exclude_file",
  "exclude_link_local",
  "exclude_loopback",
  "exclude_path",
  "exclude_private",
  "extensions",
  "fallback_extensions",
  "files_from",
  "format",
  "generate",
  "github_token",
  "glob_ignore_case",
  "header",
  "hidden",
  "host_concurrency",
  "host_request_interval",
  "host_stats",
  "hosts",
  "include",
  "include_fragments",
  "include_mail",
  "include_verbatim",
  "include_wikilinks",
  "index_files",
  "insecure",
  "max_cache_age",
  "max_concurrency",
  "max_redirects",
  "max_retries",
  "method",
  "min_tls",
  "mode",
  "no_ignore",
  "no_progress",
  "offline",
  "output",
  "preprocess",
  "remap",
  "require_https",
  "retry_wait_time",
  "root_dir",
  "scheme",
  "skip_missing",
  "suggest",
  "threads",
  "timeout",
  "user_agent",
  "verbose"
]);

// SYNC: Keep in sync with lychee v0.24.2 valid verbose values.
// Source: https://github.com/lycheeverse/lychee/blob/lychee-v0.24.2/lychee.example.toml
// SYNC: Tests in validate-lychee-config.test.js validateFieldValues describe block reference this constant
const VALID_VERBOSE_VALUES = ["error", "warn", "info", "debug", "trace"];

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Split a TOML dotted path into segments while respecting quoted segments.
 *
 * Examples:
 *   basic_auth.example.com -> ["basic_auth", "example", "com"]
 *   "my.section".key -> ["my.section", "key"]
 *
 * @param {string} pathExpression - TOML key or table expression
 * @returns {string[]} Path segments (quotes stripped)
 */
function splitTomlPath(pathExpression) {
  const segments = [];
  let current = "";
  let quoteChar = null;

  for (let i = 0; i < pathExpression.length; i += 1) {
    const char = pathExpression[i];

    if (quoteChar !== null) {
      // Keep escaped characters inside quoted segments.
      if (char === "\\" && i + 1 < pathExpression.length) {
        current += char + pathExpression[i + 1];
        i += 1;
        continue;
      }

      if (char === quoteChar) {
        quoteChar = null;
        continue;
      }

      current += char;
      continue;
    }

    if (char === '"' || char === "'") {
      quoteChar = char;
      continue;
    }

    if (char === ".") {
      segments.push(current.trim());
      current = "";
      continue;
    }

    current += char;
  }

  segments.push(current.trim());
  return segments.filter((segment) => segment.length > 0);
}

/**
 * Parse TOML table header names from lines like [section] and [[array_section]].
 *
 * @param {string} line - TOML line with comments already stripped
 * @returns {string | null}
 */
function parseTomlTableHeader(line) {
  const arrayTableMatch = line.match(/^\[\[(.+)\]\]$/);
  if (arrayTableMatch) {
    return arrayTableMatch[1].trim();
  }

  const tableMatch = line.match(/^\[(.+)\]$/);
  if (tableMatch) {
    return tableMatch[1].trim();
  }

  return null;
}

/**
 * Strip inline TOML comments while preserving hash characters inside quoted values.
 *
 * @param {string} line - TOML line
 * @returns {string} TOML line without trailing inline comments
 */
function stripInlineTomlComment(line) {
  let quoteChar = null;

  for (let i = 0; i < line.length; i += 1) {
    const char = line[i];

    if (quoteChar !== null) {
      if (char === "\\" && i + 1 < line.length) {
        i += 1;
        continue;
      }

      if (char === quoteChar) {
        quoteChar = null;
      }
      continue;
    }

    if (char === '"' || char === "'") {
      quoteChar = char;
      continue;
    }

    if (char === "#") {
      return line.slice(0, i).trimEnd();
    }
  }

  return line;
}

function bracketDeltaOutsideQuotes(value) {
  let quoteChar = null;
  let delta = 0;

  for (let i = 0; i < value.length; i += 1) {
    const char = value[i];

    if (quoteChar !== null) {
      if (char === "\\" && i + 1 < value.length) {
        i += 1;
        continue;
      }

      if (char === quoteChar) {
        quoteChar = null;
      }
      continue;
    }

    if (char === '"' || char === "'") {
      quoteChar = char;
      continue;
    }

    if (char === "[") {
      delta += 1;
    } else if (char === "]") {
      delta -= 1;
    }
  }

  return delta;
}

function extractTopLevelTomlValue(content, key) {
  const assignmentPattern = new RegExp(`^\\s*${escapeRegExp(key)}\\s*=\\s*(.*)$`);
  const collected = [];
  let bracketDepth = 0;

  for (const line of normalizeToLf(content).split("\n")) {
    const stripped = stripInlineTomlComment(line);
    if (collected.length === 0) {
      const match = stripped.match(assignmentPattern);
      if (!match) {
        continue;
      }

      const value = match[1].trim();
      collected.push(value);
      bracketDepth += bracketDeltaOutsideQuotes(value);
      if (bracketDepth <= 0) {
        return collected.join("\n");
      }
      continue;
    }

    const continuation = stripped.trim();
    collected.push(continuation);
    bracketDepth += bracketDeltaOutsideQuotes(continuation);
    if (bracketDepth <= 0) {
      return collected.join("\n");
    }
  }

  return collected.length > 0 ? collected.join("\n") : null;
}

function splitTomlArrayItems(value) {
  const trimmed = value.trim();
  if (!trimmed.startsWith("[") || !trimmed.endsWith("]")) {
    throw new Error(`expected TOML array value, received: ${value}`);
  }

  const body = trimmed.slice(1, -1);
  const items = [];
  let current = "";
  let quoteChar = null;

  for (let i = 0; i < body.length; i += 1) {
    const char = body[i];
    if (quoteChar !== null) {
      current += char;
      if (char === "\\" && i + 1 < body.length) {
        current += body[i + 1];
        i += 1;
        continue;
      }

      if (char === quoteChar) {
        quoteChar = null;
      }
      continue;
    }

    if (char === '"' || char === "'") {
      quoteChar = char;
      current += char;
      continue;
    }

    if (char === ",") {
      const item = current.trim();
      if (item.length > 0) {
        items.push(item);
      }
      current = "";
      continue;
    }

    current += char;
  }

  const finalItem = current.trim();
  if (finalItem.length > 0) {
    items.push(finalItem);
  }

  return items;
}

function normalizeTomlScalar(value) {
  return hasMatchingBoundaryQuotes(value) ? stripMatchingBoundaryQuotes(value) : value.trim();
}

// Does an `accept` entry cover `status`? Mirrors lychee's range grammar
// `[start]..[[=]end] | code`: a bare code, a bounded range (`200..299` exclusive,
// `200..=299` inclusive), OR an open-ended range (`500..`, `..404`, `..=410`).
// Open-ended forms matter for the policy guard: a contributor who writes
// `accept = ["404.."]` or `["..=599"]` would otherwise silently re-accept 404/410.
function acceptsHttpStatus(item, status) {
  const value = normalizeTomlScalar(item);
  if (/^\d+$/.test(value)) {
    return Number.parseInt(value, 10) === status;
  }

  const range = value.match(/^(\d*)\s*\.\.(=?)\s*(\d*)$/);
  if (range) {
    const [, startRaw, inclusiveEnd, endRaw] = range;
    // Bare `..` (no bounds) is lychee's match-ALL: it accepts every status code,
    // 404 and 410 included. Report it as a match so the policy guard rejects it --
    // an accept-all entry would otherwise silently re-hide the gone statuses.
    if (startRaw === "" && endRaw === "") {
      return true;
    }
    if (startRaw !== "" && status < Number.parseInt(startRaw, 10)) {
      return false;
    }
    if (endRaw === "") {
      return true; // open upper bound; lower bound already satisfied
    }
    const end = Number.parseInt(endRaw, 10);
    return inclusiveEnd === "=" ? status <= end : status < end;
  }

  return false;
}

function parseRequiredArray(content, key, errors) {
  const value = extractTopLevelTomlValue(content, key);
  if (value === null) {
    return [];
  }

  try {
    return splitTomlArrayItems(value);
  } catch (error) {
    errors.push(`Invalid value for '${key}': ${error.message}`);
    return [];
  }
}

/**
 * Parse a TOML file into top-level keys and key-value entries with optional keyPath context.
 *
 * @param {string} content - The TOML file content
 * @returns {{ keys: string[], keyValues: Array<{ key: string, value: string, keyPath?: string }> }}
 */
function parseTomlForLycheeValidation(content) {
  const keys = [];
  const keyValues = [];
  const lines = normalizeToLf(content).split("\n");
  let currentTable = null;
  // Tracks open-bracket depth across lines so that elements of a MULTILINE array
  // are not mistaken for `key = value` assignments. Without this, an element like
  // `"200..=299",` (which contains `=`) is parsed as a bogus top-level key
  // `200..`. Single-line arrays self-close on their own line (delta nets to 0).
  let arrayDepth = 0;

  for (const line of lines) {
    const trimmed = line.trim();
    if (trimmed === "" || trimmed.startsWith("#")) {
      continue;
    }

    const lineWithoutComment = stripInlineTomlComment(trimmed).trim();
    if (lineWithoutComment === "") {
      continue;
    }

    // Inside an open multiline array: this line is an element or the closing
    // bracket, never a key. Update depth and skip.
    if (arrayDepth > 0) {
      arrayDepth = Math.max(0, arrayDepth + bracketDeltaOutsideQuotes(lineWithoutComment));
      continue;
    }

    const tableHeader = parseTomlTableHeader(lineWithoutComment);
    if (tableHeader) {
      currentTable = tableHeader;
      const tableSegments = splitTomlPath(tableHeader);
      if (tableSegments.length > 0) {
        keys.push(tableSegments[0]);
      }
      continue;
    }

    const equalsIndex = lineWithoutComment.indexOf("=");
    if (equalsIndex === -1) {
      continue;
    }

    const rawKey = lineWithoutComment.slice(0, equalsIndex).trim();
    if (rawKey.length === 0) {
      continue;
    }

    const value = lineWithoutComment.slice(equalsIndex + 1).trim();
    // If this assignment opens a multiline array (`key = [` with the closing
    // bracket on a later line), record the depth so subsequent element lines are
    // skipped rather than parsed as keys. A single-line array nets to 0.
    arrayDepth = Math.max(0, arrayDepth + bracketDeltaOutsideQuotes(value));
    const keySegments = splitTomlPath(rawKey);
    if (keySegments.length === 0) {
      continue;
    }

    const parsedKey = keySegments[keySegments.length - 1];

    if (currentTable !== null) {
      keyValues.push({
        key: parsedKey,
        value,
        keyPath: `${currentTable}.${rawKey}`
      });
      continue;
    }

    keys.push(keySegments[0]);
    keyValues.push({ key: parsedKey, value });
  }

  return { keys, keyValues };
}

/**
 * Parse top-level keys from a TOML file.
 * Handles top-level assignments and TOML table headers.
 * Ignores comments and blank lines.
 *
 * For table headers such as [basic_auth] or [[hosts]], the first table segment
 * is treated as the top-level field key.
 *
 * @param {string} content - The TOML file content
 * @returns {string[]} Array of top-level key names
 */
function parseTopLevelKeys(content) {
  return parseTomlForLycheeValidation(content).keys;
}

/**
 * Parse top-level key-value pairs from a TOML file.
 * Returns an array of { key, value } objects where value is the raw string
 * after the equals sign (trimmed).
 *
 * If a key is defined inside a TOML table, the pair includes:
 *   - keyPath: fully-qualified key path (e.g., basic_auth.username)
 *
 * @param {string} content - The TOML file content
 * @returns {{ key: string, value: string, keyPath?: string }[]} Array of key-value pairs
 */
function parseTopLevelKeyValues(content) {
  return parseTomlForLycheeValidation(content).keyValues;
}

/**
 * Validate field values against known constraints.
 * Currently validates:
 *   - verbose: must be one of "error", "warn", "info", "debug", "trace"
 *
 * This function is designed to be extended with additional field validations
 * as lychee evolves.
 *
 * @param {Array<{ key: string, value: string, keyPath?: string }>} keyValues
 *   - Array of key-value pairs from the TOML file, including optional keyPath metadata
 * @returns {{ errors: string[], warnings: string[] }} Validation results
 */
function validateFieldValues(keyValues) {
  const errors = [];
  const warnings = [];

  for (const { key, value, keyPath } of keyValues) {
    if (key === "verbose") {
      // verbose must be a quoted string matching one of the valid log levels
      // Require matching boundary quotes so malformed TOML like '"info' is rejected.
      const isQuotedString = hasMatchingBoundaryQuotes(value);
      const keyDisplay = keyPath || key;

      if (!isQuotedString) {
        errors.push(
          `Invalid value for '${keyDisplay}': ${value} (must be a quoted string, one of: ${VALID_VERBOSE_VALUES.join(", ")})`
        );
        continue;
      }

      const unquoted = stripMatchingBoundaryQuotes(value);
      if (!VALID_VERBOSE_VALUES.includes(unquoted)) {
        errors.push(
          `Invalid value for '${keyDisplay}': ${value} (must be one of: ${VALID_VERBOSE_VALUES.join(", ")})`
        );
      }
    }
  }

  return { errors, warnings };
}

// Status codes whose acceptance would hide a genuinely-dead link (the server
// reports the resource does not exist). These must NEVER appear in `accept`.
const FORBIDDEN_ACCEPT_STATUSES = [404, 410];
// Status codes that MUST be accepted: external sites routinely return these to CI
// bots (WAF / bot detection / rate limiting) even though the page is fine for a
// human. Omitting them reintroduces the per-domain whack-a-mole this policy
// exists to retire (the original CI failure was a 403 from www.w3.org).
const REQUIRED_ACCEPT_STATUSES = [403, 429];
const SHARED_CONFIG_FORBIDDEN_FIELDS = new Set(["accept_timeouts"]);

/**
 * Enforce the repository's link-acceptance policy. External link liveness is
 * non-deterministic, so `accept` is intentionally broad; two invariants keep it
 * from drifting back into fragility:
 *   1. 404 and 410 must NOT be accepted (they mean the link is genuinely gone).
 *   2. 403 and 429 MUST be accepted (bot-detection / rate-limit false positives).
 */
function validateStrictLinkPolicy(content) {
  const errors = [];
  const warnings = [];
  const acceptItems = parseRequiredArray(content, "accept", errors);

  for (const status of FORBIDDEN_ACCEPT_STATUSES) {
    if (acceptItems.some((item) => acceptsHttpStatus(item, status))) {
      errors.push(
        `Invalid value for 'accept': HTTP ${status} must not be accepted -- it means the link is gone, so accepting it hides real breakage.`
      );
    }
  }

  for (const status of REQUIRED_ACCEPT_STATUSES) {
    if (!acceptItems.some((item) => acceptsHttpStatus(item, status))) {
      errors.push(
        `Invalid value for 'accept': HTTP ${status} must be accepted -- external sites return it to CI bots even when the link is valid; omitting it reintroduces flaky failures.`
      );
    }
  }

  return { errors, warnings };
}

/**
 * Enforce repository-specific policy for the shared `.lychee.toml`.
 *
 * Some fields are valid lychee v0.24.2 TOML but still unsafe in this shared
 * config because both the blocking workflow and scheduled advisory scan read it.
 *
 * @param {string[]} keys - Array of top-level key names from the TOML file
 * @returns {{ errors: string[], warnings: string[] }} Validation results
 */
function validateSharedConfigPolicy(keys) {
  const errors = [];
  const warnings = [];
  const reported = new Set();

  for (const key of keys) {
    if (!SHARED_CONFIG_FORBIDDEN_FIELDS.has(key) || reported.has(key)) {
      continue;
    }

    reported.add(key);
    errors.push(
      `Invalid field '${key}': this repository's shared .lychee.toml must not set accept_timeouts; the blocking workflow passes --accept-timeouts=true, while the scheduled advisory scan must report timeouts.`
    );
  }

  return { errors, warnings };
}

/**
 * Validate top-level keys against the known valid fields.
 *
 * @param {string[]} keys - Array of top-level key names from the TOML file
 * @returns {{ errors: string[], warnings: string[] }} Validation results
 */
function validateFields(keys) {
  const errors = [];
  const warnings = [];

  for (const key of keys) {
    if (!VALID_FIELDS.has(key)) {
      errors.push(`Invalid field '${key}': not a valid lychee v0.24.2 configuration option`);
    }
  }

  // Check for duplicate keys
  const seen = new Set();
  for (const key of keys) {
    if (seen.has(key)) {
      warnings.push(`Duplicate field '${key}' found`);
    }
    seen.add(key);
  }

  return { errors, warnings };
}

/**
 * Main entry point.
 */
function main() {
  console.log(`Validating lychee configuration: ${CONFIG_PATH}`);
  console.log();

  if (!fs.existsSync(CONFIG_PATH)) {
    console.log("No .lychee.toml found; skipping validation.");
    return 0;
  }

  let content;
  try {
    content = fs.readFileSync(CONFIG_PATH, "utf8");
  } catch (error) {
    console.error(`Cannot read .lychee.toml: ${error.message}`);
    return 1;
  }

  const keys = parseTopLevelKeys(content);
  console.log(`Found ${keys.length} top-level fields: ${keys.join(", ")}`);
  console.log();

  const { errors, warnings } = validateFields(keys);

  // Phase 2: Validate field values
  const keyValues = parseTopLevelKeyValues(content);
  const valueResult = validateFieldValues(keyValues);
  errors.push(...valueResult.errors);
  warnings.push(...valueResult.warnings);

  const strictLinkPolicyResult = validateStrictLinkPolicy(content);
  errors.push(...strictLinkPolicyResult.errors);
  warnings.push(...strictLinkPolicyResult.warnings);

  const sharedConfigPolicyResult = validateSharedConfigPolicy(keys);
  errors.push(...sharedConfigPolicyResult.errors);
  warnings.push(...sharedConfigPolicyResult.warnings);

  for (const warning of warnings) {
    console.log(`  Warning: ${warning}`);
  }

  for (const error of errors) {
    console.log(`  Error: ${error}`);
  }

  if (errors.length > 0) {
    console.log();
    console.log(`Validation failed: ${errors.length} error(s), ${warnings.length} warning(s)`);
    console.log();
    console.log("Valid lychee v0.24.2 fields:");
    const sortedFields = [...VALID_FIELDS].sort();
    for (const field of sortedFields) {
      console.log(`  - ${field}`);
    }
    return 1;
  }

  if (warnings.length > 0) {
    console.log();
    console.log(`Validation passed with ${warnings.length} warning(s)`);
  } else {
    console.log("All fields are valid lychee v0.24.2 configuration options.");
  }

  return 0;
}

/**
 * @module validate-lychee-config
 * @description Validates .lychee.toml configuration against lychee v0.24.2's valid field list.
 * Used by pre-push hooks and CI pipelines to catch deprecated or misspelled fields.
 *
 * @exports {Function} parseTopLevelKeys - Parses top-level key names from TOML content
 * @exports {Function} parseTopLevelKeyValues - Parses top-level key-value pairs from TOML content
 * @exports {Function} validateFields - Validates field names against the known valid set
 * @exports {Function} validateFieldValues - Validates field values against known constraints
 * @exports {Function} validateStrictLinkPolicy - Validates repository-specific strict link policy
 * @exports {Function} validateSharedConfigPolicy - Validates shared config repository policy
 * @exports {Set<string>} VALID_FIELDS - Set of valid lychee v0.24.2 configuration field names
 * @exports {string[]} VALID_VERBOSE_VALUES - Array of valid verbose log level values
 */
if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    parseTopLevelKeys,
    parseTopLevelKeyValues,
    validateFields,
    validateFieldValues,
    validateStrictLinkPolicy,
    validateSharedConfigPolicy,
    VALID_FIELDS,
    VALID_VERBOSE_VALUES
  };
}

// Only run main when executed directly (not when required as a module)
if (require.main === module) {
  process.exit(main());
}
