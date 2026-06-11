"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const crypto = require("node:crypto");
const { spawnSync } = require("node:child_process");

const validateNpmMeta = require("../validate-npm-meta.js");

const {
  buildLocalTarArchiveSpec,
  collectTarballEntries,
  computeRequiredMetaPaths,
  findForbiddenTarballPaths,
  normalizePackEntry,
  parsePackJsonEntries,
  runValidation,
  validatePackEntries,
  validatePublishedFilesArePairedWithMetas
} = validateNpmMeta;

const FORBIDDEN_PATH_CASES = [
  {
    path: "Runtime/bin/Debug/Foo.dll",
    rule: "bin-dir"
  },
  {
    path: "Runtime/obj/Release/Foo.dll",
    rule: "obj-dir"
  },
  {
    path: "Editor/cache.tmp",
    rule: "tmp"
  },
  {
    path: "Editor/Project.suo",
    rule: "suo"
  },
  {
    path: "Editor/UserSettings.csproj.user",
    rule: "csproj-user"
  },
  {
    path: "Editor/Team.user",
    rule: "generic-user"
  },
  {
    path: "Editor/.vs/config",
    rule: "vs-dir"
  },
  {
    path: "Editor/.idea/workspace.xml",
    rule: "idea-dir"
  },
  {
    path: "Editor/ignore.pdb",
    rule: "pdb"
  },
  {
    path: "Editor/cache.lscache",
    rule: "lscache"
  },
  {
    path: "Editor/Project.DotSettings.user",
    rule: "dotsettings-user"
  }
];

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
  const packageDir = path.join(tempDir, "package");
  fs.mkdirSync(packageDir, { recursive: true });
  fs.writeFileSync(
    path.join(packageDir, "package.json"),
    JSON.stringify({ name, version }),
    "utf8"
  );

  const tarball = path.join(tempDir, `${name}-${version}.tgz`);
  const tar = spawnSync("tar", ["-czf", `./${path.basename(tarball)}`, "-C", tempDir, "package"], {
    cwd: tempDir,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });
  if (tar.error && tar.error.code === "ENOENT") {
    fs.rmSync(tempDir, { recursive: true, force: true });
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

  return {
    dir: tempDir,
    name,
    version,
    tarball
  };
}

test("normalizePackEntry strips package prefix and trailing slash", () => {
  assert.equal(normalizePackEntry("package/Runtime/Core/Foo.cs"), "Runtime/Core/Foo.cs");
  assert.equal(normalizePackEntry("./package/Editor/"), "Editor");
  assert.equal(normalizePackEntry("package"), "");
  assert.equal(normalizePackEntry(""), "");
});

test("parsePackJsonEntries loads npm --json file entries", () => {
  const json = JSON.stringify([
    {
      filename: "pkg-1.0.0.tgz",
      files: [{ path: "Runtime/Foo.cs" }, { path: "Runtime/Foo.cs.meta" }]
    }
  ]);

  assert.deepEqual(parsePackJsonEntries(json), ["Runtime/Foo.cs", "Runtime/Foo.cs.meta"]);
});

test("parsePackJsonEntries rejects malformed pack JSON payloads", () => {
  assert.throws(() => parsePackJsonEntries("{}"), /entry list/);
  assert.throws(() => parsePackJsonEntries('[{"files":"oops"}]'), /files array/);
  assert.throws(() => parsePackJsonEntries('[{"files":[{"noPath":true}]}]'), /has no string path/);
});

test("findForbiddenTarballPaths catches issue-204 style artifact leaks", () => {
  const violations = findForbiddenTarballPaths(FORBIDDEN_PATH_CASES.map((entry) => entry.path));

  assert.deepEqual(
    violations.map((violation) => violation.path),
    FORBIDDEN_PATH_CASES.map((entry) => entry.path)
  );
  assert.deepEqual(
    violations.map((violation) => violation.rule),
    FORBIDDEN_PATH_CASES.map((entry) => entry.rule)
  );
});

