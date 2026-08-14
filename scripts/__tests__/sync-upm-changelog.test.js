"use strict";

// Coverage for the package.json `_upm.changelog` sync
// (scripts/release/sync-upm-changelog.js). The Unity Package Manager renders
// that string in the Version History tab, reading it from the resolved
// package's own package.json, so a stale or missing value is a user-visible
// regression that no other check would catch. The writer and `--check` share
// one comparison, so these tests pin the convergence rule too.

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const {
  parseArgs,
  applyUpmChangelog,
  serialize,
  run
} = require("../release/sync-upm-changelog.js");

const SCRIPT = path.resolve(__dirname, "..", "release", "sync-upm-changelog.js");

const CHANGELOG = [
  "# Changelog",
  "",
  "## [Unreleased]",
  "",
  "### Added",
  "",
  "- Not shipped yet.",
  "",
  "## [3.2.2]",
  "",
  "### Fixed",
  "",
  "- Fix the thing consumers hit.",
  "",
  "## [3.2.1]",
  "",
  "### Added",
  "",
  "- Older entry.",
  ""
].join("\n");

const MANIFEST = { name: "com.example.package", version: "3.2.2", unity: "2021.3" };

function makeFixture(t, { manifest = MANIFEST, changelog = CHANGELOG } = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-upm-changelog-"));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const packagePath = path.join(directory, "package.json");
  const changelogPath = path.join(directory, "CHANGELOG.md");
  fs.writeFileSync(packagePath, serialize(manifest), "utf8");
  fs.writeFileSync(changelogPath, changelog, "utf8");
  return { directory, packagePath, changelogPath };
}

test("applyUpmChangelog mirrors the section for the manifest version", () => {
  const updated = applyUpmChangelog(MANIFEST, CHANGELOG);
  assert.equal(updated._upm.changelog, "### Fixed\n\n- Fix the thing consumers hit.");
  assert.equal(updated.name, MANIFEST.name);
});

test("applyUpmChangelog does not mutate its input", () => {
  const input = { ...MANIFEST };
  applyUpmChangelog(input, CHANGELOG);
  assert.equal(input._upm, undefined);
});

test("applyUpmChangelog keeps other _upm keys and replaces a stale changelog", () => {
  const updated = applyUpmChangelog(
    { ...MANIFEST, _upm: { changelog: "old", gameService: true } },
    CHANGELOG
  );
  assert.equal(updated._upm.changelog, "### Fixed\n\n- Fix the thing consumers hit.");
  assert.equal(updated._upm.gameService, true);
});

test("applyUpmChangelog refuses a version with no changelog section", () => {
  assert.throws(
    () => applyUpmChangelog({ ...MANIFEST, version: "9.9.9" }, CHANGELOG),
    /no '## \[9\.9\.9\]' section/
  );
});

test("applyUpmChangelog refuses a manifest with no version", () => {
  assert.throws(() => applyUpmChangelog({ name: "x" }, CHANGELOG), /no version/);
});

test("run writes the field, is idempotent, and satisfies its own --check", (t) => {
  const fixture = makeFixture(t);
  const options = {
    repoRoot: fixture.directory,
    packagePath: fixture.packagePath,
    changelogPath: fixture.changelogPath
  };

  assert.equal(run(options).changed, true);
  const written = JSON.parse(fs.readFileSync(fixture.packagePath, "utf8"));
  assert.equal(written._upm.changelog, "### Fixed\n\n- Fix the thing consumers hit.");

  assert.equal(run(options).changed, false);
  assert.doesNotThrow(() => run({ ...options, check: true }));
});

test("run --check reports drift and names the fix command", (t) => {
  const fixture = makeFixture(t, {
    manifest: { ...MANIFEST, _upm: { changelog: "stale" } }
  });
  assert.throws(
    () =>
      run({
        repoRoot: fixture.directory,
        packagePath: fixture.packagePath,
        changelogPath: fixture.changelogPath,
        check: true
      }),
    /sync:upm-changelog/
  );
});

test("parseArgs rejects unknown flags and flag-shaped values", () => {
  assert.deepEqual(parseArgs(["--check"]), { check: true });
  assert.throws(() => parseArgs(["--nope"]), /Unknown argument/);
  assert.throws(() => parseArgs(["--package", "--check"]), /Missing value/);
});

test("the CLI exits non-zero on drift and zero once synced", (t) => {
  const fixture = makeFixture(t);
  const args = ["--package", fixture.packagePath, "--changelog", fixture.changelogPath];

  const drifted = spawnSync(process.execPath, [SCRIPT, ...args, "--check"], {
    encoding: "utf8"
  });
  assert.equal(drifted.status, 1);
  assert.match(drifted.stderr, /stale/);

  const fixed = spawnSync(process.execPath, [SCRIPT, ...args], { encoding: "utf8" });
  assert.equal(fixed.status, 0);

  const checked = spawnSync(process.execPath, [SCRIPT, ...args, "--check"], {
    encoding: "utf8"
  });
  assert.equal(checked.status, 0);
});

test("the repository package.json carries the section for its own version", () => {
  const root = path.resolve(__dirname, "..", "..");
  const manifest = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  const changelog = fs.readFileSync(path.join(root, "CHANGELOG.md"), "utf8");
  assert.equal(
    manifest._upm.changelog,
    applyUpmChangelog(manifest, changelog)._upm.changelog
  );
});
