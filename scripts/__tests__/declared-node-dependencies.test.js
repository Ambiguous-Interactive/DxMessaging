/**
 * @fileoverview Class-prevention guard: every third-party npm package that a
 * `scripts/**` file imports MUST be a DECLARED dependency in package.json.
 *
 * THE CLASS THIS GUARDS. `scripts/validate-workflows.js` (and 13 test files)
 * imported the `yaml` package, but `yaml` was never declared in package.json --
 * it only resolved because cspell/markdownlint/prettier dragged it into the
 * hoisted node_modules transitively. That is fragile three ways:
 *   1. A direct dependent bumps and stops pulling it (or pulls a different
 *      major) -> the import silently breaks or changes behavior. `js-yaml` was
 *      worse: jest brought v3.14.2 and markdownlint brought v4.1.1, so which
 *      API `require("js-yaml")` resolved to was pure hoisting luck.
 *   2. A consumer that does not install the full dev tree (or installs with a
 *      different resolver) cannot resolve the package at all.
 *   3. `npm prune` / `--omit` can legally remove an undeclared transitive.
 *
 * THE RULE. For every `.js`/`.cjs`/`.mjs` under `scripts/`, every bare module
 * specifier it imports (via require/import, excluding Node builtins and
 * relative paths, and excluding specifiers that appear only inside
 * strings/comments/regex) must be listed in package.json
 * dependencies/devDependencies/optionalDependencies/peerDependencies.
 *
 * This is the industry `import/no-extraneous-dependencies` /
 * `depcheck`-missing rule, scoped to this repo's scripts and enforced in CI so
 * the fix cannot rot. The fix for a violation is to DECLARE the package
 * (pinned), never to delete this guard.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { extractModuleSpecifiers, classifySpecifier } = require("../lib/node-require-scan");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPTS_DIR = path.join(REPO_ROOT, "scripts");

const pkg = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, "package.json"), "utf8"));
const DECLARED = new Set([
  ...Object.keys(pkg.dependencies || {}),
  ...Object.keys(pkg.devDependencies || {}),
  ...Object.keys(pkg.optionalDependencies || {}),
  ...Object.keys(pkg.peerDependencies || {})
]);

/** Recursively collect every JS module file under a directory. */
function collectJsFiles(dir) {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === "node_modules") {
        continue;
      }
      out.push(...collectJsFiles(full));
    } else if (/\.(js|cjs|mjs)$/.test(entry.name)) {
      out.push(full);
    }
  }
  return out;
}

const JS_FILES = collectJsFiles(SCRIPTS_DIR).sort();

describe("every third-party import under scripts/ is a declared dependency", () => {
  test("there are script files to scan (guard is not vacuous)", () => {
    expect(JS_FILES.length).toBeGreaterThan(50);
  });

  test.each(JS_FILES.map((f) => [path.relative(REPO_ROOT, f), f]))(
    "%s imports only declared / builtin / local modules",
    (_rel, file) => {
      const src = fs.readFileSync(file, "utf8");
      const undeclared = [];
      for (const spec of extractModuleSpecifiers(src)) {
        const classified = classifySpecifier(spec);
        if (classified.kind === "thirdparty" && !DECLARED.has(classified.packageName)) {
          undeclared.push(classified.packageName);
        }
      }
      expect({ file: path.relative(REPO_ROOT, file), undeclared }).toEqual({
        file: path.relative(REPO_ROOT, file),
        undeclared: []
      });
    }
  );
});

describe("a single canonical YAML parser (yaml), never js-yaml", () => {
  // The repo standardized on the `yaml` package (YAML 1.2). `js-yaml` (YAML 1.1)
  // was removed because the two diverge (the on:/yes/no keys, timestamps coerced
  // to Date, merge keys, empty-doc null-vs-undefined) AND it resolved ambiguously
  // (jest pulled v3, markdownlint pulled v4). The generic declared-deps guard
  // above would let js-yaml back in if a contributor DECLARED it; this pins the
  // architectural decision -- it must stay undeclared AND unimported.
  test("js-yaml is not a declared dependency", () => {
    expect(DECLARED.has("js-yaml")).toBe(false);
  });

  test("no scripts/ file imports js-yaml", () => {
    const importers = JS_FILES.filter((file) =>
      extractModuleSpecifiers(fs.readFileSync(file, "utf8")).some(
        (spec) => spec === "js-yaml" || spec.startsWith("js-yaml/")
      )
    ).map((f) => path.relative(REPO_ROOT, f));
    expect(importers).toEqual([]);
  });
});
