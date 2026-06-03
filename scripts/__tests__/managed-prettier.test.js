"use strict";

const path = require("path");

const {
  MISSING_BUNDLED_NPX_CLI_MESSAGE,
  resolveBundledNpxCliPath,
  runBundledNpxCommand,
  loadLocalPrettier
} = require("../lib/managed-prettier");

describe("managed-prettier", () => {
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

  describe("loadLocalPrettier integrity probe (Step 7)", () => {
    const fakeModulePath = "/repo/node_modules/prettier/index.cjs";
    const fakeBinPath = "/repo/node_modules/prettier/bin/prettier.cjs";

    test("returns the require()'d module when both module and bin are present and non-empty", () => {
      const requireFn = jest.fn(() => ({ format: () => "" }));
      const result = loadLocalPrettier({
        localPrettierModulePath: fakeModulePath,
        localPrettierBinPath: fakeBinPath,
        existsSyncFn: () => true,
        statSyncFn: () => ({ size: 100 }),
        requireFn
      });
      expect(requireFn).toHaveBeenCalledWith(fakeModulePath);
      expect(result).toEqual({ format: expect.any(Function) });
    });

    test("returns null when localPrettierBinPath is zero-byte (size 0)", () => {
      const requireFn = jest.fn();
      const result = loadLocalPrettier({
        localPrettierModulePath: fakeModulePath,
        localPrettierBinPath: fakeBinPath,
        existsSyncFn: () => true,
        statSyncFn: (abs) => (abs === fakeBinPath ? { size: 0 } : { size: 100 }),
        requireFn
      });
      expect(result).toBeNull();
      expect(requireFn).not.toHaveBeenCalled();
    });

    test("returns null when localPrettierModulePath is zero-byte (size 0)", () => {
      const requireFn = jest.fn();
      const result = loadLocalPrettier({
        localPrettierModulePath: fakeModulePath,
        localPrettierBinPath: fakeBinPath,
        existsSyncFn: () => true,
        statSyncFn: (abs) => (abs === fakeModulePath ? { size: 0 } : { size: 100 }),
        requireFn
      });
      expect(result).toBeNull();
      expect(requireFn).not.toHaveBeenCalled();
    });

    test("preserves the 'bin without module' error when index.cjs is absent but bin is present", () => {
      const requireFn = jest.fn();
      expect(() =>
        loadLocalPrettier({
          localPrettierModulePath: fakeModulePath,
          localPrettierBinPath: fakeBinPath,
          existsSyncFn: (abs) => abs === fakeBinPath,
          statSyncFn: () => ({ size: 100 }),
          requireFn
        })
      ).toThrow(/devDependency layout is unexpected/);
    });

    test("returns null when neither path exists (clean missing-install case)", () => {
      const result = loadLocalPrettier({
        localPrettierModulePath: fakeModulePath,
        localPrettierBinPath: fakeBinPath,
        existsSyncFn: () => false,
        statSyncFn: () => ({ size: 100 }),
        requireFn: jest.fn()
      });
      expect(result).toBeNull();
    });

    test("require errors that look like missing dependencies return null (preserved behavior)", () => {
      const requireFn = jest.fn(() => {
        const err = new Error("Cannot find module 'transitive-dep'");
        err.code = "MODULE_NOT_FOUND";
        throw err;
      });
      const result = loadLocalPrettier({
        localPrettierModulePath: fakeModulePath,
        localPrettierBinPath: fakeBinPath,
        existsSyncFn: () => true,
        statSyncFn: () => ({ size: 100 }),
        requireFn
      });
      expect(result).toBeNull();
    });
  });
});
