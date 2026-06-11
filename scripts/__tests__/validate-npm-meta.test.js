"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  collectTarballEntries,
  computeRequiredMetaPaths,
  findForbiddenTarballPaths,
  normalizePackEntry,
  parsePackJsonEntries,
  validatePackEntries,
  validatePublishedFilesArePairedWithMetas
} = require("../validate-npm-meta.js");

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
  assert.throws(() => parsePackJsonEntries("[{\"files\":\"oops\"}]"), /files array/);
  assert.throws(
    () => parsePackJsonEntries('[{"files":[{"noPath":true}]}]'),
    /has no string path/
  );
});

test("findForbiddenTarballPaths catches issue-204 style artifact leaks", () => {
  const violations = findForbiddenTarballPaths([
    "Runtime/bin/Debug/Foo.dll",
    "Runtime/obj/Release/Foo.dll",
    "Editor/cache.tmp",
    "Editor/Project.suo",
    "Editor/UserSettings.csproj.user",
    "Editor/Team.user",
    "Editor/.vs/config",
    "Editor/.idea/workspace.xml",
    "Editor/ignore.pdb"
  ]);

  assert.deepEqual(
    violations.map((violation) => violation.path),
    [
      "Runtime/bin/Debug/Foo.dll",
      "Runtime/obj/Release/Foo.dll",
      "Editor/cache.tmp",
      "Editor/Project.suo",
      "Editor/UserSettings.csproj.user",
      "Editor/Team.user",
      "Editor/.vs/config",
      "Editor/.idea/workspace.xml",
      "Editor/ignore.pdb"
    ]
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

test("collectTarballEntries normalizes tar output paths", () => {
  const entries = collectTarballEntries("dummy.tgz", () => {
    return "package/Runtime/Core/Foo.cs\npackage/Runtime/Core/Foo.cs.meta\n";
  });

  assert.deepEqual(entries, ["Runtime/Core/Foo.cs", "Runtime/Core/Foo.cs.meta"]);
});

test("collectTarballEntries reports tar command errors clearly", () => {
  assert.throws(
    () => {
      collectTarballEntries("missing.tgz", () => {
        const error = new Error("tar failed");
        error.stderr = "tar: missing.tgz: Cannot open: No such file or directory\n";
        throw error;
      });
    },
    /Unable to list tarball entries/
  );
});