#!/usr/bin/env node
"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { extractSection } = require("./changelog.js");
const { toPosixPath } = require("../lib/path-classifier.js");

const REQUIRED_ARGS =
  "outDir packageFile packageChecksum unitypackageFile unitypackageChecksum".split(" ");
const STORE_MEDIA =
  "dxmessaging-store-icon-320.png dxmessaging-store-card-420x280.png dxmessaging-og-1200x630.png".split(
    " "
  );

function sha256(filePath) {
  return crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

function copyFile(source, target) {
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.copyFileSync(source, target);
}

function assertFile(filePath, label) {
  if (!fs.existsSync(filePath) || !fs.statSync(filePath).isFile()) {
    throw new Error(`${label} is missing: ${toPosixPath(filePath)}`);
  }
}

function readChecksum(checksumPath) {
  const lines = fs
    .readFileSync(checksumPath, "utf8")
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
  if (lines.length !== 1) {
    throw new Error(`Checksum file must contain one line: ${toPosixPath(checksumPath)}`);
  }
  const match = /^([0-9a-fA-F]{64})\s+\*?(.+)$/.exec(lines[0]);
  if (!match) {
    throw new Error(`Checksum file is not sha256sum formatted: ${toPosixPath(checksumPath)}`);
  }
  return { hash: match[1].toLowerCase(), fileName: match[2].trim() };
}

function validateChecksum(filePath, checksumPath) {
  assertFile(filePath, "Release file");
  assertFile(checksumPath, "Checksum file");
  const expected = readChecksum(checksumPath);
  const actualName = path.basename(filePath);
  if (expected.fileName !== actualName) {
    throw new Error(
      `Checksum file ${toPosixPath(checksumPath)} references ${expected.fileName}; expected ${actualName}.`
    );
  }
  const actualHash = sha256(filePath);
  if (expected.hash !== actualHash) {
    throw new Error(
      `Checksum mismatch for ${toPosixPath(filePath)}: expected ${expected.hash}, got ${actualHash}.`
    );
  }
}

function collectFiles(root) {
  const result = [];
  function walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const fullPath = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(fullPath);
      } else if (entry.isFile() && entry.name !== "MANIFEST.json") {
        result.push(fullPath);
      }
    }
  }
  walk(root);
  return result.sort((left, right) => toPosixPath(left).localeCompare(toPosixPath(right), "en"));
}

function relativeEntry(root, filePath) {
  const stat = fs.statSync(filePath);
  return {
    path: toPosixPath(path.relative(root, filePath)),
    bytes: stat.size,
    sha256: sha256(filePath)
  };
}

function buildChecklist({ mode, pkg, tag, packageName, unitypackageName, changelogSection }) {
  const isClassic = mode === "classic";
  const heading = isClassic
    ? `# Classic Asset Store Upload Checklist (${tag})`
    : `# UPM Asset Store Upload Checklist (${tag})`;
  const payload = isClassic
    ? `Classic payload: ${unitypackageName}`
    : `UPM reference payload: ${packageName}`;
  const pathStep = isClassic
    ? "Upload through the in-Editor Asset Store Publishing Tools window."
    : "Use Unity's official UPM publishing workflow only if the publisher account has UPM early access.";
  return `${heading}

Package: ${pkg.displayName || pkg.name} (${pkg.name})
Version: ${pkg.version}
Unity ${pkg.unity || "not declared"}
${payload}

## Before Upload

1. Verify every \`.sha256\` file in this artifact.
1. Use the staged files from this artifact; do not re-export from a working tree.
1. Review STORE-LISTING.md and the media directory before submitting.

## Upload

1. ${pathStep}
1. Set the listing version to the package version above.
1. Paste the release notes from the changelog excerpt below.
1. Submit for Unity review.

## Changelog Excerpt

${changelogSection}`;
}

function assertSafeOutputDir(repoRoot, outDir) {
  const resolved = path.resolve(repoRoot, outDir);
  const resolvedRepoRoot = path.resolve(repoRoot);
  const relativePath = path.relative(resolvedRepoRoot, resolved);
  if (
    !relativePath ||
    relativePath.startsWith("..") ||
    path.isAbsolute(relativePath) ||
    resolved === path.parse(resolved).root
  ) {
    throw new Error(`Refusing unsafe output directory: ${toPosixPath(resolved)}`);
  }
  const segments = relativePath.split(path.sep).filter(Boolean);
  if (segments[0] !== ".artifacts" || segments.length < 2) {
    throw new Error(
      `Refusing unsafe output directory outside .artifacts/: ${toPosixPath(resolved)}`
    );
  }
  let current = resolvedRepoRoot;
  for (const segment of segments) {
    current = path.join(current, segment);
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) {
      throw new Error(`Refusing unsafe symlinked output path: ${toPosixPath(current)}`);
    }
  }
  return resolved;
}

