"use strict";

/**
 * @file asmdef-discovery.js
 *
 * Shared, deterministic discovery + classification of Unity test asmdef files.
 *
 * Used by:
 *   - .github/actions/compute-unity-assemblies (primary CI consumer)
 *   - .github/workflows-disabled/unity-tests.yml (customParameters template)
 *
 * No filesystem mutation. Pure functions only.
 *
 * Exports:
 *   - defaultIncludeAssemblies(repoRoot, options?)
 *
 * The enumeration/classification helpers are module-internal; run this file
 * directly (`node scripts/unity/lib/asmdef-discovery.js`) for a self-test
 * that prints every discovered asmdef with its classification.
 *
 * Default include/exclude rules:
 *   - "core"        => INCLUDED by default.
 *   - "perf"        => EXCLUDED by default. Opt in with { includePerf: true }.
 *   - "comparison"  => EXCLUDED by default. Opt in with
 *                      { includeComparisons: true } after installing external
 *                      comparison packages.
 *   - "integration" => EXCLUDED by default (their packages are not in the test
 *                      project's manifest.json and would fail to compile).
 *                      Opt in with { includeIntegrations: true }.
 */

const fs = require("fs");
const path = require("path");

const { walkFiles } = require("../../lib/repo-files");

/**
 * Names matching this pattern are perf/benchmark/allocation assemblies and must
 * be excluded from default Unity Test Runner runs.
 *
 * Source of truth lives in .llm/context.md line 114 (perf isolation rule).
 *
 * @type {RegExp}
 */
const PERF_NAME_REGEX = /(?:Benchmarks|Allocations)/;
const COMPARISON_NAME_REGEX = /(?:Comparisons)/;

/**
 * Names matching this pattern are DI-container integration suites
 * (VContainer / Zenject / Reflex). EXCLUDED from the default suite because
 * their backing packages (com.gustavopsantos.reflex, com.svermeulen.extenject,
 * jp.hadashikick.vcontainer) are not declared in the test project's
 * manifest.json — including them would cause compile errors. Opt in via the
 * `includeIntegrations` option on `defaultIncludeAssemblies`.
 *
 * @type {RegExp}
 */
const INTEGRATION_NAME_REGEX = /(?:VContainer|Zenject|Reflex)/;

/**
 * Assembly-name prefix that marks an asmdef as owned by DxMessaging. The Unity
 * Test Runner is invoked with an explicit `-assemblyNames` list, so a foreign
 * test asmdef that happens to live under `Tests/` (for example one pulled in by
 * an external comparison package, or a stray sample) must never be added to the
 * list -- it would not compile against the harness manifest and would fail the
 * run for a reason unrelated to DxMessaging. Every real DxMessaging test
 * assembly is named `WallstopStudios.DxMessaging.Tests*`, so this owner prefix
 * is a safe, future-proof gate that is a no-op for the current asmdef set.
 *
 * @type {string}
 */
const DXMESSAGING_ASSEMBLY_PREFIX = "WallstopStudios.DxMessaging.";
const STANDALONE_PLATFORM_NAMES = new Set([
  "Standalone",
  "WindowsStandalone32",
  "WindowsStandalone64",
  "LinuxStandalone64",
  "OSXStandalone"
]);

/**
 * True when `name` is a DxMessaging-owned assembly (see
 * {@link DXMESSAGING_ASSEMBLY_PREFIX}). Non-string / empty input is treated as
 * NOT owned so a malformed asmdef can never slip through the include gate.
 *
 * @param {string} name - Asmdef assembly name (no extension)
 * @returns {boolean} True iff the name carries the DxMessaging owner prefix
 */
function isDxMessagingOwnedAssembly(name) {
  return typeof name === "string" && name.startsWith(DXMESSAGING_ASSEMBLY_PREFIX);
}

