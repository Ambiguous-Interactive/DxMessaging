#!/usr/bin/env node
/**
 * Cross-platform banner sync.
 * Keeps docs/images/DxMessaging-banner.svg aligned with package.json version.
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

const VERSION_PATTERN =
  /(<text\b(?=[^>]*\bdata-sync="version")[^>]*>v)(\d+\.\d+\.\d+[^<]*)(<\/text>)/;
const VERSION_VALUE_PATTERN =
  /<text\b(?=[^>]*\bdata-sync="version")[^>]*>v(\d+\.\d+\.\d+[^<]*)<\/text>/;

// Self-contained version badge. Only syncBanner's in-place regex replace mutates
// the shipped banner; this helper exists purely so tests can assert VERSION_PATTERN
// against a standalone fragment, so it carries its own group transform.
function getVersionBadge(version) {
  return `<!-- Version text must contain vX.Y.Z for version sync. -->
  <g transform="translate(166 0)" font-family="'IBM Plex Sans', 'Segoe UI', sans-serif">
    <rect x="0" y="0" width="76" height="28" rx="8" fill="#0c1016" stroke="#232c38" stroke-width="1"/>
    <text data-sync="version" x="38" y="18" text-anchor="middle" font-family="'JetBrains Mono', 'SF Mono', monospace" font-size="11" font-weight="800" fill="#e9eef4">v${version}</text>
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

  if (analysis.currentVersion === analysis.version) {
    return { updated: false, version: analysis.version };
  }

  const updatedSvg = analysis.svgContent.replace(
    VERSION_PATTERN,
    (_whole, prefix, _oldVersion, suffix) => `${prefix}${analysis.version}${suffix}`
  );

  fs.writeFileSync(analysis.svgPath, updatedSvg, "utf8");

  return { updated: true, version: analysis.version };
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

  const svgContent = fs.readFileSync(svgPath, "utf8");
  if (!VERSION_PATTERN.test(svgContent)) {
    throw new Error(`Could not find version pattern in: ${svgPath}`);
  }

  const currentVersionMatch = svgContent.match(VERSION_VALUE_PATTERN);
  const currentVersion = currentVersionMatch?.[1];

  return {
    currentVersion,
    packageJsonPath,
    repoRoot,
    svgContent,
    svgPath,
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
      return;
    }

    const result = syncBanner();
    if (!result.updated) {
      console.log(`Banner already has correct version: v${result.version}`);
      return;
    }

    console.log(`Updated banner version to: v${result.version}`);
  } catch (error) {
    console.error(`Failed to ${checkMode ? "validate" : "sync"} banner: ${error.message}`);
    process.exit(1);
  }
}

// Export only the symbols consumed externally
// (scripts/__tests__/sync-banner-version.test.js).
module.exports = {
  VERSION_PATTERN,
  analyzeBanner,
  getVersionBadge,
  readPackageVersion,
  syncBanner,
  validateBanner
};

if (require.main === module) {
  main();
}
