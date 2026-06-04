"use strict";

/**
 * changed-files.js
 *
 * Compute the "change-set" that `scripts/preflight.js` runs change-aware
 * checks against. The change-set answers the question "what will this branch
 * contribute to its integration target, plus any uncommitted local work?" --
 * i.e. the PR diff (committed range vs the merge-base with the integration
 * branch) combined with staged + unstaged + untracked working-tree edits.
 *
 * This set is intentionally NARROWER than the manual/CI
 * `npm run preflight:pre-push` exhaustive sweep. The native pre-push hook runs
 * exact pushed ranges; exhaustive parity belongs to CI/manual use. The
 * preflight orchestrator delegates file -> hook matching to pre-commit itself
 * (`--from-ref/--to-ref` for committed/range input, `--files` for the working
 * tree), so this module's only job is to enumerate the right paths.
 *
 * Two scopes are supported (see {@link computeChangeSet}):
 *   - "full" (default): resolve an integration base, then union the committed
 *     range with the working-tree sources.
 *   - "worktree": SKIP base resolution and the committed range entirely; use
 *     only staged + unstaged + untracked. This keeps the Stop hook fast on a
 *     many-commit branch (it never resolves a base or scans the committed
 *     range).
 *   - "range": explicit `rangeFrom..rangeTo` diff, used by native pre-push to
 *     validate the actual ref update without consulting origin/HEAD.
 *
 * Base-resolution order (full scope, fail-soft):
 *   1. `baseOverride` if provided (CI passes the PR base).
 *   2. `origin/HEAD` (strip `origin/`).
 *   3. `origin/master`, then `origin/main`.
 *   4. local `master`, then `main`.
 *   5. No base resolves -> working-tree-only (fresh clone / detached HEAD /
 *      first push). This is a SOFT condition, not an error.
 *
 * Error policy:
 *   - A genuinely missing `git` binary (spawn ENOENT) -> HARD error (throw).
 *   - A missing base ref -> SOFT (scope narrows to working-tree-only).
 *
 * All git invocations route through `spawnPlatformCommandSync("git", ...)`
 * (the single cross-platform spawn shape; `git` is not a shim so the call is a
 * clean passthrough on every platform). `runGitFn` is injectable for tests,
 * mirroring `validate-changed-docs.js`. All path output is parsed with NUL
 * splitting (`-z`) and POSIX-normalized via `toRepoPosixRelative`.
 */

const childProcess = require("child_process");
const path = require("path");
const { spawnPlatformCommandSync } = require("./shell-command");
const { toRepoPosixRelative } = require("./path-classifier");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

/**
 * Diff filter shared by every content source: Added, Copied, Modified,
 * Renamed. Deletions (`D`) are intentionally excluded -- pre-commit skips
 * deleted files, so feeding them to `--files` would only produce "no files to
 * check" noise (verified against live pre-commit 4.6.0).
 */
const DIFF_FILTER = "ACMR";

/**
 * Ordered remote base-ref candidates tried after `origin/HEAD`. The first one
 * that `git rev-parse --verify` resolves wins.
 */
const REMOTE_BASE_CANDIDATES = Object.freeze(["origin/master", "origin/main"]);

/**
 * Ordered local base-ref candidates tried after the remote candidates.
 */
const LOCAL_BASE_CANDIDATES = Object.freeze(["master", "main"]);

/**
 * Default git runner. Routes through the cross-platform spawn shape and
 * returns the raw spawnSync result object so callers can inspect
 * `status`/`stdout`/`error` themselves.
 *
 * @param {string[]} args git arguments (no leading "git").
 * @param {object} [options] spawnSync option overrides.
 * @returns {object} spawnSync result.
 */
function runGit(args, options = {}) {
  return spawnPlatformCommandSync(
    "git",
    args,
    {
      cwd: REPO_ROOT,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      ...options
    },
    childProcess.spawnSync
  );
}

/**
 * True when a spawn result represents a missing executable (ENOENT). A missing
 * `git` binary is a hard error for this module.
 *
 * @param {object} result spawnSync result.
 * @returns {boolean}
 */
function isMissingGit(result) {
  return !!(result && result.error && result.error.code === "ENOENT");
}

/**
 * Throw the canonical hard-error when the `git` binary cannot be spawned.
 *
 * @param {object} result spawnSync result that triggered the failure.
 * @returns {never}
 */
