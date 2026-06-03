"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const TOOL_LOAD_ERROR_PATTERNS = [
  /Cannot find package/i,
  /Cannot find module/i,
  /ERR_MODULE_NOT_FOUND/i,
  /MODULE_NOT_FOUND/i
];

const MISSING_BUNDLED_NPX_CLI_MESSAGE =
  "Unable to locate npm's npx-cli.js next to the active Node executable. Run `npm install` in a shell with a complete Node/npm installation, or ensure npm is installed with this Node runtime.";

function isRecoverableToolLoadError(error) {
  const message = `${error?.code || ""}\n${error?.message || ""}\n${error?.stack || ""}`;
  return TOOL_LOAD_ERROR_PATTERNS.some((pattern) => pattern.test(message));
}

function resolveBundledNpxCliPath({
  execPath = process.execPath,
  existsSyncFn = fs.existsSync
} = {}) {
  const execDir = path.dirname(execPath);
  const candidates = [
    path.join(execDir, "node_modules", "npm", "bin", "npx-cli.js"),
    path.join(execDir, "..", "lib", "node_modules", "npm", "bin", "npx-cli.js"),
    path.join(execDir, "..", "node_modules", "npm", "bin", "npx-cli.js")
  ];

  for (const candidate of candidates) {
    const normalizedCandidate = path.normalize(candidate);
    if (existsSyncFn(normalizedCandidate)) {
      return normalizedCandidate;
    }
  }

  return null;
}

function createMissingBundledNpxCliError() {
  return new Error(MISSING_BUNDLED_NPX_CLI_MESSAGE);
}

function runBundledNpxCommand(args, options = {}) {
  const {
    execPath = process.execPath,
    resolveBundledNpxCliPathFn = resolveBundledNpxCliPath,
    runCommandFn = childProcess.spawnSync,
    cwd,
    encoding = "utf8",
    ...commandOptions
  } = options;

  const npxCliPath = resolveBundledNpxCliPathFn({ execPath });
  if (!npxCliPath) {
    throw createMissingBundledNpxCliError();
  }

  const resolvedOptions = { ...commandOptions };
  if (cwd !== undefined) {
    resolvedOptions.cwd = cwd;
  }
  if (encoding !== undefined) {
    resolvedOptions.encoding = encoding;
  }

  return runCommandFn(execPath, [npxCliPath, ...args], resolvedOptions);
}

function isHealthyFile(absPath, existsSyncFn, statSyncFn) {
  if (!existsSyncFn(absPath)) {
    return false;
  }
  try {
    const stats = statSyncFn(absPath);
    return Boolean(stats) && typeof stats.size === "number" && stats.size > 0;
  } catch {
    return false;
  }
}

function loadLocalPrettier({
  localPrettierModulePath,
  localPrettierBinPath,
  existsSyncFn = fs.existsSync,
  statSyncFn = fs.statSync,
  requireFn = require,
  isRecoverableToolLoadErrorFn = isRecoverableToolLoadError
} = {}) {
  if (!localPrettierModulePath || !localPrettierBinPath) {
    throw new Error(
      "loadLocalPrettier requires both localPrettierModulePath and localPrettierBinPath."
    );
  }

  // Phase 4: probe BOTH the module path and the bin path for presence
  // AND non-zero size. The bin path was previously only checked as a
  // tie-breaker for the "unexpected layout" error. We now treat a
  // zero-byte module OR a zero-byte bin as missing - either condition
  // means Prettier will fail to run on Windows partial-extract systems
  // even though existsSync says "yes".
  const moduleHealthy = isHealthyFile(localPrettierModulePath, existsSyncFn, statSyncFn);
  const binHealthy = isHealthyFile(localPrettierBinPath, existsSyncFn, statSyncFn);

  if (moduleHealthy && binHealthy) {
    try {
      return requireFn(localPrettierModulePath);
    } catch (error) {
      if (isRecoverableToolLoadErrorFn(error)) {
        return null;
      }
      throw error;
    }
  }

  // Existing failure case preserved: bin present without index.cjs is
  // surfaced as an explicit error so the operator knows what to repair.
  if (existsSyncFn(localPrettierBinPath) && !existsSyncFn(localPrettierModulePath)) {
    throw new Error(
      "Prettier devDependency layout is unexpected: bin present but index.cjs missing. Run `npm install` to repair."
    );
  }

  return null;
}

module.exports = {
  TOOL_LOAD_ERROR_PATTERNS,
  MISSING_BUNDLED_NPX_CLI_MESSAGE,
  isRecoverableToolLoadError,
  resolveBundledNpxCliPath,
  createMissingBundledNpxCliError,
  runBundledNpxCommand,
  loadLocalPrettier
};
