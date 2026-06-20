"use strict";

// Unit coverage for the single shared changelog-section extractor
// (scripts/release/changelog.js) that release.yml, release-prepare.yml, and
// release-drafter.yml all consume. Guards the v3.1.0 regression class: the
// published GitHub Release body must be the matching `## [version]` CHANGELOG
// section, never a stub. Fenced-code-block awareness is the subtle invariant a
// plain `awk '/^## \[/'` scan gets wrong.

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const { extractSection } = require("../release/changelog.js");

const SAMPLE = [
  "# Changelog",
  "",
  "## [Unreleased]",
  "",
  "### Added",
  "",
  "- Unreleased entry one.",
  "",
  "## [3.1.0]",
  "",
  "### Added",
  "",
  "- Real feature for 3.1.0.",
  "",
  "### Fixed",
  "",
  "- A fix whose example embeds a fake heading:",
  "",
  "  ```md",
  "  ## [9.9.9]",
  "  ```",
  "",
  "## [3.0.1]",
  "",
  "### Changed",
  "",
  "- The oldest documented change.",
  ""
].join("\n");

test("extractSection returns the trimmed body under the matching heading", () => {
  const section = extractSection(SAMPLE, "3.1.0");
  assert.match(section, /^### Added/);
  assert.match(section, /Real feature for 3\.1\.0\./);
  // Stops at the next real `## [` heading: the 3.0.1 body must not leak in.
  assert.doesNotMatch(section, /oldest documented change/);
  // No leading/trailing blank lines.
  assert.equal(section, section.trim());
});

test("a `## [x]` line inside a fenced code block is not a section boundary", () => {
  // The fenced `## [9.9.9]` lives INSIDE the 3.1.0 section; a fence-blind scan
  // would truncate the section there. It must be retained verbatim instead.
  const section = extractSection(SAMPLE, "3.1.0");
  assert.match(section, /## \[9\.9\.9\]/);
  assert.match(section, /A fix whose example embeds a fake heading/);
  // And `9.9.9` is not itself an extractable section (it is only fenced text).
  assert.throws(() => extractSection(SAMPLE, "9.9.9"), /9\.9\.9/);
});

test("a fenced `## [x]` with an info string is still not a boundary", () => {
  // CommonMark fences carry info strings (` ```ts {1,2} `, ` ```c-sharp `). A
  // `\w*`-only fence regex misses those and truncates the section at the inner
  // `## ` line; the section body must survive intact past such a fence.
  const content = [
    "## [5.0.0]",
    "",
    "- intro",
    "",
    "```ts {1,2}",
    "## [9.9.9]",
    "```",
    "",
    "- tail after the fence",
    "",
    "## [4.0.0]",
    "",
    "- older"
  ].join("\n");
  const section = extractSection(content, "5.0.0");
  assert.match(section, /tail after the fence/);
  assert.doesNotMatch(section, /older/);
});

test("a section with only `### ` subsection headers (no entries) throws", () => {
  // Symmetric with prepare-release's hasContent guard: header-only is not
  // publishable release notes.
  const content = ["## [6.0.0]", "", "### Added", "", "### Fixed", "", "## [5.0.0]", "", "- x"].join(
    "\n"
  );
  assert.throws(() => extractSection(content, "6.0.0"), /no content/);
});

test("the last section reads to end-of-file", () => {
  const section = extractSection(SAMPLE, "3.0.1");
  assert.match(section, /The oldest documented change\./);
  assert.equal(section, section.trim());
});

test("Unreleased is extractable by name", () => {
  const section = extractSection(SAMPLE, "Unreleased");
  assert.match(section, /Unreleased entry one\./);
  assert.doesNotMatch(section, /Real feature for 3\.1\.0/);
});

test("a missing version throws naming the version", () => {
  assert.throws(() => extractSection(SAMPLE, "2.0.0"), /2\.0\.0/);
});

test("CRLF input is normalized before extraction", () => {
  const crlf = SAMPLE.replace(/\n/g, "\r\n");
  assert.equal(extractSection(crlf, "3.0.1"), extractSection(SAMPLE, "3.0.1"));
});

test("the unbracketed `## X.Y.Z` heading form is also matched", () => {
  // verify-tag in release.yml accepts `## [x]` OR `## x`; the extractor must
  // agree so a release that passes the gate can always render its notes.
  const unbracketed = ["# Changelog", "", "## 4.2.0", "", "- Plain heading entry.", ""].join(
    "\n"
  );
  assert.match(extractSection(unbracketed, "4.2.0"), /Plain heading entry\./);
});

// --- release-notes.js CLI: section + optional install footer ----------------

const RELEASE_NOTES_CLI = path.join(__dirname, "..", "release", "release-notes.js");

function runReleaseNotes(args) {
  const result = spawnSync(process.execPath, [RELEASE_NOTES_CLI, ...args], {
    encoding: "utf8"
  });
  return result;
}

test("release-notes.js prints the raw section for a real CHANGELOG version", () => {
  // Drive the CLI against a temp CHANGELOG so the test never couples to the
  // repo's current version set.
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-notes-"));
  try {
    const changelog = path.join(dir, "CHANGELOG.md");
    fs.writeFileSync(changelog, SAMPLE, "utf8");
    const result = runReleaseNotes(["--version", "3.1.0", "--changelog", changelog]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /Real feature for 3\.1\.0\./);
    assert.doesNotMatch(result.stdout, /oldest documented change/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("release-notes.js --footer appends the install footer with the version", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-notes-"));
  try {
    const changelog = path.join(dir, "CHANGELOG.md");
    fs.writeFileSync(changelog, SAMPLE, "utf8");
    const out = path.join(dir, "notes.md");
    const result = runReleaseNotes([
      "--version",
      "3.1.0",
      "--changelog",
      changelog,
      "--footer",
      "--out",
      out
    ]);
    assert.equal(result.status, 0, result.stderr);
    const notes = fs.readFileSync(out, "utf8");
    assert.match(notes, /Real feature for 3\.1\.0\./);
    assert.match(notes, /com\.wallstop-studios\.dxmessaging@3\.1\.0/);
    assert.match(notes, /## Install/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("release-notes.js exits non-zero for a missing version", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-notes-"));
  try {
    const changelog = path.join(dir, "CHANGELOG.md");
    fs.writeFileSync(changelog, SAMPLE, "utf8");
    const result = runReleaseNotes(["--version", "0.0.1", "--changelog", changelog]);
    assert.notEqual(result.status, 0);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
