/**
 * @fileoverview Version-parity tests for the prettier hook entry.
 *
 * Round-4 inlines the prettier hook (cspell / markdownlint pattern) so the
 * hook no longer routes through `scripts/run-managed-prettier.js`. The
 * inlined entry pins a literal `prettier@<version>` for the npx fallback
 * branch that fires only on cold caches. That literal MUST match the
 * `prettier` version declared in `package.json` devDependencies, otherwise a
 * cold-cache machine would format with a different version than `npm
 * install` resolves -- the exact drift the wrapper used to prevent at
 * runtime cost.
 *
 * Companion to `cspell-version-parity.test.js`. Both tests are the cheap
 * static replacement for the deleted managed wrapper scripts.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");
const PRE_COMMIT_CONFIG_PATH = path.join(REPO_ROOT, ".pre-commit-config.yaml");
const PRETTIER_VERSION_LIB_PATH = path.join(REPO_ROOT, "scripts", "lib", "prettier-version.js");

function readPackageJson() {
  return JSON.parse(fs.readFileSync(PACKAGE_JSON_PATH, "utf8"));
}

function readPreCommitConfig() {
  return fs.readFileSync(PRE_COMMIT_CONFIG_PATH, "utf8");
}

function normalizeVersion(version) {
  if (typeof version !== "string") return null;
  const trimmed = version.trim().replace(/^[~^]/, "");
  if (!/^\d+\.\d+\.\d+(?:[-+].+)?$/.test(trimmed)) return null;
  return trimmed;
}

function findHookBlockText(content, hookId) {
  const lines = content.split(/\r\n|\r|\n/);
  const startIndex = lines.findIndex((line) =>
    new RegExp(`^\\s*-\\s+id:\\s*${hookId}\\s*$`).test(line)
  );
  if (startIndex < 0) return null;
  const out = [lines[startIndex]];
  for (let i = startIndex + 1; i < lines.length; i++) {
    if (/^\s*-\s+id:\s*\S+\s*$/.test(lines[i])) break;
    out.push(lines[i]);
  }
  return out.join("\n");
}

function findPinnedPrettierFallbackVersion(blockText) {
  if (!blockText) return null;
  // The hook entry routes through scripts/run-managed-prettier.js, which
  // reads the pinned fallback spec from scripts/lib/prettier-version.js.
  // The block-level parity check matches any `prettier@<semver>` mention
  // (typically in the hook description / comment); the
  // FALLBACK_PRETTIER_SPEC test below is the canonical pin.
  const re = /prettier@(\d+\.\d+\.\d+(?:[-+][\w.]+)?)/;
  const match = blockText.match(re);
  return match ? match[1] : null;
}

function findLocalPrettierBinReference(blockText) {
  if (!blockText) return null;
  // The managed wrapper script reads LOCAL_PRETTIER_BIN at this path; the
  // hook block must reference it either inline (legacy) or in the
  // description so a future audit reading the hook block alone can
  // confirm the local-bin contract.
  return /\bnode_modules\/prettier\/bin\/prettier\.cjs\b/.test(blockText);
}

describe("prettier hook version parity", () => {
  const pkg = readPackageJson();
  const config = readPreCommitConfig();
  const block = findHookBlockText(config, "prettier");
  const declaredVersion = normalizeVersion(pkg.devDependencies?.prettier);
  const fallbackVersion = findPinnedPrettierFallbackVersion(block);

  test("package.json devDependencies.prettier is a concrete version", () => {
    expect(declaredVersion).not.toBeNull();
    expect(declaredVersion).toMatch(/^\d+\.\d+\.\d+/);
  });

  test("prettier hook block exists in .pre-commit-config.yaml", () => {
    expect(block).not.toBeNull();
  });

  test("prettier hook entry references the local devDependency bin", () => {
    // The local-bin branch is the fast path; without it the cold-cache
    // fallback fires every time and the perf budget regresses.
    expect(findLocalPrettierBinReference(block)).toBe(true);
  });

  test("prettier hook entry pins the same npx fallback version as package.json", () => {
    expect(fallbackVersion).not.toBeNull();
    expect(fallbackVersion).toBe(declaredVersion);
  });

  test("FALLBACK_PRETTIER_SPEC in scripts/lib/prettier-version.js matches the hook fallback", () => {
    // The wrapper script `scripts/run-managed-prettier.js` is still used
    // by ad-hoc npm scripts (`format:md`, `check:prettier:hooks`, ...)
    // and reads its fallback spec from this constant. Drift between the
    // hook-entry literal and the wrapper's fallback would mean the same
    // command produces different formatting depending on the entry
    // point, which is exactly what version parity is supposed to
    // prevent.
    const libSource = fs.readFileSync(PRETTIER_VERSION_LIB_PATH, "utf8");
    const libMatch = libSource.match(
      /FALLBACK_PRETTIER_SPEC\s*=\s*"prettier@(\d+\.\d+\.\d+(?:[-+][\w.]+)?)"/
    );
    expect(libMatch).not.toBeNull();
    expect(libMatch[1]).toBe(declaredVersion);
  });
});
