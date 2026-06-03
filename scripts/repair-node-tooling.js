#!/usr/bin/env node
"use strict";

const path = require("path");
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
  attemptNpmCiRecovery,
  getNpmMajorVersion,
  printActionableRepairBanner
} = require("./run-managed-jest");
const { healRegenerableCaches } = require("./lib/regenerable-cache-registry");

const REPO_ROOT = path.join(__dirname, "..");

function repairNodeTooling(options = {}) {
  const {
    env = process.env,
    repoRoot = REPO_ROOT,
    runIntegrityGateWithRecoveryFn = runIntegrityGateWithRecovery,
    probeIntegrityFn = probeIntegrity,
    probeIntegrityInSubprocessFn = probeIntegrityInSubprocess,
    probeResolverHealthFn = probeResolverHealth,
    attemptNpmCiRecoveryFn = attemptNpmCiRecovery,
    getNpmMajorVersionFn = getNpmMajorVersion,
    printActionableRepairBannerFn = printActionableRepairBanner,
    healRegenerableCachesFn = healRegenerableCaches,
    warnFn = console.warn
  } = options;

  // Heal regenerable caches (currently: the isolated managed-Jest fallback
  // cache under os.tmpdir()) FIRST -- and crucially BEFORE the
  // DXMSG_HOOK_SKIP_INTEGRITY early return below. This runs FIRST in the native
  // pre-push hook -- before `npm run doctor` -- so a corrupt/partial regenerable
  // artifact is auto-purged before the read-only doctor ever sees it, killing
  // the "doctor hard-FAILs a push on a regenerable cache" class with ZERO manual
  // touch.
  //
  // ORTHOGONALITY: the regenerable-cache heal is gated ONLY by its OWN dedicated
  // opt-out (DXMSG_HOOK_NO_REGENERABLE_HEAL, honored inside
  // healRegenerableCaches), NOT by DXMSG_HOOK_SKIP_INTEGRITY. The latter bypasses
  // the EXPENSIVE node_modules npm-ci integrity gate (a node_modules concern);
  // it must NOT silently also disable this cheap, safe, unrelated tmpdir-cache
  // heal -- an operator skipping the integrity probe would not expect the cache
  // auto-heal to be coupled off, and leaving the corrupt cache would let the
  // doctor's WARN (or a reactive run-managed-jest reset) surface needlessly.
  // Near-zero on the happy path: healIsolatedJestCache returns immediately (no
  // readdir, no lock) when the cache root is absent, the common case.
  //
  // Best-effort: the default healRegenerableCaches catches every per-healer
  // throw INTERNALLY, so it cannot throw. The try/catch here makes that "never
  // changes status" contract provable even if the orchestrator function ITSELF
  // throws (a future bug, or a non-default injected healRegenerableCachesFn): a
  // heal-orchestrator throw must never abort the bootstrap -- that would be
  // strictly worse than the corrupt cache it neutralizes -- so we warn and
  // continue.
  try {
    healRegenerableCachesFn({ env, warnFn });
  } catch (error) {
    const detail = error && error.message ? error.message : String(error);
    warnFn(`WARNING: Regenerable-cache heal orchestrator threw (best-effort, ignored): ${detail}`);
  }

  if (isTruthyEnv(env.DXMSG_HOOK_SKIP_INTEGRITY)) {
    warnFn("WARNING: DXMSG_HOOK_SKIP_INTEGRITY=1 set; skipping node_modules repair bootstrap.");
    return { status: 0, skipped: true };
  }

  const gateResult = runIntegrityGateWithRecoveryFn({
    repoRoot,
    probeIntegrityFn,
    probeIntegrityInSubprocessFn,
    probeResolverHealthFn,
    attemptNpmCiRecoveryFn,
    isAutoRepairAllowedFn: () =>
      defaultIsAutoRepairAllowed({
        env,
        repoRoot,
        getNpmMajorVersionFn
      }),
    printActionableRepairBannerFn,
    decoder: jestErrorDecoderModule,
    warnFn,
    env,
    bypassCache: true
  });

  return {
    status: gateResult && gateResult.ok ? 0 : 1,
    skipped: false,
    gateResult
  };
}

function main() {
  const result = repairNodeTooling();
  process.exit(result.status);
}

if (require.main === module) {
  main();
}

module.exports = {
  REPO_ROOT,
  repairNodeTooling
};
