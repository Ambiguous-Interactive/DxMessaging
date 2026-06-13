#!/usr/bin/env node
"use strict";

/**
 * Validate npm package tarball hygiene and Unity .meta pairing invariants.
 *
 * Default mode validates the real npm pack list:
 *   npm pack --json --dry-run --ignore-scripts
 *
 * Release mode can validate a concrete tarball via:
 *   node scripts/validate-npm-meta.js --tarball <path-to.tgz>
 *
 * Release artifact mode validates the downloaded release artifact directory:
 *   node scripts/validate-npm-meta.js --release-dir .artifacts/release \
 *     --expected-name com.example.package --expected-version 1.2.3
 */

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { execFileSync } = require("child_process");

const { normalizeToLf } = require("./lib/line-endings");
const { toPosixPath } = require("./lib/path-classifier");
const { spawnPlatformCommandSync } = require("./lib/shell-command");

const REPO_ROOT = path.resolve(__dirname, "..");
const UNITY_ROOTS = ["Editor", "Runtime", "Samples~", "SourceGenerators"];
const STANDARD_CSHARP_META_MONO_IMPORTER_LINES = [
  "MonoImporter:",
  "  externalObjects: {}",
  "  serializedVersion: 2",
  "  defaultReferences: []",
  "  executionOrder: 0",
  "  icon: {instanceID: 0}",
  "  userData:",
  "  assetBundleName:",
  "  assetBundleVariant:"
];

const FORBIDDEN_PATH_RULES = [
  {
    id: "vs-dir",
    regex: /(^|\/)\.vs(\/|$)/i,
    reason: "Visual Studio cache directory (.vs/)"
  },
  {
    id: "idea-dir",
    regex: /(^|\/)\.idea(\/|$)/i,
    reason: "JetBrains IDE settings directory (.idea/)"
  },
  {
    id: "bin-dir",
    regex: /(^|\/)bin(\/|$)/i,
    reason: "Build output directory (bin/)"
  },
  {
    id: "obj-dir",
    regex: /(^|\/)obj(\/|$)/i,
    reason: "Build output directory (obj/)"
  },
  {
    id: "pdb",
    regex: /\.pdb(\.meta)?$/i,
    reason: "Debug symbols (*.pdb)"
  },
  {
    id: "lscache",
    regex: /\.lscache(\.meta)?$/i,
    reason: "C# Dev Kit cache (*.lscache)"
  },
  {
    id: "tmp",
    regex: /\.tmp(\.meta)?$/i,
    reason: "Temporary file (*.tmp)"
  },
  {
    id: "csproj-user",
    regex: /\.csproj\.user(\.meta)?$/i,
    reason: "MSBuild user settings (*.csproj.user)"
  },
  {
    id: "dotsettings-user",
    regex: /\.DotSettings\.user(\.meta)?$/,
    reason: "Rider user settings (*.DotSettings.user)"
  },
  {
    id: "suo",
    regex: /\.suo(\.meta)?$/i,
    reason: "Visual Studio solution user options (*.suo)"
  },
  {
    id: "generic-user",
    regex: /\.user(\.meta)?$/i,
    reason: "User-specific settings file (*.user)"
  }
];

function normalizePackEntry(entry) {
  if (typeof entry !== "string") {
    return "";
  }

  let normalized = toPosixPath(entry).trim();
  if (normalized.length === 0) {
    return "";
  }

  if (normalized.startsWith("./")) {
    normalized = normalized.slice(2);
  }
  if (normalized === "package") {
    return "";
  }
  if (normalized.startsWith("package/")) {
    normalized = normalized.slice("package/".length);
  }

  return normalized.replace(/\/+$/g, "");
}

function uniqSortedPaths(entries) {
  const normalized = entries
    .map((entry) => normalizePackEntry(entry))
    .filter((entry) => entry.length > 0);
  return [...new Set(normalized)].sort();
}

