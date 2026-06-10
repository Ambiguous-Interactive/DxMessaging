#!/usr/bin/env node
/*
  Fix C# method names containing underscores by converting them to PascalCase.
  - Converts names like Parse_Line_Bare -> ParseLineBare.
  - Skips names that start with op_ (operator overload metadata names).
  - By default, processes staged C# files; accepts explicit file paths.
*/

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");
const { normalizeToLf } = require("./lib/line-endings");

const METHOD_DECLARATION_PATTERN =
  /^\s*(?:(?:\[[^\]\r\n]+\]\s*)*)(?:(?:public|private|protected|internal)\s+)?(?:(?:static|virtual|override|abstract|sealed|async|new|extern|partial|unsafe|readonly)\s+)*(?:[\w<>\[\],.?]+\s+)+(?<name>[A-Za-z_]\w*_[A-Za-z0-9_]+)\s*(?:<[^>\r\n]+>\s*)?\(/gm;

const CSHARP_SOURCE_FILE_PATTERN = /\.cs$/i;
const META_FILE_PATTERN = /\.meta$/i;
const WINDOWS_POSIX_DRIVE_PATH_PATTERN = /^\/([A-Za-z])\/(.+)$/;
const DEBUG_ENV_VAR = "DXMSG_CSHARP_UNDERSCORE_DEBUG";

const EXCLUDED_DIRECTORY_PATTERNS = [
  /(^|[\\/])\.git([\\/]|$)/i,
  /(^|[\\/])node_modules([\\/]|$)/i,
  /(^|[\\/])Library([\\/]|$)/,
  /(^|[\\/])(Obj|obj)([\\/]|$)/,
  /(^|[\\/])Temp([\\/]|$)/i,
  /(^|[\\/])\.vs([\\/]|$)/,
  /(^|[\\/])\.venv([\\/]|$)/,
  /(^|[\\/])\.artifacts([\\/]|$)/,
  /(^|[\\/])site([\\/]|$)/
];

function isCsharpSourceFile(filePath) {
  if (typeof filePath !== "string" || filePath.trim().length === 0) {
    return false;
  }

  return CSHARP_SOURCE_FILE_PATTERN.test(filePath) && !META_FILE_PATTERN.test(filePath);
}

function stripOptionalWrappingQuotes(value) {
  if (value.length < 2) {
    return value;
  }

  const firstChar = value[0];
  const lastChar = value[value.length - 1];
  if ((firstChar === '"' && lastChar === '"') || (firstChar === "'" && lastChar === "'")) {
    return value.slice(1, -1);
  }

  return value;
}

function normalizeExplicitPathArg(rawArg) {
  if (typeof rawArg !== "string") {
    return "";
  }

  const withoutTrailingCarriageReturns = rawArg.replace(/\r+$/g, "");
  const trimmed = withoutTrailingCarriageReturns.trim();
  if (trimmed.length === 0) {
    return "";
  }

  return stripOptionalWrappingQuotes(trimmed).replace(/\r+$/g, "").trim();
}

function toWindowsAbsolutePathFromPosixDrivePath(value) {
  const match = WINDOWS_POSIX_DRIVE_PATH_PATTERN.exec(value);
  if (!match) {
    return "";
  }

  const driveLetter = match[1].toUpperCase();
  const segments = match[2].replace(/\//g, "\\");
  return `${driveLetter}:\\${segments}`;
}

function resolveCandidatePath(
  repoRoot,
  rawArg,
  { platform = process.platform, existsSync = fs.existsSync } = {}
) {
  const normalizedArg = normalizeExplicitPathArg(rawArg);
  if (normalizedArg.length === 0) {
    return "";
  }

  const pathResolver = platform === "win32" ? path.win32 : path;
  const directCandidatePath = pathResolver.resolve(repoRoot, normalizedArg);

  if (existsSync(directCandidatePath) || platform !== "win32") {
    return directCandidatePath;
  }

  const windowsAbsolutePath = toWindowsAbsolutePathFromPosixDrivePath(normalizedArg);
  if (!windowsAbsolutePath) {
    return directCandidatePath;
  }

  const convertedCandidatePath = pathResolver.resolve(windowsAbsolutePath);
  if (existsSync(convertedCandidatePath)) {
    return convertedCandidatePath;
  }

  return directCandidatePath;
}

function getGitRepoRoot() {
  const result = spawnSync("git", ["rev-parse", "--show-toplevel"], {
    cwd: process.cwd(),
    encoding: "utf8"
  });

  if (result.status === 0 && result.stdout) {
    return result.stdout.trim();
  }

  if (result.status !== 0) {
    console.error(
      "Warning: unable to determine git repository root; defaulting to current working directory."
    );
  }

  return process.cwd();
}

function parseArgs(argv) {
  // Only `--check` and `--all` are flags; every other token -- bare paths, a
  // lone `-`, `--`, `=`-forms, and unrecognized `--opt` -- falls through to
  // fileArgs verbatim, in argv order (pre-commit passes filenames that way).
  const parsed = { checkOnly: false, allFiles: false, fileArgs: [] };

  for (const token of argv) {
    if (token === "--check") {
      parsed.checkOnly = true;
    } else if (token === "--all") {
      parsed.allFiles = true;
    } else {
      parsed.fileArgs.push(token);
    }
  }

  return parsed;
}

function isExcludedPath(fullPath) {
  return EXCLUDED_DIRECTORY_PATTERNS.some((pattern) => pattern.test(fullPath));
}

function debugLog(message) {
  if (process.env[DEBUG_ENV_VAR] === "1") {
    console.error(`[fix-csharp-underscore-methods] ${message}`);
  }
}

function pathModuleForPath(value) {
  return /^[A-Za-z]:[\\/]/.test(value) || value.includes("\\") ? path.win32 : path.posix;
}

function realpathIfExists(fullPath, { realpathSync = fs.realpathSync } = {}) {
  try {
    if (typeof realpathSync.native === "function") {
      return realpathSync.native(fullPath);
    }

    return realpathSync(fullPath);
  } catch {
    return fullPath;
  }
}

function canonicalPathForComparison(fullPath, options = {}) {
  const pathModule = pathModuleForPath(fullPath);
  return realpathIfExists(pathModule.resolve(fullPath), options);
}

function isPathInsideRoot(rootDir, fullPath, options = {}) {
  const canonicalRootDir = canonicalPathForComparison(rootDir, options);
  const canonicalFullPath = canonicalPathForComparison(fullPath, options);
  const pathModule =
    pathModuleForPath(canonicalRootDir) === path.win32
      ? path.win32
      : pathModuleForPath(canonicalFullPath);
  const normalizedRootDir = pathModule.resolve(canonicalRootDir);
  const normalizedFullPath = pathModule.resolve(canonicalFullPath);
  const relativePath = pathModule.relative(normalizedRootDir, normalizedFullPath);

  if (relativePath === "") {
    return true;
  }

  // On Windows, different drive letters can yield an absolute relative path.
  return !relativePath.startsWith("..") && !pathModule.isAbsolute(relativePath);
}

function isExcludedRepoLocalPath(repoRoot, fullPath, options = {}) {
  const canonicalRepoRoot = canonicalPathForComparison(repoRoot, options);
  const canonicalFullPath = canonicalPathForComparison(fullPath, options);
  const pathModule =
    pathModuleForPath(canonicalRepoRoot) === path.win32
      ? path.win32
      : pathModuleForPath(canonicalFullPath);
  const relativePath = pathModule.relative(
    pathModule.resolve(canonicalRepoRoot),
    pathModule.resolve(canonicalFullPath)
  );

  if (relativePath === "" || relativePath.startsWith("..") || pathModule.isAbsolute(relativePath)) {
    return false;
  }

  return relativePath
    .split(/[\\/]+/)
    .filter(Boolean)
    .some((segment) =>
      [
        ".git",
        "node_modules",
        "Library",
        "Obj",
        "obj",
        "Temp",
        ".vs",
        ".venv",
        ".artifacts",
        "site"
      ].includes(segment)
    );
}

// INTERNAL ONLY: rootDir is expected to be the repository root.
function walkCsharpFiles(rootDir, files = []) {
  let entries;

  try {
    entries = fs.readdirSync(rootDir, { withFileTypes: true });
  } catch {
    return files;
  }

  for (const entry of entries) {
    const fullPath = path.join(rootDir, entry.name);

    if (isExcludedPath(fullPath)) {
      continue;
    }

    if (entry.isDirectory()) {
      walkCsharpFiles(fullPath, files);
      continue;
    }

    if (entry.isFile() && isCsharpSourceFile(fullPath)) {
      files.push(fullPath);
    }
  }

  return files;
}

function resolveExplicitFiles(repoRoot, fileArgs) {
  const resolved = [];
  const seen = new Set();

  for (const rawArg of fileArgs) {
    const normalizedArg = normalizeExplicitPathArg(rawArg);
    if (!isCsharpSourceFile(normalizedArg)) {
      continue;
    }

    const candidatePath = resolveCandidatePath(repoRoot, normalizedArg);
    if (candidatePath.length === 0) {
      continue;
    }

    if (!fs.existsSync(candidatePath)) {
      debugLog(
        `Skipping missing explicit path: raw=${JSON.stringify(rawArg)} resolved=${candidatePath}`
      );
      continue;
    }

    // Apply excluded-directory patterns only for repo-local paths.
    if (
      isPathInsideRoot(repoRoot, candidatePath) &&
      isExcludedRepoLocalPath(repoRoot, candidatePath)
    ) {
      debugLog(`Skipping repo-local excluded path: ${candidatePath}`);
      continue;
    }

    const stats = fs.statSync(candidatePath);
    if (!stats.isFile() || !isCsharpSourceFile(candidatePath)) {
      debugLog(`Skipping non-file or non-C# explicit path: ${candidatePath}`);
      continue;
    }

    if (!seen.has(candidatePath)) {
      seen.add(candidatePath);
      resolved.push(candidatePath);
    }
  }

  return resolved;
}

function getStagedCsharpFiles(repoRoot) {
  const result = spawnSync(
    "git",
    ["diff", "--cached", "--name-only", "--diff-filter=ACMR", "--", "*.cs"],
    {
      cwd: repoRoot,
      encoding: "utf8"
    }
  );

  if (result.status !== 0) {
    console.error("Warning: unable to read staged C# files from git; no files were processed.");
    return [];
  }

  if (!result.stdout) {
    return [];
  }

  const files = [];
  const seen = new Set();

  for (const relativePath of normalizeToLf(result.stdout)
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)) {
    const fullPath = path.resolve(repoRoot, relativePath);

    if (!isCsharpSourceFile(fullPath)) {
      continue;
    }

    if (!fs.existsSync(fullPath)) {
      continue;
    }

    if (isExcludedPath(fullPath)) {
      continue;
    }

    if (!seen.has(fullPath)) {
      seen.add(fullPath);
      files.push(fullPath);
    }
  }

  return files;
}

function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function convertMethodNameToPascalCase(methodName) {
  return methodName
    .split("_")
    .filter((segment) => segment.length > 0)
    .map((segment) => {
      if (segment.length === 1) {
        return segment.toUpperCase();
      }

      return `${segment[0].toUpperCase()}${segment.slice(1)}`;
    })
    .join("");
}

function collectMethodRenames(content) {
  const methodRenames = new Map();
  let match;

  METHOD_DECLARATION_PATTERN.lastIndex = 0;

  while ((match = METHOD_DECLARATION_PATTERN.exec(content)) !== null) {
    const methodName = match.groups ? match.groups.name : "";

    if (!methodName || methodName.startsWith("op_")) {
      continue;
    }

    const newName = convertMethodNameToPascalCase(methodName);
    if (!newName || newName === methodName) {
      continue;
    }

    methodRenames.set(methodName, newName);
  }

  return methodRenames;
}

function applyMethodRenames(content, methodRenames) {
  let updatedContent = content;
  let renameCount = 0;

  for (const [oldName, newName] of methodRenames.entries()) {
    // Capture the non-identifier prefix so we can preserve it in the replacement without
    // relying on lookbehind, which is unavailable on older Node runtimes.
    const namePattern = new RegExp(
      `(^|[^A-Za-z0-9_])(${escapeRegExp(oldName)})(?![A-Za-z0-9_])`,
      "g"
    );

    const candidateContent = updatedContent.replace(
      namePattern,
      (_match, prefix) => `${prefix}${newName}`
    );
    if (candidateContent !== updatedContent) {
      updatedContent = candidateContent;
      renameCount += 1;
    }
  }

  return { updatedContent, renameCount };
}

function processFile(filePath, checkOnly) {
  let source;

  try {
    source = fs.readFileSync(filePath, "utf8");
  } catch {
    return { changed: false, renameCount: 0, methodRenames: new Map() };
  }

  const methodRenames = collectMethodRenames(source);
  if (methodRenames.size === 0) {
    return { changed: false, renameCount: 0, methodRenames };
  }

  const { updatedContent, renameCount } = applyMethodRenames(source, methodRenames);
  if (updatedContent === source || renameCount === 0) {
    return { changed: false, renameCount: 0, methodRenames };
  }

  if (!checkOnly) {
    fs.writeFileSync(filePath, updatedContent, "utf8");
  }

  return { changed: true, renameCount, methodRenames };
}

function resolveTargetFiles(repoRoot, parsedArgs) {
  if (parsedArgs.fileArgs.length > 0) {
    return resolveExplicitFiles(repoRoot, parsedArgs.fileArgs);
  }

  if (parsedArgs.allFiles) {
    return walkCsharpFiles(repoRoot);
  }

  return getStagedCsharpFiles(repoRoot);
}

function main() {
  const repoRoot = getGitRepoRoot();
  const parsedArgs = parseArgs(process.argv.slice(2));
  const files = resolveTargetFiles(repoRoot, parsedArgs);

  if (files.length === 0) {
    console.log("No C# files to process.");
    return 0;
  }

  const changedFiles = [];
  let totalRenamed = 0;

  for (const filePath of files) {
    const result = processFile(filePath, parsedArgs.checkOnly);

    if (!result.changed) {
      continue;
    }

    changedFiles.push(path.relative(repoRoot, filePath));
    totalRenamed += result.renameCount;
  }

  if (parsedArgs.checkOnly) {
    if (changedFiles.length > 0) {
      console.error("Found C# methods with underscores. Run fixer to update these files:");
      for (const relativePath of changedFiles) {
        console.error(`- ${relativePath}`);
      }
      console.error(
        "Method names must use PascalCase without underscores (for example: ParseLineInput)."
      );
      return 1;
    }

    console.log("No C# method naming fixes required.");
    return 0;
  }

  if (changedFiles.length > 0) {
    console.log(
      `Updated ${changedFiles.length} file(s); renamed ${totalRenamed} method identifier(s).`
    );
    for (const relativePath of changedFiles) {
      console.log(`Fixed: ${relativePath}`);
    }
  } else {
    console.log("No C# method naming fixes required.");
  }

  return 0;
}

// Export only the symbols consumed externally
// (scripts/__tests__/fix-csharp-underscore-methods.test.js).
module.exports = {
  isCsharpSourceFile,
  convertMethodNameToPascalCase,
  collectMethodRenames,
  applyMethodRenames,
  processFile
};

if (require.main === module) {
  process.exit(main());
}
