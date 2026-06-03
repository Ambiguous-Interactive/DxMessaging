/**
 * @fileoverview Tests for scripts/run-managed-cspell.js (Step 8).
 *
 * Mirrors the run-managed-prettier shape: integrity gate first, then local-
 * tier preferred when healthy, then npx fallback on cold caches. Version
 * pin is sourced from package.json#devDependencies.cspell.
 */

"use strict";

const path = require("path");

const {
  REPO_ROOT,
  LOCAL_CSPELL_BIN,
  FALLBACK_CSPELL_SPEC,
  normalizeVersion,
  getPinnedCspellSpec,
  runNpxCspell,
  runManagedCspell
} = require("../run-managed-cspell");

describe("run-managed-cspell", () => {
  // The integrity gate caches its "ok" verdict per-repoRoot to amortize
  // the resolver-probe subprocess spawn across the managed wrappers in a
  // single hook. Multiple tests below share the same REPO_ROOT; without
  // a per-test reset, a previous test's success verdict would
  // short-circuit later tests' gate-failure assertions.
  const { __clearIntegrityGateCacheForTests } = require("../lib/integrity-gate-with-recovery");
  beforeEach(() => {
    __clearIntegrityGateCacheForTests();
  });

  test("normalizeVersion strips ^/~ prefixes", () => {
    expect(normalizeVersion("10.0.0")).toBe("10.0.0");
    expect(normalizeVersion("^10.0.0")).toBe("10.0.0");
    expect(normalizeVersion("~10.0.0")).toBe("10.0.0");
    expect(normalizeVersion("not-a-version")).toBe(null);
    expect(normalizeVersion(null)).toBe(null);
  });

  test("getPinnedCspellSpec reads version from package.json devDependencies", () => {
    const readFileSyncFn = jest.fn(() => JSON.stringify({ devDependencies: { cspell: "10.1.2" } }));
    expect(getPinnedCspellSpec(readFileSyncFn)).toBe("cspell@10.1.2");
  });

  test("getPinnedCspellSpec falls back to static spec when package.json is invalid", () => {
    const readFileSyncFn = jest.fn(() => "not-json");
    expect(getPinnedCspellSpec(readFileSyncFn)).toBe(FALLBACK_CSPELL_SPEC);
  });

  test("getPinnedCspellSpec falls back when devDependencies.cspell is missing", () => {
    const readFileSyncFn = jest.fn(() => JSON.stringify({ devDependencies: {} }));
    expect(getPinnedCspellSpec(readFileSyncFn)).toBe(FALLBACK_CSPELL_SPEC);
  });

  test("runNpxCspell invokes bundled npx with the pinned package spec", () => {
    const runBundledNpxCommandFn = jest.fn(() => ({ status: 0, error: null }));
    const result = runNpxCspell(["--no-progress", "README.md"], "cspell@10.0.0", {
      runBundledNpxCommandFn
    });
    expect(result).toEqual({ status: 0, error: null });
    expect(runBundledNpxCommandFn).toHaveBeenCalledWith(
      ["--yes", "--package=cspell@10.0.0", "cspell", "--no-progress", "README.md"],
      expect.objectContaining({
        cwd: REPO_ROOT,
        stdio: "inherit"
      })
    );
  });

  test("runNpxCspell returns launch error object when bundled npx resolver throws", () => {
    const err = new Error("missing npx-cli.js");
    const result = runNpxCspell(["--check", "x"], "cspell@10.0.0", {
      runBundledNpxCommandFn: () => {
        throw err;
      }
    });
    expect(result).toEqual({ status: null, error: err });
  });

  test("runManagedCspell prefers local cspell when present", () => {
    const runLocalCspellFn = jest.fn(() => ({ status: 0, error: null }));
    const runNpxCspellFn = jest.fn();

    const result = runManagedCspell(["--no-progress", "x.md"], {
      envFn: () => ({ DXMSG_HOOK_SKIP_INTEGRITY: "1" }),
      existsSyncFn: () => true,
      runLocalCspellFn,
      runNpxCspellFn
    });

    expect(result).toEqual({ status: 0, error: null });
    expect(runLocalCspellFn).toHaveBeenCalledWith(["--no-progress", "x.md"]);
    expect(runNpxCspellFn).not.toHaveBeenCalled();
  });

  test("runManagedCspell falls back to npx when local cspell is missing", () => {
    const runLocalCspellFn = jest.fn();
    const runNpxCspellFn = jest.fn(() => ({ status: 0, error: null }));

    const result = runManagedCspell(["--no-progress", "x.md"], {
      envFn: () => ({ DXMSG_HOOK_SKIP_INTEGRITY: "1" }),
      existsSyncFn: () => false,
      runLocalCspellFn,
      runNpxCspellFn
    });

    expect(result).toEqual({ status: 0, error: null });
    expect(runNpxCspellFn).toHaveBeenCalledWith(["--no-progress", "x.md"]);
    expect(runLocalCspellFn).not.toHaveBeenCalled();
  });

  test("integrity gate runs BEFORE cspell tier dispatch", () => {
    const probeIntegrityFn = jest.fn(() => ({
      ok: false,
      missing: [{ tool: "cspell", relPath: "node_modules/cspell/bin.mjs", reason: "missing" }]
    }));
    const probeIntegrityInSubprocessFn = jest
      .fn()
      .mockReturnValueOnce({
        ok: false,
        missing: [{ tool: "cspell", relPath: "node_modules/cspell/bin.mjs", reason: "missing" }]
      })
      .mockReturnValueOnce({ ok: true, missing: [] });
    const attemptNpmCiRecoveryFn = jest.fn(() => ({ status: 0, error: null }));
    const isAutoRepairAllowedFn = jest.fn(() => ({ allowed: true, reason: null }));
    const runLocalCspellFn = jest.fn(() => ({ status: 0, error: null }));

    const result = runManagedCspell(["x.md"], {
      envFn: () => ({}),
      probeIntegrityFn,
      probeIntegrityInSubprocessFn,
      attemptNpmCiRecoveryFn,
      isAutoRepairAllowedFn,
      existsSyncFn: () => true,
      runLocalCspellFn,
      runNpxCspellFn: jest.fn()
    });

    expect(probeIntegrityFn).toHaveBeenCalledTimes(1);
    expect(isAutoRepairAllowedFn).toHaveBeenCalledTimes(1);
    expect(attemptNpmCiRecoveryFn).toHaveBeenCalledTimes(1);
    expect(probeIntegrityInSubprocessFn).toHaveBeenCalledTimes(2);
    expect(probeIntegrityInSubprocessFn.mock.invocationCallOrder[0]).toBeLessThan(
      runLocalCspellFn.mock.invocationCallOrder[0]
    );
    expect(result).toEqual({ status: 0, error: null });
  });

  test("integrity gate failure returns status=1 without invoking cspell", () => {
    const probeIntegrityFn = jest.fn(() => ({
      ok: false,
      missing: [{ tool: "cspell", relPath: "x", reason: "missing" }]
    }));
    const probeIntegrityInSubprocessFn = jest.fn(() => ({
      ok: false,
      missing: [{ tool: "cspell", relPath: "x", reason: "missing" }]
    }));
    const attemptNpmCiRecoveryFn = jest.fn(() => ({ status: 0, error: null }));
    const isAutoRepairAllowedFn = jest.fn(() => ({ allowed: true, reason: null }));
    const runLocalCspellFn = jest.fn();
    const printActionableRepairBannerFn = jest.fn();

    const result = runManagedCspell(["x.md"], {
      envFn: () => ({}),
      probeIntegrityFn,
      probeIntegrityInSubprocessFn,
      attemptNpmCiRecoveryFn,
      isAutoRepairAllowedFn,
      existsSyncFn: () => true,
      runLocalCspellFn,
      runNpxCspellFn: jest.fn(),
      printActionableRepairBannerFn
    });

    expect(runLocalCspellFn).not.toHaveBeenCalled();
    expect(probeIntegrityInSubprocessFn).toHaveBeenCalledTimes(2);
    expect(printActionableRepairBannerFn).toHaveBeenCalledTimes(1);
    expect(result).toEqual({ status: 1, error: null });
  });

  test("LOCAL_CSPELL_BIN points at node_modules/cspell/bin.mjs", () => {
    expect(LOCAL_CSPELL_BIN).toBe(path.join(REPO_ROOT, "node_modules", "cspell", "bin.mjs"));
  });
});
