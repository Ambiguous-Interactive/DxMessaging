#!/usr/bin/env node
"use strict";

const TARGETED_STEP_NAME = "Run cross-platform spawn + host-env hermeticity regression suite";

// A repo-relative POSIX path ending in `.test.js`. Linear/literal-class only.
const TEST_PATH_TOKEN = /^scripts\/[\w./-]*\.test\.js$/;

/**
 * Extract the body lines of a named workflow step's block-scalar `run:` body.
 *
 * @param {string} rawWorkflow LF-normalized workflow source.
 * @param {string} stepName Workflow step name.
 * @returns {string|null} Run block text, or null when the step/run body is absent.
 */
function extractTargetedStepRunBlock(rawWorkflow, stepName = TARGETED_STEP_NAME) {
  const lines = String(rawWorkflow || "").split("\n");

  let nameLineIndex = -1;
  for (let i = 0; i < lines.length; i++) {
    const match = /^\s*-\s+name:\s*(.+?)\s*$/.exec(lines[i]);
    if (match && match[1].replace(/^["']|["']$/g, "") === stepName) {
      nameLineIndex = i;
      break;
    }
  }
  if (nameLineIndex === -1) {
    return null;
  }

  const nameIndent = lines[nameLineIndex].length - lines[nameLineIndex].trimStart().length;
  let stepEnd = lines.length - 1;
  for (let i = nameLineIndex + 1; i < lines.length; i++) {
    const trimmed = lines[i].trim();
    if (trimmed.length === 0) {
      continue;
    }
    const indent = lines[i].length - lines[i].trimStart().length;
    if (indent <= nameIndent && /^-\s+/.test(trimmed)) {
      stepEnd = i - 1;
      break;
    }
    if (indent < nameIndent) {
      stepEnd = i - 1;
      break;
    }
  }

  for (let i = nameLineIndex + 1; i <= stepEnd; i++) {
    const runMatch = /^(\s*)run:\s*[|>][+-]?\s*$/.exec(lines[i]);
    if (!runMatch) {
      continue;
    }

    const runIndent = runMatch[1].length;
    const body = [];
    for (let j = i + 1; j <= stepEnd; j++) {
      const bodyLine = lines[j];
      if (bodyLine.trim().length === 0) {
        body.push("");
        continue;
      }
      const bodyIndent = bodyLine.length - bodyLine.trimStart().length;
      if (bodyIndent <= runIndent) {
        break;
      }
      body.push(bodyLine);
    }
    return body.join("\n");
  }

  return null;
}

function scanTestPathTokens(lines) {
  const seen = new Set();
  const paths = [];
  for (const line of lines) {
    for (const token of String(line || "").split(/\s+/)) {
      const normalized = token.replace(/^["']|["',;)]$/g, "");
      if (TEST_PATH_TOKEN.test(normalized) && !seen.has(normalized)) {
        seen.add(normalized);
        paths.push(normalized);
      }
    }
  }
  return paths;
}

function stripPowerShellTrailingComment(line) {
  const text = String(line || "");
  let quote = "";
  let escaped = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (escaped) {
      escaped = false;
      continue;
    }
    if (quote) {
      if (ch === "`") {
        escaped = true;
        continue;
      }
      if (ch === quote) {
        quote = "";
      }
      continue;
    }
    if (ch === '"' || ch === "'") {
      quote = ch;
      continue;
    }
    if (ch === "#") {
      return text.slice(0, i);
    }
  }
  return text;
}

function extractPowerShellQuotedPathTokens(text) {
  const paths = [];
  const source = stripPowerShellTrailingComment(text);
  let quote = "";
  let value = "";
  let escaped = false;

  for (let i = 0; i < source.length; i++) {
    const ch = source[i];
    if (!quote) {
      if (ch === '"' || ch === "'") {
        quote = ch;
        value = "";
        escaped = false;
      }
      continue;
    }

    if (escaped) {
      value += ch;
      escaped = false;
      continue;
    }
    if (quote === '"' && ch === "`") {
      escaped = true;
      continue;
    }
    if (ch === quote) {
      if (TEST_PATH_TOKEN.test(value)) {
        paths.push(value);
      }
      quote = "";
      value = "";
      continue;
    }
    value += ch;
  }

  return paths;
}

function findClosingPowerShellArrayLine(lines, startIndex) {
  for (let i = startIndex; i < lines.length; i++) {
    if (/^\s*\)\s*(?:#.*)?$/.test(lines[i])) {
      return i;
    }
  }
  return -1;
}

function extractPowerShellArrayVariables(lines) {
  const arrays = new Map();

  for (let i = 0; i < lines.length; i++) {
    const line = stripPowerShellTrailingComment(lines[i]);
    const start = /^\s*\$([A-Za-z_][A-Za-z0-9_]*)\s*=\s*@\((.*)$/.exec(line);
    if (!start) {
      continue;
    }

    const name = start[1];
    const values = [];
    const firstTail = start[2];
    const firstClose = firstTail.indexOf(")");
    if (firstClose !== -1) {
      values.push(...extractPowerShellQuotedPathTokens(firstTail.slice(0, firstClose)));
      arrays.set(name, values);
      continue;
    }

    const closeLine = findClosingPowerShellArrayLine(lines, i + 1);
    const end = closeLine === -1 ? lines.length - 1 : closeLine;
    values.push(...extractPowerShellQuotedPathTokens(firstTail));
    for (let j = i + 1; j < end; j++) {
      values.push(...extractPowerShellQuotedPathTokens(lines[j]));
    }
    arrays.set(name, values);
    if (closeLine !== -1) {
      i = closeLine;
    }
  }

  return arrays;
}

function extractPowerShellSplatNames(line) {
  const names = [];
  const re = /(?:^|[\s,])@([A-Za-z_][A-Za-z0-9_]*)(?=$|[\s,])/g;
  let match;
  while ((match = re.exec(line)) !== null) {
    names.push(match[1]);
  }
  return names;
}

function classifyBashContinuation(line) {
  const body = String(line || "").endsWith("\r")
    ? String(line || "").slice(0, -1)
    : String(line || "");
  if (body.endsWith("\\")) {
    const withoutSlash = body.slice(0, -1);
    const content = withoutSlash.endsWith(" ") ? withoutSlash.slice(0, -1) : withoutSlash;
    return { content, continues: true };
  }

  let end = body.length;
  while (end > 0) {
    const ch = body[end - 1];
    if (ch === " " || ch === "\t" || ch === "\f" || ch === "\v" || ch === "\r") {
      end--;
    } else {
      break;
    }
  }
  const trimmedRight = body.slice(0, end);
  if (trimmedRight.endsWith("\\") && trimmedRight.length < body.length) {
    return { malformed: true };
  }
  return { content: body, continues: false };
}

/**
 * Parse `--runTestsByPath` targets from the targeted workflow step.
 *
 * Supported forms:
 * - Bash-style `--runTestsByPath \` followed by backslash-continued paths.
 * - PowerShell array+splat, e.g. `$tests = @("a.test.js")` then
 *   `--runTestsByPath @tests`.
 *
 * @param {string} runBlock Workflow run block text.
 * @returns {string[]} Ordered, deduped repo-relative test paths.
 */
function extractListedTestPaths(runBlock) {
  if (typeof runBlock !== "string" || runBlock.length === 0) {
    return [];
  }

  const lines = runBlock.split("\n");

  let startIndex = -1;
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].includes("--runTestsByPath")) {
      startIndex = i;
      break;
    }
  }
  if (startIndex === -1) {
    return scanTestPathTokens(lines);
  }
  const powerShellArrays = extractPowerShellArrayVariables(lines.slice(0, startIndex + 1));

  const seen = new Set();
  const paths = [];
  const collect = (items) => {
    for (const item of items) {
      if (TEST_PATH_TOKEN.test(item) && !seen.has(item)) {
        seen.add(item);
        paths.push(item);
      }
    }
  };
  const collectFrom = (content) => collect(scanTestPathTokens([content]));

  const startClassified = classifyBashContinuation(lines[startIndex]);
  if (startClassified.malformed) {
    throw new Error(
      "Malformed line continuation (trailing space after `\\`) in the targeted " +
        "regression step of .github/workflows/cross-platform-preflight.yml on the " +
        `--runTestsByPath line:\n  ${lines[startIndex]}`
    );
  }

  collectFrom(startClassified.content);
  const splatNames = extractPowerShellSplatNames(startClassified.content);
  for (const name of splatNames) {
    if (!powerShellArrays.has(name)) {
      throw new Error(
        "Unresolved PowerShell array splat in the targeted regression step of " +
          `.github/workflows/cross-platform-preflight.yml: --runTestsByPath @${name} ` +
          `has no preceding $${name} = @(...) assignment.`
      );
    }
    collect(powerShellArrays.get(name));
  }
  if (splatNames.length > 0 && paths.length === 0) {
    throw new Error(
      "Empty PowerShell test array in the targeted regression step of " +
        `.github/workflows/cross-platform-preflight.yml: --runTestsByPath ` +
        `${splatNames.map((name) => `@${name}`).join(", ")} resolved no .test.js paths.`
    );
  }

  let endIndex = startIndex;
  let continuing = startClassified.continues;

  while (continuing && endIndex + 1 < lines.length) {
    const i = endIndex + 1;
    const line = lines[i];
    const classified = classifyBashContinuation(line);
    if (classified.malformed) {
      throw new Error(
        "Malformed line continuation (trailing space after `\\`) in the targeted " +
          "regression step of .github/workflows/cross-platform-preflight.yml:\n" +
          `  ${line}\n` +
          "A bash line-continuation backslash must be the last character on the line."
      );
    }
    collectFrom(classified.content);
    endIndex = i;
    continuing = classified.continues;
  }

  for (let i = endIndex + 1; i < lines.length; i++) {
    const orphan = scanTestPathTokens([lines[i]])[0];
    if (orphan) {
      throw new Error(
        "Truncated --runTestsByPath list in the targeted regression step of " +
          ".github/workflows/cross-platform-preflight.yml: a path line is missing " +
          "its trailing `\\` continuation, so the shell command terminated early " +
          "and these path token(s) were orphaned:\n" +
          `  ${lines[i].trim()}\n` +
          "Every path line except the last must end in ` \\`."
      );
    }
  }

  return paths;
}

module.exports = {
  TARGETED_STEP_NAME,
  TEST_PATH_TOKEN,
  extractTargetedStepRunBlock,
  extractListedTestPaths
};
