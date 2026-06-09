#!/usr/bin/env node
"use strict";

/**
 * @fileoverview Bans duplicated local copies of the consolidated shared helpers.
 *
 * Sessions 037-038 of the JS consolidation moved five copy-pasted helpers into
 * shared libraries:
 *
 *   - `readUtf8`, `lineNumberAt`, `toRepoRelative`  -> `scripts/lib/repo-files.js`
 *   - `parseArgs`                                   -> `scripts/lib/cli-options.js`
 *   - `walk` (as `walkFiles`)                       -> `scripts/lib/repo-files.js`
 *
 * To keep that consolidation from eroding, this gate fails when a `scripts/**`
 * file defines a local function/const named one of those helpers OUTSIDE its
 * shared home, unless the file is on the ALLOWLIST below. Each allowlist entry
 * records WHY the local copy is legitimately bespoke (a behavior the shared API
 * cannot reproduce, a trivial alias not worth routing through the shared call,
 * or a test fixture). A stale allowlist entry -- one whose file no longer
 * defines the named helper -- also fails the gate, so the allowlist cannot rot.
 *
 * Detection is name-based on definition syntax only (`function NAME(`,
 * `function* NAME(`, and `const|let|var NAME =`), after stripping comments and
 * string literals via `source-stripping` so a helper name inside a fixture
 * string or prose is never counted. Destructured imports (`const { readUtf8 } =
 * require(...)`) are NOT matched (the name does not directly follow `const`), so
 * a file that USES a shared helper is never flagged; only a local re-DEFINITION
 * is.
 *
 * This is the "ban duplicated local helpers" half of the JS size/policy budget
 * gate. The total/per-file LOC budget half is deferred until the large
 * `validate-workflows.js` refactor lands and a stable cleaned baseline exists.
 */

const path = require("path");

const { REPO_ROOT, listTrackedFiles, readUtf8, lineNumberAt } = require("./lib/repo-files");
const { stripJsCommentsAndStrings } = require("./lib/source-stripping");

/**
 * Helper names whose local duplication is banned outside their shared home.
 * @type {string[]}
 */
const BANNED_HELPERS = ["readUtf8", "lineNumberAt", "toRepoRelative", "parseArgs", "walk"];

/**
 * The shared libraries that DEFINE these helpers and are therefore exempt.
 * @type {Set<string>}
 */
const SHARED_HOMES = new Set(["scripts/lib/repo-files.js", "scripts/lib/cli-options.js"]);

/**
 * Files permitted to keep a local definition of a banned helper, with the
 * reason. Key: repo-relative POSIX path. Value: the banned helper names that
 * file is allowed to define, plus a human reason. Every (file, helper) found by
 * the scan must be a SHARED_HOME or listed here; every entry here must still
 * correspond to a real local definition (otherwise it is stale and fails).
 *
 * @type {Record<string, { helpers: string[], reason: string }>}
 */
