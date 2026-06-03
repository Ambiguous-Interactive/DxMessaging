#!/usr/bin/env node
/**
 * @fileoverview Fails CI if `git` reports any untracked-and-unignored path at
 * the repo level. Forces the contributor of a new tooling output directory to
 * either commit it or add it to `.gitignore` (with a one-line rationale) and
 * to update `.npmignore` if the path should not ship in the published
 * package.
 *
 * The validator runs `git ls-files -z --others --exclude-standard` (NUL
 * separator + `core.quotepath=false`) from the repo root via the project's
 * `spawnPlatformCommandSync` helper. An empty result means a clean tree; any
 * non-empty result fails by default.
 *
 * An emergency-override surface exists for local debugging only:
 *   - `--allow=<glob>` (repeatable) on the CLI, OR
 *   - `DX_UNTRACKED_ALLOW=<colon-separated-globs>` in the environment.
 * CI invocations and the wired npm script must pass NO allowlist; the strict
 * default is the whole point of the validator.
 *
 * Globs use a small inline matcher that supports `*` (single-segment) and
 * a doubled-star recursive form (cross-separator). The two patterns we use
 * in practice (`foo*`, `<recursive>/.benchmark-*`) are well within this
 * subset, and inlining a ~40-line matcher keeps the validator free of the
 * `minimatch` runtime dependency.
 *
 * Path-resolution policy:
 *   The repo root is `path.resolve(__dirname, "..")` so the validator works
 *   whether invoked from the repo root or a subdirectory. Subprocess git
 *   invocations always set `cwd` to the resolved repo root so the result
 *   reflects the package's real working tree.
 *
 * Reporting policy (M4):
 *   When more than three untracked paths share a common first path segment
 *   (typically a build-output directory), the validator emits ONE error
 *   naming the directory plus a count instead of N separate errors. Three
 *   or fewer paths in a group are listed individually.
 *
 * Exit codes:
 *   0  Clean working tree (no untracked-and-unignored paths or all matched
 *      by an allowlist entry).
 *   1  Untracked path found that does not match any allowlist entry, or git
 *      was unavailable, or unknown CLI flag.
 */

"use strict";

const path = require("path");
const { spawnPlatformCommandSync } = require("./lib/shell-command");

const REPO_ROOT = path.resolve(__dirname, "..");

// Threshold above which a per-directory rollup replaces per-file errors.
// Three or fewer files in the same first-segment group are listed verbatim;
// four or more roll up into a single directory-level diagnostic.
const ROLLUP_THRESHOLD = 3;

/**
 * Compile a glob pattern into a `RegExp`. Supports `*` (matches a single path
 * segment without separators) and the doubled-star recursive form (matches
 * across separators including empty segments). The matcher is intentionally
 * minimal so the two patterns the project uses (`foo*`, recursive
 * `<star><star>/...` forms, and `dir/<star><star>`) Just Work without
 * pulling in `minimatch`.
 *
 * @param {string} pattern
 * @returns {RegExp}
 */
function compileGlob(pattern) {
  // Escape regex meta-characters except those we explicitly handle (`*`).
  const specials = /[.+?^${}()|[\]\\]/g;
  let regex = "";
  let i = 0;
  while (i < pattern.length) {
    const ch = pattern[i];
    if (ch === "*") {
      if (pattern[i + 1] === "*") {
        // `**` matches across separators (including empty). Trailing `/` is
        // consumed when present so `dir/**` matches `dir/x` and `dir/x/y`.
        if (pattern[i + 2] === "/") {
          regex += "(?:.*/)?";
          i += 3;
        } else {
          regex += ".*";
          i += 2;
        }
        continue;
      }
      regex += "[^/]*";
      i += 1;
      continue;
    }
    if (ch === "?") {
      regex += "[^/]";
      i += 1;
      continue;
    }
    regex += ch.replace(specials, "\\$&");
    i += 1;
  }
  return new RegExp(`^${regex}$`);
}

/**
 * Match a candidate path against an allowlist of glob patterns.
 *
 * @param {string} candidate - Repo-relative POSIX path
 * @param {string[]} allowList
 * @returns {boolean}
 */
