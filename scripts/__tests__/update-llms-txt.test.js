"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { execFileSync } = require("node:child_process");
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
  summarizeDrift,
  collectValidationErrors
} = require("../update-llms-txt.js");
const { normalizeToLf } = require("../lib/line-endings.js");

const ROOT_DIR = path.resolve(__dirname, "..", "..");
const SCRIPT = path.join(__dirname, "..", "update-llms-txt.js");
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

// --- Update/check convergence: the fixer never reports a false success ---
// These spawn the CLI against env-pointed temp fixtures (DX_LLMS_TXT/DX_README),
// the same exit-code testing pattern generate-skills-index.test.js uses. They
// live in `npm test` (not the fast pre-push subset) because they fork node.

test("CLI update mode fails fast (no false success) when a guarded claim is unfixable", () => {
  // syncSkillCountClaim refuses to rewrite a doc whose claim is missing or
  // duplicated (an ambiguous regex edit could mangle prose). Update must surface
  // that as a non-zero exit -- never report success and let --check / pre-commit
  // / CI reject the very state it left behind. Regression guard for the Copilot
  // finding: update silently no-ops while --check rejects an unfixable README.
  for (const body of ["no skill claim here at all\n", `${SKILL_CLAIM(10)}\n${SKILL_CLAIM(20)}\n`]) {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "llms-cli-bad-"));
    try {
      const readme = path.join(dir, "README.md");
      fs.writeFileSync(readme, body);
      const before = fs.readFileSync(readme, "utf8");
      const env = { ...process.env, DX_LLMS_TXT: path.join(dir, "llms.txt"), DX_README: readme };

      let err;
      try {
        execFileSync("node", [SCRIPT], { env, stdio: "pipe" });
      } catch (caught) {
        err = caught;
      }
      assert.ok(err, `update must exit non-zero for ${JSON.stringify(body)}`);
      assert.equal(err.status, 1, "update should exit with code 1, not crash with another code");
      assert.match(String(err.stderr), /README\.md/);
      assert.equal(
        fs.readFileSync(readme, "utf8"),
        before,
        "an unfixable doc is left for a human, never mangled"
      );
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  }
});

test("CLI update mode fixes a stale single claim, then --check passes (update/check converge)", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "llms-cli-conv-"));
  try {
    // Hermetic fixture: a temp skills dir with a KNOWN count (3) so the assertion
    // never couples to the live repo's skill count (DX_SKILLS_DIR mirrors the
    // temp-fixture pattern in generate-skills-index.test.js).
    const skills = path.join(dir, "skills");
    for (const rel of ["documentation/a.md", "testing/b.md", "testing/c.md"]) {
      fs.mkdirSync(path.join(skills, path.dirname(rel)), { recursive: true });
      fs.writeFileSync(path.join(skills, rel), "# skill\n");
    }
    const llmsTxt = path.join(dir, "llms.txt");
    const readme = path.join(dir, "README.md");
    // A single, understated claim is the fixable case: update rewrites it to the
    // exact count (3) and the very next --check must pass (they converge).
    fs.writeFileSync(readme, `intro\n${SKILL_CLAIM(1)}\noutro\n`);
    const env = { ...process.env, DX_SKILLS_DIR: skills, DX_LLMS_TXT: llmsTxt, DX_README: readme };

    execFileSync("node", [SCRIPT], { env, stdio: "pipe" }); // update -> exit 0
    assert.match(fs.readFileSync(readme, "utf8"), /\b3\+ specialized skill documents\b/);
    assert.ok(fs.existsSync(llmsTxt), "update wrote the env-pointed llms.txt");

    execFileSync("node", [SCRIPT, "--check"], { env, stdio: "pipe" }); // converged -> exit 0
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("collectValidationErrors (the shared --check/update contract) flags a malformed sibling", () => {
  // Direct, in-process coverage of the single validator both modes consume: a
  // duplicated sibling claim is reported (shape failure, count-independent) while
  // a freshly generated llms.txt is accepted.
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "llms-cve-"));
  try {
    const llmsTxt = path.join(dir, "llms.txt");
    const newContent = generateLlmsTxt();
    fs.writeFileSync(llmsTxt, normalizeToLf(newContent));
    const readme = path.join(dir, "README.md");
    fs.writeFileSync(readme, `${SKILL_CLAIM(10)}\n${SKILL_CLAIM(20)}`); // duplicated -> invalid
    const claimFiles = [
      { label: "llms.txt", filePath: llmsTxt },
      { label: "README.md", filePath: readme }
    ];

    const errors = collectValidationErrors({
      newContent,
      llmsTxtPath: llmsTxt,
      claimFiles,
      actualCount: countSkillFiles()
    });
    assert.ok(
      errors.some((error) => /README\.md/.test(error)),
      "a duplicated README claim must be reported"
    );
    assert.ok(
      !errors.some((error) => /llms\.txt/.test(error)),
      "a freshly generated llms.txt is valid (no structural or claim error)"
    );
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
