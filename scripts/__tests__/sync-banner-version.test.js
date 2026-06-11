"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  VERSION_PATTERN,
  TEST_COUNT_PATTERN,
  stripSourceComments,
  countTestMarkers,
  roundTestCount,
  getVersionBadge,
  readPackageVersion,
  syncBanner
} = require("../sync-banner-version.js");

test("roundTestCount floors to the nearest hundred but never to zero", () => {
  assert.equal(roundTestCount(1234), 1200);
  assert.equal(roundTestCount(100), 100);
  assert.equal(roundTestCount(199), 100);
  assert.equal(roundTestCount(99), 99);
  assert.equal(roundTestCount(0), 0);
});

test("stripSourceComments removes comments but keeps URLs", () => {
  const source = 'int x = 1; // trailing\n/* block\ncomment */\nstring u = "https://a.b";';
  const stripped = stripSourceComments(source);
  assert.ok(!stripped.includes("trailing"));
  assert.ok(!stripped.includes("comment"));
  assert.ok(stripped.includes("https://a.b"));
});

test("countTestMarkers counts C# test attributes outside comments", () => {
  const csharp = [
    "[Test]",
    "public void First() {}",
    "// [Test] commented out",
    "[UnityTest]",
    "public IEnumerator Second() { yield break; }",
    "[TestCase(1)]",
    "public void Third(int x) {}"
  ].join("\n");
  assert.equal(countTestMarkers("Foo/BarTests.cs", csharp), 3);
  assert.equal(countTestMarkers("Foo/bar.js", csharp), 0);
});

test("getVersionBadge output matches VERSION_PATTERN and embeds the version", () => {
  const badge = getVersionBadge("1.2.3");
  assert.ok(badge.includes(">v1.2.3</text>"));
  assert.ok(VERSION_PATTERN.test(badge));
});

test("TEST_COUNT_PATTERN matches the banner test-count text element", () => {
  const svgText = '<text x="20" y="13" fill="#00d9ff" font-size="10">500+ Tests</text>';
  const match = svgText.match(TEST_COUNT_PATTERN);
  assert.ok(match);
  assert.equal(match[2], "500+ Tests");
  assert.ok(!'<text x="21" y="13" fill="#00d9ff">500+ Tests</text>'.match(TEST_COUNT_PATTERN));
});

test("readPackageVersion returns semver and rejects invalid versions", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "banner-test-"));
  const good = path.join(dir, "package.json");
  fs.writeFileSync(good, JSON.stringify({ version: "1.4.2" }), "utf8");
  assert.equal(readPackageVersion(good), "1.4.2");

  const bad = path.join(dir, "bad.json");
  fs.writeFileSync(bad, JSON.stringify({ version: "not-a-version" }), "utf8");
  assert.throws(() => readPackageVersion(bad), /Invalid version format/);
  fs.rmSync(dir, { recursive: true, force: true });
});

test("syncBanner updates the SVG badge, never stages, and is idempotent", () => {
  const repoRoot = fs.mkdtempSync(path.join(os.tmpdir(), "banner-repo-"));
  try {
    fs.writeFileSync(
      path.join(repoRoot, "package.json"),
      JSON.stringify({ version: "9.9.9" }),
      "utf8"
    );
    const testsDir = path.join(repoRoot, "Tests");
    fs.mkdirSync(testsDir, { recursive: true });
    fs.writeFileSync(
      path.join(testsDir, "SampleTests.cs"),
      "[Test]\npublic void One() {}\n[Test]\npublic void Two() {}\n",
      "utf8"
    );
    const svgPath = path.join(repoRoot, "docs", "images", "DxMessaging-banner.svg");
    fs.mkdirSync(path.dirname(svgPath), { recursive: true });
    const svg = `<svg>${getVersionBadge("0.0.1")}\n<text x="20" y="13" fill="#00d9ff">900+ Tests</text></svg>`;
    fs.writeFileSync(svgPath, svg, "utf8");

    const first = syncBanner({ repoRoot });
    assert.equal(first.updated, true);
    assert.equal(first.version, "9.9.9");
    assert.equal(first.testCount, 2);
    assert.equal(first.testCountLabel, "2+ Tests");

    const updatedSvg = fs.readFileSync(svgPath, "utf8");
    assert.ok(updatedSvg.includes(">v9.9.9</text>"));
    assert.ok(updatedSvg.includes("2+ Tests"));

    // The temp repoRoot is not a git repo: a leftover internal `git add`
    // would have thrown before we got here.
    const second = syncBanner({ repoRoot });
    assert.equal(second.updated, false);
  } finally {
    fs.rmSync(repoRoot, { recursive: true, force: true });
  }
});
