"use strict";

// Drift-guard for GitHub issue #229 ("Not Using Assembly Definitions Breaks Roslyn
// Code Gen"). The DxMessaging source generator + analyzer ship as RoslynAnalyzer-labeled
// DLLs. Unity scopes a folder-resident analyzer to "the assembly defined by the nearest
// enclosing .asmdef, plus every assembly that references it" (see
// docs.unity3d.com .../analyzer-scope-and-diagnostics.html). If those DLLs sit under the
// EDITOR-ONLY asmdef (as they historically did, in Editor/Analyzers/), no consumer runtime
// code can reference that editor assembly, so a project's [DxUntargetedMessage] /
// [DxAutoConstructor] types in Assembly-CSharp (or any runtime asmdef) never get generated
// and fail with cryptic CS0315/CS0452 errors.
//
// The fix ships the labeled DLLs under Runtime/Analyzers/ (governed by the all-platforms
// runtime asmdef), so Unity applies the generator to the runtime assembly AND everything
// that references it, including the predefined Assembly-CSharp. This test fails if anyone
// moves them back under an editor-only asmdef.

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
  // The label block is a YAML sequence:  labels:\n  - RoslynAnalyzer
  return /(^|\n)labels:\s*(\n\s*-\s.*)*\n\s*-\s*RoslynAnalyzer\b/.test(text) ||
    /(^|\n)\s*-\s*RoslynAnalyzer\b/.test(text);
}

// Walk up from `startDir` (inclusive) to REPO_ROOT looking for the nearest enclosing
// .asmdef file. Returns its parsed JSON, or null if none governs the folder.
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

// An asmdef is "editor-only" when its includePlatforms restricts it to the Editor (so no
// runtime/player assembly, including Assembly-CSharp, can reference it).
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
      `${dll} must ship under Runtime/Analyzers/ so Unity scopes the generator to the ` +
        `runtime assembly + everything that references it (including Assembly-CSharp). ` +
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
    "These RoslynAnalyzer-labeled DLLs are scoped under an editor-only asmdef, so the source " +
      "generator will NOT reach consumer runtime code (Assembly-CSharp or runtime asmdefs) and " +
      "[Dx*Message]/[DxAutoConstructor] types will fail with CS0315/CS0452. Move them under the " +
      `all-platforms runtime asmdef (Runtime/Analyzers/). Offenders: ${violations.join(", ")}`
  );
});

test("editor-only detection logic catches a regression (red-green sentinel)", () => {
  // Proves the guard would fire if the DLLs were moved back under the editor-only asmdef.
  assert.equal(isEditorOnlyAsmdef({ includePlatforms: ["Editor"] }), true);
  assert.equal(isEditorOnlyAsmdef({ includePlatforms: [] }), false); // all-platforms (runtime asmdef)
  assert.equal(isEditorOnlyAsmdef({ includePlatforms: ["Editor", "WindowsStandalone64"] }), false);
  assert.equal(isEditorOnlyAsmdef(null), false);
});
