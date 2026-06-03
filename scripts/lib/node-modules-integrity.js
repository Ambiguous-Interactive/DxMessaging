"use strict";

/**
 * node-modules-integrity.js
 *
 * Single source of truth for the "are the on-disk node_modules files
 * actually present and non-zero?" health check. Every managed wrapper
 * (run-managed-jest, run-managed-prettier, run-managed-cspell) and the
 * doctor + validate-node-tooling validators import INTEGRITY_TARGETS from
 * here. Duplication elsewhere is a policy violation.
 *
 * Three layers of probe:
 *
 *   1. `probeIntegrity({ repoRoot })` - in-process synchronous check.
 *      Returns `{ ok, missing: [{ tool, relPath, reason }] }`. Reason is
 *      `"missing"` (existsSync false) or `"empty"` (size 0). Pure I/O, no
 *      side effects; the only writes happen later via the integrity-gate
 *      caller (npm ci).
 *
 *   2. `probeIntegrityInSubprocess({ repoRoot, execPath, spawnSyncFn })` -
 *      Re-runs probeIntegrity in a fresh Node process. Defeats Node's
 *      module / fs cache, which is critical after npm ci has rewritten
 *      node_modules: the parent process may still have stale require/stat
 *      cache entries for the previously-broken file. Parent and child
 *      MUST be in lockstep on what counts as "healthy"; the child inlines
 *      probeIntegrity via require() against this same file.
 *
 *   3. `findZeroByteNativeBinaries({ repoRoot })` - Windows-only quick
 *      scan for *.node files with size 0 under node_modules. On
 *      non-Windows, returns [] without walking. Bounded depth keeps the
 *      probe under ~50ms even on large trees.
 */

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

/**
 * The canonical list of managed tools whose absence breaks the pre-push
 * hooks. Every entry's `files` array lists the critical files that MUST
 * exist on disk with non-zero size for the tool to actually run.
 *
 * Verification rules (when extending this list):
 *   - Every relPath MUST exist in a healthy `npm install` of this
 *     repository. Add a list only after confirming `ls -la
 *     node_modules/<tool>/<relPath>` on a fresh `npm ci`.
 *   - Prefer the load-bearing entrypoint (e.g. bin script, runner module)
 *     over README or package.json. The gate's purpose is to predict
 *     whether the tool's invocation will succeed, not whether the package
 *     metadata is complete.
 *
 * The list is frozen at module load; consumers cannot mutate it (and the
 * tests assert this).
 */
const INTEGRITY_TARGETS = Object.freeze([
  Object.freeze({
    tool: "prettier",
    files: Object.freeze([
      Object.freeze({ relPath: "node_modules/prettier/index.cjs", minBytes: 1 }),
      Object.freeze({ relPath: "node_modules/prettier/bin/prettier.cjs", minBytes: 1 })
    ])
  }),
  Object.freeze({
    tool: "markdownlint-cli2",
    files: Object.freeze([
      Object.freeze({
        relPath: "node_modules/markdownlint-cli2/markdownlint-cli2.mjs",
        minBytes: 1
      })
    ])
  }),
  Object.freeze({
    tool: "cspell",
    files: Object.freeze([
      Object.freeze({ relPath: "node_modules/cspell/bin.mjs", minBytes: 1 }),
      Object.freeze({ relPath: "node_modules/cspell/dist/esm/app.js", minBytes: 1 }),
      Object.freeze({ relPath: "node_modules/cspell-lib/dist/index.js", minBytes: 1 })
    ])
  }),
  Object.freeze({
    tool: "jest",
    files: Object.freeze([Object.freeze({ relPath: "node_modules/jest/bin/jest.js", minBytes: 1 })])
  }),
  Object.freeze({
    tool: "jest-circus",
    files: Object.freeze([
      Object.freeze({ relPath: "node_modules/jest-circus/build/runner.js", minBytes: 1 }),
      Object.freeze({ relPath: "node_modules/jest-circus/build/index.js", minBytes: 1 }),
      // Verified live on the repo: jestAdapterInit.js lives directly
      // under build/, not under build/legacy-code-todo-rewrite/. The
      // plan called for the legacy path but on-disk inspection at
      // 2026-05-18 shows the flat layout; we follow on-disk truth.
      Object.freeze({
        relPath: "node_modules/jest-circus/build/jestAdapterInit.js",
        minBytes: 1
      })
    ])
  })
]);

