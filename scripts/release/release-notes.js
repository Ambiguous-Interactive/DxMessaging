#!/usr/bin/env node
"use strict";

/**
 * Compose the GitHub Release body for a version.
 *
 * Prints (or writes) the matching CHANGELOG.md `## [version]` section, and with
 * --footer appends a short install footer (npm/UPM install line + the attached
 * asset list). This is the SINGLE entry point all release workflows call, so
 * the published body, the release-PR excerpt, and the release-drafter draft can
 * never diverge:
 *   - release.yml         : --version <tag-version> --footer --out <file>
 *   - release-prepare.yml : --version <new-version>
 *   - release-drafter.yml : --version Unreleased
 *
 * See .llm/skills/github-actions/release-asset-and-notes-invariants.md.
 *
 * Usage:
 *   node scripts/release/release-notes.js --version X.Y.Z [--footer] \
 *     [--changelog PATH] [--package PATH] [--out FILE]
 */

const fs = require("fs");
const path = require("path");
const { extractSection } = require("./changelog.js");

const VALUE_FLAGS = ["--version", "--changelog", "--package", "--out"];

function parseArgs(argv) {
  const options = { version: undefined, footer: false };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--footer") {
      options.footer = true;
    } else if (VALUE_FLAGS.includes(arg)) {
      const value = argv[index + 1];
      // Reject a flag-shaped value (e.g. `--version --footer`) instead of
      // silently consuming the next flag as the value.
      if (value === undefined || value.startsWith("--")) {
        throw new Error(`Missing value for ${arg}.`);
      }
      index += 1;
      options[arg.slice(2)] = value;
    } else {
      throw new Error(`Unknown argument '${arg}'.`);
    }
  }
  if (!options.version) {
    throw new Error("Pass --version <X.Y.Z|Unreleased>.");
  }
  return options;
}

function buildFooter(packageName, version) {
  return [
    "## Install",
    "",
    "- Import the attached `.unitypackage` directly into your Unity project, or",
    `- add \`${packageName}\` through the Unity Package Manager, pinning this`,
    `  release as \`${packageName}@${version}\` (scoped npm registry) or the`,
    "  matching Git tag.",
    "",
    "Each release attaches the npm tarball and the `.unitypackage`, each with a",
    "`.sha256` checksum; verify a download with `sha256sum -c <file>.sha256`."
  ].join("\n");
}

function buildNotes({ repoRoot, version, footer, changelogPath, packagePath } = {}) {
  const root = repoRoot ?? path.resolve(__dirname, "..", "..");
  const changelog = changelogPath ?? path.join(root, "CHANGELOG.md");
  const section = extractSection(fs.readFileSync(changelog, "utf8"), version);
  if (!footer) {
    return `${section}\n`;
  }
  const pkgPath = packagePath ?? path.join(root, "package.json");
  const packageName = JSON.parse(fs.readFileSync(pkgPath, "utf8")).name;
  return `${section}\n\n---\n\n${buildFooter(packageName, version)}\n`;
}

function main() {
  try {
    const options = parseArgs(process.argv.slice(2));
    const notes = buildNotes({
      version: options.version,
      footer: options.footer,
      changelogPath: options.changelog,
      packagePath: options.package
    });
    if (options.out) {
      const out = path.resolve(options.out);
      fs.mkdirSync(path.dirname(out), { recursive: true });
      fs.writeFileSync(out, notes, "utf8");
      // Diagnostics go to stderr so stdout stays a clean section for piping.
      console.error(`release-notes: wrote ${options.out} (${notes.length} bytes).`);
    } else {
      process.stdout.write(notes);
    }
  } catch (error) {
    console.error(`release-notes failed: ${error.message}`);
    process.exit(1);
  }
}

module.exports = { parseArgs, buildFooter, buildNotes };

if (require.main === module) {
  main();
}
