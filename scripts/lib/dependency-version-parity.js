"use strict";

/**
 * dependency-version-parity.js
 *
 * Offline, cross-platform detector for the dependency-version-drift class
 * that produced the cspell-lib 10.0.0-vs-10.0.1 pre-push failure.
 *
 * THE BUG CLASS (root cause of pre-push.txt):
 *   - `package.json` is the committed source of truth. Its dependency pins
 *     are EXACT (e.g. `"cspell": "10.0.1"`), no `^`/`~` range.
 *   - `package-lock.json` is GITIGNORED (see .gitignore) -- a per-machine,
 *     regenerated artifact, NOT authoritative and never committed.
 *   - A `git pull` (or a Dependabot bump) advances package.json to a new
 *     pin, but a developer machine that ran `npm install` during the old
 *     pin still has a STALE local lockfile + node_modules at the old
 *     version. The `npm ci`-based auto-repair (getNpmRecoveryCommand picks
 *     `npm ci` whenever a lockfile exists) then faithfully RE-CEMENTS the
 *     stale version from the stale lockfile -- it can never reconcile a
 *     manifest change. CI never hits this because a fresh checkout has no
 *     lockfile, so CI runs `npm i` and always matches package.json.
 *   - Nothing in the integrity/freshness/resolver-health stack checked
 *     VERSION parity (only file presence + resolver health), so the drift
 *     surfaced only at the slow last-resort native pre-push Jest suite.
 *
 * THE INVARIANT this module enforces (kills the EXACT-pin drift class for the
 * ROOT npm `package.json` -- the manifest the gitignored lockfile derives
 * from; sibling toolchains are out of scope, see below):
 *   For every EXACT-pinned direct dependency, the INSTALLED version on disk
 *   AND the local lockfile's resolved version (when a lockfile exists) MUST
 *   equal the package.json pin. RANGE pins (`^`/`~`/etc.) are checked for
 *   PRESENCE only by default -- a stale lockfile cannot strand a range the way
 *   it strands an exact pin, and the repo takes no `semver` dependency (the
 *   satisfier is an injectable hook used by tests). Any violation is "drift"
 *   and is the trigger for `npm install` recovery (reconcile from the
 *   manifest), NOT `npm ci` (which honors the stale lockfile).
 *
 * SCOPE (honest): this guards the root npm `package.json` only. Other
 * committed manifests in this repo are governed by their own restore
 * mechanisms and do NOT share the gitignored-stale-lockfile failure mode:
 * `.config/dotnet-tools.json` is restored directly from the manifest by
 * `dotnet tool restore` (no separate stale local lockfile), and
 * `.unity-test-project/Packages/packages-lock.json` is COMMITTED (authoritative,
 * not gitignored), so it cannot drift behind the manifest the way this one can.
 *
 * Purely synchronous file reads + JSON parsing: no network, no node_modules
 * code execution, no shell. Reads each tool's `node_modules/<name>/package.json`
 * DIRECTLY off disk -- cspell-lib's `exports` map blocks
 * `require("cspell-lib/package.json")`, so a direct read is the only portable
 * way to learn the installed version. Works identically on Linux, macOS, and
 * Windows.
 */

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

// An EXACT semver pin: MAJOR.MINOR.PATCH with an OPTIONAL prerelease
// (`-...`) THEN an OPTIONAL build (`+...`) -- both may appear together
// (`1.2.3-rc.1+build.9`), and NO leading range operator. These are the pins
// that drift silently against a stale lockfile; `^`/`~`/`>=`/`*`/`x` ranges
// resolve to whatever satisfies them and do not produce the "stale exact"
// failure. (An earlier `[-+]...` form rejected pins carrying BOTH a
// prerelease and a build, silently dropping them to `unversioned` -- the very
// miss this detector exists to prevent.)
const EXACT_PIN_RE = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z-.]+)?(?:\+[0-9A-Za-z-.]+)?$/;

// A range pin we can meaningfully satisfy-check (operator-prefixed or
// wildcard semver). Anything else (git URL, tarball URL, `file:`,
// `workspace:`, `npm:` alias, `link:`) is intentionally NOT version-checked
// here: those resolve by a mechanism this offline reader cannot model.
const RANGE_PIN_RE = /^(?:[\^~]|>=?|<=?|=|\d+(?:\.\d+)?(?:\.[xX*])?$|[xX*]$|\d+\.[xX*]$)/;

