#!/usr/bin/env node
/**
 * run-managed-prettier.js
 *
 * Runs Prettier in a robust, non-interactive way for hooks and local automation:
 * 1) Prefer local devDependency (node_modules/prettier/bin/prettier.cjs).
 * 2) If local Prettier is missing, provision a pinned fallback via npx.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");
const { runBundledNpxCommand } = require("./lib/managed-prettier");
const { getPinnedPrettierSpec } = require("./lib/prettier-version");
const jestErrorDecoderModule = require("./lib/jest-error-decoder");
const { isTruthyEnv } = jestErrorDecoderModule;
const {
  probeIntegrity,
  probeResolverHealth,
  probeIntegrityInSubprocess
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
const LOCAL_PRETTIER_BIN = path.join(REPO_ROOT, "node_modules", "prettier", "bin", "prettier.cjs");

function runCommand(command, args, options = {}) {
  const result = childProcess.spawnSync(command, args, {
    cwd: REPO_ROOT,
    stdio: "inherit",
    ...options
  });

  return {
    status: result.status,
    error: result.error || null
  };
}

function runLocalPrettier(args) {
  return runCommand(process.execPath, [LOCAL_PRETTIER_BIN, ...args]);
}

function runNpxPrettier(args, prettierSpec = getPinnedPrettierSpec(), options = {}) {
  const { runBundledNpxCommandFn = runBundledNpxCommand } = options;

  try {
    const result = runBundledNpxCommandFn(
      ["--yes", `--package=${prettierSpec}`, "prettier", ...args],
      {
        cwd: REPO_ROOT,
        stdio: "inherit"
      }
    );

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

function runManagedPrettier(args, options = {}) {
  const {
    existsSyncFn = fs.existsSync,
    runLocalPrettierFn = runLocalPrettier,
    runNpxPrettierFn = runNpxPrettier,
    // Integrity gate dependencies (Step 7). Same shape as
    // run-managed-jest; the gate logic lives in
    // scripts/lib/integrity-gate-with-recovery.js.
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

  // ---- Integrity gate (runs BEFORE tier dispatch) ----
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
          "WARNING: integrity gate failed but DXMSG_HOOK_NO_AUTOREPAIR=1 -> proceeding to Prettier invocation with degraded gate."
        );
      } else {
        return { status: 1, error: null };
      }
    }
  }

  if (existsSyncFn(LOCAL_PRETTIER_BIN)) {
    return runLocalPrettierFn(args);
  }

  return runNpxPrettierFn(args);
}

function printManagedPrettierLaunchError(error) {
  const detail = error && error.message ? ` (${error.message})` : "";
  console.error(`Failed to launch managed Prettier${detail}.`);
  console.error("Ensure Node.js/npm are available in this shell, or run npm install.");
  if (process.platform === "win32") {
    console.error(
      "Windows tip: if you use nvm/fnm, open PowerShell or Git Bash with Node initialized and verify npm --version."
    );
  }
}

function main() {
  const result = runManagedPrettier(process.argv.slice(2));

  if (result.error) {
    printManagedPrettierLaunchError(result.error);
    process.exit(1);
  }

  process.exit(typeof result.status === "number" ? result.status : 1);
}

module.exports = {
  REPO_ROOT,
  LOCAL_PRETTIER_BIN,
  runCommand,
  runLocalPrettier,
  runNpxPrettier,
  runManagedPrettier,
  printManagedPrettierLaunchError
};

if (require.main === module) {
  main();
}