/**
 * Strip the `.asmdef` extension and return the asmdef's declared name. The
 * file's `name` field is the canonical assembly name and must match the
 * filename per Unity convention; we read the JSON to be safe.
 *
 * @param {string} asmdefPath - Absolute path to an .asmdef file
 * @returns {string} Asmdef name (without extension)
 */
function readAsmdefName(asmdefPath) {
  const raw = fs.readFileSync(asmdefPath, "utf8");
  const parsed = JSON.parse(raw);
  if (typeof parsed.name !== "string" || parsed.name.length === 0) {
    // Fall back to the filename to keep this function pure-ish.
    return path.basename(asmdefPath, ".asmdef");
  }
  return parsed.name;
}

/**
 * Classify an asmdef name into a single category.
 *
 * Categories:
 *   - "perf"        — Benchmarks/Allocations (excluded from PR
 *                     gates per .llm/context.md line 114).
 *   - "comparison"  — external comparison benchmarks.
 *   - "integration" — VContainer/Zenject/Reflex DI integration suites.
 *   - "core"        — Everything else (Editor, Runtime, etc.).
 *
 * Note: comparison suites benchmark DxMessaging against alternative messaging
 * libraries, so they require an additional opt-in after the external packages
 * are installed in the harness manifest.
 *
 * @param {string} name - Asmdef assembly name (no extension)
 * @returns {"perf" | "comparison" | "integration" | "core"} Classification
 */
function classifyAsmdef(name) {
  if (typeof name !== "string" || name.length === 0) {
    return "core";
  }

  if (PERF_NAME_REGEX.test(name)) {
    return "perf";
  }

  if (COMPARISON_NAME_REGEX.test(name)) {
    return "comparison";
  }

  if (INTEGRATION_NAME_REGEX.test(name)) {
    return "integration";
  }

  return "core";
}

/**
 * @typedef {object} AsmdefEntry
 * @property {string} name - Asmdef assembly name
 * @property {string} path - Absolute path to the asmdef file
 * @property {boolean} isPerf - True when classification is "perf"
 * @property {boolean} isComparison - True when classification is "comparison"
 * @property {boolean} isInteg - True when classification is "integration"
 * @property {boolean} isEditorOnly - True iff includePlatforms is exactly ["Editor"]
 * @property {boolean} isForeign - True when the assembly is NOT DxMessaging-owned
 *                     (name lacks the `WallstopStudios.DxMessaging.` prefix). Such
 *                     assemblies are never added to the Unity `-assemblyNames` list.
 */

/**
 * Read an asmdef's `includePlatforms` array and decide whether the assembly is
 * editor-only. An assembly is editor-only iff `includePlatforms` is exactly
 * `["Editor"]`. Editor-only test assemblies (EditMode suites + Editor
 * benchmarks/integrations) cannot run inside a built player, so the standalone
 * runtime-only flow must exclude them.
 *
 * @param {string} asmdefPath - Absolute path to an .asmdef file
 * @returns {boolean} True when includePlatforms === ["Editor"]
 */
function readAsmdefPlatforms(asmdefPath) {
  const raw = fs.readFileSync(asmdefPath, "utf8");
  const parsed = JSON.parse(raw);
  return {
    includePlatforms: Array.isArray(parsed.includePlatforms) ? parsed.includePlatforms : [],
    excludePlatforms: Array.isArray(parsed.excludePlatforms) ? parsed.excludePlatforms : []
  };
}

/**
 * @param {string[]} includePlatforms
 * @param {string[]} excludePlatforms
 * @param {"editmode" | "playmode" | "standalone"} target
 * @returns {boolean}
 */
function isAsmdefCompatibleWithTarget(includePlatforms, excludePlatforms, target) {
  const includes = new Set(includePlatforms);
  const excludes = new Set(excludePlatforms);

  if (target === "standalone") {
    if (excludes.has("Standalone") || excludes.has("WindowsStandalone64")) {
      return false;
    }
    if (includes.size === 0) {
      return true;
    }
    for (const platform of includes) {
      if (STANDALONE_PLATFORM_NAMES.has(platform)) {
        return true;
      }
    }
    return false;
  }

  if (target === "editmode") {
    if (excludes.has("Editor")) {
      return false;
    }
    return includes.size === 0 || includes.has("Editor");
  }

  if (target === "playmode") {
    if (excludes.has("Editor")) {
      return false;
    }
    return includes.size === 0;
  }

  if (excludes.has("Editor")) {
    return false;
  }
  return includes.size === 0 || includes.has("Editor");
}

