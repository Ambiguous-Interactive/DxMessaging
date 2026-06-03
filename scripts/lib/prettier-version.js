"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.join(__dirname, "..", "..");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");
const PACKAGE_LOCK_PATH = path.join(REPO_ROOT, "package-lock.json");
const FALLBACK_PRETTIER_SPEC = "prettier@3.8.3";

function normalizePinnedVersion(version) {
  if (typeof version !== "string") {
    return null;
  }

  const trimmedVersion = version.trim().replace(/^[~^]/, "");
  if (!/^\d+\.\d+\.\d+$/.test(trimmedVersion)) {
    return null;
  }

  return trimmedVersion;
}

function getConfiguredPrettierSpec(readFileSyncFn = fs.readFileSync) {
  try {
    const packageJson = JSON.parse(readFileSyncFn(PACKAGE_JSON_PATH, "utf8"));
    const configuredVersion = normalizePinnedVersion(
      packageJson && packageJson.devDependencies && packageJson.devDependencies.prettier
    );

    if (configuredVersion) {
      return `prettier@${configuredVersion}`;
    }
  } catch {
    // Fall through to runtime fallback.
  }

  return null;
}

function getPinnedFallbackPrettierSpec(
  readFileSyncFn = fs.readFileSync,
  fallbackSpec = FALLBACK_PRETTIER_SPEC
) {
  try {
    const packageLock = JSON.parse(readFileSyncFn(PACKAGE_LOCK_PATH, "utf8"));
    const lockfileVersion = normalizePinnedVersion(
      packageLock &&
        packageLock.packages &&
        packageLock.packages["node_modules/prettier"] &&
        packageLock.packages["node_modules/prettier"].version
    );

    if (lockfileVersion) {
      return `prettier@${lockfileVersion}`;
    }
  } catch {
    // Fall through to static fallback when lockfile is unavailable or malformed.
  }

  return fallbackSpec;
}

function getPinnedPrettierSpec(readFileSyncFn = fs.readFileSync) {
  const configuredSpec = getConfiguredPrettierSpec(readFileSyncFn);
  return getPinnedFallbackPrettierSpec(readFileSyncFn, configuredSpec || FALLBACK_PRETTIER_SPEC);
}

module.exports = {
  REPO_ROOT,
  PACKAGE_JSON_PATH,
  PACKAGE_LOCK_PATH,
  FALLBACK_PRETTIER_SPEC,
  normalizePinnedVersion,
  getConfiguredPrettierSpec,
  getPinnedFallbackPrettierSpec,
  getPinnedPrettierSpec
};