/**
 * Classify a package.json version spec.
 *
 * "unversioned" covers specs this offline reader cannot version-compare:
 * `git+`/tarball URLs, `file:`, `workspace:`, `npm:` aliases, `link:`, AND
 * dist-tags (`latest`/`next`) and `v`-prefixed forms. Those are intentionally
 * left unguarded -- their resolution is not derivable from the manifest text
 * alone, and none of this repo's pins use them.
 *
 * @param {string} spec Raw version string from dependencies/devDependencies.
 * @returns {"exact"|"range"|"unversioned"}
 */
function classifyPin(spec) {
  if (typeof spec !== "string") {
    return "unversioned";
  }
  const trimmed = spec.trim();
  if (EXACT_PIN_RE.test(trimmed)) {
    return "exact";
  }
  if (RANGE_PIN_RE.test(trimmed)) {
    return "range";
  }
  return "unversioned";
}

/**
 * Read the installed version of a package by reading its package.json
 * DIRECTLY off disk under node_modules. Bypasses `exports` maps (cspell-lib
 * blocks `require("cspell-lib/package.json")`). Handles scoped names
 * (`@scope/name`) via split on "/".
 *
 * @param {object} options
 * @param {string} options.repoRoot Absolute repository root.
 * @param {string} options.name Package name (possibly scoped).
 * @param {Function} [options.readFileSyncFn] Override fs.readFileSync.
 * @param {Function} [options.existsSyncFn] Override fs.existsSync.
 * @returns {string|null} The installed version, or null when the package is
 *   absent / its manifest is unreadable or malformed.
 */
function readInstalledVersion(options) {
  const {
    repoRoot,
    name,
    readFileSyncFn = fs.readFileSync,
    existsSyncFn = fs.existsSync
  } = options;
  const manifestPath = path.join(repoRoot, "node_modules", ...name.split("/"), "package.json");
  if (!existsSyncFn(manifestPath)) {
    return null;
  }
  try {
    const parsed = JSON.parse(readFileSyncFn(manifestPath, "utf8"));
    return typeof parsed.version === "string" ? parsed.version : null;
  } catch {
    return null;
  }
}

/**
 * Look up a top-level dependency's resolved version in a parsed
 * package-lock.json. Supports the npm v7+ `packages` map keyed by
 * "node_modules/<name>".
 *
 * @param {object|null} lockfile Parsed lockfile object, or null when none.
 * @param {string} name Package name.
 * @returns {string|null|undefined} The lockfile version; `null` when the
 *   lockfile exists but has no top-level entry for the package; `undefined`
 *   when there is no lockfile to consult.
 */
function readLockfileVersion(lockfile, name) {
  if (!lockfile || typeof lockfile !== "object") {
    return undefined;
  }
  const packages = lockfile.packages;
  if (!packages || typeof packages !== "object") {
    // Lockfile present but lacks the v7+ `packages` map. We cannot reason
    // about it precisely; treat as "no lockfile signal" rather than risk a
    // false drift verdict.
    return undefined;
  }
  const entry = packages[`node_modules/${name}`];
  if (entry && typeof entry.version === "string") {
    return entry.version;
  }
  return null;
}

/**
 * Probe every direct dependency for version parity between the package.json
 * pin, the installed node_modules copy, and (when present) the local
 * lockfile.
 *
 * @param {object} [options]
 * @param {string} [options.repoRoot] Absolute repository root.
 * @param {object} [options.packageJson] Pre-parsed package.json (for tests).
 * @param {object|null} [options.lockfile] Pre-parsed lockfile, or null. When
 *   omitted, the lockfile is read from disk (absent -> treated as null).
 * @param {Function} [options.readFileSyncFn] Override fs.readFileSync.
 * @param {Function} [options.existsSyncFn] Override fs.existsSync.
 * @param {Function} [options.semverSatisfiesFn] Optional `(version, range) =>
 *   boolean` satisfier for RANGE pins. Defaults to `null`: the repo
 *   deliberately takes NO `semver` dependency (keeping deps minimal), and the
 *   drift class this guards is EXACT pins, which need only string equality.
 *   With no satisfier, range pins are checked for PRESENCE only (an absent
 *   range dep is still "not-installed" drift). Tests inject a satisfier to
 *   exercise the range branch.
 * @returns {{ok: boolean, drifted: Array<{name: string, kind: string, declared: string, installed: string|null, lockfile: string|null|undefined, reason: string}>, checked: number}}
 */
