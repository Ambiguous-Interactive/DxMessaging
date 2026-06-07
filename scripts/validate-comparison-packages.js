#!/usr/bin/env node
/**
 * validate-comparison-packages.js
 *
 * Drift detector for the comparison-benchmark package single source.
 *
 * CONTRACT
 * --------
 * `.github/comparison-packages.json` is the SINGLE SOURCE OF TRUTH for the
 * OpenUPM scoped registry and the PINNED versions of the third-party packages
 * the comparison benchmarks measure against (MessagePipe, UniTask, Zenject,
 * UniRx, Unity Atoms core + base atoms):
 *
 *   {
 *     "registry": { "name": ..., "url": "https://...", "scopes": ["com.x", ...] },
 *     "packages": { "com.cysharp.messagepipe": "1.8.1", ... },        // x6
 *     "defines":  { "com.cysharp.messagepipe": "MESSAGEPIPE_PRESENT", ... },
 *     "minUnityForComparisons": "2021.3.0f1"
 *   }
 *
 * Three consumers read this file and MUST agree with it:
 *   1. The gated comparison asmdefs under Tests/Runtime/Comparisons/** (their
 *      versionDefines map package id -> define, and their defineConstraints gate
 *      compilation on those defines).
 *   2. The committed local-parity manifest
 *      `.unity-test-project/Packages/manifest.json` (the scoped registry + the
 *      six pinned dependencies, so a local Unity open reproduces CI exactly).
 *   3. The ephemeral CI manifest generator scripts/unity/run-ci-tests.ps1 (which
 *      reads this JSON at runtime -- a light text guard keeps it wired).
 *
 * Bump versions / scopes HERE only; THIS validator fails CI if any consumer
 * drifts. See .llm/skills/testing/comparison-parity-and-package-single-source.md.
 *
 * This script is PURE Node and dependency-free (fs + path + JSON.parse only) so
 * it runs in CI without an `npm install`.
 *
 * @usage
 *   node scripts/validate-comparison-packages.js
 *
 * @exitcodes
 *   0 - Success (single source valid, no consumer drift)
 *   1 - Validation failed (bad schema or one or more drift violations)
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.join(__dirname, "..");

const SOURCE_RELATIVE_PATH = ".github/comparison-packages.json";
const LOCAL_MANIFEST_RELATIVE_PATH = ".unity-test-project/Packages/manifest.json";
const COMPARISONS_RELATIVE_DIR = "Tests/Runtime/Comparisons";
const GENERATOR_RELATIVE_PATH = "scripts/unity/run-ci-tests.ps1";

/**
 * Reads and JSON-parses a repo-relative file.
 *
 * @param {string} repoRoot Repository root.
 * @param {string} relativePath Repo-relative POSIX path.
 * @returns {{ data: unknown, path: string }} Parsed object + absolute path.
 * @throws {Error} When the file is missing or not valid JSON.
 */
function loadJsonFile(repoRoot, relativePath) {
  const absolutePath = path.join(repoRoot, relativePath);
  let raw;
  try {
    raw = fs.readFileSync(absolutePath, "utf8");
  } catch (error) {
    throw new Error(`Cannot read '${relativePath}': ${error.message}`);
  }
  let data;
  try {
    data = JSON.parse(raw);
  } catch (error) {
    throw new Error(`'${relativePath}' is not valid JSON: ${error.message}`);
  }
  return { data, path: absolutePath };
}

/**
 * Returns true when `value` is a plain (non-array, non-null) object.
 *
 * @param {unknown} value Candidate.
 * @returns {boolean}
 */
function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

/**
 * Returns true when `id` is covered by `scope`: an exact match, or a dotted
 * descendant (`id === scope` or `id` startsWith `scope + "."`). A bare prefix
 * that is not a dot-boundary (e.g. scope "com.cy" vs id "com.cysharp.x") does
 * NOT count -- Unity scoped-registry scopes match on package-name segments.
 *
 * @param {string} id Package id.
 * @param {string} scope Registry scope.
 * @returns {boolean}
 */
function scopeCoversId(id, scope) {
  return id === scope || id.startsWith(`${scope}.`);
}

