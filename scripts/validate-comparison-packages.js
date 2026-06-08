#!/usr/bin/env node
/**
 * validate-comparison-packages.js
 *
 * Drift detector for the comparison-benchmark package single source.
 *
 * CONTRACT
 * --------
 * `.github/comparison-packages.json` is the SINGLE SOURCE OF TRUTH for the
 * OpenUPM scoped registry, the PINNED versions of the third-party packages the
 * comparison benchmarks measure against (MessagePipe, UniTask, Zenject, UniRx,
 * Unity Atoms core + base atoms), and the Unity built-in module packages those
 * libraries need to compile:
 *
 *   {
 *     "registry": { "name": ..., "url": "https://...", "scopes": ["com.x", ...] },
 *     "packages": { "com.cysharp.messagepipe": "1.8.1", ... },        // x6
 *     "unityBuiltInPackages": { "com.unity.ugui": "1.0.0", ... },
 *     "defines":  { "com.cysharp.messagepipe": "MESSAGEPIPE_PRESENT", ... }, // unique
 *     "minUnityForComparisons": "2021.3.0f1"
 *   }
 *
 * Three consumers read this file and MUST agree with it:
 *   1. The gated comparison asmdefs under Tests/Runtime/Comparisons/** (their
 *      versionDefines map package id -> define, and their defineConstraints gate
 *      compilation on those defines).
 *   2. The committed local-parity manifest
 *      `.unity-test-project/Packages/manifest.json` and
 *      `.unity-test-project/Packages/packages-lock.json` (the scoped registry,
 *      the pinned dependencies, and the required Unity built-ins, so a local
 *      Unity open reproduces CI exactly).
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
const LOCAL_PACKAGE_LOCK_RELATIVE_PATH = ".unity-test-project/Packages/packages-lock.json";
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
 *   - unityBuiltInPackages is a non-empty object of Unity package id -> "1.0.0";
 *   - no Unity built-in package id is duplicated in packages;
 *   - defines has EXACTLY one entry per package id (no missing, no extras),
 *     each a non-empty string, and each define string maps to exactly one
 *     package id;
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

  // --- Unity built-ins required by the comparison packages ---
  const unityBuiltInPackages = data.unityBuiltInPackages;
  if (!isPlainObject(unityBuiltInPackages)) {
    errors.push(`${SOURCE_RELATIVE_PATH}: \`unityBuiltInPackages\` must be an object.`);
  } else {
    const builtInIds = Object.keys(unityBuiltInPackages);
    if (builtInIds.length === 0) {
      errors.push(`${SOURCE_RELATIVE_PATH}: \`unityBuiltInPackages\` must have at least one entry.`);
    }
    for (const id of builtInIds) {
      const version = unityBuiltInPackages[id];
      if (!id.startsWith("com.unity.")) {
        errors.push(
          `${SOURCE_RELATIVE_PATH}: Unity built-in package '${id}' must start with 'com.unity.'.`
        );
      }
      if (version !== "1.0.0") {
        errors.push(
          `${SOURCE_RELATIVE_PATH}: \`unityBuiltInPackages.${id}\` must be the Unity built-in ` +
            "package version '1.0.0'."
        );
      }
      if (isPlainObject(packages) && Object.prototype.hasOwnProperty.call(packages, id)) {
        errors.push(
          `${SOURCE_RELATIVE_PATH}: Unity built-in package '${id}' must not also appear in ` +
            "`packages`; OpenUPM-scoped comparison packages and Unity built-ins are separate."
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
    const packageIdsByDefine = new Map();
    for (const id of packageIds) {
      const define = defines[id];
      if (typeof define !== "string" || define.length === 0) {
        continue;
      }
      if (!packageIdsByDefine.has(define)) {
        packageIdsByDefine.set(define, []);
      }
      packageIdsByDefine.get(define).push(id);
    }
    for (const [define, ids] of packageIdsByDefine) {
      if (ids.length <= 1) {
        continue;
      }
      errors.push(
        `${SOURCE_RELATIVE_PATH}: define '${define}' is assigned to multiple packages ` +
          `[${ids.join(", ")}]. Package define mappings must be one-to-one so asmdef ` +
          "defineConstraints can require every package independently."
      );
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
 *   (a) every define value in `defines` must appear in at least one
 *       gated asmdef's defineConstraints AND be produced by some versionDefines
 *       entry across the gated asmdefs;
 *   (b) every versionDefines entry across the gated asmdefs must have
 *       name === a package id present in `packages` AND
 *       define === that package's `defines` value.
 *   (c) every package define produced by an asmdef's versionDefines must be
 *       required by that same asmdef's defineConstraints, and every
 *       single-source package define required by an asmdef must be produced by
 *       that same asmdef.
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
  const sourceDefines = new Set(Object.values(defines));
  for (const asmdef of gatedAsmdefs) {
    const localConstraints = new Set();
    const localProducedDefines = new Set();
    const localPackagesByName = new Map();
    const localPackagesByDefine = new Map();

    for (const constraint of asmdef.defineConstraints) {
      constraintsUnion.add(constraint);
      if (typeof constraint === "string") {
        localConstraints.add(constraint);
      }
    }
    for (const entry of asmdef.versionDefines) {
      const name = entry && entry.name;
      const define = entry && entry.define;
      if (typeof name === "string") {
        if (!localPackagesByName.has(name)) {
          localPackagesByName.set(name, []);
        }
        localPackagesByName.get(name).push(define);
      }
      if (typeof define === "string") {
        if (!localPackagesByDefine.has(define)) {
          localPackagesByDefine.set(define, []);
        }
        localPackagesByDefine.get(define).push(name);
        localProducedDefines.add(define);
        producedDefines.add(define);
      }
    }

    for (const [name, definesForName] of localPackagesByName) {
      if (definesForName.length <= 1) {
        continue;
      }
      violations.push(
        `asmdef cross-check: ${asmdef.relativePath} declares package '${name}' multiple times ` +
          "in versionDefines; each package gate must be declared once."
      );
    }

    for (const [define, namesForDefine] of localPackagesByDefine) {
      if (namesForDefine.length <= 1) {
        continue;
      }
      violations.push(
        `asmdef cross-check: ${asmdef.relativePath} maps define '${define}' to multiple ` +
          `versionDefines packages [${namesForDefine.join(", ")}]; package gates must use ` +
          "one define per package."
      );
    }

    for (const define of localProducedDefines) {
      if (!localConstraints.has(define)) {
        violations.push(
          `asmdef cross-check: ${asmdef.relativePath} produces package define '${define}' in ` +
            "versionDefines but does not require it in defineConstraints."
        );
      }
    }

    for (const constraint of localConstraints) {
      if (sourceDefines.has(constraint) && !localProducedDefines.has(constraint)) {
        violations.push(
          `asmdef cross-check: ${asmdef.relativePath} requires single-source define ` +
            `'${constraint}' in defineConstraints but does not produce it in that asmdef's ` +
            "versionDefines."
        );
      }
    }
  }

  // (a) every single-source define is both constrained and produced.
  for (const define of sourceDefines) {
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
 *     version from `packages`;
 *   - manifest.dependencies must contain EVERY Unity built-in package id with
 *     the EXACT version from `unityBuiltInPackages`.
 *
 * @param {object} params Parameters.
 * @param {unknown} params.manifest Parsed local manifest.
 * @param {object} params.registry Single-source registry object.
 * @param {Record<string, string>} params.packages Single-source packages map.
 * @param {Record<string, string>} params.unityBuiltInPackages Unity built-ins map.
 * @returns {string[]} Violation messages. Empty when consistent.
 */
