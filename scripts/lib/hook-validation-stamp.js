#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { spawnPlatformCommandSync } = require("./shell-command");

const STAMP_VERSION = 7;
const CHANGELOG_RELEVANT_PATHS = Object.freeze([
  "CHANGELOG.md",
  "package.json",
  "Runtime",
  "Editor",
  "SourceGenerators",
  "Samples~"
]);

function runGit(repoRoot, args, deps = {}) {
  const spawnFn = deps.spawnFn || spawnPlatformCommandSync;
  return spawnFn("git", args, {
    cwd: repoRoot,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });
}

function gitOutput(repoRoot, args, deps = {}) {
  const result = runGit(repoRoot, args, deps);
  const status = result && typeof result.status === "number" ? result.status : 1;
  if (status !== 0 || (result && result.error)) {
    const detail = `${(result && result.stderr) || ""}\n${(result && result.stdout) || ""}`.trim();
    throw new Error(detail || `git ${args.join(" ")} failed`);
  }
  return String((result && result.stdout) || "");
}

function stampPath(repoRoot, hookName, deps = {}) {
  const rel = gitOutput(
    repoRoot,
    ["rev-parse", "--git-path", `dxmsg-${hookName}-stamp.json`],
    deps
  ).trim();
  return path.isAbsolute(rel) ? rel : path.join(repoRoot, rel);
}

function sha256(value) {
  return crypto.createHash("sha256").update(value).digest("hex");
}

function sha256GitOutput(repoRoot, args, deps = {}) {
  return sha256(gitOutput(repoRoot, args, deps));
}

function zeroSeparatedPaths(value) {
  return zeroSeparatedParts(value).sort();
}

function zeroSeparatedParts(value) {
  return String(value || "")
    .split("\0")
    .filter((entry) => entry.length > 0);
}

function hashUntrackedFiles(repoRoot, relPaths, deps = {}) {
  const readFileSyncFn = deps.readFileSyncFn || fs.readFileSync.bind(fs);
  const hash = crypto.createHash("sha256");
  for (const rel of relPaths) {
    hash.update("path\0");
    hash.update(rel);
    hash.update("\0");
    try {
      hash.update(readFileSyncFn(path.join(repoRoot, rel)));
    } catch (error) {
      hash.update("unavailable\0");
      hash.update(error && error.code ? error.code : "read-error");
    }
    hash.update("\0");
  }
  return hash.digest("hex");
}

function hashPathList(relPaths) {
  const hash = crypto.createHash("sha256");
  for (const rel of relPaths) {
    hash.update("path\0");
    hash.update(rel);
    hash.update("\0");
  }
  return hash.digest("hex");
}

function parseRawDiffEntries(value) {
  const parts = zeroSeparatedParts(value);
  const entries = [];
  for (let index = 0; index < parts.length; ) {
    const header = parts[index++];
    const status = (header.trim().split(/\s+/).pop() || "").trim();
    const statusKind = status[0] || "";
    const sourcePath = parts[index++];
    if (!header || !sourcePath) {
      break;
    }

    if (statusKind === "R" || statusKind === "C") {
      const destinationPath = parts[index++];
      entries.push({
        header,
        path: destinationPath || sourcePath
      });
      continue;
    }

    entries.push({ header, path: sourcePath });
  }

  return entries.sort((left, right) => {
    const pathOrder = left.path.localeCompare(right.path);
    return pathOrder !== 0 ? pathOrder : left.header.localeCompare(right.header);
  });
}

function hashTrackedRawDiffState(repoRoot, rawDiff, deps = {}) {
  const readFileSyncFn = deps.readFileSyncFn || fs.readFileSync.bind(fs);
  const hash = crypto.createHash("sha256");
  for (const entry of parseRawDiffEntries(rawDiff)) {
    hash.update("raw\0");
    hash.update(entry.header);
    hash.update("\0path\0");
    hash.update(entry.path);
    hash.update("\0content\0");
    try {
      hash.update(readFileSyncFn(path.join(repoRoot, entry.path)));
    } catch (error) {
      hash.update("unavailable\0");
      hash.update(error && error.code ? error.code : "read-error");
    }
    hash.update("\0");
  }
  return hash.digest("hex");
}

function hashGitIndexFile(repoRoot, deps = {}) {
  const readFileSyncFn = deps.readFileSyncFn || fs.readFileSync.bind(fs);
  const rel = gitOutput(repoRoot, ["rev-parse", "--git-path", "index"], deps).trim();
  const filePath = path.isAbsolute(rel) ? rel : path.join(repoRoot, rel);
  return sha256(readFileSyncFn(filePath));
}