/**
 * Validates the single-source object's schema (registry / packages / defines /
 * scope coverage). Does NOT touch consumers.
 *
 * Rules:
 *   - root is an object;
 *   - registry.url is a non-empty https:// string;
 *   - registry.scopes is a non-empty array of non-empty strings;
 *   - packages is a non-empty object of id -> non-empty version string;
 *   - defines has EXACTLY one entry per package id (no missing, no extras),
 *     each a non-empty string;
 *   - every package id is covered by some registry scope.
 *
 * @param {unknown} data Parsed single-source object.
 * @returns {string[]} Human-readable error messages. Empty when valid.
 */
function validateSourceSchema(data) {
  const errors = [];

  if (!isPlainObject(data)) {
    return [`${SOURCE_RELATIVE_PATH}: root must be a JSON object.`];
  }

  // --- registry ---
  const registry = data.registry;
  if (!isPlainObject(registry)) {
    errors.push(`${SOURCE_RELATIVE_PATH}: \`registry\` must be an object.`);
  } else {
    if (typeof registry.url !== "string" || registry.url.length === 0) {
      errors.push(`${SOURCE_RELATIVE_PATH}: \`registry.url\` must be a non-empty string.`);
    } else if (!registry.url.startsWith("https://")) {
      errors.push(
        `${SOURCE_RELATIVE_PATH}: \`registry.url\` '${registry.url}' must start with 'https://'.`
      );
    }

    if (!Array.isArray(registry.scopes) || registry.scopes.length === 0) {
      errors.push(`${SOURCE_RELATIVE_PATH}: \`registry.scopes\` must be a non-empty array.`);
    } else {
      const everyScopeOk = registry.scopes.every(
        (scope) => typeof scope === "string" && scope.length > 0
      );
      if (!everyScopeOk) {
        errors.push(
          `${SOURCE_RELATIVE_PATH}: every entry of \`registry.scopes\` must be a non-empty string.`
        );
      }
    }
  }

  // --- packages ---
  const packages = data.packages;
  if (!isPlainObject(packages)) {
    errors.push(`${SOURCE_RELATIVE_PATH}: \`packages\` must be an object.`);
  } else {
    const packageIds = Object.keys(packages);
    if (packageIds.length === 0) {
      errors.push(`${SOURCE_RELATIVE_PATH}: \`packages\` must have at least one entry.`);
    }
    for (const id of packageIds) {
      const version = packages[id];
      if (typeof version !== "string" || version.length === 0) {
        errors.push(
          `${SOURCE_RELATIVE_PATH}: \`packages.${id}\` must be a non-empty version string.`
        );
      }
    }
  }

  // --- defines (one per package id, no extras) ---
  const defines = data.defines;
  if (!isPlainObject(defines)) {
    errors.push(`${SOURCE_RELATIVE_PATH}: \`defines\` must be an object.`);
  } else if (isPlainObject(packages)) {
    const packageIds = Object.keys(packages);
    const defineIds = Object.keys(defines);
    for (const id of packageIds) {
      if (!Object.prototype.hasOwnProperty.call(defines, id)) {
        errors.push(
          `${SOURCE_RELATIVE_PATH}: \`defines\` is missing an entry for package '${id}'.`
        );
      } else if (typeof defines[id] !== "string" || defines[id].length === 0) {
        errors.push(
          `${SOURCE_RELATIVE_PATH}: \`defines.${id}\` must be a non-empty define string.`
        );
      }
    }
    for (const id of defineIds) {
      if (!Object.prototype.hasOwnProperty.call(packages, id)) {
        errors.push(
          `${SOURCE_RELATIVE_PATH}: \`defines\` has an entry for '${id}' which is not a package ` +
            "in `packages` (no extras allowed)."
        );
      }
    }
  }

  // --- scope coverage: every package id under some scope prefix ---
  if (isPlainObject(packages) && isPlainObject(registry) && Array.isArray(registry.scopes)) {
    const scopes = registry.scopes.filter((scope) => typeof scope === "string" && scope.length > 0);
    for (const id of Object.keys(packages)) {
      const covered = scopes.some((scope) => scopeCoversId(id, scope));
      if (!covered) {
        errors.push(
          `${SOURCE_RELATIVE_PATH}: package '${id}' is not covered by any \`registry.scopes\` ` +
            `prefix [${scopes.join(", ")}].`
        );
      }
    }
  }

  return errors;
}