function parsePackJsonEntries(packJsonText) {
  let parsed;
  try {
    parsed = JSON.parse(normalizeToLf(packJsonText));
  } catch (error) {
    throw new Error(`Unable to parse npm pack JSON output: ${error.message}`);
  }

  if (!Array.isArray(parsed) || parsed.length === 0 || parsed[0] === null) {
    throw new Error("npm pack JSON output did not contain an entry list.");
  }

  const files = parsed[0].files;
  if (!Array.isArray(files)) {
    throw new Error("npm pack JSON output is missing the files array.");
  }

  const entries = files.map((file, index) => {
    if (!file || typeof file.path !== "string") {
      throw new Error(`npm pack JSON file entry at index ${index} has no string path.`);
    }
    return file.path;
  });

  return uniqSortedPaths(entries);
}

function collectDryRunEntries() {
  const result = spawnPlatformCommandSync(
    "npm",
    ["pack", "--json", "--dry-run", "--ignore-scripts"],
    {
      cwd: REPO_ROOT,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    }
  );

  if (result.error) {
    throw result.error;
  }
  if (result.status !== 0) {
    const stderr = normalizeToLf(String(result.stderr || "")).trim();
    const detail = stderr.length > 0 ? stderr : `exit code ${result.status}`;
    throw new Error(`npm pack --dry-run failed: ${detail}`);
  }

  return parsePackJsonEntries(String(result.stdout || ""));
}

function buildLocalTarArchiveSpec(tarballPath, pathImpl = path, baseDir = REPO_ROOT) {
  if (typeof tarballPath !== "string" || tarballPath.length === 0) {
    throw new Error("buildLocalTarArchiveSpec requires a non-empty tarball path.");
  }

  const resolved = pathImpl.resolve(baseDir, tarballPath);
  return {
    archive: `./${pathImpl.basename(resolved)}`,
    cwd: pathImpl.dirname(resolved)
  };
}

function collectTarballEntries(tarballPath, execFileSyncImpl = execFileSync) {
  if (typeof tarballPath !== "string" || tarballPath.length === 0) {
    throw new Error("collectTarballEntries requires a non-empty tarball path.");
  }

  const archiveSpec = buildLocalTarArchiveSpec(tarballPath);
  let output;
  try {
    output = execFileSyncImpl("tar", ["-tzf", archiveSpec.archive], {
      cwd: archiveSpec.cwd,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    });
  } catch (error) {
    const stderr = normalizeToLf(String(error.stderr || "")).trim();
    const detail = stderr.length > 0 ? stderr : error.message;
    throw new Error(`Unable to list tarball entries for '${toPosixPath(tarballPath)}': ${detail}`);
  }

  return uniqSortedPaths(normalizeToLf(output).split("\n"));
}

function readTarballPackageJson(tarballPath, execFileSyncImpl = execFileSync) {
  const archiveSpec = buildLocalTarArchiveSpec(tarballPath);
  let output;
  try {
    output = execFileSyncImpl("tar", ["-xOf", archiveSpec.archive, "package/package.json"], {
      cwd: archiveSpec.cwd,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    });
  } catch (error) {
    const stderr = normalizeToLf(String(error.stderr || "")).trim();
    const detail = stderr.length > 0 ? stderr : error.message;
    throw new Error(
      `Unable to read package/package.json from '${toPosixPath(tarballPath)}': ${detail}`
    );
  }

  try {
    return JSON.parse(normalizeToLf(output));
  } catch (error) {
    throw new Error(`Unable to parse package/package.json from tarball: ${error.message}`);
  }
}

function buildStandardCsharpMetaContent(guid) {
  return ["fileFormatVersion: 2", `guid: ${guid}`, ...STANDARD_CSHARP_META_MONO_IMPORTER_LINES, ""].join("\n");
}

function isCsharpMetaPath(relativePath) {
  return typeof relativePath === "string" && relativePath.toLowerCase().endsWith(".cs.meta");
}

