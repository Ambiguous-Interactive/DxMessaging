/**
 * @fileoverview Tests for scripts/lib/prettier-version.js.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const {
  FALLBACK_PRETTIER_SPEC,
  normalizePinnedVersion,
  getConfiguredPrettierSpec,
  getPinnedFallbackPrettierSpec,
  getPinnedPrettierSpec
} = require("../lib/prettier-version");

function createReadFileSyncStub({ packageJson, packageLock } = {}) {
  return (filePath) => {
    if (filePath.endsWith("package.json")) {
      if (packageJson === undefined) {
        throw new Error("package.json unavailable");
      }
      return packageJson;
    }

    if (filePath.endsWith("package-lock.json")) {
      if (packageLock === undefined) {
        throw new Error("package-lock.json unavailable");
      }
      return packageLock;
    }

    throw new Error(`Unexpected path: ${filePath}`);
  };
}

describe("prettier-version", () => {
  test("normalizePinnedVersion strips supported semver prefixes", () => {
    expect(normalizePinnedVersion("^3.8.3")).toBe("3.8.3");
    expect(normalizePinnedVersion("~3.8.3")).toBe("3.8.3");
    expect(normalizePinnedVersion("3.8.3")).toBe("3.8.3");
  });

  test("normalizePinnedVersion rejects non-semver strings", () => {
    expect(normalizePinnedVersion("latest")).toBeNull();
    expect(normalizePinnedVersion("^3.8")).toBeNull();
    expect(normalizePinnedVersion(null)).toBeNull();
  });

  test("getConfiguredPrettierSpec reads package.json devDependency", () => {
    const readFileSyncFn = createReadFileSyncStub({
      packageJson: JSON.stringify({
        devDependencies: {
          prettier: "^3.8.3"
        }
      })
    });

    expect(getConfiguredPrettierSpec(readFileSyncFn)).toBe("prettier@3.8.3");
  });

  test("getConfiguredPrettierSpec returns null when prettier is missing", () => {
    const readFileSyncFn = createReadFileSyncStub({
      packageJson: JSON.stringify({ devDependencies: {} })
    });

    expect(getConfiguredPrettierSpec(readFileSyncFn)).toBeNull();
  });

  test("getPinnedFallbackPrettierSpec prefers lockfile version", () => {
    const readFileSyncFn = createReadFileSyncStub({
      packageLock: JSON.stringify({
        packages: {
          "node_modules/prettier": {
            version: "3.8.4"
          }
        }
      })
    });

    expect(getPinnedFallbackPrettierSpec(readFileSyncFn)).toBe("prettier@3.8.4");
  });

  test("getPinnedFallbackPrettierSpec uses fallback when lockfile is invalid", () => {
    const readFileSyncFn = createReadFileSyncStub({
      packageLock: "not-json"
    });

    expect(getPinnedFallbackPrettierSpec(readFileSyncFn)).toBe(FALLBACK_PRETTIER_SPEC);
  });

  test("getPinnedPrettierSpec uses lockfile when available", () => {
    const readFileSyncFn = createReadFileSyncStub({
      packageJson: JSON.stringify({
        devDependencies: {
          prettier: "^3.8.3"
        }
      }),
      packageLock: JSON.stringify({
        packages: {
          "node_modules/prettier": {
            version: "3.9.0"
          }
        }
      })
    });

    expect(getPinnedPrettierSpec(readFileSyncFn)).toBe("prettier@3.9.0");
  });

  test("getPinnedPrettierSpec falls back to configured package version", () => {
    const readFileSyncFn = createReadFileSyncStub({
      packageJson: JSON.stringify({
        devDependencies: {
          prettier: "~3.8.3"
        }
      }),
      packageLock: "not-json"
    });

    expect(getPinnedPrettierSpec(readFileSyncFn)).toBe("prettier@3.8.3");
  });

  test("getPinnedPrettierSpec matches repository package.json configuration", () => {
    const packageJsonPath = path.resolve(__dirname, "../../package.json");
    const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, "utf8"));
    const configuredVersion = normalizePinnedVersion(packageJson.devDependencies.prettier);

    expect(configuredVersion).toBeTruthy();
    expect(getPinnedPrettierSpec()).toBe(`prettier@${configuredVersion}`);
  });
});
