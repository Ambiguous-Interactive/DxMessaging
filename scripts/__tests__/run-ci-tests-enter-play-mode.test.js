"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const RUN_CI_SCRIPT_PATH = path.join(__dirname, "..", "unity", "run-ci-tests.ps1");
// prettier-ignore
const ROSLYNATOR_ANALYZER_FILES = ["Roslynator.CSharp.Analyzers.dll", "Roslynator_Analyzers_Roslynator.Common.dll", "Roslynator_Analyzers_Roslynator.Core.dll", "Roslynator_Analyzers_Roslynator.CSharp.dll"];
// prettier-ignore
const INTEGRATION_PACKAGES = { "com.gustavopsantos.reflex": "9.2.1", "com.svermeulen.extenject": "9.2.0-stcf3", "jp.hadashikick.vcontainer": "1.19.0" };
// Drift-guard the committed reload-disabled CI project; the local host is gitignored.
const runCiTests = fs.readFileSync(RUN_CI_SCRIPT_PATH, "utf8");
// prettier-ignore
const exportUnityPackage = fs.readFileSync(path.join(__dirname, "..", "unity", "export-unitypackage.ps1"), "utf8");
const UNITY_VERSION = "2022.3.45f1";

function commandExists(command) {
  // prettier-ignore
  const result = spawnSync(command, ["-NoLogo", "-NoProfile", "-Command", "$PSVersionTable.PSVersion"],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  return !result.error && result.status === 0;
}

const HAS_PWSH = commandExists("pwsh");

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function createGenerateOnlyRepo(root) {
  const analyzerRoot = path.join(root, "Runtime", "Analyzers");
  fs.mkdirSync(analyzerRoot, { recursive: true });
  // prettier-ignore
  const compileInputs = ["Diagnostics Tooling Exerciser/A.cs", "Diagnostics Tooling Exerciser/Sample.asmdef", "Mini Combat/A.cs", "Mini Combat/Sample.asmdef", "UI Buttons + Inspector/A.cs", "UI Buttons + Inspector/Sample.asmdef", "DI/VContainer/ConditionalSample.cs"];
  // prettier-ignore
  for (const relative of compileInputs) { const fullPath = path.join(root, "Samples~", ...relative.split("/")); fs.mkdirSync(path.dirname(fullPath), { recursive: true }); fs.writeFileSync(fullPath, "", "utf8"); }
  fs.writeFileSync(path.join(root, "package.json"), "{}\n", "utf8");
  const analyzerSourceRoot = path.join(root, ".github", "analyzers");
  fs.mkdirSync(analyzerSourceRoot, { recursive: true });
  // prettier-ignore
  for (const name of ROSLYNATOR_ANALYZER_FILES) fs.copyFileSync(path.join(__dirname, "..", "..", ".github", "analyzers", name), path.join(analyzerSourceRoot, name));
  // prettier-ignore
  const comparisonPackages = { registry: { name: "package.openupm.com", url: "https://package.openupm.com", scopes: ["com.gustavopsantos", "com.svermeulen", "jp.hadashikick"] }, integrationPackages: INTEGRATION_PACKAGES };
  // prettier-ignore
  fs.writeFileSync(path.join(root, ".github", "comparison-packages.json"), JSON.stringify(comparisonPackages), "utf8");
  for (const dllName of [
    "WallstopStudios.DxMessaging.SourceGenerators.dll",
    "WallstopStudios.DxMessaging.Analyzer.dll"
  ]) {
    fs.writeFileSync(path.join(analyzerRoot, dllName), "", "utf8");
  }
}

function runGenerateOnly(stagingRoot, repoRoot, artifactsPath, options = {}) {
  // prettier-ignore
  const args = ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", RUN_CI_SCRIPT_PATH, "-UnityVersion", UNITY_VERSION, "-TestMode", "editmode", "-AssemblyNames", "WallstopStudios.DxMessaging.Tests.Editor", "-ArtifactsPath", artifactsPath, "-RepoRoot", repoRoot];
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
  // `.artifacts/u`, kept short for the Windows MAX_PATH budget (issue #357).
  const projectPath = path.join(fakeRepoRoot, ".artifacts", "u", `${UNITY_VERSION}-editmode`);
  const cacheRoot = path.join(fakeRepoRoot, ".artifacts", "unity", "cache", UNITY_VERSION);

  try {
    createGenerateOnlyRepo(fakeRepoRoot);
    const result = runGenerateOnly(stagingRoot, fakeRepoRoot, artifactsPath);

    assert.equal(result.status, 0, `GenerateOnly failed:\n${result.stdout}\n${result.stderr}`);
    // prettier-ignore
    const analyzerFiles = ROSLYNATOR_ANALYZER_FILES.flatMap((name) => [`Assets/${name}`, `Assets/${name}.meta`]);
    // prettier-ignore
    const expectedFiles = ["Packages/manifest.json", "ProjectSettings/EditorSettings.asset", "Assets/Editor/DxmCiTestConfigurator.cs", "Assets/csc.rsp", ...analyzerFiles, "Assets/DxmCiSamples/Diagnostics Tooling Exerciser/Sample.asmdef", "Assets/DxmCiSamples/Mini Combat/Sample.asmdef", "Assets/DxmCiSamples/UI Buttons + Inspector/Sample.asmdef", "Assets/DxmCiSamples/DI/VContainer/ConditionalSample.cs", "Assets/DxmCiSamples/DI/DxmCi.Samples.DI.asmdef", ".dxmessaging-ci-project", "Library"];
    for (const relative of expectedFiles) {
      assert.ok(fs.existsSync(path.join(projectPath, ...relative.split("/"))), relative);
    }
    const cscRsp = fs.readFileSync(path.join(projectPath, "Assets", "csc.rsp"), "utf8");
    // prettier-ignore
    const expectedCscOptions = ["-warnaserror", "-warn:9999", ...ROSLYNATOR_ANALYZER_FILES.map((name) => `-analyzer:"${path.join(projectPath, "Assets", name)}"`)];
    // prettier-ignore
    assert.equal(cscRsp.replace(/^\uFEFF/, "").replace(/\r\n/g, "\n").trim(), expectedCscOptions.join("\n"));
    // prettier-ignore
    for (const name of ROSLYNATOR_ANALYZER_FILES) assert.doesNotMatch(fs.readFileSync(path.join(projectPath, "Assets", `${name}.meta`), "utf8"), /RoslynAnalyzer/);
    // prettier-ignore
    const manifest = JSON.parse(fs.readFileSync(path.join(projectPath, "Packages", "manifest.json"), "utf8"));
    // prettier-ignore
    for (const [name, version] of Object.entries(INTEGRATION_PACKAGES)) assert.equal(manifest.dependencies[name], version);
    // prettier-ignore
    const diAsmdef = JSON.parse(fs.readFileSync(path.join(projectPath, "Assets", "DxmCiSamples", "DI", "DxmCi.Samples.DI.asmdef"), "utf8"));
    // prettier-ignore
    assert.deepEqual(diAsmdef.versionDefines.map(({ define }) => define).sort(), ["REFLEX_PRESENT", "VCONTAINER_PRESENT", "ZENJECT_PRESENT"]);
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