function getCsharpMetaShapeViolation(relativePath, content) {
  if (!isCsharpMetaPath(relativePath)) {
    return "";
  }

  const normalized = normalizeToLf(String(content || ""));
  if (!/^fileFormatVersion: 2\nguid: [0-9a-f]{32}(?:\n|$)/.test(normalized)) {
    return "must start with fileFormatVersion: 2 followed by a 32-hex guid";
  }

  const importerMatch = /^MonoImporter:\n((?:  .*(?:\n|$))*)/m.exec(normalized);
  if (!importerMatch) {
    return "is missing the standard MonoImporter block for Unity C# scripts";
  }

  const importerLines = importerMatch[1].split("\n").map((line) => line.trimEnd()).filter(Boolean);
  const expectedLines = STANDARD_CSHARP_META_MONO_IMPORTER_LINES.slice(1).map((line) => line.trimEnd());

  let lastIndex = -1;
  for (const expected of expectedLines) {
    const foundIndex = importerLines.indexOf(expected);
    if (foundIndex < 0) {
      return `is missing standard MonoImporter line '${expected}'`;
    }
    if (foundIndex <= lastIndex) {
      return "has standard MonoImporter lines out of order";
    }
    lastIndex = foundIndex;
  }

  return "";
}

function validateCsharpMetaFiles(relativePaths, options = {}) {
  const readFileSyncImpl = options.readFileSync || fs.readFileSync;
  const csharpMetaPaths = relativePaths.filter(isCsharpMetaPath);
  const invalid = [];

  for (const relativePath of csharpMetaPaths) {
    const content = readFileSyncImpl(path.join(REPO_ROOT, relativePath), "utf8");
    const reason = getCsharpMetaShapeViolation(relativePath, content);
    if (reason) {
      invalid.push({ path: toPosixPath(relativePath), reason });
    }
  }

  invalid.sort((left, right) => left.path.localeCompare(right.path, "en"));
  return {
    checked: csharpMetaPaths.length,
    invalid
  };
}

function collectTrackedCsharpMetaPaths(execFileSyncImpl = execFileSync) {
  try {
    const output = execFileSyncImpl("git", ["ls-files", "-z", "--", "*.cs.meta"], {
      cwd: REPO_ROOT,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    });
    return String(output || "").split("\0").filter(Boolean).sort();
  } catch (error) {
    const stderr = normalizeToLf(String(error.stderr || "")).trim();
    const detail = stderr.length > 0 ? stderr : error.message;
    throw new Error(`Unable to list tracked C# .meta files with git: ${detail}`);
  }
}

function validateRepositoryCsharpMetaFiles(options = {}) {
  const relativePaths = Array.isArray(options.relativePaths)
    ? options.relativePaths
    : collectTrackedCsharpMetaPaths(options.execFileSync);
  return validateCsharpMetaFiles(
    relativePaths,
    options
  );
}

function computeFileSha256(filePath) {
  const hash = crypto.createHash("sha256");
  hash.update(fs.readFileSync(filePath));
  return hash.digest("hex");
}

function readSingleSha256Line(checksumFile) {
  const lines = normalizeToLf(fs.readFileSync(checksumFile, "utf8"))
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);

  if (lines.length !== 1) {
    throw new Error(
      `Checksum file '${toPosixPath(checksumFile)}' must contain exactly one non-empty line.`
    );
  }

  const match = /^([0-9a-fA-F]{64})\s+\*?(.+)$/.exec(lines[0]);
  if (!match) {
    throw new Error(`Checksum file '${toPosixPath(checksumFile)}' is not sha256sum formatted.`);
  }

  return {
    hash: match[1].toLowerCase(),
    fileName: match[2].trim()
  };
}