/**
 * Recursively lists every `*.asmdef` file under a directory (repo-relative
 * POSIX paths). Returns [] when the directory is absent.
 *
 * @param {string} repoRoot Repository root.
 * @param {string} relativeDir Repo-relative POSIX directory to walk.
 * @returns {string[]} Sorted repo-relative asmdef paths.
 */
function listAsmdefs(repoRoot, relativeDir) {
  const results = [];

  const walk = (currentRelative) => {
    const absoluteDir = path.join(repoRoot, currentRelative);
    let entries;
    try {
      entries = fs.readdirSync(absoluteDir, { withFileTypes: true });
    } catch (_error) {
      return;
    }
    for (const entry of entries) {
      const childRelative = `${currentRelative}/${entry.name}`;
      if (entry.isDirectory()) {
        walk(childRelative);
      } else if (entry.isFile() && entry.name.endsWith(".asmdef")) {
        results.push(childRelative);
      }
    }
  };

  walk(relativeDir);
  return results.sort();
}

/**
 * Collects the GATED comparison asmdefs: those whose `versionDefines` is a
 * non-empty array (i.e. they participate in the comparison package -> define
 * gating). The shared base Comparisons asmdef -- which carries only
 * UNITY_INCLUDE_TESTS and an empty versionDefines -- is intentionally excluded.
 *
 * @param {string} repoRoot Repository root.
 * @returns {Array<{ relativePath: string, defineConstraints: string[],
 *   versionDefines: Array<{ name: string, define: string }> }>} Parsed gated
 *   asmdefs.
 * @throws {Error} When an asmdef file is unreadable or not valid JSON.
 */
function collectGatedAsmdefs(repoRoot) {
  const gated = [];
  for (const relativePath of listAsmdefs(repoRoot, COMPARISONS_RELATIVE_DIR)) {
    const { data } = loadJsonFile(repoRoot, relativePath);
    const versionDefines = Array.isArray(data.versionDefines) ? data.versionDefines : [];
    if (versionDefines.length === 0) {
      continue;
    }
    const defineConstraints = Array.isArray(data.defineConstraints) ? data.defineConstraints : [];
    gated.push({ relativePath, defineConstraints, versionDefines });
  }
  return gated;
}

/**
 * Cross-checks the single source's defines against the gated comparison
 * asmdefs in BOTH directions.
 *
 *   (a) every DISTINCT define value in `defines` must appear in at least one
 *       gated asmdef's defineConstraints AND be produced by some versionDefines
 *       entry across the gated asmdefs;
 *   (b) every versionDefines entry across the gated asmdefs must have
 *       name === a package id present in `packages` AND
 *       define === that package's `defines` value.
 *
 * @param {object} params Parameters.
 * @param {Record<string, string>} params.packages Single-source packages map.
 * @param {Record<string, string>} params.defines Single-source defines map.
 * @param {Array<object>} params.gatedAsmdefs Output of collectGatedAsmdefs.
 * @returns {string[]} Violation messages. Empty when consistent.
 */
function checkAsmdefCrossReference({ packages, defines, gatedAsmdefs }) {
  const violations = [];

  if (gatedAsmdefs.length === 0) {
    violations.push(
      `${COMPARISONS_RELATIVE_DIR}: no gated comparison asmdef (with a non-empty versionDefines) ` +
        "was found; expected the comparison legs to gate compilation on the single-source defines."
    );
    return violations;
  }

  // Aggregate what the asmdefs declare.
  const constraintsUnion = new Set();
  const producedDefines = new Set();
  for (const asmdef of gatedAsmdefs) {
    for (const constraint of asmdef.defineConstraints) {
      constraintsUnion.add(constraint);
    }
    for (const entry of asmdef.versionDefines) {
      if (entry && typeof entry.define === "string") {
        producedDefines.add(entry.define);
      }
    }
  }

  // (a) every distinct single-source define is both constrained and produced.
  const distinctDefines = [...new Set(Object.values(defines))];
  for (const define of distinctDefines) {
    if (!constraintsUnion.has(define)) {
      violations.push(
        `asmdef cross-check: single-source define '${define}' is not present in any gated ` +
          `comparison asmdef's defineConstraints under ${COMPARISONS_RELATIVE_DIR}.`
      );
    }
    if (!producedDefines.has(define)) {
      violations.push(
        `asmdef cross-check: single-source define '${define}' is not produced by any gated ` +
          `comparison asmdef's versionDefines under ${COMPARISONS_RELATIVE_DIR}.`
      );
    }
  }

  // (b) every versionDefines entry maps a known package id to the agreed define.
  for (const asmdef of gatedAsmdefs) {
    for (const entry of asmdef.versionDefines) {
      const name = entry && entry.name;
      const define = entry && entry.define;
      if (typeof name !== "string" || !Object.prototype.hasOwnProperty.call(packages, name)) {
        violations.push(
          `asmdef cross-check: ${asmdef.relativePath} versionDefines name '${name}' is not a ` +
            "package id in the single source's `packages`."
        );
        continue;
      }
      const expectedDefine = defines[name];
      if (define !== expectedDefine) {
        violations.push(
          `asmdef cross-check: ${asmdef.relativePath} maps package '${name}' to define ` +
            `'${define}', but the single source says '${expectedDefine}'.`
        );
      }
    }
  }

  return violations;
}