function isAllowed(candidate, allowList) {
  if (!allowList || allowList.length === 0) {
    return false;
  }
  for (const pattern of allowList) {
    if (compileGlob(pattern).test(candidate)) {
      return true;
    }
  }
  return false;
}

/**
 * Convert NUL-terminated `git ls-files -z` output into an array of paths.
 * The `-z` flag avoids any quoting, so non-ASCII bytes survive intact and
 * paths containing whitespace or special characters are split correctly.
 *
 * @param {string|Buffer} stdout
 * @returns {string[]}
 */
function parseUntrackedOutput(stdout) {
  if (stdout == null) {
    return [];
  }
  const text = Buffer.isBuffer(stdout) ? stdout.toString("utf8") : String(stdout);
  if (text.length === 0) {
    return [];
  }
  return text
    .split("\0")
    .map((entry) => entry.replace(/\r/g, "").trim())
    .filter((entry) => entry.length > 0);
}

/**
 * Parse `--allow=<glob>` and `--allow <glob>` flag forms into a flat array.
 * Returns `errors` for any unknown argument so a typo cannot accidentally
 * disable the check.
 *
 * @param {string[]} argv
 * @returns {{allow: string[], help: boolean, errors: string[]}}
 */
function parseArgs(argv) {
  const allow = [];
  const errors = [];
  let help = false;

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];

    if (arg === "--help" || arg === "-h") {
      help = true;
      continue;
    }

    if (arg.startsWith("--allow=")) {
      const value = arg.slice("--allow=".length);
      if (value.length > 0) {
        allow.push(value);
      }
      continue;
    }

    if (arg === "--allow") {
      const next = argv[i + 1];
      if (typeof next !== "string" || next.length === 0) {
        errors.push("--allow requires a glob pattern argument");
        continue;
      }
      allow.push(next);
      i += 1;
      continue;
    }

    errors.push(`Unknown argument: ${arg}`);
  }

  return { allow, help, errors };
}

/**
 * Read the env-var override into a list of globs.
 *
 * @param {string|undefined} value - Raw env-var value
 * @returns {string[]}
 */
function parseEnvAllowList(value) {
  if (typeof value !== "string" || value.length === 0) {
    return [];
  }
  // Use the OS-natural list separator. POSIX uses `:`; Windows uses `;`.
  // Both are accepted regardless of platform so a contributor copying a
  // value across machines does not have to remember which one to use.
  return value
    .split(/[:;]/)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0);
}

/**
 * Invoke `git -c core.quotepath=false ls-files -z --others
 * --exclude-standard` and return the parsed output. The `-z` flag uses NUL
 * terminators that survive any byte content; pairing it with
 * `core.quotepath=false` (defense in depth) guarantees the validator sees
 * the same bytes git stored in the index.
 *
 * Failure to invoke git is treated as a hard error per `.llm/context.md`
 * policy: validators MUST NOT silently default to permissive behavior when
 * git metadata is unavailable.
 *
 * @param {object} [options]
 * @param {string} [options.cwd] - Override the cwd (used by tests)
 * @param {Function} [options.spawn] - Inject a spawn implementation for tests
 * @returns {{ok: true, files: string[]} | {ok: false, type: string, message: string}}
 */