function collectReleaseArtifacts(releaseDir) {
  if (typeof releaseDir !== "string" || releaseDir.length === 0) {
    throw new Error("collectReleaseArtifacts requires a non-empty release directory.");
  }
  if (!fs.existsSync(releaseDir) || !fs.statSync(releaseDir).isDirectory()) {
    throw new Error(`Release artifact directory does not exist: ${toPosixPath(releaseDir)}`);
  }

  const entries = fs.readdirSync(releaseDir, { withFileTypes: true });
  const tarballs = entries
    .filter((entry) => entry.isFile() && entry.name.endsWith(".tgz"))
    .map((entry) => path.join(releaseDir, entry.name))
    .sort();
  const checksumFiles = entries
    .filter((entry) => entry.isFile() && entry.name.endsWith(".sha256"))
    .map((entry) => path.join(releaseDir, entry.name))
    .sort();

  if (tarballs.length !== 1) {
    throw new Error(
      `Expected exactly one .tgz in release artifact directory; found ${tarballs.length}.`
    );
  }
  if (checksumFiles.length !== 1) {
    throw new Error(
      `Expected exactly one .sha256 in release artifact directory; found ${checksumFiles.length}.`
    );
  }

  const tarball = tarballs[0];
  const checksumFile = checksumFiles[0];
  const expectedChecksumFile = `${tarball}.sha256`;
  if (path.resolve(checksumFile) !== path.resolve(expectedChecksumFile)) {
    throw new Error(
      `Checksum file must be adjacent to the tarball as '${toPosixPath(expectedChecksumFile)}'.`
    );
  }

  const releaseNotes = path.join(releaseDir, "release-notes.md");
  if (!fs.existsSync(releaseNotes) || !fs.statSync(releaseNotes).isFile()) {
    throw new Error(`Release notes artifact is missing: ${toPosixPath(releaseNotes)}`);
  }
  if (fs.readFileSync(releaseNotes, "utf8").trim().length === 0) {
    throw new Error(`Release notes artifact is empty: ${toPosixPath(releaseNotes)}`);
  }

  return {
    tarball,
    checksumFile,
    releaseNotes
  };
}

function validateReleaseArtifacts(options) {
  const releaseDir = options.releaseDir;
  const expectedName = options.expectedName;
  const expectedVersion = options.expectedVersion;
  if (!expectedName || !expectedVersion) {
    throw new Error("--release-dir requires --expected-name and --expected-version.");
  }

  const artifacts = collectReleaseArtifacts(releaseDir);
  const checksum = readSingleSha256Line(artifacts.checksumFile);
  const expectedFileName = path.basename(artifacts.tarball);
  if (checksum.fileName !== expectedFileName) {
    throw new Error(
      `Checksum file references '${checksum.fileName}', expected '${expectedFileName}'.`
    );
  }

  const actualHash = computeFileSha256(artifacts.tarball);
  if (checksum.hash !== actualHash) {
    throw new Error(
      `Checksum mismatch for '${toPosixPath(artifacts.tarball)}': expected ${checksum.hash}, got ${actualHash}.`
    );
  }

  const packageJson = readTarballPackageJson(artifacts.tarball);
  if (packageJson.name !== expectedName || packageJson.version !== expectedVersion) {
    throw new Error(
      `Downloaded package artifact identity mismatch: expected ${expectedName}@${expectedVersion}, got ${packageJson.name}@${packageJson.version}.`
    );
  }

  return {
    ...artifacts,
    packageName: packageJson.name,
    packageVersion: packageJson.version
  };
}

function isUnityRelevantPath(relativePath) {
  if (typeof relativePath !== "string" || relativePath.length === 0) {
    return false;
  }

  return UNITY_ROOTS.some(
    (root) =>
      relativePath === root ||
      relativePath === `${root}.meta` ||
      relativePath.startsWith(`${root}/`)
  );
}

function findForbiddenTarballPaths(entries) {
  const violations = [];
  for (const entry of entries) {
    for (const rule of FORBIDDEN_PATH_RULES) {
      if (rule.regex.test(entry)) {
        violations.push({ path: entry, rule: rule.id, reason: rule.reason });
        break;
      }
    }
  }

  return violations;
}

function computeRequiredMetaPaths(entries, options = {}) {
  const excludedPaths =
    options && options.excludedPaths instanceof Set ? options.excludedPaths : new Set();
  const required = new Set();

  for (const entry of entries) {
    if (excludedPaths.has(entry) || !isUnityRelevantPath(entry) || entry.endsWith(".meta")) {
      continue;
    }

    required.add(`${entry}.meta`);

    let parent = path.posix.dirname(entry);
    while (parent !== ".") {
      required.add(`${parent}.meta`);
      parent = path.posix.dirname(parent);
    }
  }

  // Unity convention: hidden sample root has no sibling .meta.
  required.delete("Samples~.meta");
  return required;
}