function joinRepoPath(repoRoot, relPath) {
  return path.join(repoRoot, ...relPath.split("/"));
}

/**
 * Probe every file in INTEGRITY_TARGETS for presence + non-zero size.
 *
 * @param {object} [options]
 * @param {string} options.repoRoot Absolute path to the repository root.
 * @param {Function} [options.statSyncFn] Override fs.statSync (for tests).
 *   The callback receives the absolute path; it must throw ENOENT to
 *   signal missing, or return `{ size: N }`.
 * @param {Function} [options.existsSyncFn] Override fs.existsSync (for tests).
 * @param {Array} [options.targets] Override INTEGRITY_TARGETS (for tests).
 * @returns {{ok: boolean, missing: Array<{tool: string, relPath: string, reason: string}>}}
 */
function probeIntegrity(options = {}) {
  const {
    repoRoot,
    statSyncFn = fs.statSync,
    existsSyncFn = fs.existsSync,
    targets = INTEGRITY_TARGETS
  } = options;

  if (typeof repoRoot !== "string" || repoRoot.length === 0) {
    throw new TypeError("probeIntegrity requires options.repoRoot (string)");
  }

  const missing = [];

  for (const target of targets) {
    for (const file of target.files) {
      const abs = joinRepoPath(repoRoot, file.relPath);

      // We deliberately call existsSync before stat. On Windows, stat
      // on a phantom drive letter can throw EPERM rather than ENOENT;
      // the existsSync gate normalizes that to a clean "missing"
      // verdict, and a defensive try/catch around stat catches any
      // remaining edge case (e.g. EACCES on a partially-restored
      // file).
      if (!existsSyncFn(abs)) {
        missing.push({ tool: target.tool, relPath: file.relPath, reason: "missing" });
        continue;
      }

      let stats;
      try {
        stats = statSyncFn(abs);
      } catch (error) {
        const code = error && error.code ? error.code : "stat-error";
        missing.push({
          tool: target.tool,
          relPath: file.relPath,
          reason: code === "ENOENT" ? "missing" : "empty"
        });
        continue;
      }

      const minBytes = typeof file.minBytes === "number" && file.minBytes > 0 ? file.minBytes : 1;
      if (!stats || typeof stats.size !== "number" || stats.size < minBytes) {
        missing.push({ tool: target.tool, relPath: file.relPath, reason: "empty" });
      }
    }
  }

  return { ok: missing.length === 0, missing };
}

/**
 * Spawn `node -e '<inline-script>'` to run probeIntegrity in a fresh
 * Node process. The child requires this same module and prints its JSON
 * output to stdout; the parent parses and returns the same shape.
 *
 * Why a subprocess? After npm ci finishes, the parent process still has
 * the original `node_modules/.../runner.js` cached in its require + fs
 * stat caches. A re-probe in the same process can falsely report "still
 * broken". The subprocess starts with a clean module cache.
 *
 * @param {object} [options]
 * @param {string} options.repoRoot Absolute path to the repository root.
 * @param {string} [options.execPath] Node executable to spawn. Defaults
 *   to process.execPath.
 * @param {Function} [options.spawnSyncFn] Override child_process.spawnSync
 *   (for tests).
 * @returns {{ok: boolean, missing: Array<{tool: string, relPath: string, reason: string}>}}
 *   On spawn failure, returns `{ ok: false, missing: [{ tool, relPath, reason }] }`
 *   with a synthetic entry describing the spawn error.
 */
