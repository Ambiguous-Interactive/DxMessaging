/**
 * @fileoverview Change-set unit tests for scripts/lib/changed-files.js
 * (design 7.2). Drives `computeChangeSet` with an injected fake `runGitFn` so no
 * real git/filesystem is touched, and pins:
 *
 *   - Union math across committed-range u staged u unstaged u untracked, deduped,
 *     sorted, POSIX-normalized.
 *   - Deletions (`D`) are EXCLUDED (encodes verified pre-commit-skips-deletions).
 *   - Renames/copies keep ONLY the new path (`-z` name-status pairwise consume).
 *   - Base-resolution priority: baseOverride -> origin/HEAD -> origin/master ->
 *     origin/main -> local master -> main -> none (working-tree-only). `@{push}`
 *     is NEVER consulted (regression guard against the under-reporting base).
 *   - Fresh-clone / detached-HEAD / first-push: every base ref fails ->
 *     working-tree-only scope, no throw.
 *   - Missing `git` binary (spawn ENOENT) -> THROWS (hard error), distinct from
 *     the soft no-base condition.
 *   - `-z` NUL parsing of paths with spaces / unicode.
 *   - Two-pass decision inputs: `sources.committed` populated only when a base +
 *     merge-base resolve; empty otherwise (drives preflight.js's single-vs-two
 *     pass dedupe).
 *   - scope=worktree SKIPS base resolution + the committed range entirely (it
 *     issues only the three working-tree git commands, never rev-parse /
 *     merge-base).
 *
 * Node stdlib only; no shell-outs.
 */

"use strict";

const changedFiles = require("../lib/changed-files");
const { computeChangeSet } = changedFiles;

/**
 * Build a fake `runGitFn` from a declarative spec. Each git invocation is keyed
 * by its joined args; the spec maps a matching predicate to a spawnSync-shaped
 * result. Unspecified commands default to a clean success with empty stdout
 * (status 0), so the working-tree collectors never throw unless asked to.
 *
 * The fake also RECORDS every invocation's args so a test can assert which git
 * commands were (and were not) issued.
 *
 * @param {object} spec
 * @param {Array<{when: (args: string[]) => boolean, result: object}>} [spec.rules]
 * @param {object} [spec.fallback] result for unmatched commands.
 * @returns {{ fn: Function, calls: string[][] }}
 */
function makeFakeGit(spec = {}) {
  const rules = spec.rules || [];
  const fallback = spec.fallback || { status: 0, stdout: "" };
  const calls = [];
  const fn = (args) => {
    calls.push(args.slice());
    for (const rule of rules) {
      if (rule.when(args)) {
        return rule.result;
      }
    }
    return fallback;
  };
  return { fn, calls };
}

const ENOENT = () => ({ error: Object.assign(new Error("spawn git ENOENT"), { code: "ENOENT" }) });

// Helpers to recognize the exact git command shapes changed-files.js issues.
const isRevParse = (args, ref) =>
  args[0] === "rev-parse" &&
  args.includes("--verify") &&
  args.includes("--quiet") &&
  args.includes(ref);
const isMergeBase = (args) => args[0] === "merge-base";
const isCommittedDiff = (args) =>
  args[0] === "diff" &&
  args.includes("--name-status") &&
  args.includes("HEAD") &&
  !args.includes("--cached");
const isStagedDiff = (args) => args[0] === "diff" && args.includes("--cached");
const isUnstagedDiff = (args) =>
  args[0] === "diff" &&
  args.includes("--name-status") &&
  !args.includes("--cached") &&
  !args.includes("HEAD");
const isUntracked = (args) => args[0] === "ls-files";

