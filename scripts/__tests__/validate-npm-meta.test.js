"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const validateNpmMeta = require("../validate-npm-meta.js");

const {
  buildLocalTarArchiveSpec,
  buildStandardCsharpMetaContent,
  collectTarballEntries,
  computeRequiredMetaPaths,
  findForbiddenTarballPaths,
  getCsharpMetaShapeViolation,
  normalizePackEntry,
  parsePackJsonEntries,
  runValidation,
  validateCsharpMetaFiles,
  validatePackEntries,
  validatePublishedFilesArePairedWithMetas
} = validateNpmMeta;

const FORBIDDEN_PATH_CASES = [
  ["Runtime/bin/Debug/Foo.dll", "bin-dir"],
  ["Runtime/obj/Release/Foo.dll", "obj-dir"],
  ["Editor/cache.tmp", "tmp"],
  ["Editor/Project.suo", "suo"],
  ["Editor/UserSettings.csproj.user", "csproj-user"],
  ["Editor/Team.user", "generic-user"],
  ["Editor/.vs/config", "vs-dir"],
  ["Editor/.idea/workspace.xml", "idea-dir"],
  ["Editor/ignore.pdb", "pdb"],
  ["Editor/cache.lscache", "lscache"],
  ["Editor/Project.DotSettings.user", "dotsettings-user"]
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

const VALID_CSHARP_META = buildStandardCsharpMetaContent("0123456789abcdef0123456789abcdef");
const VALID_CSHARP_META_HEADER = "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\n";

test("normalizePackEntry strips package prefix and trailing slash", () => {
  assert.equal(normalizePackEntry("package/Runtime/Core/Foo.cs"), "Runtime/Core/Foo.cs");
  assert.equal(normalizePackEntry("./package/Editor/"), "Editor");
  assert.equal(normalizePackEntry("package"), "");
  assert.equal(normalizePackEntry(""), "");
});

test("getCsharpMetaShapeViolation validates Unity C# MonoImporter shape", () => {
  const withTrailingSpaces = VALID_CSHARP_META.replace("  userData:\n", "  userData: \n")
    .replace("  assetBundleName:\n", "  assetBundleName: \n")
    .replace("  assetBundleVariant:\n", "  assetBundleVariant: \n");
  const withLegacyMetadata = VALID_CSHARP_META.replace(
    "guid: 0123456789abcdef0123456789abcdef\n",
    "guid: 0123456789abcdef0123456789abcdef\ntimeCreated: 1745161457\n"
  );

  for (const [relativePath, content, expected] of [
    ["Runtime/Foo.cs.meta", VALID_CSHARP_META, ""],
    ["Runtime/Foo.asmdef.meta", VALID_CSHARP_META_HEADER, ""],
    ["Runtime/Foo.cs.meta", VALID_CSHARP_META_HEADER, /missing the standard MonoImporter/],
    ["Runtime/Foo.cs.meta", `${VALID_CSHARP_META_HEADER}timeCreated: 1745161457\n`, /missing the standard MonoImporter/],
    ["Runtime/Foo.cs.meta", VALID_CSHARP_META.replace("  defaultReferences: []\n", ""), /defaultReferences/],
    ["Runtime/Foo.cs.meta", withTrailingSpaces, ""],
    ["Runtime/Foo.cs.meta", withLegacyMetadata, ""]
  ]) {
    const violation = getCsharpMetaShapeViolation(relativePath, content);
    if (expected instanceof RegExp) {
      assert.match(violation, expected);
    } else {
      assert.equal(violation, expected);
    }
  }
});

test("validateCsharpMetaFiles aggregates invalid tracked C# meta diagnostics", () => {
  const contents = {
    "Valid.cs.meta": VALID_CSHARP_META,
    "TwoLine.cs.meta": VALID_CSHARP_META_HEADER,
    "Foo.asmdef.meta": "ignored"
  };
  const result = validateCsharpMetaFiles(Object.keys(contents), {
    readFileSync: (filePath) => contents[path.basename(filePath)]
  });

  assert.equal(result.checked, 2);
  assert.deepEqual(result.invalid, [
    {
      path: "TwoLine.cs.meta",
      reason: "is missing the standard MonoImporter block for Unity C# scripts"
    }
  ]);
});

test("runValidation --repo-cs-metas-only reports invalid tracked C# metas", () => {
  const result = withQuietValidation(() =>
    runValidation({
      repoCsharpMetasOnly: true,
      relativePaths: ["TwoLine.cs.meta"],
      readFileSync: () => VALID_CSHARP_META_HEADER
    })
  );

  assert.equal(result.valid, false);
  assert.deepEqual(result.invalidCsharpMetas, [
    { path: "TwoLine.cs.meta", reason: "is missing the standard MonoImporter block for Unity C# scripts" }
  ]);
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
  const violations = findForbiddenTarballPaths(FORBIDDEN_PATH_CASES.map(([path]) => path));

  assert.deepEqual(
    violations.map((violation) => violation.path),
    FORBIDDEN_PATH_CASES.map(([path]) => path)
  );
  assert.deepEqual(
    violations.map((violation) => violation.rule),
    FORBIDDEN_PATH_CASES.map(([, rule]) => rule)
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
    for (const [forbiddenPath, rule] of FORBIDDEN_PATH_CASES) {
      const jsonFile = path.join(tempDir, `${rule}.json`);
      fs.writeFileSync(
        jsonFile,
        JSON.stringify([
          {
            filename: "pkg.tgz",
            files: [{ path: `package/${forbiddenPath}` }]
          }
        ]),
        "utf8"
      );

      const result = withQuietValidation(() => runValidation({ packJson: jsonFile }));
      assert.equal(result.valid, false, `${forbiddenPath} should make pack validation fail`);
      assert.deepEqual(
        result.forbidden.map((violation) => violation.rule),
        [rule]
      );
      assert.deepEqual(
        result.forbidden.map((violation) => violation.path),
        [forbiddenPath]
      );
    }
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
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
  const tarball = path.join(os.tmpdir(), "release", "pkg.tgz");
  const expectedArchiveSpec = buildLocalTarArchiveSpec(tarball);

  const packageJson = validateNpmMeta.readTarballPackageJson(tarball, (command, args, options) => {
    calls.push({ command, args, cwd: options.cwd });
    return json;
  });

  assert.deepEqual(packageJson, { name: "com.example.package", version: "1.2.3" });
  assert.deepEqual(calls, [
    {
      command: "tar",
      args: ["-xOf", expectedArchiveSpec.archive, "package/package.json"],
      cwd: expectedArchiveSpec.cwd
    }
  ]);
});

test("collectTarballEntries lists the archive from its parent directory", () => {
  const archiveDir = path.join(os.tmpdir(), "tarball-entry-test");
  const tarball = path.join(archiveDir, "pkg.tgz");
  const expectedArchiveSpec = buildLocalTarArchiveSpec(tarball);
  const calls = [];
  const entries = collectTarballEntries(tarball, (command, args, options) => {
    calls.push({ command, args, cwd: options.cwd });
    return "package/Runtime/Core/Foo.cs\npackage/Runtime/Core/Foo.cs.meta\n";
  });

  assert.deepEqual(entries, ["Runtime/Core/Foo.cs", "Runtime/Core/Foo.cs.meta"]);
  assert.deepEqual(calls, [
    {
      command: "tar",
      args: ["-tzf", expectedArchiveSpec.archive],
      cwd: expectedArchiveSpec.cwd
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
