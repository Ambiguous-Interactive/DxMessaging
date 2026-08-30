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
const UNITY_META_EXEMPT_PATHS = new Set(["Samples~"]);
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
  ["vs-dir", /(^|\/)\.vs(\/|$)/i, "Visual Studio cache directory (.vs/)"],
  ["idea-dir", /(^|\/)\.idea(\/|$)/i, "JetBrains IDE settings directory (.idea/)"],
  ["bin-dir", /(^|\/)bin(\/|$)/i, "Build output directory (bin/)"],
  ["obj-dir", /(^|\/)obj(\/|$)/i, "Build output directory (obj/)"],
  ["pdb", /\.pdb(\.meta)?$/i, "Debug symbols (*.pdb)"],
  ["lscache", /\.lscache(\.meta)?$/i, "C# Dev Kit cache (*.lscache)"],
  ["tmp", /\.tmp(\.meta)?$/i, "Temporary file (*.tmp)"],
  ["csproj-user", /\.csproj\.user(\.meta)?$/i, "MSBuild user settings (*.csproj.user)"],
  ["dotsettings-user", /\.DotSettings\.user(\.meta)?$/, "Rider settings (*.DotSettings.user)"],
  ["suo", /\.suo(\.meta)?$/i, "Visual Studio solution user options (*.suo)"],
  ["generic-user", /\.user(\.meta)?$/i, "User-specific settings file (*.user)"]
].map(([id, regex, reason]) => ({ id, regex, reason }));

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

