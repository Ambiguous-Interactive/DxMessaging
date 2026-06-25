"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { execFileSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  analyze,
  compareDescending,
  computeExpected,
  normalizeVersions,
  parseChangelogVersions,
  parsePackageVersion,
  parseRegion,
  renderOptions
} = require("../generate-issue-template-versions.js");

const SCRIPT = path.join(__dirname, "..", "generate-issue-template-versions.js");
const INDENT = "        ";

/** A bug_report.yml fixture carrying `committed` versions between the sentinels. */
function templateFixture(committed) {
  return [
    "  - type: dropdown",
    "    id: package-version",
    "    attributes:",
    "      label: Package Version",
    "      options:",
    `${INDENT}# <!-- AUTO-UPDATED: package-versions -->`,
    ...committed.map((version) => `${INDENT}- ${version}`),
    `${INDENT}- Other`,
    `${INDENT}# <!-- END AUTO-UPDATED: package-versions -->`,
    "    validations:",
    "      required: true",
    ""
  ].join("\n");
}

/** Stage template + package.json + CHANGELOG in a temp dir and return their paths. */
function stageFixture({ committed, packageVersion, changelogVersions }) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dx-issue-versions-"));
  const templatePath = path.join(dir, "bug_report.yml");
  const packagePath = path.join(dir, "package.json");
  const changelogPath = path.join(dir, "CHANGELOG.md");
  fs.writeFileSync(templatePath, templateFixture(committed));
  fs.writeFileSync(packagePath, JSON.stringify({ version: packageVersion }));
  fs.writeFileSync(changelogPath, changelogVersions.map((version) => `## [${version}]`).join("\n") + "\n");
  return { dir, templatePath, packagePath, changelogPath };
}

test("normalizeVersions strips a leading v, drops non-strict, dedupes, sorts descending", () => {
  const result = normalizeVersions(["v2.1.0", "3.1.0", "2.1.0", "3.0.1", "1.0", "2.0.0-rc1", "Other", "10.0.0"]);
  assert.deepEqual(result, ["10.0.0", "3.1.0", "3.0.1", "2.1.0"]);
});

test("compareDescending orders numerically, not lexically (2.10.0 > 2.9.0)", () => {
  const sorted = ["2.9.0", "2.10.0", "2.2.0"].sort(compareDescending);
  assert.deepEqual(sorted, ["2.10.0", "2.9.0", "2.2.0"]);
});

test("parsePackageVersion / parseChangelogVersions extract the right shapes", () => {
  assert.deepEqual(parsePackageVersion(JSON.stringify({ version: "3.1.0" })), ["3.1.0"]);
  assert.deepEqual(parsePackageVersion(JSON.stringify({ name: "x" })), []);
  const changelog = "# Changelog\n## [Unreleased]\n## [3.1.0]\nfoo\n## [3.0.1] - 2026\n";
  assert.deepEqual(parseChangelogVersions(changelog), ["3.1.0", "3.0.1"]);
});

test("parseRegion finds the sentinels, indent, and committed versions", () => {
  const lines = templateFixture(["3.1.0", "3.0.0"]).split("\n");
  const region = parseRegion(lines);
  assert.equal(region.indent, INDENT);
  assert.deepEqual(region.committed, ["3.1.0", "3.0.0"]);
  assert.ok(region.endIdx > region.beginIdx);
});

test("parseRegion throws on missing or out-of-order sentinels", () => {
  assert.throws(() => parseRegion(["      options:", "        - 3.1.0"]), /sentinel/);
  const swapped = [
    `${INDENT}# <!-- END AUTO-UPDATED: package-versions -->`,
    `${INDENT}# <!-- AUTO-UPDATED: package-versions -->`
  ];
  assert.throws(() => parseRegion(swapped), /precedes its BEGIN/);
});

test("computeExpected folds the committed list in so versions are never dropped", () => {
  // No git tags and a CHANGELOG that omits old versions: the committed ones survive.
  const expected = computeExpected(["3.1.0"], ["3.0.0", "2.0.0"]);
  assert.deepEqual(expected, ["3.1.0", "3.0.0", "2.0.0"]);
});

test("renderOptions appends a trailing Other fallback at the region indent", () => {
  assert.deepEqual(renderOptions(["3.1.0", "3.0.0"], INDENT), [`${INDENT}- 3.1.0`, `${INDENT}- 3.0.0`, `${INDENT}- Other`]);
});

test("analyze: an up-to-date template reports no drift and a byte-identical rewrite", () => {
  const fixture = stageFixture({
    committed: ["3.1.0", "3.0.0", "2.0.0"],
    packageVersion: "3.1.0",
    changelogVersions: ["Unreleased", "3.1.0"]
  });
  const result = analyze({ ...paths(fixture), gitTags: ["v3.0.0", "v2.0.0"] });
  assert.deepEqual(result.drift, []);
  assert.equal(result.newText, fs.readFileSync(fixture.templatePath, "utf8"));
});

test("analyze: a new package.json version not in the dropdown is flagged and fixed", () => {
  const fixture = stageFixture({
    committed: ["3.0.1", "3.0.0"],
    packageVersion: "3.1.0",
    changelogVersions: ["3.1.0", "3.0.1"]
  });
  const result = analyze({ ...paths(fixture), gitTags: [] });
  assert.deepEqual(result.drift, ["missing version option: 3.1.0"]);
  // The rewrite inserts 3.1.0 at the top and keeps Other last.
  assert.match(result.newText, /- 3\.1\.0\n.*- 3\.0\.1\n.*- 3\.0\.0\n.*- Other/s);
});

test("analyze: a shallow checkout (no git tags) keeps committed history -> no drift", () => {
  const fixture = stageFixture({
    committed: ["3.1.0", "3.0.0", "2.1.9", "2.0.0"],
    packageVersion: "3.1.0",
    changelogVersions: ["3.1.0"] // CHANGELOG omits the old ones; tags are absent
  });
  const result = analyze({ ...paths(fixture), gitTags: [] });
  assert.deepEqual(result.drift, []);
});

test("CLI --check exits 0 on the real repo template (the validate:all gate)", () => {
  const output = execFileSync("node", [SCRIPT, "--check"], { encoding: "utf8" });
  assert.match(output, /up to date/);
});

test("CLI --check exits 1 (red) when a source version is missing, then write (green) fixes it", () => {
  const fixture = stageFixture({
    committed: ["3.0.0"],
    packageVersion: "3.1.0",
    changelogVersions: ["3.1.0"]
  });
  const env = {
    ...process.env,
    DX_ISSUE_TEMPLATE: fixture.templatePath,
    DX_PACKAGE_JSON: fixture.packagePath,
    DX_CHANGELOG: fixture.changelogPath,
    DX_GIT_TAGS: ""
  };
  assert.throws(
    () => execFileSync("node", [SCRIPT, "--check"], { env, stdio: "pipe" }),
    (error) => /out of date/.test(String(error.stderr))
  );
  execFileSync("node", [SCRIPT], { env, stdio: "pipe" });
  const fixed = fs.readFileSync(fixture.templatePath, "utf8");
  assert.match(fixed, /- 3\.1\.0/);
  // Now the gate passes (green).
  const output = execFileSync("node", [SCRIPT, "--check"], { env, encoding: "utf8" });
  assert.match(output, /up to date/);
});

/** Map a staged fixture onto analyze()'s path option names. */
function paths(fixture) {
  return { templatePath: fixture.templatePath, packagePath: fixture.packagePath, changelogPath: fixture.changelogPath };
}
