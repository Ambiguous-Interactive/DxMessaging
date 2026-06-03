/**
 * @fileoverview Version-parity tests for cspell and markdownlint-cli2.
 *
 * The hook entries in `.pre-commit-config.yaml` invoke pinned package
 * versions (e.g. `cspell@10.0.0`) for the npx fallback path. Those literal
 * versions MUST match the devDependencies versions in `package.json` so
 * cold-cache fallback installs the same binary that `npm install` brings
 * in. The previous round used a managed Node wrapper to derive the version
 * dynamically; the wrapper added wall-clock cost. These parity tests are
 * the cheaper guardrail against version drift.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const {
  expandExtensionPattern,
  getCspellHookExtensions,
  getPackageCspellAllExtensions
} = require("../lib/cspell-extension-parity");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const PACKAGE_JSON_PATH = path.join(REPO_ROOT, "package.json");
const PRE_COMMIT_CONFIG_PATH = path.join(REPO_ROOT, ".pre-commit-config.yaml");
const CSPELL_CONFIG_PATH = path.join(REPO_ROOT, ".cspell.json");
const WORKFLOWS_DIR = path.join(REPO_ROOT, ".github", "workflows");
const MANAGED_CSPELL_PATH = path.join(REPO_ROOT, "scripts", "run-managed-cspell.js");

function readPackageJson() {
  return JSON.parse(fs.readFileSync(PACKAGE_JSON_PATH, "utf8"));
}

function readPreCommitConfig() {
  return fs.readFileSync(PRE_COMMIT_CONFIG_PATH, "utf8");
}

function normalizeVersion(version) {
  if (typeof version !== "string") return null;
  const trimmed = version.trim().replace(/^[~^]/, "");
  if (!/^\d+\.\d+\.\d+(?:[-+].+)?$/.test(trimmed)) return null;
  return trimmed;
}

function findHookBlockText(content, hookId) {
  // Naive but sufficient: locate "- id: <hookId>" and capture lines until
  // the next "- id:" or the end of file. Whitespace-tolerant.
  const lines = content.split(/\r\n|\r|\n/);
  const startIndex = lines.findIndex((line) =>
    new RegExp(`^\\s*-\\s+id:\\s*${hookId}\\s*$`).test(line)
  );
  if (startIndex < 0) return null;
  const out = [lines[startIndex]];
  for (let i = startIndex + 1; i < lines.length; i++) {
    if (/^\s*-\s+id:\s*\S+\s*$/.test(lines[i])) break;
    out.push(lines[i]);
  }
  return out.join("\n");
}

function findPinnedVersionInEntry(blockText, packageName) {
  if (!blockText) return null;
  const re = new RegExp(
    `${packageName.replace(/[.+*?^${}()|[\]\\]/g, "\\$&")}@([0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][\\w.]+)?)`
  );
  const match = blockText.match(re);
  return match ? match[1] : null;
}

function findPinnedVersionsInText(text, packageName) {
  if (typeof text !== "string") return [];
  const re = new RegExp(
    `${packageName.replace(/[.+*?^${}()|[\]\\]/g, "\\$&")}@([0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][\\w.]+)?)`,
    "g"
  );
  return Array.from(text.matchAll(re), (match) => match[1]);
}

function readWorkflowFiles() {
  return fs
    .readdirSync(WORKFLOWS_DIR)
    .filter((fileName) => fileName.endsWith(".yml") || fileName.endsWith(".yaml"))
    .map((fileName) => ({
      fileName,
      content: fs.readFileSync(path.join(WORKFLOWS_DIR, fileName), "utf8")
    }));
}

describe("cspell version parity", () => {
  const pkg = readPackageJson();
  const config = readPreCommitConfig();
  const block = findHookBlockText(config, "cspell");
  const declaredVersion = normalizeVersion(pkg.devDependencies?.cspell);
  const entryVersion = findPinnedVersionInEntry(block, "cspell");
  const cspellPins = [
    {
      name: ".pre-commit-config.yaml cspell fallback",
      version: entryVersion
    }
  ];

  test("package.json devDependencies.cspell is a concrete version", () => {
    expect(declaredVersion).not.toBeNull();
    expect(declaredVersion).toMatch(/^\d+\.\d+\.\d+/);
  });

  test("cspell hook block exists in .pre-commit-config.yaml", () => {
    expect(block).not.toBeNull();
  });

  test.each(cspellPins)(
    "$name pins the same version as package.json devDependencies",
    ({ version }) => {
      // If an entry pattern changes (e.g. different fallback flow), either
      // preserve the @<version> token or update this test.
      expect(version).not.toBeNull();
      expect(version).toBe(declaredVersion);
    }
  );

  test("package cspell scripts use the managed runner with the repository-pinned fallback", () => {
    expect(pkg.scripts?.["check:cspell:all"]).toContain("node scripts/run-managed-cspell.js");
    expect(pkg.scripts?.["check:cspell:scripts"]).toContain("node scripts/run-managed-cspell.js");
    expect(pkg.scripts?.["check:workflow-cspell"]).toContain("node scripts/run-managed-cspell.js");

    const managedCspell = fs.readFileSync(MANAGED_CSPELL_PATH, "utf8");
    expect(managedCspell).toContain(`FALLBACK_CSPELL_SPEC = "cspell@${declaredVersion}"`);
  });

  test("check:cspell:all covers the same extensions as the pre-push cspell hook", () => {
    expect(getPackageCspellAllExtensions(pkg.scripts?.["check:cspell:all"])).toEqual(
      getCspellHookExtensions(block)
    );
  });

  test("workflow npx fallback pins match package.json devDependency version", () => {
    const workflowPins = readWorkflowFiles().flatMap((workflowFile) =>
      findPinnedVersionsInText(workflowFile.content, "cspell").map((version) => ({
        workflowFile: workflowFile.fileName,
        version
      }))
    );

    expect(workflowPins.length).toBeGreaterThan(0);
    for (const pin of workflowPins) {
      expect(pin.version).toBe(declaredVersion);
    }
  });

  test("spellcheck workflow uses the repository-pinned cspell CLI", () => {
    const spellcheckWorkflow = fs.readFileSync(path.join(WORKFLOWS_DIR, "spellcheck.yml"), "utf8");

    expect(spellcheckWorkflow).toContain(`cspell@${declaredVersion}`);
    expect(spellcheckWorkflow).not.toContain("streetsidesoftware/cspell-action");
  });
});

describe("cspell-lib version parity (edit-time checker tracks the cspell CLI)", () => {
  // scripts/hooks/post-edit-validate-guard.js dynamic-requires the cspell-lib
  // engine by a HARD path (node_modules/cspell-lib/dist/index.js) for in-process
  // edit-time spellchecking, while CI runs the `cspell` CLI. The cspell monorepo
  // publishes cspell and cspell-lib in EXACT lockstep, so cspell-lib is declared
  // pinned to the same version. Without this guard, a future one-sided bump of
  // `cspell` would leave a stale `cspell-lib` pin: npm then installs cspell-lib
  // TWICE (a top-level copy for the hook, a nested copy for the CLI) and the
  // edit-time engine silently diverges from CI -- the same resolve-by-luck skew
  // this PR exists to kill. These assertions pin the lockstep.
  const pkg = readPackageJson();
  const declaredCspell = normalizeVersion(pkg.devDependencies?.cspell);
  const declaredCspellLib = normalizeVersion(pkg.devDependencies?.["cspell-lib"]);

  test("package.json pins cspell-lib to a concrete version", () => {
    expect(declaredCspellLib).not.toBeNull();
    expect(declaredCspellLib).toMatch(/^\d+\.\d+\.\d+/);
  });

  test("cspell-lib is pinned to the exact same version as cspell", () => {
    expect(pkg.devDependencies?.["cspell-lib"]).toBe(pkg.devDependencies?.cspell);
  });

  test("the installed (top-level) cspell-lib matches the declared cspell version", () => {
    // Mirrors the markdownlint-cli2 installed-version check below. cspell-lib's
    // package `exports` map blocks `require("cspell-lib/package.json")`, so read
    // the manifest directly off disk. This is the copy post-edit-validate-guard.js
    // loads, so it must equal the CLI version a one-sided bump would skew it from.
    const installedPkgPath = path.resolve(REPO_ROOT, "node_modules", "cspell-lib", "package.json");
    expect(fs.existsSync(installedPkgPath)).toBe(true);
    const installedVersion = normalizeVersion(
      JSON.parse(fs.readFileSync(installedPkgPath, "utf8")).version
    );
    expect(installedVersion).toBe(declaredCspell);
  });
});

describe("markdownlint-cli2 version parity", () => {
  const pkg = readPackageJson();
  const declaredVersion = normalizeVersion(pkg.devDependencies?.["markdownlint-cli2"]);

  test("package.json devDependencies.markdownlint-cli2 is a concrete version", () => {
    expect(declaredVersion).not.toBeNull();
    expect(declaredVersion).toMatch(/^\d+\.\d+\.\d+/);
  });

  test("the installed markdownlint-cli2 module matches the package.json devDependency version", () => {
    // Round-4: the standalone `markdownlint` hook was folded into
    // `run-staged-md-pipeline.js`, which dynamically imports
    // node_modules/markdownlint-cli2/markdownlint-cli2.mjs. There is no
    // longer a pinned `markdownlint-cli2@<v>` literal in the YAML to
    // diff against, so the parity check now reads the installed
    // module's package.json instead. If `npm install` lands a different
    // version than declared, this test fails the same way the YAML
    // literal check used to.
    const installedPkgPath = path.resolve(
      REPO_ROOT,
      "node_modules",
      "markdownlint-cli2",
      "package.json"
    );
    expect(fs.existsSync(installedPkgPath)).toBe(true);
    const installedPkg = JSON.parse(fs.readFileSync(installedPkgPath, "utf8"));
    const installedVersion = normalizeVersion(installedPkg.version);
    expect(installedVersion).toBe(declaredVersion);
  });

  test("workflow npx fallback pins match package.json devDependency version", () => {
    const workflowPins = readWorkflowFiles().flatMap((workflowFile) =>
      findPinnedVersionsInText(workflowFile.content, "markdownlint-cli2").map((version) => ({
        workflowFile: workflowFile.fileName,
        version
      }))
    );

    expect(workflowPins.length).toBeGreaterThan(0);
    for (const pin of workflowPins) {
      expect(pin.version).toBe(declaredVersion);
    }
  });
});

describe("cspell configuration exclusions", () => {
  test("ignores Vale reject vocabularies containing intentional misspellings", () => {
    const cspellConfig = JSON.parse(fs.readFileSync(CSPELL_CONFIG_PATH, "utf8"));

    expect(cspellConfig.ignorePaths).toContain(".vale/styles/Vocab/**/reject.txt");
  });

  test("ignores generated dependency lockfiles", () => {
    const cspellConfig = JSON.parse(fs.readFileSync(CSPELL_CONFIG_PATH, "utf8"));

    expect(cspellConfig.ignorePaths).toContain("package-lock.json");
    expect(cspellConfig.ignorePaths).toContain("**/packages-lock.json");
  });
});
