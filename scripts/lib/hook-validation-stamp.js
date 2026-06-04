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

function fingerprintGitState(repoRoot, deps = {}) {
  return fingerprintPreCommitGitState(repoRoot, deps);
}

function fingerprintHookGitState(repoRoot, hookName, deps = {}) {
  if (hookName !== "pre-commit") {
    throw new Error(`Unsupported hook validation stamp: ${hookName}`);
  }
  return fingerprintPreCommitGitState(repoRoot, deps);
}

function writeHookValidationStamp(repoRoot, hookName, deps = {}) {
  if (hookName !== "pre-commit") {
    throw new Error(`Unsupported hook validation stamp: ${hookName}`);
  }
  const writeFileSyncFn = deps.writeFileSyncFn || fs.writeFileSync.bind(fs);
  const mkdirSyncFn = deps.mkdirSyncFn || fs.mkdirSync.bind(fs);
  const filePath = stampPath(repoRoot, hookName, deps);
  const fingerprint = fingerprintHookGitState(repoRoot, hookName, deps);

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
    if (hookName !== "pre-commit") {
      return { valid: false, reason: "unsupported-hook", filePath: undefined };
    }
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
  fingerprintGitState,
  writeHookValidationStamp,
  hasValidHookValidationStamp
};
