"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { execFileSync } = require("node:child_process");

const { normalizeToLf } = require("../line-endings.js");
const { buildSpawnInvocation, spawnPlatformCommandSync } = require("../shell-command.js");
const {
  isPathOutsideDirectory,
  isOutsideRelative,
  toPosixPath,
  toRepoPosixRelative
} = require("../path-classifier.js");
const { walkFiles } = require("../repo-files.js");

function fakeRealpathSync(mapping) {
  const realpathSync = (fullPath) => {
    const key = fullPath.toLowerCase();
    if (mapping.has(key)) {
      return mapping.get(key);
    }

    const error = new Error(`ENOENT: no such file or directory, realpath '${fullPath}'`);
    error.code = "ENOENT";
    throw error;
  };

  realpathSync.native = realpathSync;
  return realpathSync;
}

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

test("PowerShell scripts avoid bare Node shim commands", () => {
  const repoRoot = path.resolve(__dirname, "..", "..", "..");
  const command = String.raw`['"]?(?:npm|npx)['"]?`;
  const pattern = new RegExp(
    String.raw`(?:^\s*|[=({;|]\s*)(?:&\s*)?${command}(?=$|\s+(?![})]))|\bGet-Command\s+(?:-Name\s+)?${command}(?=\s|$)|\bStart-Process\s+(?:-FilePath\s+)?${command}(?=\s|$)`,
    "gmi"
  );
  const violations = [];
  const files = execFileSync("git", ["ls-files", "*.ps1"], { cwd: repoRoot, encoding: "utf8" })
    .split(/\r?\n/)
    .filter(Boolean);
  for (const file of files) {
    const text = fs.readFileSync(path.join(repoRoot, file), "utf8");
    for (const match of text.matchAll(pattern)) {
      const line = text.slice(0, match.index).split(/\r\n|\r|\n/).length;
      violations.push(`${file}:${line}:${match[0].trim()}`);
    }
  }
  assert.deepEqual(violations, []);
});

test("isOutsideRelative flags traversal, parent, and absolute results", () => {
  const cases = [
    ["empty relative path is self", "", path, false],
    ["child path stays inside", "child", path, false],
    ["parent path escapes", "..", path, true],
    ["host separator parent path escapes", ".." + path.sep + "sibling", path, true],
    ["windows separator parent path escapes", "..\\sibling", path.win32, true],
    // Cross-drive Windows: path.relative returns an absolute target.
    ["windows absolute relative result escapes", "C:\\Users\\other", path.win32, true]
  ];

  for (const [name, relativePath, pathImpl, expected] of cases) {
    assert.equal(isOutsideRelative(relativePath, pathImpl), expected, name);
  }
});

test("isPathOutsideDirectory detects descendants and escapes", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "lib-test-"));
  try {
    const existingFile = path.join(dir, "existing.txt");
    fs.writeFileSync(existingFile, "", "utf8");

    const cases = [
      ["missing child stays inside", path.join(dir, "a.txt"), false],
      ["existing child stays inside", existingFile, false],
      ["directory self stays inside", dir, false],
      ["normalized sibling escapes", path.join(dir, "..", "elsewhere"), true],
      ["same-prefix sibling escapes", `${dir}-sibling`, true]
    ];

    for (const [name, filePath, expected] of cases) {
      assert.equal(isPathOutsideDirectory(filePath, dir), expected, name);
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("isPathOutsideDirectory classifies Windows namespaced missing descendants", () => {
  const realpathSync = fakeRealpathSync(
    new Map([
      ["c:\\repo\\skills", "\\\\?\\C:\\Repo\\Skills"],
      ["c:\\repo", "\\\\?\\C:\\Repo"]
    ])
  );
  const options = {
    pathImpl: path.win32,
    realpathSync,
    caseInsensitive: true
  };

  const cases = [
    ["missing descendant stays inside", "C:\\Repo\\Skills\\missing.md", false],
    ["self stays inside", "C:\\Repo\\Skills", false],
    ["sibling escapes", "C:\\Repo\\other.md", true],
    ["cross-drive absolute relative result escapes", "D:\\other\\missing.md", true]
  ];

  for (const [name, filePath, expected] of cases) {
    assert.equal(isPathOutsideDirectory(filePath, "C:\\Repo\\Skills", options), expected, name);
  }
});

test("isPathOutsideDirectory classifies missing descendants under symlinked directories", (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "lib-symlink-test-"));
  const realDir = path.join(root, "real");
  const linkDir = path.join(root, "link");
  fs.mkdirSync(realDir);

  try {
    fs.symlinkSync(realDir, linkDir, process.platform === "win32" ? "junction" : "dir");
  } catch (error) {
    fs.rmSync(root, { recursive: true, force: true });
    t.skip(`symlink creation unavailable: ${error.message}`);
    return;
  }

  try {
    assert.equal(isPathOutsideDirectory(path.join(linkDir, "missing.txt"), linkDir), false);
    assert.equal(isPathOutsideDirectory(path.join(root, "sibling.txt"), linkDir), true);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
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
