"use strict";
const { test } = require("node:test");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
// prettier-ignore
const { parseArgs, stageAssetStoreSubmission } = require("../release/asset-store-submission.js");
function sha256(file) {
  return crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
}
function write(file, content = "") {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, content);
  return file;
}
// prettier-ignore
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-asset-store-"));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  write(path.join(root, "package.json"), '{"name":"com.example.fixture","version":"1.2.3","_upm":{"changelog":"### Added\\n\\n- Release note."}}');
  write(path.join(root, "CHANGELOG.md"), "# Changelog\n\n## [1.2.3]\n\n### Added\n\n- Release note.\n");
  fs.cpSync(path.resolve(__dirname, "../../docs/images"), path.join(root, "docs", "images"), { recursive: true });
  const packageFile = write(path.join(root, "release", "fixture-1.2.3.tgz"), "tgz");
  const unitypackageFile = write(path.join(root, "release", "fixture-1.2.3.unitypackage"), "unitypackage");
  const packageChecksum = write(`${packageFile}.sha256`, `${sha256(packageFile)}  ${path.basename(packageFile)}\n`);
  const unitypackageChecksum = write(`${unitypackageFile}.sha256`, `${sha256(unitypackageFile)}  ${path.basename(unitypackageFile)}\n`);
  return { root, packageFile, packageChecksum, unitypackageFile, unitypackageChecksum };
}
function stage(data, options = {}) {
  return stageAssetStoreSubmission({
    ...data,
    repoRoot: data.root,
    outDir: ".artifacts/asset-store-submission",
    ...options
  });
}
test("stageAssetStoreSubmission creates a verified operator artifact", (t) => {
  const data = fixture(t);
  const result = stage(data);
  const output = result.outDir;
  assert.equal(result.version, "1.2.3");
  const classic = fs.readFileSync(path.join(output, "CLASSIC-UPLOAD-CHECKLIST.md"), "utf8");
  const upm = fs.readFileSync(path.join(output, "UPM-UPLOAD-CHECKLIST.md"), "utf8");
  assert.match(
    classic,
    /Unity Console[\s\S]*Tools > Asset Store > Validator[\s\S]*Tools > Asset Store > Uploader/
  );
  assert.match(
    upm,
    /Add package from tarball[\s\S]*Window > Tools > Asset Store > Validator[\s\S]*UPM Packages/
  );
  assert.deepEqual(JSON.parse(fs.readFileSync(path.join(output, "EXPECTED-UPM-FIELDS.json"))), {
    name: "com.example.fixture",
    version: "1.2.3",
    _upm: { changelog: "### Added\n\n- Release note." }
  });
  const manifest = JSON.parse(fs.readFileSync(path.join(output, "MANIFEST.json")));
  assert.ok(manifest.files.some((file) => file.path === "EXPECTED-UPM-FIELDS.json"));
  for (const file of manifest.files) {
    const actual = path.join(output, file.path);
    assert.equal(file.bytes, fs.statSync(actual).size, file.path);
    assert.equal(file.sha256, sha256(actual), file.path);
  }
});
test("stageAssetStoreSubmission rejects unsafe and inconsistent inputs", (t) => {
  const data = fixture(t);
  for (const outDir of [".", "docs", path.dirname(data.root)])
    assert.throws(() => stage(data, { outDir }), /Refusing unsafe output directory/);
  const outside = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-asset-store-outside-"));
  t.after(() => fs.rmSync(outside, { recursive: true, force: true }));
  fs.symlinkSync(outside, path.join(data.root, ".artifacts"), "dir");
  assert.throws(() => stage(data), /Refusing unsafe symlinked output path/);
  fs.rmSync(path.join(data.root, ".artifacts"));
  assert.throws(() => stage(data, { tag: "v9.9.9" }), /does not match package version/);
  write(data.packageChecksum, `${"0".repeat(64)}  ${path.basename(data.packageFile)}\n`);
  assert.throws(() => stage(data), /Checksum mismatch/);
});
test("stageAssetStoreSubmission rejects missing, corrupt, and stale collateral", (t) => {
  const missing = fixture(t);
  fs.rmSync(path.join(missing.root, "docs", "images", "dxmessaging-store-card-420x280.png"));
  assert.throws(() => stage(missing), /Store media is missing/);
  const corrupt = fixture(t);
  write(path.join(corrupt.root, "docs", "images", "dxmessaging-store-icon-320.png"), "junk");
  assert.throws(() => stage(corrupt), /lacks the required PNG structure/);
  const wrongSize = fixture(t);
  fs.copyFileSync(
    path.resolve(__dirname, "../../docs/images/dxmessaging-store-card-420x280.png"),
    path.join(wrongSize.root, "docs", "images", "dxmessaging-og-1200x630.png")
  );
  assert.throws(() => stage(wrongSize), /420x280; expected 1200x630/);
  const staleMedia = fixture(t);
  write(path.join(staleMedia.root, "docs", "images", "dxmessaging-og-1200x630.svg"), "stale");
  assert.throws(() => stage(staleMedia), /source\/output lock is stale/);
  const stale = fixture(t);
  const manifest = JSON.parse(fs.readFileSync(path.join(stale.root, "package.json")));
  manifest._upm.changelog = "stale";
  write(path.join(stale.root, "package.json"), JSON.stringify(manifest));
  assert.throws(() => stage(stale), /_upm\.changelog does not match/);
});
// prettier-ignore
test("Asset Store workflows stay quarantined and stage before npm publication", () => {
  const release = fs.readFileSync(path.resolve(__dirname, "../../.github/workflows/release.yml"), "utf8");
  const steps = ["Assemble the Asset Store", "Upload the Asset Store", "Publish to npm"];
  const positions = steps.map((step) => release.indexOf(`- name: ${step}`));
  assert.ok(positions[0] >= 0 && positions[0] < positions[1] && positions[1] < positions[2]);
  assert.match(release, /Upload the Asset Store[\s\S]*if-no-files-found: error[\s\S]*overwrite: true[\s\S]*retention-days: 30/);
  assert.match(release, /asset-store-submission\.js[\s\S]*--package-file[\s\S]*--package-checksum[\s\S]*--unitypackage-file[\s\S]*--unitypackage-checksum[\s\S]*--tag/);
  const research = fs.readFileSync(path.resolve(__dirname, "../../.github/workflows/asset-store-unsupported-upload-research.yml"), "utf8");
  assert.equal(research.match(/\$\{\{ inputs\.acknowledge_risks \}\}/g)?.length, 1);
  assert.match(research, /ACKNOWLEDGE_RISKS: \$\{\{ inputs\.acknowledge_risks \}\}/);
  assert.equal(research.match(/^            "https:/gm)?.length, 5);
  assert.match(research, /environment: asset-store-experimental[\s\S]*GITHUB_REF_TYPE[\s\S]*--max-time 15[\s\S]*grep -Fqi[\s\S]*FAILURES\.txt[\s\S]*test ! -s/);
  assert.match(research, /Upload research evidence\n\s+if: always\(\)[\s\S]*overwrite: true[\s\S]*Require complete official evidence/);
});
test("asset-store CLI rejects unknown and missing values", () => {
  assert.throws(() => parseArgs(["--unknown"]), /Unknown argument/);
  assert.throws(() => parseArgs(["--out", "--package-file"]), /Missing value/);
});
