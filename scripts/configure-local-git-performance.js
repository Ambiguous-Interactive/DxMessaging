#!/usr/bin/env node
"use strict";

const childProcess = require("child_process");
const { spawnPlatformCommandSync } = require("./lib/shell-command");

function runGit(args, options = {}) {
  return spawnPlatformCommandSync(
    "git",
    args,
    {
      cwd: options.cwd || process.cwd(),
      encoding: "utf8",
      stdio: options.stdio || ["ignore", "pipe", "pipe"]
    },
    childProcess.spawnSync
  );
}

function getRepoRoot(cwd, runGitFn) {
  const result = runGitFn(["rev-parse", "--show-toplevel"], { cwd });
  if (result.error || result.status !== 0) {
    return null;
  }
  const root = String(result.stdout || "").trim();
  return root.length > 0 ? root : null;
}

function parseGitVersion(stdout) {
  const match = String(stdout || "").match(/git version\s+(\d+)\.(\d+)\.(\d+)/i);
  if (!match) {
    return null;
  }
  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3])
  };
}

function versionAtLeast(version, major, minor, patch = 0) {
  if (!version) {
    return false;
  }
  if (version.major !== major) {
    return version.major > major;
  }
  if (version.minor !== minor) {
    return version.minor > minor;
  }
  return version.patch >= patch;
}

function getLocalConfigValue(runGitFn, repoRoot, key) {
  const result = runGitFn(["config", "--local", "--get", key], { cwd: repoRoot });
  if (!result || result.error || result.status !== 0) {
    return null;
  }
  const value = String(result.stdout || "").trim();
  return value.length > 0 ? value : null;
}

function setLocalConfigWhenUnset(runGitFn, repoRoot, key, value, changed, warnings, log) {
  const existing = getLocalConfigValue(runGitFn, repoRoot, key);
  if (existing !== null) {
    log(`git performance: ${key} already set locally; leaving it unchanged.`);
    return;
  }

  const result = runGitFn(["config", "--local", key, value], {
    cwd: repoRoot,
    stdio: "inherit"
  });
  if (!result.error && result.status === 0) {
    changed.push(key);
  } else {
    warnings.push(`failed to enable ${key}`);
  }
}

function configureLocalGitPerformance(options = {}) {
  const cwd = options.cwd || process.cwd();
  const platform = options.platform || process.platform;
  const runGitFn = options.runGitFn || runGit;
  const log = options.log || console.log;
  const warn = options.warn || console.warn;

  const repoRoot = getRepoRoot(cwd, runGitFn);
  if (!repoRoot) {
    log("git performance: not inside a Git worktree; skipping local Git optimizations.");
    return { ok: true, skipped: true, changed: [] };
  }

  const changed = [];
  const warnings = [];

  const testUntracked = runGitFn(["update-index", "--test-untracked-cache"], { cwd: repoRoot });
  if (!testUntracked.error && testUntracked.status === 0) {
    setLocalConfigWhenUnset(
      runGitFn,
      repoRoot,
      "core.untrackedCache",
      "true",
      changed,
      warnings,
      log
    );
  } else {
    log("git performance: core.untrackedCache unsupported here; leaving it unchanged.");
  }

  if (platform === "win32" || platform === "darwin") {
    const versionResult = runGitFn(["--version"], { cwd: repoRoot });
    const version = parseGitVersion(versionResult.stdout);
    if (!versionResult.error && versionResult.status === 0 && versionAtLeast(version, 2, 37, 0)) {
      setLocalConfigWhenUnset(
        runGitFn,
        repoRoot,
        "core.fsmonitor",
        "true",
        changed,
        warnings,
        log
      );
    } else {
      log("git performance: built-in core.fsmonitor unsupported here; leaving it unchanged.");
    }
  } else {
    log("git performance: built-in core.fsmonitor is only enabled automatically on Windows/macOS.");
  }

  for (const warning of warnings) {
    warn(`git performance: ${warning}; continuing.`);
  }

  if (changed.length > 0) {
    log(`git performance: enabled ${changed.join(", ")} in local Git config.`);
  }

  return {
    ok: true,
    skipped: false,
    changed,
    warnings
  };
}

if (require.main === module) {
  configureLocalGitPerformance();
  process.exit(0);
}

module.exports = {
  parseGitVersion,
  versionAtLeast,
  getLocalConfigValue,
  configureLocalGitPerformance
};
