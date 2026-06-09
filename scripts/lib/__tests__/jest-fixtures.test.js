/**
 * @fileoverview Unit tests for scripts/lib/jest-fixtures.js.
 *
 * Pins the canonical behavior of the shared Jest fixture helpers
 * (`withPlatform`, `makeTempDir`, `cleanupDir`, `makeTempGitRepo`,
 * `tempDirTracker`). Dozens of suites depend on these, so a regression here
 * would silently change scratch-directory naming, platform overrides, or
 * teardown across the repo. The tests dogfood the cleanup helpers so they leak
 * no scratch directories themselves.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

const {
  withPlatform,
  makeTempDir,
  cleanupDir,
  makeTempGitRepo,
  tempDirTracker
} = require("../jest-fixtures");

/** @returns {boolean} Whether a `git` binary is available on this host. */
function gitAvailable() {
  const probe = childProcess.spawnSync("git", ["--version"], { encoding: "utf8" });
  return !probe.error && probe.status === 0;
}

/** Run a git subcommand in `cwd` and return the trimmed stdout. */
function gitConfigValue(cwd, key) {
  const result = childProcess.spawnSync("git", ["config", key], { cwd, encoding: "utf8" });
  return (result.stdout || "").trim();
}

const maybeGit = gitAvailable() ? test : test.skip;

