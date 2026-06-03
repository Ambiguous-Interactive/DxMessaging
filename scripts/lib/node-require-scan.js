"use strict";

/**
 * node-require-scan.js
 *
 * Pure static analysis of the module specifiers a repo script imports, used by
 * the dependency-hygiene policy guards:
 *
 *   1. declared-node-dependencies.test.js -- every third-party package a
 *      `scripts/**` file imports must be a DECLARED dependency in package.json,
 *      never an undeclared transitive that happens to be hoisted into
 *      node_modules (the fragility that let `yaml` / `js-yaml` resolve by luck).
 *   2. workflow-node-install-policy.test.js -- any CI job that runs a repo node
 *      script whose transitive (local-require) closure pulls in a third-party
 *      package MUST install node_modules first (the regression that broke the
 *      Actionlint job: it ran validate-workflows.js, which needs `yaml`, with no
 *      install step, so `require("yaml")` threw MODULE_NOT_FOUND).
 *
 * The scan is deliberately CONSERVATIVE about false positives: specifiers that
 * appear inside string literals, template literals, regex literals, or comments
 * are NOT counted. That matters because node-modules-integrity.js intentionally
 * builds `require("unrs-resolver")` / `require("jest-resolve")` as the SOURCE of
 * a probe it spawns in a child process; those are strings, not real imports of
 * this module, and must not be mistaken for undeclared dependencies.
 *
 * Comment/string/regex masking is delegated to the shared, battle-tested
 * single-pass tokenizer in ./source-stripping.js (maskCommentsAndStrings) -- a
 * length- and offset-preserving mask that blanks comment/string/regex payloads
 * to spaces while keeping real code (including `${...}` template expressions)
 * verbatim. We locate real import sites on the masked copy, then read the
 * specifier text back from the ORIGINAL source at the aligned offset.
 *
 * No side effects at module load. `collectTransitiveThirdParty` reads files
 * from disk (the only I/O); everything else is pure.
 */

const fs = require("fs");
const path = require("path");
const { builtinModules } = require("module");
const { maskCommentsAndStrings } = require("./source-stripping");

// Node builtins, both bare (`fs`) and `node:`-prefixed (`node:fs`). Any
// `node:`-prefixed specifier is a builtin by definition, handled in classify().
const BUILTIN_MODULES = new Set([
  ...builtinModules,
  ...builtinModules.map((name) => `node:${name}`)
]);

// Import-site patterns, matched against the MASKED source so a keyword quoted
// inside a string/comment/regex is never treated as a real import. A quoted
// specifier body is all blanked-to-spaces on the mask, so these only locate the
// call SHAPE; the specifier text is read back from the original source.
//
// `(?<![.\w$])` rejects property/method calls like `loader.require("x")` and
// `obj.import("x")` (a `\b` boundary would wrongly match right after the dot).
// The delimiter class includes the backtick so constant template-literal
// specifiers (`require(`pkg`)`) are seen; interpolated ones are filtered later.
const QUOTE = "[\"'`]";
const IMPORT_SITE_PATTERNS = [
  // require("x")
  new RegExp(`(?<![.\\w$])require\\s*\\(\\s*(${QUOTE})(?:(?!\\1).)+\\1\\s*\\)`, "g"),
  // dynamic import("x")
  new RegExp(`(?<![.\\w$])import\\s*\\(\\s*(${QUOTE})(?:(?!\\1).)+\\1\\s*\\)`, "g"),
  // import ... from "x"  /  export ... from "x"
  new RegExp(`(?<![.\\w$])from\\s*(${QUOTE})(?:(?!\\1).)+\\1`, "g"),
  // bare side-effect import "x"
  new RegExp(`(?<![.\\w$])import\\s*(${QUOTE})(?:(?!\\1).)+\\1`, "g")
];
const SPECIFIER_FROM_SITE = new RegExp(`(${QUOTE})((?:(?!\\1).)*)\\1`);

/**
 * Extract the set of module specifiers imported by a JavaScript source string
 * via `require(...)`, static `import`/`export ... from`, and dynamic
 * `import(...)`. Specifiers inside comments/strings/regex are excluded, as are
 * interpolated template-literal specifiers (`import(`${x}`)`), which are
 * genuinely dynamic and not statically resolvable.
 *
 * @param {string} src - JavaScript source text
 * @returns {string[]} Unique specifiers (order not significant; grouped by
 *   import-site pattern, not source position). All consumers fold into a Set.
 */
function extractModuleSpecifiers(src) {
  const masked = maskCommentsAndStrings(src);
  const specifiers = new Set();
  for (const re of IMPORT_SITE_PATTERNS) {
    re.lastIndex = 0;
    let match;
    while ((match = re.exec(masked)) !== null) {
      const original = src.slice(match.index, match.index + match[0].length);
      const inner = SPECIFIER_FROM_SITE.exec(original);
      // Skip interpolated template literals -- a dynamic specifier, not a
      // statically declarable dependency.
      if (inner && inner[2] && !inner[2].includes("${")) {
        specifiers.add(inner[2]);
      }
    }
  }
  return [...specifiers];
}

