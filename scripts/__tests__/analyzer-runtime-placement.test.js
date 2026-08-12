"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { walkFiles } = require("../lib/repo-files");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

const FIRST_PARTY_ANALYZER_DLLS = [
  "WallstopStudios.DxMessaging.SourceGenerators.dll",
  "WallstopStudios.DxMessaging.Analyzer.dll"
];

function hasRoslynAnalyzerLabel(metaPath) {
  return /(^|\n)\s*-\s*RoslynAnalyzer\b/.test(fs.readFileSync(metaPath, "utf8"));
}

function nearestAsmdef(startDir) {
  let dir = startDir;
  while (true) {
    let entries = [];
    try {
      entries = fs.readdirSync(dir);
    } catch {
      entries = [];
    }
    const asmdef = entries.find((name) => name.endsWith(".asmdef"));
    if (asmdef) {
      return JSON.parse(fs.readFileSync(path.join(dir, asmdef), "utf8"));
    }
    if (path.resolve(dir) === REPO_ROOT) {
      return null;
    }
    const parent = path.dirname(dir);
    if (parent === dir) {
      return null;
    }
    dir = parent;
  }
}

function isEditorOnlyAsmdef(asmdef) {
  if (!asmdef || !Array.isArray(asmdef.includePlatforms)) {
    return false;
  }
  const platforms = asmdef.includePlatforms;
  return platforms.length > 0 && platforms.every((p) => p === "Editor");
}

function findLabeledAnalyzerDlls() {
  const options = { match: (_full, entry) => entry.name.toLowerCase().endsWith(".dll.meta") };
  const metas = ["Runtime", "Editor"].flatMap((dir) =>
    walkFiles(path.join(REPO_ROOT, dir), options)
  );
  return metas
    .filter((metaPath) => hasRoslynAnalyzerLabel(metaPath))
    .map((metaPath) => ({
      metaPath,
      dllPath: metaPath.replace(/\.meta$/, ""),
      relative: path
        .relative(REPO_ROOT, metaPath.replace(/\.meta$/, ""))
        .split(path.sep)
        .join("/")
    }));
}

test("the source generator + analyzer ship under Runtime/Analyzers (issue #229)", () => {
  for (const dll of FIRST_PARTY_ANALYZER_DLLS) {
    const expected = path.join(REPO_ROOT, "Runtime", "Analyzers", dll);
    assert.ok(
      fs.existsSync(expected),
      `${dll} must ship under Runtime/Analyzers/ for Unity analyzer scope. ` +
        `Missing: ${path.relative(REPO_ROOT, expected)}`
    );
    const meta = `${expected}.meta`;
    assert.ok(
      fs.existsSync(meta),
      `${dll}.meta (carrying the RoslynAnalyzer label) must ship alongside it.`
    );
    assert.ok(hasRoslynAnalyzerLabel(meta), `${dll}.meta must carry the RoslynAnalyzer label.`);
  }
});

test("Runtime/Analyzers ships only first-party compiler extensions (issue #371)", () => {
  const analyzerDir = path.join(REPO_ROOT, "Runtime", "Analyzers");
  const shippedDlls = walkFiles(analyzerDir, {
    match: (_full, entry) => entry.name.toLowerCase().endsWith(".dll")
  })
    .map((dllPath) => path.relative(analyzerDir, dllPath).split(path.sep).join("/"))
    .sort();

  assert.deepEqual(
    shippedDlls,
    [...FIRST_PARTY_ANALYZER_DLLS].sort(),
    "Unity supplies Roslyn's runtime assemblies. Shipping private compiler-support DLLs " +
      "adds package weight and can diverge from the editor's compiler host."
  );
});

test("no RoslynAnalyzer-labeled DLL is scoped under an editor-only asmdef (issue #229)", () => {
  const labeled = findLabeledAnalyzerDlls();
  assert.ok(
    labeled.length >= FIRST_PARTY_ANALYZER_DLLS.length,
    `Expected to find the RoslynAnalyzer-labeled generator/analyzer DLLs; found ${labeled.length}.`
  );

  const violations = [];
  for (const dll of labeled) {
    const asmdef = nearestAsmdef(path.dirname(dll.dllPath));
    if (isEditorOnlyAsmdef(asmdef)) {
      violations.push(dll.relative);
    }
  }

  assert.deepEqual(
    violations,
    [],
    "RoslynAnalyzer-labeled DLLs under editor-only asmdefs cannot reach runtime code. " +
      `Move them under Runtime/Analyzers/. Offenders: ${violations.join(", ")}`
  );
});

test("editor-only detection logic catches a regression (red-green sentinel)", () => {
  assert.equal(isEditorOnlyAsmdef({ includePlatforms: ["Editor"] }), true);
  assert.equal(isEditorOnlyAsmdef({ includePlatforms: [] }), false);
  assert.equal(isEditorOnlyAsmdef({ includePlatforms: ["Editor", "WindowsStandalone64"] }), false);
  assert.equal(isEditorOnlyAsmdef(null), false);
});