function probeIntegrityInSubprocess(options = {}) {
  const { repoRoot, execPath = process.execPath, spawnSyncFn = childProcess.spawnSync } = options;

  if (typeof repoRoot !== "string" || repoRoot.length === 0) {
    throw new TypeError("probeIntegrityInSubprocess requires options.repoRoot (string)");
  }

  // The inline script is intentionally minimal: it requires this exact
  // module, calls probeIntegrity({ repoRoot }), and prints the JSON
  // result. We JSON.stringify both the module path and repoRoot so
  // backslashes (on Windows) and any embedded quotes are escape-safe.
  //
  // It additionally calls findZeroByteNativeBinaries on Windows so the
  // post-`npm ci` re-probe surfaces the AV-truncation failure mode the
  // first-pass in-process gate already covers. The result is reported
  // under a separate `zeroByteNativeBinaries` field; the parent merges
  // it into missing[] using the same shape as the in-process gate.
  const integrityModulePath = __filename;
  const inlineScript =
    '"use strict";\n' +
    "const integrity = require(" +
    JSON.stringify(integrityModulePath) +
    "); " +
    "const repoRoot = " +
    JSON.stringify(repoRoot) +
    "; " +
    "const result = integrity.probeIntegrity({ repoRoot }); " +
    'const zeroByteNativeBinaries = process.platform === "win32" ' +
    "? integrity.findZeroByteNativeBinaries({ repoRoot }) " +
    ": []; " +
    "process.stdout.write(JSON.stringify(Object.assign({}, result, { zeroByteNativeBinaries })));";

  const spawnResult = spawnSyncFn(execPath, ["-e", inlineScript], {
    cwd: repoRoot,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });

  if (!spawnResult || spawnResult.error) {
    const errorMessage =
      spawnResult && spawnResult.error && spawnResult.error.message
        ? spawnResult.error.message
        : "spawn returned null";
    return {
      ok: false,
      missing: [
        {
          tool: "(subprocess)",
          relPath: "(node -e probeIntegrity)",
          reason: `spawn failed: ${errorMessage}`
        }
      ]
    };
  }

  if (spawnResult.status !== 0) {
    const stderrText = typeof spawnResult.stderr === "string" ? spawnResult.stderr : "";
    return {
      ok: false,
      missing: [
        {
          tool: "(subprocess)",
          relPath: "(node -e probeIntegrity)",
          reason: `exit=${spawnResult.status}; stderr=${stderrText.trim().slice(0, 200)}`
        }
      ]
    };
  }

  const stdoutText = typeof spawnResult.stdout === "string" ? spawnResult.stdout : "";
  try {
    const parsed = JSON.parse(stdoutText);
    if (!parsed || typeof parsed.ok !== "boolean" || !Array.isArray(parsed.missing)) {
      throw new Error("subprocess returned malformed shape");
    }
    // Merge zero-byte native bindings into the missing[] list using the
    // same shape the in-process integrity gate uses, so the downstream
    // banner formatter and recovery flow see one uniform offender list.
    // The `zeroByteNativeBinaries` field is optional for backward
    // compatibility with subprocess scripts that don't emit it.
    const zeroByteNative = Array.isArray(parsed.zeroByteNativeBinaries)
      ? parsed.zeroByteNativeBinaries
      : [];
    if (zeroByteNative.length === 0) {
      return { ok: parsed.ok, missing: parsed.missing };
    }
    const augmentedMissing = parsed.missing.slice();
    for (const relPath of zeroByteNative) {
      augmentedMissing.push({
        tool: "<native-binding>",
        relPath,
        reason: "zero-byte"
      });
    }
    return { ok: false, missing: augmentedMissing };
  } catch (parseError) {
    const detail = parseError && parseError.message ? parseError.message : String(parseError);
    return {
      ok: false,
      missing: [
        {
          tool: "(subprocess)",
          relPath: "(node -e probeIntegrity)",
          reason: `JSON parse failed: ${detail}`
        }
      ]
    };
  }
}

