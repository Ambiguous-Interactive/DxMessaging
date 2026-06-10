"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const {
  generateLlmsTxt,
  countSkillFiles,
  getSkillCategories,
  hasValidLastUpdatedLine,
  normalizeForComparison
} = require("../update-llms-txt.js");
const { normalizeToLf } = require("../lib/line-endings.js");

const ROOT_DIR = path.resolve(__dirname, "..", "..");

test("hasValidLastUpdatedLine accepts exactly one ISO-dated line", () => {
  assert.equal(hasValidLastUpdatedLine("intro\n**Last Updated:** 2026-06-10\n"), true);
  assert.equal(hasValidLastUpdatedLine("**Last Updated:** 2026-06-10\r\nend\r\n"), true);
});

test("hasValidLastUpdatedLine rejects missing, duplicate, or malformed lines", () => {
  assert.equal(hasValidLastUpdatedLine("no marker here"), false);
  assert.equal(hasValidLastUpdatedLine("**Last Updated:**\n"), false);
  assert.equal(hasValidLastUpdatedLine("**Last Updated:** June 10, 2026\n"), false);
  assert.equal(
    hasValidLastUpdatedLine("**Last Updated:** 2026-06-10\n**Last Updated:** 2026-06-11\n"),
    false
  );
});

test("normalizeForComparison treats different dates as equal content", () => {
  const a = "# Title\n**Last Updated:** 2024-01-01\n";
  const b = "# Title\r\n**Last Updated:** 2026-06-10\r\n";
  assert.equal(normalizeForComparison(a), normalizeForComparison(b));
});

test("normalizeForComparison still detects structural differences", () => {
  const a = "# Title\n**Last Updated:** 2024-01-01\n";
  const b = "# Other Title\n**Last Updated:** 2024-01-01\n";
  assert.notEqual(normalizeForComparison(a), normalizeForComparison(b));
});

test("generateLlmsTxt embeds package metadata and a valid Last Updated line", () => {
  const pkg = JSON.parse(fs.readFileSync(path.join(ROOT_DIR, "package.json"), "utf8"));
  const content = generateLlmsTxt();
  assert.ok(content.startsWith("# DxMessaging"));
  assert.ok(content.includes(`**Version:** ${pkg.version}`));
  assert.ok(content.includes(`openupm add ${pkg.name}`));
  assert.equal(hasValidLastUpdatedLine(content), true);
});

test("generateLlmsTxt reflects skill counts and categories", () => {
  const content = generateLlmsTxt();
  const skillCount = countSkillFiles();
  const categories = getSkillCategories();
  assert.ok(Number.isInteger(skillCount) && skillCount >= 0);
  assert.ok(content.includes(`${skillCount}+ specialized skill documents`));
  for (const category of categories) {
    assert.ok(content.includes(`- **${category}/**`), `missing category ${category}`);
  }
});

test("--check freshness logic: regeneration is a no-op, edits are detected", () => {
  // The --check mode passes when the on-disk file regenerates identically
  // (modulo the date line and line endings) and fails on any structural edit.
  const generated = generateLlmsTxt();
  const onDisk = normalizeToLf(generated).replace(
    /^\*\*Last Updated:\*\* \d{4}-\d{2}-\d{2}$/m,
    "**Last Updated:** 2024-01-01"
  );
  assert.equal(hasValidLastUpdatedLine(onDisk), true);
  assert.equal(normalizeForComparison(onDisk), normalizeForComparison(generated));

  const edited = onDisk.replace("## Overview", "## Overhauled");
  assert.notEqual(normalizeForComparison(edited), normalizeForComparison(generated));
});
