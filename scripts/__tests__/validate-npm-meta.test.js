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
  runValidation,
  validatePackEntries,
  validatePublishedFilesArePairedWithMetas
} = require("../validate-npm-meta.js");

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

test("collectTarballEntries normalizes tar output paths", () => {
  const entries = collectTarballEntries("dummy.tgz", () => {
    return "package/Runtime/Core/Foo.cs\npackage/Runtime/Core/Foo.cs.meta\n";
  });

  assert.deepEqual(entries, ["Runtime/Core/Foo.cs", "Runtime/Core/Foo.cs.meta"]);
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
