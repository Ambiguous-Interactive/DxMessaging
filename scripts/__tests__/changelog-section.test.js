"use strict";

// Release publication and unit coverage for the shared changelog-section extractor
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
const YAML = require("yaml");

const { extractSection } = require("../release/changelog.js");

// Execute the shipped shell; only GitHub transport is replaced by a local fixture.
const RELEASE_API_STUB = `
const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2);
const state = JSON.parse(fs.readFileSync('state.json', 'utf8'));
state.calls.push(args);
const fail = (message) => { console.error(message); process.exitCode = 1; };
const output = (value) => process.stdout.write(JSON.stringify(value));
const endpoint = args.find((arg) => arg.startsWith('repos/')) || '';
if (args[0] === 'api' && endpoint.includes('?per_page=')) {
  if (state.failure === 'lookup' || !args.includes('--paginate') || !args.includes('--slurp')) { output([[]]); fail('incomplete lookup'); }
  else output([[{tag_name:'v0.0.1'}], state.release ? [state.release] : []]);
} else if (args[0] === 'api' && endpoint.endsWith('/releases/7')) output(state.release);
else if (args[0] === 'api' && endpoint.includes('/assets/')) {
  const asset = state.release.assets.find((asset) => asset.id === Number(endpoint.split('/').pop()));
  if (state.failure === 'download') fail('download failed');
  else process.stdout.write(asset.content);
} else if (args[0] === 'release' && args[1] === 'create') {
  if (!args.includes('--draft') || !args.includes('--verify-tag')) fail('unsafe create');
  else state.release = {id:7, tag_name:'v1.2.3', draft:true, assets:[]};
} else if (args[0] === 'release' && args[1] === 'upload') {
  if (state.release.draft !== true) fail('published assets overwritten');
  else if (state.failure === 'upload') fail('upload failed');
  else state.release.assets = args.slice(3).filter((arg) => !arg.startsWith('--')).map((file, i) =>
    ({id:i+1, name:path.basename(file), state:'uploaded', content:fs.readFileSync(file, 'utf8')}));
} else if (args[0] === 'release' && args[1] === 'edit') state.release.draft = false;
else fail('unexpected command: ' + JSON.stringify(args));
fs.writeFileSync('state.json', JSON.stringify(state));
`;

