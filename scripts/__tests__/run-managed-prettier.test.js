/**
 * @fileoverview Tests for run-managed-prettier.js.
 */

"use strict";

const childProcess = require("child_process");
const path = require("path");
const {
  MISSING_BUNDLED_NPX_CLI_MESSAGE,
  resolveBundledNpxCliPath,
  runBundledNpxCommand
} = require("../lib/managed-prettier");
const {
  REPO_ROOT,
  runCommand,
  runNpxPrettier,
  runManagedPrettier
} = require("../run-managed-prettier");

describe("run-managed-prettier", () => {
  // The integrity gate caches its "ok" verdict per-repoRoot to amortize
  // the resolver-probe subprocess spawn across the managed wrappers in a
  // single hook. Tests below that exercise the gate share the same
  // REPO_ROOT; without a per-test reset, a previous test's success
  // verdict would short-circuit later tests' gate-failure assertions.
  const { __clearIntegrityGateCacheForTests } = require("../lib/integrity-gate-with-recovery");
  beforeEach(() => {
    __clearIntegrityGateCacheForTests();
  });

  afterEach(() => {
    jest.restoreAllMocks();
  });

  test("runManagedPrettier prefers local prettier when available", () => {
    const runLocalPrettierFn = jest.fn(() => ({ status: 0, error: null }));
    const runNpxPrettierFn = jest.fn(() => ({ status: 1, error: null }));

    const result = runManagedPrettier(["--check", "README.md"], {
      // Bypass the integrity gate so this tier-fallback test is
      // independent of on-disk node_modules state. See
      // scripts/__tests__/run-managed-cspell.test.js for the same
      // pattern.
      envFn: () => ({ DXMSG_HOOK_SKIP_INTEGRITY: "1" }),
      existsSyncFn: () => true,
      runLocalPrettierFn,
      runNpxPrettierFn
    });

    expect(result).toEqual({ status: 0, error: null });
    expect(runLocalPrettierFn).toHaveBeenCalledWith(["--check", "README.md"]);
    expect(runNpxPrettierFn).not.toHaveBeenCalled();
  });

  test("runManagedPrettier falls back to npx when local prettier is missing", () => {
    const runLocalPrettierFn = jest.fn(() => ({ status: 0, error: null }));
    const runNpxPrettierFn = jest.fn(() => ({ status: 0, error: null }));

    const result = runManagedPrettier(["--write", "README.md"], {
      // Bypass the integrity gate (see rationale above).
      envFn: () => ({ DXMSG_HOOK_SKIP_INTEGRITY: "1" }),
      existsSyncFn: () => false,
      runLocalPrettierFn,
      runNpxPrettierFn
    });

    expect(result).toEqual({ status: 0, error: null });
    expect(runLocalPrettierFn).not.toHaveBeenCalled();
    expect(runNpxPrettierFn).toHaveBeenCalledWith(["--write", "README.md"]);
  });

  test("runNpxPrettier invokes bundled npx with pinned package spec", () => {
    const runBundledNpxCommandFn = jest.fn(() => ({ status: 0, error: null }));

    const result = runNpxPrettier(["--check", "README.md"], "prettier@3.8.3", {
      runBundledNpxCommandFn
    });

    expect(result).toEqual({ status: 0, error: null });
    expect(runBundledNpxCommandFn).toHaveBeenCalledWith(
      ["--yes", "--package=prettier@3.8.3", "prettier", "--check", "README.md"],
      expect.objectContaining({
        cwd: REPO_ROOT,
        stdio: "inherit"
      })
    );
  });

  test("runNpxPrettier returns launch error object when bundled npx resolver throws", () => {
    const missingCliError = new Error("missing npx-cli.js");

    const result = runNpxPrettier(["--check", "README.md"], "prettier@3.8.3", {
      runBundledNpxCommandFn: () => {
        throw missingCliError;
      }
    });

    expect(result).toEqual({
      status: null,
      error: missingCliError
    });
  });

  test("runCommand delegates non-shell-shim commands to child_process.spawnSync", () => {
    const spawnSyncSpy = jest
      .spyOn(childProcess, "spawnSync")
      .mockReturnValue({ status: 0, error: null });

    const result = runCommand(process.execPath, ["tool.js"]);

    expect(result).toEqual({ status: 0, error: null });
    expect(spawnSyncSpy).toHaveBeenCalledWith(
      process.execPath,
      ["tool.js"],
      expect.objectContaining({ cwd: REPO_ROOT, stdio: "inherit" })
    );
    spawnSyncSpy.mockRestore();
  });

  test("resolveBundledNpxCliPath returns bundled npx-cli.js when present", () => {
    const execPath = path.join(path.sep, "opt", "node", "bin", "node");
    const expected = path.join(path.dirname(execPath), "node_modules", "npm", "bin", "npx-cli.js");

    const resolved = resolveBundledNpxCliPath({
      execPath,
      existsSyncFn: (candidatePath) => candidatePath === expected
    });

    expect(resolved).toBe(expected);
  });

  test("resolveBundledNpxCliPath returns null when bundled npx-cli.js is missing", () => {
    const resolved = resolveBundledNpxCliPath({
      execPath: path.join(path.sep, "opt", "node", "bin", "node"),
      existsSyncFn: () => false
    });

    expect(resolved).toBeNull();
  });

  test("resolveBundledNpxCliPath supports Linux distro npm layout", () => {
    const execPath = path.join(path.sep, "usr", "bin", "node");
    const expected = path.join(path.sep, "usr", "lib", "node_modules", "npm", "bin", "npx-cli.js");

    const resolved = resolveBundledNpxCliPath({
      execPath,
      existsSyncFn: (candidatePath) => candidatePath === expected
    });

    expect(resolved).toBe(expected);
  });

  test("runBundledNpxCommand invokes Node with the bundled npx CLI", () => {
    const execPath = String.raw`C:\node\node.exe`;
    const npxCliPath = String.raw`C:\node\node_modules\npm\bin\npx-cli.js`;
    const runCommandFn = jest.fn(() => ({ status: 0 }));

    const result = runBundledNpxCommand(["--yes", "prettier", "--check", "README.md"], {
      execPath,
      resolveBundledNpxCliPathFn: () => npxCliPath,
      runCommandFn,
      cwd: path.join(path.sep, "repo")
    });

    expect(result).toEqual({ status: 0 });
    expect(runCommandFn).toHaveBeenCalledWith(
      execPath,
      [npxCliPath, "--yes", "prettier", "--check", "README.md"],
      expect.objectContaining({
        cwd: path.join(path.sep, "repo"),
        encoding: "utf8"
      })
    );
  });

  test("runBundledNpxCommand fails closed when bundled npx CLI cannot be resolved", () => {
    expect(() =>
      runBundledNpxCommand(["--yes", "prettier", "--check", "README.md"], {
        resolveBundledNpxCliPathFn: () => null,
        runCommandFn: jest.fn()
      })
    ).toThrow(MISSING_BUNDLED_NPX_CLI_MESSAGE);
  });

  test("integrity gate runs BEFORE prettier tier dispatch", () => {
    // Step 7: prettier wrapper mirrors the jest wrapper's gate. If the
    // gate fails and auto-repair is allowed, npm ci runs and we then
    // re-probe before the local prettier path is taken.
    const probeIntegrityFn = jest.fn(() => ({
      ok: false,
      missing: [{ tool: "prettier", relPath: "node_modules/prettier/index.cjs", reason: "missing" }]
    }));
    const probeIntegrityInSubprocessFn = jest
      .fn()
      .mockReturnValueOnce({
        ok: false,
        missing: [
          { tool: "prettier", relPath: "node_modules/prettier/index.cjs", reason: "missing" }
        ]
      })
      .mockReturnValueOnce({ ok: true, missing: [] });
    const attemptNpmCiRecoveryFn = jest.fn(() => ({ status: 0, error: null }));
    const isAutoRepairAllowedFn = jest.fn(() => ({ allowed: true, reason: null }));
    const runLocalPrettierFn = jest.fn(() => ({ status: 0, error: null }));
    const runNpxPrettierFn = jest.fn();

    const result = runManagedPrettier(["--check", "README.md"], {
      envFn: () => ({}),
      probeIntegrityFn,
      probeIntegrityInSubprocessFn,
      attemptNpmCiRecoveryFn,
      isAutoRepairAllowedFn,
      existsSyncFn: () => true,
      runLocalPrettierFn,
      runNpxPrettierFn
    });

    expect(probeIntegrityFn).toHaveBeenCalledTimes(1);
    expect(isAutoRepairAllowedFn).toHaveBeenCalledTimes(1);
    expect(attemptNpmCiRecoveryFn).toHaveBeenCalledTimes(1);
    expect(probeIntegrityInSubprocessFn).toHaveBeenCalledTimes(2);
    // Tier dispatch happens AFTER gate succeeds.
    expect(probeIntegrityInSubprocessFn.mock.invocationCallOrder[0]).toBeLessThan(
      runLocalPrettierFn.mock.invocationCallOrder[0]
    );
    expect(result).toEqual({ status: 0, error: null });
  });

  test("integrity gate failure with no auto-repair returns status=1 without invoking prettier", () => {
    const probeIntegrityFn = jest.fn(() => ({
      ok: false,
      missing: [{ tool: "prettier", relPath: "x", reason: "missing" }]
    }));
    const probeIntegrityInSubprocessFn = jest.fn(() => ({
      ok: false,
      missing: [{ tool: "prettier", relPath: "x", reason: "missing" }]
    }));
    const attemptNpmCiRecoveryFn = jest.fn(() => ({ status: 0, error: null }));
    const isAutoRepairAllowedFn = jest.fn(() => ({ allowed: true, reason: null }));
    const runLocalPrettierFn = jest.fn();
    const printActionableRepairBannerFn = jest.fn();

    const result = runManagedPrettier(["--check", "README.md"], {
      envFn: () => ({}),
      probeIntegrityFn,
      probeIntegrityInSubprocessFn,
      attemptNpmCiRecoveryFn,
      isAutoRepairAllowedFn,
      existsSyncFn: () => true,
      runLocalPrettierFn,
      runNpxPrettierFn: jest.fn(),
      printActionableRepairBannerFn
    });

    expect(runLocalPrettierFn).not.toHaveBeenCalled();
    expect(probeIntegrityInSubprocessFn).toHaveBeenCalledTimes(2);
    expect(printActionableRepairBannerFn).toHaveBeenCalledTimes(1);
    expect(result).toEqual({ status: 1, error: null });
  });

  test("DXMSG_HOOK_SKIP_INTEGRITY=1 bypasses the prettier integrity gate", () => {
    const probeIntegrityFn = jest.fn();
    const runLocalPrettierFn = jest.fn(() => ({ status: 0, error: null }));

    const result = runManagedPrettier(["--check", "README.md"], {
      envFn: () => ({ DXMSG_HOOK_SKIP_INTEGRITY: "1" }),
      probeIntegrityFn,
      existsSyncFn: () => true,
      runLocalPrettierFn,
      runNpxPrettierFn: jest.fn()
    });

    expect(probeIntegrityFn).not.toHaveBeenCalled();
    expect(runLocalPrettierFn).toHaveBeenCalledTimes(1);
    expect(result).toEqual({ status: 0, error: null });
  });
});
