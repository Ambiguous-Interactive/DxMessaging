"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const RUN_CI_SCRIPT_PATH = path.join(__dirname, "..", "unity", "run-ci-tests.ps1");
// Drift-guard for the test-suite-performance contract: Initialize-EphemeralProject
// must emit a ProjectSettings/EditorSettings.asset that disables enter-play-mode
// domain + scene reload (value 3) so the PlayMode CI legs skip the per-entry
// reload. This guards the COMMITTED CI enforcement; the local .unity-test-project
// copy is gitignored, so the runner emit is the source of truth for CI. See
// docs/runbooks/test-suite-performance.md and the Fast Unity Tests skill.
const runCiTests = fs.readFileSync(RUN_CI_SCRIPT_PATH, "utf8");
const exportUnityPackage = fs.readFileSync(
  path.join(__dirname, "..", "unity", "export-unitypackage.ps1"),
  "utf8"
);
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

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function createGenerateOnlyRepo(root) {
  const analyzerRoot = path.join(root, "Runtime", "Analyzers");
  fs.mkdirSync(analyzerRoot, { recursive: true });
  fs.writeFileSync(path.join(root, "package.json"), "{}\n", "utf8");
  for (const dllName of [
    "WallstopStudios.DxMessaging.SourceGenerators.dll",
    "WallstopStudios.DxMessaging.Analyzer.dll"
  ]) {
    fs.writeFileSync(path.join(analyzerRoot, dllName), "", "utf8");
  }
}

function runGenerateOnly(stagingRoot, repoRoot, artifactsPath, options = {}) {
  const args = [
    "-NoLogo",
    "-NoProfile",
    "-NonInteractive",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    RUN_CI_SCRIPT_PATH,
    "-UnityVersion",
    UNITY_VERSION,
    "-TestMode",
    "editmode",
    "-AssemblyNames",
    "WallstopStudios.DxMessaging.Tests.Editor",
    "-ArtifactsPath",
    artifactsPath,
    "-RepoRoot",
    repoRoot
  ];
  if (options.projectPath) {
    args.push("-ProjectPath", options.projectPath);
  }
  args.push("-GenerateOnly");

  return spawnSync("pwsh", args, { cwd: stagingRoot, encoding: "utf8", timeout: 120000 });
}

test("run-ci-tests emits EnterPlayModeOptions reload-disable for CI projects", () => {
  assert.match(
    runCiTests,
    /m_EnterPlayModeOptionsEnabled:\s*1/,
    "run-ci-tests.ps1 must emit m_EnterPlayModeOptionsEnabled: 1"
  );
  assert.match(
    runCiTests,
    /m_EnterPlayModeOptions:\s*3/,
    "run-ci-tests.ps1 must emit m_EnterPlayModeOptions: 3 (DisableDomainReload | DisableSceneReload)"
  );
  assert.match(
    runCiTests,
    /\[System\.IO\.Path\]::Combine\(\$project,\s*'ProjectSettings',\s*'EditorSettings\.asset'\)/,
    "the EnterPlayModeOptions block must be written to ProjectSettings/EditorSettings.asset through native path segments"
  );
});

test("Unity scripts clear native exit codes that are treated as nonfatal", () => {
  for (const text of [runCiTests, exportUnityPackage]) {
    assert.match(
      text,
      /(?=[\s\S]*function Clear-NonFatalNativeExitCode[\s\S]*\$global:LASTEXITCODE = 0)(?=[\s\S]*\$exitCode = \$LASTEXITCODE\s+Clear-NonFatalNativeExitCode -Context \$Label)(?=[\s\S]*finally \{\s+Clear-NonFatalNativeExitCode -Context 'Unity license return cleanup'\s+\})/
    );
  }
});

test("run-ci-tests -GenerateOnly defaults to managed artifact project and cache paths", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const stagingRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-run-ci-generate-"));
  const fakeRepoRoot = path.join(stagingRoot, "repo");
  const artifactsPath = path.join(stagingRoot, "artifacts");
  const projectPath = path.join(
    fakeRepoRoot,
    ".artifacts",
    "unity",
    "projects",
    `${UNITY_VERSION}-editmode`
  );
  const cacheRoot = path.join(fakeRepoRoot, ".artifacts", "unity", "cache", UNITY_VERSION);

  try {
    createGenerateOnlyRepo(fakeRepoRoot);
    const result = runGenerateOnly(stagingRoot, fakeRepoRoot, artifactsPath);

    assert.equal(result.status, 0, `GenerateOnly failed:\n${result.stdout}\n${result.stderr}`);
    for (const relative of [
      ["Packages", "manifest.json"],
      ["ProjectSettings", "EditorSettings.asset"],
      ["Assets", "Editor", "DxmCiTestConfigurator.cs"],
      [".dxmessaging-ci-project"],
      ["Library"]
    ]) {
      assert.ok(fs.existsSync(path.join(projectPath, ...relative)), relative.join("/"));
    }
    for (const cacheName of ["upm", "npm"]) {
      assert.ok(fs.existsSync(path.join(cacheRoot, cacheName)), cacheName);
    }
    assert.match(result.stdout, new RegExp(escapeRegExp(`ProjectPath: ${projectPath}`)));
  } finally {
    fs.rmSync(stagingRoot, { recursive: true, force: true });
  }
});

test("run-ci-tests -GenerateOnly refuses an unowned existing custom ProjectPath", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const stagingRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-run-ci-unsafe-"));
  const fakeRepoRoot = path.join(stagingRoot, "repo");
  const artifactsPath = path.join(stagingRoot, "artifacts");
  const existingProjectPath = path.join(stagingRoot, "consumer-project");
  const consumerFile = path.join(existingProjectPath, "keep.txt");

  try {
    createGenerateOnlyRepo(fakeRepoRoot);
    fs.mkdirSync(existingProjectPath, { recursive: true });
    fs.writeFileSync(consumerFile, "do not delete", "utf8");
    const cases = [
      {
        projectPath: existingProjectPath,
        pattern: /lacks the ownership marker/,
        after: () => assert.equal(fs.readFileSync(consumerFile, "utf8"), "do not delete")
      },
      {
        projectPath: path.join(artifactsPath, "project"),
        pattern: /inside the uploaded artifacts directory/
      }
    ];

    const managedLink = path.join(fakeRepoRoot, ".artifacts", "unity", "projects", "linked");
    try {
      fs.mkdirSync(path.dirname(managedLink), { recursive: true });
      fs.mkdirSync(path.join(stagingRoot, "linked-target"), { recursive: true });
      fs.symlinkSync(path.join(stagingRoot, "linked-target"), managedLink, "dir");
      cases.push({ projectPath: managedLink, pattern: /symlink or reparse point/ });
    } catch {
      fs.rmSync(managedLink, { recursive: true, force: true });
    }

    for (const testCase of cases) {
      const result = runGenerateOnly(stagingRoot, fakeRepoRoot, artifactsPath, testCase);
      assert.notEqual(result.status, 0, `${testCase.projectPath} should be rejected`);
      assert.match(`${result.stdout}\n${result.stderr}`, testCase.pattern);
      testCase.after?.();
    }
  } finally {
    fs.rmSync(stagingRoot, { recursive: true, force: true });
  }
});