describe("jest-fixtures", () => {
  // Track every directory a test creates directly so a failing assertion
  // never leaks scratch state across the suite.
  const leaks = [];
  afterEach(() => {
    for (const dir of leaks) {
      cleanupDir(dir);
    }
    leaks.length = 0;
  });

  describe("withPlatform", () => {
    test("reports the overridden platform inside the callback and returns its value", () => {
      const seen = withPlatform("win32", () => {
        expect(process.platform).toBe("win32");
        return "result";
      });
      expect(seen).toBe("result");
    });

    test("restores the original platform after the callback", () => {
      const original = process.platform;
      withPlatform("darwin", () => {});
      expect(process.platform).toBe(original);
    });

    test("restores the original platform even when the callback throws", () => {
      const original = process.platform;
      expect(() =>
        withPlatform("win32", () => {
          throw new Error("boom");
        })
      ).toThrow("boom");
      expect(process.platform).toBe(original);
    });

    test("nested overrides unwind to the enclosing value, then the original", () => {
      const original = process.platform;
      withPlatform("linux", () => {
        withPlatform("win32", () => {
          expect(process.platform).toBe("win32");
        });
        expect(process.platform).toBe("linux");
      });
      expect(process.platform).toBe(original);
    });
  });

  describe("makeTempDir", () => {
    test("creates a real directory whose name carries the default prefix and label", () => {
      const dir = makeTempDir("unit-default");
      leaks.push(dir);
      expect(fs.statSync(dir).isDirectory()).toBe(true);
      expect(path.dirname(dir)).toBe(os.tmpdir());
      expect(path.basename(dir)).toMatch(/^dxmsg-unit-default-/);
    });

    test("honors a custom root and prefix", () => {
      const root = makeTempDir("unit-root");
      leaks.push(root);
      const child = makeTempDir("inner", { root, prefix: "custom-" });
      expect(path.dirname(child)).toBe(root);
      expect(path.basename(child)).toMatch(/^custom-inner-/);
    });

    test("returns a distinct directory on each call", () => {
      const a = makeTempDir("unit-distinct");
      const b = makeTempDir("unit-distinct");
      leaks.push(a, b);
      expect(a).not.toBe(b);
    });
  });

  describe("cleanupDir", () => {
    test("recursively removes a populated directory", () => {
      const dir = makeTempDir("unit-cleanup");
      fs.mkdirSync(path.join(dir, "nested"), { recursive: true });
      fs.writeFileSync(path.join(dir, "nested", "file.txt"), "x");
      cleanupDir(dir);
      expect(fs.existsSync(dir)).toBe(false);
    });

    test("swallows errors for a non-existent path", () => {
      const missing = path.join(os.tmpdir(), "dxmsg-unit-missing-does-not-exist-zzz");
      expect(() => cleanupDir(missing)).not.toThrow();
    });
  });

  describe("makeTempGitRepo", () => {
    maybeGit("initializes a Git repository", () => {
      const repo = makeTempGitRepo("unit-git");
      leaks.push(repo);
      expect(fs.existsSync(path.join(repo, ".git"))).toBe(true);
    });

    maybeGit("configures a local commit identity when user is supplied", () => {
      const repo = makeTempGitRepo("unit-git-user", {
        user: { email: "fixture@example.test", name: "Fixture User" }
      });
      leaks.push(repo);
      expect(gitConfigValue(repo, "user.email")).toBe("fixture@example.test");
      expect(gitConfigValue(repo, "user.name")).toBe("Fixture User");
    });

    maybeGit("does not set a commit identity when user is omitted", () => {
      const repo = makeTempGitRepo("unit-git-no-user");
      leaks.push(repo);
      // `--local` reads only repo-scoped config, so a non-zero exit proves no
      // local identity was written, independent of any global identity.
      const localEmail = childProcess.spawnSync("git", ["config", "--local", "user.email"], {
        cwd: repo,
        encoding: "utf8"
      });
      expect(localEmail.status).not.toBe(0);
    });

    // The init-arg and failure-path tests stub `git` via spawnSync, so they run
    // (and pin the only non-trivial error handling) even where git is absent.
    test("passes 'init -q' by default and plain 'init' when quiet is false", () => {
      const initArgs = [];
      const spy = jest.spyOn(childProcess, "spawnSync").mockImplementation((command, args) => {
        initArgs.push(args);
        return { status: 0, stdout: "", stderr: "" };
      });
      try {
        cleanupDir(makeTempGitRepo("unit-git-quiet-default"));
        cleanupDir(makeTempGitRepo("unit-git-quiet-false", { quiet: false }));
      } finally {
        spy.mockRestore();
      }
      expect(initArgs).toEqual([["init", "-q"], ["init"]]);
    });

    test("throws when git init exits non-zero", () => {
      let createdDir;
      const spy = jest
        .spyOn(childProcess, "spawnSync")
        .mockImplementation((command, args, opts) => {
          createdDir = opts && opts.cwd;
          return { status: 1, stdout: "", stderr: "fatal: nope" };
        });
      try {
        expect(() => makeTempGitRepo("unit-git-init-fail")).toThrow(
          /"git init" exited with status 1/
        );
      } finally {
        spy.mockRestore();
        cleanupDir(createdDir);
      }
    });

    test("throws when git cannot be spawned", () => {
      let createdDir;
      const spy = jest
        .spyOn(childProcess, "spawnSync")
        .mockImplementation((command, args, opts) => {
          createdDir = opts && opts.cwd;
          return { error: new Error("spawn git ENOENT"), status: null };
        });
      try {
        expect(() => makeTempGitRepo("unit-git-missing")).toThrow(/Failed to run "git init"/);
      } finally {
        spy.mockRestore();
        cleanupDir(createdDir);
      }
    });
  });

  describe("tempDirTracker", () => {
    test("registers created directories and removes them all on cleanup", () => {
      const tracker = tempDirTracker();
      const a = tracker.make("unit-track-a");
      const b = tracker.make("unit-track-b");
      expect(fs.existsSync(a)).toBe(true);
      expect(fs.existsSync(b)).toBe(true);

      tracker.cleanup();
      expect(fs.existsSync(a)).toBe(false);
      expect(fs.existsSync(b)).toBe(false);
    });

    test("merges tracker defaults under each call's own options", () => {
      const root = makeTempDir("unit-track-root");
      leaks.push(root);
      const tracker = tempDirTracker({ root, prefix: "tracked-" });
      const dir = tracker.make("entry");
      try {
        expect(path.dirname(dir)).toBe(root);
        expect(path.basename(dir)).toMatch(/^tracked-entry-/);
      } finally {
        tracker.cleanup();
      }
    });

    test("cleanup is idempotent", () => {
      const tracker = tempDirTracker();
      const dir = tracker.make("unit-track-idempotent");
      tracker.cleanup();
      expect(() => tracker.cleanup()).not.toThrow();
      expect(fs.existsSync(dir)).toBe(false);
    });

    maybeGit("makeGitRepo registers and initializes a repository", () => {
      const tracker = tempDirTracker();
      const repo = tracker.makeGitRepo("unit-track-git");
      try {
        expect(fs.existsSync(path.join(repo, ".git"))).toBe(true);
      } finally {
        tracker.cleanup();
      }
      expect(fs.existsSync(repo)).toBe(false);
    });

    // The consumer-representative path: tracker prefix default + per-call user.
    maybeGit("makeGitRepo applies tracker defaults and the user identity", () => {
      const tracker = tempDirTracker({ prefix: "tracked-" });
      const repo = tracker.makeGitRepo("g", {
        user: { email: "tracked@example.test", name: "Tracked User" }
      });
      try {
        expect(path.basename(repo)).toMatch(/^tracked-g-/);
        expect(fs.existsSync(path.join(repo, ".git"))).toBe(true);
        expect(gitConfigValue(repo, "user.email")).toBe("tracked@example.test");
        expect(gitConfigValue(repo, "user.name")).toBe("Tracked User");
      } finally {
        tracker.cleanup();
      }
    });
  });
});
