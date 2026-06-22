"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

const FIRST_PARTY_ANALYZER_DLLS = [
  "WallstopStudios.DxMessaging.SourceGenerators.dll",
  "WallstopStudios.DxMessaging.Analyzer.dll"
];

function walkDllMetas(dir, out) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return out;
  }
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walkDllMetas(full, out);
    } else if (entry.isFile() && entry.name.endsWith(".dll.meta")) {
      out.push(full);
    }
  }
  return out;
}

function hasRoslynAnalyzerLabel(metaPath) {
  const text = fs.readFileSync(metaPath, "utf8");
  return /(^|\n)labels:\s*(\n\s*-\s.*)*\n\s*-\s*RoslynAnalyzer\b/.test(text) ||
    /(^|\n)\s*-\s*RoslynAnalyzer\b/.test(text);
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
  const metas = [];
  walkDllMetas(path.join(REPO_ROOT, "Runtime"), metas);
  walkDllMetas(path.join(REPO_ROOT, "Editor"), metas);
  return metas
    .filter((metaPath) => hasRoslynAnalyzerLabel(metaPath))
    .map((metaPath) => ({
      metaPath,
      dllPath: metaPath.replace(/\.meta$/, ""),
      relative: path.relative(REPO_ROOT, metaPath.replace(/\.meta$/, "")).split(path.sep).join("/")
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
    assert.ok(
      fs.existsSync(`${expected}.meta`),
      `${dll}.meta (carrying the RoslynAnalyzer label) must ship alongside it.`
    );
  }
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