/**
 * Recursively walk node_modules looking for `*.node` files with size 0.
 *
 * Native binaries get truncated by certain AV products on Windows mid-
 * install, leaving the file present but unusable. JS files larger than 0
 * are still loadable; *.node files at size 0 always fail. We can scan
 * once at probe time and surface a single actionable error.
 *
 * Bounded depth (~5) keeps the scan fast even on very large trees. On
 * non-Windows platforms, this function returns [] without walking; the
 * Windows-specific failure mode does not apply.
 *
 * @param {object} [options]
 * @param {string} options.repoRoot Absolute path to the repository root.
 * @param {Function} [options.readdirSyncFn] Override fs.readdirSync (for tests).
 * @param {boolean} [options.skip] If true, returns [] immediately without
 *   walking. Callers use this when they have already confirmed the
 *   platform is non-Windows or the cost is unacceptable.
 * @param {string} [options.platform] Override process.platform (for tests).
 * @returns {string[]} Repo-relative POSIX-style paths of zero-byte *.node files.
 */
function findZeroByteNativeBinaries(options = {}) {
  const {
    repoRoot,
    readdirSyncFn = fs.readdirSync,
    statSyncFn = fs.statSync,
    skip = false,
    platform = process.platform,
    maxDepth = 5
  } = options;

  if (skip || platform !== "win32") {
    return [];
  }
  if (typeof repoRoot !== "string" || repoRoot.length === 0) {
    return [];
  }

  const nodeModulesRoot = path.join(repoRoot, "node_modules");
  const offenders = [];

  function walk(dirAbsPath, depth) {
    if (depth > maxDepth) {
      return;
    }
    let entries;
    try {
      entries = readdirSyncFn(dirAbsPath, { withFileTypes: true });
    } catch {
      return;
    }
    for (const entry of entries) {
      const childAbs = path.join(dirAbsPath, entry.name);
      if (entry.isDirectory()) {
        walk(childAbs, depth + 1);
        continue;
      }
      if (entry.isFile() && entry.name.endsWith(".node")) {
        try {
          const stats = statSyncFn(childAbs);
          if (stats && typeof stats.size === "number" && stats.size === 0) {
            const rel = path.relative(repoRoot, childAbs).split(path.sep).join("/");
            offenders.push(rel);
          }
        } catch {
          // ignore: a file we can't stat is not actionable here.
        }
      }
    }
  }

  walk(nodeModulesRoot, 0);
  return offenders;
}

/**
 * Render a single-line summary of an integrity probe failure.
 *
 * All `relPath` values are emitted with POSIX separators so log output is
 * identical across Windows, macOS, and Linux. Cross-platform string
 * assertions in tests can therefore match a single, platform-agnostic form.
 *
 * @param {{ok: boolean, missing: Array<{tool: string, relPath: string, reason: string}>}} result
 * @returns {string}
 */
function formatIntegrityFailure(result) {
  if (!result || !Array.isArray(result.missing) || result.missing.length === 0) {
    return "Integrity probe failed: no detail available.";
  }
  const first = result.missing[0];
  const remaining = result.missing.length - 1;
  const tail = remaining > 0 ? `; ${remaining} more` : "";
  const relPathPosix =
    typeof first.relPath === "string" ? first.relPath.replace(/\\/g, "/") : first.relPath;
  return `Integrity probe failed: missing ${relPathPosix} (${first.reason}) for ${first.tool}${tail}`;
}

/**
 * Critical resolver entries that MUST resolve via require.resolve from the
 * repository root. The file-based integrity probe checks that these files
 * are present on disk; this list is what the resolver-health probe asks
 * Node's actual resolver to find at runtime.
 *
 * Why a separate list from INTEGRITY_TARGETS:
 *   - INTEGRITY_TARGETS pins exact relative file paths under node_modules.
 *   - The resolver chain depends on additional metadata (package.json
 *     "exports", platform-specific native bindings) that the file probe
 *     cannot evaluate. The Windows failure mode that motivated this probe
 *     is: jest-circus/build/runner.js IS present on disk (file probe OK),
 *     but `require.resolve('jest-circus/runner')` THROWS because the
 *     `@unrs/resolver-binding-win32-x64-msvc` native binding is missing
 *     or broken.
 *   - jest-circus/runner is the canonical specifier because it is the
 *     deepest, most-load-bearing entry; if it resolves cleanly, every
 *     intermediate package resolves too.
 */
