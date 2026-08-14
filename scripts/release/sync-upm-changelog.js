#!/usr/bin/env node
"use strict";

/**
 * Mirror the shipped version's CHANGELOG.md section into package.json `_upm.changelog`.
 *
 * The Unity Package Manager renders that string in the Version History tab:
 * `PackageDetailsVersionHistoryItem.RefreshChangeLog` reads
 * `UpmCache.ParseUpmReserved(packageInfo)["changelog"]`, and
 * `PackageInfo.upmReserved` is populated from the resolved package's own
 * package.json. Unity's first-party packages ship the field the same way. It
 * therefore travels in the tarball (`npm pack` copies package.json verbatim)
 * and reaches every install path -- npm, OpenUPM, Git URL, .unitypackage --
 * regardless of what a registry does with the published manifest, which
 * matters because `npm publish` strips every `_`-prefixed key from the
 * metadata document it uploads.
 *
 * The value tracks package.json's own version, so it describes what a consumer
 * has installed. `--check` is the drift gate wired into `validate:all`; the
 * writer re-verifies its own output so the two can never disagree.
 *
 * Usage:
 *   node scripts/release/sync-upm-changelog.js [--check]
 *     [--package PATH] [--changelog PATH]
 */

const fs = require("fs");
const path = require("path");
const { extractSection } = require("./changelog.js");

const VALUE_FLAGS = ["--package", "--changelog"];

function parseArgs(argv) {
  const options = { check: false };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--check") {
      options.check = true;
    } else if (VALUE_FLAGS.includes(arg)) {
      const value = argv[index + 1];
      if (value === undefined || value.startsWith("--")) {
        throw new Error(`Missing value for ${arg}.`);
      }
      index += 1;
      options[arg.slice(2)] = value;
    } else {
      throw new Error(`Unknown argument '${arg}'.`);
    }
  }
  return options;
}

/**
 * @param {object} manifest - Parsed package.json.
 * @param {string} changelog - Raw CHANGELOG.md text.
 * @returns {object} A copy of the manifest whose `_upm.changelog` is the
 *   section for `manifest.version`. Other `_upm` keys are preserved.
 */
function applyUpmChangelog(manifest, changelog) {
  if (!manifest || typeof manifest.version !== "string" || manifest.version === "") {
    throw new Error("package.json has no version.");
  }
  const section = extractSection(changelog, manifest.version);
  const existing =
    manifest._upm && typeof manifest._upm === "object" ? manifest._upm : undefined;
  const updated = { ...manifest };
  delete updated._upm;
  updated._upm = { ...existing, changelog: section };
  return updated;
}

function serialize(manifest) {
  return `${JSON.stringify(manifest, null, 2)}\n`;
}

function run({ repoRoot, check, packagePath, changelogPath } = {}) {
  const root = repoRoot ?? path.resolve(__dirname, "..", "..");
  const pkgPath = packagePath ?? path.join(root, "package.json");
  const logPath = changelogPath ?? path.join(root, "CHANGELOG.md");
  const original = fs.readFileSync(pkgPath, "utf8");
  const changelog = fs.readFileSync(logPath, "utf8");
  const expected = serialize(applyUpmChangelog(JSON.parse(original), changelog));

  if (expected === original) {
    return { changed: false, version: JSON.parse(original).version };
  }
  if (check) {
    throw new Error(
      `${path.relative(root, pkgPath)} '_upm.changelog' is stale. ` +
        "Run `npm run sync:upm-changelog`."
    );
  }
  fs.writeFileSync(pkgPath, expected, "utf8");
  // Re-verify the written state so the writer can never leave `--check` failing.
  const written = fs.readFileSync(pkgPath, "utf8");
  if (written !== expected) {
    throw new Error(`Failed to write ${path.relative(root, pkgPath)}.`);
  }
  return { changed: true, version: JSON.parse(expected).version };
}

function main() {
  try {
    const options = parseArgs(process.argv.slice(2));
    const result = run({
      check: options.check,
      packagePath: options.package,
      changelogPath: options.changelog
    });
    if (options.check) {
      console.log(`[ok] package.json '_upm.changelog' matches ${result.version}.`);
    } else {
      console.log(
        result.changed
          ? `sync-upm-changelog: updated package.json for ${result.version}.`
          : `sync-upm-changelog: package.json already matches ${result.version}.`
      );
    }
  } catch (error) {
    console.error(`sync-upm-changelog failed: ${error.message}`);
    process.exit(1);
  }
}

module.exports = { parseArgs, applyUpmChangelog, serialize, run };

if (require.main === module) {
  main();
}
