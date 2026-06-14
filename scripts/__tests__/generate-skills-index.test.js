"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { execFileSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  analyze,
  buildExpectedModel,
  countLines,
  enumerateSkillFiles,
  findBijectionErrors,
  findDrift,
  parseIndex,
  renderIndex,
  slugify,
  statusMarker
} = require("../generate-skills-index.js");

const SCRIPT = path.join(__dirname, "..", "generate-skills-index.js");

// A small fixture index with one row per category and one Summary/TOC block.
const FIXTURE_INDEX = [
  "# Skills Index",
  "",
  "## Summary",
  "",
  "| Metric       | Value |",
  "| ------------ | ----- |",
  "| Total Skills | 3     |",
  "| Categories   | 2     |",
  "",
  "## Table of Contents",
  "",
  "- [Documentation](#documentation) (2)",
  "- [Testing](#testing) (1)",
  "",
  "## Documentation",
  "",
  "| Skill                             | Lines      | Complexity | Status   | Performance  | Tags |",
  "| --------------------------------- | ---------- | ---------- | -------- | ------------ | ---- |",
  "| [Alpha](./documentation/alpha.md) | [ok] 130   | [basic]    | [stable] | [risk: none] | a, b |",
  "| [Beta](./documentation/beta.md)   | [draft] 50 | [basic]    | [stable] | [risk: low]  | c    |",
  "",
  "## Testing",
  "",
  "| Skill                       | Lines      | Complexity | Status   | Performance  | Tags |",
  "| --------------------------- | ---------- | ---------- | -------- | ------------ | ---- |",
  "| [Gamma](./testing/gamma.md) | [warn] 300 | [basic]    | [stable] | [risk: none] | d    |",
  ""
].join("\n");

function skillFile(link, lineCount) {
  return { link, category: link.replace("./", "").split("/")[0], lineCount };
}

function writeLines(file, lineCount) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, "content line\n".repeat(lineCount), "utf8");
}

test("countLines counts newline bytes like wc -l", () => {
  assert.equal(countLines(""), 0);
  assert.equal(countLines("a\nb\nc\n"), 3);
  assert.equal(countLines("a\nb\nc"), 2); // no trailing newline
  assert.equal(countLines("a\r\nb\r\n"), 2); // CRLF
  assert.equal(countLines("x\n".repeat(173)), 173);
});

test("statusMarker draws the boundaries at 120 and 260", () => {
  assert.equal(statusMarker(119), "draft");
  assert.equal(statusMarker(120), "ok");
  assert.equal(statusMarker(260), "ok");
  assert.equal(statusMarker(261), "warn");
});

test("slugify matches GitHub anchor slugs", () => {
  assert.equal(slugify("Documentation"), "documentation");
  assert.equal(slugify("GitHub Actions"), "github-actions");
  assert.equal(slugify("Table of Contents"), "table-of-contents");
});

test("parseIndex extracts rows, TOC literals, and the Summary values", () => {
  const parsed = parseIndex(FIXTURE_INDEX);
  assert.deepEqual(
    parsed.rows.map((row) => row.link),
    ["./documentation/alpha.md", "./documentation/beta.md", "./testing/gamma.md"]
  );
  assert.equal(parsed.rows[0].section, "documentation");
  assert.equal(parsed.rows[2].section, "testing");
  // tocCounts is the literal "(N)" beside each TOC entry, not a row tally.
  assert.equal(parsed.tocCounts.get("documentation"), 2);
  assert.equal(parsed.tocCounts.get("testing"), 1);
  assert.deepEqual(parsed.summary, { totalSkills: 3, categoryCount: 2 });
});

