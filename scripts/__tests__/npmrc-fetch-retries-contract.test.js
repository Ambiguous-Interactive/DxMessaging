/**
 * @fileoverview Contract test for the repo's `.npmrc` fetch-retry tuning.
 *
 * WHY THIS EXISTS: npm's default fetch-retry settings (2 retries, narrow
 * backoff window) let a transient `ECONNRESET` against the registry surface
 * as a hard install failure across 22+ CI workflow call sites plus every
 * devcontainer rebuild. The repo's `.npmrc` raises the retry count and
 * widens the backoff window so a transient network blip is absorbed instead
 * of escalating to a workflow failure. The settings are documented at
 * https://docs.npmjs.com/cli/v11/using-npm/config/#fetch-retries.
 *
 * The .npmrc is the LEVERAGED fix (one file, every npm call site honors it)
 * vs. wrapping each workflow in a retry action. This guard:
 *   * Asserts .npmrc exists at the repo root.
 *   * Parses and asserts lower bounds on the three knobs (so a future
 *     tweak that LOWERS values is caught).
 *   * Asserts there is NO contradicting subordinate .npmrc anywhere under
 *     the repo (a nested .npmrc with weaker values would silently win for
 *     `npm` invocations made inside that subtree).
 *
 * COMPATIBILITY NOTE for the reviewer: the actions/setup-node@v6 cache key
 * hashes `package-lock.json`, NOT `.npmrc`, so adding this `.npmrc` does
 * NOT invalidate any existing cache key or conflict with cache-restore.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const ROOT_NPMRC = path.join(REPO_ROOT, ".npmrc");

// Lower bounds. We deliberately pick numbers BELOW the production values so a
// minor production tweak (e.g. raising fetch-retries from 5 to 6) does not
// break the contract, while still catching a regression that resets the
// defaults or lowers them under what we know works.
const LOWER_BOUND_FETCH_RETRIES = 3;
const LOWER_BOUND_MINTIMEOUT_MS = 15000;
const LOWER_BOUND_MAXTIMEOUT_MS = 60000;

// Directory names we never descend into when scanning for subordinate
// .npmrc files. node_modules carries a sea of vendor .npmrc files that are
// off-policy by definition (we don't ship npm to them); .git is binary noise;
// build outputs (.artifacts, site, coverage) hold transient files only.
const PRUNE_DIR_NAMES = new Set([
  "node_modules",
  ".git",
  ".artifacts",
  "coverage",
  "site",
  ".cache",
  ".unity-test-project",
  "Library",
  "Temp",
  "Logs"
]);

/**
 * Parse an `key=value` style .npmrc into an object. Tolerant of:
 *   - blank lines
 *   - comments (lines starting with `;` or `#`)
 *   - whitespace surrounding `=`
 *
 * npm's own parser is more permissive (nested-`[scope]` sections, etc.) but
 * for the LOWER-BOUND check we only care about three numeric keys and the
 * presence of any value at all.
 */
function parseNpmrc(text) {
  const out = {};
  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line) {
      continue;
    }
    if (line.startsWith(";") || line.startsWith("#")) {
      continue;
    }
    const eq = line.indexOf("=");
    if (eq < 0) {
      continue;
    }
    const key = line.slice(0, eq).trim();
    const value = line.slice(eq + 1).trim();
    out[key] = value;
  }
  return out;
}

/**
 * Walk the repo looking for `.npmrc` files OUTSIDE of the prune list. Returns
 * absolute paths. The root .npmrc is INCLUDED in the result; callers filter
 * it out as needed.
 */
function findAllNpmrc(root) {
  const found = [];
  const stack = [root];
  while (stack.length > 0) {
    const dir = stack.pop();
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const entry of entries) {
      if (entry.isDirectory()) {
        if (PRUNE_DIR_NAMES.has(entry.name)) {
          continue;
        }
        stack.push(path.join(dir, entry.name));
      } else if (entry.isFile() && entry.name === ".npmrc") {
        found.push(path.join(dir, entry.name));
      }
    }
  }
  return found;
}

describe(".npmrc fetch-retry contract", () => {
  test(".npmrc exists at the repo root", () => {
    expect(fs.existsSync(ROOT_NPMRC)).toBe(true);
  });

  describe("root .npmrc lower bounds", () => {
    let parsed;
    beforeAll(() => {
      parsed = parseNpmrc(fs.readFileSync(ROOT_NPMRC, "utf8"));
    });

    test.each([
      ["fetch-retries", LOWER_BOUND_FETCH_RETRIES],
      ["fetch-retry-mintimeout", LOWER_BOUND_MINTIMEOUT_MS],
      ["fetch-retry-maxtimeout", LOWER_BOUND_MAXTIMEOUT_MS]
    ])("%s >= %i", (key, lowerBound) => {
      expect(parsed[key]).toBeDefined();
      const numeric = Number(parsed[key]);
      expect(Number.isFinite(numeric)).toBe(true);
      expect(numeric).toBeGreaterThanOrEqual(lowerBound);
    });

    test("the three fetch-retry knobs are all present (single source of truth)", () => {
      // A regression that deletes ONE of the three would silently lose the
      // retry budget for the affected dimension; assert the set explicitly.
      const required = ["fetch-retries", "fetch-retry-mintimeout", "fetch-retry-maxtimeout"];
      const missing = required.filter((k) => parsed[k] === undefined);
      expect(missing).toEqual([]);
    });
  });

  test("no subordinate .npmrc weakens any of the three fetch-retry knobs", () => {
    const all = findAllNpmrc(REPO_ROOT);
    // Sanity: the walker found at least the root file we asserted above. A
    // walker bug that returned [] would otherwise let a subordinate weakening
    // slip through this test.
    expect(all.length).toBeGreaterThanOrEqual(1);

    const subordinates = all.filter((p) => path.resolve(p) !== path.resolve(ROOT_NPMRC));
    const violations = [];
    for (const subPath of subordinates) {
      const sub = parseNpmrc(fs.readFileSync(subPath, "utf8"));
      // Per-knob: a subordinate is allowed to be SILENT on a key (root wins),
      // or to RAISE it; it must NEVER LOWER. An unparseable value is treated
      // as a violation (could silently zero the budget).
      for (const [key, lowerBound] of [
        ["fetch-retries", LOWER_BOUND_FETCH_RETRIES],
        ["fetch-retry-mintimeout", LOWER_BOUND_MINTIMEOUT_MS],
        ["fetch-retry-maxtimeout", LOWER_BOUND_MAXTIMEOUT_MS]
      ]) {
        if (sub[key] === undefined) {
          continue;
        }
        const numeric = Number(sub[key]);
        if (!Number.isFinite(numeric) || numeric < lowerBound) {
          violations.push(
            `${path.relative(REPO_ROOT, subPath)} sets ${key}=${sub[key]} (below ${lowerBound})`
          );
        }
      }
    }
    if (violations.length > 0) {
      throw new Error(
        `Subordinate .npmrc weakens fetch-retry settings:\n  ${violations.join("\n  ")}`
      );
    }
    expect(violations).toEqual([]);
  });
});