describe("computeChangeSet (change-set unit, fake git)", () => {
  test("unions committed/staged/unstaged/untracked, dedupes, sorts, POSIX-normalizes", () => {
    const { fn } = makeFakeGit({
      rules: [
        {
          when: (a) => isRevParse(a, "refs/remotes/origin/HEAD"),
          result: { status: 0, stdout: "" }
        },
        { when: isMergeBase, result: { status: 0, stdout: "abc123\n" } },
        // committed: an Added + a Modified (with a backslash separator path).
        {
          when: isCommittedDiff,
          result: { status: 0, stdout: "A\u0000src/a.cs\u0000M\u0000nested\\b.cs\u0000" }
        },
        // staged: duplicate of a.cs (dedupe) + a new path.
        { when: isStagedDiff, result: { status: 0, stdout: "M\u0000src/a.cs\u0000A\u0000docs/c.md\u0000" } },
        // unstaged: another file.
        { when: isUnstagedDiff, result: { status: 0, stdout: "M\u0000e.json\u0000" } },
        // untracked: plain path fields, no status prefix.
        { when: isUntracked, result: { status: 0, stdout: "f.txt\u0000" } }
      ]
    });

    const cs = computeChangeSet({ runGitFn: fn, scope: "full" });

    expect(cs.files).toEqual(["docs/c.md", "e.json", "f.txt", "nested/b.cs", "src/a.cs"]);
    expect(cs.base).toBe("origin/HEAD");
    expect(cs.mergeBase).toBe("abc123");
    expect(cs.scope).toBe("full");
    expect(cs.sources.committed).toEqual(["nested/b.cs", "src/a.cs"]);
    expect(cs.sources.staged).toEqual(["docs/c.md", "src/a.cs"]);
    expect(cs.sources.unstaged).toEqual(["e.json"]);
    expect(cs.sources.untracked).toEqual(["f.txt"]);
  });

  test("deletions (D) are excluded from every source", () => {
    const { fn } = makeFakeGit({
      rules: [
        {
          when: (a) => isRevParse(a, "refs/remotes/origin/HEAD"),
          result: { status: 0, stdout: "" }
        },
        { when: isMergeBase, result: { status: 0, stdout: "base\n" } },
        // A deletion must not appear, but the Add alongside it must.
        {
          when: isCommittedDiff,
          result: { status: 0, stdout: "D\u0000gone.cs\u0000A\u0000kept.cs\u0000" }
        },
        { when: isStagedDiff, result: { status: 0, stdout: "D\u0000staged-gone.md\u0000" } }
      ]
    });

    const cs = computeChangeSet({ runGitFn: fn, scope: "full" });
    expect(cs.files).toEqual(["kept.cs"]);
    expect(cs.files).not.toContain("gone.cs");
    expect(cs.files).not.toContain("staged-gone.md");
    expect(cs.sources.committed).toEqual(["kept.cs"]);
    expect(cs.sources.staged).toEqual([]);
  });

  test("renames/copies keep ONLY the new path (-z pairwise consume)", () => {
    const { fn } = makeFakeGit({
      rules: [
        {
          when: (a) => isRevParse(a, "refs/remotes/origin/HEAD"),
          result: { status: 0, stdout: "" }
        },
        { when: isMergeBase, result: { status: 0, stdout: "base\n" } },
        // R100 old new ; C075 srcCopy newCopy ; then a plain M.
        {
          when: isCommittedDiff,
          result: {
            status: 0,
            stdout: "R100\u0000old/path.cs\u0000new/path.cs\u0000" + "C075\u0000orig.md\u0000copy.md\u0000" + "M\u0000plain.json\u0000"
          }
        }
      ]
    });

    const cs = computeChangeSet({ runGitFn: fn, scope: "full" });
    expect(cs.sources.committed).toEqual(["copy.md", "new/path.cs", "plain.json"]);
    expect(cs.files).not.toContain("old/path.cs");
    expect(cs.files).not.toContain("orig.md");
  });

  test("base priority: baseOverride wins and short-circuits origin/HEAD", () => {
    const { fn, calls } = makeFakeGit({
      rules: [
        { when: (a) => isRevParse(a, "release/1.x"), result: { status: 0, stdout: "" } },
        { when: isMergeBase, result: { status: 0, stdout: "mb\n" } }
      ]
    });
    const cs = computeChangeSet({ runGitFn: fn, baseOverride: "release/1.x", scope: "full" });
    expect(cs.base).toBe("release/1.x");
    // origin/HEAD must never be probed once the override resolves.
    const probedOriginHead = calls.some((a) => isRevParse(a, "refs/remotes/origin/HEAD"));
    expect(probedOriginHead).toBe(false);
  });

  test("base priority: falls through origin/master then origin/main then local", () => {
    // origin/HEAD missing, origin/master missing, origin/main resolves.
    const { fn } = makeFakeGit({
      rules: [
        {
          when: (a) => isRevParse(a, "refs/remotes/origin/HEAD"),
          result: { status: 1, stdout: "" }
        },
        { when: (a) => isRevParse(a, "origin/master"), result: { status: 1, stdout: "" } },
        { when: (a) => isRevParse(a, "origin/main"), result: { status: 0, stdout: "" } },
        { when: isMergeBase, result: { status: 0, stdout: "mb\n" } }
      ]
    });
    const cs = computeChangeSet({ runGitFn: fn, scope: "full" });
    expect(cs.base).toBe("origin/main");
  });

  test("base priority: NEVER consults @{push} (regression guard)", () => {
    const { fn, calls } = makeFakeGit({
      rules: [
        {
          when: (a) => isRevParse(a, "refs/remotes/origin/HEAD"),
          result: { status: 0, stdout: "" }
        },
        { when: isMergeBase, result: { status: 0, stdout: "mb\n" } }
      ]
    });
    computeChangeSet({ runGitFn: fn, scope: "full" });
    const consultedPush = calls.some((args) => args.some((a) => String(a).includes("@{push}")));
    expect(consultedPush).toBe(false);
  });

  test("fresh-clone / detached-HEAD / first-push: no base resolves -> working-tree-only, no throw", () => {
    // Every base ref fails; working-tree collectors still succeed.
    const { fn, calls } = makeFakeGit({
      rules: [
        { when: (a) => a[0] === "rev-parse", result: { status: 1, stdout: "" } },
        { when: isStagedDiff, result: { status: 0, stdout: "A\u0000new.cs\u0000" } },
        { when: isUnstagedDiff, result: { status: 0, stdout: "" } },
        { when: isUntracked, result: { status: 0, stdout: "untracked.txt\u0000" } }
      ]
    });

    let cs;
    expect(() => {
      cs = computeChangeSet({ runGitFn: fn, scope: "full" });
    }).not.toThrow();
    expect(cs.base).toBeNull();
    expect(cs.mergeBase).toBeNull();
    expect(cs.sources.committed).toEqual([]);
    expect(cs.files).toEqual(["new.cs", "untracked.txt"]);
    // No committed-range diff is attempted when there is no merge base.
    expect(calls.some(isCommittedDiff)).toBe(false);
  });

  test("a resolved base with NO common ancestor (merge-base fails) -> committed range skipped, no throw", () => {
    const { fn, calls } = makeFakeGit({
      rules: [
        {
          when: (a) => isRevParse(a, "refs/remotes/origin/HEAD"),
          result: { status: 0, stdout: "" }
        },
        { when: isMergeBase, result: { status: 1, stdout: "" } },
        { when: isStagedDiff, result: { status: 0, stdout: "M\u0000x.cs\u0000" } }
      ]
    });
    const cs = computeChangeSet({ runGitFn: fn, scope: "full" });
    expect(cs.base).toBe("origin/HEAD");
    expect(cs.mergeBase).toBeNull();
    expect(cs.sources.committed).toEqual([]);
    expect(calls.some(isCommittedDiff)).toBe(false);
    expect(cs.files).toEqual(["x.cs"]);
  });

  test("missing git binary (ENOENT) -> THROWS (hard error)", () => {
    const { fn } = makeFakeGit({ fallback: ENOENT() });
    expect(() => computeChangeSet({ runGitFn: fn, scope: "full" })).toThrow(
      /unable to spawn git|git/i
    );
  });

  test("ENOENT during a working-tree collector also throws (worktree scope)", () => {
    // worktree scope skips base resolution; the first working-tree command
    // surfaces the missing-git hard error.
    const { fn } = makeFakeGit({ fallback: ENOENT() });
    expect(() => computeChangeSet({ runGitFn: fn, scope: "worktree" })).toThrow();
  });

  test("-z NUL parsing handles paths with spaces and unicode", () => {
    const { fn } = makeFakeGit({
      rules: [
        { when: (a) => a[0] === "rev-parse", result: { status: 1, stdout: "" } },
        {
          when: isStagedDiff,
          result: { status: 0, stdout: "A\u0000docs/with space.md\u0000M\u0000süd/é.cs\u0000" }
        }
      ]
    });
    const cs = computeChangeSet({ runGitFn: fn, scope: "full" });
    expect(cs.sources.staged).toContain("docs/with space.md");
    expect(cs.sources.staged).toContain("süd/é.cs");
  });

  test("scope=worktree SKIPS base resolution and the committed range entirely", () => {
    const { fn, calls } = makeFakeGit({
      rules: [
        { when: isStagedDiff, result: { status: 0, stdout: "M\u0000only.cs\u0000" } },
        { when: isUnstagedDiff, result: { status: 0, stdout: "" } },
        { when: isUntracked, result: { status: 0, stdout: "" } }
      ]
    });

    const cs = computeChangeSet({ runGitFn: fn, scope: "worktree" });

    expect(cs.scope).toBe("worktree");
    expect(cs.base).toBeNull();
    expect(cs.mergeBase).toBeNull();
    expect(cs.sources.committed).toEqual([]);
    expect(cs.files).toEqual(["only.cs"]);

    // The three working-tree commands are issued; rev-parse / merge-base / the
    // committed diff are NEVER issued. This is the property the advisory Stop
    // hook relies on to stay fast on a many-commit branch.
    expect(calls.some((a) => a[0] === "rev-parse")).toBe(false);
    expect(calls.some(isMergeBase)).toBe(false);
    expect(calls.some(isCommittedDiff)).toBe(false);
    expect(calls.some(isStagedDiff)).toBe(true);
    expect(calls.some(isUnstagedDiff)).toBe(true);
    expect(calls.some(isUntracked)).toBe(true);
  });

  test("explicit range skips base discovery and uses rangeFrom..rangeTo", () => {
    const { fn, calls } = makeFakeGit({
      rules: [
        {
          when: (a) => a[0] === "diff" && a.includes("old") && a.includes("new"),
          result: { status: 0, stdout: "M\u0000range.cs\u0000" }
        },
        { when: isStagedDiff, result: { status: 0, stdout: "" } },
        { when: isUnstagedDiff, result: { status: 0, stdout: "" } },
        { when: isUntracked, result: { status: 0, stdout: "" } }
      ]
    });

    const cs = computeChangeSet({
      runGitFn: fn,
      rangeFrom: "old",
      rangeTo: "new",
      scope: "full"
    });

    expect(cs.scope).toBe("range");
    expect(cs.base).toBe("old");
    expect(cs.mergeBase).toBe("old");
    expect(cs.rangeTo).toBe("new");
    expect(cs.sources.committed).toEqual(["range.cs"]);
    expect(calls.some((a) => a[0] === "rev-parse")).toBe(false);
    expect(calls.some(isMergeBase)).toBe(false);
    expect(calls).toContainEqual([
      "diff",
      "--name-status",
      "--diff-filter=ACMR",
      "-z",
      "old",
      "new"
    ]);
  });

  test("noWorktree skips staged, unstaged, and untracked scans", () => {
    const { fn, calls } = makeFakeGit({
      rules: [
        {
          when: (a) => a[0] === "diff" && a.includes("old") && a.includes("new"),
          result: { status: 0, stdout: "M\u0000range.cs\u0000" }
        }
      ]
    });

    const cs = computeChangeSet({
      runGitFn: fn,
      rangeFrom: "old",
      rangeTo: "new",
      noWorktree: true
    });

    expect(cs.files).toEqual(["range.cs"]);
    expect(cs.sources.staged).toEqual([]);
    expect(cs.sources.unstaged).toEqual([]);
    expect(cs.sources.untracked).toEqual([]);
    expect(calls.some(isStagedDiff)).toBe(false);
    expect(calls.some(isUntracked)).toBe(false);
    expect(calls).toEqual([
      ["diff", "--name-status", "--diff-filter=ACMR", "-z", "old", "new"]
    ]);
  });

  test("two-pass decision inputs: committed populated ONLY when base + merge-base resolve", () => {
    // With a base + merge-base + committed content, sources.committed is
    // non-empty (preflight.js then runs BOTH passes when working files exist).
    const withCommitted = makeFakeGit({
      rules: [
        {
          when: (a) => isRevParse(a, "refs/remotes/origin/HEAD"),
          result: { status: 0, stdout: "" }
        },
        { when: isMergeBase, result: { status: 0, stdout: "mb\n" } },
        { when: isCommittedDiff, result: { status: 0, stdout: "M\u0000committed.cs\u0000" } },
        { when: isStagedDiff, result: { status: 0, stdout: "M\u0000working.cs\u0000" } }
      ]
    });
    const a = computeChangeSet({ runGitFn: withCommitted.fn, scope: "full" });
    expect(a.sources.committed.length).toBeGreaterThan(0);
    expect(
      [...a.sources.staged, ...a.sources.unstaged, ...a.sources.untracked].length
    ).toBeGreaterThan(0);

    // Worktree scope -> committed always empty (single pass only).
    const worktreeOnly = makeFakeGit({
      rules: [{ when: isStagedDiff, result: { status: 0, stdout: "M\u0000working.cs\u0000" } }]
    });
    const b = computeChangeSet({ runGitFn: worktreeOnly.fn, scope: "worktree" });
    expect(b.sources.committed).toEqual([]);
  });

  test("an unrecognized scope string falls back to full", () => {
    const { fn } = makeFakeGit({
      rules: [{ when: (a) => a[0] === "rev-parse", result: { status: 1, stdout: "" } }]
    });
    const cs = computeChangeSet({ runGitFn: fn, scope: "bogus" });
    expect(cs.scope).toBe("full");
  });
});