const DEFAULT_RESOLVER_SPECIFIERS = Object.freeze(["jest-circus/runner"]);

/**
 * Critical module files that must not only exist, but also parse/load in a
 * fresh Node process. This catches the non-empty-but-corrupt install state that
 * a size-only file probe cannot see.
 */
const DEFAULT_LOADABLE_REL_PATHS = Object.freeze([
  "node_modules/cspell-lib/dist/index.js",
  "node_modules/cspell/dist/esm/app.js"
]);

/**
 * Spawn a fresh Node subprocess that EXERCISES the unrs-resolver native
 * binding the same way jest-resolve will at test-runner startup, then
 * additionally runs Node's own `require.resolve` for each critical specifier.
 * Returns `{ ok, failures: [{ specifier, error }] }`.
 *
 * The probe is layered intentionally so a healthy file tree but a broken
 * native binding still fails the gate:
 *
 *   - LAYER 1: `require("unrs-resolver")` from the repo. On Windows when
 *     `@unrs/resolver-binding-win32-x64-msvc` is missing or partially
 *     extracted, napi-postinstall's loader throws at module load. If
 *     unrs-resolver is unreachable from this repo's tree, the script falls
 *     back to `require("jest-resolve")`, which transitively requires
 *     unrs-resolver and triggers the same failure surface.
 *   - LAYER 2: `new ResolverFactory({}).sync(repoRoot, "jest-circus/runner")`.
 *     Verified against `node_modules/unrs-resolver/index.d.ts`: the exported
 *     class is `ResolverFactory` with a `sync(directory, request)` method.
 *     `jest-resolve/build/index.js` uses the same shape (see lines 114, 172).
 *     A half-loaded native binding sometimes survives `require()` but throws
 *     here when the JS side instantiates the factory or calls into native.
 *   - LAYER 3: existing Node-resolver `require.resolve(spec)` checks. Kept
 *     belt-and-suspenders because they catch a different failure mode
 *     (missing peer dep, broken `exports` map) that the unrs-resolver layers
 *     do not surface as cleanly.
 *
 * Why a subprocess: like {@link probeIntegrityInSubprocess}, a fresh Node
 * process starts with a clean module + native-binding cache. Probing from
 * the parent risks false positives (loaded cached binding) and false
 * negatives (parent already crashed loading the same binding).
 *
 * The subprocess writes a single JSON document to stdout. The parent never
 * runs the user's code via `eval` — it only spawns a strict-mode Node
 * runtime with a hand-rolled inline script whose body is fully under our
 * control. Inputs (`repoRoot`, `specifiers`) flow through `JSON.stringify`
 * so backslashes and embedded quotes are escape-safe on Windows.
 *
 * @param {object} [options]
 * @param {string} options.repoRoot Absolute path to the repository root.
 * @param {string} [options.execPath] Node executable to spawn. Defaults to
 *   process.execPath.
 * @param {Function} [options.spawnSyncFn] Override child_process.spawnSync
 *   (for tests).
 * @param {string[]} [options.specifiers] Resolver specifiers to probe.
 *   Defaults to {@link DEFAULT_RESOLVER_SPECIFIERS}.
 * @param {string[]} [options.loadableRelPaths] Repo-relative module files to
 *   load in the subprocess. Defaults to {@link DEFAULT_LOADABLE_REL_PATHS}.
 * @returns {{ok: boolean, failures: Array<{specifier: string, error: string}>}}
 *   On any subprocess malfunction (non-zero exit, malformed stdout,
 *   missing stdout), returns `{ ok: false, failures: [{ specifier:
 *   "<subprocess>", error: ... }] }` so callers can treat it uniformly with
 *   real resolver failures.
 */