for (const scenario of [
  "new",
  "draft",
  "published",
  "immutable",
  "lookup",
  "upload",
  "download",
  "invalid-draft",
  "invalid-id",
  ...[0, 1, 2, 3].flatMap((index) => [`missing-${index}`, `corrupt-${index}`, `duplicate-${index}`])
]) {
  test(`release publication verifies bytes before publishing: ${scenario}`, (t) => {
    if (process.platform === "win32") return t.skip("Release publication runs on Ubuntu");
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-release-publish-"));
    try {
      const names = [
        "package.tgz",
        "package.tgz.sha256",
        "package.unitypackage",
        "package.unitypackage.sha256"
      ];
      const assets = names.map((name, index) => ({
        id: index + 1,
        name,
        state: "uploaded",
        content: `bytes-${index}\n`
      }));
      for (const asset of assets) fs.writeFileSync(path.join(root, asset.name), asset.content);
      const state = {
        calls: [],
        failure: scenario,
        release: { tag_name: "v1.2.3", draft: false, immutable: scenario === "immutable", assets }
      };
      state.release.id = scenario === "invalid-id" ? null : 7;
      if (scenario === "new") state.release = null;
      if (scenario === "draft" || scenario === "upload") state.release.draft = true;
      if (scenario === "invalid-draft") state.release.draft = "false";
      const [defect, index] = scenario.split("-");
      if (defect === "missing") assets.splice(Number(index), 1);
      if (defect === "corrupt") assets[Number(index)].content += "corruption";
      if (defect === "duplicate") assets.push({ ...assets[Number(index)], id: 99 });
      fs.writeFileSync(path.join(root, "state.json"), JSON.stringify(state));
      fs.writeFileSync(path.join(root, "github.cjs"), RELEASE_API_STUB);
      const workflow = YAML.parse(
        fs.readFileSync(path.join(__dirname, "../../.github/workflows/release.yml"), "utf8")
      );
      const steps = workflow.jobs.publish.steps;
      const step = steps.find((step) => step.name === "Create or update GitHub Release");
      assert.ok(steps.findIndex((item) => item.run?.includes("npm publish")) < steps.indexOf(step));
      const result = spawnSync(
        "bash",
        ["-c", 'gh() { "$RELEASE_TEST_NODE" github.cjs "$@"; }\n' + step.run],
        {
          cwd: root,
          encoding: "utf8",
          env: {
            ...process.env,
            RELEASE_TEST_NODE: process.execPath,
            GITHUB_REPOSITORY: "test/repo",
            RELEASE_TAG: "v1.2.3",
            ...Object.fromEntries(
              ["PACKAGE_FILE", "CHECKSUM_FILE", "UNITYPACKAGE_FILE", "UNITYPACKAGE_CHECKSUM"].map(
                (key, index) => [key, names[index]]
              )
            )
          }
        }
      );
      const finalState = JSON.parse(fs.readFileSync(path.join(root, "state.json"), "utf8"));
      const success = ["new", "draft", "published", "immutable"].includes(scenario);
      assert.equal(result.status === 0, success, `${scenario}: ${result.stdout}\n${result.stderr}`);
      const edits = finalState.calls.filter((call) => call[1] === "edit");
      assert.equal(edits.length, success ? 1 : 0, "failed verification must not publish or edit");
      if (success) {
        assert.equal(finalState.release.draft, false);
        assert.equal(
          finalState.calls.filter((call) => call.some((arg) => arg.includes("/assets/"))).length,
          4
        );
        assert.equal(finalState.calls.at(-1)[1], "edit", "all downloads precede publication");
      }
      if (!["new", "draft", "upload"].includes(scenario)) {
        assert.ok(
          !finalState.calls.some((call) => ["create", "upload"].includes(call[1])),
          "published reruns and failed lookups must not write assets"
        );
      }
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });
}

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
  const content = [
    "## [6.0.0]",
    "",
    "### Added",
    "",
    "### Fixed",
    "",
    "## [5.0.0]",
    "",
    "- x"
  ].join("\n");
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
  const unbracketed = ["# Changelog", "", "## 4.2.0", "", "- Plain heading entry.", ""].join("\n");
  assert.match(extractSection(unbracketed, "4.2.0"), /Plain heading entry\./);
});

// --- release-notes.js CLI: section + optional install footer ----------------

const RELEASE_NOTES_CLI = path.join(__dirname, "..", "release", "release-notes.js");

for (const mode of ["stdout", "footer-file", "missing-version"]) {
  test(`release-notes.js CLI: ${mode}`, () => {
    // A temporary changelog keeps these cases independent of released versions.
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-notes-"));
    try {
      const changelog = path.join(dir, "CHANGELOG.md");
      const out = path.join(dir, "notes.md");
      fs.writeFileSync(changelog, SAMPLE, "utf8");
      const version = mode === "missing-version" ? "0.0.1" : "3.1.0";
      const args = [RELEASE_NOTES_CLI, "--version", version, "--changelog", changelog];
      if (mode === "footer-file") args.push("--footer", "--out", out);
      const result = spawnSync(process.execPath, args, { encoding: "utf8" });
      assert.equal(result.status === 0, mode !== "missing-version", `${mode}: ${result.stderr}`);
      if (mode === "missing-version") return;
      const notes = mode === "footer-file" ? fs.readFileSync(out, "utf8") : result.stdout;
      assert.match(notes, /Real feature for 3\.1\.0\./, mode);
      assert.doesNotMatch(notes, /oldest documented change/, mode);
      if (mode === "footer-file") {
        assert.match(notes, /com\.wallstop-studios\.dxmessaging@3\.1\.0/, mode);
        assert.match(notes, /## Install/, mode);
      }
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });
}
