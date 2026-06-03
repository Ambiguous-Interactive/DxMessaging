/**
 * @fileoverview Locks the env-driven contract between the integrity gate
 * and the isolated fallback selection.
 *
 * Background: `.github/workflows/pre-commit-tooling-check.yml` deletes
 * `node_modules/jest-circus/build/runner.js` to force the isolated fallback
 * path, then asserts that the "Using isolated fallback Jest" diagnostic
 * appears. Production scripts/run-managed-jest.js runs the integrity gate
 * BEFORE the tier dispatcher; when the missing runner is detected the gate
 * auto-runs `npm ci` and silently restores the local install -- which is
 * correct production behavior but invalidates the workflow assertion.
 *
 * This test pins the env-driven escape hatch:
 *   - DXMSG_HOOK_SKIP_INTEGRITY=1 bypasses the gate entirely.
 *   - DXMSG_HOOK_NO_AUTOREPAIR=1 still runs the probe (diagnostic logging
 *     stays) but skips the npm ci repair so the local install remains
 *     unhealthy and the dispatcher selects the isolated tier.
 *   - With both unset, the gate auto-repairs and the local tier wins.
 *
 * The CI step at `Assert isolated Jest fallback path is active` sets
 * NO_AUTOREPAIR explicitly. This test guarantees that env continues to
 * route execution through the isolated tier even if future refactors
 * reshuffle the integrity gate.
 */

"use strict";

const { runManagedJest } = require("../run-managed-jest.js");

function makeGateResult(ok) {
  return ok ? { ok: true } : { ok: false, reason: "integrity-probe-failed" };
}

/**
 * Drive runManagedJest with deterministic dependency injection. The
 * `runIntegrityGateWithRecoveryFn` mock simulates the production gate:
 * when auto-repair is allowed and integrity fails, it "fixes" the local
 * install and returns ok=true; otherwise it reports ok=false.
 */
function runWithEnv(envOverrides, { localHealthy = true, autoRepairAllowed = true } = {}) {
  const previousEnv = {};
  for (const key of ["DXMSG_HOOK_SKIP_INTEGRITY", "DXMSG_HOOK_NO_AUTOREPAIR"]) {
    previousEnv[key] = process.env[key];
    if (envOverrides[key] === undefined) {
      delete process.env[key];
    } else {
      process.env[key] = envOverrides[key];
    }
  }

  const localResult = { status: 0, error: null, stderr: "" };
  const isolatedResult = { status: 0, error: null, stderr: "" };

  const runLocalJestFn = jest.fn(() => localResult);
  const runIsolatedFallbackJestFn = jest.fn(() => isolatedResult);
  const runNpmExecJestFn = jest.fn();
  const runNpxJestFn = jest.fn();
  const printLocalJestFallbackWarningFn = jest.fn();

  let healthyAfterRepair = localHealthy;
  const hasHealthyLocalJestInstallFn = jest.fn(() => healthyAfterRepair);

  const runIntegrityGateWithRecoveryFn = jest.fn(() => {
    if (localHealthy) {
      return makeGateResult(true);
    }
    if (autoRepairAllowed) {
      // Production-equivalent self-heal: gate fixes the install and
      // the dispatcher sees the local tier as healthy on the next call.
      healthyAfterRepair = true;
      return makeGateResult(true);
    }
    return makeGateResult(false);
  });

  const isAutoRepairAllowedFn = jest.fn(() => ({
    allowed: autoRepairAllowed,
    reason: autoRepairAllowed ? "ok" : "NO_AUTOREPAIR=1"
  }));

  try {
    const result = runManagedJest(["--version"], {
      hasHealthyLocalJestInstallFn,
      runLocalJestFn,
      runIsolatedFallbackJestFn,
      runNpmExecJestFn,
      runNpxJestFn,
      printLocalJestFallbackWarningFn,
      runIntegrityGateWithRecoveryFn,
      isAutoRepairAllowedFn,
      getNpmMajorVersionFn: () => 10
    });

    return {
      result,
      runLocalJestFn,
      runIsolatedFallbackJestFn,
      runIntegrityGateWithRecoveryFn,
      hasHealthyLocalJestInstallFn,
      isAutoRepairAllowedFn,
      printLocalJestFallbackWarningFn,
      localResult,
      isolatedResult
    };
  } finally {
    for (const key of Object.keys(previousEnv)) {
      if (previousEnv[key] === undefined) {
        delete process.env[key];
      } else {
        process.env[key] = previousEnv[key];
      }
    }
  }
}

