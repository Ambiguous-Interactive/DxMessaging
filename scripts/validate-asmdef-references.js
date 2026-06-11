#!/usr/bin/env node
/**
 * @fileoverview Static regression guard for Unity assembly definition
 * (`*.asmdef`) reference misconfiguration.
 *
 * Background (the bug this guards against)
 * ----------------------------------------
 * Unity only honors an asmdef's `precompiledReferences` array when
 * `overrideReferences` is `true`. When `overrideReferences` is `false` (or
 * missing, which defaults to false) Unity SILENTLY IGNORES every entry in
 * `precompiledReferences`. The asmdef still compiles in the Editor -- which
 * auto-supplies many BCL facade assemblies -- so the misconfiguration is
 * invisible locally. It only surfaces as a player build failure: a standalone
 * IL2CPP build that lacks the auto-supplied reference fails with errors such
 * as `CS0103: The name 'Unsafe' does not exist`.
 *
 * In other words, `precompiledReferences` populated alongside
 * `overrideReferences: false` is ALWAYS dead config, and it can hide a real
 * missing player reference. This validator fails the build on that exact
 * fingerprint so the dead config can never silently regress.
 *
 * Scope
 * -----
 * Only the package's OWN asmdefs are scanned (those under `Runtime/`,
 * `Editor/`, `Tests/`, and `Samples~/`). Third-party / cache trees such as
 * `node_modules/` and `.unity-test-project/Library/PackageCache/` are excluded
 * so the guard never reports on assemblies the package does not own.
 *
 * PRIMARY check (implemented): non-empty `precompiledReferences` with
 *   `overrideReferences` falsy. Always a violation.
 *
 * SECONDARY check (intentionally NOT implemented -- see SECONDARY_SCOPE_NOTE):
 *   verifying that an actively-overridden precompiled reference resolves to a
 *   managed plugin enabled for a non-Editor player platform. This repo cannot
 *   resolve it without false positives (the Tests asmdefs legitimately
 *   override `nunit.framework.dll`, which Unity supplies for test assemblies
 *   and is not shipped as a runtime plugin). Implementing it would flag valid
 *   config, so PRIMARY is the whole guard. Correctness over coverage.
 *
 * Usage:
 *   node scripts/validate-asmdef-references.js          # human-readable report
 *   node scripts/validate-asmdef-references.js --check  # exit non-zero on failure
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");
const { normalizeToLf } = require("./lib/line-endings");
const { toPosixPath } = require("./lib/path-classifier");

const repoRoot = path.resolve(__dirname, "..");

/**
 * Top-level directories that hold the package's OWN assembly definitions.
 * Anything outside these (node_modules, the embedded Unity test project's
 * PackageCache, etc.) is third-party or cache and is never our config to fix.
 */
const PACKAGE_SOURCE_PREFIXES = ["Runtime/", "Editor/", "Tests/", "Samples~/"];

/**
 * Why SECONDARY is out of scope, surfaced both in source and (optionally) in
 * the validator's own report so the boundary is discoverable at runtime.
 */
const SECONDARY_SCOPE_NOTE =
  "SECONDARY managed-plugin platform resolution is intentionally out of scope: " +
  "it cannot be determined here without false positives (e.g. nunit.framework.dll " +
  "is supplied by Unity for test assemblies, not shipped as a runtime plugin).";

/**
 * Run `git ls-files <args>` from the repo root and return the path list.
 *
 * Per repo policy for metadata-dependent validators, a git failure is a HARD
 * failure (it throws), never a silent pass: a guard that cannot see the files
 * must not report "clean".
 *
 * @param {string[]} args - Arguments after `ls-files`.
 * @param {Function} execFileSyncImpl - Injectable for tests.
 * @returns {string[]} Repo-relative POSIX paths (already POSIX from git).
 */
