#!/usr/bin/env node
// cspell:words lscache
/**
 * @fileoverview Validates NPM package meta file integrity.
 *
 * This script ensures that:
 * 1. Every .meta file in the package corresponds to an actual file or directory
 * 2. Every Unity-tracked file/directory has its .meta file included in the package
 *
 * Unity requires .meta files for every asset to maintain consistent GUIDs across
 * installations. Missing or orphaned .meta files can break Unity projects.
 *
 * Usage:
 *   node scripts/validate-npm-meta.js         # Validate the npm package
 *   node scripts/validate-npm-meta.js --check # Exit with error if validation fails
 */

"use strict";

const path = require("path");
const { normalizeToLf } = require("./lib/quote-parser");
const { spawnPlatformCommandSync } = require("./lib/shell-command");

/**
 * Normalize a packaged file path to a canonical form.
 *
 * `npm pack --json` and `tar -tzf` occasionally surface entries prefixed with
 * `./` (e.g. `./Runtime/Foo.cs`). Without normalization, downstream consumers
 * relying on `startsWith("Runtime/")` would silently skip those paths and miss
 * real regressions. Stripping a single leading `./` keeps the validator inputs
 * canonical so every consumer sees the same shape.
 *
 * @param {string} file - Package-relative file path
 * @returns {string} Canonical package-relative file path
 */
function normalizeTarballPath(file) {
  if (typeof file !== "string") {
    return file;
  }
  if (file.startsWith("./")) {
    return file.slice(2);
  }
  return file;
}

/**
 * Parse tar listing output into package-relative file paths.
 *
 * @param {string} tarOutput - Raw `tar -tzf` output
 * @returns {string[]} Package-relative file list
 */
