#!/usr/bin/env node
/**
 * Prepare a release: bump package.json and rotate CHANGELOG.md.
 *
 * Given --bump major|minor|patch OR --version X.Y.Z (explicit version wins),
 * compute the next version, rewrite the package.json version line in place
 * (targeted string replace so the file's formatting is untouched), and move
 * the current `## [Unreleased]` content under a new undated `## [X.Y.Z]`
 * heading, leaving a fresh empty `## [Unreleased]` section on top.
 *
 * Refuses to run when the Unreleased section has no content. CHANGELOG.md is
 * written BEFORE package.json; when the changelog already carries the target
 * heading (a prior run crashed between the two writes), the rotation is
 * skipped and only the package.json bump is finished, so a re-run self-heals.
 * True duplicates stay refused: a completed --version re-run fails the
 * strictly-greater check, and a completed --bump re-run computes a fresh
 * version whose rotation trips the empty-Unreleased guard. --dry-run prints
 * the plan without writing.
 *
 * Usage:
 *   node scripts/release/prepare-release.js --bump patch [--dry-run]
 *   node scripts/release/prepare-release.js --version 3.1.0 [--dry-run]
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { normalizeToLf } = require("../lib/line-endings.js");
const {
  UNRELEASED_HEADING,
  computeFencedLineMask,
  changelogHasVersionHeading
} = require("./changelog.js");

// Leading zeros are rejected (npm/semver reject them at publish time; letting
// them through here would poison package.json, the changelog heading, the
// release branch, and the tag long before npm finally failed).
const SEMVER_PATTERN = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const BUMP_KINDS = ["major", "minor", "patch"];

function parseSemver(version) {
  const match = SEMVER_PATTERN.exec(String(version));
  if (!match) {
    throw new Error(
      `Invalid semver '${version}' (expected X.Y.Z with numeric components and no leading zeros).`
    );
  }
  return { major: Number(match[1]), minor: Number(match[2]), patch: Number(match[3]) };
}

function compareSemver(a, b) {
  const left = parseSemver(a);
  const right = parseSemver(b);
  return (
    left.major - right.major || left.minor - right.minor || left.patch - right.patch
  );
}

function computeNextVersion(currentVersion, { bump, version } = {}) {
  const current = parseSemver(currentVersion);
  if (version) {
    parseSemver(version);
    if (compareSemver(version, currentVersion) <= 0) {
      throw new Error(
        `Explicit version ${version} must be strictly greater than the current version ${currentVersion}.`
      );
    }
    return version;
  }
  switch (bump) {
    case "major":
      return `${current.major + 1}.0.0`;
    case "minor":
      return `${current.major}.${current.minor + 1}.0`;
    case "patch":
      return `${current.major}.${current.minor}.${current.patch + 1}`;
    default:
      throw new Error(`Unknown bump kind '${bump}' (expected ${BUMP_KINDS.join("|")}).`);
  }
}

function replacePackageVersion(rawPackageJson, currentVersion, nextVersion) {
  const needle = `"version": "${currentVersion}"`;
  const occurrences = rawPackageJson.split(needle).length - 1;
  if (occurrences !== 1) {
    throw new Error(
      `Expected exactly one '${needle}' in package.json but found ${occurrences}; refusing the targeted replace.`
    );
  }
  const updated = rawPackageJson.replace(needle, `"version": "${nextVersion}"`);
  const reparsed = JSON.parse(updated);
  if (reparsed.version !== nextVersion) {
    throw new Error(
      `package.json rewrite verification failed: parsed version is '${reparsed.version}', expected '${nextVersion}'.`
    );
  }
  return updated;
}

/**
 * Move the Unreleased block under a new `## [version]` heading.
 *
 * The block is moved verbatim (so the existing subsection order -- Added,
 * Changed, Deprecated, Removed, Fixed, Security, as present -- is preserved),
 * with leading/trailing blank lines trimmed and single blank lines around the
 * inserted headings (matches prettier/markdownlint conventions). Lines inside
 * fenced code blocks are never treated as headings.
 */
