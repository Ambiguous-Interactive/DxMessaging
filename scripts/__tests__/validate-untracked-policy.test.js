/**
 * @fileoverview Tests for validate-untracked-policy.js
 *
 * Covers:
 * - parseArgs: --allow flags, env var, unknown flags.
 * - listUntrackedFiles: clean, untracked, gitignored, allowlisted, not-a-git-repo.
 * - Non-ASCII path handling via `git ls-files -z` (M1).
 * - Per-directory rollup when more than three files share a prefix (M4).
 * - Remediation message mentions BOTH .gitignore AND .npmignore (m4).
 * - compileGlob: simple `*`, recursive `**`, and dotted-allowlist patterns.
 * - Real-repo integration: the project's working tree should be clean.
 *
 * Temp-dir tests use `child_process.spawnSync('git', ...)` directly to set up
 * fixture repositories; the validator itself uses the project's
 * `spawnPlatformCommandSync` helper internally.
 */

"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const {
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
  reportResult
} = require("../validate-untracked-policy.js");

let tempRepos = [];

function makeTempRepo() {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), "dx-untracked-"));
  tempRepos.push(repo);

  const initResult = childProcess.spawnSync("git", ["init", "-q"], {
    cwd: repo,
    encoding: "utf8"
  });
  if (initResult.status !== 0) {
    throw new Error(
      `git init failed: status=${initResult.status} stderr=${initResult.stderr || ""}`
    );
  }
  // Configure a local identity so any future commit calls inside tests do
  // not fail on user.email/user.name lookups in CI environments.
  childProcess.spawnSync("git", ["config", "user.email", "test@example.com"], { cwd: repo });
  childProcess.spawnSync("git", ["config", "user.name", "Test User"], { cwd: repo });
  return repo;
}

function writeFile(repo, relativePath, contents) {
  const fullPath = path.join(repo, relativePath);
  fs.mkdirSync(path.dirname(fullPath), { recursive: true });
  fs.writeFileSync(fullPath, contents, "utf8");
  return fullPath;
}

afterEach(() => {
  for (const repo of tempRepos) {
    if (fs.existsSync(repo)) {
      fs.rmSync(repo, { recursive: true, force: true });
    }
  }
  tempRepos = [];
});