function throwMissingGit(result) {
  const detail = result && result.error && result.error.message ? result.error.message : "ENOENT";
  throw new Error(`changed-files: unable to spawn git (${detail}); git must be on PATH.`);
}

/**
 * Run a git command that MUST succeed for the change-set to be meaningful (the
 * working-tree sources). A missing git binary throws (hard error); any other
 * non-zero exit also throws because a broken `git diff` would silently
 * under-report the change-set.
 *
 * @param {Function} runGitFn injected git runner.
 * @param {string[]} args git arguments.
 * @param {string} description human label for error messages.
 * @returns {object} spawnSync result (guaranteed status 0).
 */
function runRequiredGit(runGitFn, args, description) {
  const result = runGitFn(args);
  if (isMissingGit(result)) {
    throwMissingGit(result);
  }
  if (!result || result.error || result.status !== 0) {
    const detail =
      result && result.error ? result.error.message : `status ${result && result.status}`;
    const stderr = result && result.stderr ? String(result.stderr).trim() : "";
    throw new Error(
      `changed-files: git ${description} failed: ${detail}${stderr ? ` (${stderr})` : ""}.`
    );
  }
  return result;
}

/**
 * Split a NUL-delimited (`-z`) git stdout payload into a list of non-empty
 * fields, preserving order.
 *
 * @param {string} stdout raw `-z` stdout.
 * @returns {string[]} ordered NUL fields with empties removed.
 */
function splitNulFields(stdout) {
  return String(stdout || "")
    .split("\0")
    .filter((field) => field.length > 0);
}

/**
 * Parse `git diff --name-status -z` output into a list of changed paths,
 * honoring {@link DIFF_FILTER}.
 *
 * Under `-z`, each entry is a status field followed by one path field
 * (`M\0path\0`), EXCEPT renames/copies which emit a score-suffixed status
 * (`R100`, `C075`, ...) followed by TWO path fields (old, then new). We keep
 * only the NEW path for `R`/`C` so the path exists in the working tree (pre-
 * commit re-resolves matching against the live tree; a non-existent old path
 * would just be skipped). `D` deletions are dropped.
 *
 * @param {string} stdout raw `git diff --name-status -z` stdout.
 * @returns {string[]} changed paths (new path for renames/copies).
 */
function parseNameStatusZ(stdout) {
  const fields = splitNulFields(stdout);
  const paths = [];

  for (let i = 0; i < fields.length; i++) {
    const status = fields[i];
    const code = status.charAt(0).toUpperCase();

    if (code === "R" || code === "C") {
      // Score-suffixed status; consume old + new, keep new.
      const newPath = fields[i + 2];
      if (newPath) {
        paths.push(newPath);
      }
      i += 2;
      continue;
    }

    // Single-path statuses (A/M/T/U/X/B and any others). Consume one path.
    const single = fields[i + 1];
    i += 1;
    if (!single) {
      continue;
    }
    if (code === "D") {
      // Deletions excluded for content (DIFF_FILTER ACMR).
      continue;
    }
    paths.push(single);
  }

  return paths;
}

/**
 * Resolve a ref to true/false via `git rev-parse --verify --quiet <ref>`.
 *
 * @param {Function} runGitFn injected git runner.
 * @param {string} ref ref expression.
 * @returns {boolean} true when the ref resolves.
 */
function refExists(runGitFn, ref) {
  const result = runGitFn(["rev-parse", "--verify", "--quiet", ref]);
  if (isMissingGit(result)) {
    throwMissingGit(result);
  }
  return !!(result && !result.error && result.status === 0);
}

/**
 * Resolve the default-branch candidate from `origin/HEAD` if it exists,
 * stripping the leading `origin/`.
 *
 * @param {Function} runGitFn injected git runner.
 * @returns {string|null} e.g. "master" / "main", or null when unresolved.
 */
function resolveOriginHeadBranch(runGitFn) {
  const result = runGitFn(["rev-parse", "--verify", "--quiet", "refs/remotes/origin/HEAD"]);
  if (isMissingGit(result)) {
    throwMissingGit(result);
  }
  if (!result || result.error || result.status !== 0) {
    return null;
  }
  // The candidate ref is symbolic; use `origin/HEAD` itself as the candidate
  // ref for merge-base (it dereferences to the default branch tip).
  return "origin/HEAD";
}