function stageAssetStoreSubmission(options = {}) {
  const repoRoot = path.resolve(options.repoRoot || path.join(__dirname, "..", ".."));
  for (const arg of REQUIRED_ARGS) {
    if (!options[arg]) {
      throw new Error(`Missing required option: ${arg}`);
    }
  }
  const outDir = assertSafeOutputDir(repoRoot, options.outDir);
  const packageFile = path.resolve(repoRoot, options.packageFile);
  const packageChecksum = path.resolve(repoRoot, options.packageChecksum);
  const unitypackageFile = path.resolve(repoRoot, options.unitypackageFile);
  const unitypackageChecksum = path.resolve(repoRoot, options.unitypackageChecksum);

  validateChecksum(packageFile, packageChecksum);
  validateChecksum(unitypackageFile, unitypackageChecksum);

  const pkg = JSON.parse(fs.readFileSync(path.join(repoRoot, "package.json"), "utf8"));
  const tag = options.tag || `v${pkg.version}`;
  const changelogSection = extractSection(
    fs.readFileSync(path.join(repoRoot, "CHANGELOG.md"), "utf8"),
    pkg.version
  );
  const listing = path.join(repoRoot, "STORE-LISTING.md");
  assertFile(listing, "STORE-LISTING.md");

  const mediaRoot = path.join(repoRoot, "docs", "images");
  const missingMedia = STORE_MEDIA.filter((name) => !fs.existsSync(path.join(mediaRoot, name)));
  if (missingMedia.length > 0) {
    throw new Error(`Required store media is missing: ${missingMedia.join(", ")}`);
  }

  fs.rmSync(outDir, { recursive: true, force: true });
  fs.mkdirSync(outDir, { recursive: true });
  for (const source of [packageFile, packageChecksum, unitypackageFile, unitypackageChecksum]) {
    copyFile(source, path.join(outDir, path.basename(source)));
  }
  copyFile(listing, path.join(outDir, "STORE-LISTING.md"));
  for (const name of STORE_MEDIA) {
    copyFile(path.join(mediaRoot, name), path.join(outDir, "media", name));
  }

  const checklistInput = {
    pkg,
    tag,
    packageName: path.basename(packageFile),
    unitypackageName: path.basename(unitypackageFile),
    changelogSection
  };
  fs.writeFileSync(
    path.join(outDir, "CLASSIC-UPLOAD-CHECKLIST.md"),
    `${buildChecklist({ ...checklistInput, mode: "classic" })}\n`,
    "utf8"
  );
  fs.writeFileSync(
    path.join(outDir, "UPM-UPLOAD-CHECKLIST.md"),
    `${buildChecklist({ ...checklistInput, mode: "upm" })}\n`,
    "utf8"
  );

  const manifest = {
    schemaVersion: 1,
    package: {
      name: pkg.name,
      displayName: pkg.displayName || "",
      version: pkg.version,
      unity: pkg.unity || "",
      description: pkg.description || "",
      documentationUrl: pkg.documentationUrl || "",
      licensesUrl: pkg.licensesUrl || ""
    },
    tag,
    upload: {
      sanctionedAutomation: false,
      note: "Unity Asset Store upload is manual until Unity publishes a supported non-interactive API."
    },
    files: collectFiles(outDir).map((file) => relativeEntry(outDir, file))
  };
  fs.writeFileSync(path.join(outDir, "MANIFEST.json"), `${JSON.stringify(manifest, null, 2)}\n`);
  return { outDir, version: pkg.version, files: manifest.files };
}

function parseArgs(argv) {
  const out = {};
  const values = {
    "--out": "outDir",
    "--package-file": "packageFile",
    "--package-checksum": "packageChecksum",
    "--unitypackage-file": "unitypackageFile",
    "--unitypackage-checksum": "unitypackageChecksum",
    "--tag": "tag"
  };
  for (let index = 0; index < argv.length; index += 1) {
    const key = values[argv[index]];
    if (!key) {
      throw new Error(`Unknown argument: ${argv[index]}`);
    }
    const value = argv[index + 1];
    if (!value || value.startsWith("--")) {
      throw new Error(`Missing value for ${argv[index]}`);
    }
    out[key] = value;
    index += 1;
  }
  const missing = REQUIRED_ARGS.filter((arg) => !out[arg]);
  if (missing.length > 0) {
    throw new Error(`Missing required arguments: ${missing.join(", ")}`);
  }
  return out;
}

function main() {
  try {
    const result = stageAssetStoreSubmission(parseArgs(process.argv.slice(2)));
    console.log(
      `asset-store-submission: staged ${result.files.length} files for v${result.version} at ${toPosixPath(result.outDir)}`
    );
  } catch (error) {
    console.error(`asset-store-submission failed: ${error.message}`);
    process.exit(1);
  }
}

module.exports = { STORE_MEDIA, parseArgs, stageAssetStoreSubmission };

if (require.main === module) {
  main();
}