test("findDrift catches a stale Table-of-Contents count when rows are correct", () => {
  // Only the TOC literal is wrong; every row, marker, and Summary value matches
  // disk. The row tally would hide this -- the TOC literal must be the input.
  const staleToc = FIXTURE_INDEX.replace("(#testing) (1)", "(#testing) (9)");
  const parsed = parseIndex(staleToc);
  const expected = buildExpectedModel([
    skillFile("./documentation/alpha.md", 130),
    skillFile("./documentation/beta.md", 50),
    skillFile("./testing/gamma.md", 300)
  ]);
  assert.match(findDrift(parsed, expected).join("\n"), /testing": \(9\) -> \(1\)/);
});

test("a missing Summary row drifts but renderIndex cannot invent it (no silent convergence)", () => {
  const broken = FIXTURE_INDEX.replace("| Total Skills | 3     |\n", "");
  const parsed = parseIndex(broken);
  assert.equal(parsed.summary.totalSkills, null);
  const expected = buildExpectedModel([
    skillFile("./documentation/alpha.md", 130),
    skillFile("./documentation/beta.md", 50),
    skillFile("./testing/gamma.md", 300)
  ]);
  assert.ok(findDrift(parsed, expected).some((entry) => /Total Skills/.test(entry)));
  assert.equal(renderIndex(broken, expected), broken); // cannot insert a missing row -> main() errors
});

test("renderIndex does not mistake a '[ok] 5' inside a title for the Lines cell", () => {
  const tricky = FIXTURE_INDEX.replace("[Alpha]", "[Alpha [ok] 5 form]");
  const expected = buildExpectedModel([
    skillFile("./documentation/alpha.md", 200),
    skillFile("./documentation/beta.md", 50),
    skillFile("./testing/gamma.md", 300)
  ]);
  const rendered = renderIndex(tricky, expected);
  assert.match(rendered, /\[Alpha \[ok\] 5 form\]/); // title token survives
  assert.match(rendered, /\[ok\] 200/); // real Lines cell updated
});

test("parseIndex tolerates a title containing square brackets (link-anchored)", () => {
  const tricky = FIXTURE_INDEX.replace("[Alpha]", "[Alpha [array] form]");
  const parsed = parseIndex(tricky);
  assert.equal(parsed.rows.length, 3);
  assert.ok(parsed.rows.some((row) => row.link === "./documentation/alpha.md"));
});

test("findDrift is clean when the index matches disk", () => {
  const parsed = parseIndex(FIXTURE_INDEX);
  const expected = buildExpectedModel([
    skillFile("./documentation/alpha.md", 130),
    skillFile("./documentation/beta.md", 50),
    skillFile("./testing/gamma.md", 300)
  ]);
  assert.deepEqual(findDrift(parsed, expected), []);
});

test("findDrift reports count, marker, summary, and TOC drift", () => {
  const parsed = parseIndex(FIXTURE_INDEX);
  const expected = buildExpectedModel([
    skillFile("./documentation/alpha.md", 119), // marker flips ok -> draft
    skillFile("./documentation/beta.md", 50),
    skillFile("./testing/gamma.md", 240), // marker flips warn -> ok
    skillFile("./testing/delta.md", 75) // extra file: total + testing section grow
  ]);
  const joined = findDrift(parsed, expected).join("\n");
  assert.match(joined, /alpha\.md: index \[ok\] 130 -> \[draft\] 119/);
  assert.match(joined, /gamma\.md: index \[warn\] 300 -> \[ok\] 240/);
  assert.match(joined, /Total Skills": 3 -> 4/);
  assert.match(joined, /testing": \(1\) -> \(2\)/);
});

test("findBijectionErrors passes on a bijective, correctly-sectioned index", () => {
  const parsed = parseIndex(FIXTURE_INDEX);
  const files = [
    skillFile("./documentation/alpha.md", 130),
    skillFile("./documentation/beta.md", 50),
    skillFile("./testing/gamma.md", 300)
  ];
  assert.deepEqual(findBijectionErrors(files, parsed), []);
});

test("findBijectionErrors flags missing rows, orphan rows, and duplicates", () => {
  const parsed = parseIndex(FIXTURE_INDEX);
  // Drop beta (orphan row), add an undocumented file (missing row).
  const missingRow = findBijectionErrors(
    [skillFile("./documentation/alpha.md", 130), skillFile("./testing/gamma.md", 300), skillFile("./testing/extra.md", 90)],
    parsed
  );
  assert.ok(missingRow.some((error) => /missing skill file: \.\/documentation\/beta\.md/.test(error)));
  assert.ok(missingRow.some((error) => /no index row: \.\/testing\/extra\.md/.test(error)));
});

test("findBijectionErrors flags a row filed under the wrong section", () => {
  // Move gamma's row under ## Documentation (its file lives in testing/).
  const misfiled = FIXTURE_INDEX.replace(
    "| [Beta](./documentation/beta.md)   | [draft] 50 | [basic]    | [stable] | [risk: low]  | c    |",
    "| [Beta](./documentation/beta.md)   | [draft] 50 | [basic]    | [stable] | [risk: low]  | c    |\n| [Gamma](./testing/gamma.md) | [warn] 300 | [basic] | [stable] | [risk: none] | d |"
  ).replace("| [Gamma](./testing/gamma.md) | [warn] 300 | [basic]    | [stable] | [risk: none] | d    |", "");
  const parsed = parseIndex(misfiled);
  const files = [
    skillFile("./documentation/alpha.md", 130),
    skillFile("./documentation/beta.md", 50),
    skillFile("./testing/gamma.md", 300)
  ];
  const errors = findBijectionErrors(files, parsed);
  assert.ok(errors.some((error) => /filed under section "documentation" but belongs to "testing"/.test(error)));
});

test("renderIndex rewrites Lines/Summary/TOC, preserves metadata, and is idempotent", () => {
  const drifted = FIXTURE_INDEX.replace("[ok] 130", "[ok] 131").replace("[warn] 300", "[warn] 305");
  const expected = buildExpectedModel([
    skillFile("./documentation/alpha.md", 119), // -> [draft] 119
    skillFile("./documentation/beta.md", 50),
    skillFile("./testing/gamma.md", 240) // -> [ok] 240
  ]);

  const rendered = renderIndex(drifted, expected);
  const reparsed = parseIndex(rendered);
  assert.deepEqual(findDrift(reparsed, expected), []);
  assert.equal(reparsed.rows.find((row) => row.link === "./documentation/alpha.md").marker, "draft");

  // Hand-authored columns survive verbatim.
  assert.match(rendered, /\[risk: low\]/);
  assert.match(rendered, /\| a, b \|/);

  // Re-running on the corrected text is a byte-for-byte no-op.
  assert.equal(renderIndex(rendered, expected), rendered);
});

test("enumerateSkillFiles excludes index.md, specification.md, and templates/", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "skills-enum-"));
  try {
    writeLines(path.join(dir, "documentation", "alpha.md"), 130);
    writeLines(path.join(dir, "testing", "gamma.md"), 300);
    writeLines(path.join(dir, "index.md"), 10);
    writeLines(path.join(dir, "specification.md"), 10);
    writeLines(path.join(dir, "templates", "skeleton.md"), 10);
    const files = enumerateSkillFiles(dir);
    assert.deepEqual(
      files.map((file) => file.link),
      ["./documentation/alpha.md", "./testing/gamma.md"]
    );
    assert.equal(files[0].lineCount, 130);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("analyze detects drift then produces a clean, round-tripping index", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "skills-analyze-"));
  try {
    writeLines(path.join(dir, "documentation", "alpha.md"), 119);
    writeLines(path.join(dir, "documentation", "beta.md"), 50);
    writeLines(path.join(dir, "testing", "gamma.md"), 240);
    const indexPath = path.join(dir, "index.md");
    fs.writeFileSync(indexPath, FIXTURE_INDEX, "utf8");

    const before = analyze(dir, indexPath);
    assert.deepEqual(before.errors, []);
    assert.ok(before.drift.length > 0);

    fs.writeFileSync(indexPath, before.newText, "utf8");
    const after = analyze(dir, indexPath);
    assert.deepEqual(after.errors, []);
    assert.deepEqual(after.drift, []);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("CLI exits 1 on drift, writes a fix, then exits 0 (via env-pointed fixture)", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "skills-cli-"));
  try {
    writeLines(path.join(dir, "documentation", "alpha.md"), 119);
    writeLines(path.join(dir, "documentation", "beta.md"), 50);
    writeLines(path.join(dir, "testing", "gamma.md"), 240);
    const indexPath = path.join(dir, "index.md");
    fs.writeFileSync(indexPath, FIXTURE_INDEX, "utf8");
    const env = { ...process.env, DX_SKILLS_DIR: dir, DX_SKILLS_INDEX: indexPath };

    assert.throws(() => execFileSync("node", [SCRIPT, "--check"], { env, stdio: "pipe" }));
    const indexBefore = fs.readFileSync(indexPath, "utf8");
    assert.equal(indexBefore, FIXTURE_INDEX, "--check must not write");

    execFileSync("node", [SCRIPT], { env, stdio: "pipe" }); // write mode exits 0
    assert.notEqual(fs.readFileSync(indexPath, "utf8"), FIXTURE_INDEX);
    execFileSync("node", [SCRIPT, "--check"], { env, stdio: "pipe" }); // now clean, exits 0
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("CLI exits 1 when a skill file has no row (bijection guard)", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "skills-cli-bij-"));
  try {
    writeLines(path.join(dir, "documentation", "alpha.md"), 130);
    writeLines(path.join(dir, "documentation", "beta.md"), 50);
    writeLines(path.join(dir, "testing", "gamma.md"), 300);
    writeLines(path.join(dir, "testing", "undocumented.md"), 88);
    const indexPath = path.join(dir, "index.md");
    fs.writeFileSync(indexPath, FIXTURE_INDEX, "utf8");
    const env = { ...process.env, DX_SKILLS_DIR: dir, DX_SKILLS_INDEX: indexPath };
    assert.throws(() => execFileSync("node", [SCRIPT], { env, stdio: "pipe" }));
    assert.equal(fs.readFileSync(indexPath, "utf8"), FIXTURE_INDEX, "bijection error must not write");
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("CLI write mode exits 1 instead of falsely converging when a Summary row is missing", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "skills-cli-missing-"));
  try {
    // Rows/Lines/TOC all match disk; only the "| Total Skills | N |" row is gone,
    // which renderIndex cannot insert -> write mode must error, not loop.
    writeLines(path.join(dir, "documentation", "alpha.md"), 130);
    writeLines(path.join(dir, "documentation", "beta.md"), 50);
    writeLines(path.join(dir, "testing", "gamma.md"), 300);
    const indexPath = path.join(dir, "index.md");
    const broken = FIXTURE_INDEX.replace("| Total Skills | 3     |\n", "");
    fs.writeFileSync(indexPath, broken, "utf8");
    const env = { ...process.env, DX_SKILLS_DIR: dir, DX_SKILLS_INDEX: indexPath };
    assert.throws(() => execFileSync("node", [SCRIPT], { env, stdio: "pipe" }));
    assert.equal(fs.readFileSync(indexPath, "utf8"), broken, "must not write identical text");
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("parseIndex reads the Lines cell, not a '[ok] 5' inside the title", () => {
  const tricky = FIXTURE_INDEX.replace("[Alpha]", "[Alpha [ok] 5 form]");
  const alpha = parseIndex(tricky).rows.find((row) => row.link === "./documentation/alpha.md");
  assert.equal(alpha.count, 130); // the Lines cell, never the title token
  assert.equal(alpha.marker, "ok");
});