/**
 * Resolve the integration base ref (fail-soft). Returns the candidate ref
 * string, or null when nothing resolves (working-tree-only scope).
 *
 * @param {Function} runGitFn injected git runner.
 * @param {string|null} baseOverride explicit base (CI), highest priority.
 * @returns {string|null} candidate base ref or null.
 */
function resolveBaseRef(runGitFn, baseOverride) {
  if (typeof baseOverride === "string" && baseOverride.length > 0) {
    return refExists(runGitFn, baseOverride) ? baseOverride : null;
  }

  const originHead = resolveOriginHeadBranch(runGitFn);
  if (originHead) {
    return originHead;
  }

  for (const candidate of REMOTE_BASE_CANDIDATES) {
    if (refExists(runGitFn, candidate)) {
      return candidate;
    }
  }

  for (const candidate of LOCAL_BASE_CANDIDATES) {
    if (refExists(runGitFn, candidate)) {
      return candidate;
    }
  }

  return null;
}

/**
 * Compute the merge-base between a base candidate ref and HEAD. Returns the
 * merge-base sha, or null when the two have no common ancestor (treated soft:
 * the committed range is simply skipped).
 *
 * @param {Function} runGitFn injected git runner.
 * @param {string} baseRef resolved base candidate ref.
 * @returns {string|null} merge-base sha or null.
 */
function resolveMergeBase(runGitFn, baseRef) {
  const result = runGitFn(["merge-base", baseRef, "HEAD"]);
  if (isMissingGit(result)) {
    throwMissingGit(result);
  }
  if (!result || result.error || result.status !== 0) {
    return null;
  }
  const sha = String(result.stdout || "").trim();
  return sha.length > 0 ? sha : null;
}

/**
 * Collect the committed-range change source: `git diff --name-status
 * --diff-filter=ACMR -z <mergeBase> HEAD`. Only invoked in full scope when a
 * merge base resolved.
 *
 * @param {Function} runGitFn injected git runner.
 * @param {string} mergeBase merge-base sha.
 * @returns {string[]} changed paths.
 */
function collectCommittedRange(runGitFn, rangeFrom, rangeTo = "HEAD") {
  const result = runRequiredGit(
    runGitFn,
    ["diff", "--name-status", `--diff-filter=${DIFF_FILTER}`, "-z", rangeFrom, rangeTo],
    "diff committed-range"
  );
  return parseNameStatusZ(result.stdout);
}

/**
 * Collect staged changes: `git diff --cached --name-status
 * --diff-filter=ACMR -z`.
 *
 * @param {Function} runGitFn injected git runner.
 * @returns {string[]} changed paths.
 */
function collectStaged(runGitFn) {
  const result = runRequiredGit(
    runGitFn,
    ["diff", "--cached", "--name-status", `--diff-filter=${DIFF_FILTER}`, "-z"],
    "diff --cached"
  );
  return parseNameStatusZ(result.stdout);
}

/**
 * Collect unstaged tracked changes: `git diff --name-status
 * --diff-filter=ACMR -z`.
 *
 * @param {Function} runGitFn injected git runner.
 * @returns {string[]} changed paths.
 */
function collectUnstaged(runGitFn) {
  const result = runRequiredGit(
    runGitFn,
    ["diff", "--name-status", `--diff-filter=${DIFF_FILTER}`, "-z"],
    "diff"
  );
  return parseNameStatusZ(result.stdout);
}

/**
 * Collect untracked-and-unignored files: `git ls-files --others
 * --exclude-standard -z`. These are plain path fields (no status prefix).
 *
 * @param {Function} runGitFn injected git runner.
 * @returns {string[]} untracked paths.
 */
function collectUntracked(runGitFn) {
  const result = runRequiredGit(
    runGitFn,
    ["ls-files", "--others", "--exclude-standard", "-z"],
    "ls-files --others"
  );
  return splitNulFields(result.stdout);
}

/**
 * POSIX-normalize, de-duplicate, and sort a list of repo-relative paths.
 *
 * @param {string[]} paths raw paths.
 * @returns {string[]} sorted, unique, POSIX-separator paths.
 */
function normalizePaths(paths) {
  const normalized = paths
    .map((file) => toRepoPosixRelative(file, REPO_ROOT))
    .filter((file) => typeof file === "string" && file.length > 0);
  return [...new Set(normalized)].sort();
}

