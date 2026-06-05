/**
 * @fileoverview Tests for scripts/run-and-stage.js.
 */

"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const { main } = require("../run-and-stage");

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
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-run-and-stage-"));
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
    }
  };
}

describe("run-and-stage", () => {
  test("does not stage pre-existing diffs when the wrapped command is a no-op", () => {
    const fsImpl = makeFs({ "llms.txt": "already changed\n" });
    const calls = [];
    const runFn = jest.fn((command, args) => {
      calls.push([command, args]);
      return { status: 0 };
    });
    const runCapturedFn = jest.fn(() => ({ status: 0, stdout: "" }));

    const status = main(["node", "scripts/no-op.js", "--", "llms.txt"], {
      fsImpl,
      runFn,
      runCapturedFn
    });

    expect(status).toBe(0);
    expect(calls).toEqual([["node", ["scripts/no-op.js"]]]);
    expect(runCapturedFn.mock.calls.map(([, args]) => args)).toEqual([
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--cached", "--", "llms.txt"],
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--", "llms.txt"],
      ["ls-files", "--others", "--exclude-standard", "-z", "--", "llms.txt"]
    ]);
  });

  test("stages only generated files changed by the wrapped command", () => {
    const fsImpl = makeFs({ "banner.svg": "old\n", "llms.txt": "old\n" });
    const calls = [];
    const runFn = jest.fn((command, args) => {
      calls.push([command, args]);
      if (command === "node") {
        fsImpl.writeFile("banner.svg", "new\n");
      }
      return { status: 0 };
    });
    const runCapturedFn = jest.fn(() => ({ status: 0, stdout: "" }));

    const status = main(["node", "scripts/generate.js", "--", "banner.svg", "llms.txt"], {
      fsImpl,
      runFn,
      runCapturedFn
    });

    expect(status).toBe(0);
    expect(calls).toEqual([
      ["node", ["scripts/generate.js"]],
      ["git", ["add", "--", "banner.svg"]]
    ]);
    expect(runCapturedFn.mock.calls.map(([, args]) => args)).toEqual([
      [
        "diff",
        "--binary",
        "--no-ext-diff",
        "--unified=0",
        "--cached",
        "--",
        "banner.svg",
        "llms.txt"
      ],
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--", "banner.svg", "llms.txt"],
      ["ls-files", "--others", "--exclude-standard", "-z", "--", "banner.svg", "llms.txt"]
    ]);
  });

  test("stages generated files that did not exist before the wrapped command", () => {
    const fsImpl = makeFs({});
    const calls = [];
    const runFn = jest.fn((command, args) => {
      calls.push([command, args]);
      if (command === "node") {
        fsImpl.writeFile("llms.txt", "generated\n");
      }
      return { status: 0 };
    });
    const runCapturedFn = jest.fn(() => ({ status: 0, stdout: "" }));

    const status = main(["node", "scripts/generate.js", "--", "llms.txt"], {
      fsImpl,
      runFn,
      runCapturedFn
    });

    expect(status).toBe(0);
    expect(calls).toEqual([
      ["node", ["scripts/generate.js"]],
      ["git", ["add", "--", "llms.txt"]]
    ]);
    expect(runCapturedFn.mock.calls.map(([, args]) => args)).toEqual([
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--cached", "--", "llms.txt"],
      ["diff", "--binary", "--no-ext-diff", "--unified=0", "--", "llms.txt"],
      ["ls-files", "--others", "--exclude-standard", "-z", "--", "llms.txt"]
    ]);
  });

  test("refuses to stage a pre-existing untracked target changed by generation", () => {
    const repo = makeTempRepo();
    try {
      const file = path.join(repo, "untracked.txt");
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

      const status = main(["node", "scripts/generate.js", "--", "untracked.txt"], {
        cwd: repo,
        runFn
      });

      expect(status).toBe(1);
      expect(runGit(repo, ["diff", "--cached", "--name-only", "--", "untracked.txt"])).toBe("");
      expect(fs.readFileSync(file, "utf8")).toBe("manual\nfresh\n");
    } finally {
      fs.rmSync(repo, { recursive: true, force: true });
    }
  });

  test("does not stage same-file stale hunks that existed before generation", () => {
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

      const status = main(["node", "scripts/generate.js", "--", "file.txt"], {
        cwd: repo,
        runFn
      });

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