/**
 * The installable package name for a specifier: the part npm would install.
 * `@scope/name/sub` -> `@scope/name`; `name/sub` -> `name`.
 *
 * @param {string} spec - A bare (non-relative) module specifier
 * @returns {string} The package name
 */
function packageNameOf(spec) {
  if (spec.startsWith("@")) {
    return spec.split("/").slice(0, 2).join("/");
  }
  return spec.split("/")[0];
}

/**
 * Classify a specifier as a Node builtin, a relative/absolute local path, or a
 * third-party package.
 *
 * @param {string} spec - A module specifier
 * @returns {{ kind: "builtin"|"relative"|"thirdparty", packageName: string|null }}
 */
function classifySpecifier(spec) {
  if (spec.startsWith("node:")) {
    return { kind: "builtin", packageName: null };
  }
  if (spec.startsWith(".") || spec.startsWith("/") || /^[a-zA-Z]:[\\/]/.test(spec)) {
    return { kind: "relative", packageName: null };
  }
  if (BUILTIN_MODULES.has(spec) || BUILTIN_MODULES.has(packageNameOf(spec))) {
    return { kind: "builtin", packageName: null };
  }
  return { kind: "thirdparty", packageName: packageNameOf(spec) };
}

/**
 * Resolve a relative specifier to a concrete on-disk `.js`/`.cjs`/`.mjs`/`.json`
 * file, trying the bare path, common extensions, and an `index.*` directory
 * entry. Returns null if nothing resolves (a missing local file is reported by
 * callers, not silently treated as third-party).
 *
 * @param {string} spec - Relative specifier (e.g. "./lib/foo")
 * @param {string} fromFile - Absolute path of the importing file
 * @returns {string|null} Absolute resolved file path, or null
 */
function resolveLocalFile(spec, fromFile) {
  const base = path.resolve(path.dirname(fromFile), spec);
  const candidates = [
    base,
    `${base}.js`,
    `${base}.cjs`,
    `${base}.mjs`,
    `${base}.json`,
    path.join(base, "index.js"),
    path.join(base, "index.cjs"),
    path.join(base, "index.mjs")
  ];
  for (const candidate of candidates) {
    try {
      if (fs.statSync(candidate).isFile()) {
        return candidate;
      }
    } catch {
      // not this candidate
    }
  }
  return null;
}

/**
 * Compute the set of third-party package names reachable from an entry by
 * following its LOCAL (relative) require/import graph transitively.
 *
 * An empty result means the entry statically require-closes to only builtins
 * and local files -- e.g. the dependency-free wiki scripts, and
 * run-managed-prettier.js, which self-bootstraps its own tooling rather than
 * importing it. (This is a STATIC require closure: a script that spawns another
 * process or resolves a module by computed path is out of its scope.)
 *
 * The entry may be a file path OR an in-memory source snippet (e.g. the body of
 * a `node -e "..."` invocation) passed via `opts.sourceText`. In the snippet
 * case, `entryFile` is the notional path the snippet's relative requires resolve
 * against (e.g. `<dir>/__inline__.js`), so the caller controls the resolution
 * base by choosing that path.
 *
 * @param {string} entryFile - Absolute path of the entry script, or the notional
 *   anchor path when scanning an in-memory snippet
 * @param {{ sourceText?: string }} [opts]
 * @returns {{ thirdParty: Set<string>, files: Set<string>, missing: string[] }}
 */
function collectTransitiveThirdParty(entryFile, opts = {}) {
  const thirdParty = new Set();
  const files = new Set();
  const missing = [];

  const startFile = path.resolve(entryFile);
  const queue = [];

  if (typeof opts.sourceText === "string") {
    // Seed the walk from an in-memory snippet; its relative requires resolve
    // against the entry anchor path.
    files.add(startFile);
    for (const spec of extractModuleSpecifiers(opts.sourceText)) {
      const classified = classifySpecifier(spec);
      if (classified.kind === "thirdparty") {
        thirdParty.add(classified.packageName);
      } else if (classified.kind === "relative") {
        const resolved = resolveLocalFile(spec, startFile);
        if (resolved) {
          queue.push(resolved);
        }
      }
    }
  } else {
    queue.push(startFile);
  }

  while (queue.length > 0) {
    const file = queue.pop();
    if (files.has(file)) {
      continue;
    }
    files.add(file);

    let src;
    try {
      src = fs.readFileSync(file, "utf8");
    } catch {
      missing.push(file);
      continue;
    }

    for (const spec of extractModuleSpecifiers(src)) {
      const classified = classifySpecifier(spec);
      if (classified.kind === "thirdparty") {
        thirdParty.add(classified.packageName);
      } else if (classified.kind === "relative") {
        const resolved = resolveLocalFile(spec, file);
        if (resolved) {
          queue.push(resolved);
        }
        // A relative specifier that does not resolve is intentionally ignored
        // here: it is a local-path bug for other tooling to catch, not a
        // third-party dependency.
      }
    }
  }

  return { thirdParty, files, missing };
}

module.exports = {
  BUILTIN_MODULES,
  extractModuleSpecifiers,
  packageNameOf,
  classifySpecifier,
  resolveLocalFile,
  collectTransitiveThirdParty
};