function rotateChangelog(content, nextVersion) {
  const lines = normalizeToLf(content).split("\n");
  const fenced = computeFencedLineMask(lines);
  const versionHeading = `## [${nextVersion}]`;

  if (lines.some((line, index) => line === versionHeading && !fenced[index])) {
    throw new Error(
      `CHANGELOG.md already contains the heading '${versionHeading}'; refusing to rotate twice.`
    );
  }
  const unreleasedIndexes = lines.flatMap((line, index) =>
    line === UNRELEASED_HEADING && !fenced[index] ? [index] : []
  );
  if (unreleasedIndexes.length !== 1) {
    throw new Error(
      `CHANGELOG.md must contain exactly one '${UNRELEASED_HEADING}' heading (found ${unreleasedIndexes.length}).`
    );
  }
  const unreleasedIndex = unreleasedIndexes[0];

  let nextHeadingIndex = lines.length;
  for (let index = unreleasedIndex + 1; index < lines.length; index += 1) {
    if (lines[index].startsWith("## ") && !fenced[index]) {
      nextHeadingIndex = index;
      break;
    }
  }

  const block = lines.slice(unreleasedIndex + 1, nextHeadingIndex);
  while (block.length > 0 && block[0].trim() === "") {
    block.shift();
  }
  while (block.length > 0 && block[block.length - 1].trim() === "") {
    block.pop();
  }
  const hasContent = block.some((line) => line.trim() !== "" && !line.startsWith("### "));
  if (!hasContent) {
    throw new Error(
      "The '## [Unreleased]' section has no content to release; add changelog entries before preparing a release."
    );
  }

  const rotated = [
    ...lines.slice(0, unreleasedIndex + 1),
    "",
    versionHeading,
    "",
    ...block,
    ...(nextHeadingIndex < lines.length ? ["", ...lines.slice(nextHeadingIndex)] : [""])
  ];
  return `${rotated.join("\n").replace(/\n+$/, "")}\n`;
}

function prepareRelease({ repoRoot, bump, version, dryRun = false } = {}) {
  const root = repoRoot ?? path.resolve(__dirname, "..", "..");
  const packageJsonPath = path.join(root, "package.json");
  const changelogPath = path.join(root, "CHANGELOG.md");

  const rawPackageJson = fs.readFileSync(packageJsonPath, "utf8");
  const currentVersion = JSON.parse(rawPackageJson).version;
  parseSemver(currentVersion);

  const nextVersion = computeNextVersion(currentVersion, { bump, version });
  const updatedPackageJson = replacePackageVersion(rawPackageJson, currentVersion, nextVersion);
  const rawChangelog = fs.readFileSync(changelogPath, "utf8");
  // Resume path: package.json still holds the OLD version (computeNextVersion
  // succeeded above) but CHANGELOG.md already carries the target heading --
  // the state a crash between the changelog and package.json writes leaves
  // behind. Skip the rotation and finish the package.json bump.
  const changelogAlreadyRotated = changelogHasVersionHeading(rawChangelog, nextVersion);
  const updatedChangelog = changelogAlreadyRotated
    ? null
    : rotateChangelog(rawChangelog, nextVersion);

  if (!dryRun) {
    // CHANGELOG.md first: a crash between the two writes leaves the
    // self-healing resume state above instead of a bumped package.json whose
    // changelog rotation never happened (which no re-run could repair).
    if (!changelogAlreadyRotated) {
      fs.writeFileSync(changelogPath, updatedChangelog, "utf8");
    }
    fs.writeFileSync(packageJsonPath, updatedPackageJson, "utf8");
  }

  return {
    currentVersion,
    nextVersion,
    dryRun,
    packageJsonPath,
    changelogPath,
    changelogAlreadyRotated
  };
}

function parseArgs(argv) {
  const options = { bump: undefined, version: undefined, dryRun: false };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--bump" || arg === "--version") {
      const value = argv[index + 1];
      if (value === undefined) {
        throw new Error(`Missing value for ${arg}.`);
      }
      index += 1;
      if (arg === "--bump") {
        options.bump = value;
      } else {
        options.version = value;
      }
    } else if (arg === "--dry-run") {
      options.dryRun = true;
    } else {
      throw new Error(`Unknown argument '${arg}'. Expected --bump, --version, or --dry-run.`);
    }
  }
  if (!options.version && !BUMP_KINDS.includes(options.bump)) {
    throw new Error(
      `Pass --version X.Y.Z or --bump ${BUMP_KINDS.join("|")} (explicit --version wins when both are set).`
    );
  }
  return options;
}

function main() {
  try {
    const options = parseArgs(process.argv.slice(2));
    const result = prepareRelease(options);
    console.log(`prepare-release: ${result.currentVersion} -> ${result.nextVersion}`);
    if (result.dryRun) {
      console.log("prepare-release: dry run; no files were written.");
      return;
    }
    console.log(`prepare-release: rewrote ${result.packageJsonPath}`);
    if (result.changelogAlreadyRotated) {
      console.log(
        `prepare-release: ${result.changelogPath} already had the '## [${result.nextVersion}]' heading; left untouched.`
      );
    } else {
      console.log(`prepare-release: rotated ${result.changelogPath}`);
    }
  } catch (error) {
    console.error(`prepare-release failed: ${error.message}`);
    process.exit(1);
  }
}

module.exports = {
  computeNextVersion,
  replacePackageVersion,
  rotateChangelog,
  prepareRelease,
  parseArgs
};

if (require.main === module) {
  main();
}