function parseTarListingOutput(tarOutput) {
  return normalizeToLf(tarOutput)
    .split("\n")
    .filter((line) => line.trim())
    .map((line) => line.replace(/^package\//, ""))
    .map((line) => normalizeTarballPath(line))
    .filter((line) => line); // Remove empty strings
}

/**
 * Parse `npm pack --json --dry-run` output and return package-relative file paths.
 *
 * @param {string} packOutput - Raw JSON output from npm pack
 * @returns {string[]} Package-relative file list
 */
function parseNpmPackJsonOutput(packOutput) {
  const trimmedOutput = normalizeToLf(packOutput || "").trim();

  if (!trimmedOutput) {
    throw new Error("npm pack produced no output");
  }

  let parsedOutput;
  try {
    parsedOutput = JSON.parse(trimmedOutput);
  } catch (error) {
    throw new Error(`Unable to parse npm pack --json output: ${error.message}`);
  }

  if (
    !Array.isArray(parsedOutput) ||
    parsedOutput.length === 0 ||
    parsedOutput[0] === null ||
    typeof parsedOutput[0] !== "object"
  ) {
    throw new Error("npm pack --json output did not contain package metadata");
  }

  const packageInfo = parsedOutput[0];
  if (!Array.isArray(packageInfo.files)) {
    throw new Error("npm pack --json output did not include a files list");
  }

  const files = packageInfo.files
    .map((entry) => {
      if (typeof entry === "string") {
        return entry;
      }

      if (entry && typeof entry.path === "string") {
        return entry.path;
      }

      return "";
    })
    .map((entry) => normalizeTarballPath(entry))
    .filter((entry) => entry.length > 0);

  if (files.length === 0) {
    throw new Error("npm pack --json output contained an empty files list");
  }

  return files;
}

/**
 * Get list of files that would be included in the npm package.
 *
 * Uses npm's JSON dry-run output so the check is shell-safe and cross-platform.
 *
 * @returns {string[]} Array of file paths relative to package root
 */
function getPackageFiles() {
  const repoRoot = path.resolve(__dirname, "..");

  try {
    console.log("Computing package file list via npm pack --json --dry-run...");
    // --ignore-scripts is the recursion guard: when this validator is wired into
    // the `prepack` script in package.json, the inner `npm pack` would re-trigger
    // any `prepack` script and recurse into this validator indefinitely. Passing
    // --ignore-scripts skips lifecycle scripts entirely so the inner pack
    // resolves to a single one-shot listing.
    const packResult = spawnPlatformCommandSync(
      "npm",
      ["pack", "--json", "--dry-run", "--ignore-scripts"],
      {
        encoding: "utf8",
        cwd: repoRoot,
        stdio: ["ignore", "pipe", "pipe"]
      }
    );

    if (packResult.error) {
      throw packResult.error;
    }

    if (packResult.status !== 0) {
      const stderr = normalizeToLf(packResult.stderr || "").trim();
      throw new Error(
        `npm pack --json --dry-run failed with exit code ${packResult.status}${stderr ? `: ${stderr}` : ""}`
      );
    }

    return parseNpmPackJsonOutput(packResult.stdout || "");
  } catch (error) {
    if (error && error.code === "ENOENT") {
      console.error(
        "Error creating or reading npm package:",
        `${error.message}\n` +
          "npm was not found in this hook shell. Verify npm --version in the same shell used for git commits."
      );
    } else {
      console.error("Error creating or reading npm package:", error.message);
    }
    throw error;
  }
}

// Files that don't need .meta files because they are package metadata or non-Unity assets.
const metaRequirementExcludePatterns = [
  /^package\.json$/,
  /^package-lock\.json$/,
  /^node_modules\//,
  /^\.git\//,
  /^\.github\//
];

const developmentFileExcludePatterns = [
  /^\.ambiguous-organization-build-lock(?:\/|\.meta$|$)/,
  /^\.config(?:\/|\.meta$|$)/,
  /^\.devcontainer(?:\/|\.meta$|$)/,
  /^\.git(?:\/|\.meta$|$)/,
  /^\.gitattributes(?:\.meta)?$/,
  /^\.github(?:\/|\.meta$|$)/,
  /^\.husky(?:\/|\.meta$|$)/,
  /^\.llm(?:\/|\.meta$|$)/,
  /^\.unity-test-project(?:\/|\.meta$|$)/,
  /^\.venv(?:\/|\.meta$|$)/,
  /^\.(?:editorconfig|prettierrc(?:\.json)?|prettierignore|markdownlint(?:\.json|\.jsonc)|markdownlint-cli2\.jsonc|markdownlintignore|yamllint\.yaml|cspell\.json|lychee\.toml|csharpierignore|csharpierrc\.json|cursorrules|pre-commit-config\.yaml)(?:\.meta)?$/,
  /^AGENTS\.md(?:\.meta)?$/,
  /^CLAUDE\.md(?:\.meta)?$/,
  /^CONTRIBUTING\.md(?:\.meta)?$/,
  /^GH-PAGES-PLAN\.md(?:\.meta)?$/,
  /^PLAN\.md(?:\.meta)?$/,
  /^Tests(?:\/|\.meta$|$)/,
  /^SourceGenerators\/WallstopStudios\.DxMessaging\.SourceGenerators\.Tests(?:\/|\.meta$|$)/,
  /^scripts(?:\/|\.meta$|$)/,
  /^node_modules(?:\/|\.meta$|$)/,
  /^coverage(?:\/|\.meta$|$)/,
  /^docs\/(?:__pycache__\/|hooks\.py(?:\.meta)?$)/,
  /^jest\.config\.(?:js|mjs)(?:\.meta)?$/,
  /^mkdocs\.yml(?:\.meta)?$/,
  /^__pycache__(?:\/|\.meta$|$)/,
  /^progress(?:\/|\.meta$|$)/,
  /^requirements-docs\.txt(?:\.meta)?$/,
  /^site(?:\/|\.meta$|$)/,
  /^package-lock\.json(?:\.meta)?$/
];

const directoryMetaRequirementExcludePatterns = [
  // UPM hides Samples~ from the package asset tree; sample folders beneath it still need metadata files.
  /^Samples~$/
];

function matchesAnyPattern(file, patterns) {
  return patterns.some((pattern) => pattern.test(file));
}

function shouldSkipMetaRequirement(file) {
  return (
    matchesAnyPattern(file, metaRequirementExcludePatterns) ||
    matchesAnyPattern(file, developmentFileExcludePatterns)
  );
}

function shouldSkipDirectoryMetaRequirement(directory) {
  return (
    shouldSkipMetaRequirement(directory) ||
    matchesAnyPattern(directory, directoryMetaRequirementExcludePatterns)
  );
}

/**
 * Validate that all .meta files correspond to actual files/directories
 * @param {string[]} files - List of files in the package
 * @returns {Object} Validation result with errors array
 */
function validateMetaFilesHaveTargets(files) {
  const errors = [];
  const fileSet = new Set(files);

  for (const file of files) {
    if (file.endsWith(".meta")) {
      // Remove .meta extension to get the target path
      const targetPath = file.substring(0, file.length - 5);

      // Check if the target file exists directly
      const hasTargetFile = fileSet.has(targetPath);

      // Check if this is a directory .meta by seeing if any files start with targetPath/
      const targetPathPrefix = targetPath + "/";
      const hasFilesInDirectory = files.some((f) => f.startsWith(targetPathPrefix));

      if (!hasTargetFile && !hasFilesInDirectory) {
        errors.push({
          type: "orphaned-meta",
          file: file,
          message: `Meta file '${file}' has no corresponding file or directory in the package`
        });
      }
    }
  }

  return { valid: errors.length === 0, errors };
}

/**
 * Validate that all files/directories have corresponding .meta files
 * @param {string[]} files - List of files in the package
 * @returns {Object} Validation result with errors array
 */
function validateFilesHaveMetaFiles(files) {
  const errors = [];
  const metaFiles = new Set(files.filter((f) => f.endsWith(".meta")));
  const packageDirectories = new Set();

  for (const file of files) {
    // Skip .meta files themselves
    if (file.endsWith(".meta")) {
      continue;
    }

    // Skip files that don't need .meta files
    if (shouldSkipMetaRequirement(file)) {
      continue;
    }

    let directory = path.posix.dirname(file);
    while (directory && directory !== ".") {
      packageDirectories.add(directory);
      directory = path.posix.dirname(directory);
    }

    const metaPath = file + ".meta";
    if (!metaFiles.has(metaPath)) {
      errors.push({
        type: "missing-meta",
        file: file,
        message: `File '${file}' is missing its .meta file in the package`
      });
    }
  }

  for (const directory of packageDirectories) {
    if (shouldSkipDirectoryMetaRequirement(directory)) {
      continue;
    }

    const metaPath = directory + ".meta";
    if (!metaFiles.has(metaPath)) {
      errors.push({
        type: "missing-meta",
        file: directory,
        message: `Directory '${directory}' is missing its .meta file in the package`
      });
    }
  }

  return { valid: errors.length === 0, errors };
}

// Patterns that catch build artifacts and IDE state that must never ship in the npm tarball.
// See https://github.com/Ambiguous-Interactive/DxMessaging/issues/204 -- pre-2.1.8 npm tarballs shipped
// SourceGenerator `bin/` and `obj/` outputs whose paths had no .meta partner. Unity then
// emitted `GuidDB::CreateMetaFileMappings` warnings on every asset-database refresh.
const buildArtifactPatterns = [
  { pattern: /(^|\/)(bin|obj)\//, label: "build output directory (bin/ or obj/)" },
  { pattern: /\.pdb$/, label: "compiler debug symbol file (.pdb)" },
  { pattern: /\.tmp$/, label: "temporary build file (.tmp)" },
  { pattern: /\.csproj\.user$/, label: "per-user MSBuild settings (.csproj.user)" },
  { pattern: /(^|\/)\.vs\//, label: "Visual Studio workspace state (.vs/)" },
  { pattern: /(^|\/)\.idea\//, label: "JetBrains IDE state (.idea/)" },
  { pattern: /\.suo$/, label: "Visual Studio solution user options (.suo)" },
  { pattern: /\.DotSettings\.user$/, label: "ReSharper per-user settings (.DotSettings.user)" },
  { pattern: /\.lscache(\.meta)?$/, label: "C# Dev Kit per-project cache (.lscache)" },
  // Plain `.user` is checked last so the more specific .csproj.user / .DotSettings.user
  // patterns above own their richer messages first.
  { pattern: /\.user$/, label: "per-user IDE settings file (.user)" }
];

const issue204Reference = "https://github.com/Ambiguous-Interactive/DxMessaging/issues/204";

/**
 * Validate that no build artifacts, IDE state, or per-user files were packed.
 *
 * Issue #204 traced `GuidDB::CreateMetaFileMappings` warnings on every Unity
 * asset-database refresh back to SourceGenerator `bin/` and `obj/` outputs that
 * shipped in the npm tarball without `.meta` partners. This validator enforces
 * defense-in-depth: even if `.npmignore` and the `package.json` allowlist drift,
 * any build artifact reaching this stage is rejected with an explicit reference
 * to the originating issue.
 *
 * @param {string[]} tarballFiles - Package-relative file paths that would ship in the tarball
 * @returns {{valid: boolean, errors: Array<{type: string, file: string, message: string}>}}
 */
function validateNoBuildArtifactsInTarball(tarballFiles) {
  const errors = [];

  // Normalize at the boundary: callers (parseNpmPackJsonOutput / parseTarListingOutput)
  // already canonicalize, but the validators are also reachable directly from tests and
  // any future caller. Stripping a leading `./` here is the single source of truth.
  const normalizedFiles = tarballFiles.map((file) => normalizeTarballPath(file));

  for (const file of normalizedFiles) {
    for (const { pattern, label } of buildArtifactPatterns) {
      if (pattern.test(file)) {
        errors.push({
          type: "build-artifact-in-tarball",
          file: file,
          message:
            `Build artifact '${file}' (${label}) must not ship in the npm package. ` +
            `Issue #204 (${issue204Reference}) was caused by paths like this leaking into ` +
            `the tarball without .meta partners, producing GuidDB::CreateMetaFileMappings ` +
            `warnings on every Unity asset-database refresh.`
        });
        break;
      }
    }
  }

  return { valid: errors.length === 0, errors };
}

// Root-level files that ship in the package and require a .meta partner per package.json's
// "files" allowlist. Keep this list synchronized with package.json.
const rootShippedFilesRequiringMeta = new Set([
  "CHANGELOG.md",
  "LICENSE.md",
  "README.md",
  "Third Party Notices.md"
]);

// SourceGenerator-shipped files outside the canonical `*.cs` / `*.csproj` set that the
// package.json "files" allowlist explicitly publishes alongside their `.meta` partners.
// Keep this set synchronized with package.json -- adding an entry here without a matching
// allowlist entry (or vice versa) is the regression vector this validator is meant to catch.
const sourceGeneratorTrackedNonCodeFiles = new Set(["SourceGenerators/Directory.Build.props"]);

// The package MUST ship its own two analyzer assemblies
// (WallstopStudios.DxMessaging.SourceGenerators.dll and
// WallstopStudios.DxMessaging.Analyzer.dll) -- SetupCscRsp copies them to
// Assets/Plugins and activates them with Unity's RoslynAnalyzer label.
// Editor/Analyzers/ ALSO ships the Roslyn runtime
// deps (Microsoft.CodeAnalysis[.CSharp], System.Collections.Immutable,
// System.Reflection.Metadata, System.Runtime.CompilerServices.Unsafe)
// alongside them; the list below only enforces that the two REQUIRED analyzer
// assemblies (and their .meta partners) are present, and intentionally does NOT
// forbid the dep DLLs from shipping.
const requiredAnalyzerPackageFiles = [
  "Editor/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll",
  "Editor/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll.meta",
  "Editor/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll",
  "Editor/Analyzers/WallstopStudios.DxMessaging.Analyzer.dll.meta"
];

/**
 * Determine whether a packaged path is "Unity-relevant" -- i.e. Unity will look
 * for a `.meta` partner for it during asset import. This intentionally mirrors
 * the package.json "files" allowlist shape so the validator stays accurate when
 * the allowlist evolves.
 *
 * @param {string} file - Package-relative file path
 * @returns {boolean} `true` if `file` requires a sibling `.meta` to ship in the tarball
 */
function isUnityRelevantPackagedPath(file) {
  if (file.startsWith("Editor/") || file.startsWith("Runtime/") || file.startsWith("Samples~/")) {
    return true;
  }

  if (
    file.startsWith("SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/") &&
    (file.endsWith(".cs") || file.endsWith(".csproj"))
  ) {
    return true;
  }

  if (sourceGeneratorTrackedNonCodeFiles.has(file)) {
    return true;
  }

  if (rootShippedFilesRequiringMeta.has(file)) {
    return true;
  }

  return false;
}

/**
 * Determine whether a packaged directory must have a `<dir>.meta` partner.
 * Mirrors the Unity rule that every imported folder needs a folder .meta so the
 * GUID mapping is stable across installs.
 *
 * @param {string} directory - Package-relative directory path
 * @returns {boolean} `true` if `directory` requires a sibling `<dir>.meta` to ship
 */
function isUnityRelevantPackagedDirectory(directory) {
  if (directory === "Samples~") {
    // Unity hides Samples~ itself from the asset tree; subdirectories still need .meta.
    return false;
  }

  if (
    directory === "Editor" ||
    directory === "Runtime" ||
    directory === "SourceGenerators" ||
    directory.startsWith("Editor/") ||
    directory.startsWith("Runtime/") ||
    directory.startsWith("Samples~/")
  ) {
    return true;
  }

  if (
    directory === "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators" ||
    directory.startsWith("SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/")
  ) {
    return true;
  }

  return false;
}

/**
 * Validate that every Unity-relevant file and directory shipped in the tarball
 * has its `.meta` partner shipped alongside it.
 *
 * Issue #204 (https://github.com/Ambiguous-Interactive/DxMessaging/issues/204) was triggered
 * by `.cs` files inside `bin/Debug/netstandard2.0/` reaching the published
 * tarball without `.meta` neighbours. Unity then logged
 * `GuidDB::CreateMetaFileMappings` warnings on every asset-database refresh.
 * This validator catches both the file-level and directory-level missing-meta
 * cases that #204 surfaced.
 *
 * @param {string[]} tarballFiles - Package-relative file paths that would ship in the tarball
 * @returns {{valid: boolean, errors: Array<{type: string, file: string, message: string}>}}
 */
function validatePublishedFilesArePairedWithMetas(tarballFiles) {
  const errors = [];
  // Normalize at the boundary so `./Runtime/Foo.cs` style entries cannot mask a missing
  // .meta partner via the `startsWith("Runtime/")` checks downstream.
  const normalizedFiles = tarballFiles.map((file) => normalizeTarballPath(file));
  const fileSet = new Set(normalizedFiles);
  const shippedDirectories = new Set();

  // File-level checks -- every Unity-relevant non-.meta path must have its .meta neighbour.
  for (const file of normalizedFiles) {
    if (file.endsWith(".meta")) {
      continue;
    }

    let directory = path.posix.dirname(file);
    while (directory && directory !== ".") {
      shippedDirectories.add(directory);
      directory = path.posix.dirname(directory);
    }

    if (!isUnityRelevantPackagedPath(file)) {
      continue;
    }

    const expectedMeta = file + ".meta";
    if (!fileSet.has(expectedMeta)) {
      errors.push({
        type: "missing-meta-in-tarball",
        file: expectedMeta,
        message:
          `Tarball is missing '${expectedMeta}' for shipped file '${file}'. ` +
          `Unity requires a .meta partner for every imported asset; absence triggers ` +
          `GuidDB::CreateMetaFileMappings warnings on every asset-database refresh, ` +
          `which is exactly the regression filed as issue #204 (${issue204Reference}).`
      });
    }
  }

  // Directory-level checks -- every shipped directory under Unity-relevant roots needs `<dir>.meta`.
  // The directory walk above already records every ancestor of every shipped file, so the
  // package roots (`Editor/`, `Runtime/`, `SourceGenerators/`) are covered without an extra
  // explicit pass: Unity-relevant subdirectories propagate up to their root directory entries.
  for (const directory of shippedDirectories) {
    if (!isUnityRelevantPackagedDirectory(directory)) {
      continue;
    }

    const expectedMeta = directory + ".meta";
    if (!fileSet.has(expectedMeta)) {
      errors.push({
        type: "missing-meta-in-tarball",
        file: expectedMeta,
        message:
          `Tarball is missing directory meta '${expectedMeta}' for shipped folder '${directory}/'. ` +
          `Unity requires '<folder>.meta' alongside each imported folder; without it, the asset ` +
          `database cannot resolve the folder GUID and emits GuidDB::CreateMetaFileMappings ` +
          `warnings on every refresh, the regression tracked by issue #204 (${issue204Reference}).`
      });
    }
  }

  return { valid: errors.length === 0, errors };
}

function validateRequiredAnalyzerFilesInTarball(tarballFiles) {
  const fileSet = new Set(tarballFiles.map((file) => normalizeTarballPath(file)));
  const errors = [];

  for (const file of requiredAnalyzerPackageFiles) {
    if (fileSet.has(file)) {
      continue;
    }

    errors.push({
      type: "missing-required-analyzer-file",
      file,
      message:
        `Tarball is missing required analyzer file '${file}'. ` +
        "SetupCscRsp copies DxMessaging analyzer DLLs into Assets/Plugins and activates them with the RoslynAnalyzer label; missing DLLs or .meta files break Unity package imports and CI compilation."
    });
  }

  return { valid: errors.length === 0, errors };
}

/**
 * Validate that development-only repository paths are not published.
 * @param {string[]} files - List of files in the package
 * @returns {Object} Validation result with errors array
 */
function validateDevelopmentFilesExcluded(files) {
  const errors = [];

  for (const file of files) {
    if (matchesAnyPattern(file, developmentFileExcludePatterns)) {
      errors.push({
        type: "development-file-in-package",
        file: file,
        message: `Development-only file '${file}' must not be included in the npm package`
      });
    }
  }

  return { valid: errors.length === 0, errors };
}

/**
 * Main validation function
 * @param {Object} options - Options for validation
 * @param {boolean} options.check - If true, exit with error code on validation failure
 * @returns {Object} Validation results
 */
function validateNpmMeta(options = {}) {
  console.log("Validating NPM package meta files...\n");

  const files = getPackageFiles();
  console.log(`Found ${files.length} files in package\n`);

  // Count .meta files
  const metaFileCount = files.filter((f) => f.endsWith(".meta")).length;
  const regularFileCount = files.length - metaFileCount;
  console.log(`  - Regular files: ${regularFileCount}`);
  console.log(`  - Meta files: ${metaFileCount}\n`);

  // Validate orphaned .meta files
  console.log("Checking for orphaned .meta files...");
  const orphanedResult = validateMetaFilesHaveTargets(files);
  if (orphanedResult.valid) {
    console.log("✓ All .meta files have corresponding files/directories\n");
  } else {
    console.log(`✗ Found ${orphanedResult.errors.length} orphaned .meta file(s):\n`);
    for (const error of orphanedResult.errors) {
      console.log(`  - ${error.message}`);
    }
    console.log();
  }

  // Validate missing .meta files
  console.log("Checking for missing .meta files...");
  const missingResult = validateFilesHaveMetaFiles(files);
  if (missingResult.valid) {
    console.log("✓ All files have corresponding .meta files\n");
  } else {
    console.log(`✗ Found ${missingResult.errors.length} file(s) missing .meta:\n`);
    for (const error of missingResult.errors) {
      console.log(`  - ${error.message}`);
    }
    console.log();
  }

  console.log("Checking for development-only package contents...");
  const developmentFilesResult = validateDevelopmentFilesExcluded(files);
  if (developmentFilesResult.valid) {
    console.log("✓ Development-only files are excluded from the package\n");
  } else {
    console.log(
      `✗ Found ${developmentFilesResult.errors.length} development-only file(s) in package:\n`
    );
    for (const error of developmentFilesResult.errors) {
      console.log(`  - ${error.message}`);
    }
    console.log();
  }

  // Issue #204 regression guards: bin/obj/IDE artifacts and unpaired .meta files in tarball.
  console.log("Checking for build artifacts and IDE state in tarball (issue #204 guard)...");
  const buildArtifactResult = validateNoBuildArtifactsInTarball(files);
  if (buildArtifactResult.valid) {
    console.log("✓ No build artifacts or per-user IDE state in tarball\n");
  } else {
    console.log(`✗ Found ${buildArtifactResult.errors.length} build artifact(s) in tarball:\n`);
    for (const error of buildArtifactResult.errors) {
      console.log(`  - ${error.message}`);
    }
    console.log();
  }

  console.log("Checking shipped paths are paired with .meta partners (issue #204 guard)...");
  const tarballMetaPairingResult = validatePublishedFilesArePairedWithMetas(files);
  if (tarballMetaPairingResult.valid) {
    console.log("✓ All shipped Unity-relevant paths have their .meta partners\n");
  } else {
    console.log(
      `✗ Found ${tarballMetaPairingResult.errors.length} missing .meta partner(s) in tarball:\n`
    );
    for (const error of tarballMetaPairingResult.errors) {
      console.log(`  - ${error.message}`);
    }
    console.log();
  }

  console.log("Checking required analyzer DLLs are included in the tarball...");
  const requiredAnalyzerResult = validateRequiredAnalyzerFilesInTarball(files);
  if (requiredAnalyzerResult.valid) {
    console.log("✓ Required analyzer DLLs and .meta files are included in the tarball\n");
  } else {
    console.log(
      `✗ Found ${requiredAnalyzerResult.errors.length} missing analyzer file(s) in tarball:\n`
    );
    for (const error of requiredAnalyzerResult.errors) {
      console.log(`  - ${error.message}`);
    }
    console.log();
  }

  // Summary
  const allValid =
    orphanedResult.valid &&
    missingResult.valid &&
    developmentFilesResult.valid &&
    buildArtifactResult.valid &&
    tarballMetaPairingResult.valid &&
    requiredAnalyzerResult.valid;
  if (allValid) {
    console.log("✓ NPM package meta file validation passed!");
    return { valid: true, errors: [] };
  } else {
    console.log("✗ NPM package meta file validation failed!");
    const allErrors = [
      ...orphanedResult.errors,
      ...missingResult.errors,
      ...developmentFilesResult.errors,
      ...buildArtifactResult.errors,
      ...tarballMetaPairingResult.errors,
      ...requiredAnalyzerResult.errors
    ];

    if (options.check) {
      process.exit(1);
    }

    return { valid: false, errors: allErrors };
  }
}

// Run validation if called directly
if (require.main === module) {
  const args = process.argv.slice(2);
  const check = args.includes("--check");

  try {
    validateNpmMeta({ check });
  } catch (error) {
    console.error("Validation failed with error:", error.message);
    process.exit(1);
  }
}

// Export for testing
module.exports = {
  getPackageFiles,
  parseNpmPackJsonOutput,
  parseTarListingOutput,
  validateDevelopmentFilesExcluded,
  validateMetaFilesHaveTargets,
  validateFilesHaveMetaFiles,
  validateNoBuildArtifactsInTarball,
  validatePublishedFilesArePairedWithMetas,
  validateRequiredAnalyzerFilesInTarball,
  validateNpmMeta
};