/**
 * Enumerate every asmdef under `<repoRoot>/Tests/`. Sorted by `name` for
 * stable downstream output (CI summaries, contract tests).
 *
 * @param {string} repoRoot - Absolute path to the repository root
 * @returns {AsmdefEntry[]} Discovered test asmdefs
 */
function enumerateTestAsmdefs(repoRoot) {
  if (typeof repoRoot !== "string" || repoRoot.length === 0) {
    throw new TypeError("enumerateTestAsmdefs: repoRoot must be a non-empty string");
  }

  const testsDir = path.join(repoRoot, "Tests");
  const asmdefPaths = walkFiles(testsDir, {
    match: (full, dirent) => dirent.name.endsWith(".asmdef")
  });

  /** @type {AsmdefEntry[]} */
  const entries = asmdefPaths.map((asmdefPath) => {
    const name = readAsmdefName(asmdefPath);
    const classification = classifyAsmdef(name);
    const platforms = readAsmdefPlatforms(asmdefPath);
    return {
      name,
      path: asmdefPath,
      isPerf: classification === "perf",
      isComparison: classification === "comparison",
      isInteg: classification === "integration",
      includePlatforms: platforms.includePlatforms,
      excludePlatforms: platforms.excludePlatforms,
      isEditorOnly:
        platforms.includePlatforms.length === 1 && platforms.includePlatforms[0] === "Editor",
      isForeign: !isDxMessagingOwnedAssembly(name)
    };
  });

  entries.sort((a, b) => a.name.localeCompare(b.name));
  return entries;
}

/**
 * @typedef {object} IncludeOptions
 * @property {boolean} [includePerf=false]         Include "perf" asmdefs.
 * @property {boolean} [includeComparisons=false]  Include comparison benchmark asmdefs.
 * @property {boolean} [includeIntegrations=false] Include "integration" asmdefs.
 * @property {"editmode" | "playmode" | "standalone"} [target=editmode]
 *                     Select assemblies compatible with the Unity test target.
 *                     PlayMode and standalone omit editor-only asmdefs.
 * @property {boolean} [runtimeOnly=false]         Back-compat alias for
 *                     target: "standalone". Applied before the perf/comparison/
 *                     integration gating so it composes.
 */

/**
 * Names of test asmdefs included in the default Unity Test Runner suite.
 *
 * By default ONLY "core" asmdefs are returned. Perf and integration suites
 * are opt-in:
 *   - includePerf:         add Benchmarks/Allocations.
 *   - includeComparisons:  add external comparison benchmarks.
 *   - includeIntegrations: add VContainer/Zenject/Reflex (caller must ensure
 *                          the corresponding DI packages are in manifest.json).
 *
 * @param {string} repoRoot - Absolute path to the repository root
 * @param {IncludeOptions} [options] - Opt-in flags (default: all false)
 * @returns {string[]} Sorted asmdef names (no extension)
 */
function defaultIncludeAssemblies(repoRoot, options) {
  const opts = options || {};
  const includePerf = opts.includePerf === true;
  const includeComparisons = opts.includeComparisons === true;
  const includeIntegrations = opts.includeIntegrations === true;
  const target = opts.target || (opts.runtimeOnly === true ? "standalone" : "editmode");

  return enumerateTestAsmdefs(repoRoot)
    .filter((entry) => {
      // Foreign (non-DxMessaging-owned) asmdefs are never added to the Unity
      // -assemblyNames list: they would not compile against the harness
      // manifest and would fail the run for a reason unrelated to DxMessaging.
      // Gated first, ahead of every other decision. A no-op for the current
      // asmdef set (all entries are DxMessaging-owned).
      if (entry.isForeign) {
        return false;
      }
      if (!isAsmdefCompatibleWithTarget(entry.includePlatforms, entry.excludePlatforms, target)) {
        return false;
      }
      if (entry.isPerf) {
        return includePerf;
      }
      if (entry.isComparison) {
        return includeComparisons;
      }
      if (entry.isInteg) {
        return includeIntegrations;
      }
      return true;
    })
    .map((entry) => entry.name);
}