function checkLocalManifest({ manifest, registry, packages, unityBuiltInPackages = {} }) {
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
    for (const [label, pins] of [
      ["pinned comparison package", packages],
      ["required Unity built-in package", unityBuiltInPackages]
    ]) {
      for (const id of Object.keys(pins)) {
        if (!Object.prototype.hasOwnProperty.call(dependencies, id)) {
          violations.push(
            `${LOCAL_MANIFEST_RELATIVE_PATH}: \`dependencies\` is missing ${label} '${id}' ` +
              `(expected '${pins[id]}').`
          );
        } else if (dependencies[id] !== pins[id]) {
          violations.push(
            `${LOCAL_MANIFEST_RELATIVE_PATH}: \`dependencies.${id}\` is '${dependencies[id]}', ` +
              `but the single source pins '${pins[id]}'.`
          );
        }
      }
    }
  }

  return violations;
}

/**
 * Cross-checks the committed Unity packages lock against the single source.
 *
 *   - packages-lock.dependencies must contain EVERY external comparison package
 *     as a direct OpenUPM registry dependency at the exact pinned version;
 *   - packages-lock.dependencies must contain EVERY Unity built-in package as a
 *     direct builtin dependency at the exact pinned version.
 *
 * @param {object} params Parameters.
 * @param {unknown} params.packageLock Parsed Unity package lock.
 * @param {object} params.registry Single-source registry object.
 * @param {Record<string, string>} params.packages Single-source packages map.
 * @param {Record<string, string>} params.unityBuiltInPackages Unity built-ins map.
 * @returns {string[]} Violation messages. Empty when consistent.
 */