function gitListFiles(args, execFileSyncImpl = execFileSync) {
  const output = execFileSyncImpl("git", ["ls-files", ...args], {
    cwd: repoRoot,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });

  return normalizeToLf(output)
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/**
 * Decide whether a repo-relative path is one of the package's own asmdefs.
 *
 * @param {string} relativePath - Repo-relative path (POSIX or native).
 * @returns {boolean}
 */
function isOwnPackageAsmdef(relativePath) {
  const posix = toPosixPath(relativePath);
  if (!posix.endsWith(".asmdef")) {
    return false;
  }
  return PACKAGE_SOURCE_PREFIXES.some((prefix) => posix.startsWith(prefix));
}

/**
 * Discover every asmdef owned by this package (tracked + staged + untracked,
 * mirroring the candidate-file pattern used by other repo validators so an
 * in-flight asmdef is guarded before it is even committed).
 *
 * @param {Function} execFileSyncImpl - Injectable for tests.
 * @returns {string[]} Sorted, de-duplicated repo-relative POSIX asmdef paths.
 */
function getOwnPackageAsmdefPaths(execFileSyncImpl = execFileSync) {
  const tracked = gitListFiles(["*.asmdef"], execFileSyncImpl);
  const untracked = gitListFiles(["--others", "--exclude-standard", "*.asmdef"], execFileSyncImpl);

  const all = [...tracked, ...untracked]
    .map((file) => toPosixPath(file))
    .filter((file) => isOwnPackageAsmdef(file));

  return [...new Set(all)].sort();
}

/**
 * Read and strict-JSON-parse an asmdef from disk.
 *
 * asmdef files are strict JSON (no comments), so JSON.parse is correct. Read
 * and parse errors are HARD failures (thrown) so a malformed asmdef can never
 * masquerade as "no violations".
 *
 * @param {string} relativePath - Repo-relative asmdef path.
 * @param {Function} readFileSyncImpl - Injectable for tests.
 * @returns {{ path: string, asmdef: object }}
 */
function readAsmdef(relativePath, readFileSyncImpl = fs.readFileSync) {
  const absolutePath = path.resolve(repoRoot, relativePath);
  let raw;
  try {
    raw = readFileSyncImpl(absolutePath, "utf8");
  } catch (error) {
    throw new Error(`Unable to read asmdef '${toPosixPath(relativePath)}': ${error.message}`);
  }

  let asmdef;
  try {
    asmdef = JSON.parse(normalizeToLf(raw));
  } catch (error) {
    throw new Error(
      `Unable to parse asmdef '${toPosixPath(relativePath)}' as JSON: ${error.message}`
    );
  }

  if (asmdef === null || typeof asmdef !== "object" || Array.isArray(asmdef)) {
    throw new Error(
      `asmdef '${toPosixPath(relativePath)}' did not contain a JSON object at its root.`
    );
  }

  return { path: toPosixPath(relativePath), asmdef };
}

/**
 * Normalize a single validator input entry into `{ path, asmdef }`.
 *
 * Accepts three shapes so the pure check is easy to unit-test and reuse:
 *   1. A string path  -> read + parse from disk.
 *   2. `{ path, asmdef }` -> use the pre-parsed object verbatim (synthetic).
 *   3. A bare asmdef object -> wrap with a synthetic display path.
 *
 * @param {string|object} entry
 * @param {number} index - Used to synthesize a path for bare objects.
 * @param {Function} readFileSyncImpl - Injectable for tests.
 * @returns {{ path: string, asmdef: object }}
 */
function normalizeEntry(entry, index, readFileSyncImpl) {
  if (typeof entry === "string") {
    return readAsmdef(entry, readFileSyncImpl);
  }

  if (entry && typeof entry === "object" && !Array.isArray(entry)) {
    if (typeof entry.path === "string" && entry.asmdef && typeof entry.asmdef === "object") {
      return { path: toPosixPath(entry.path), asmdef: entry.asmdef };
    }
    // Treat the object itself as the asmdef body.
    return { path: `<asmdef[${index}]>`, asmdef: entry };
  }

  throw new Error(
    `Invalid asmdef input at index ${index}: expected a path string or asmdef object.`
  );
}

/**
 * Coerce a possibly-missing `overrideReferences` field to a strict boolean.
 * Missing / non-boolean defaults to `false` (Unity's default), which is the
 * dangerous side: it means `precompiledReferences` would be ignored.
 *
 * @param {*} value
 * @returns {boolean}
 */
function resolveOverrideReferences(value) {
  return value === true;
}

/**
 * Coerce a possibly-missing `precompiledReferences` field to a string array.
 * Missing / non-array defaults to `[]`. Non-string members are stringified so
 * the error message is still useful on malformed-but-parseable input.
 *
 * @param {*} value
 * @returns {string[]}
 */
function resolvePrecompiledReferences(value) {
  if (!Array.isArray(value)) {
    return [];
  }
  return value.map((entry) => (typeof entry === "string" ? entry : String(entry)));
}

/**
 * PURE check. Given asmdef inputs (paths and/or pre-parsed objects), return the
 * list of PRIMARY violations: a non-empty `precompiledReferences` while
 * `overrideReferences` is falsy (the IL2CPP/standalone dead-config fingerprint).
 *
 * @param {(string|object)[]} entries - asmdef paths or `{path, asmdef}` / bare
 *   asmdef objects.
 * @param {object} [options]
 * @param {Function} [options.readFileSync] - Injectable fs.readFileSync for
 *   string-path entries (used by the real-repo end-to-end pass).
 * @returns {Array<{ type: string, path: string, ignoredReferences: string[], message: string }>}
 */
function findAsmdefReferenceViolations(entries, options = {}) {
  if (!Array.isArray(entries)) {
    throw new Error("findAsmdefReferenceViolations expects an array of asmdef entries.");
  }

  const readFileSyncImpl = options.readFileSync || fs.readFileSync;
  const violations = [];

  entries.forEach((entry, index) => {
    const { path: displayPath, asmdef } = normalizeEntry(entry, index, readFileSyncImpl);

    const overrideReferences = resolveOverrideReferences(asmdef.overrideReferences);
    const precompiledReferences = resolvePrecompiledReferences(asmdef.precompiledReferences);

    if (!overrideReferences && precompiledReferences.length > 0) {
      const quoted = precompiledReferences.map((ref) => `'${ref}'`).join(", ");
      violations.push({
        type: "dead-precompiled-references",
        path: displayPath,
        ignoredReferences: precompiledReferences,
        message:
          `${displayPath}: precompiledReferences [${quoted}] is set while ` +
          `"overrideReferences" is false (or missing). Unity ignores ` +
          `precompiledReferences unless overrideReferences is true, so this is ` +
          `dead config: it compiles in the Editor but can fail standalone/IL2CPP ` +
          `player builds (for example CS0103 "The name 'Unsafe' does not exist"). ` +
          `Fix one of: (a) set "overrideReferences": true if the assembly really ` +
          `needs these precompiled references; (b) remove the dead ` +
          `precompiledReferences entries; or (c) if a named DLL is a real player ` +
          `dependency, ship it in a runtime Plugins folder (not Editor-only) so ` +
          `players link against it.`
      });
    }
  });

  return violations;
}

/**
 * Validator entry point: discover the package's own asmdefs and run the PURE
 * check over them.
 *
 * @param {object} [options]
 * @param {boolean} [options.check] - When true, exit the process non-zero on
 *   failure (CLI contract shared with sibling validators).
 * @param {Function} [options.execFileSync] - Injectable for tests.
 * @param {Function} [options.readFileSync] - Injectable for tests.
 * @returns {{ valid: boolean, violations: Array, scanned: string[] }}
 */
function validateAsmdefReferences(options = {}) {
  const asmdefPaths = getOwnPackageAsmdefPaths(options.execFileSync);
  const violations = findAsmdefReferenceViolations(asmdefPaths, {
    readFileSync: options.readFileSync
  });

  if (violations.length === 0) {
    console.log(
      `asmdef reference validation passed: scanned ${asmdefPaths.length} package asmdef(s); ` +
        `no dead precompiledReferences found.`
    );
    console.log(SECONDARY_SCOPE_NOTE);
    return { valid: true, violations: [], scanned: asmdefPaths };
  }

  console.error(
    `asmdef reference validation failed: found ${violations.length} dead-config violation(s) ` +
      `across ${asmdefPaths.length} scanned asmdef(s).`
  );
  for (const violation of violations) {
    console.error(`  - ${violation.message}`);
  }

  if (options.check) {
    process.exit(1);
  }

  return { valid: false, violations, scanned: asmdefPaths };
}

if (require.main === module) {
  const args = process.argv.slice(2);

  try {
    const result = validateAsmdefReferences({ check: args.includes("--check") });
    if (!result.valid) {
      process.exitCode = 1;
    }
  } catch (error) {
    console.error("asmdef reference validation failed with error:", error.message);
    process.exit(1);
  }
}

// Export only the symbols consumed externally
// (scripts/__tests__/validate-asmdef-references.test.js).
module.exports = {
  isOwnPackageAsmdef,
  resolveOverrideReferences,
  resolvePrecompiledReferences,
  findAsmdefReferenceViolations
};
