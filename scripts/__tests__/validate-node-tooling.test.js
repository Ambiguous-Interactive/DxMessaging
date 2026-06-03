"use strict";

const path = require("path");

const {
  formatInstallGuidance,
  validateManagedNpxCliAvailability,
  validateManagedNpxPolicy,
  validateTooling
} = require("../validate-node-tooling");
const { toPosixPath } = require("../lib/path-classifier");

const REPO_ROOT = path.resolve(__dirname, "../..");

describe("validate-node-tooling", () => {
  test("passes when required files exist and load checks succeed", async () => {
    const violations = await validateTooling({
      existsSyncFn: () => true,
      requireFn: () => ({}),
      importFn: async () => ({}),
      enforceManagedNpxCliAvailability: false,
      enforceIntegrityProbe: false,
      scriptSources: [],
      toolSpecs: [
        {
          name: "prettier",
          requiredFiles: ["node_modules/prettier/index.cjs"],
          load: "require",
          entry: "node_modules/prettier/index.cjs"
        },
        {
          name: "markdownlint-cli2",
          requiredFiles: ["node_modules/markdownlint-cli2/markdownlint-cli2.mjs"],
          load: "import",
          entry: "node_modules/markdownlint-cli2/markdownlint-cli2.mjs"
        }
      ]
    });

    expect(violations).toEqual([]);
  });

  test("reports missing files and failed transitive imports", async () => {
    const violations = await validateTooling({
      existsSyncFn: () => false,
      requireFn: () => ({}),
      importFn: async () => {
        throw new Error("Cannot find package 'fast-glob' imported from globby/index.js");
      },
      enforceManagedNpxCliAvailability: false,
      enforceIntegrityProbe: false,
      scriptSources: [],
      toolSpecs: [
        {
          name: "markdownlint-cli2",
          requiredFiles: ["node_modules/markdownlint-cli2/markdownlint-cli2.mjs"],
          load: "import",
          entry: "node_modules/markdownlint-cli2/markdownlint-cli2.mjs"
        }
      ]
    });

    expect(violations).toEqual([
      "markdownlint-cli2: missing node_modules/markdownlint-cli2/markdownlint-cli2.mjs",
      expect.stringContaining("Cannot find package 'fast-glob'")
    ]);
  });

  test("reports unresolved modules for resolve-based tooling checks", async () => {
    const violations = await validateTooling({
      existsSyncFn: () => true,
      resolveModuleFn: () => null,
      enforceManagedNpxCliAvailability: false,
      enforceIntegrityProbe: false,
      scriptSources: [],
      toolSpecs: [
        {
          name: "jest-circus",
          requiredFiles: [],
          load: "resolve",
          entry: "jest-circus/runner"
        }
      ]
    });

    expect(violations).toEqual(["jest-circus: failed to resolve jest-circus/runner"]);
  });

  test("reports missing files when resolve-based entries point to stale paths", async () => {
    const resolvedRunnerPath = "/repo/node_modules/jest-circus/build/runner.js";
    const violations = await validateTooling({
      existsSyncFn: () => false,
      resolveModuleFn: () => resolvedRunnerPath,
      enforceManagedNpxCliAvailability: false,
      enforceIntegrityProbe: false,
      scriptSources: [],
      toolSpecs: [
        {
          name: "jest-circus",
          requiredFiles: [],
          load: "resolve",
          entry: "jest-circus/runner"
        }
      ]
    });

    expect(violations).toEqual([
      `jest-circus: resolved jest-circus/runner to missing file: ${resolvedRunnerPath}`
    ]);
  });

  test("reports load failure when resolve-based entry exists but cannot be required", async () => {
    const resolvedRunnerPath = "/repo/node_modules/jest-circus/build/runner.js";
    const violations = await validateTooling({
      existsSyncFn: () => true,
      resolveModuleFn: () => resolvedRunnerPath,
      requireFn: (modulePath) => {
        if (modulePath === resolvedRunnerPath) {
          throw new Error("Unexpected end of file");
        }
        return {};
      },
      enforceManagedNpxCliAvailability: false,
      enforceIntegrityProbe: false,
      scriptSources: [],
      toolSpecs: [
        {
          name: "jest-circus",
          requiredFiles: [],
          load: "resolve",
          entry: "jest-circus/runner"
        }
      ]
    });

    expect(violations).toEqual([
      "jest-circus: resolved jest-circus/runner could not be loaded: Unexpected end of file"
    ]);
  });

  test("validateManagedNpxPolicy reports direct npx process spawns", () => {
    const violations = validateManagedNpxPolicy({
      scriptSources: [
        {
          filePath: path.join(REPO_ROOT, "scripts", "bad-npx.js"),
          content: 'childProcess.spawnSync("npx", ["--yes", "prettier", "--check", "README.md"]);'
        }
      ]
    });

    expect(violations).toEqual([
      expect.stringContaining(
        "managed-npx-policy: scripts/bad-npx.js uses direct npx process spawning"
      )
    ]);
  });

  test("validateManagedNpxPolicy reports npx command variables passed to process invokers", () => {
    const violations = validateManagedNpxPolicy({
      scriptSources: [
        {
          filePath: path.join(REPO_ROOT, "scripts", "bad-npx-command-var.js"),
          content: [
            'const childProcess = require("child_process");',
            'const npxCommand = "npx";',
            'childProcess.spawnSync(npxCommand, ["--yes", "prettier", "--check", "README.md"]);'
          ].join("\n")
        }
      ]
    });

    expect(violations).toEqual([
      expect.stringContaining(
        "managed-npx-policy: scripts/bad-npx-command-var.js uses direct npx process spawning"
      )
    ]);
  });

  test("validateManagedNpxPolicy reports child_process alias invokers with npx command variables", () => {
    const violations = validateManagedNpxPolicy({
      scriptSources: [
        {
          filePath: path.join(REPO_ROOT, "scripts", "bad-npx-invoker-alias.js"),
          content: [
            'const childProcess = require("child_process");',
            "const { spawnSync: invoke } = childProcess;",
            'const cmd = "npx.cmd";',
            'invoke(cmd, ["--yes", "prettier", "--check", "README.md"]);'
          ].join("\n")
        }
      ]
    });

    expect(violations).toEqual([
      expect.stringContaining(
        "managed-npx-policy: scripts/bad-npx-invoker-alias.js uses direct npx process spawning"
      )
    ]);
  });

  test("validateManagedNpxPolicy allows managed bundled npx helper usage", () => {
    const violations = validateManagedNpxPolicy({
      scriptSources: [
        {
          filePath: path.join(REPO_ROOT, "scripts", "good-npx.js"),
          content:
            'runBundledNpxCommand(["--yes", "--package=prettier@3.8.3", "prettier", "--check", "README.md"]);'
        }
      ]
    });

    expect(violations).toEqual([]);
  });

  test("validateManagedNpxCliAvailability reports missing bundled npx-cli", () => {
    const violations = validateManagedNpxCliAvailability({
      execPath: "/opt/node/bin/node",
      resolveBundledNpxCliPathFn: () => null,
      existsSyncFn: () => false
    });

    expect(violations).toEqual([
      expect.stringContaining("unable to resolve npm bundled npx-cli.js")
    ]);
  });

  test("validateManagedNpxCliAvailability passes when bundled npx-cli resolves", () => {
    const violations = validateManagedNpxCliAvailability({
      execPath: "/opt/node/bin/node",
      resolveBundledNpxCliPathFn: () => "/opt/node/bin/node_modules/npm/bin/npx-cli.js",
      existsSyncFn: () => true
    });

    expect(violations).toEqual([]);
  });

  test("validateTooling includes managed npx-cli availability violations by default", async () => {
    const violations = await validateTooling({
      existsSyncFn: () => true,
      statSyncFn: () => ({ size: 100 }),
      requireFn: () => ({}),
      importFn: async () => ({}),
      resolveBundledNpxCliPathFn: () => null,
      scriptSources: [],
      toolSpecs: []
    });

    expect(violations).toEqual([
      expect.stringContaining("unable to resolve npm bundled npx-cli.js")
    ]);
  });

  test("flags missing jest-circus/build/runner.js even when bin/jest.js exists (integrity layer)", async () => {
    // Step 6: the integrity probe layer must surface the partial-extract
    // failure mode (`testRunner option was not found`) - bin/jest.js present
    // but build/runner.js missing - as a violation.
    const violations = await validateTooling({
      // POSIX-normalize the absolute path before substring comparison so the
      // fixture behaves identically on Windows (where path.join uses "\")
      // and on POSIX.
      existsSyncFn: (abs) => !toPosixPath(abs).endsWith("runner.js"),
      statSyncFn: () => ({ size: 100 }),
      requireFn: () => ({}),
      importFn: async () => ({}),
      resolveModuleFn: () => "/repo/node_modules/jest-circus/build/runner.js",
      enforceManagedNpxCliAvailability: false,
      scriptSources: [],
      toolSpecs: []
    });
    expect(
      violations.some((v) =>
        v.includes("jest-circus: missing node_modules/jest-circus/build/runner.js")
      )
    ).toBe(true);
  });

  test("flags zero-byte critical files (size 0) via the integrity layer", async () => {
    const violations = await validateTooling({
      existsSyncFn: () => true,
      statSyncFn: (abs) =>
        // POSIX-normalize the absolute path before substring comparison so
        // the fixture is platform-agnostic.
        toPosixPath(abs).endsWith("prettier/index.cjs") ? { size: 0 } : { size: 100 },
      requireFn: () => ({}),
      importFn: async () => ({}),
      resolveModuleFn: () => "/repo/node_modules/jest-circus/build/runner.js",
      enforceManagedNpxCliAvailability: false,
      scriptSources: [],
      toolSpecs: []
    });
    expect(
      violations.some((v) =>
        v.includes("prettier: node_modules/prettier/index.cjs is empty (size 0)")
      )
    ).toBe(true);
  });

  test("INTEGRITY_TARGETS is the source of truth shared with the gate", () => {
    const { INTEGRITY_TARGETS: gateTargets } = require("../lib/node-modules-integrity");
    const { INTEGRITY_TARGETS: validatorTargets } = require("../validate-node-tooling");
    // The validator re-exports the same frozen array reference.
    expect(validatorTargets).toBe(gateTargets);
  });

  test("install guidance keeps hooks out of dependency bootstrapping", () => {
    expect(formatInstallGuidance()).toContain("npm install");
    expect(formatInstallGuidance()).toContain("npm ci");
  });

  test("install guidance references preflight:pre-push and the jest-hook-robustness skill", () => {
    const guidance = formatInstallGuidance();
    expect(guidance).toContain("npm run preflight:pre-push");
    expect(guidance).toContain(".llm/skills/scripting/jest-hook-robustness.md");
  });
});
