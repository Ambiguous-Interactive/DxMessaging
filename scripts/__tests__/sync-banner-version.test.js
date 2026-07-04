"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  VERSION_PATTERN,
  TEST_COUNT_PATTERN,
  analyzeBanner,
  stripSourceComments,
  countTestMarkers,
  roundTestCount,
  getVersionBadge,
  readPackageVersion,
  syncBanner,
  validateBanner
} = require("../sync-banner-version.js");

test("roundTestCount floors to the nearest hundred but never to zero", () => {
  for (const [input, expected] of [
    [1234, 1200],
    [100, 100],
    [199, 100],
    [99, 99],
    [0, 0]
  ]) {
    assert.equal(roundTestCount(input), expected);
  }
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
  const svgText = '<text data-sync="test-count" x="176" y="18" fill="#22d3ee">500+ Tests</text>';
  const match = svgText.match(TEST_COUNT_PATTERN);
  assert.ok(match);
  assert.equal(match[2], "500+ Tests");
  assert.ok(!'<text x="176" y="18" fill="#22d3ee">500+ Tests</text>'.match(TEST_COUNT_PATTERN));
});

test("real README banner exposes sync anchors", () => {
  const repoRoot = path.resolve(__dirname, "..", "..");
  const result = analyzeBanner({ repoRoot });
  assert.equal(result.currentVersion, readPackageVersion(path.join(repoRoot, "package.json")));
  assert.equal(result.currentTestCountLabel, result.testCountLabel);
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

test("syncBanner updates the SVG badge, never stages, and is idempotent", () => {
  const repoRoot = fs.mkdtempSync(path.join(os.tmpdir(), "banner-repo-"));
  try {
    fs.writeFileSync(path.join(repoRoot, "package.json"), '{"version":"9.9.9"}', "utf8");
    const testsDir = path.join(repoRoot, "Tests");
    fs.mkdirSync(testsDir, { recursive: true });
    fs.writeFileSync(
      path.join(testsDir, "SampleTests.cs"),
      "[Test]\npublic void One() {}\n[Test]\npublic void Two() {}\n",
      "utf8"
    );
    const docsTestsDir = path.join(repoRoot, ".docs-tests");
    fs.mkdirSync(docsTestsDir, { recursive: true });
    fs.writeFileSync(
      path.join(docsTestsDir, "DocsSnippetCompilationTests.cs"),
      "[Test]\npublic void DocsOne() {}\n",
      "utf8"
    );
    const svgPath = path.join(repoRoot, "docs", "images", "DxMessaging-banner.svg");
    fs.mkdirSync(path.dirname(svgPath), { recursive: true });
    const svg = `<svg>${getVersionBadge("0.0.1")}\n<text data-sync="test-count">900+ Tests</text></svg>`;
    fs.writeFileSync(svgPath, svg, "utf8");

    const first = syncBanner({ repoRoot });
    assert.equal(first.updated, true);
    assert.equal(first.version, "9.9.9");
    assert.equal(first.testCount, 3);
    assert.equal(first.testCountLabel, "3+ Tests");

    const updatedSvg = fs.readFileSync(svgPath, "utf8");
    assert.ok(updatedSvg.includes(">v9.9.9</text>"));
    assert.ok(updatedSvg.includes("3+ Tests"));

    const second = syncBanner({ repoRoot });
    assert.equal(second.updated, false);
  } finally {
    fs.rmSync(repoRoot, { recursive: true, force: true });
  }
});

test("validateBanner requires the current rounded test-count label", () => {
  const repoRoot = fs.mkdtempSync(path.join(os.tmpdir(), "banner-check-"));
  try {
    fs.writeFileSync(path.join(repoRoot, "package.json"), '{"version":"1.2.3"}', "utf8");
    const testsDir = path.join(repoRoot, "Tests");
    fs.mkdirSync(testsDir, { recursive: true });
    fs.writeFileSync(
      path.join(testsDir, "SampleTests.cs"),
      Array.from({ length: 123 }, (_, index) => `[Test]\npublic void T${index}() {}`).join("\n"),
      "utf8"
    );
    const svgPath = path.join(repoRoot, "docs", "images", "DxMessaging-banner.svg");
    fs.mkdirSync(path.dirname(svgPath), { recursive: true });
    const writeSvg = (label) =>
      fs.writeFileSync(
        svgPath,
        `<svg>${getVersionBadge("1.2.3")}<text data-sync="test-count">${label}</text></svg>`,
        "utf8"
      );

    for (const { label, ok, error } of [
      { label: "100+ Tests", ok: true },
      { label: "99+ Tests", ok: false, error: /expected 100\+ Tests.*is stale/ },
      { label: "124+ Tests", ok: false, error: /expected 100\+ Tests.*overstates/ }
    ]) {
      writeSvg(label);
      const result = validateBanner({ repoRoot });
      assert.equal(result.ok, ok, label);
      if (error) {
        assert.match(result.errors.join("\n"), error);
      } else {
        assert.deepEqual(result.errors, []);
      }
    }
  } finally {
    fs.rmSync(repoRoot, { recursive: true, force: true });
  }
});