function resolveHead(repoRoot, deps = {}) {
  const result = runGit(repoRoot, ["rev-parse", "--verify", "HEAD"], deps);
  const status = result && typeof result.status === "number" ? result.status : 1;
  if (status === 0 && !result.error) {
    return String(result.stdout || "").trim();
  }
  return "UNBORN";
}

function fingerprintPreCommitGitState(repoRoot, deps = {}) {
  const changelogUntrackedPaths = zeroSeparatedPaths(
    gitOutput(
      repoRoot,
      ["ls-files", "--others", "--exclude-standard", "-z", "--", ...CHANGELOG_RELEVANT_PATHS],
      deps
    )
  );
  return {
    head: resolveHead(repoRoot, deps),
    indexTree: gitOutput(repoRoot, ["write-tree"], deps).trim(),
    changelogUnstagedDiffHash: sha256GitOutput(
      repoRoot,
      ["diff", "--binary", "--no-ext-diff", "--", ...CHANGELOG_RELEVANT_PATHS],
      deps
    ),
    changelogUntrackedFilesHash: hashUntrackedFiles(repoRoot, changelogUntrackedPaths, deps)
  };
}

function fingerprintPrePushGitState(repoRoot, deps = {}) {
  const untrackedPaths = zeroSeparatedPaths(
    gitOutput(repoRoot, ["ls-files", "--others", "--exclude-standard", "-z"], deps)
  );
  const rawTrackedDiff = gitOutput(
    repoRoot,
    ["diff-files", "--raw", "--abbrev=40", "-z", "--"],
    deps
  );
  return {
    head: resolveHead(repoRoot, deps),
    indexFileHash: hashGitIndexFile(repoRoot, deps),
    unstagedTrackedWorktreeStateHash: hashTrackedRawDiffState(repoRoot, rawTrackedDiff, deps),
    untrackedPathCount: untrackedPaths.length,
    untrackedPathsHash: hashPathList(untrackedPaths)
  };
}

function fingerprintGitState(repoRoot, deps = {}) {
  return fingerprintPreCommitGitState(repoRoot, deps);
}

function fingerprintHookGitState(repoRoot, hookName, deps = {}) {
  if (hookName === "pre-push") {
    return fingerprintPrePushGitState(repoRoot, deps);
  }
  return fingerprintPreCommitGitState(repoRoot, deps);
}

function writeHookValidationStamp(repoRoot, hookName, deps = {}) {
  const writeFileSyncFn = deps.writeFileSyncFn || fs.writeFileSync.bind(fs);
  const mkdirSyncFn = deps.mkdirSyncFn || fs.mkdirSync.bind(fs);
  const filePath = stampPath(repoRoot, hookName, deps);
  const fingerprint = fingerprintHookGitState(repoRoot, hookName, deps);
  if (hookName === "pre-push" && fingerprint.untrackedPathCount !== 0) {
    throw new Error("Refusing to write pre-push validation stamp with untracked paths present");
  }

  const payload = {
    version: STAMP_VERSION,
    hookName,
    fingerprint,
    writtenAt: new Date().toISOString()
  };

  mkdirSyncFn(path.dirname(filePath), { recursive: true });
  writeFileSyncFn(filePath, `${JSON.stringify(payload)}\n`, "utf8");
  return filePath;
}

function hasValidHookValidationStamp(repoRoot, hookName, deps = {}) {
  const readFileSyncFn = deps.readFileSyncFn || fs.readFileSync.bind(fs);
  let filePath;
  try {
    filePath = stampPath(repoRoot, hookName, deps);
    const parsed = JSON.parse(readFileSyncFn(filePath, "utf8"));
    if (
      !parsed ||
      parsed.version !== STAMP_VERSION ||
      parsed.hookName !== hookName ||
      !parsed.fingerprint
    ) {
      return { valid: false, reason: "stamp-shape", filePath };
    }

    const current = fingerprintHookGitState(repoRoot, hookName, deps);
    const stamped = parsed.fingerprint;
    const valid = Object.keys(current).every((key) => stamped[key] === current[key]);
    return {
      valid,
      reason: valid ? "match" : "fingerprint-mismatch",
      filePath
    };
  } catch (error) {
    return {
      valid: false,
      reason: error && error.code === "ENOENT" ? "missing" : "unavailable",
      filePath
    };
  }
}

module.exports = {
  CHANGELOG_RELEVANT_PATHS,
  STAMP_VERSION,
  stampPath,
  fingerprintPreCommitGitState,
  fingerprintPrePushGitState,
  fingerprintGitState,
  parseRawDiffEntries,
  hashGitIndexFile,
  writeHookValidationStamp,
  hasValidHookValidationStamp
};