function checkLocalPackageLock({ packageLock, registry, packages, unityBuiltInPackages = {} }) {
  const violations = [];

  if (!isPlainObject(packageLock)) {
    return [`${LOCAL_PACKAGE_LOCK_RELATIVE_PATH}: root must be a JSON object.`];
  }

  const dependencies = isPlainObject(packageLock.dependencies) ? packageLock.dependencies : null;
  if (!dependencies) {
    return [`${LOCAL_PACKAGE_LOCK_RELATIVE_PATH}: \`dependencies\` must be an object.`];
  }

  const checkEntry = ({ id, version, label, source, url }) => {
    const entry = dependencies[id];
    if (!isPlainObject(entry)) {
      violations.push(
        `${LOCAL_PACKAGE_LOCK_RELATIVE_PATH}: \`dependencies\` is missing ${label} '${id}' ` +
          `(expected '${version}').`
      );
      return;
    }
    if (entry.version !== version) {
      violations.push(
        `${LOCAL_PACKAGE_LOCK_RELATIVE_PATH}: \`dependencies.${id}.version\` is ` +
          `'${entry.version}', but the single source pins '${version}'.`
      );
    }
    if (entry.depth !== 0) {
      violations.push(
        `${LOCAL_PACKAGE_LOCK_RELATIVE_PATH}: \`dependencies.${id}.depth\` is '${entry.depth}', ` +
          "but comparison manifest dependencies must be direct depth 0 entries."
      );
    }
    if (entry.source !== source) {
      violations.push(
        `${LOCAL_PACKAGE_LOCK_RELATIVE_PATH}: \`dependencies.${id}.source\` is ` +
          `'${entry.source}', expected '${source}'.`
      );
    }
    if (url && entry.url !== url) {
      violations.push(
        `${LOCAL_PACKAGE_LOCK_RELATIVE_PATH}: \`dependencies.${id}.url\` is '${entry.url}', ` +
          `expected '${url}'.`
      );
    }
  };

  for (const [id, version] of Object.entries(packages)) {
    checkEntry({
      id,
      version,
      label: "pinned comparison package",
      source: "registry",
      url: registry.url
    });
  }

  for (const [id, version] of Object.entries(unityBuiltInPackages)) {
    checkEntry({
      id,
      version,
      label: "required Unity built-in package",
      source: "builtin"
    });
  }

  return violations;
}

/**
 * Strips PowerShell line comments from a script for lightweight text guards.
 * Good enough for this generator script because the relevant contract markers
 * are ordinary code lines; generated C# here-strings do not contain these tokens.
 *
 * @param {string} content PowerShell script text.
 * @returns {string} Text without # comments outside simple quotes.
 */