describe("validate-untracked-policy", () => {
  describe("ROLLUP_THRESHOLD", () => {
    test("is set to 3 (groups of >3 roll up)", () => {
      // Locked by test so a future tweak is intentional.
      expect(ROLLUP_THRESHOLD).toBe(3);
    });
  });

  describe("compileGlob", () => {
    test("matches a single-segment glob with `*`", () => {
      const re = compileGlob("foo*");
      expect(re.test("foo")).toBe(true);
      expect(re.test("foo-tmp.txt")).toBe(true);
      expect(re.test("nested/foo.txt")).toBe(false);
    });

    test("supports a recursive glob", () => {
      const re = compileGlob("a/**");
      expect(re.test("a/b/c.txt")).toBe(true);
      expect(re.test("a/b")).toBe(true);
    });

    test("escapes regex metacharacters", () => {
      const re = compileGlob("foo+bar.txt");
      expect(re.test("foo+bar.txt")).toBe(true);
      // The literal `+` in the pattern is not a wildcard; replacing it with
      // an arbitrary character must NOT match.
      expect(re.test("foo-bar.txt")).toBe(false);
    });
  });

  describe("parseArgs", () => {
    test("collects --allow=<glob> values", () => {
      const result = parseArgs(["--allow=foo*", "--allow=bar/**"]);
      expect(result.allow).toEqual(["foo*", "bar/**"]);
      expect(result.errors).toEqual([]);
    });

    test("supports --allow <glob> form", () => {
      const result = parseArgs(["--allow", "baz/*"]);
      expect(result.allow).toEqual(["baz/*"]);
    });

    test("flags an unknown argument as an error", () => {
      const result = parseArgs(["--no-such-flag"]);
      expect(result.errors).toEqual(["Unknown argument: --no-such-flag"]);
    });

    test("supports --help", () => {
      expect(parseArgs(["--help"]).help).toBe(true);
      expect(parseArgs(["-h"]).help).toBe(true);
    });
  });

  describe("parseEnvAllowList", () => {
    test("splits on colon", () => {
      expect(parseEnvAllowList("a:b:c")).toEqual(["a", "b", "c"]);
    });

    test("splits on semicolon for Windows", () => {
      expect(parseEnvAllowList("a;b;c")).toEqual(["a", "b", "c"]);
    });

    test("returns [] for empty/missing input", () => {
      expect(parseEnvAllowList(undefined)).toEqual([]);
      expect(parseEnvAllowList("")).toEqual([]);
    });

    test("trims whitespace and drops empties", () => {
      expect(parseEnvAllowList("a: : b ::")).toEqual(["a", "b"]);
    });
  });

  describe("parseUntrackedOutput", () => {
    test("splits on NUL terminator (M1: -z output)", () => {
      expect(parseUntrackedOutput("foo\0bar\0baz\0")).toEqual(["foo", "bar", "baz"]);
    });

    test("preserves whitespace inside paths", () => {
      // `-z` makes spaces inside path names just bytes; the parser must keep
      // them.
      expect(parseUntrackedOutput("with space.txt\0other.txt\0")).toEqual([
        "with space.txt",
        "other.txt"
      ]);
    });

    test("accepts a Buffer with NUL terminators", () => {
      const buf = Buffer.from("alpha\0beta\0", "utf8");
      expect(parseUntrackedOutput(buf)).toEqual(["alpha", "beta"]);
    });

    test("returns [] for empty input", () => {
      expect(parseUntrackedOutput("")).toEqual([]);
      expect(parseUntrackedOutput(null)).toEqual([]);
    });
  });

  describe("isAllowed", () => {
    test("matches a single-segment glob", () => {
      expect(isAllowed("foo.txt", ["foo*"])).toBe(true);
      expect(isAllowed("nested/foo.txt", ["foo*"])).toBe(false);
    });

    test("matches a recursive glob", () => {
      expect(isAllowed("a/b/c.txt", ["a/**"])).toBe(true);
    });

    test("returns false when no allowlist", () => {
      expect(isAllowed("foo", [])).toBe(false);
    });

    test("respects dotfile-prefix matching", () => {
      expect(isAllowed(".artifacts/log.txt", [".artifacts/**"])).toBe(true);
    });
  });

  describe("groupByFirstSegment (M4)", () => {
    test("rolls up groups larger than ROLLUP_THRESHOLD", () => {
      const result = groupByFirstSegment([
        "build/a",
        "build/b",
        "build/c",
        "build/d",
        "scratch.txt"
      ]);
      expect(result.groups).toHaveLength(1);
      expect(result.groups[0].prefix).toBe("build");
      expect(result.groups[0].count || result.groups[0].files.length).toBe(4);
      expect(result.singletons).toEqual(["scratch.txt"]);
    });

    test("keeps groups with three or fewer items as singletons", () => {
      const result = groupByFirstSegment(["build/a", "build/b", "build/c", "scratch.txt"]);
      expect(result.groups).toEqual([]);
      expect(result.singletons.sort()).toEqual(["build/a", "build/b", "build/c", "scratch.txt"]);
    });

    test("root-level files are always singletons", () => {
      const result = groupByFirstSegment(["a", "b", "c", "d", "e"]);
      expect(result.groups).toEqual([]);
      expect(result.singletons.sort()).toEqual(["a", "b", "c", "d", "e"]);
    });
  });

  describe("buildRemediationMessage (m4)", () => {
    test("file-form mentions BOTH .gitignore AND .npmignore", () => {
      const message = buildRemediationMessage("scratch.txt", false);
      expect(message).toContain(".gitignore");
      expect(message).toContain(".npmignore");
      expect(message).toContain("scratch.txt");
    });

    test("directory-form mentions BOTH .gitignore AND .npmignore", () => {
      const message = buildRemediationMessage("build-output", true, 47);
      expect(message).toContain(".gitignore");
      expect(message).toContain(".npmignore");
      expect(message).toContain("build-output/");
      expect(message).toContain("47 files");
    });
  });

  describe("listUntrackedFiles (with fixture repo)", () => {
    test("returns empty for a clean repo", () => {
      const repo = makeTempRepo();
      const result = listUntrackedFiles({ cwd: repo });
      expect(result.ok).toBe(true);
      expect(result.files).toEqual([]);
    });

    test("returns the untracked path when one exists", () => {
      const repo = makeTempRepo();
      writeFile(repo, "scratch.txt", "hello\n");
      const result = listUntrackedFiles({ cwd: repo });
      expect(result.ok).toBe(true);
      expect(result.files).toEqual(["scratch.txt"]);
    });

    test("ignores paths covered by .gitignore", () => {
      const repo = makeTempRepo();
      writeFile(repo, ".gitignore", "ignored.txt\n");
      writeFile(repo, "ignored.txt", "ignored\n");
      const result = listUntrackedFiles({ cwd: repo });
      expect(result.ok).toBe(true);
      // The .gitignore is itself untracked at this point; it must surface
      // because nothing else ignores it. ignored.txt must NOT surface.
      expect(result.files).toContain(".gitignore");
      expect(result.files).not.toContain("ignored.txt");
    });

    test("non-ASCII filenames are reported in their unescaped form (M1)", () => {
      const repo = makeTempRepo();
      // Build the name from an explicit Unicode escape so this source file
      // stays ASCII-only per the project policy. e-acute is the small
      // letter e with acute. The resulting filename is "fil" + e-acute +
      // ".txt".
      const nonAsciiName = "fil\u00e9.txt";
      writeFile(repo, nonAsciiName, "accented filename\n");
      const result = listUntrackedFiles({ cwd: repo });
      expect(result.ok).toBe(true);
      // Without `-z` and `core.quotepath=false`, git would emit
      // `"fil\303\251.txt"` (escaped). The validator MUST report the real
      // path so error messages and `--allow` matching make sense.
      expect(result.files).toEqual([nonAsciiName]);
    });

    test("filenames with spaces survive parsing (M1)", () => {
      const repo = makeTempRepo();
      writeFile(repo, "with space.txt", "spaced\n");
      const result = listUntrackedFiles({ cwd: repo });
      expect(result.ok).toBe(true);
      expect(result.files).toContain("with space.txt");
    });

    test("returns not-a-git-repository when cwd is not a repo", () => {
      const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dx-untracked-not-git-"));
      tempRepos.push(tempDir);
      const result = listUntrackedFiles({ cwd: tempDir });
      expect(result.ok).toBe(false);
      expect(result.type).toBe("not-a-git-repository");
    });

    test("returns git-not-installed when spawn fails with ENOENT", () => {
      const fakeSpawn = () => ({
        error: Object.assign(new Error("spawn ENOENT"), { code: "ENOENT" })
      });
      const result = listUntrackedFiles({ cwd: process.cwd(), spawn: fakeSpawn });
      expect(result.ok).toBe(false);
      expect(result.type).toBe("git-not-installed");
    });
  });

  describe("validate (with fixture repo)", () => {
    test("clean repo => exit 0", () => {
      const repo = makeTempRepo();
      const result = validate({ cwd: repo });
      expect(result.valid).toBe(true);
      expect(result.errors).toEqual([]);
    });

    test("untracked-and-unignored file fails and names the file", () => {
      const repo = makeTempRepo();
      writeFile(repo, "scratch.txt", "scratch\n");
      const result = validate({ cwd: repo });
      expect(result.valid).toBe(false);
      expect(result.errors).toHaveLength(1);
      expect(result.errors[0].type).toBe("untracked-path");
      expect(result.errors[0].file).toBe("scratch.txt");
      expect(result.errors[0].message).toContain("scratch.txt");
      expect(result.errors[0].message).toContain(".gitignore");
      // m4: remediation must mention .npmignore as well.
      expect(result.errors[0].message).toContain(".npmignore");
    });

    test("untracked-and-ignored file => exit 0", () => {
      const repo = makeTempRepo();
      // Commit the .gitignore so it is tracked, otherwise it is itself an
      // untracked path that would fail this test.
      writeFile(repo, ".gitignore", "ignored.txt\n");
      childProcess.spawnSync("git", ["add", ".gitignore"], { cwd: repo });
      childProcess.spawnSync("git", ["commit", "-m", "add gitignore", "-q"], { cwd: repo });
      writeFile(repo, "ignored.txt", "ignored\n");
      const result = validate({ cwd: repo });
      expect(result.valid).toBe(true);
    });

    test("--allow=<glob> covers an otherwise-failing path", () => {
      const repo = makeTempRepo();
      writeFile(repo, "foo-tmp.txt", "scratch\n");
      const result = validate({ cwd: repo, allow: ["foo*"] });
      expect(result.valid).toBe(true);
      expect(result.ignoredByAllowlist).toEqual(["foo-tmp.txt"]);
    });

    test("env-var allow merges with CLI --allow", () => {
      const repo = makeTempRepo();
      writeFile(repo, "foo-tmp.txt", "scratch\n");
      writeFile(repo, "bar-tmp.txt", "scratch\n");
      const result = validate({ cwd: repo, allow: ["foo*"], envAllow: ["bar*"] });
      expect(result.valid).toBe(true);
      expect(result.ignoredByAllowlist.sort()).toEqual(["bar-tmp.txt", "foo-tmp.txt"]);
    });

    test("not-a-git-repository surfaces as a hard error", () => {
      const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dx-untracked-not-git-2-"));
      tempRepos.push(tempDir);
      const result = validate({ cwd: tempDir });
      expect(result.valid).toBe(false);
      expect(result.errors[0].type).toBe("not-a-git-repository");
    });

    test("rollup: 4+ files in one directory becomes ONE error (M4)", () => {
      const repo = makeTempRepo();
      writeFile(repo, "build/a.txt", "a\n");
      writeFile(repo, "build/b.txt", "b\n");
      writeFile(repo, "build/c.txt", "c\n");
      writeFile(repo, "build/d.txt", "d\n");
      writeFile(repo, "build/e.txt", "e\n");
      const result = validate({ cwd: repo });
      expect(result.valid).toBe(false);
      expect(result.errors).toHaveLength(1);
      const error = result.errors[0];
      expect(error.type).toBe("untracked-directory");
      expect(error.directory).toBe("build");
      expect(error.count).toBe(5);
      expect(error.message).toContain("'build/'");
      expect(error.message).toContain("5 files");
      expect(error.message).toContain(".npmignore");
    });

    test("rollup: 3-or-fewer files stay as individual errors (M4)", () => {
      const repo = makeTempRepo();
      writeFile(repo, "build/a.txt", "a\n");
      writeFile(repo, "build/b.txt", "b\n");
      writeFile(repo, "build/c.txt", "c\n");
      const result = validate({ cwd: repo });
      expect(result.valid).toBe(false);
      expect(result.errors).toHaveLength(3);
      for (const error of result.errors) {
        expect(error.type).toBe("untracked-path");
      }
    });
  });

  describe("reportResult", () => {
    test("logs OK and returns 0 on success", () => {
      const messages = [];
      const exit = reportResult(
        { valid: true, errors: [], untracked: [], ignoredByAllowlist: [] },
        { logger: { log: (message) => messages.push(message) } }
      );
      expect(exit).toBe(0);
      expect(messages.join("\n")).toContain("OK");
    });

    test("logs file-prefixed errors and returns 1 on failure", () => {
      const messages = [];
      const exit = reportResult(
        {
          valid: false,
          errors: [{ type: "untracked-path", file: "scratch.txt", message: "violates policy" }],
          untracked: ["scratch.txt"],
          ignoredByAllowlist: []
        },
        { logger: { log: (message) => messages.push(message) } }
      );
      expect(exit).toBe(1);
      expect(messages.join("\n")).toContain("scratch.txt");
    });

    test("logs directory-rollup errors with count", () => {
      const messages = [];
      const exit = reportResult(
        {
          valid: false,
          errors: [
            {
              type: "untracked-directory",
              directory: "build",
              count: 47,
              files: [],
              message: "rollup message"
            }
          ],
          untracked: [],
          ignoredByAllowlist: []
        },
        { logger: { log: (message) => messages.push(message) } }
      );
      expect(exit).toBe(1);
      const log = messages.join("\n");
      expect(log).toContain("build");
      expect(log).toContain("47 files");
    });

    test("logs allowlist diagnostic and returns 0 when only allowlisted paths exist", () => {
      const messages = [];
      const exit = reportResult(
        {
          valid: true,
          errors: [],
          untracked: ["allowed.txt"],
          ignoredByAllowlist: ["allowed.txt"]
        },
        { logger: { log: (message) => messages.push(message) } }
      );
      expect(exit).toBe(0);
      expect(messages.join("\n")).toContain("allowlist");
    });
  });

  describe("real repository", () => {
    // m13: this test is NOT skipped by design. It only passes once the
    // working tree is fully committed (every file is either tracked or
    // covered by .gitignore). When this test fails locally, the cause is
    // almost always uncommitted work in your tree — that's the point. Do
    // not add a skip guard here.
    test("real repo is clean", () => {
      const result = validate();
      if (!result.valid) {
        // Surface the offending paths so a maintainer reading this failure
        // does not need to re-run the validator manually.
        const detail = result.errors
          .map((error) => {
            if (error.type === "untracked-directory") {
              return `[${error.type}] ${error.directory}/ (${error.count} files) ${error.message}`;
            }
            return `[${error.type}] ${error.file || ""} ${error.message}`;
          })
          .join("\n");
        throw new Error(`validate-untracked-policy failed on the real repo:\n${detail}`);
      }
      expect(result.valid).toBe(true);
    });
  });
});