test("computeRequiredMetaPaths includes ancestor dirs but skips Samples~ root", () => {
  const required = computeRequiredMetaPaths([
    "Samples~/Mini Combat/Scenes/Demo.unity",
    "Runtime/Core/Bus.cs",
    "Runtime/Core/Bus.cs.meta"
  ]);

  assert.equal(required.has("Samples~.meta"), false);
  assert.equal(required.has("Samples~/Mini Combat.meta"), true);
  assert.equal(required.has("Samples~/Mini Combat/Scenes.meta"), true);
  assert.equal(required.has("Samples~/Mini Combat/Scenes/Demo.unity.meta"), true);
  assert.equal(required.has("Runtime.meta"), true);
  assert.equal(required.has("Runtime/Core.meta"), true);
  assert.equal(required.has("Runtime/Core/Bus.cs.meta"), true);
});

test("validatePublishedFilesArePairedWithMetas reports missing and orphan metas", () => {
  const result = validatePublishedFilesArePairedWithMetas([
    "Runtime/Core/Bus.cs",
    "Runtime.meta",
    "Runtime/Core.meta",
    "Runtime/Core/Unused.cs.meta",
    "Samples~/Example/Scene.unity",
    "Samples~/Example.meta",
    "Samples~/Example/Scene.unity.meta"
  ]);

  assert.deepEqual(result.missing, ["Runtime/Core/Bus.cs.meta"]);
  assert.deepEqual(result.orphans, ["Runtime/Core/Unused.cs.meta"]);
});

test("validatePackEntries returns valid for properly paired Unity paths", () => {
  const entries = [
    "Editor.meta",
    "Editor/Analyzers.meta",
    "Editor/Analyzers/Analyzer.cs",
    "Editor/Analyzers/Analyzer.cs.meta",
    "Runtime.meta",
    "Runtime/Core.meta",
    "Runtime/Core/Bus.cs",
    "Runtime/Core/Bus.cs.meta",
    "Samples~/Demo.meta",
    "Samples~/Demo/Scene.unity",
    "Samples~/Demo/Scene.unity.meta"
  ];

  const result = validatePackEntries(entries);
  assert.equal(result.valid, true);
  assert.deepEqual(result.forbidden, []);
  assert.deepEqual(result.missingMetas, []);
  assert.deepEqual(result.orphanMetas, []);
});

test("validatePackEntries aggregates forbidden, missing, and orphan diagnostics", () => {
  const result = validatePackEntries([
    "Runtime/Foo.cs",
    "Runtime.meta",
    "Runtime/Leak.pdb",
    "Runtime/Orphan.meta"
  ]);

  assert.equal(result.valid, false);
  assert.equal(result.forbidden.length, 1);
  assert.deepEqual(result.missingMetas, ["Runtime/Foo.cs.meta"]);
  assert.deepEqual(result.orphanMetas, ["Runtime/Orphan.meta"]);
});

test("parses real-world pack JSON from a temporary file", () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "pack-json-test-"));
  try {
    const jsonFile = path.join(tempDir, "pack.json");
    fs.writeFileSync(
      jsonFile,
      JSON.stringify([
        {
          filename: "pkg.tgz",
          files: [
            { path: "package/Editor.meta" },
            { path: "package/Editor/Tool.cs" },
            { path: "package/Editor/Tool.cs.meta" }
          ]
        }
      ]),
      "utf8"
    );

    const entries = parsePackJsonEntries(fs.readFileSync(jsonFile, "utf8"));
    assert.deepEqual(entries, ["Editor.meta", "Editor/Tool.cs", "Editor/Tool.cs.meta"]);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test("runValidation rejects forbidden paths from npm pack JSON output", () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "pack-json-validation-test-"));
  try {
    for (const entry of FORBIDDEN_PATH_CASES) {
      const jsonFile = path.join(tempDir, `${entry.rule}.json`);
      fs.writeFileSync(
        jsonFile,
        JSON.stringify([
          {
            filename: "pkg.tgz",
            files: [{ path: `package/${entry.path}` }]
          }
        ]),
        "utf8"
      );

      const result = withQuietValidation(() => runValidation({ packJson: jsonFile }));
      assert.equal(result.valid, false, `${entry.path} should make pack validation fail`);
      assert.deepEqual(
        result.forbidden.map((violation) => violation.rule),
        [entry.rule]
      );
      assert.deepEqual(
        result.forbidden.map((violation) => violation.path),
        [entry.path]
      );
    }
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

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

  try {
    const result = withQuietValidation(() =>
      runValidation({
        releaseDir: fixture.dir,
        expectedName: fixture.name,
        expectedVersion: fixture.version
      })
    );
    assert.equal(result.valid, true);
  } finally {
    fs.rmSync(fixture.dir, { recursive: true, force: true });
  }
});