const ALLOWLIST = {
  // --- readUtf8: trivial raw `fs.readFileSync(p, "utf8")` aliases. They carry
  // no divergent EOL/BOM logic (the hazard the shared readUtf8 consolidates), so
  // routing their many call sites through `readUtf8(p, {normalizeEol:false,
  // stripBom:false})` would add verbosity for zero behavior change. Inline them
  // (do not allowlist) only if a future cleanup prefers a carve-out-free gate.
  "scripts/__tests__/analyzer-payload-reproducibility.test.js": {
    helpers: ["readUtf8"],
    reason: "trivial raw fs.readFileSync alias; no EOL/BOM logic to consolidate"
  },
  "scripts/__tests__/unity-native-startup-probe-isolation.test.js": {
    helpers: ["readUtf8"],
    reason: "trivial raw fs.readFileSync alias; no EOL/BOM logic to consolidate"
  },
  "scripts/__tests__/unity-runner-host-prereq-contract.test.js": {
    helpers: ["readUtf8"],
    reason: "trivial raw fs.readFileSync alias; no EOL/BOM logic to consolidate"
  },
  "scripts/__tests__/unity-runner-host-prereq-workflow-contract.test.js": {
    helpers: ["readUtf8"],
    reason: "trivial raw fs.readFileSync alias; no EOL/BOM logic to consolidate"
  },
  "scripts/__tests__/unity-runner-maintenance-contract.test.js": {
    helpers: ["readUtf8"],
    reason: "trivial raw fs.readFileSync alias; no EOL/BOM logic to consolidate"
  },

  // --- readUtf8 (raw alias) + toRepoRelative (raw native-separator key). The
  // toRepoRelative here returns `path.relative(REPO_ROOT, abs)` WITHOUT POSIX
  // conversion and is used in Set membership against native `path.join` keys;
  // routing it through the shared (POSIX) toRepoRelative would break the lookup
  // on Windows.
  "scripts/__tests__/no-testrunner-injection-policy.test.js": {
    helpers: ["readUtf8", "toRepoRelative"],
    reason: "raw fs.readFileSync alias; toRepoRelative is a native-separator Set key"
  },

  // --- toRepoRelative: a test-fixture mock injected INTO the formatter under
  // test, not a repository helper.
  "scripts/__tests__/staged-doc-formatters.test.js": {
    helpers: ["toRepoRelative"],
    reason: "test-fixture mock passed into the code under test"
  },

  // --- parseArgs: thin wrappers that DELEGATE to cli-options.parseArgs. The
  // local function name is kept only for the module's export/return shape; the
  // body calls the shared parser. These are the faithful migration pattern, not
  // a reimplementation.
  "scripts/fix-csharp-underscore-methods.js": {
    helpers: ["parseArgs"],
    reason: "thin wrapper delegating to cli-options.parseArgs"
  },
  "scripts/validate-no-plan-vocabulary.js": {
    helpers: ["parseArgs"],
    reason: "thin wrapper delegating to cli-options.parseArgs"
  },
  "scripts/validate-runtime-settings-docs.js": {
    helpers: ["parseArgs"],
    reason: "thin wrapper delegating to cli-options.parseArgs"
  },
  "scripts/fix-yaml-block-scalar-line-length.js": {
    helpers: ["parseArgs"],
    reason: "thin wrapper delegating to cli-options.parseArgs"
  },
  "scripts/fix-yaml-comments-line-length.js": {
    helpers: ["parseArgs"],
    reason: "thin wrapper delegating to cli-options.parseArgs"
  },
  "scripts/validate-docs-out-of-tree-links.js": {
    helpers: ["parseArgs"],
    reason: "thin wrapper delegating to cli-options.parseArgs"
  },

  // --- parseArgs: genuinely bespoke parsers whose semantics the declarative
  // shared parser cannot reproduce byte-exact (greedy/option-like-value
  // consumption, in-parse process.exit, custom throw messages, positional
  // transforms, or mutual-exclusion checks). Migrate only behind a faithfulness
  // proof; until then they stay bespoke.
  "scripts/preflight.js": {
    helpers: ["parseArgs"],
    reason:
      "consumes the next token unconditionally (no option-like guard); ignores unknown flags; --files comma-splits and accumulates"
  },
  "scripts/analyzers/verify-analyzer-payload.js": {
    helpers: ["parseArgs"],
    reason: "mutual-exclusion parser"
  },
  "scripts/measure-hook-wallclock.js": {
    helpers: ["parseArgs"],
    reason: "performs in-parse process.exit on --help"
  },
  "scripts/unity/render-perf-deltas.js": {
    helpers: ["parseArgs"],
    reason: "custom parseTolerance/parseThreshold throw messages; '--'-only value guard"
  },
  "scripts/unity/render-perf-doc.js": {
    helpers: ["parseArgs"],
    reason: "custom parseTolerance throw messages; '--'-only value guard"
  },
  "scripts/unity/extract-perf-baseline.js": {
    helpers: ["parseArgs"],
    reason: "mutual-exclusion (--append/--replace) + custom requireValue guard"
  },
  "scripts/validate-changelog.js": {
    helpers: ["parseArgs"],
    reason: "accepts option-like values (!value guard); custom 'requires a path value' message"
  },
  "scripts/validate-docs-ascii.js": {
    helpers: ["parseArgs"],
    reason: "single-token greedy --paths consume + in-parse process.exit"
  },
  "scripts/validate-doc-code-patterns.js": {
    helpers: ["parseArgs"],
    reason: "single-token greedy --paths consume + in-parse process.exit"
  },
  "scripts/validate-docs-prose.js": {
    helpers: ["parseArgs"],
    reason: "single-token greedy --paths/--rule/--baseline consume + in-parse process.exit"
  },
  "scripts/normalize-docs-ascii.js": {
    helpers: ["parseArgs"],
    reason: "single-token greedy --paths consume + in-parse process.exit"
  },
  "scripts/validate-untracked-policy.js": {
    helpers: ["parseArgs"],
    reason: "non-throwing errors array; --allow= empty-value skip asymmetry"
  },
  "scripts/fix-pwsh-output-assertions.js": {
    helpers: ["parseArgs"],
    reason: "passthrough (--) + early-return help + path-resolve positional transform"
  },

  // --- walk: bespoke recursive directory walks whose behavior walkFiles does
  // not (yet) reproduce: excluded-directory-segment matching, depth limits, or
  // repo-relative POSIX traversal. These are candidates for a later walkFiles
  // migration, tracked separately.
  "scripts/check-eol.js": {
    helpers: ["walk"],
    reason: "excluded-directory-segment walk (case-sensitive Temp etc.)"
  },
  "scripts/fix-eol.js": {
    helpers: ["walk"],
    reason: "excluded-directory-segment walk"
  },
  "scripts/lib/node-modules-integrity.js": {
    helpers: ["walk"],
    reason: "depth-limited walk"
  },
  "scripts/validate-comparison-packages.js": {
    helpers: ["walk"],
    reason: "repo-relative POSIX recursive walk"
  },
  "scripts/__tests__/runtime-test-editor-guard-policy.test.js": {
    helpers: ["walk"],
    reason: "bespoke filtered walk"
  }
};