describe("run-managed-jest fallback isolation under env-driven gate controls", () => {
  test.each([
    {
      name: "default (no env vars set, healthy local) runs local tier",
      env: {},
      localHealthy: true,
      autoRepairAllowed: true,
      expectLocal: true,
      expectIsolated: false,
      expectGateInvoked: true
    },
    {
      name: "default (no env vars set, unhealthy local) gate auto-repairs and local tier runs",
      env: {},
      localHealthy: false,
      autoRepairAllowed: true,
      expectLocal: true,
      expectIsolated: false,
      expectGateInvoked: true
    },
    {
      name: "NO_AUTOREPAIR=1 with unhealthy local -> falls through to isolated tier",
      env: { DXMSG_HOOK_NO_AUTOREPAIR: "1" },
      localHealthy: false,
      autoRepairAllowed: false,
      expectLocal: false,
      expectIsolated: true,
      expectGateInvoked: true
    },
    {
      name: "SKIP_INTEGRITY=1 with healthy local -> local tier still runs (gate bypassed)",
      env: { DXMSG_HOOK_SKIP_INTEGRITY: "1" },
      localHealthy: true,
      autoRepairAllowed: true,
      expectLocal: true,
      expectIsolated: false,
      expectGateInvoked: false
    },
    {
      name: "SKIP_INTEGRITY=1 with unhealthy local -> falls through to isolated (no gate to repair)",
      env: { DXMSG_HOOK_SKIP_INTEGRITY: "1" },
      localHealthy: false,
      autoRepairAllowed: true,
      expectLocal: false,
      expectIsolated: true,
      expectGateInvoked: false
    },
    {
      name: "BOTH env vars set with unhealthy local -> isolated tier (skip wins)",
      env: { DXMSG_HOOK_SKIP_INTEGRITY: "1", DXMSG_HOOK_NO_AUTOREPAIR: "1" },
      localHealthy: false,
      autoRepairAllowed: false,
      expectLocal: false,
      expectIsolated: true,
      expectGateInvoked: false
    }
  ])(
    "$name",
    ({ env, localHealthy, autoRepairAllowed, expectLocal, expectIsolated, expectGateInvoked }) => {
      const outcome = runWithEnv(env, { localHealthy, autoRepairAllowed });

      if (expectGateInvoked) {
        expect(outcome.runIntegrityGateWithRecoveryFn).toHaveBeenCalled();
      } else {
        expect(outcome.runIntegrityGateWithRecoveryFn).not.toHaveBeenCalled();
      }

      if (expectLocal) {
        expect(outcome.runLocalJestFn).toHaveBeenCalled();
        expect(outcome.result).toEqual(outcome.localResult);
      } else {
        expect(outcome.runLocalJestFn).not.toHaveBeenCalled();
      }

      if (expectIsolated) {
        expect(outcome.runIsolatedFallbackJestFn).toHaveBeenCalled();
        expect(outcome.result).toEqual(outcome.isolatedResult);
      } else {
        expect(outcome.runIsolatedFallbackJestFn).not.toHaveBeenCalled();
      }
    }
  );

  test("NO_AUTOREPAIR keeps the gate observable so diagnostic logs still print", () => {
    // Operators rely on the integrity probe output to confirm the
    // assertion step actually reached the gate. NO_AUTOREPAIR must NOT
    // suppress the gate invocation -- only the repair.
    const outcome = runWithEnv(
      { DXMSG_HOOK_NO_AUTOREPAIR: "1" },
      { localHealthy: false, autoRepairAllowed: false }
    );
    expect(outcome.runIntegrityGateWithRecoveryFn).toHaveBeenCalled();
    expect(outcome.runIsolatedFallbackJestFn).toHaveBeenCalled();
  });
});