function listUntrackedFiles(options = {}) {
  const cwd = options.cwd || REPO_ROOT;
  const spawn = options.spawn || spawnPlatformCommandSync;

  const result = spawn(
    "git",
    ["-c", "core.quotepath=false", "ls-files", "-z", "--others", "--exclude-standard"],
    {
      cwd,
      // We request a Buffer for stdout so `-z`'s NUL bytes survive, but pass
      // utf8 for stderr so error messages render. Spawn helpers that ignore
      // the encoding split still receive the same arguments.
      encoding: "buffer",
      stdio: ["ignore", "pipe", "pipe"]
    }
  );

  if (result && result.error) {
    if (result.error.code === "ENOENT") {
      return {
        ok: false,
        type: "git-not-installed",
        message:
          "git was not found on PATH. Install git or run this validator from a shell where git is available."
      };
    }
    return {
      ok: false,
      type: "git-spawn-error",
      message: `Failed to spawn git: ${result.error.message || result.error}`
    };
  }

  if (!result || typeof result.status !== "number") {
    return {
      ok: false,
      type: "git-spawn-error",
      message: "git ls-files produced no result object"
    };
  }

  if (result.status !== 0) {
    const stderrRaw = result.stderr;
    const stderr =
      stderrRaw == null
        ? ""
        : Buffer.isBuffer(stderrRaw)
          ? stderrRaw.toString("utf8")
          : String(stderrRaw);
    if (/not a git repository/i.test(stderr)) {
      return {
        ok: false,
        type: "not-a-git-repository",
        message:
          `Not a git repository (cwd=${cwd}). ` +
          `validate-untracked-policy must run inside a git working tree so ` +
          `it can enumerate untracked-and-unignored paths.`
      };
    }
    return {
      ok: false,
      type: "git-exit-error",
      message: `git ls-files exited with status ${result.status}: ${stderr.trim() || "no stderr"}`
    };
  }

  return { ok: true, files: parseUntrackedOutput(result.stdout) };
}

/**
 * Group untracked paths by their first path segment so a directory-level
 * rollup can replace N per-file errors with one diagnostic. Paths with no
 * separator are placed in a synthetic `__root__` bucket and listed
 * individually.
 *
 * @param {string[]} paths
 * @returns {{singletons: string[], groups: Array<{prefix: string, files: string[]}>}}
 */
function groupByFirstSegment(paths) {
  const buckets = new Map();
  const rootSingletons = [];

  for (const file of paths) {
    const slashIdx = file.indexOf("/");
    if (slashIdx <= 0) {
      // No directory prefix; track separately so it is always reported as
      // an individual file.
      rootSingletons.push(file);
      continue;
    }
    const prefix = file.slice(0, slashIdx);
    if (!buckets.has(prefix)) {
      buckets.set(prefix, []);
    }
    buckets.get(prefix).push(file);
  }

  const groups = [];
  const singletons = rootSingletons.slice();
  for (const [prefix, files] of buckets.entries()) {
    if (files.length > ROLLUP_THRESHOLD) {
      groups.push({ prefix, files });
    } else {
      singletons.push(...files);
    }
  }

  // Stable order: singletons by path, groups by prefix.
  singletons.sort();
  groups.sort((a, b) => a.prefix.localeCompare(b.prefix));
  return { singletons, groups };
}

/**
 * Build the per-file remediation message used by both individual paths and
 * directory rollups. Mentions BOTH `.gitignore` AND `.npmignore` because a
 * tooling-output directory typically needs to be excluded from BOTH.
 *
 * @param {string} pathOrDir
 * @param {boolean} isDirectory
 * @param {number} [count]
 * @returns {string}
 */
function buildRemediationMessage(pathOrDir, isDirectory, count) {
  if (isDirectory) {
    const trailing = pathOrDir.endsWith("/") ? pathOrDir : `${pathOrDir}/`;
    return (
      `Untracked-and-unignored directory '${trailing}' contains ${count} files. ` +
      `Either commit them or add '${trailing}' to .gitignore ` +
      `(and .npmignore if the directory should not ship in the published package). ` +
      `For intentionally-local paths, add a one-line comment in your .gitignore explaining why.`
    );
  }
  return (
    `Untracked-and-unignored path '${pathOrDir}'. ` +
    `Either commit it or add it to .gitignore ` +
    `(and .npmignore if the path should not ship in the published package). ` +
    `If it is intentionally local, add a one-line comment in your .gitignore explaining why.`
  );
}

/**
 * Run the validator and return a structured result.
 *
 * @param {object} [options]
 * @param {string[]} [options.allow] - CLI-supplied allowlist globs
 * @param {string[]} [options.envAllow] - Env-var-supplied allowlist globs
 * @param {string} [options.cwd] - Override repo root
 * @param {Function} [options.spawn] - Inject spawn implementation
 * @returns {{
 *   valid: boolean,
 *   errors: Array<{type: string, file?: string, directory?: string, count?: number, files?: string[], message: string}>,
 *   untracked: string[],
 *   ignoredByAllowlist: string[]
 * }}
 */