/**
 * Cross-checks the committed local-parity manifest against the single source.
 *
 *   - manifest.scopedRegistries must contain an entry whose url === registry.url
 *     and whose scopes is a SUPERSET of registry.scopes;
 *   - manifest.dependencies must contain EVERY package id with the EXACT
 *     version from `packages`.
 *
 * @param {object} params Parameters.
 * @param {unknown} params.manifest Parsed local manifest.
 * @param {object} params.registry Single-source registry object.
 * @param {Record<string, string>} params.packages Single-source packages map.
 * @returns {string[]} Violation messages. Empty when consistent.
 */
function checkLocalManifest({ manifest, registry, packages }) {
  const violations = [];

  if (!isPlainObject(manifest)) {
    return [`${LOCAL_MANIFEST_RELATIVE_PATH}: root must be a JSON object.`];
  }

  // --- scoped registry ---
  const scopedRegistries = manifest.scopedRegistries;
  if (!Array.isArray(scopedRegistries) || scopedRegistries.length === 0) {
    violations.push(
      `${LOCAL_MANIFEST_RELATIVE_PATH}: \`scopedRegistries\` must be a non-empty array carrying ` +
        `the OpenUPM registry '${registry.url}'.`
    );
  } else {
    const match = scopedRegistries.find(
      (candidate) => isPlainObject(candidate) && candidate.url === registry.url
    );
    if (!match) {
      violations.push(
        `${LOCAL_MANIFEST_RELATIVE_PATH}: no \`scopedRegistries\` entry has url '${registry.url}' ` +
          "(single-source registry drift)."
      );
    } else {
      const manifestScopes = Array.isArray(match.scopes) ? match.scopes : [];
      const manifestScopeSet = new Set(manifestScopes);
      for (const scope of registry.scopes) {
        if (!manifestScopeSet.has(scope)) {
          violations.push(
            `${LOCAL_MANIFEST_RELATIVE_PATH}: scoped registry '${registry.url}' is missing scope ` +
              `'${scope}' (must be a superset of the single source's scopes).`
          );
        }
      }
    }
  }

  // --- dependencies: every package id at the exact pinned version ---
  const dependencies = isPlainObject(manifest.dependencies) ? manifest.dependencies : null;
  if (!dependencies) {
    violations.push(`${LOCAL_MANIFEST_RELATIVE_PATH}: \`dependencies\` must be an object.`);
  } else {
    for (const id of Object.keys(packages)) {
      if (!Object.prototype.hasOwnProperty.call(dependencies, id)) {
        violations.push(
          `${LOCAL_MANIFEST_RELATIVE_PATH}: \`dependencies\` is missing pinned comparison package ` +
            `'${id}' (expected '${packages[id]}').`
        );
      } else if (dependencies[id] !== packages[id]) {
        violations.push(
          `${LOCAL_MANIFEST_RELATIVE_PATH}: \`dependencies.${id}\` is '${dependencies[id]}', but ` +
            `the single source pins '${packages[id]}'.`
        );
      }
    }
  }

  return violations;
}

/**
 * Light text guard: the ephemeral CI manifest generator must still reference the
 * single-source filename, proving it stays wired to read it at runtime.
 *
 * @param {string} repoRoot Repository root.
 * @returns {string[]} Violation messages. Empty when the reference is present.
 */
