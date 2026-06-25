#!/usr/bin/env node
"use strict";

/**
 * Regenerate the `package-version` dropdown in
 * `.github/ISSUE_TEMPLATE/bug_report.yml` (between the AUTO-UPDATED sentinel
 * comments, ending with a literal "Other") so reporters pick a real released
 * version instead of free-typing one.
 *
 *   node scripts/generate-issue-template-versions.js [--check]
 *
 * Run via `npm run update:issue-template-versions`; `--check` gates
 * `validate:all`. Versions = union of `package.json` `version`, `CHANGELOG.md`
 * `## [x.y.z]` headings, and git tags (best-effort, `v` stripped) -- strict
 * `x.y.z` only, deduped, descending. The already-committed options are folded
 * in too, so a shallow CI checkout (no tags) never DROPS a historical version;
 * it only flags a NEW version (always present in package.json/CHANGELOG) that
 * was not regenerated. Hermetic and append-only.
 */

const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");
const { normalizeToLf } = require("./lib/line-endings");

const ROOT_DIR = path.resolve(__dirname, "..");
const TEMPLATE_PATH = path.join(ROOT_DIR, ".github", "ISSUE_TEMPLATE", "bug_report.yml");
const PACKAGE_PATH = path.join(ROOT_DIR, "package.json");
const CHANGELOG_PATH = path.join(ROOT_DIR, "CHANGELOG.md");

const OTHER = "Other";
const STRICT_SEMVER = /^\d+\.\d+\.\d+$/;
const BEGIN_RE = /<!--\s*AUTO-UPDATED:\s*package-versions\b/;
const END_RE = /<!--\s*END\s+AUTO-UPDATED:\s*package-versions\s*-->/;
const OPTION_RE = /^\s*-\s*(\d+\.\d+\.\d+)\s*$/; // a "- x.y.z" option line

/** Descending semver compare (numeric, three components). */
function compareDescending(a, b) {
  const pa = a.split(".").map(Number);
  const pb = b.split(".").map(Number);
  for (let i = 0; i < 3; i += 1) {
    if (pa[i] !== pb[i]) {
      return pb[i] - pa[i];
    }
  }
  return 0;
}

/** Keep only strict `x.y.z`, dedupe, sort descending. */
function normalizeVersions(versions) {
  const seen = new Set();
  for (const raw of versions) {
    const value = String(raw).trim().replace(/^v/, "");
    if (STRICT_SEMVER.test(value)) {
      seen.add(value);
    }
  }
  return [...seen].sort(compareDescending);
}

function parsePackageVersion(packageText) {
  const version = JSON.parse(packageText).version;
  return typeof version === "string" ? [version] : [];
}

function parseChangelogVersions(changelogText) {
  const versions = [];
  const pattern = /^##\s*\[(\d+\.\d+\.\d+)\]/gm;
  let match;
  while ((match = pattern.exec(normalizeToLf(changelogText))) !== null) {
    versions.push(match[1]);
  }
  return versions;
}

/** git tags, best-effort: never throws (a tagless/shallow checkout yields []). */
function readGitTags(cwd = ROOT_DIR) {
  try {
    const output = execFileSync("git", ["tag", "--list"], { cwd, encoding: "utf8" });
    return output.split("\n").filter(Boolean);
  } catch {
    return [];
  }
}

/**
 * Locate the sentinel region. Returns the marker line indices, the indentation
 * to render options at, and the option values already committed inside it.
 * Throws on a missing/out-of-order marker (a structural problem to fix by hand).
 */
function parseRegion(lines) {
  const beginIdx = lines.findIndex((line) => BEGIN_RE.test(line));
  const endIdx = lines.findIndex((line) => END_RE.test(line));
  if (beginIdx === -1 || endIdx === -1) {
    throw new Error("bug_report.yml is missing the AUTO-UPDATED: package-versions sentinel comments");
  }
  if (endIdx <= beginIdx) {
    throw new Error("bug_report.yml package-versions END sentinel precedes its BEGIN sentinel");
  }
  const indent = (lines[beginIdx].match(/^\s*/) || [""])[0];
  const committed = [];
  for (let i = beginIdx + 1; i < endIdx; i += 1) {
    const option = OPTION_RE.exec(lines[i]);
    if (option) {
      committed.push(option[1]);
    }
  }
  return { beginIdx, endIdx, indent, committed };
}

