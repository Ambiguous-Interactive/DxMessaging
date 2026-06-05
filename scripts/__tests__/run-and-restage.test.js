/**
 * @fileoverview Tests for scripts/run-and-restage.js.
 */

"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const { main } = require("../run-and-restage");

function runGit(cwd, args) {
  const result = childProcess.spawnSync("git", args, {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });
  if (result.error || result.status !== 0) {
    throw new Error(
      `git ${args.join(" ")} failed: ${result.error ? result.error.message : result.stderr}`
    );
  }
  return result.stdout || "";
}

function makeTempRepo() {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-run-and-restage-"));
  runGit(repo, ["init"]);
  runGit(repo, ["config", "user.email", "test@example.invalid"]);
  runGit(repo, ["config", "user.name", "Test User"]);
  fs.writeFileSync(
    path.join(repo, "file.txt"),
    ["one", "stale-anchor", "middle", "fresh-anchor", "end", ""].join("\n"),
    "utf8"
  );
  runGit(repo, ["add", "file.txt"]);
  runGit(repo, ["commit", "-m", "init"]);
  return repo;
}

function makeFs(initialFiles = {}) {
  const files = new Map(
    Object.entries(initialFiles).map(([file, content]) => [file, Buffer.from(content)])
  );

  return {
    readFileSync(file) {
      if (!files.has(file)) {
        const error = new Error(`ENOENT: no such file or directory, open '${file}'`);
        error.code = "ENOENT";
        throw error;
      }
      return Buffer.from(files.get(file));
    },
    writeFile(file, content) {
      files.set(file, Buffer.from(content));
    },
    deleteFile(file) {
      files.delete(file);
    }
  };
}

describe("run-and-restage", () => {
  test("does not stage pre-existing diffs when the wrapped command is a no-op", () => {
    const fsImpl = makeFs({ "test.js": "already changed\n" });
    const calls = [];
    const runFn = jest.fn((command, args) => {
      calls.push([command, args]);
      return { status: 0 };
    });
    const runCapturedFn = jest.fn(() => ({ status: 0, stdout: "" }));

    const status = main(["node", "scripts/no-op.js", "--", "test.js"], {
      fsImpl,
      runFn,
      runCapturedFn
    });

    expect(status).toBe(0);
    expect(calls).toEqual([["node", ["scripts/no-op.js", "test.js"]]]);
    expect(runCapturedFn.mock.calls.map(([, args]) => args)).toEqual([
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--cached", "--", "test.js"],
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--", "test.js"],
      ["ls-files", "--others", "--exclude-standard", "-z", "--", "test.js"]
    ]);
  });

  test("stages only files changed by the wrapped command", () => {
    const fsImpl = makeFs({ "a.js": "old\n", "b.js": "old\n" });
    const calls = [];
    const runFn = jest.fn((command, args) => {
      calls.push([command, args]);
      if (command === "node") {
        fsImpl.writeFile("b.js", "new\n");
      }
      return { status: 0 };
    });
    const runCapturedFn = jest.fn(() => ({ status: 0, stdout: "" }));

    const status = main(["node", "scripts/fixer.js", "--", "a.js", "b.js"], {
      fsImpl,
      runFn,
      runCapturedFn
    });

    expect(status).toBe(0);
    expect(calls).toEqual([
      ["node", ["scripts/fixer.js", "a.js", "b.js"]],
      ["git", ["add", "--", "b.js"]]
    ]);
    expect(runCapturedFn.mock.calls.map(([, args]) => args)).toEqual([
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--cached", "--", "a.js", "b.js"],
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--", "a.js", "b.js"],
      ["ls-files", "--others", "--exclude-standard", "-z", "--", "a.js", "b.js"]
    ]);
  });

  test("stages deletions made by the wrapped command", () => {
    const fsImpl = makeFs({ "generated.txt": "old\n" });
    const calls = [];
    const runFn = jest.fn((command, args) => {
      calls.push([command, args]);
      if (command === "node") {
        fsImpl.deleteFile("generated.txt");
      }
      return { status: 0 };
    });
    const runCapturedFn = jest.fn(() => ({ status: 0, stdout: "" }));

    const status = main(["node", "scripts/remove.js", "--", "generated.txt"], {
      fsImpl,
      runFn,
      runCapturedFn
    });

    expect(status).toBe(0);
    expect(calls).toEqual([
      ["node", ["scripts/remove.js", "generated.txt"]],
      ["git", ["add", "--", "generated.txt"]]
    ]);
    expect(runCapturedFn.mock.calls.map(([, args]) => args)).toEqual([
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--cached", "--", "generated.txt"],
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--", "generated.txt"],
      ["ls-files", "--others", "--exclude-standard", "-z", "--", "generated.txt"]
    ]);
  });

  test("refuses to stage a pre-existing untracked target changed by the fixer", () => {
    const repo = makeTempRepo();
    try {
      const file = path.join(repo, "untracked.js");
      fs.writeFileSync(file, "manual\n", "utf8");

      const runFn = jest.fn((command, args, options = {}) => {
        if (command === "node") {
          fs.writeFileSync(file, "manual\nfresh\n", "utf8");
          return { status: 0 };
        }
        return childProcess.spawnSync(command, args, {
          cwd: options.cwd || repo,
          encoding: "utf8",
          stdio: ["ignore", "pipe", "pipe"]
        });
      });

      const status = main(["node", "scripts/fixer.js", "--", "untracked.js"], {
        cwd: repo,
        runFn
      });

      expect(status).toBe(1);
      expect(runGit(repo, ["diff", "--cached", "--name-only", "--", "untracked.js"])).toBe("");
      expect(fs.readFileSync(file, "utf8")).toBe("manual\nfresh\n");
    } finally {
      fs.rmSync(repo, { recursive: true, force: true });
    }
  });

  test("does not stage same-file stale hunks that existed before the fixer", () => {
    const repo = makeTempRepo();
    try {
      const file = path.join(repo, "file.txt");
      fs.writeFileSync(
        file,
        ["one", "stale-anchor", "stale-only", "middle", "fresh-anchor", "end", ""].join("\n"),
        "utf8"
      );

      const runFn = jest.fn((command, args, options = {}) => {
        if (command === "node") {
          fs.writeFileSync(
            file,
            [
              "one",
              "stale-anchor",
              "stale-only",
              "middle",
              "fresh-anchor",
              "fresh-only",
              "end",
              ""
            ].join("\n"),
            "utf8"
          );
          return { status: 0 };
        }
        return childProcess.spawnSync(command, args, {
          cwd: options.cwd || repo,
          encoding: "utf8",
          stdio: ["ignore", "pipe", "pipe"]
        });
      });

      const status = main(["node", "scripts/fixer.js", "--", "file.txt"], { cwd: repo, runFn });

      expect(status).toBe(0);
      const cached = runGit(repo, ["diff", "--cached", "--", "file.txt"]);
      const unstaged = runGit(repo, ["diff", "--", "file.txt"]);
      expect(cached).toContain("+fresh-only");
      expect(cached).not.toContain("+stale-only");
      expect(unstaged).toContain("+stale-only");
    } finally {
      fs.rmSync(repo, { recursive: true, force: true });
    }
  });
});
