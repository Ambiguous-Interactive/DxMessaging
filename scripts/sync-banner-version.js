#!/usr/bin/env node
/**
 * Cross-platform banner sync.
 * Keeps docs/images/DxMessaging-banner.svg aligned with package.json version
 * and rounded repository test count.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");

const VERSION_PATTERN =
  /<!-- Version badge \(top right\).*?-->\s*<g[^>]*>\s*<rect[^>]*\/>\s*<text[^>]*>v\d+\.\d+\.\d+[^<]*<\/text>\s*<\/g>/s;
const VERSION_VALUE_PATTERN = />v(\d+\.\d+\.\d+[^<]*)<\/text>/;
const TEST_COUNT_PATTERN =
  /(<text(?=[^>]*\bx="20")(?=[^>]*\by="13")(?=[^>]*\bfill="#00d9ff")[^>]*>)(\d+\+ Tests)(<\/text>)/;
const TEST_FILE_NAME_PATTERN = /(?:Test|Tests)\.cs$|\.(?:test|spec)\.js$/;
const TEST_ROOTS = ["Tests", "SourceGenerators", "scripts"];

function stripSourceComments(content) {
  return content.replace(/\/\*[\s\S]*?\*\//g, "").replace(/(^|[^:])\/\/.*$/gm, "$1");
}

function maskJavaScriptNonCode(content) {
  let result = "";
  let state = "code";
  let quote = "";
  let escaped = false;

  const mask = (char) => (char === "\n" || char === "\r" ? char : " ");

  for (let i = 0; i < content.length; ) {
    const char = content[i];
    const next = content[i + 1] ?? "";

    if (state === "code") {
      if (char === "/" && next === "/") {
        result += mask(char) + mask(next);
        i += 2;
        state = "line-comment";
        continue;
      }
      if (char === "/" && next === "*") {
        result += mask(char) + mask(next);
        i += 2;
        state = "block-comment";
        continue;
      }
      if (char === "'" || char === '"' || char === "`") {
        result += mask(char);
        quote = char;
        escaped = false;
        i++;
        state = "string";
        continue;
      }

      result += char;
      i++;
      continue;
    }

    if (state === "line-comment") {
      result += mask(char);
      i++;
      if (char === "\n" || char === "\r") {
        state = "code";
      }
      continue;
    }

    if (state === "block-comment") {
      result += mask(char);
      if (char === "*" && next === "/") {
        result += mask(next);
        i += 2;
        state = "code";
        continue;
      }
      i++;
      continue;
    }

    result += mask(char);
    i++;
    if (escaped) {
      escaped = false;
      continue;
    }
    if (char === "\\") {
      escaped = true;
      continue;
    }
    if (char === quote) {
      state = "code";
    }
  }

  return result;
}

function countTestMarkers(filePath, content) {
  if (filePath.endsWith(".cs")) {
    const source = stripSourceComments(content);
    return (source.match(/\[(?:UnityTest|Test|TestCase|TestCaseSource|Theory|Fact)\b/g) ?? [])
      .length;
  }

  if (/\.(?:test|spec)\.js$/.test(filePath)) {
    const source = maskJavaScriptNonCode(content);
    return (source.match(/(?<![\w.])(?:test|it)\s*\(/g) ?? []).length;
  }

  return 0;
}

function collectTestFiles(dir, results) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      collectTestFiles(fullPath, results);
      continue;
    }
    if (entry.isFile() && TEST_FILE_NAME_PATTERN.test(entry.name)) {
      results.push(fullPath);
    }
  }
}

function getRepositoryTestFiles(repoRoot) {
  const results = [];
  for (const relativeRoot of TEST_ROOTS) {
    const absoluteRoot = path.join(repoRoot, relativeRoot);
    if (!fs.existsSync(absoluteRoot)) {
      continue;
    }
    collectTestFiles(absoluteRoot, results);
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

function stageFile(repoRoot, filePath, execFileSyncFn = execFileSync) {
  try {
    execFileSyncFn("git", ["add", filePath], {
      cwd: repoRoot,
      stdio: ["ignore", "pipe", "pipe"]
    });
  } catch (error) {
    const stderr =
      typeof error?.stderr === "string"
        ? error.stderr.trim()
        : Buffer.isBuffer(error?.stderr)
          ? error.stderr.toString("utf8").trim()
          : "";
    const suffix = stderr.length > 0 ? ` ${stderr}` : "";
    throw new Error(`Failed to stage ${filePath} with git add.${suffix}`);
  }
}

function syncBanner(options = {}) {
  const repoRoot = options.repoRoot ?? path.resolve(__dirname, "..");
  const packageJsonPath = options.packageJsonPath ?? path.join(repoRoot, "package.json");
  const svgPath =
    options.svgPath ?? path.join(repoRoot, "docs", "images", "DxMessaging-banner.svg");
  const stageFileFn = options.stageFileFn ?? stageFile;

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
  stageFileFn(repoRoot, svgPath);

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

module.exports = {
  VERSION_PATTERN,
  TEST_COUNT_PATTERN,
  stripSourceComments,
  maskJavaScriptNonCode,
  countTestMarkers,
  getRepositoryTestFiles,
  calculateRepositoryTestCount,
  roundTestCount,
  getVersionBadge,
  readPackageVersion,
  stageFile,
  syncBanner
};

if (require.main === module) {
  main();
}
