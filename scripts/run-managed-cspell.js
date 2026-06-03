#!/usr/bin/env node
/**
 * run-managed-cspell.js
 *
 * Runs cspell in a robust, non-interactive way for hooks and local automation:
 * 1) Integrity gate (Step 8): probe node_modules health BEFORE invoking
 *    cspell. If a partial extract is detected and auto-repair is allowed,
 *    run npm ci, then re-probe in a subprocess (defeats the parent's stat
 *    cache).
 * 2) Prefer local devDependency (node_modules/cspell/bin.mjs).
 * 3) If local cspell is missing, provision a pinned fallback via npm's
 *    bundled npx-cli.
 *
 * Cspell version pin: read from package.json devDependencies.cspell with
 * a static fallback. The hook entry in `.pre-commit-config.yaml` points at
 * this wrapper, which then forwards args via spawnPlatformCommandSync for
 * Windows shell-shim safety.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");
const { runBundledNpxCommand } = require("./lib/managed-prettier");
const { spawnPlatformCommandSync } = require("./lib/shell-command");
const jestErrorDecoderModule = require("./lib/jest-error-decoder");
const { isTruthyEnv } = jestErrorDecoderModule;
const {
  probeIntegrity,
  probeIntegrityInSubprocess,
  probeResolverHealth
} = require("./lib/node-modules-integrity");
const {
  isAutoRepairAllowed: defaultIsAutoRepairAllowed,
  runIntegrityGateWithRecovery
} = require("./lib/integrity-gate-with-recovery");
const {
  getNpmMajorVersion,
  attemptNpmCiRecovery,
  printActionableRepairBanner
} = require("./run-managed-jest");

const REPO_ROOT = path.join(__dirname, "..");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");
const LOCAL_CSPELL_BIN = path.join(REPO_ROOT, "node_modules", "cspell", "bin.mjs");
// Static fallback used only when package.json is unparseable. Kept in sync
// with the package.json devDependency by the cspell-version-parity test.
const FALLBACK_CSPELL_SPEC = "cspell@10.0.0";

function normalizeVersion(rawVersion) {
  if (typeof rawVersion !== "string") {
    return null;
  }
  const trimmed = rawVersion.trim().replace(/^[~^]/, "");
  if (!/^\d+\.\d+\.\d+(?:[-+].+)?$/.test(trimmed)) {
    return null;
  }
  return trimmed;
}

function getPinnedCspellSpec(readFileSyncFn = fs.readFileSync) {
  try {
    const pkg = JSON.parse(readFileSyncFn(PACKAGE_JSON_PATH, "utf8"));
    const version = normalizeVersion(pkg && pkg.devDependencies && pkg.devDependencies.cspell);
    if (version) {
      return `cspell@${version}`;
    }
  } catch {
    // Fall through to static fallback when package.json is unavailable.
  }
  return FALLBACK_CSPELL_SPEC;
}

function runCommand(command, args, options = {}) {
  const spawnSync =
    command === "npm" || command === "npx" ? spawnPlatformCommandSync : childProcess.spawnSync;
  const result = spawnSync(command, args, {
    cwd: REPO_ROOT,
    stdio: "inherit",
    ...options
  });
  return {
    status: result.status,
    error: result.error || null
  };
}

function runLocalCspell(args) {
  return runCommand(process.execPath, [LOCAL_CSPELL_BIN, ...args]);
}

function runNpxCspell(args, cspellSpec = getPinnedCspellSpec(), options = {}) {
  const { runBundledNpxCommandFn = runBundledNpxCommand } = options;
  try {
    const result = runBundledNpxCommandFn(["--yes", `--package=${cspellSpec}`, "cspell", ...args], {
      cwd: REPO_ROOT,
      stdio: "inherit"
    });
    return {
      status: result.status,
      error: result.error || null
    };
  } catch (error) {
    return {
      status: null,
      error
    };
  }
}

function runManagedCspell(args, options = {}) {
  const {
    existsSyncFn = fs.existsSync,
    runLocalCspellFn = runLocalCspell,
    runNpxCspellFn = runNpxCspell,
    // Integrity gate dependencies (Step 8).
    runIntegrityGateWithRecoveryFn = runIntegrityGateWithRecovery,
    probeIntegrityFn = probeIntegrity,
    probeIntegrityInSubprocessFn = probeIntegrityInSubprocess,
    probeResolverHealthFn = probeResolverHealth,
    attemptNpmCiRecoveryFn = attemptNpmCiRecovery,
    getNpmMajorVersionFn = getNpmMajorVersion,
    printActionableRepairBannerFn = printActionableRepairBanner,
    isAutoRepairAllowedFn = null,
    envFn = () => process.env,
    warnFn = console.warn
  } = options;

  const env = (envFn && envFn()) || process.env;
  if (!isTruthyEnv(env.DXMSG_HOOK_SKIP_INTEGRITY)) {
    const resolvedIsAutoRepairAllowed =
      isAutoRepairAllowedFn !== null
        ? isAutoRepairAllowedFn
        : () =>
            defaultIsAutoRepairAllowed({
              env,
              repoRoot: REPO_ROOT,
              getNpmMajorVersionFn
            });

    const gateResult = runIntegrityGateWithRecoveryFn({
      repoRoot: REPO_ROOT,
      probeIntegrityFn,
      probeIntegrityInSubprocessFn,
      probeResolverHealthFn,
      attemptNpmCiRecoveryFn,
      isAutoRepairAllowedFn: resolvedIsAutoRepairAllowed,
      printActionableRepairBannerFn,
      decoder: jestErrorDecoderModule,
      warnFn,
      env
    });

    if (!gateResult || !gateResult.ok) {
      if (isTruthyEnv(env.DXMSG_HOOK_NO_AUTOREPAIR)) {
        warnFn(
          "WARNING: integrity gate failed but DXMSG_HOOK_NO_AUTOREPAIR=1 -> proceeding to cspell invocation with degraded gate."
        );
      } else {
        return { status: 1, error: null };
      }
    }
  }

  if (existsSyncFn(LOCAL_CSPELL_BIN)) {
    return runLocalCspellFn(args);
  }
  return runNpxCspellFn(args);
}

function printManagedCspellLaunchError(error) {
  const detail = error && error.message ? ` (${error.message})` : "";
  console.error(`Failed to launch managed cspell${detail}.`);
  console.error("Ensure Node.js/npm are available in this shell, or run npm install.");
  if (process.platform === "win32") {
    console.error(
      "Windows tip: if you use nvm/fnm, open PowerShell or Git Bash with Node initialized and verify npm --version."
    );
  }
}

function main() {
  const result = runManagedCspell(process.argv.slice(2));
  if (result.error) {
    printManagedCspellLaunchError(result.error);
    process.exit(1);
  }
  process.exit(typeof result.status === "number" ? result.status : 1);
}

module.exports = {
  REPO_ROOT,
  LOCAL_CSPELL_BIN,
  FALLBACK_CSPELL_SPEC,
  normalizeVersion,
  getPinnedCspellSpec,
  runCommand,
  runLocalCspell,
  runNpxCspell,
  runManagedCspell,
  printManagedCspellLaunchError
};

if (require.main === module) {
  main();
}
