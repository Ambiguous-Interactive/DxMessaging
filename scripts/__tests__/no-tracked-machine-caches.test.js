"use strict";

// cspell:words lscache

// Prevention guard: machine-specific, auto-generated cache files must never be
// committed. The concrete case that motivated this guard is the C# Dev Kit
// language-service cache (*.lscache and its paired *.lscache.meta). Those files
// are per-developer-machine artifacts; checking them in pollutes diffs, leaks
// local paths, and causes spurious churn. They are ignored via .gitignore, but
// a string in .gitignore is not proof of effective behavior -- a `git add -f`,
// a future .gitignore regression, or an un-ignored re-add could still land them
// in the index. This suite asserts the EFFECTIVE git behavior instead of
// parsing .gitignore text:
//   1. No such file is currently tracked (`git ls-files`).
//   2. The ignore genuinely matches representative synthetic paths
//      (`git check-ignore`), so the protection is real, not just declared.
//
// The patterns here are intentionally narrow (specific generated-cache
// extensions) so the guard cannot false-positive on legitimate files. To extend
// coverage to another class of machine-specific generated cache, add its
// concrete glob to TRACKED_CACHE_GLOBS and a representative path to
// REPRESENTATIVE_IGNORED_PATHS below -- keep both lists concrete and avoid broad
// globs (e.g. never `*.cache`) that could swallow legitimate tracked files.

const childProcess = require("child_process");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

// Globs handed to `git ls-files` to detect any tracked machine-specific cache.
// Each entry MUST be specific to an auto-generated, per-machine artifact.
const TRACKED_CACHE_GLOBS = ["*.lscache", "*.lscache.meta"];

// Representative synthetic paths used to prove the ignore rules actually fire.
// These need not exist on disk; `git check-ignore` evaluates the rules against
// the path string. Each path mirrors a real generated-cache location.
const REPRESENTATIVE_IGNORED_PATHS = [
  "SourceGenerators/Probe/Probe.csproj.lscache",
  "SourceGenerators/Probe/Probe.csproj.lscache.meta"
];

// A control path that the ignore RULES must NOT match. If an over-broad rule
// (e.g. `scripts/`) ever crept in, the ignore assertions above could pass
// vacuously while real source files got hidden; this control catches that.
// The control test evaluates ignore rules with `git check-ignore --no-index`
// (see below): without --no-index, git short-circuits check-ignore for any
// path already in the index and reports it as not-ignored regardless of the
// rules, which would make this control vacuous once the file is committed.
const REPRESENTATIVE_TRACKED_PATH = "scripts/__tests__/no-tracked-machine-caches.test.js";

// Run a git subcommand from the repo root, capturing status/stdout/stderr.
// Treats a git that fails to spawn or is signal-killed as a HARD failure (never
// skip-pass): a prevention guard that silently no-ops when git is missing would
// give false assurance. The only tolerated non-zero exit is `git check-ignore`
// returning 1, which is a legitimate "not ignored" answer the caller inspects.
function runGit(args) {
  let result;
  try {
    result = childProcess.spawnSync("git", args, {
      cwd: REPO_ROOT,
      encoding: "utf8"
    });
  } catch (error) {
    throw new Error(
      `Failed to invoke \`git ${args.join(" ")}\` from ${REPO_ROOT}: ${error.message}. ` +
        "git must be available for the machine-cache prevention guard to run."
    );
  }

  if (result.error) {
    throw new Error(
      `Failed to invoke \`git ${args.join(" ")}\` from ${REPO_ROOT}: ${result.error.message}. ` +
        "git must be available for the machine-cache prevention guard to run."
    );
  }

  if (result.status === null) {
    throw new Error(
      `\`git ${args.join(" ")}\` was terminated by signal ${result.signal} (no exit code). ` +
        "git must run to completion for the machine-cache prevention guard."
    );
  }

  return result;
}

// Sanity-check that we are operating inside a real git work tree before drawing
// any conclusions from git output. Outside a checkout (e.g. a `git archive`
// extract) the guard cannot evaluate tracking or ignore rules, so it must fail
// loudly rather than report a false green.
function assertInsideWorkTree() {
  const result = runGit(["rev-parse", "--is-inside-work-tree"]);
  const inside = result.status === 0 && String(result.stdout).trim() === "true";
  if (!inside) {
    throw new Error(
      `Not inside a git work tree at ${REPO_ROOT} ` +
        `(rev-parse exit ${result.status}, stdout ${JSON.stringify(String(result.stdout).trim())}). ` +
        "The machine-cache prevention guard requires a real git checkout."
    );
  }
}

describe("no tracked machine-specific cache files", () => {
  beforeAll(() => {
    assertInsideWorkTree();
  });

  test("no *.lscache / *.lscache.meta files are tracked", () => {
    const result = runGit(["ls-files", "--", ...TRACKED_CACHE_GLOBS]);
    expect(result.status).toBe(0);

    const trackedPaths = String(result.stdout)
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter((line) => line.length > 0);

    if (trackedPaths.length > 0) {
      throw new Error(
        "Machine-specific generated cache files are tracked by git and must be removed " +
          "from version control (gitignored, not committed):\n" +
          trackedPaths.map((p) => `  - ${p}`).join("\n") +
          "\n\nThese are auto-generated, per-machine artifacts (e.g. C# Dev Kit " +
          "language-service caches). Untrack them with `git rm --cached <path>` and " +
          "ensure the matching .gitignore rule stays in place."
      );
    }

    expect(trackedPaths).toEqual([]);
  });

  test.each(REPRESENTATIVE_IGNORED_PATHS)(
    "representative generated-cache path is genuinely ignored by git: %s",
    (candidate) => {
      const result = runGit(["check-ignore", "-q", "--", candidate]);
      // Exit 0 => path is ignored. Exit 1 => NOT ignored (assertion failure).
      // Any other exit was already converted to a thrown error by runGit.
      if (result.status !== 0) {
        throw new Error(
          `git does NOT ignore "${candidate}" (check-ignore exit ${result.status}). ` +
            "This machine-specific generated cache pattern must be covered by .gitignore " +
            "so the file class cannot be committed. Add or restore the matching ignore rule."
        );
      }
      expect(result.status).toBe(0);
    }
  );

  test("control path is NOT ignored (ignore rules are not over-broad)", () => {
    // Evaluate the ignore RULES against the control path with --no-index. The
    // flag is load-bearing: by default `git check-ignore` short-circuits for any
    // path already tracked in the index and reports exit 1 (not ignored) without
    // consulting the rules, so once this test file is committed a plain
    // check-ignore would pass vacuously even if .gitignore became over-broad
    // (e.g. `scripts/`). --no-index forces git to apply the rules to the path
    // string regardless of index membership, so an over-broad rule is caught.
    const result = runGit(["check-ignore", "-q", "--no-index", "--", REPRESENTATIVE_TRACKED_PATH]);
    // For a path the rules do not match, check-ignore exits 1. Exit 0 means a
    // too-greedy ignore rule matches a legitimate source path and would hide it.
    if (result.status === 0) {
      throw new Error(
        `An ignore rule matches the legitimate path "${REPRESENTATIVE_TRACKED_PATH}". ` +
          "An ignore rule is too broad and would hide real source files. Narrow the rule " +
          "so it matches only machine-specific generated caches."
      );
    }
    expect(result.status).toBe(1);
  });
});