/**
 * Names of test asmdefs excluded from the default Unity Test Runner suite.
 * Mirror of `defaultIncludeAssemblies` — anything not selected by the include
 * options is returned here. With no options, returns all perf + integration
 * asmdefs.
 *
 * @param {string} repoRoot - Absolute path to the repository root
 * @param {IncludeOptions} [options] - Opt-in flags (default: all false)
 * @returns {string[]} Sorted asmdef names (no extension)
 */
function defaultExcludeAssemblies(repoRoot, options) {
  const opts = options || {};
  const includePerf = opts.includePerf === true;
  const includeComparisons = opts.includeComparisons === true;
  const includeIntegrations = opts.includeIntegrations === true;
  const target = opts.target || (opts.runtimeOnly === true ? "standalone" : "editmode");

  return enumerateTestAsmdefs(repoRoot)
    .filter((entry) => {
      // Mirror of defaultIncludeAssemblies. Foreign (non-DxMessaging-owned)
      // asmdefs are never included, so they are always "excluded" here too.
      if (entry.isForeign) {
        return true;
      }
      if (!isAsmdefCompatibleWithTarget(entry.includePlatforms, entry.excludePlatforms, target)) {
        return true;
      }
      if (entry.isPerf) {
        return !includePerf;
      }
      if (entry.isComparison) {
        return !includeComparisons;
      }
      if (entry.isInteg) {
        return !includeIntegrations;
      }
      return false;
    })
    .map((entry) => entry.name);
}

// Only defaultIncludeAssemblies has external consumers
// (compute-unity-assemblies/action.yml). The other helpers are internal; the
// self-test block below uses them directly.
module.exports = {
  defaultIncludeAssemblies
};

if (require.main === module) {
  // Self-test mode: print classified asmdefs for the current repo.
  const repoRoot = path.resolve(__dirname, "..", "..", "..");
  const all = enumerateTestAsmdefs(repoRoot);
  const include = defaultIncludeAssemblies(repoRoot);
  const exclude = defaultExcludeAssemblies(repoRoot);

  process.stdout.write(`repoRoot: ${repoRoot}\n`);
  process.stdout.write(`discovered ${all.length} asmdef(s):\n`);
  for (const entry of all) {
    const cls = entry.isPerf
      ? "perf"
      : entry.isComparison
        ? "comparison"
        : entry.isInteg
          ? "integration"
          : "core";
    process.stdout.write(`  [${cls}] ${entry.name}\n`);
  }
  process.stdout.write(
    `\ndefault include (${include.length}, core only — pass ` +
      `{ includePerf, includeComparisons, includeIntegrations } to opt in):\n`
  );
  for (const name of include) {
    process.stdout.write(`  + ${name}\n`);
  }
  process.stdout.write(
    `\ndefault exclude (${exclude.length}, perf + comparison + integration suites):\n`
  );
  for (const name of exclude) {
    process.stdout.write(`  - ${name}\n`);
  }

  // Diagnostic: runtime-only include list (used by the standalone player flow,
  // where EditMode/editor-only asmdefs cannot run).
  const runtimeInclude = defaultIncludeAssemblies(repoRoot, { target: "standalone" });
  process.stdout.write(
    `\nruntime-only include (${runtimeInclude.length}, drops editor-only asmdefs):\n`
  );
  for (const name of runtimeInclude) {
    process.stdout.write(`  * ${name}\n`);
  }
}
