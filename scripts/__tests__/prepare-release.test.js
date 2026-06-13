"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  computeNextVersion,
  replacePackageVersion,
  rotateChangelog,
  prepareRelease,
  parseArgs
} = require("../release/prepare-release.js");

const FULL_CHANGELOG = [
  "# Changelog",
  "",
  "Header prose.",
  "",
  "## [Unreleased]",
  "",
  "### Added",
  "",
  "- A new thing.",
  "",
  "### Changed",
  "",
  "- A changed thing.",
  "",
  "### Fixed",
  "",
  "- A fixed thing.",
  "",
  "## [3.0.1]",
  "",
  "### Fixed",
  "",
  "- An old fix.",
  ""
].join("\n");

function makeFixture(t, { packageJson, changelog }) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-prepare-release-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.writeFileSync(path.join(root, "package.json"), packageJson, "utf8");
  fs.writeFileSync(path.join(root, "CHANGELOG.md"), changelog, "utf8");
  return root;
}

function makeDefaultFixture(t) {
  const packageJson = [
    "{",
    '  "name": "com.example.fixture",',
    '  "version": "3.0.1",',
    '  "displayName": "Fixture"',
    "}",
    ""
  ].join("\n");
  return makeFixture(t, { packageJson, changelog: FULL_CHANGELOG });
}

test("computeNextVersion bumps patch, minor, and major with resets", () => {
  assert.equal(computeNextVersion("3.0.1", { bump: "patch" }), "3.0.2");
  assert.equal(computeNextVersion("3.0.1", { bump: "minor" }), "3.1.0");
  assert.equal(computeNextVersion("3.9.7", { bump: "minor" }), "3.10.0");
  assert.equal(computeNextVersion("3.4.5", { bump: "major" }), "4.0.0");
});

test("computeNextVersion prefers an explicit version over the bump kind", () => {
  assert.equal(computeNextVersion("3.0.1", { bump: "patch", version: "5.0.0" }), "5.0.0");
});

test("computeNextVersion rejects invalid or non-increasing explicit versions", () => {
  assert.throws(() => computeNextVersion("3.0.1", { version: "3.0" }), /Invalid semver/);
  assert.throws(() => computeNextVersion("3.0.1", { version: "v3.0.2" }), /Invalid semver/);
  assert.throws(() => computeNextVersion("3.0.1", { version: "3.0.2-rc.1" }), /Invalid semver/);
  assert.throws(() => computeNextVersion("3.0.1", { version: "3.0.1" }), /strictly greater/);
  assert.throws(() => computeNextVersion("3.0.1", { version: "2.9.9" }), /strictly greater/);
  // Componentwise compare, not string compare: 3.0.10 > 3.0.9.
  assert.equal(computeNextVersion("3.0.9", { version: "3.0.10" }), "3.0.10");
});

test("computeNextVersion rejects components with leading zeros", () => {
  assert.throws(() => computeNextVersion("3.0.1", { version: "03.1.0" }), /Invalid semver/);
  assert.throws(() => computeNextVersion("3.0.1", { version: "3.01.0" }), /Invalid semver/);
  assert.throws(() => computeNextVersion("3.0.1", { version: "3.1.00" }), /Invalid semver/);
});

test("computeNextVersion accepts zero and multi-digit components", () => {
  assert.equal(computeNextVersion("0.1.9", { version: "0.2.0" }), "0.2.0");
  assert.equal(computeNextVersion("9.9.9", { version: "10.0.0" }), "10.0.0");
  assert.equal(computeNextVersion("0.0.1", { bump: "patch" }), "0.0.2");
});

test("computeNextVersion rejects an unknown bump kind", () => {
  assert.throws(() => computeNextVersion("3.0.1", { bump: "huge" }), /Unknown bump kind/);
});

test("parseArgs errors when --version or --bump has no value", () => {
  assert.throws(() => parseArgs(["--bump", "patch", "--version"]), /Missing value for --version/);
  assert.throws(() => parseArgs(["--bump"]), /Missing value for --bump/);
});

test("parseArgs parses flags with values", () => {
  assert.deepEqual(parseArgs(["--bump", "patch", "--dry-run"]), {
    bump: "patch",
    version: undefined,
    dryRun: true
  });
  assert.deepEqual(parseArgs(["--version", "4.0.0"]), {
    bump: undefined,
    version: "4.0.0",
    dryRun: false
  });
});

