#!/usr/bin/env node
/**
 * Cross-platform banner sync.
 * Keeps docs/images/DxMessaging-banner.svg aligned with package.json version
 * and the rounded C# test count (Tests/ + SourceGenerators/).
 *
 * Usage:
 *   node scripts/sync-banner-version.js [--check]
 *
 * This script only rewrites the SVG; it never stages anything. The pre-commit
 * hook relies on pre-commit's own modified-file detection. Check mode validates
 * without mutating the working tree.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { walkFiles } = require("./lib/repo-files");

const VERSION_PATTERN =
  /(<text\b(?=[^>]*\bdata-sync="version")[^>]*>v)(\d+\.\d+\.\d+[^<]*)(<\/text>)/;
const VERSION_VALUE_PATTERN =
  /<text\b(?=[^>]*\bdata-sync="version")[^>]*>v(\d+\.\d+\.\d+[^<]*)<\/text>/;
const TEST_COUNT_PATTERN =
  /(<text\b(?=[^>]*\bdata-sync="test-count")[^>]*>)(\d+\+ Tests)(<\/text>)/;
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
  return (source.match(/\[(?:UnityTest|Test|TestCase|TestCaseSource|Theory|Fact)\b/g) ?? []).length;
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
  return `<!-- Version text must contain vX.Y.Z for version sync. -->
  <g transform="translate(244, 0)">
    <rect x="0" y="0" width="76" height="28" rx="8" fill="#14111e" stroke="#332c45" stroke-width="1"/>
    <text data-sync="version" x="38" y="18" text-anchor="middle" font-family="'JetBrains Mono', 'SF Mono', monospace" font-size="11" font-weight="800" fill="#f5f3fb">v${version}</text>
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
  const analysis = analyzeBanner(options);

  if (
    analysis.currentVersion === analysis.version &&
    analysis.currentTestCountLabel === analysis.testCountLabel
  ) {
    return {
      updated: false,
      version: analysis.version,
      testCount: analysis.testCount,
      testCountLabel: analysis.testCountLabel
    };
  }

  let updatedSvg = analysis.svgContent.replace(
    VERSION_PATTERN,
    (_whole, prefix, _oldVersion, suffix) => `${prefix}${analysis.version}${suffix}`
  );
  updatedSvg = updatedSvg.replace(
    TEST_COUNT_PATTERN,
    (_whole, prefix, _oldLabel, suffix) => `${prefix}${analysis.testCountLabel}${suffix}`
  );

  fs.writeFileSync(analysis.svgPath, updatedSvg, "utf8");

  return {
    updated: true,
    version: analysis.version,
    testCount: analysis.testCount,
    testCountLabel: analysis.testCountLabel
  };
}

function analyzeBanner(options = {}) {
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
  const currentVersion = currentVersionMatch?.[1];
  const currentTestCountLabel = currentTestCountMatch?.[2];
  const currentTestCount = Number.parseInt(currentTestCountLabel, 10);

  return {
    currentTestCount,
    currentTestCountLabel,
    currentVersion,
    packageJsonPath,
    repoRoot,
    svgContent,
    svgPath,
    testCount,
    testCountLabel,
    version
  };
}

function validateBanner(options = {}) {
  const analysis = analyzeBanner(options);
  const errors = [];

  if (analysis.currentVersion !== analysis.version) {
    errors.push(
      `${analysis.svgPath}: version badge is v${analysis.currentVersion}; expected v${analysis.version}.`
    );
  }

  if (analysis.currentTestCountLabel !== analysis.testCountLabel) {
    const reason =
      analysis.currentTestCount > analysis.testCount ? "overstates the marker count" : "is stale";
    errors.push(
      `${analysis.svgPath}: test-count badge is ${analysis.currentTestCountLabel}; expected ${analysis.testCountLabel} from ${analysis.testCount} test markers (${reason}).`
    );
  }

  return { ...analysis, errors, ok: errors.length === 0 };
}

function main() {
  const checkMode = process.argv.slice(2).includes("--check");
  try {
    if (checkMode) {
      const result = validateBanner();
      if (!result.ok) {
        console.error("ERROR: docs/images/DxMessaging-banner.svg is not valid:");
        result.errors.forEach((error) => console.error(`  - ${error}`));
        console.error("Run: npm run sync:banner");
        process.exit(1);
      }
      console.log(`Banner version is current: v${result.version}`);
      console.log(
        `Banner test count is current: ${result.testCountLabel} (${result.testCount} markers).`
      );
      return;
    }

    const result = syncBanner();
    if (!result.updated) {
      console.log(`Banner already has correct version: v${result.version}`);
      console.log(`Banner already has correct test count: ${result.testCountLabel}`);
      return;
    }

    console.log(`Updated banner version to: v${result.version}`);
    console.log(`Updated banner test count to: ${result.testCountLabel}`);
  } catch (error) {
    console.error(`Failed to ${checkMode ? "validate" : "sync"} banner: ${error.message}`);
    process.exit(1);
  }
}

// Export only the symbols consumed externally
// (scripts/__tests__/sync-banner-version.test.js).
module.exports = {
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
};

if (require.main === module) {
  main();
}
