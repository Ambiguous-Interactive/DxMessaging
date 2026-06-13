"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const crypto = require("node:crypto");
const { spawnSync } = require("node:child_process");

const { runValidation } = require("../validate-npm-meta.js");

function withQuietValidation(callback) {
  const originalLog = console.log;
  const originalError = console.error;
  console.log = () => {};
  console.error = () => {};
  try {
    return callback();
  } finally {
    console.log = originalLog;
    console.error = originalError;
  }
}

function computeSha256(filePath) {
  const hash = crypto.createHash("sha256");
  hash.update(fs.readFileSync(filePath));
  return hash.digest("hex");
}

function createReleaseFixture(t, options = {}) {
  const name = options.name || "com.example.package";
  const version = options.version || "1.2.3";
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "release-artifact-test-"));
  t.after(() => fs.rmSync(tempDir, { recursive: true, force: true }));
  const packageDir = path.join(tempDir, "package");
  fs.mkdirSync(packageDir, { recursive: true });
  fs.writeFileSync(path.join(packageDir, "package.json"), JSON.stringify({ name, version }), "utf8");

  const tarball = path.join(tempDir, `${name}-${version}.tgz`);
  const tar = spawnSync("tar", ["-czf", `./${path.basename(tarball)}`, "-C", tempDir, "package"], {
    cwd: tempDir,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });
  if (tar.error && tar.error.code === "ENOENT") {
    t.skip("tar is not available");
    return null;
  }
  assert.equal(tar.status, 0, tar.stderr);

  fs.writeFileSync(
    `${tarball}.sha256`,
    `${computeSha256(tarball)}  ${path.basename(tarball)}\n`,
    "utf8"
  );
  fs.writeFileSync(path.join(tempDir, "release-notes.md"), "Release notes\n", "utf8");
  return { dir: tempDir, name, version, tarball };
}

test("runValidation rejects forbidden paths from a concrete tarball", (t) => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "tarball-validation-test-"));
  try {
    const artifactDir = path.join(tempDir, "package", "Runtime", "bin", "Debug");
    fs.mkdirSync(artifactDir, { recursive: true });
    fs.writeFileSync(path.join(artifactDir, "Leaked.dll"), "", "utf8");

    const tarball = path.join(tempDir, "package.tgz");
    const tar = spawnSync(
      "tar",
      ["-czf", `./${path.basename(tarball)}`, "-C", tempDir, "package"],
      {
        cwd: tempDir,
        encoding: "utf8",
        stdio: ["ignore", "pipe", "pipe"]
      }
    );
    if (tar.error && tar.error.code === "ENOENT") {
      t.skip("tar is not available");
      return;
    }
    assert.equal(tar.status, 0, tar.stderr);

    const result = withQuietValidation(() => runValidation({ tarball }));
    assert.equal(result.valid, false);
    assert.ok(
      result.forbidden.some(
        (violation) => violation.rule === "bin-dir" && violation.path.startsWith("Runtime/bin")
      ),
      JSON.stringify(result.forbidden)
    );
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test("runValidation validates release artifact directories", (t) => {
  const fixture = createReleaseFixture(t);
  if (!fixture) {
    return;
  }

  const result = withQuietValidation(() =>
    runValidation({
      releaseDir: fixture.dir,
      expectedName: fixture.name,
      expectedVersion: fixture.version
    })
  );
  assert.equal(result.valid, true);
});

test("runValidation rejects release artifact identity mismatches", (t) => {
  const fixture = createReleaseFixture(t);
  if (!fixture) {
    return;
  }

  assert.throws(
    () =>
      withQuietValidation(() =>
        runValidation({
          releaseDir: fixture.dir,
          expectedName: fixture.name,
          expectedVersion: "9.9.9"
        })
      ),
    /identity mismatch/
  );
});

test("runValidation rejects extra release checksum artifacts", (t) => {
  const fixture = createReleaseFixture(t);
  if (!fixture) {
    return;
  }

  fs.writeFileSync(
    path.join(fixture.dir, "stale.sha256"),
    `${"0".repeat(64)}  stale.tgz\n`,
    "utf8"
  );
  assert.throws(
    () =>
      withQuietValidation(() =>
        runValidation({
          releaseDir: fixture.dir,
          expectedName: fixture.name,
          expectedVersion: fixture.version
        })
      ),
    /exactly one \.sha256/
  );
});

test("runValidation rejects missing release notes artifacts", (t) => {
  const fixture = createReleaseFixture(t);
  if (!fixture) {
    return;
  }

  fs.rmSync(path.join(fixture.dir, "release-notes.md"), { force: true });
  assert.throws(
    () =>
      withQuietValidation(() =>
        runValidation({
          releaseDir: fixture.dir,
          expectedName: fixture.name,
          expectedVersion: fixture.version
        })
      ),
    /Release notes artifact is missing/
  );
});