function collectPresentMetaPaths(entries) {
  const present = new Set();
  for (const entry of entries) {
    if (isUnityRelevantPath(entry) && entry.endsWith(".meta")) {
      present.add(entry);
    }
  }
  return present;
}

function hasEntryOrDescendant(entrySet, target) {
  if (entrySet.has(target)) {
    return true;
  }
  const prefix = `${target}/`;
  for (const entry of entrySet) {
    if (entry.startsWith(prefix)) {
      return true;
    }
  }
  return false;
}

function validatePublishedFilesArePairedWithMetas(entries) {
  const options = arguments.length > 1 ? arguments[1] : {};
  const excludedPaths =
    options && options.excludedPaths instanceof Set ? options.excludedPaths : new Set();

  const required = computeRequiredMetaPaths(entries, { excludedPaths });
  const present = collectPresentMetaPaths(entries);

  const missing = [];
  for (const expected of required) {
    if (!present.has(expected)) {
      missing.push(expected);
    }
  }

  const allEntries = new Set(entries);
  const orphans = [];
  for (const meta of present) {
    if (excludedPaths.has(meta)) {
      continue;
    }
    const target = meta.slice(0, -".meta".length);
    if (!hasEntryOrDescendant(allEntries, target)) {
      orphans.push(meta);
    }
  }

  return {
    missing: missing.sort(),
    orphans: orphans.sort()
  };
}

function validatePackEntries(entries) {
  const forbidden = findForbiddenTarballPaths(entries);
  const excludedPaths = new Set(forbidden.map((violation) => violation.path));
  const metaValidation = validatePublishedFilesArePairedWithMetas(entries, {
    excludedPaths
  });

  return {
    valid:
      forbidden.length === 0 &&
      metaValidation.missing.length === 0 &&
      metaValidation.orphans.length === 0,
    forbidden,
    missingMetas: metaValidation.missing,
    orphanMetas: metaValidation.orphans,
    invalidCsharpMetas: []
  };
}

function parseCliArgs(args) {
  const options = { tarball: "", packJson: "", releaseDir: "", expectedName: "", expectedVersion: "", repoCsharpMetasOnly: false };
  const valueOptions = {
    "--tarball": "tarball",
    "--pack-json": "packJson",
    "--release-dir": "releaseDir",
    "--expected-name": "expectedName",
    "--expected-version": "expectedVersion"
  };

  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index];
    const optionName = valueOptions[arg];
    if (optionName) {
      const value = args[index + 1];
      if (!value) {
        throw new Error(`${arg} requires a value.`);
      }
      options[optionName] = value;
      index += 1;
      continue;
    }
    if (arg === "--repo-cs-metas-only") {
      options.repoCsharpMetasOnly = true;
      continue;
    }
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    throw new Error(`Unknown argument: ${arg}`);
  }

  const sources = [options.tarball, options.packJson, options.releaseDir, options.repoCsharpMetasOnly].filter(Boolean);
  if (sources.length > 1) {
    throw new Error(
      "Use only one of --tarball, --pack-json, --release-dir, or --repo-cs-metas-only."
    );
  }
  if (!options.releaseDir && (options.expectedName || options.expectedVersion)) {
    throw new Error("--expected-name and --expected-version are only valid with --release-dir.");
  }

  return options;
}

function printHelp() {
  console.log([
    "Usage: node scripts/validate-npm-meta.js [--tarball <file.tgz>] [--pack-json <file.json>]",
    "       node scripts/validate-npm-meta.js --release-dir <dir> --expected-name <name> --expected-version <version>",
    "       node scripts/validate-npm-meta.js --repo-cs-metas-only",
    "  Default mode validates npm pack --json --dry-run --ignore-scripts output."
  ].join("\n"));
}

function printDiagnosticList(title, entries, formatEntry = (entry) => entry) {
  if (entries.length === 0) return;
  console.error(title);
  for (const entry of entries) console.error(`  - ${formatEntry(entry)}`);
}

