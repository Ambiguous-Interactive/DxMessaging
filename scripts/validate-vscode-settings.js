#!/usr/bin/env node
/**
 * validate-vscode-settings.js
 *
 * Guards against committing VS Code chat terminal auto-approval settings.
 * Repository settings must not auto-approve terminal commands.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");
const { normalizeToLf } = require("./lib/quote-parser");

const REPO_ROOT = path.join(__dirname, "..");
const SETTINGS_PATH = path.join(__dirname, "..", ".vscode", "settings.json");
const FORBIDDEN_KEY_PATTERN = /"chat\.tools\.terminal\.[^"]*autoApprove[^"]*"\s*:/i;

class Violation {
  constructor(line, message) {
    this.line = line;
    this.message = message;
  }
}

function toLineNumber(content, index) {
  if (index < 0) {
    return 1;
  }
  const prefix = content.slice(0, index);
  return prefix.split("\n").length;
}

function validateSettingsContent(content) {
  // Normalize line endings so line-number diagnostics are stable across CRLF/LF environments.
  const normalized = normalizeToLf(content);
  const violations = [];

  const keyMatch = FORBIDDEN_KEY_PATTERN.exec(normalized);
  if (keyMatch) {
    violations.push(
      new Violation(
        toLineNumber(normalized, keyMatch.index),
        "Repository settings must not define chat.tools.terminal auto-approval keys."
      )
    );
  }

  return violations;
}

function isGitTracked(settingsPath, repoRoot = REPO_ROOT) {
  const relativePath = path.relative(repoRoot, settingsPath).replace(/\\/g, "/");
  const result = childProcess.spawnSync(
    "git",
    ["ls-files", "--error-unmatch", "--", relativePath],
    {
      cwd: repoRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    }
  );

  return result.status === 0;
}

function validateVscodeSettings(settingsPath = SETTINGS_PATH, options = {}) {
  const requireTracked = Boolean(options.requireTracked);

  if (!fs.existsSync(settingsPath)) {
    return { fileExists: false, tracked: false, violations: [] };
  }

  if (requireTracked && !isGitTracked(settingsPath)) {
    return { fileExists: true, tracked: false, violations: [] };
  }

  const content = fs.readFileSync(settingsPath, "utf8");
  return {
    fileExists: true,
    tracked: true,
    violations: validateSettingsContent(content)
  };
}

function main() {
  const result = validateVscodeSettings(SETTINGS_PATH, { requireTracked: true });

  if (!result.fileExists) {
    console.log("No .vscode/settings.json found; skipping validation.");
    process.exit(0);
  }

  if (!result.tracked) {
    console.log(
      "Found local .vscode/settings.json, but it is not tracked by git; skipping validation."
    );
    process.exit(0);
  }

  if (result.violations.length === 0) {
    console.log("✅ .vscode/settings.json passed security validation.");
    process.exit(0);
  }

  console.error("❌ .vscode/settings.json contains forbidden auto-approval settings:");
  for (const violation of result.violations) {
    console.error(`  - Line ${violation.line}: ${violation.message}`);
  }
  process.exit(1);
}

module.exports = {
  SETTINGS_PATH,
  FORBIDDEN_KEY_PATTERN,
  Violation,
  isGitTracked,
  validateSettingsContent,
  validateVscodeSettings,
  toLineNumber
};

if (require.main === module) {
  main();
}