/** The full descending version list the dropdown should carry. */
function computeExpected(sources, committed) {
  return normalizeVersions([...sources, ...committed]);
}

/** Render the option lines (versions then "Other") at the region's indent. */
function renderOptions(versions, indent) {
  return [...versions.map((version) => `${indent}- ${version}`), `${indent}- ${OTHER}`];
}

/**
 * Read disk, compute the expected options, and return everything write/check
 * need. `gitTags` is injectable so tests stay hermetic.
 */
function analyze(options = {}) {
  const templatePath = options.templatePath || TEMPLATE_PATH;
  const packagePath = options.packagePath || PACKAGE_PATH;
  const changelogPath = options.changelogPath || CHANGELOG_PATH;
  const gitTags = options.gitTags || readGitTags();

  const templateText = fs.readFileSync(templatePath, "utf8");
  const eol = templateText.includes("\r\n") ? "\r\n" : "\n";
  const lines = normalizeToLf(templateText).split("\n");

  const region = parseRegion(lines);
  const sources = [
    ...parsePackageVersion(fs.readFileSync(packagePath, "utf8")),
    ...parseChangelogVersions(fs.readFileSync(changelogPath, "utf8")),
    ...gitTags
  ];
  const expected = computeExpected(sources, region.committed);
  const expectedBody = renderOptions(expected, region.indent);
  const currentBody = lines.slice(region.beginIdx + 1, region.endIdx);

  const newLines = [...lines.slice(0, region.beginIdx + 1), ...expectedBody, ...lines.slice(region.endIdx)];
  const newText = newLines.join("\n").replace(/\n/g, eol);

  const drift = [];
  if (currentBody.join("\n") !== expectedBody.join("\n")) {
    const missing = expected.filter((version) => !region.committed.includes(version));
    missing.forEach((version) => drift.push(`missing version option: ${version}`));
    if (drift.length === 0) {
      drift.push("package-version options are out of order or missing the trailing Other fallback");
    }
  }
  return { templateText, newText, drift, expected };
}

/** Env overrides exist only so the CLI exit paths are testable on a fixture. */
function cliOptions() {
  const options = {};
  if (process.env.DX_ISSUE_TEMPLATE) options.templatePath = process.env.DX_ISSUE_TEMPLATE;
  if (process.env.DX_PACKAGE_JSON) options.packagePath = process.env.DX_PACKAGE_JSON;
  if (process.env.DX_CHANGELOG) options.changelogPath = process.env.DX_CHANGELOG;
  if (process.env.DX_GIT_TAGS !== undefined) options.gitTags = process.env.DX_GIT_TAGS.split(",").filter(Boolean);
  return options;
}

function main() {
  const checkMode = process.argv.slice(2).includes("--check");
  const options = cliOptions();
  try {
    const { templateText, newText, drift } = analyze(options);
    if (checkMode) {
      if (drift.length > 0) {
        console.error("ERROR: .github/ISSUE_TEMPLATE/bug_report.yml package-version dropdown is out of date:");
        drift.forEach((entry) => console.error(`  - ${entry}`));
        console.error("Run: npm run update:issue-template-versions");
        process.exit(1);
      }
      console.log("[ok] bug_report.yml package-version dropdown is up to date");
      return;
    }
    if (newText !== templateText) {
      fs.writeFileSync(options.templatePath || TEMPLATE_PATH, newText, "utf8");
      console.log(`[ok] Updated bug_report.yml package-version dropdown (${drift.length} change(s)); run prettier to align.`);
    } else {
      console.log("[ok] bug_report.yml package-version dropdown already up to date");
    }
  } catch (error) {
    console.error("ERROR:", error.message);
    process.exit(1);
  }
}

if (require.main === module) {
  main();
}

module.exports = {
  analyze,
  compareDescending,
  computeExpected,
  normalizeVersions,
  parseChangelogVersions,
  parsePackageVersion,
  parseRegion,
  readGitTags,
  renderOptions
};