function stripPowerShellLineComments(content) {
  return content
    .split(/\r?\n/)
    .map((line) => line.replace(/#.*$/, ""))
    .join("\n");
}

/**
 * Light text guard: the ephemeral CI manifest generator must still read the
 * single source and loop over both external packages and Unity built-ins while
 * assigning `$dependencies[$pkg.Name] = $pkg.Value`.
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
  const violations = [];
  const uncommented = stripPowerShellLineComments(content);
  if (!uncommented.includes("comparison-packages.json")) {
    violations.push(
      `${GENERATOR_RELATIVE_PATH}: does not reference 'comparison-packages.json'; the ephemeral ` +
        "CI manifest generator must read the single source at runtime."
    );
  }
  if (!/Get-ComparisonPackages\s+-Root\s+\$RepoRoot/.test(uncommented)) {
    violations.push(
      `${GENERATOR_RELATIVE_PATH}: does not call Get-ComparisonPackages -Root $RepoRoot; the ` +
        "ephemeral CI manifest generator must read the single source at runtime."
    );
  }
  if (!/\$comparisons\.packages\.PSObject\.Properties/.test(uncommented)) {
    violations.push(
      `${GENERATOR_RELATIVE_PATH}: does not loop over $comparisons.packages.PSObject.Properties; ` +
        "the ephemeral CI manifest generator must inject the external comparison package pins."
    );
  }
  if (!/unityBuiltInPackages/.test(uncommented)) {
    violations.push(
      `${GENERATOR_RELATIVE_PATH}: does not reference 'unityBuiltInPackages'; the ephemeral ` +
        "CI manifest generator must inject the Unity built-in modules required by comparison packages."
    );
  }
  if (!/\$builtInPackages\.Value\.PSObject\.Properties/.test(uncommented)) {
    violations.push(
      `${GENERATOR_RELATIVE_PATH}: does not loop over $builtInPackages.Value.PSObject.Properties; ` +
        "the ephemeral CI manifest generator must inject the Unity built-in modules required by comparison packages."
    );
  }
  const dependencyAssignments = uncommented.match(
    /\$dependencies\s*\[\s*\$pkg\.Name\s*\]\s*=\s*\$pkg\.Value/g
  );
  if (!dependencyAssignments || dependencyAssignments.length < 2) {
    violations.push(
      `${GENERATOR_RELATIVE_PATH}: must assign both external package pins and Unity built-ins ` +
        "into the manifest dependencies map."
    );
  }
  return violations;
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
  const unityBuiltInPackages = source.data.unityBuiltInPackages;
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
  violations.push(
    ...checkLocalManifest({ manifest: manifest.data, registry, packages, unityBuiltInPackages })
  );

  // --- committed local-parity package lock cross-check ---
  let packageLock;
  try {
    packageLock = loadJsonFile(repoRoot, LOCAL_PACKAGE_LOCK_RELATIVE_PATH);
  } catch (error) {
    errorLog(error.message);
    return 1;
  }
  violations.push(
    ...checkLocalPackageLock({
      packageLock: packageLock.data,
      registry,
      packages,
      unityBuiltInPackages
    })
  );

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
  const builtInPackageIds = Object.keys(unityBuiltInPackages);
  log("Comparison package single-source check passed.");
  log(`  single source: ${SOURCE_RELATIVE_PATH}`);
  log(`  registry:      ${registry.url} [${registry.scopes.join(", ")}]`);
  log(`  packages:      ${packageIds.length} pinned`);
  log(`  Unity built-ins:${builtInPackageIds.length} required`);
  log(`  asmdefs:       ${gatedAsmdefs.length} gated comparison asmdef(s) cross-checked`);
  log(`  local manifest:${LOCAL_MANIFEST_RELATIVE_PATH} in parity`);
  log(`  package lock:  ${LOCAL_PACKAGE_LOCK_RELATIVE_PATH} in parity`);
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
  checkLocalPackageLock,
  stripPowerShellLineComments,
  checkGeneratorWired,
  main,
  SOURCE_RELATIVE_PATH,
  LOCAL_MANIFEST_RELATIVE_PATH,
  LOCAL_PACKAGE_LOCK_RELATIVE_PATH,
  COMPARISONS_RELATIVE_DIR,
  GENERATOR_RELATIVE_PATH
};

if (require.main === module) {
  process.exit(main());
}