function checkGeneratorWired(repoRoot) {
  const absolutePath = path.join(repoRoot, GENERATOR_RELATIVE_PATH);
  let content;
  try {
    content = fs.readFileSync(absolutePath, "utf8");
  } catch (error) {
    return [
      `${GENERATOR_RELATIVE_PATH}: expected generator is missing or unreadable (${error.message}).`
    ];
  }
  if (!content.includes("comparison-packages.json")) {
    return [
      `${GENERATOR_RELATIVE_PATH}: does not reference 'comparison-packages.json'; the ephemeral ` +
        "CI manifest generator must read the single source at runtime."
    ];
  }
  return [];
}

/**
 * Entry point. Loads + validates the single source, then enforces every
 * consumer cross-check. Prints a clear report and returns the process exit code.
 *
 * @param {object} [options] Options.
 * @param {string} [options.repoRoot] Repository root (defaults to this repo).
 * @param {(message?: unknown) => void} [options.log] stdout sink (default
 *   console.log).
 * @param {(message?: unknown) => void} [options.errorLog] stderr sink (default
 *   console.error).
 * @returns {number} 0 on success, 1 on any failure.
 */
function main(options = {}) {
  const repoRoot = options.repoRoot || REPO_ROOT;
  const log = options.log || console.log;
  const errorLog = options.errorLog || console.error;

  // --- load + schema-validate the single source ---
  let source;
  try {
    source = loadJsonFile(repoRoot, SOURCE_RELATIVE_PATH);
  } catch (error) {
    errorLog(error.message);
    return 1;
  }

  const schemaErrors = validateSourceSchema(source.data);
  if (schemaErrors.length > 0) {
    errorLog(`Comparison package single source schema is invalid (${SOURCE_RELATIVE_PATH}):\n`);
    for (const message of schemaErrors) {
      errorLog(`  ${message}`);
    }
    return 1;
  }

  const registry = source.data.registry;
  const packages = source.data.packages;
  const defines = source.data.defines;

  const violations = [];

  // --- asmdef cross-check (both directions) ---
  let gatedAsmdefs;
  try {
    gatedAsmdefs = collectGatedAsmdefs(repoRoot);
  } catch (error) {
    errorLog(`Failed to read a comparison asmdef: ${error.message}`);
    return 1;
  }
  violations.push(...checkAsmdefCrossReference({ packages, defines, gatedAsmdefs }));

  // --- committed local-parity manifest cross-check ---
  let manifest;
  try {
    manifest = loadJsonFile(repoRoot, LOCAL_MANIFEST_RELATIVE_PATH);
  } catch (error) {
    errorLog(error.message);
    return 1;
  }
  violations.push(...checkLocalManifest({ manifest: manifest.data, registry, packages }));

  // --- generator wiring guard ---
  violations.push(...checkGeneratorWired(repoRoot));

  if (violations.length > 0) {
    errorLog(`Comparison package drift detected (single source: ${SOURCE_RELATIVE_PATH}):\n`);
    for (const violation of violations) {
      errorLog(`  ${violation}`);
    }
    errorLog(
      `\n${violations.length} violation(s). Bump versions/scopes/defines only in ` +
        `${SOURCE_RELATIVE_PATH} and re-run.`
    );
    return 1;
  }

  const packageIds = Object.keys(packages);
  log("Comparison package single-source check passed.");
  log(`  single source: ${SOURCE_RELATIVE_PATH}`);
  log(`  registry:      ${registry.url} [${registry.scopes.join(", ")}]`);
  log(`  packages:      ${packageIds.length} pinned`);
  log(`  asmdefs:       ${gatedAsmdefs.length} gated comparison asmdef(s) cross-checked`);
  log(`  local manifest:${LOCAL_MANIFEST_RELATIVE_PATH} in parity`);
  return 0;
}

module.exports = {
  loadJsonFile,
  isPlainObject,
  scopeCoversId,
  validateSourceSchema,
  listAsmdefs,
  collectGatedAsmdefs,
  checkAsmdefCrossReference,
  checkLocalManifest,
  checkGeneratorWired,
  main,
  SOURCE_RELATIVE_PATH,
  LOCAL_MANIFEST_RELATIVE_PATH,
  COMPARISONS_RELATIVE_DIR,
  GENERATOR_RELATIVE_PATH
};

if (require.main === module) {
  process.exit(main());
}
