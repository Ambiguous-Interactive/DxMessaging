/**
 * @fileoverview Tests for verify-managed-jest-fallback.js.
 */

"use strict";

const {
  FORCE_DELETE_FLAG,
  resolveManagedRunnerPath,
  isManagedRunnerPathSafe,
  assertManagedRunnerPathSafe,
  hasForcedDeletionOptIn,
  removeManagedRunner,
  verifyManagedJestFallback,
  main
} = require("../verify-managed-jest-fallback.js");

describe("verify-managed-jest-fallback", () => {
  test("resolveManagedRunnerPath uses provided resolver", () => {
    const moduleResolver = jest.fn(() => "/tmp/jest-circus/runner.js");

    const runnerPath = resolveManagedRunnerPath(moduleResolver);

    expect(runnerPath).toBe("/tmp/jest-circus/runner.js");
    expect(moduleResolver).toHaveBeenCalledWith("jest-circus/runner");
  });

  test("isManagedRunnerPathSafe accepts expected managed runner paths", () => {
    expect(isManagedRunnerPathSafe("/repo/node_modules/jest-circus/build/runner.js")).toBe(true);
  });

  test("isManagedRunnerPathSafe accepts Windows-style managed runner paths", () => {
    const pathModule = { resolve: (inputPath) => inputPath };
    expect(
      isManagedRunnerPathSafe("C:\\repo\\node_modules\\jest-circus\\build\\runner.cjs", {
        pathModule
      })
    ).toBe(true);
    expect(
      isManagedRunnerPathSafe("C:\\repo\\node_modules\\jest-circus\\build\\runner.mjs", {
        pathModule
      })
    ).toBe(true);
  });

  test("assertManagedRunnerPathSafe rejects unexpected deletion targets", () => {
    expect(() => assertManagedRunnerPathSafe("/tmp/runner.js")).toThrow(
      "Refusing to delete unexpected runner path"
    );
  });

  test("hasForcedDeletionOptIn requires explicit flag", () => {
    expect(hasForcedDeletionOptIn([])).toBe(false);
    expect(hasForcedDeletionOptIn([FORCE_DELETE_FLAG])).toBe(true);
  });

  test("removeManagedRunner throws when runner does not exist before deletion", () => {
    const existsSyncFn = jest.fn(() => false);
    const logFn = jest.fn();

    expect(() =>
      removeManagedRunner("/repo/node_modules/jest-circus/build/runner.js", { existsSyncFn, logFn })
    ).toThrow("Runner path does not exist before deletion.");
  });

  test("removeManagedRunner throws for unsafe path even if file exists", () => {
    const existsSyncFn = jest.fn(() => true);

    expect(() =>
      removeManagedRunner("/tmp/runner.js", {
        existsSyncFn,
        rmSyncFn: jest.fn(),
        logFn: jest.fn()
      })
    ).toThrow("Refusing to delete unexpected runner path");
  });

  test("removeManagedRunner removes existing runner", () => {
    const existsSyncFn = jest.fn().mockReturnValueOnce(true).mockReturnValueOnce(false);
    const rmSyncFn = jest.fn();
    const logFn = jest.fn();
    const runnerPath = "/repo/node_modules/jest-circus/build/runner.js";

    removeManagedRunner(runnerPath, { existsSyncFn, rmSyncFn, logFn });

    expect(logFn).toHaveBeenCalledWith(
      "Deleting managed Jest runner: /repo/node_modules/jest-circus/build/runner.js"
    );
    expect(rmSyncFn).toHaveBeenCalledWith(runnerPath);
    expect(existsSyncFn).toHaveBeenCalledTimes(2);
  });

  test("verifyManagedJestFallback resolves and removes runner", () => {
    const moduleResolver = jest.fn(() => "/repo/node_modules/jest-circus/build/runner.js");
    const existsSyncFn = jest.fn().mockReturnValueOnce(true).mockReturnValueOnce(false);
    const rmSyncFn = jest.fn();
    const logFn = jest.fn();

    const runnerPath = verifyManagedJestFallback({
      moduleResolver,
      existsSyncFn,
      rmSyncFn,
      logFn
    });

    expect(moduleResolver).toHaveBeenCalledWith("jest-circus/runner");
    expect(runnerPath).toBe("/repo/node_modules/jest-circus/build/runner.js");
    expect(rmSyncFn).toHaveBeenCalledWith("/repo/node_modules/jest-circus/build/runner.js");
  });

  test("main requires explicit force flag", () => {
    expect(() => main([])).toThrow("Refusing to delete managed Jest runner");
  });

  test("main invokes verifier when force flag is present", () => {
    const verifyManagedJestFallbackFn = jest.fn();

    main([FORCE_DELETE_FLAG], { verifyManagedJestFallbackFn });

    expect(verifyManagedJestFallbackFn).toHaveBeenCalledTimes(1);
  });
});