test("runValidation rejects release artifact identity mismatches", (t) => {
  const fixture = createReleaseFixture(t);
  if (!fixture) {
    return;
  }

  try {
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
  } finally {
    fs.rmSync(fixture.dir, { recursive: true, force: true });
  }
});

test("runValidation rejects extra release checksum artifacts", (t) => {
  const fixture = createReleaseFixture(t);
  if (!fixture) {
    return;
  }

  try {
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
  } finally {
    fs.rmSync(fixture.dir, { recursive: true, force: true });
  }
});

test("runValidation rejects missing release notes artifacts", (t) => {
  const fixture = createReleaseFixture(t);
  if (!fixture) {
    return;
  }

  try {
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
  } finally {
    fs.rmSync(fixture.dir, { recursive: true, force: true });
  }
});

test("buildLocalTarArchiveSpec keeps Windows drive letters out of tar operands", () => {
  const spec = buildLocalTarArchiveSpec(
    "C:\\Temp\\release\\com.example.package-1.2.3.tgz",
    path.win32,
    "D:\\Repo"
  );

  assert.deepEqual(spec, {
    archive: "./com.example.package-1.2.3.tgz",
    cwd: "C:\\Temp\\release"
  });
});

test("buildLocalTarArchiveSpec keeps local colon basenames local", () => {
  const spec = buildLocalTarArchiveSpec("/tmp/release/foo:bar.tgz", path.posix, "/workspace/repo");

  assert.deepEqual(spec, {
    archive: "./foo:bar.tgz",
    cwd: "/tmp/release"
  });
});

test("readTarballPackageJson uses the local tar archive operand", () => {
  const calls = [];
  const json = JSON.stringify({ name: "com.example.package", version: "1.2.3" });

  const packageJson = validateNpmMeta.readTarballPackageJson(
    "/tmp/release/pkg.tgz",
    (command, args, options) => {
      calls.push({ command, args, cwd: options.cwd });
      return json;
    }
  );

  assert.deepEqual(packageJson, { name: "com.example.package", version: "1.2.3" });
  assert.deepEqual(calls, [
    {
      command: "tar",
      args: ["-xOf", "./pkg.tgz", "package/package.json"],
      cwd: "/tmp/release"
    }
  ]);
});

test("collectTarballEntries lists the archive from its parent directory", () => {
  const archiveDir = path.join(os.tmpdir(), "tarball-entry-test");
  const calls = [];
  const entries = collectTarballEntries(
    path.join(archiveDir, "pkg.tgz"),
    (command, args, options) => {
      calls.push({ command, args, cwd: options.cwd });
      return "package/Runtime/Core/Foo.cs\npackage/Runtime/Core/Foo.cs.meta\n";
    }
  );

  assert.deepEqual(entries, ["Runtime/Core/Foo.cs", "Runtime/Core/Foo.cs.meta"]);
  assert.deepEqual(calls, [
    {
      command: "tar",
      args: ["-tzf", "./pkg.tgz"],
      cwd: archiveDir
    }
  ]);
});

test("collectTarballEntries reports tar command errors clearly", () => {
  assert.throws(() => {
    collectTarballEntries("missing.tgz", () => {
      const error = new Error("tar failed");
      error.stderr = "tar: missing.tgz: Cannot open: No such file or directory\n";
      throw error;
    });
  }, /Unable to list tarball entries/);
});
