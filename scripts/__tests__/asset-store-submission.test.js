"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const { parseArgs, stageAssetStoreSubmission } = require("../release/asset-store-submission.js");

function sha256(filePath) {
  return crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

function writeFile(filePath, content = "") {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, content);
  return filePath;
}

function makeFixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-asset-store-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  writeFile(
    path.join(root, "package.json"),
    JSON.stringify(
      {
        name: "com.example.fixture",
        version: "1.2.3",
        displayName: "Fixture Messaging",
        unity: "2021.3",
        description: "Fixture package.",
        documentationUrl: "https://example.test/docs",
        licensesUrl: "https://example.test/license"
      },
      null,
      2
    )
  );
  writeFile(
    path.join(root, "CHANGELOG.md"),
    ["# Changelog", "", "## [1.2.3]", "", "### Added", "", "- Release note.", ""].join("\n")
  );
  writeFile(path.join(root, "STORE-LISTING.md"), "# Fixture Store Listing\n\nDraft copy.\n");
  for (const name of "dxmessaging-store-icon-320.png dxmessaging-store-card-420x280.png dxmessaging-og-1200x630.png".split(
    " "
  )) {
    writeFile(path.join(root, "docs", "images", name), `media:${name}`);
  }
  const releaseDir = path.join(root, "release");
  const packageFile = writeFile(path.join(releaseDir, "fixture-1.2.3.tgz"), "tgz");
  const unitypackageFile = writeFile(
    path.join(releaseDir, "fixture-1.2.3.unitypackage"),
    "unitypackage"
  );
  const packageChecksum = writeFile(
    `${packageFile}.sha256`,
    `${sha256(packageFile)}  ${path.basename(packageFile)}\n`
  );
  const unitypackageChecksum = writeFile(
    `${unitypackageFile}.sha256`,
    `${sha256(unitypackageFile)}  ${path.basename(unitypackageFile)}\n`
  );
  return { root, packageFile, packageChecksum, unitypackageFile, unitypackageChecksum };
}

function stageFixture(fixture, outDir = ".artifacts/asset-store-submission") {
  return stageAssetStoreSubmission({ ...fixture, repoRoot: fixture.root, outDir });
}

test("parseArgs requires all source files and parses optional tag/output", () => {
  assert.deepEqual(
    parseArgs([
      "--out",
      "out",
      "--package-file",
      "pkg.tgz",
      "--package-checksum",
      "pkg.tgz.sha256",
      "--unitypackage-file",
      "pkg.unitypackage",
      "--unitypackage-checksum",
      "pkg.unitypackage.sha256",
      "--tag",
      "v1.2.3"
    ]),
    {
      outDir: "out",
      packageFile: "pkg.tgz",
      packageChecksum: "pkg.tgz.sha256",
      unitypackageFile: "pkg.unitypackage",
      unitypackageChecksum: "pkg.unitypackage.sha256",
      tag: "v1.2.3"
    }
  );
  assert.throws(() => parseArgs(["--out", "out"]), /Missing required arguments/);
});

test("stageAssetStoreSubmission copies assets, listing, checklists, and manifest", (t) => {
  const fixture = makeFixture(t);
  const outDir = path.join(fixture.root, ".artifacts", "asset-store-submission");

  const result = stageFixture(fixture, outDir);

  assert.equal(result.version, "1.2.3");
  assert.deepEqual(fs.readdirSync(outDir).sort(), [
    "CLASSIC-UPLOAD-CHECKLIST.md",
    "MANIFEST.json",
    "STORE-LISTING.md",
    "UPM-UPLOAD-CHECKLIST.md",
    "fixture-1.2.3.tgz",
    "fixture-1.2.3.tgz.sha256",
    "fixture-1.2.3.unitypackage",
    "fixture-1.2.3.unitypackage.sha256",
    "media"
  ]);
  assert.deepEqual(fs.readdirSync(path.join(outDir, "media")).sort(), [
    "dxmessaging-og-1200x630.png",
    "dxmessaging-store-card-420x280.png",
    "dxmessaging-store-icon-320.png"
  ]);
  const classic = fs.readFileSync(path.join(outDir, "CLASSIC-UPLOAD-CHECKLIST.md"), "utf8");
  assert.match(classic, /Fixture Messaging/);
  assert.match(classic, /fixture-1\.2\.3\.unitypackage/);
  assert.match(classic, /Release note\./);
  const upm = fs.readFileSync(path.join(outDir, "UPM-UPLOAD-CHECKLIST.md"), "utf8");
  assert.match(upm, /fixture-1\.2\.3\.tgz/);
  assert.match(upm, /Unity 2021\.3/);

  const manifest = JSON.parse(fs.readFileSync(path.join(outDir, "MANIFEST.json"), "utf8"));
  assert.equal(manifest.schemaVersion, 1);
  assert.equal(manifest.package.name, "com.example.fixture");
  assert.equal(manifest.package.version, "1.2.3");
  assert.ok(manifest.files.some((file) => file.path === "media/dxmessaging-store-icon-320.png"));
  assert.ok(manifest.files.every((file) => /^[0-9a-f]{64}$/.test(file.sha256)));
});

test("stageAssetStoreSubmission refuses unsafe output directories", (t) => {
  const fixture = makeFixture(t);
  for (const outDir of [".", "docs", path.dirname(fixture.root)]) {
    assert.throws(() => stageFixture(fixture, outDir), /Refusing unsafe output directory/);
  }

  const outside = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-asset-store-outside-"));
  t.after(() => fs.rmSync(outside, { recursive: true, force: true }));
  fs.symlinkSync(outside, path.join(fixture.root, ".artifacts"), "dir");
  assert.throws(() => stageFixture(fixture), /Refusing unsafe symlinked output path/);
});

test("stageAssetStoreSubmission rejects stale checksums and missing media", (t) => {
  const fixture = makeFixture(t);
  fs.writeFileSync(fixture.packageChecksum, `${"0".repeat(64)}  fixture-1.2.3.tgz\n`);
  assert.throws(() => stageFixture(fixture), /Checksum mismatch/);

  const second = makeFixture(t);
  fs.rmSync(path.join(second.root, "docs", "images", "dxmessaging-store-card-420x280.png"));
  assert.throws(() => stageFixture(second), /Required store media is missing/);
});
