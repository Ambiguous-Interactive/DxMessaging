"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const { normalizeToLf } = require("../line-endings.js");
const { buildSpawnInvocation, spawnPlatformCommandSync } = require("../shell-command.js");
const {
  isPathOutsideDirectory,
  isOutsideRelative,
  toPosixPath,
  toRepoPosixRelative
} = require("../path-classifier.js");
const { walkFiles } = require("../repo-files.js");

test("normalizeToLf converts CRLF and lone CR to LF", () => {
  assert.equal(normalizeToLf("a\r\nb\rc\nd"), "a\nb\nc\nd");
  assert.equal(normalizeToLf(""), "");
});

test("buildSpawnInvocation is a passthrough on non-Windows platforms", () => {
  const invocation = buildSpawnInvocation("npm", ["run", "test"], { cwd: "/tmp" }, "linux");
  assert.equal(invocation.command, "npm");
  assert.deepEqual(invocation.args, ["run", "test"]);
  assert.deepEqual(invocation.options, { cwd: "/tmp" });
});

test("buildSpawnInvocation wraps npm shims in ComSpec on win32", () => {
  const invocation = buildSpawnInvocation("npm", ["ci"], {}, "win32");
  assert.equal(invocation.command, process.env.ComSpec || "cmd.exe");
  assert.deepEqual(invocation.args, ["/d", "/s", "/c", "npm.cmd", "ci"]);
  assert.equal(invocation.options.shell, false);
  assert.equal(invocation.options.windowsHide, true);

  const git = buildSpawnInvocation("git", ["status"], {}, "win32");
  assert.equal(git.command, "git");
  assert.deepEqual(git.args, ["status"]);
});

test("spawnPlatformCommandSync forwards the buildSpawnInvocation triple", () => {
  const calls = [];
  const fakeSpawnSync = (command, args, options) => {
    calls.push({ command, args, options });
    return { status: 0 };
  };
  const result = spawnPlatformCommandSync("npx", ["prettier"], {}, fakeSpawnSync, "win32");
  assert.equal(result.status, 0);
  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0], buildSpawnInvocation("npx", ["prettier"], {}, "win32"));
});

test("isOutsideRelative flags traversal, parent, and absolute results", () => {
  assert.equal(isOutsideRelative(""), false);
  assert.equal(isOutsideRelative("child"), false);
  assert.equal(isOutsideRelative(".."), true);
  assert.equal(isOutsideRelative(".." + path.sep + "sibling"), true);
  // Cross-drive Windows: path.relative returns an absolute target.
  assert.equal(isOutsideRelative("C:\\Users\\other", path.win32), true);
});

test("isPathOutsideDirectory detects descendants and escapes", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "lib-test-"));
  try {
    assert.equal(isPathOutsideDirectory(path.join(dir, "a.txt"), dir), false);
    assert.equal(isPathOutsideDirectory(dir, dir), false);
    assert.equal(isPathOutsideDirectory(path.join(dir, "..", "elsewhere"), dir), true);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("toPosixPath and toRepoPosixRelative produce forward-slash output", () => {
  assert.equal(toPosixPath("a\\b\\c"), "a/b/c");
  assert.equal(toPosixPath(null), "");
  assert.equal(toPosixPath(undefined), "");

  const repoRoot = path.resolve(__dirname, "..", "..", "..");
  const inside = path.join(repoRoot, "scripts", "lib");
  assert.equal(toRepoPosixRelative(inside, repoRoot), "scripts/lib");
  const outside = path.resolve(repoRoot, "..", "outside.txt");
  assert.equal(toRepoPosixRelative(outside, repoRoot), toPosixPath(outside));
});

test("walkFiles matches files, excludes directories, and tolerates missing roots", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "walk-test-"));
  try {
    fs.mkdirSync(path.join(dir, "keep"));
    fs.mkdirSync(path.join(dir, "skip"));
    fs.writeFileSync(path.join(dir, "keep", "a.md"), "a", "utf8");
    fs.writeFileSync(path.join(dir, "keep", "b.txt"), "b", "utf8");
    fs.writeFileSync(path.join(dir, "skip", "c.md"), "c", "utf8");

    const found = walkFiles(dir, {
      match: (fullPath) => fullPath.endsWith(".md"),
      excludeDir: (fullPath) => path.basename(fullPath) === "skip"
    });
    assert.deepEqual(found, [path.join(dir, "keep", "a.md")]);

    const errors = [];
    const missing = walkFiles(path.join(dir, "does-not-exist"), {
      onError: (error, failedDir) => errors.push({ code: error.code, failedDir })
    });
    assert.deepEqual(missing, []);
    assert.equal(errors.length, 1);
    assert.equal(errors[0].code, "ENOENT");
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