function validate(options = {}) {
  const allowList = [...(options.allow || []), ...(options.envAllow || [])];
  const list = listUntrackedFiles({ cwd: options.cwd, spawn: options.spawn });

  if (!list.ok) {
    return {
      valid: false,
      errors: [{ type: list.type, message: list.message }],
      untracked: [],
      ignoredByAllowlist: []
    };
  }

  if (list.files.length === 0) {
    return { valid: true, errors: [], untracked: [], ignoredByAllowlist: [] };
  }

  const violations = [];
  const ignoredByAllowlist = [];

  // Apply the allowlist first so a per-file allow can suppress an entry that
  // would otherwise contribute to the rollup count.
  const remaining = [];
  for (const file of list.files) {
    if (isAllowed(file, allowList)) {
      ignoredByAllowlist.push(file);
      continue;
    }
    remaining.push(file);
  }

  const { singletons, groups } = groupByFirstSegment(remaining);

  for (const file of singletons) {
    violations.push({
      type: "untracked-path",
      file,
      message: buildRemediationMessage(file, false)
    });
  }

  for (const group of groups) {
    violations.push({
      type: "untracked-directory",
      directory: group.prefix,
      count: group.files.length,
      files: group.files,
      message: buildRemediationMessage(group.prefix, true, group.files.length)
    });
  }

  return {
    valid: violations.length === 0,
    errors: violations,
    untracked: list.files,
    ignoredByAllowlist
  };
}

/**
 * Pretty-print the validation result. Returns the process exit code.
 *
 * @param {ReturnType<typeof validate>} result
 * @param {{logger?: typeof console}} [options]
 * @returns {number}
 */
function reportResult(result, options = {}) {
  const logger = options.logger || console;

  if (result.valid && result.untracked.length === 0) {
    logger.log("validate-untracked-policy: OK (no untracked-and-unignored paths)");
    return 0;
  }

  if (result.valid && result.ignoredByAllowlist.length > 0) {
    logger.log(
      `validate-untracked-policy: OK (${result.ignoredByAllowlist.length} ` +
        `untracked path(s) matched the allowlist; the strict default for CI is no allowlist)`
    );
    return 0;
  }

  logger.log("validate-untracked-policy: FAILED");
  for (const error of result.errors) {
    if (error.type === "untracked-directory") {
      logger.log(
        `  - [${error.type}] ${error.directory}/ (${error.count} files): ${error.message}`
      );
    } else if (error.file) {
      logger.log(`  - [${error.type}] ${error.file}: ${error.message}`);
    } else {
      logger.log(`  - [${error.type}] ${error.message}`);
    }
  }
  logger.log(
    "Remediation: each untracked path must be either committed or covered by " +
      ".gitignore (and .npmignore if it should not ship), with a one-line " +
      "rationale comment for intentionally-local paths."
  );
  return 1;
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    console.log(
      "Usage: node scripts/validate-untracked-policy.js [--allow=<glob>...] \n" +
        "Env override (debugging only): DX_UNTRACKED_ALLOW=<glob>:<glob>...\n" +
        "Strict default: any untracked-and-unignored path fails the run."
    );
    return 0;
  }
  if (args.errors.length > 0) {
    for (const message of args.errors) {
      console.error(message);
    }
    return 1;
  }

  const envAllow = parseEnvAllowList(process.env.DX_UNTRACKED_ALLOW);
  const result = validate({ allow: args.allow, envAllow });
  return reportResult(result);
}

if (require.main === module) {
  process.exitCode = main();
}

module.exports = {
  REPO_ROOT,
  ROLLUP_THRESHOLD,
  compileGlob,
  buildRemediationMessage,
  groupByFirstSegment,
  parseArgs,
  parseEnvAllowList,
  parseUntrackedOutput,
  isAllowed,
  listUntrackedFiles,
  validate,
  reportResult,
  main
};
