#!/usr/bin/env node
/**
 * Cross-platform banner sync.
 * Keeps docs/images/DxMessaging-banner.svg aligned with package.json version
 * and the rounded C# test count (Tests/ + SourceGenerators/).
 *
 * This script only rewrites the SVG; it never stages anything. The pre-commit
 * hook relies on pre-commit's own modified-file detection, and CI
 * (validate-banner.yml) follows the sync with `git diff --exit-code`.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { walkFiles } = require("./lib/repo-files");

const VERSION_PATTERN =
  /<!-- Version badge \(top right\).*?-->\s*<g[^>]*>\s*<rect[^>]*\/>\s*<text[^>]*>v\d+\.\d+\.\d+[^<]*<\/text>\s*<\/g>/s;
const VERSION_VALUE_PATTERN = />v(\d+\.\d+\.\d+[^<]*)<\/text>/;
const TEST_COUNT_PATTERN =
  /(<text(?=[^>]*\bx="20")(?=[^>]*\by="13")(?=[^>]*\bfill="#00d9ff")[^>]*>)(\d+\+ Tests)(<\/text>)/;
const TEST_FILE_NAME_PATTERN = /(?:Test|Tests)\.cs$/;
const TEST_ROOTS = ["Tests", "SourceGenerators"];

function stripSourceComments(content) {
  return content.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
}

function countTestMarkers(filePath, content) {
  if (!filePath.endsWith(".cs")) {
    return 0;
  }
  const source = stripSourceComments(content);
  return (source.match(/\[(?:UnityTest|Test|TestCase|TestCaseSource|Theory|Fact)\b/g) ?? [])
    .length;
}

function getRepositoryTestFiles(repoRoot) {
  const results = [];
  for (const relativeRoot of TEST_ROOTS) {
    const absoluteRoot = path.join(repoRoot, relativeRoot);
    if (!fs.existsSync(absoluteRoot)) {
      continue;
    }
    results.push(
      ...walkFiles(absoluteRoot, {
        match: (fullPath, entry) => TEST_FILE_NAME_PATTERN.test(entry.name),
        // Preserve the prior fail-hard behavior for unreadable directories.
        onError: (error) => {
          throw error;
        }
      })
    );
  }
  return results;
}

function calculateRepositoryTestCount(repoRoot) {
  return getRepositoryTestFiles(repoRoot).reduce(
    (sum, filePath) => sum + countTestMarkers(filePath, fs.readFileSync(filePath, "utf8")),
    0
  );
}

function roundTestCount(testCount) {
  const rounded = Math.floor(testCount / 100) * 100;
  return rounded < 1 ? testCount : rounded;
}

function getVersionBadge(version) {
  return `<!-- Version badge (top right) - text must contain vX.Y.Z for version sync -->
  <g transform="translate(720, 18)">
    <rect x="0" y="0" width="62" height="22" rx="11" ry="11" fill="#e94560" opacity="0.95" filter="url(#softShadow)"/>
    <text x="31" y="15" text-anchor="middle" font-family="'SF Mono', 'Fira Code', monospace" font-size="11" font-weight="700" fill="#ffffff" letter-spacing="0.5">v${version}</text>
  </g>`;
}

function readPackageVersion(packageJsonPath) {
  const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
  const version = packageJson?.version;
  if (typeof version !== "string" || !/^\d+\.\d+\.\d+/.test(version)) {
    throw new Error(
      `Invalid version format in package.json: ${String(version)} (expected semver X.Y.Z)`
    );
  }
  return version;
}

function syncBanner(options = {}) {
  const repoRoot = options.repoRoot ?? path.resolve(__dirname, "..");
  const packageJsonPath = options.packageJsonPath ?? path.join(repoRoot, "package.json");
  const svgPath =
    options.svgPath ?? path.join(repoRoot, "docs", "images", "DxMessaging-banner.svg");

  if (!fs.existsSync(packageJsonPath)) {
    throw new Error(`package.json not found at: ${packageJsonPath}`);
  }
  if (!fs.existsSync(svgPath)) {
    throw new Error(`SVG banner not found at: ${svgPath}`);
  }

  const version = readPackageVersion(packageJsonPath);
  const testCount = calculateRepositoryTestCount(repoRoot);
  const testCountLabel = `${roundTestCount(testCount)}+ Tests`;

  const svgContent = fs.readFileSync(svgPath, "utf8");
  if (!VERSION_PATTERN.test(svgContent)) {
    throw new Error(`Could not find version pattern in: ${svgPath}`);
  }
  if (!TEST_COUNT_PATTERN.test(svgContent)) {
    throw new Error(`Could not find test-count pattern in: ${svgPath}`);
  }

  const currentVersionMatch = svgContent.match(VERSION_VALUE_PATTERN);
  const currentTestCountMatch = svgContent.match(TEST_COUNT_PATTERN);

  if (currentVersionMatch?.[1] === version && currentTestCountMatch?.[2] === testCountLabel) {
    return {
      updated: false,
      version,
      testCount,
      testCountLabel
    };
  }

  let updatedSvg = svgContent.replace(VERSION_PATTERN, getVersionBadge(version));
  updatedSvg = updatedSvg.replace(
    TEST_COUNT_PATTERN,
    (_whole, prefix, _oldLabel, suffix) => `${prefix}${testCountLabel}${suffix}`
  );

  fs.writeFileSync(svgPath, updatedSvg, "utf8");

  return {
    updated: true,
    version,
    testCount,
    testCountLabel
  };
}

function main() {
  try {
    const result = syncBanner();
    if (!result.updated) {
      console.log(`Banner already has correct version: v${result.version}`);
      console.log(`Banner already has correct test count: ${result.testCountLabel}`);
      return;
    }

    console.log(`Updated banner version to: v${result.version}`);
    console.log(`Updated banner test count to: ${result.testCountLabel}`);
  } catch (error) {
    console.error(`Failed to sync banner: ${error.message}`);
    process.exit(1);
  }
}

// Export only the symbols consumed externally
// (scripts/__tests__/sync-banner-version.test.js).
module.exports = {
  VERSION_PATTERN,
  TEST_COUNT_PATTERN,
  stripSourceComments,
  countTestMarkers,
  roundTestCount,
  getVersionBadge,
  readPackageVersion,
  syncBanner
};

if (require.main === module) {
  main();
}