function describeProcessFailure(rawStderr, fallback) {
  const stderr = normalizeToLf(String(rawStderr || "")).trim();
  return stderr.length > 0 ? stderr : fallback;
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
    const detail = describeProcessFailure(result.stderr, `exit code ${result.status}`);
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

function readTarOutput(tarballPath, command, operands, action, execFileSyncImpl = execFileSync) {
  if (typeof tarballPath !== "string" || tarballPath.length === 0) {
    throw new Error(`${action} requires a non-empty tarball path.`);
  }
  const archiveSpec = buildLocalTarArchiveSpec(tarballPath);
  try {
    return execFileSyncImpl("tar", [command, archiveSpec.archive, ...operands], {
      cwd: archiveSpec.cwd,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    });
  } catch (error) {
    const detail = describeProcessFailure(error.stderr, error.message);
    throw new Error(`Unable to ${action} for '${toPosixPath(tarballPath)}': ${detail}`);
  }
}

function collectTarballEntries(tarballPath, execFileSyncImpl = execFileSync) {
  const output = readTarOutput(tarballPath, "-tzf", [], "list tarball entries", execFileSyncImpl);
  return uniqSortedPaths(normalizeToLf(output).split("\n"));
}

function readTarballPackageJson(tarballPath, execFileSyncImpl = execFileSync) {
  const output = readTarOutput(
    tarballPath,
    "-xOf",
    ["package/package.json"],
    "read package/package.json",
    execFileSyncImpl
  );

  try {
    return JSON.parse(normalizeToLf(output));
  } catch (error) {
    throw new Error(`Unable to parse package/package.json from tarball: ${error.message}`);
  }
}

function buildStandardCsharpMetaContent(guid) {
  return [
    "fileFormatVersion: 2",
    `guid: ${guid}`,
    ...STANDARD_CSHARP_META_MONO_IMPORTER_LINES,
    ""
  ].join("\n");
}

function isCsharpMetaPath(relativePath) {
  return typeof relativePath === "string" && relativePath.toLowerCase().endsWith(".cs.meta");
}

function getCsharpMetaShapeViolation(relativePath, content) {
  if (!isCsharpMetaPath(relativePath)) {
    return "";
  }

  const normalized = normalizeToLf(String(content || ""));
  const lines = normalized.split("\n");
  if (lines[0] !== "fileFormatVersion: 2" || !/^guid: [0-9a-f]{32}$/.test(lines[1] || "")) {
    return "must start with fileFormatVersion: 2 followed by a 32-hex guid";
  }

  const malformedImporterIndex = lines.findIndex(
    (line) => line.trimEnd() === "MonoImporter:" && line !== "MonoImporter:"
  );
  if (malformedImporterIndex >= 0) {
    return `line ${malformedImporterIndex + 1} must match standard line 'MonoImporter:' without trailing whitespace`;
  }
  const importerIndex = lines.indexOf("MonoImporter:");
  if (importerIndex < 0) {
    return "is missing the standard MonoImporter block for Unity C# scripts";
  }

  const mismatch = STANDARD_CSHARP_META_MONO_IMPORTER_LINES.findIndex(
    (expected, offset) => lines[importerIndex + offset] !== expected
  );
  if (mismatch >= 0) {
    const expected = STANDARD_CSHARP_META_MONO_IMPORTER_LINES[mismatch];
    const actual = lines[importerIndex + mismatch];
    if (actual !== expected) {
      if (typeof actual === "string" && actual.trimEnd() === expected) {
        return `line ${importerIndex + mismatch + 1} must match standard line '${expected}' without trailing whitespace`;
      }
      return `line ${importerIndex + mismatch + 1} must match standard line '${expected}'`;
    }
  }

  const trailingStart = importerIndex + STANDARD_CSHARP_META_MONO_IMPORTER_LINES.length;
  const trailingOffset = lines.slice(trailingStart).findIndex((line) => line.length > 0);
  if (trailingOffset >= 0) {
    return `line ${trailingStart + trailingOffset + 1} is not part of the standard MonoImporter block`;
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

function hasDotPrefixedPathSegment(relativePath) {
  return toPosixPath(relativePath)
    .split("/")
    .some((segment) => segment.startsWith("."));
}

function collectTrackedRepositoryPaths(execFileSyncImpl = execFileSync) {
  try {
    const output = execFileSyncImpl("git", ["ls-files", "-z"], {
      cwd: REPO_ROOT,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    });
    return String(output || "")
      .split("\0")
      .filter(Boolean);
  } catch (error) {
    const detail = describeProcessFailure(error.stderr, error.message);
    throw new Error(`Unable to list tracked repository files with git: ${detail}`);
  }
}

function validateRepositoryMetaPairs(options = {}) {
  const trackedPaths = options.trackedPaths || collectTrackedRepositoryPaths(options.execFileSync);
  const importablePaths = trackedPaths.filter(
    (relativePath) => !hasDotPrefixedPathSegment(relativePath)
  );
  return {
    checkedAssets: importablePaths.filter((entry) => !entry.endsWith(".meta")).length,
    ...validateMetaPairs(importablePaths, { isRelevant: () => true })
  };
}

function validateRepositoryUnityMetaFiles(options = {}) {
  const execFileSyncImpl = options.execFileSync || execFileSync;
  const trackedPaths = options.trackedPaths || collectTrackedRepositoryPaths(execFileSyncImpl);
  const readFileSyncImpl =
    options.readFileSync ||
    ((filePath, encoding) =>
      execFileSyncImpl("git", ["show", `:${toPosixPath(path.relative(REPO_ROOT, filePath))}`], {
        cwd: REPO_ROOT,
        encoding,
        stdio: ["ignore", "pipe", "pipe"]
      }));
  const pairs = validateRepositoryMetaPairs({ ...options, trackedPaths });
  const csharp = validateCsharpMetaFiles(
    options.relativePaths || trackedPaths.filter(isCsharpMetaPath),
    { ...options, readFileSync: readFileSyncImpl }
  );
  return {
    ...pairs,
    checkedCsharpMetas: csharp.checked,
    invalidCsharpMetas: csharp.invalid
  };
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
  const excludedPaths = options.excludedPaths || new Set();
  const isRelevant = options.isRelevant || Boolean;
  const required = new Set();

  for (const entry of entries) {
    if (excludedPaths.has(entry) || !isRelevant(entry) || entry.endsWith(".meta")) {
      continue;
    }

    if (!UNITY_META_EXEMPT_PATHS.has(entry)) required.add(`${entry}.meta`);

    let parent = path.posix.dirname(entry);
    while (parent !== ".") {
      if (!UNITY_META_EXEMPT_PATHS.has(parent)) required.add(`${parent}.meta`);
      parent = path.posix.dirname(parent);
    }
  }

  return required;
}

function validateMetaPairs(entries, options = {}) {
  const excludedPaths = options.excludedPaths || new Set();
  const isRelevant = options.isRelevant || Boolean;
  const assets = entries.filter(
    (entry) => isRelevant(entry) && !entry.endsWith(".meta") && !excludedPaths.has(entry)
  );
  const present = new Set(
    entries.filter(
      (entry) => isRelevant(entry) && entry.endsWith(".meta") && !excludedPaths.has(entry)
    )
  );
  const missing = [...computeRequiredMetaPaths(entries, { excludedPaths, isRelevant })].filter(
    (expected) => !present.has(expected)
  );
  const orphans = [...present].filter((meta) => {
    const target = meta.slice(0, -".meta".length);
    return !assets.some((asset) => asset === target || asset.startsWith(`${target}/`));
  });
  return {
    missing: missing.sort(),
    orphans: orphans.sort()
  };
}

function validatePackEntries(entries) {
  const forbidden = findForbiddenTarballPaths(entries);
  const excludedPaths = new Set(forbidden.map((violation) => violation.path));
  const metaValidation = validateMetaPairs(entries, { excludedPaths });

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
  const options = {
    tarball: "",
    packJson: "",
    releaseDir: "",
    expectedName: "",
    expectedVersion: "",
    repoMetasOnly: false
  };
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
    if (arg === "--repo-metas-only" || arg === "--repo-cs-metas-only") {
      options.repoMetasOnly = true;
      continue;
    }
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    throw new Error(`Unknown argument: ${arg}`);
  }

  const sources = [
    options.tarball,
    options.packJson,
    options.releaseDir,
    options.repoMetasOnly
  ].filter(Boolean);
  if (sources.length > 1) {
    throw new Error("Use only one of --tarball, --pack-json, --release-dir, or --repo-metas-only.");
  }
  if (!options.releaseDir && (options.expectedName || options.expectedVersion)) {
    throw new Error("--expected-name and --expected-version are only valid with --release-dir.");
  }

  return options;
}

function printHelp() {
  console.log(
    [
      "Usage: node scripts/validate-npm-meta.js [--tarball <file.tgz>] [--pack-json <file.json>]",
      "       node scripts/validate-npm-meta.js --release-dir <dir> --expected-name <name> --expected-version <version>",
      "       node scripts/validate-npm-meta.js --repo-metas-only",
      "  Default mode validates npm pack --json --dry-run --ignore-scripts output."
    ].join("\n")
  );
}

function printDiagnosticList(title, entries, formatEntry = (entry) => entry) {
  if (entries.length === 0) return;
  console.error(title);
  for (const entry of entries) console.error(`  - ${formatEntry(entry)}`);
}

function runValidation(options = {}) {
  let source = "npm pack --json --dry-run --ignore-scripts";
  let entries;
  let repoMetaValidation = {
    checkedAssets: 0,
    checkedCsharpMetas: 0,
    missing: [],
    orphans: [],
    invalidCsharpMetas: []
  };

  if (options.repoMetasOnly || options.repoCsharpMetasOnly) {
    repoMetaValidation = validateRepositoryUnityMetaFiles(options);
    const result = {
      valid:
        repoMetaValidation.missing.length === 0 &&
        repoMetaValidation.orphans.length === 0 &&
        repoMetaValidation.invalidCsharpMetas.length === 0,
      forbidden: [],
      missingMetas: repoMetaValidation.missing,
      orphanMetas: repoMetaValidation.orphans,
      invalidCsharpMetas: repoMetaValidation.invalidCsharpMetas
    };

    if (result.valid) {
      console.log(
        `Repository Unity .meta validation passed (${repoMetaValidation.checkedAssets} tracked assets; ${repoMetaValidation.checkedCsharpMetas} C# metas).`
      );
      return result;
    }

    console.error(
      `Repository Unity .meta validation failed (${repoMetaValidation.checkedAssets} tracked assets; ${repoMetaValidation.checkedCsharpMetas} C# metas).`
    );
    printDiagnosticList("Missing tracked Unity .meta companion paths:", result.missingMetas);
    printDiagnosticList("Orphan tracked Unity .meta paths:", result.orphanMetas);
    printDiagnosticList(
      "Invalid Unity C# .meta file shapes:",
      result.invalidCsharpMetas,
      (invalid) => `${invalid.path}: ${invalid.reason}`
    );
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
  options.readFileSync ||= fs.readFileSync;
  repoMetaValidation = validateRepositoryUnityMetaFiles(options);
  const result = {
    ...packResult,
    missingRepositoryMetas: repoMetaValidation.missing,
    orphanRepositoryMetas: repoMetaValidation.orphans,
    invalidCsharpMetas: repoMetaValidation.invalidCsharpMetas,
    valid:
      packResult.valid &&
      repoMetaValidation.missing.length === 0 &&
      repoMetaValidation.orphans.length === 0 &&
      repoMetaValidation.invalidCsharpMetas.length === 0
  };
  if (result.valid) {
    console.log(
      `npm packaging validation passed (${entries.length} entries from ${source}; ${repoMetaValidation.checkedAssets} tracked repository assets; ${repoMetaValidation.checkedCsharpMetas} C# metas).`
    );
    return result;
  }

  console.error(`npm packaging validation failed (${entries.length} entries from ${source}).`);

  printDiagnosticList(
    "Forbidden build-artifact paths:",
    result.forbidden,
    (violation) => `${violation.path} (${violation.reason})`
  );
  printDiagnosticList("Missing Unity .meta sibling paths:", result.missingMetas);
  printDiagnosticList("Orphan Unity .meta paths:", result.orphanMetas);
  printDiagnosticList("Missing tracked repository .meta paths:", result.missingRepositoryMetas);
  printDiagnosticList("Orphan tracked repository .meta paths:", result.orphanRepositoryMetas);
  printDiagnosticList(
    "Invalid Unity C# .meta file shapes:",
    result.invalidCsharpMetas,
    (invalid) => `${invalid.path}: ${invalid.reason}`
  );

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
  buildStandardCsharpMetaContent,
  buildLocalTarArchiveSpec,
  collectReleaseArtifacts,
  collectDryRunEntries,
  collectTarballEntries,
  collectTrackedRepositoryPaths,
  computeRequiredMetaPaths,
  findForbiddenTarballPaths,
  getCsharpMetaShapeViolation,
  normalizePackEntry,
  parsePackJsonEntries,
  readTarballPackageJson,
  runValidation,
  validateCsharpMetaFiles,
  validatePackEntries,
  validateReleaseArtifacts,
  validateRepositoryMetaPairs,
  validateRepositoryUnityMetaFiles
};
