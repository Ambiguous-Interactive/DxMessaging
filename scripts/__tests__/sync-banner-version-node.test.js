/**
 * @fileoverview Tests for the Node banner sync fallback.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");

const { syncBanner, roundTestCount, readPackageVersion } = require("../sync-banner-version.js");

function buildBanner(version = "1.0.0", testLabel = "0+ Tests") {
  return `<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 800 200" width="800" height="200">
  <g transform="translate(342, 0)">
    <text x="20" y="13" fill="#00d9ff">${testLabel}</text>
  </g>
  <!-- Version badge (top right) - text must contain vX.Y.Z for version sync -->
  <g transform="translate(720, 18)">
    <rect x="0" y="0" width="62" height="22" rx="11" ry="11" fill="#e94560" opacity="0.95" filter="url(#softShadow)"/>
    <text x="31" y="15" text-anchor="middle" font-family="'SF Mono', 'Fira Code', monospace" font-size="11" font-weight="700" fill="#ffffff" letter-spacing="0.5">v${version}</text>
  </g>
</svg>`;
}

describe("sync-banner-version.js", () => {
  let tempDir;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "sync-banner-node-"));
    fs.mkdirSync(path.join(tempDir, "docs", "images"), { recursive: true });
    fs.mkdirSync(path.join(tempDir, "Tests"), { recursive: true });
    fs.mkdirSync(path.join(tempDir, "scripts"), { recursive: true });
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  test("syncBanner updates version, rounds test count, and stages the SVG", () => {
    const packageJsonPath = path.join(tempDir, "package.json");
    const svgPath = path.join(tempDir, "docs", "images", "DxMessaging-banner.svg");
    const testFilePath = path.join(tempDir, "Tests", "ExampleTests.cs");
    const stageFileFn = jest.fn();

    fs.writeFileSync(packageJsonPath, JSON.stringify({ version: "9.9.9" }, null, 2), "utf8");

    const testLines = [];
    for (let i = 0; i < 105; i++) {
      testLines.push("[Test]");
      testLines.push(`public void Case${i}() {}`);
    }
    fs.writeFileSync(testFilePath, testLines.join("\n"), "utf8");
    fs.writeFileSync(svgPath, buildBanner("1.0.0", "0+ Tests"), "utf8");

    const result = syncBanner({
      repoRoot: tempDir,
      packageJsonPath,
      svgPath,
      stageFileFn
    });

    expect(result.updated).toBe(true);
    expect(result.version).toBe("9.9.9");
    expect(result.testCount).toBe(105);
    expect(result.testCountLabel).toBe("100+ Tests");
    expect(stageFileFn).toHaveBeenCalledWith(tempDir, svgPath);

    const updatedSvg = fs.readFileSync(svgPath, "utf8");
    expect(updatedSvg).toContain(">v9.9.9</text>");
    expect(updatedSvg).toContain(">100+ Tests</text>");
  });

  test("syncBanner is a no-op when version and test label already match", () => {
    const packageJsonPath = path.join(tempDir, "package.json");
    const svgPath = path.join(tempDir, "docs", "images", "DxMessaging-banner.svg");
    const testFilePath = path.join(tempDir, "Tests", "SampleTests.cs");
    const stageFileFn = jest.fn();

    fs.writeFileSync(packageJsonPath, JSON.stringify({ version: "2.3.4" }, null, 2), "utf8");
    fs.writeFileSync(testFilePath, "[Test]\npublic void OnlyCase() {}\n", "utf8");
    fs.writeFileSync(svgPath, buildBanner("2.3.4", "1+ Tests"), "utf8");

    const before = fs.readFileSync(svgPath, "utf8");
    const result = syncBanner({
      repoRoot: tempDir,
      packageJsonPath,
      svgPath,
      stageFileFn
    });
    const after = fs.readFileSync(svgPath, "utf8");

    expect(result.updated).toBe(false);
    expect(stageFileFn).not.toHaveBeenCalled();
    expect(after).toBe(before);
  });

  test("readPackageVersion rejects invalid semver prefixes", () => {
    const packageJsonPath = path.join(tempDir, "package.json");
    fs.writeFileSync(packageJsonPath, JSON.stringify({ version: "v1" }, null, 2), "utf8");

    expect(() => readPackageVersion(packageJsonPath)).toThrow(/Invalid version format/);
  });

  test("roundTestCount rounds down to nearest hundred", () => {
    expect(roundTestCount(0)).toBe(0);
    expect(roundTestCount(1)).toBe(1);
    expect(roundTestCount(99)).toBe(99);
    expect(roundTestCount(100)).toBe(100);
    expect(roundTestCount(199)).toBe(100);
    expect(roundTestCount(2305)).toBe(2300);
    expect(roundTestCount(10000)).toBe(10000);
    expect(roundTestCount(10299)).toBe(10200);
    expect(roundTestCount(99999)).toBe(99900);
  });
});
