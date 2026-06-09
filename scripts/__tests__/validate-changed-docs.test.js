"use strict";

const fs = require("fs");
const path = require("path");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const {
  getChangedDocEntries,
  getChangedDocFiles,
  runChangedDocValidators
} = require("../validate-changed-docs");

describe("validate-changed-docs", () => {
  test("collects tracked and untracked changed documentation files", () => {
    const calls = [];
    const runGitFn = (args) => {
      calls.push(args.join(" "));
      if (args[0] === "diff" && args.includes("--cached")) {
        return {
          status: 0,
          stdout: ["docs/a.md", "Runtime/Foo.cs", "package.json"].join("\n")
        };
      }
      if (args[0] === "diff") {
        return { status: 0, stdout: "docs/worktree.md\n" };
      }
      if (args[0] === "ls-files") {
        return {
          status: 0,
          stdout: ["docs/new.markdown", "scratch.txt"].join("\n")
        };
      }
      return { status: 0, stdout: "" };
    };

    expect(getChangedDocFiles({ runGitFn })).toEqual([
      "Runtime/Foo.cs",
      "docs/a.md",
      "docs/new.markdown",
      "docs/worktree.md"
    ]);
    expect(calls.some((call) => call.includes("ls-files --others"))).toBe(true);
  });

  test("throws when git metadata cannot be read", () => {
    const runGitFn = (args) => {
      if (args[0] === "diff" && args.includes("--cached")) {
        return { status: 128, stdout: "", stderr: "fatal: not a git repository" };
      }
      return { status: 0, stdout: "" };
    };

    expect(() => getChangedDocFiles({ runGitFn })).toThrow(/git diff --cached/);
  });

  test("reads staged entries from the index", () => {
    const runGitFn = (args) => {
      if (args[0] === "diff" && args.includes("--cached")) {
        return { status: 0, stdout: "docs/staged.md\n" };
      }
      if (args[0] === "diff") {
        return { status: 0, stdout: "" };
      }
      if (args[0] === "ls-files") {
        return { status: 0, stdout: "" };
      }
      if (args[0] === "show") {
        return { status: 0, stdout: "# Staged\n" };
      }
      return { status: 0, stdout: "" };
    };

    expect(getChangedDocEntries({ runGitFn })).toEqual([
      {
        file: "docs/staged.md",
        source: "staged",
        content: "# Staged\n"
      }
    ]);
  });

  test("runs shared validators and reports ASCII violations before hook-time", () => {
    const emDash = String.fromCodePoint(0x2014);
    const tempDir = makeTempDir("changed-docs");
    const target = path.join(tempDir, "failing.md");
    fs.writeFileSync(target, `# Title\n\nBad${emDash}dash.\n`, "utf8");

    try {
      const stderrSpy = jest.spyOn(process.stderr, "write").mockImplementation(() => true);
      const stdoutSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
      try {
        const result = runChangedDocValidators({
          files: [target]
        });

        expect(result.totalViolations).toBeGreaterThan(0);
        expect(result.results.get("validate-docs-ascii").violations[0].codepoint).toBe(0x2014);
      } finally {
        stderrSpy.mockRestore();
        stdoutSpy.mockRestore();
      }
    } finally {
      cleanupDir(tempDir);
    }
  });

  test("reports staged ASCII violations even when the worktree file is clean", () => {
    const emDash = String.fromCodePoint(0x2014);
    const tempDir = makeTempDir("changed-docs");
    fs.mkdirSync(path.join(tempDir, "docs"), { recursive: true });
    fs.writeFileSync(path.join(tempDir, "docs", "failing.md"), "# Title\n\nClean dash.\n", "utf8");

    const runGitFn = (args) => {
      if (args[0] === "diff" && args.includes("--cached")) {
        return { status: 0, stdout: "docs/failing.md\n" };
      }
      if (args[0] === "diff") {
        return { status: 0, stdout: "docs/failing.md\n" };
      }
      if (args[0] === "ls-files") {
        return { status: 0, stdout: "" };
      }
      if (args[0] === "show") {
        return { status: 0, stdout: `# Title\n\nBad${emDash}dash.\n` };
      }
      return { status: 0, stdout: "" };
    };

    try {
      const stderrSpy = jest.spyOn(process.stderr, "write").mockImplementation(() => true);
      const stdoutSpy = jest.spyOn(process.stdout, "write").mockImplementation(() => true);
      try {
        const result = runChangedDocValidators({
          repoRoot: tempDir,
          runGitFn
        });

        expect(result.totalViolations).toBeGreaterThan(0);
        expect(result.results.get("validate-docs-ascii").violations[0].codepoint).toBe(0x2014);
      } finally {
        stderrSpy.mockRestore();
        stdoutSpy.mockRestore();
      }
    } finally {
      cleanupDir(tempDir);
    }
  });
});