/**
 * Compute the preflight change-set.
 *
 * @param {object} [options]
 * @param {Function} [options.runGitFn] Injected git runner (default routes
 *   through `spawnPlatformCommandSync("git", ...)`). Tests pass a fake.
 * @param {string|null} [options.baseOverride] Explicit integration base ref
 *   (CI passes the PR base). Highest-priority base candidate.
 * @param {"full"|"worktree"} [options.scope] "full" (default) resolves a base
 *   and includes the committed range; "worktree" skips base resolution and the
 *   committed range entirely.
 * @param {string|null} [options.rangeFrom] Explicit range start for native
 *   pre-push. When paired with rangeTo, base resolution is skipped.
 * @param {string|null} [options.rangeTo] Explicit range end for native
 *   pre-push.
 * @param {boolean} [options.noWorktree] Skip staged/unstaged/untracked scans.
 * @returns {{
 *   files: string[],
 *   base: string|null,
 *   mergeBase: string|null,
 *   scope: "full"|"worktree"|"range",
 *   sources: {
 *     committed: string[],
 *     staged: string[],
 *     unstaged: string[],
 *     untracked: string[]
 *   }
 * }} Documented shape consumed by preflight.js and the tests:
 *   - `files`: the de-duped, sorted, POSIX-normalized union of every source.
 *   - `base`: the resolved base candidate ref, explicit range start, or null
 *     (worktree scope, or no base resolved).
 *   - `mergeBase`: the merge-base/range-start sha used as `--from-ref`, or
 *     null.
 *   - `rangeTo`: explicit range end used as `--to-ref`, or null/HEAD.
 *   - `scope`: the effective scope.
 *   - `sources`: each source's normalized contribution (for the two-pass
 *     dedupe in preflight.js: committed -> `--from-ref/--to-ref` pass, the rest
 *     -> `--files` pass).
 * @throws {Error} when the `git` binary is missing (hard error) or a required
 *   working-tree git command fails.
 */
function computeChangeSet(options = {}) {
  const {
    runGitFn = runGit,
    baseOverride = null,
    scope = "full",
    rangeFrom = null,
    rangeTo = null,
    noWorktree = false
  } = options;
  const hasExplicitRange =
    typeof rangeFrom === "string" &&
    rangeFrom.length > 0 &&
    typeof rangeTo === "string" &&
    rangeTo.length > 0;
  const effectiveScope = hasExplicitRange ? "range" : scope === "worktree" ? "worktree" : "full";

  let base = null;
  let mergeBase = null;
  let committed = [];

  if (hasExplicitRange) {
    base = rangeFrom;
    mergeBase = rangeFrom;
    committed = collectCommittedRange(runGitFn, rangeFrom, rangeTo);
  } else if (effectiveScope === "full") {
    base = resolveBaseRef(runGitFn, baseOverride);
    if (base) {
      mergeBase = resolveMergeBase(runGitFn, base);
      if (mergeBase) {
        committed = collectCommittedRange(runGitFn, mergeBase, "HEAD");
      }
    }
  }

  // Working-tree sources run in every scope unless explicitly disabled by the
  // native pre-push range runner. These are the commands that surface a
  // genuinely missing git binary as the hard ENOENT error in worktree/full mode.
  const staged = noWorktree ? [] : collectStaged(runGitFn);
  const unstaged = noWorktree ? [] : collectUnstaged(runGitFn);
  const untracked = noWorktree ? [] : collectUntracked(runGitFn);

  const sources = {
    committed: normalizePaths(committed),
    staged: normalizePaths(staged),
    unstaged: normalizePaths(unstaged),
    untracked: normalizePaths(untracked)
  };

  const files = normalizePaths([...committed, ...staged, ...unstaged, ...untracked]);

  return {
    files,
    base,
    mergeBase,
    rangeTo: hasExplicitRange ? rangeTo : null,
    scope: effectiveScope,
    sources
  };
}

module.exports = {
  REPO_ROOT,
  DIFF_FILTER,
  REMOTE_BASE_CANDIDATES,
  LOCAL_BASE_CANDIDATES,
  runGit,
  isMissingGit,
  splitNulFields,
  parseNameStatusZ,
  refExists,
  resolveOriginHeadBranch,
  resolveBaseRef,
  resolveMergeBase,
  normalizePaths,
  computeChangeSet
};
