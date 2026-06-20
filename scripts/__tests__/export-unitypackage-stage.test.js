"use strict";

// -StageOnly smoke for scripts/unity/export-unitypackage.ps1: packs the REAL
// repo payload with npm pack and stages it into a temp dir, pinning the
// Assets-form invariants (exclusions, Samples~ rename, .meta pairing, the
// exporter living outside the export root, and deterministic folder metas).
// pwsh-gated like verify-unity-results-action.test.js.

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPT_PATH = path.join(REPO_ROOT, "scripts", "unity", "export-unitypackage.ps1");

function commandExists(command) {
  const result = spawnSync(
    command,
    ["-NoLogo", "-NoProfile", "-Command", "$PSVersionTable.PSVersion"],
    {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    }
  );
  return !result.error && result.status === 0;
}

const HAS_PWSH = commandExists("pwsh");

function runStageOnly(stagingRoot, projectPath) {
  const result = spawnSync(
    "pwsh",
    [
      "-NoLogo",
      "-NoProfile",
      "-NonInteractive",
      "-ExecutionPolicy",
      "Bypass",
      "-File",
      SCRIPT_PATH,
      "-UnityVersion",
      "2022.3.45f1",
      "-RepoRoot",
      REPO_ROOT,
      "-ProjectPath",
      projectPath,
      "-ArtifactsPath",
      path.join(stagingRoot, "artifacts"),
      "-StageOnly"
    ],
    { cwd: stagingRoot, encoding: "utf8", timeout: 600000 }
  );
  assert.equal(result.status, 0, `stage-only run failed:\n${result.stdout}\n${result.stderr}`);
}

function walk(dir) {
  const entries = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    entries.push({ full, isDirectory: entry.isDirectory() });
    if (entry.isDirectory()) {
      entries.push(...walk(full));
    }
  }
  return entries;
}

// The three staged folders whose .meta the payload cannot supply; the script
// pre-writes them with deterministic GUIDs (finding: byte-stable exports).
const ANCESTOR_METAS = [
  path.join("Assets", "WallstopStudios.meta"),
  path.join("Assets", "WallstopStudios", "DxMessaging.meta"),
  path.join("Assets", "WallstopStudios", "DxMessaging", "Samples.meta")
];

test("export-unitypackage -StageOnly stages the Assets-form payload with stable metas", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const stagingRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-unitypackage-stage-"));
  const projectPath = path.join(stagingRoot, "project");
  try {
    runStageOnly(stagingRoot, projectPath);
    const assetsRoot = path.join(projectPath, "Assets");
    const exportRoot = path.join(assetsRoot, "WallstopStudios", "DxMessaging");

    // (a) Generator sources and their meta never reach the staged tree.
    assert.ok(!fs.existsSync(path.join(exportRoot, "SourceGenerators")));
    assert.ok(!fs.existsSync(path.join(exportRoot, "SourceGenerators.meta")));
    // (b) Samples~ was renamed to Samples.
    assert.ok(fs.existsSync(path.join(exportRoot, "Samples")));
    assert.ok(!fs.existsSync(path.join(exportRoot, "Samples~")));
    // (e) The payload manifest and its meta survive.
    assert.ok(fs.existsSync(path.join(exportRoot, "package.json")));
    assert.ok(fs.existsSync(path.join(exportRoot, "package.json.meta")));

    // (d) The generated exporter lives OUTSIDE the export root.
    const staged = walk(assetsRoot);
    const exporters = staged.filter((e) => e.full.endsWith("DxmUnityPackageExporter.cs"));
    assert.deepEqual(
      exporters.map((e) => e.full),
      [path.join(assetsRoot, "Editor", "DxmUnityPackageExporter.cs")]
    );

    // (c) Every staged file AND folder under the export root has a sibling
    // .meta, and no .meta anywhere in the staged Assets tree is an orphan.
    for (const entry of walk(exportRoot)) {
      if (!entry.full.endsWith(".meta")) {
        assert.ok(fs.existsSync(`${entry.full}.meta`), `missing .meta for ${entry.full}`);
      }
    }
    for (const entry of staged) {
      if (!entry.isDirectory && entry.full.endsWith(".meta")) {
        const sibling = entry.full.slice(0, -".meta".length);
        assert.ok(fs.existsSync(sibling), `orphan .meta ${entry.full}`);
      }
    }

    // Deterministic ancestor folder metas: standard folderAsset yaml...
    const firstMetas = ANCESTOR_METAS.map((relative) => {
      const content = fs.readFileSync(path.join(projectPath, relative), "utf8");
      assert.match(
        content,
        /^fileFormatVersion: 2\nguid: [0-9a-f]{32}\nfolderAsset: yes\nDefaultImporter:\n/,
        `unexpected folder meta shape for ${relative}`
      );
      return content;
    });
    assert.equal(new Set(firstMetas.map((c) => c.match(/guid: (\w+)/)[1])).size, 3);
    // ...and byte-identical across two consecutive stagings (the script wipes
    // and rebuilds the project dir on every run).
    runStageOnly(stagingRoot, projectPath);
    const secondMetas = ANCESTOR_METAS.map((relative) =>
      fs.readFileSync(path.join(projectPath, relative), "utf8")
    );
    assert.deepEqual(secondMetas, firstMetas);
  } finally {
    fs.rmSync(stagingRoot, { recursive: true, force: true });
  }
});

// Single source of truth for the built-in module set the ephemeral export
// project must enable; both export-unitypackage.ps1 and this guard read it.
const MODULE_DATA_PATH = path.join(REPO_ROOT, "scripts", "unity", "unity-builtin-modules.json");

test("export-unitypackage -StageOnly enables the built-in Unity modules", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const stagingRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-unitypackage-manifest-"));
  const projectPath = path.join(stagingRoot, "project");
  try {
    runStageOnly(stagingRoot, projectPath);
    const manifest = JSON.parse(
      fs.readFileSync(path.join(projectPath, "Packages", "manifest.json"), "utf8")
    );
    const deps = manifest.dependencies || {};

    // The proven v3.1.0 failure: EditorGUIUtility's base GUIUtility lives in
    // UnityEngine.IMGUIModule, which an empty `dependencies: {}` never enables.
    assert.ok(
      Object.prototype.hasOwnProperty.call(deps, "com.unity.modules.imgui"),
      "manifest must enable com.unity.modules.imgui (the module that broke v3.1.0)"
    );

    // Every entry in the single-source data file must reach the manifest, so the
    // export project compiles the payload exactly like a default Unity project.
    const required = JSON.parse(fs.readFileSync(MODULE_DATA_PATH, "utf8")).dependencies;
    for (const id of Object.keys(required)) {
      assert.ok(
        Object.prototype.hasOwnProperty.call(deps, id),
        `manifest is missing required dependency ${id}`
      );
    }
  } finally {
    fs.rmSync(stagingRoot, { recursive: true, force: true });
  }
});