test("replacePackageVersion rewrites only the version line", () => {
  const raw = ['{\n  "name": "x",\n  "version": "3.0.1",\n  "note": "v3.0.1 text"\n}', ""].join(
    "\n"
  );
  const updated = replacePackageVersion(raw, "3.0.1", "3.0.2");
  assert.ok(updated.includes('"version": "3.0.2"'));
  assert.ok(updated.includes('"note": "v3.0.1 text"'));
  assert.equal(updated.replace('"version": "3.0.2"', '"version": "3.0.1"'), raw);
});

test("replacePackageVersion refuses ambiguous or missing version lines", () => {
  const twice = '{ "version": "3.0.1", "nested": { "version": "3.0.1" } }';
  assert.throws(() => replacePackageVersion(twice, "3.0.1", "3.0.2"), /found 2/);
  assert.throws(() => replacePackageVersion('{ "version": "9.9.9" }', "3.0.1", "3.0.2"), /found 0/);
});

test("rotateChangelog moves the Unreleased block under the new heading", () => {
  const rotated = rotateChangelog(FULL_CHANGELOG, "3.1.0");
  const lines = rotated.split("\n");
  const unreleasedIndex = lines.indexOf("## [Unreleased]");
  assert.equal(lines[unreleasedIndex + 1], "");
  assert.equal(lines[unreleasedIndex + 2], "## [3.1.0]");
  // The moved block keeps its original subsection order, verbatim.
  const headings = lines.filter((line) => line.startsWith("## ") || line.startsWith("### "));
  assert.deepEqual(headings, [
    "## [Unreleased]",
    "## [3.1.0]",
    "### Added",
    "### Changed",
    "### Fixed",
    "## [3.0.1]",
    "### Fixed"
  ]);
  assert.ok(rotated.includes("- A new thing."));
  assert.ok(rotated.includes("- An old fix."));
  assert.ok(rotated.endsWith("- An old fix.\n"));
});

test("rotateChangelog handles a subset of subsections and no later headings", () => {
  const changelog = [
    "# Changelog",
    "",
    "## [Unreleased]",
    "",
    "### Fixed",
    "",
    "- Only a fix.",
    ""
  ].join("\n");
  const rotated = rotateChangelog(changelog, "1.2.3");
  assert.equal(
    rotated,
    [
      "# Changelog",
      "",
      "## [Unreleased]",
      "",
      "## [1.2.3]",
      "",
      "### Fixed",
      "",
      "- Only a fix.",
      ""
    ].join("\n")
  );
});

test("rotateChangelog normalizes CRLF input to LF output", () => {
  const rotated = rotateChangelog(FULL_CHANGELOG.replace(/\n/g, "\r\n"), "3.1.0");
  assert.ok(!rotated.includes("\r"));
  assert.ok(rotated.includes("## [3.1.0]"));
});

test("rotateChangelog refuses an Unreleased section with no content", () => {
  const empty = ["# Changelog", "", "## [Unreleased]", "", "## [3.0.1]", "", "- Old.", ""].join(
    "\n"
  );
  assert.throws(() => rotateChangelog(empty, "3.0.2"), /no content/);
  const onlyHeadings = [
    "# Changelog",
    "",
    "## [Unreleased]",
    "",
    "### Added",
    "",
    "### Fixed",
    "",
    "## [3.0.1]",
    ""
  ].join("\n");
  assert.throws(() => rotateChangelog(onlyHeadings, "3.0.2"), /no content/);
});

test("rotateChangelog refuses when the version heading already exists", () => {
  assert.throws(() => rotateChangelog(FULL_CHANGELOG, "3.0.1"), /refusing to rotate twice/);
});

test("rotateChangelog requires exactly one Unreleased heading", () => {
  assert.throws(() => rotateChangelog("# Changelog\n\n- entry\n", "1.0.0"), /exactly one/);
  const doubled = `${FULL_CHANGELOG}\n## [Unreleased]\n\n- dup\n`;
  assert.throws(() => rotateChangelog(doubled, "3.1.0"), /exactly one/);
});