function probeResolverHealth(options = {}) {
  const {
    repoRoot,
    execPath = process.execPath,
    spawnSyncFn = childProcess.spawnSync,
    specifiers = DEFAULT_RESOLVER_SPECIFIERS,
    loadableRelPaths = DEFAULT_LOADABLE_REL_PATHS
  } = options;

  if (typeof repoRoot !== "string" || repoRoot.length === 0) {
    throw new TypeError("probeResolverHealth requires options.repoRoot (string)");
  }

  // Hand-rolled inline script. The parent fully controls every byte; all
  // dynamic values are routed through JSON.stringify so a malicious
  // repoRoot or specifier cannot inject code.
  //
  // Layered probe (see function JSDoc for the rationale):
  //   1. require("unrs-resolver") to trigger napi-postinstall's native
  //      binding load. Falls back to require("jest-resolve") if
  //      unrs-resolver is not directly reachable from the repo tree.
  //   2. Instantiate ResolverFactory and call .sync() to exercise the
  //      native binding's actual entry points (a half-loaded binding can
  //      survive require but throw here).
  //   3. Node's createRequire(...).resolve(spec) as the legacy fallback.
  //
  // The literal "unrs-resolver" token in this script body is asserted by
  // the policy test in node-modules-integrity.test.js so a refactor cannot
  // silently regress this probe to Node-only resolution.
  const inlineScript =
    '"use strict";\n' +
    "const Module = require('module');\n" +
    "const path = require('path');\n" +
    "const repoRoot = " +
    JSON.stringify(repoRoot) +
    ";\n" +
    "const repoRequire = Module.createRequire(path.join(repoRoot, 'package.json'));\n" +
    "const specifiers = " +
    JSON.stringify(specifiers) +
    ";\n" +
    "const loadableRelPaths = " +
    JSON.stringify(loadableRelPaths) +
    ";\n" +
    "const failures = [];\n" +
    "function describeError(e) {\n" +
    "  if (!e) return 'unknown';\n" +
    "  const code = e.code ? e.code + ': ' : '';\n" +
    "  return code + (e.message || String(e));\n" +
    "}\n" +
    "// LAYER 1: force-load unrs-resolver. On Windows with a broken\n" +
    "// @unrs/resolver-binding-* native binding, this throws at module\n" +
    "// load time (napi-postinstall's loader surfaces the underlying\n" +
    "// MODULE_NOT_FOUND or load error here).\n" +
    "//\n" +
    "// If unrs-resolver itself cannot be located (MODULE_NOT_FOUND for the\n" +
    "// JS file, not for the native binding), we additionally try to load\n" +
    "// jest-resolve, which transitively requires unrs-resolver. That path\n" +
    "// catches the failure surface even in a repo whose direct dependency\n" +
    "// tree does not list unrs-resolver. In either case, a failure to load\n" +
    "// IS a probe failure and is always recorded.\n" +
    "let unrsModule = null;\n" +
    "let unrsLoadVia = 'unrs-resolver';\n" +
    "let unrsLoadOk = false;\n" +
    "try {\n" +
    "  unrsModule = repoRequire('unrs-resolver');\n" +
    "  unrsLoadOk = true;\n" +
    "} catch (primaryErr) {\n" +
    "  failures.push({ specifier: 'unrs-resolver', error: describeError(primaryErr) });\n" +
    "  // Probe via jest-resolve as a secondary signal: if it ALSO\n" +
    "  // throws, we learn the failure is reproducible through the\n" +
    "  // jest-resolve load chain too; if it succeeds, the failure is\n" +
    "  // confined to the direct unrs-resolver entrypoint.\n" +
    "  try {\n" +
    "    repoRequire('jest-resolve');\n" +
    "    unrsLoadVia = 'jest-resolve (transitive unrs-resolver)';\n" +
    "  } catch (fallbackErr) {\n" +
    "    failures.push({ specifier: 'jest-resolve', error: describeError(fallbackErr) });\n" +
    "  }\n" +
    "}\n" +
    "// LAYER 2: actually instantiate ResolverFactory and exercise sync().\n" +
    "if (unrsModule && typeof unrsModule.ResolverFactory === 'function') {\n" +
    "  let factory = null;\n" +
    "  try {\n" +
    "    factory = new unrsModule.ResolverFactory({});\n" +
    "  } catch (factoryErr) {\n" +
    "    failures.push({\n" +
    "      specifier: 'unrs-resolver(' + unrsLoadVia + ')',\n" +
    "      error: 'ResolverFactory ctor failed: ' + describeError(factoryErr)\n" +
    "    });\n" +
    "  }\n" +
    "  if (factory) {\n" +
    "    for (const spec of specifiers) {\n" +
    "      try { factory.sync(repoRoot, spec); }\n" +
    "      catch (resolveErr) {\n" +
    "        failures.push({ specifier: 'unrs-resolver:' + spec, error: describeError(resolveErr) });\n" +
    "      }\n" +
    "    }\n" +
    "  }\n" +
    "}\n" +
    "// LAYER 3: legacy Node-resolver probe (different failure modes).\n" +
    "for (const spec of specifiers) {\n" +
    "  try { repoRequire.resolve(spec); }\n" +
    "  catch (e) { failures.push({ specifier: spec, error: describeError(e) }); }\n" +
    "}\n" +
    "// LAYER 4: load critical module files so non-empty corrupt JS is repaired.\n" +
    "for (const relPath of loadableRelPaths) {\n" +
    "  try { repoRequire(path.join(repoRoot, relPath)); }\n" +
    "  catch (e) {\n" +
    "    failures.push({ specifier: relPath, error: 'module-load-throw: ' + describeError(e) });\n" +
    "  }\n" +
    "}\n" +
    "process.stdout.write(JSON.stringify({ ok: failures.length === 0, failures }));\n";

  let spawnResult;
  try {
    spawnResult = spawnSyncFn(execPath, ["-e", inlineScript], {
      cwd: repoRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    });
  } catch (spawnError) {
    const detail = spawnError && spawnError.message ? spawnError.message : String(spawnError);
    return {
      ok: false,
      failures: [{ specifier: "<subprocess>", error: "spawn threw: " + detail }]
    };
  }

  if (!spawnResult) {
    return {
      ok: false,
      failures: [{ specifier: "<subprocess>", error: "spawn returned null" }]
    };
  }
  if (spawnResult.error) {
    const detail = spawnResult.error.message || String(spawnResult.error);
    return {
      ok: false,
      failures: [{ specifier: "<subprocess>", error: "spawn errored: " + detail }]
    };
  }
  if (spawnResult.status !== 0) {
    const stderrText =
      typeof spawnResult.stderr === "string" ? spawnResult.stderr.trim().slice(0, 200) : "";
    return {
      ok: false,
      failures: [
        {
          specifier: "<subprocess>",
          error: "exit=" + spawnResult.status + (stderrText ? "; stderr=" + stderrText : "")
        }
      ]
    };
  }

  const stdoutText = typeof spawnResult.stdout === "string" ? spawnResult.stdout : "";
  if (stdoutText.length === 0) {
    return {
      ok: false,
      failures: [{ specifier: "<subprocess>", error: "empty stdout from probe" }]
    };
  }

  try {
    const parsed = JSON.parse(stdoutText);
    if (!parsed || typeof parsed.ok !== "boolean" || !Array.isArray(parsed.failures)) {
      throw new Error("subprocess returned malformed shape");
    }
    return { ok: parsed.ok, failures: parsed.failures };
  } catch (parseError) {
    const detail = parseError && parseError.message ? parseError.message : String(parseError);
    return {
      ok: false,
      failures: [
        {
          specifier: "<subprocess>",
          error: "malformed probe output: " + detail
        }
      ]
    };
  }
}

module.exports = {
  INTEGRITY_TARGETS,
  DEFAULT_RESOLVER_SPECIFIERS,
  DEFAULT_LOADABLE_REL_PATHS,
  probeIntegrity,
  probeIntegrityInSubprocess,
  findZeroByteNativeBinaries,
  formatIntegrityFailure,
  probeResolverHealth
};