function runValidation(options = {}) {
  let source = "npm pack --json --dry-run --ignore-scripts";
  let entries;
  let repoMetaValidation = { checked: 0, invalid: [] };

  if (options.repoCsharpMetasOnly) {
    repoMetaValidation = validateRepositoryCsharpMetaFiles(options);
    const result = {
      valid: repoMetaValidation.invalid.length === 0,
      forbidden: [],
      missingMetas: [],
      orphanMetas: [],
      invalidCsharpMetas: repoMetaValidation.invalid
    };

    if (result.valid) {
      console.log(
        `Unity C# .meta validation passed (${repoMetaValidation.checked} tracked files).`
      );
      return result;
    }

    console.error(
      `Unity C# .meta validation failed (${repoMetaValidation.checked} tracked files).`
    );
    printDiagnosticList("Invalid Unity C# .meta file shapes:", result.invalidCsharpMetas, (invalid) => `${invalid.path}: ${invalid.reason}`);
    return result;
  }

  if (options.tarball) {
    source = `tarball ${toPosixPath(options.tarball)}`;
    entries = collectTarballEntries(options.tarball);
  } else if (options.releaseDir) {
    const artifacts = validateReleaseArtifacts(options);
    source = `release artifact ${toPosixPath(artifacts.tarball)}`;
    entries = collectTarballEntries(artifacts.tarball);
  } else if (options.packJson) {
    source = `pack JSON ${toPosixPath(options.packJson)}`;
    entries = parsePackJsonEntries(fs.readFileSync(options.packJson, "utf8"));
  } else {
    entries = collectDryRunEntries();
  }

  const packResult = validatePackEntries(entries);
  try {
    repoMetaValidation = validateRepositoryCsharpMetaFiles(options);
  } catch (error) {
    console.error(
      `Skipping tracked Unity C# .meta validation because git is unavailable: ${error.message}`
    );
    repoMetaValidation = { checked: 0, invalid: [] };
  }
  const result = {
    ...packResult,
    invalidCsharpMetas: repoMetaValidation.invalid,
    valid: packResult.valid && repoMetaValidation.invalid.length === 0
  };
  if (result.valid) {
    console.log(
      `npm packaging validation passed (${entries.length} entries from ${source}; ${repoMetaValidation.checked} tracked C# .meta files).`
    );
    return result;
  }

  console.error(`npm packaging validation failed (${entries.length} entries from ${source}).`);

  printDiagnosticList("Forbidden build-artifact paths:", result.forbidden, (violation) => `${violation.path} (${violation.reason})`);
  printDiagnosticList("Missing Unity .meta sibling paths:", result.missingMetas);
  printDiagnosticList("Orphan Unity .meta paths:", result.orphanMetas);
  printDiagnosticList("Invalid Unity C# .meta file shapes:", result.invalidCsharpMetas, (invalid) => `${invalid.path}: ${invalid.reason}`);

  return result;
}

if (require.main === module) {
  try {
    const options = parseCliArgs(process.argv.slice(2));
    if (options.help) {
      printHelp();
      process.exit(0);
    }
    const result = runValidation(options);
    if (!result.valid) {
      process.exit(1);
    }
  } catch (error) {
    console.error(`validate-npm-meta failed: ${error.message}`);
    process.exit(1);
  }
}

module.exports = {
  FORBIDDEN_PATH_RULES,
  STANDARD_CSHARP_META_MONO_IMPORTER_LINES,
  UNITY_ROOTS,
  buildStandardCsharpMetaContent,
  buildLocalTarArchiveSpec,
  collectReleaseArtifacts,
  collectTarballEntries,
  collectTrackedCsharpMetaPaths,
  computeRequiredMetaPaths,
  findForbiddenTarballPaths,
  getCsharpMetaShapeViolation,
  isUnityRelevantPath,
  normalizePackEntry,
  parsePackJsonEntries,
  readTarballPackageJson,
  runValidation,
  validateCsharpMetaFiles,
  validatePackEntries,
  validateReleaseArtifacts,
  validateRepositoryCsharpMetaFiles,
  validatePublishedFilesArePairedWithMetas
};