test("rotateChangelog keeps fenced ## lines inside the Unreleased block", () => {
  for (const fence of ["```", "~~~"]) {
    const changelog = [
      "# Changelog",
      "",
      "## [Unreleased]",
      "",
      "### Added",
      "",
      "- A migration example:",
      "",
      `${fence}markdown`,
      "## [Unreleased]",
      "## Not a heading",
      fence,
      "",
      "- After the fence.",
      "",
      "## [3.0.1]",
      "",
      "- Old.",
      ""
    ].join("\n");
    const rotated = rotateChangelog(changelog, "3.1.0").split("\n");
    // The fenced lines must not terminate (or duplicate) the Unreleased
    // section: everything through "- After the fence." moves under 3.1.0.
    const order = [
      rotated.indexOf("## [3.1.0]"),
      rotated.indexOf("## Not a heading"),
      rotated.indexOf("- After the fence."),
      rotated.indexOf("## [3.0.1]")
    ];
    assert.ok(
      order.every((index, i) => index !== -1 && (i === 0 || order[i - 1] < index)),
      `unexpected ordering ${JSON.stringify(order)} for fence ${fence}:\n${rotated.join("\n")}`
    );
  }
});

test("prepareRelease writes package.json and CHANGELOG.md", (t) => {
  const root = makeDefaultFixture(t);
  const result = prepareRelease({ repoRoot: root, bump: "minor" });
  assert.equal(result.currentVersion, "3.0.1");
  assert.equal(result.nextVersion, "3.1.0");
  const packageJson = fs.readFileSync(path.join(root, "package.json"), "utf8");
  assert.ok(packageJson.includes('"version": "3.1.0"'));
  assert.ok(packageJson.includes('"displayName": "Fixture"'));
  const changelog = fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8");
  assert.ok(changelog.includes("## [3.1.0]"));
  assert.ok(changelog.includes("## [Unreleased]"));
});

test("prepareRelease --dry-run leaves both files untouched", (t) => {
  const root = makeDefaultFixture(t);
  const beforePackage = fs.readFileSync(path.join(root, "package.json"), "utf8");
  const beforeChangelog = fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8");
  const result = prepareRelease({ repoRoot: root, bump: "patch", dryRun: true });
  assert.equal(result.nextVersion, "3.0.2");
  assert.equal(fs.readFileSync(path.join(root, "package.json"), "utf8"), beforePackage);
  assert.equal(fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8"), beforeChangelog);
});

test("prepareRelease fails before writing anything when the changelog refuses", (t) => {
  const packageJson = '{\n  "version": "3.0.1"\n}\n';
  const changelog = "# Changelog\n\n## [Unreleased]\n\n## [3.0.1]\n\n- Old.\n";
  const root = makeFixture(t, { packageJson, changelog });
  assert.throws(() => prepareRelease({ repoRoot: root, bump: "patch" }), /no content/);
  assert.equal(fs.readFileSync(path.join(root, "package.json"), "utf8"), packageJson);
  assert.equal(fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8"), changelog);
});

test("prepareRelease writes CHANGELOG.md before package.json", (t) => {
  const root = makeDefaultFixture(t);
  const realWriteFileSync = fs.writeFileSync;
  const writes = [];
  t.mock.method(fs, "writeFileSync", (file, data, encoding) => {
    writes.push(path.basename(String(file)));
    return realWriteFileSync(file, data, encoding);
  });
  prepareRelease({ repoRoot: root, bump: "minor" });
  assert.deepEqual(writes, ["CHANGELOG.md", "package.json"]);
});

test("prepareRelease re-run self-heals a crash between the two writes", (t) => {
  const root = makeDefaultFixture(t);
  // Crash state: CHANGELOG.md already rotated, package.json still 3.0.1.
  const rotated = rotateChangelog(FULL_CHANGELOG, "3.1.0");
  fs.writeFileSync(path.join(root, "CHANGELOG.md"), rotated, "utf8");
  const result = prepareRelease({ repoRoot: root, bump: "minor" });
  assert.equal(result.nextVersion, "3.1.0");
  assert.equal(result.changelogAlreadyRotated, true);
  const packageJson = fs.readFileSync(path.join(root, "package.json"), "utf8");
  assert.ok(packageJson.includes('"version": "3.1.0"'));
  assert.equal(fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8"), rotated);
});

test("prepareRelease still refuses true duplicates after a completed release", (t) => {
  const root = makeDefaultFixture(t);
  prepareRelease({ repoRoot: root, bump: "minor" });
  // Same explicit version again: caught by the strictly-greater check.
  assert.throws(() => prepareRelease({ repoRoot: root, version: "3.1.0" }), /strictly greater/);
  // Same bump again: the fresh 3.2.0 rotation trips the empty-Unreleased guard.
  assert.throws(() => prepareRelease({ repoRoot: root, bump: "minor" }), /no content/);
});
