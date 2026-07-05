"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  VERSION_PATTERN,
  analyzeBanner,
  getVersionBadge,
  readPackageVersion,
  syncBanner,
  validateBanner
} = require("../sync-banner-version.js");

test("getVersionBadge output matches VERSION_PATTERN and embeds the version", () => {
  const badge = getVersionBadge("1.2.3");
  assert.ok(badge.includes(">v1.2.3</text>"));
  assert.ok(VERSION_PATTERN.test(badge));
});

test("real README banner exposes the version sync anchor", () => {
  const repoRoot = path.resolve(__dirname, "..", "..");
  const result = analyzeBanner({ repoRoot });
  assert.equal(result.currentVersion, readPackageVersion(path.join(repoRoot, "package.json")));
  assert.ok(result.svgContent.includes("DxKit-family amber DxMessaging banner"));
  assert.ok(result.svgContent.includes("Decoupled, simple systems."));
  assert.ok(result.svgContent.includes("#f4a836"));
  assert.ok(!result.svgContent.includes("Hanken Grotesk"));
  assert.ok(!result.svgContent.includes("#6d5ef6"));
});

test("readPackageVersion returns semver and rejects invalid versions", (t) => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "banner-test-"));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const good = path.join(dir, "package.json");
  fs.writeFileSync(good, JSON.stringify({ version: "1.4.2" }), "utf8");
  assert.equal(readPackageVersion(good), "1.4.2");

  const bad = path.join(dir, "bad.json");
  fs.writeFileSync(bad, JSON.stringify({ version: "not-a-version" }), "utf8");
  assert.throws(() => readPackageVersion(bad), /Invalid version format/);
});

test("syncBanner updates the SVG version badge, never stages, and is idempotent", () => {
  const repoRoot = fs.mkdtempSync(path.join(os.tmpdir(), "banner-repo-"));
  try {
    fs.writeFileSync(path.join(repoRoot, "package.json"), '{"version":"9.9.9"}', "utf8");
    const svgPath = path.join(repoRoot, "docs", "images", "DxMessaging-banner.svg");
    fs.mkdirSync(path.dirname(svgPath), { recursive: true });
    fs.writeFileSync(svgPath, `<svg>${getVersionBadge("0.0.1")}</svg>`, "utf8");

    const first = syncBanner({ repoRoot });
    assert.equal(first.updated, true);
    assert.equal(first.version, "9.9.9");

    const updatedSvg = fs.readFileSync(svgPath, "utf8");
    assert.ok(updatedSvg.includes(">v9.9.9</text>"));

    const second = syncBanner({ repoRoot });
    assert.equal(second.updated, false);
  } finally {
    fs.rmSync(repoRoot, { recursive: true, force: true });
  }
});

test("validateBanner flags a stale version badge and passes when current", () => {
  const repoRoot = fs.mkdtempSync(path.join(os.tmpdir(), "banner-check-"));
  try {
    fs.writeFileSync(path.join(repoRoot, "package.json"), '{"version":"1.2.3"}', "utf8");
    const svgPath = path.join(repoRoot, "docs", "images", "DxMessaging-banner.svg");
    fs.mkdirSync(path.dirname(svgPath), { recursive: true });
    const writeSvg = (version) =>
      fs.writeFileSync(svgPath, `<svg>${getVersionBadge(version)}</svg>`, "utf8");

    writeSvg("1.2.3");
    assert.deepEqual(validateBanner({ repoRoot }).errors, []);

    writeSvg("1.0.0");
    const stale = validateBanner({ repoRoot });
    assert.equal(stale.ok, false);
    assert.match(stale.errors.join("\n"), /version badge is v1\.0\.0; expected v1\.2\.3/);
  } finally {
    fs.rmSync(repoRoot, { recursive: true, force: true });
  }
});
