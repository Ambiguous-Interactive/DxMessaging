"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPT_PATH = path.join(REPO_ROOT, "scripts", "unity", "export-unitypackage.ps1");
const UNITY_VERSION = "2022.3.45f1";

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

function swapAsciiCase(value) {
  return value.replace(/[A-Za-z]/g, (character) =>
    character === character.toUpperCase() ? character.toLowerCase() : character.toUpperCase()
  );
}

function runStageOnlyRaw(stagingRoot, projectPath, options = {}) {
  const artifactsPath = options.artifactsPath ?? path.join(stagingRoot, "artifacts");
  const repoRoot = options.repoRoot ?? REPO_ROOT;
  const args = [
    "-NoLogo",
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    SCRIPT_PATH,
    "-UnityVersion",
    UNITY_VERSION,
    "-RepoRoot",
    repoRoot,
    "-ArtifactsPath",
    artifactsPath
  ];
  if (projectPath) {
    args.push("-ProjectPath", projectPath);
  }
  if (options.outputPath) {
    args.push("-OutputPath", options.outputPath);
  }
  args.push("-StageOnly");

  return spawnSync("pwsh", args, { cwd: stagingRoot, encoding: "utf8", timeout: 600000 });
}

function runStageOnly(stagingRoot, projectPath) {
  const result = runStageOnlyRaw(stagingRoot, projectPath);
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

function createMinimalPackageRepo(root) {
  fs.mkdirSync(path.join(root, "Runtime"), { recursive: true });
  const packageJson = {
    name: "com.wallstop-studios.dxmessaging",
    version: "0.0.0",
    files: ["Runtime/**", "package.json"]
  };
  fs.writeFileSync(path.join(root, "package.json"), `${JSON.stringify(packageJson)}\n`, "utf8");
  fs.writeFileSync(path.join(root, "Runtime", "DxMessaging.Runtime.asmdef"), "{}\n", "utf8");
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

test("export-unitypackage -StageOnly defaults to the managed artifact project path", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const stagingRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-unitypackage-default-"));
  const fakeRepoRoot = path.join(stagingRoot, "repo");
  const artifactsPath = path.join(stagingRoot, "artifacts");
  const defaultProjectPath = path.join(
    fakeRepoRoot,
    ".artifacts",
    "unity",
    "projects",
    `${UNITY_VERSION}-unitypackage`
  );

  try {
    createMinimalPackageRepo(fakeRepoRoot);

    const defaultResult = runStageOnlyRaw(stagingRoot, undefined, {
      repoRoot: fakeRepoRoot,
      artifactsPath
    });
    assert.equal(
      defaultResult.status,
      0,
      `default stage-only run failed:\n${defaultResult.stdout}\n${defaultResult.stderr}`
    );
    assert.ok(fs.existsSync(path.join(defaultProjectPath, ".dxmessaging-unitypackage-project")));
    assert.ok(fs.existsSync(path.join(defaultProjectPath, "Packages", "manifest.json")));
    assert.ok(
      fs.existsSync(path.join(defaultProjectPath, "Assets", "Editor", "DxmUnityPackageExporter.cs"))
    );
  } finally {
    fs.rmSync(stagingRoot, { recursive: true, force: true });
  }
});

test("export-unitypackage -StageOnly refuses to delete an unowned existing ProjectPath", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const stagingRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-unitypackage-unsafe-"));
  const projectPath = path.join(stagingRoot, "consumer-project");
  const consumerFile = path.join(projectPath, "SomeOtherPlugin.dll");
  try {
    fs.mkdirSync(projectPath, { recursive: true });
    fs.writeFileSync(consumerFile, "do not delete", "utf8");

    const result = runStageOnlyRaw(stagingRoot, projectPath);
    assert.notEqual(result.status, 0, "stage-only run should reject the unsafe project path");
    assert.match(
      `${result.stdout}\n${result.stderr}`,
      /Refusing to delete existing ProjectPath/,
      "failure should explain that the existing project path is not owned by the exporter"
    );
    assert.equal(
      fs.readFileSync(consumerFile, "utf8"),
      "do not delete",
      "unsafe cleanup must not delete consumer-owned files"
    );
  } finally {
    fs.rmSync(stagingRoot, { recursive: true, force: true });
  }
});

test("export-unitypackage -StageOnly rejects destructive ProjectPath values before cleanup", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const stagingRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-unitypackage-paths-"));
  const artifactRoot = path.join(stagingRoot, "artifacts");
  const cases = [
    ["filesystem root", path.parse(stagingRoot).root, /filesystem root/],
    ["repository root", REPO_ROOT, /repository root/],
    ["case-variant repository root", swapAsciiCase(REPO_ROOT), /repository root/],
    ["repository parent", path.dirname(REPO_ROOT), /parent of the repository root/],
    ["artifacts directory", artifactRoot, /artifacts directory/],
    [
      "project inside artifacts directory",
      path.join(artifactRoot, "project"),
      /inside the uploaded artifacts directory/
    ],
    [
      "output directory parent",
      path.join(stagingRoot, "project-parent"),
      /unitypackage output directory/,
      path.join(stagingRoot, "project-parent", "out", "package.unitypackage")
    ],
    [
      "repo-contained unmanaged project",
      path.join(REPO_ROOT, ".artifacts", "unity", "projects", "unmanaged"),
      /Repo-contained export projects must live/
    ]
  ];

  try {
    const fakeRepoRoot = path.join(stagingRoot, "repo");
    const managedLink = path.join(
      fakeRepoRoot,
      ".artifacts",
      "unity",
      "projects",
      "linked-unitypackage"
    );
    try {
      createMinimalPackageRepo(fakeRepoRoot);
      fs.mkdirSync(path.dirname(managedLink), { recursive: true });
      fs.mkdirSync(path.join(stagingRoot, "linked-target"), { recursive: true });
      fs.symlinkSync(path.join(stagingRoot, "linked-target"), managedLink, "dir");
      cases.push(["managed symlink", managedLink, /symlink or reparse point/, null, fakeRepoRoot]);
    } catch {
      fs.rmSync(managedLink, { recursive: true, force: true });
    }

    for (const [name, projectPath, pattern, outputPath, repoRoot] of cases) {
      const result = runStageOnlyRaw(stagingRoot, projectPath, {
        artifactsPath: artifactRoot,
        outputPath,
        repoRoot
      });
      assert.notEqual(result.status, 0, `${name} should be rejected`);
      assert.match(
        `${result.stdout}\n${result.stderr}`,
        pattern,
        `${name} should explain the blocked destructive path`
      );
    }
    assert.ok(fs.existsSync(path.join(REPO_ROOT, "package.json")));
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
