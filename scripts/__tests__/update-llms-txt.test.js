"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  generateLlmsTxt,
  countSkillFiles,
  getSkillCategories,
  hasValidLastUpdatedLine,
  normalizeForComparison,
  parseSkillCountClaims,
  validateSkillCountClaim,
  syncSkillCountClaim,
  summarizeDrift
} = require("../update-llms-txt.js");
const { normalizeToLf } = require("../lib/line-endings.js");

const ROOT_DIR = path.resolve(__dirname, "..", "..");
const SKILL_CLAIM = (n) => `- ${n}+ specialized skill documents covering:`;

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

// --- Skill-count claim: floored "at least N", only overstating is an error ---

test("parseSkillCountClaims extracts every claim in document order", () => {
  assert.deepEqual(parseSkillCountClaims("no claim here"), []);
  assert.deepEqual(parseSkillCountClaims(SKILL_CLAIM(155)), [155]);
  assert.deepEqual(
    parseSkillCountClaims(`${SKILL_CLAIM(10)}\nmiddle\n${SKILL_CLAIM(20)}`),
    [10, 20]
  );
});

test("validateSkillCountClaim allows exact and conservative claims, rejects overstatement", () => {
  // [claimText, actualCount, expectedOk] — adding skills (claim < actual) must
  // stay green; only claiming MORE than exist is a real, failing error.
  const cases = [
    { name: "exact", content: SKILL_CLAIM(155), actual: 155, ok: true },
    { name: "understated (skills added)", content: SKILL_CLAIM(140), actual: 155, ok: true },
    { name: "overstated by one", content: SKILL_CLAIM(156), actual: 155, ok: false },
    { name: "missing claim", content: "nothing here", actual: 155, ok: false },
    {
      name: "duplicate claims",
      content: `${SKILL_CLAIM(10)}\n${SKILL_CLAIM(10)}`,
      actual: 155,
      ok: false
    },
    { name: "zero claim", content: SKILL_CLAIM(0), actual: 155, ok: false }
  ];
  for (const { name, content, actual, ok } of cases) {
    const result = validateSkillCountClaim(content, actual, "doc");
    assert.equal(result.ok, ok, `${name}: expected ok=${ok}`);
    if (!ok) {
      assert.match(result.reason, /doc:/, `${name}: reason should be labeled and actionable`);
    }
  }
});

test("normalizeForComparison ignores the skill-count number but keeps the phrase", () => {
  // Adding/removing a skill changes the number; that must not trip --check.
  assert.equal(normalizeForComparison(SKILL_CLAIM(150)), normalizeForComparison(SKILL_CLAIM(175)));
  // But the phrase disappearing entirely is still a structural difference.
  assert.notEqual(
    normalizeForComparison(SKILL_CLAIM(150)),
    normalizeForComparison("- removed the skills line entirely:")
  );
});

test("summarizeDrift reports the first differing lines, capped", () => {
  const empty = summarizeDrift("a\nb\nc", "a\nb\nc");
  assert.equal(empty, "", "identical content has no drift");

  const drift = summarizeDrift("a\nB\nc", "a\nx\nc");
  assert.match(drift, /line 2:/);
  assert.match(drift, /"B"/);
  assert.match(drift, /"x"/);

  const many = summarizeDrift(
    Array.from({ length: 20 }, (_, i) => `old${i}`).join("\n"),
    Array.from({ length: 20 }, (_, i) => `new${i}`).join("\n"),
    3
  );
  assert.match(many, /showing first 3 differing lines/);
});

test("syncSkillCountClaim rewrites exactly one claim and is otherwise a no-op", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "llms-sync-"));
  try {
    const writeTmp = (name, body) => {
      const p = path.join(dir, name);
      fs.writeFileSync(p, body);
      return p;
    };

    // Stale single claim -> rewritten to the exact count.
    const stale = writeTmp("stale.md", `intro\n${SKILL_CLAIM(140)}\noutro\n`);
    assert.equal(syncSkillCountClaim(stale, 155), true);
    assert.match(fs.readFileSync(stale, "utf8"), /155\+ specialized skill documents/);

    // Already exact -> no rewrite.
    assert.equal(syncSkillCountClaim(stale, 155), false);

    // No claim, multiple claims, and missing file are all safe no-ops.
    const none = writeTmp("none.md", "nothing here");
    assert.equal(syncSkillCountClaim(none, 155), false);
    const dup = writeTmp("dup.md", `${SKILL_CLAIM(1)}\n${SKILL_CLAIM(2)}`);
    const dupBefore = fs.readFileSync(dup, "utf8");
    assert.equal(syncSkillCountClaim(dup, 155), false);
    assert.equal(fs.readFileSync(dup, "utf8"), dupBefore, "duplicate-claim file is left untouched");
    assert.equal(syncSkillCountClaim(path.join(dir, "missing.md"), 155), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("shipped llms.txt and README skill claims never overstate the real count", () => {
  // Integration guard: the committed docs agree with reality. This is the exact
  // class of drift that previously only llms.txt caught and README did not.
  const actualCount = countSkillFiles();
  for (const file of ["llms.txt", "README.md"]) {
    const content = fs.readFileSync(path.join(ROOT_DIR, file), "utf8");
    const result = validateSkillCountClaim(content, actualCount, file);
    assert.ok(result.ok, result.reason);
  }
});