function probeDependencyVersionParity(options = {}) {
  const {
    repoRoot = REPO_ROOT,
    readFileSyncFn = fs.readFileSync,
    existsSyncFn = fs.existsSync,
    semverSatisfiesFn = null
  } = options;

  const packageJson =
    options.packageJson || JSON.parse(readFileSyncFn(path.join(repoRoot, "package.json"), "utf8"));

  let lockfile = options.lockfile;
  if (lockfile === undefined) {
    const lockPath = path.join(repoRoot, "package-lock.json");
    lockfile = null;
    if (existsSyncFn(lockPath)) {
      try {
        lockfile = JSON.parse(readFileSyncFn(lockPath, "utf8"));
      } catch {
        lockfile = null;
      }
    }
  }

  const declared = Object.assign(
    {},
    packageJson.dependencies || {},
    packageJson.devDependencies || {}
  );

  const drifted = [];
  let checked = 0;

  for (const name of Object.keys(declared)) {
    const spec = declared[name];
    const kind = classifyPin(spec);
    if (kind === "unversioned") {
      // git/url/file/workspace/alias specs: not version-comparable offline.
      continue;
    }
    checked += 1;

    const installed = readInstalledVersion({ repoRoot, name, readFileSyncFn, existsSyncFn });
    const lockVersion = readLockfileVersion(lockfile, name);
    const trimmedSpec = spec.trim();

    if (installed === null) {
      drifted.push({
        name,
        kind,
        declared: trimmedSpec,
        installed: null,
        lockfile: lockVersion,
        reason: "not-installed"
      });
      continue;
    }

    if (kind === "exact") {
      if (installed !== trimmedSpec) {
        drifted.push({
          name,
          kind,
          declared: trimmedSpec,
          installed,
          lockfile: lockVersion,
          reason: "installed-mismatch"
        });
        continue;
      }
      // Installed is correct, but a stale lockfile would let a later
      // `npm ci` re-cement the wrong version. Flag it so recovery
      // (npm install) reconciles the lockfile too. `undefined` means no
      // lockfile to consult; `null` means present-but-no-entry (we cannot
      // judge -- do not flag).
      if (typeof lockVersion === "string" && lockVersion !== trimmedSpec) {
        drifted.push({
          name,
          kind,
          declared: trimmedSpec,
          installed,
          lockfile: lockVersion,
          reason: "lockfile-stale"
        });
      }
      continue;
    }

    // kind === "range": installed must satisfy the range. When semver is
    // unavailable, a present install is accepted (no false drift).
    if (typeof semverSatisfiesFn === "function") {
      const satisfies = semverSatisfiesFn(installed, trimmedSpec);
      if (satisfies === false) {
        drifted.push({
          name,
          kind,
          declared: trimmedSpec,
          installed,
          lockfile: lockVersion,
          reason: "range-unsatisfied"
        });
      }
    }
  }

  return { ok: drifted.length === 0, drifted, checked };
}

/**
 * Render a single, platform-agnostic line per drifted dependency. Used by
 * validators and the doctor so the message is identical across OSes.
 *
 * @param {{drifted: Array<object>}} result
 * @returns {string[]}
 */
function formatDriftLines(result) {
  if (!result || !Array.isArray(result.drifted) || result.drifted.length === 0) {
    return [];
  }
  return result.drifted.map((entry) => {
    const installed = entry.installed === null ? "<not installed>" : entry.installed;
    const lockPart = typeof entry.lockfile === "string" ? `, lockfile=${entry.lockfile}` : "";
    return `${entry.name}: declared ${entry.declared} (${entry.kind}) but installed ${installed}${lockPart} [${entry.reason}]`;
  });
}

module.exports = {
  REPO_ROOT,
  EXACT_PIN_RE,
  classifyPin,
  readInstalledVersion,
  readLockfileVersion,
  probeDependencyVersionParity,
  formatDriftLines
};