/**
 * Build the regex that finds a local definition of one of `names`. Matches
 * `function NAME(`, `function* NAME(` (generator), and `const|let|var NAME =`
 * (the conventional definition forms); deliberately does NOT match destructured
 * imports (`const { NAME }`) or call sites.
 *
 * @param {string[]} names
 * @returns {RegExp}
 */
function buildDefinitionRegex(names) {
  const alternation = names.join("|");
  // After `function`, require a real separator: whitespace, or a generator `*`
  // (with optional surrounding space). This matches `function NAME(`,
  // `function* NAME(`, and `function*NAME(` but never the glued `functionNAME(`
  // (which is a single identifier, not a definition).
  return new RegExp(
    `(?:^|[^.\\w$])(?:function(?:\\s+|\\s*\\*\\s*)(${alternation})\\s*\\(|(?:const|let|var)\\s+(${alternation})\\s*=)`,
    "g"
  );
}

/**
 * Scan `scripts/**` for local definitions of the banned helpers.
 *
 * @param {{ repoRoot?: string }} [options]
 * @returns {Array<{ file: string, helper: string, line: number }>}
 */
function findLocalHelperDefinitions(options = {}) {
  const { repoRoot = REPO_ROOT } = options;
  const files = listTrackedFiles(["scripts"], { repoRoot }).filter((file) => file.endsWith(".js"));
  const regex = buildDefinitionRegex(BANNED_HELPERS);

  /** @type {Array<{ file: string, helper: string, line: number }>} */
  const found = [];
  for (const file of files) {
    if (SHARED_HOMES.has(file)) {
      continue;
    }
    const absolute = path.join(repoRoot, file);
    const stripped = stripJsCommentsAndStrings(readUtf8(absolute));
    regex.lastIndex = 0;
    let match;
    while ((match = regex.exec(stripped)) !== null) {
      const helper = match[1] || match[2];
      found.push({ file, helper, line: lineNumberAt(stripped, match.index) });
    }
  }
  return found;
}

/**
 * Pure classification of scan findings against an allowlist. Separated from the
 * filesystem scan so the violation- and stale-detection logic is unit-testable
 * with synthetic inputs.
 *
 * @param {Array<{ file: string, helper: string, line: number }>} found
 * @param {Record<string, { helpers: string[], reason: string }>} allowlist
 * @returns {{ violations: string[], staleAllowlist: string[] }}
 *   `violations`: undocumented local helper definitions. `staleAllowlist`:
 *   allowlist (file, helper) pairs with no matching local definition.
 */
function classifyFindings(found, allowlist) {
  const violations = [];
  /** @type {Set<string>} */
  const matchedAllowlistPairs = new Set();

  for (const { file, helper, line } of found) {
    const entry = allowlist[file];
    if (entry && entry.helpers.includes(helper)) {
      matchedAllowlistPairs.add(`${file}::${helper}`);
      continue;
    }
    violations.push(
      `${file}:${line}: local "${helper}" duplicates the shared helper. ` +
        `Use the shared version (repo-files.js / cli-options.js), or add a documented ALLOWLIST entry.`
    );
  }

  const staleAllowlist = [];
  for (const [file, entry] of Object.entries(allowlist)) {
    for (const helper of entry.helpers) {
      if (!matchedAllowlistPairs.has(`${file}::${helper}`)) {
        staleAllowlist.push(
          `${file}: allowlisted "${helper}" no longer found (stale entry -- remove it from ALLOWLIST).`
        );
      }
    }
  }

  return { violations, staleAllowlist };
}

/**
 * Scan the repository and classify against the live ALLOWLIST.
 *
 * @param {{ repoRoot?: string }} [options]
 * @returns {{ violations: string[], staleAllowlist: string[] }}
 */
function evaluate(options = {}) {
  return classifyFindings(findLocalHelperDefinitions(options), ALLOWLIST);
}

function main() {
  const { violations, staleAllowlist } = evaluate();

  if (violations.length === 0 && staleAllowlist.length === 0) {
    const allowlistPairs = Object.values(ALLOWLIST).reduce(
      (total, entry) => total + entry.helpers.length,
      0
    );
    console.log(
      `validate:shared-helpers OK -- no undocumented local copies of ` +
        `${BANNED_HELPERS.join(", ")} (${allowlistPairs} allowlisted).`
    );
    return 0;
  }

  if (violations.length > 0) {
    console.error("Duplicated shared helpers (use the shared library or allowlist):");
    for (const message of violations) {
      console.error(`  ${message}`);
    }
  }
  if (staleAllowlist.length > 0) {
    console.error("Stale ALLOWLIST entries in scripts/validate-shared-helper-usage.js:");
    for (const message of staleAllowlist) {
      console.error(`  ${message}`);
    }
  }
  return 1;
}

if (require.main === module) {
  process.exitCode = main();
}

module.exports = {
  BANNED_HELPERS,
  SHARED_HOMES,
  ALLOWLIST,
  buildDefinitionRegex,
  findLocalHelperDefinitions,
  classifyFindings,
  evaluate,
  main
};
