"use strict";

// Deterministic test selection. See the Unity test execution skill for scope and dependency rules.

const fs = require("fs");
const path = require("path");
const { walkFiles } = require("../../lib/repo-files");

const PERF_NAME_REGEX = /(?:Benchmarks|Allocations)/;
const COMPARISON_NAME_REGEX = /(?:Comparisons)/;
const TRANSPORT_NAME_REGEX = /\.PipelineTransportResearch$/;
const INTEGRATION_NAME_REGEX = /(?:VContainer|Zenject|Reflex)/;

const DXMESSAGING_ASSEMBLY_PREFIX = "WallstopStudios.DxMessaging.";
const STANDALONE_PLATFORM_NAMES = new Set([
  "Standalone",
  "WindowsStandalone32",
  "WindowsStandalone64",
  "LinuxStandalone64",
  "OSXStandalone"
]);

function isDxMessagingOwnedAssembly(name) {
  return typeof name === "string" && name.startsWith(DXMESSAGING_ASSEMBLY_PREFIX);
}

function readAsmdefName(asmdefPath) {
  const raw = fs.readFileSync(asmdefPath, "utf8");
  const parsed = JSON.parse(raw);
  if (typeof parsed.name !== "string" || parsed.name.length === 0) {
    return path.basename(asmdefPath, ".asmdef");
  }
  return parsed.name;
}

function classifyAsmdef(name) {
  for (const [pattern, classification] of [
    [PERF_NAME_REGEX, "perf"],
    [COMPARISON_NAME_REGEX, "comparison"],
    [INTEGRATION_NAME_REGEX, "integration"],
    [TRANSPORT_NAME_REGEX, "transport"]
  ]) {
    if (typeof name === "string" && pattern.test(name)) return classification;
  }
  return "core";
}

function readAsmdefPlatforms(asmdefPath) {
  const raw = fs.readFileSync(asmdefPath, "utf8");
  const parsed = JSON.parse(raw);
  return {
    includePlatforms: Array.isArray(parsed.includePlatforms) ? parsed.includePlatforms : [],
    excludePlatforms: Array.isArray(parsed.excludePlatforms) ? parsed.excludePlatforms : []
  };
}

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

function enumerateTestAsmdefs(repoRoot) {
  if (typeof repoRoot !== "string" || repoRoot.length === 0) {
    throw new TypeError("enumerateTestAsmdefs: repoRoot must be a non-empty string");
  }

  const testsDir = path.join(repoRoot, "Tests");
  const asmdefPaths = walkFiles(testsDir, {
    match: (full, dirent) => dirent.name.endsWith(".asmdef")
  });

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
      isTransport: classification === "transport",
      includePlatforms: platforms.includePlatforms,
      excludePlatforms: platforms.excludePlatforms,
      isForeign: !isDxMessagingOwnedAssembly(name)
    };
  });

  entries.sort((a, b) => a.name.localeCompare(b.name));
  return entries;
}

// Scope and observed-host admission: docs/runbooks/pipeline-host-repair.md.
function resolveTestAssemblySelection(repoRoot, options) {
  const opts = options || {};
  const includePerf = opts.includePerf === true;
  const includeComparisons = opts.includeComparisons === true;
  const includeIntegrations = opts.includeIntegrations === true;
  const includeTransportControls = opts.includeTransportControls === true;
  const target = opts.target || (opts.runtimeOnly === true ? "standalone" : "editmode");
  if (includeTransportControls && target !== "editmode") {
    throw new Error("Transport controls require the EditMode target.");
  }
  if (includeTransportControls) {
    if (typeof opts.transportPackagePath !== "string" || !opts.transportPackagePath) {
      throw new Error("Transport controls require the resolved Pipeline package path.");
    }
    const dependency = JSON.parse(
      fs.readFileSync(path.join(opts.transportPackagePath, "package.json"), "utf8")
    );
    if (
      opts.unityVersion !== "6000.4.6f1" ||
      dependency.name !== "com.unity.pipeline" ||
      dependency.version !== "0.8.0-exp.1" ||
      dependency.repository?.revision !== "dde7080264d32a1091171d4b358351279fa10ddb"
    ) {
      throw new Error(
        "Transport controls require the admitted Unity 6000.4.6f1 / Pipeline 0.8.0-exp.1 host."
      );
    }
  }
  const entries = enumerateTestAsmdefs(repoRoot).filter(
    (entry) =>
      !entry.isForeign &&
      isAsmdefCompatibleWithTarget(entry.includePlatforms, entry.excludePlatforms, target)
  );
  const transport = entries.filter((entry) => entry.isTransport).map((entry) => entry.name);
  if (includeTransportControls && transport.length === 0) {
    throw new Error(
      "Transport controls were requested but no compatible owned transport assembly exists."
    );
  }
  const assemblies = entries
    .filter((entry) => {
      if (entry.isPerf) {
        return includePerf;
      }
      if (entry.isComparison) {
        return includeComparisons;
      }
      if (entry.isInteg) {
        return includeIntegrations;
      }
      if (entry.isTransport) {
        return includeTransportControls;
      }
      return true;
    })
    .map((entry) => entry.name);
  return { assemblies, excludedTransportControls: includeTransportControls ? [] : transport };
}

// Preserve the array API for existing callers; CI also records excluded transport controls.
function defaultIncludeAssemblies(repoRoot, options) {
  return resolveTestAssemblySelection(repoRoot, options).assemblies;
}

module.exports = {
  defaultIncludeAssemblies,
  resolveTestAssemblySelection
};

if (require.main === module) {
  const repoRoot = path.resolve(__dirname, "..", "..", "..");
  const report = {
    repoRoot,
    discovered: enumerateTestAsmdefs(repoRoot),
    shipping: resolveTestAssemblySelection(repoRoot),
    standalone: resolveTestAssemblySelection(repoRoot, { target: "standalone" })
  };
  process.stdout.write(JSON.stringify(report, null, 2) + "\n");
}
