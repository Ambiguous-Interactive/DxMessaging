/**
 * @fileoverview Contract tests for the generated Unity package test harness.
 *
 * CI creates an ephemeral Unity project under .artifacts/ that imports this
 * repo as a UPM package and exposes the package's Tests/ asmdefs through
 * `testables`. The repository itself remains a package, not a checked-in
 * Unity project.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const yaml = require("yaml");
const { spawnSync } = require("child_process");

const { sandboxHostFolderEnv } = require("../lib/spawn-env-sandbox");
const { getPropertyValue, hasElement, getElements } = require("../lib/msbuild-xml");
const { assertSpawnStatus } = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const CI_RUNNER = path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1");

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

describe("generated Unity test harness contract", () => {
  describe("scripts/unity/run-ci-tests.ps1", () => {
    let content;

    beforeAll(() => {
      expect(fs.existsSync(CI_RUNNER)).toBe(true);
      content = fs.readFileSync(CI_RUNNER, "utf8");
    });

    test("creates the Unity project only under .artifacts", () => {
      expect(content).toContain(".artifacts\\unity\\projects\\$Version-$Mode");
      expect(content).toContain("Initialize-EphemeralProject");
      expect(content).not.toContain(".unity-test-project");
    });

    test("generates a minimal manifest that imports this repo as the package under test", () => {
      expect(content).toContain("'com.unity.test-framework'");
      expect(content).toContain("'com.unity.test-framework.performance'");
      expect(content).toContain("'com.wallstop-studios.dxmessaging'");
      expect(content).toContain('"file:$packagePath"');
      expect(content).toContain("testables = @($PackageName)");
    });

    test("configures standalone Windows IL2CPP in the generated project", () => {
      expect(content).toContain("DxmCiTestConfigurator");
      expect(content).toContain("BuildTarget.StandaloneWindows64");
      expect(content).toContain("ScriptingImplementation.IL2CPP");
    });

    test("pre-creates the single Assets/Plugins analyzer copy before any Unity compile", () => {
      // The harness reproduces the SINGLE registration SetupCscRsp makes for
      // consumers by pre-creating the Assets/Plugins copy BEFORE Unity launches,
      // and writes NO csc.rsp (a second `-a:` registration there is what
      // double-registered the generator and broke 2021/2022 play/standalone).
      const initializeIndex = content.indexOf("function Initialize-EphemeralProject");
      const copyCallIndex = content.indexOf(
        "Copy-DxMessagingAnalyzersToAssets -Root $Root -Project $project",
        initializeIndex
      );
      const generateOnlyIndex = content.indexOf("if ($GenerateOnly)");
      const firstUnityLaunchIndex = content.indexOf(
        "Invoke-UnityNativeStartupProbe -EditorPath $UnityEditorPath"
      );

      expect(initializeIndex).toBeGreaterThanOrEqual(0);
      expect(copyCallIndex).toBeGreaterThan(initializeIndex);
      expect(copyCallIndex).toBeLessThan(generateOnlyIndex);
      expect(copyCallIndex).toBeLessThan(firstUnityLaunchIndex);
      expect(content).toContain("Assets\\Plugins\\Editor\\WallstopStudios.DxMessaging");
      // The harness must NOT write csc.rsp or generate `-a:` analyzer args -- that
      // was the duplicate registration. (SetupCscRsp owns csc.rsp at editor load.)
      expect(content).not.toContain("function New-CscRspContent");
      expect(content).not.toContain("Join-Path $project 'Assets\\csc.rsp'");
      expect(content).not.toContain('-a:`"$analyzerPath`"');
    });

    test("pre-created Assets copy carries the RoslynAnalyzer-labeled DxMessaging DLLs", () => {
      expect(content).toContain("function Copy-DxMessagingAnalyzersToAssets");
      expect(content).toContain("function Assert-DxMessagingAnalyzerDllsPresent");
      expect(content).toContain("WallstopStudios.DxMessaging.SourceGenerators.dll");
      expect(content).toContain("WallstopStudios.DxMessaging.Analyzer.dll");
      expect(content).toContain("Missing required DxMessaging analyzer DLL(s)");
      expect(content).toContain("RoslynAnalyzer");
      // A fresh GUID per copied .meta so the Assets copy never collides with the
      // package-resident asset.
      expect(content).toContain("[guid]::NewGuid().ToString('N')");
    });

    test("reports whether Unity compile logs mention DxMessaging analyzer arguments", () => {
      expect(content).toContain("function Write-AnalyzerSetupDiagnostics");
      expect(content).toContain(
        "Generated Assets/Plugins analyzer copy is missing the RoslynAnalyzer-labeled"
      );
      expect(content).toContain("Unity compile log mentioned DxMessaging source-generator arg");
      expect(content).toContain("Unity compile log mentioned DxMessaging analyzer arg");
      expect(content).toContain("Write-AnalyzerSetupDiagnostics -Project $ProjectPath");
    });

    test("GenerateOnly pre-creates the labeled Assets/Plugins analyzer copy and writes NO csc.rsp", () => {
      if (!pwshAvailable()) {
        console.warn("[unity-harness-contract] pwsh not found; skipping GenerateOnly assertion.");
        return;
      }

      const base = fs.mkdtempSync(path.join(require("os").tmpdir(), "dxm-generate-only-"));
      const repoRoot = path.join(base, "repo");
      const project = path.join(base, "project");
      const artifacts = path.join(base, "artifacts");
      try {
        fs.mkdirSync(path.join(repoRoot, "Runtime"), { recursive: true });
        fs.mkdirSync(path.join(repoRoot, "Editor", "Analyzers"), { recursive: true });
        fs.writeFileSync(path.join(repoRoot, "package.json"), "{}\n", "utf8");
        for (const dllName of [
          "WallstopStudios.DxMessaging.SourceGenerators.dll",
          "WallstopStudios.DxMessaging.Analyzer.dll"
        ]) {
          fs.writeFileSync(path.join(repoRoot, "Editor", "Analyzers", dllName), "stub", "utf8");
        }

        // Hermetic by construction: run-ci-tests.ps1 probes host-default FOLDER
        // vars (`$env:LOCALAPPDATA`, `${env:ProgramFiles}`, ...). Even though
        // -GenerateOnly exits before those probes today, build the spawn env via
        // sandboxHostFolderEnv (empty sandbox dirs under this run's temp base) so
        // this spawn stays inside the hermetic discipline and a future code path
        // that probes the host before -GenerateOnly cannot leak a real install.
        const hostEnvSandbox = path.join(base, "host-env-sandbox");
        const run = spawnSync(
          "pwsh",
          [
            "-NoProfile",
            "-NonInteractive",
            "-File",
            CI_RUNNER,
            "-UnityVersion",
            "2021.3.45f1",
            "-TestMode",
            "editmode",
            "-AssemblyNames",
            "WallstopStudios.DxMessaging.Tests.Editor",
            "-ArtifactsPath",
            artifacts,
            "-RepoRoot",
            repoRoot,
            "-ProjectPath",
            project,
            "-GenerateOnly"
          ],
          {
            env: sandboxHostFolderEnv(process.env, hostEnvSandbox),
            encoding: "utf8",
            maxBuffer: 16 * 1024 * 1024
          }
        );

        assertSpawnStatus(run, 0, expect.getState().currentTestName || "pwsh harness");

        // The harness pre-creates the SINGLE registration: the Assets/Plugins
        // copy, with the two analyzer DLLs RoslynAnalyzer-labeled. (SetupCscRsp
        // would make this same copy at editor load for a real consumer.)
        const copyDir = path.join(
          project,
          "Assets",
          "Plugins",
          "Editor",
          "WallstopStudios.DxMessaging"
        );
        for (const dllName of [
          "WallstopStudios.DxMessaging.SourceGenerators.dll",
          "WallstopStudios.DxMessaging.Analyzer.dll"
        ]) {
          const dll = path.join(copyDir, dllName);
          const meta = `${dll}.meta`;
          expect(fs.existsSync(dll)).toBe(true);
          expect(fs.existsSync(meta)).toBe(true);
          const metaText = fs.readFileSync(meta, "utf8");
          expect(metaText).toContain("RoslynAnalyzer");
          // EFFECTIVE proof: the generated meta excludes EVERY platform (Editor
          // included), so Unity treats the DLL as a RoslynAnalyzer-only analyzer,
          // never a managed precompiled assembly. An Editor-ENABLED copy here is the
          // exact misconfiguration that tripped Unity 2021's "Multiple precompiled
          // assemblies with the same name" abort. (\s spans the multi-line block;
          // "enabled: 1" appears only in a platformData block, never as
          // isOverridable/validateReferences.)
          expect(metaText).not.toMatch(/Editor:\s+Editor\s+second:\s+enabled:\s*1/);
          expect(metaText).not.toMatch(/enabled:\s*1/);
        }

        // And it must NOT write Assets/csc.rsp -- a second `-a:` registration
        // there is exactly what duplicated the generator. (SetupCscRsp manages
        // csc.rsp at editor load, which -GenerateOnly never reaches.)
        expect(fs.existsSync(path.join(project, "Assets", "csc.rsp"))).toBe(false);
      } finally {
        fs.rmSync(base, { recursive: true, force: true });
      }
    });

    test("validates real NUnit output instead of trusting the Unity process exit code", () => {
      expect(content).toContain("Test-NUnitResults");
      expect(content).toContain("SelectSingleNode('//test-run')");
      expect(content).toContain("$total -lt 1");
      expect(content).toContain("$failed -gt 0");
      expect(content).toContain("Write-UnityResultFailureDiagnostics");
      expect(content).toContain("Write-AnalyzerSetupDiagnostics -Project $Project");
      expect(content).toContain("warning CS8032");
      expect(content).toContain("Unity exited with code 0 but did not write NUnit results");
    });

    test("the DURABLE ARTIFACT is the source of truth; a non-zero Unity exit is advisory", () => {
      // The editor invocation RETURNS the exit code (it no longer throws on a
      // non-zero value), and the artifact validators gate pass/fail: a valid
      // results.xml / configure marker / built player exe wins even when Unity
      // crashed in a background thread DURING shutdown (the 0xC0000005
      // DirectoryMonitor class) and exited non-zero.
      expect(content).toContain("$runExit = Invoke-UnityEditor");
      expect(content).toContain("-UnityExitCode $runExit");
      // The benign post-work shutdown crash is surfaced as a non-fatal warning.
      expect(content).toContain("function Write-UnityBenignExitWarning");
      expect(content).toContain("benign post-work shutdown crash");
      // The configure pass is gated on the marker, not the exit code.
      expect(content).toContain("function Test-UnityConfigureMarker");
      expect(content).toContain("$env:DXM_CONFIGURE_MARKER_PATH = $configureMarkerPath");
      // The crash exit code is decoded for humans (0xC0000005 / ACCESS_VIOLATION).
      expect(content).toContain("STATUS_ACCESS_VIOLATION");
      // The retired throw-on-non-zero wrapper is gone as a definition.
      expect(content).not.toMatch(/function\s+Invoke-UnityEditorWithFailureDiagnostics\b/);
    });

    test("wires Unity Accelerator and UPM caches without mutating package source", () => {
      expect(content).toContain("UNITY_ACCELERATOR_ENDPOINT");
      expect(content).toContain("-cacheServerEndpoint");
      expect(content).toContain("UPM_CACHE_ROOT");
      expect(content).toContain("UPM_NPM_CACHE_PATH");
      expect(content).toContain("LOCALAPPDATA");
    });
  });

  describe("default runtime test asmdef", () => {
    const runtimeAsmdefPath = path.join(
      REPO_ROOT,
      "Tests",
      "Runtime",
      "WallstopStudios.DxMessaging.Tests.Runtime.asmdef"
    );

    test("does not reference optional DI integration assemblies", () => {
      const parsed = JSON.parse(fs.readFileSync(runtimeAsmdefPath, "utf8"));
      expect(parsed.references).toEqual(expect.any(Array));
      expect(parsed.references).not.toContain("WallstopStudios.DxMessaging.Reflex");
      expect(parsed.references).not.toContain("WallstopStudios.DxMessaging.VContainer");
      expect(parsed.references).not.toContain("WallstopStudios.DxMessaging.Zenject");
    });
  });

  describe("default benchmark asmdefs", () => {
    const externalComparisonRefs = ["Zenject", "MessagePipe", "UniRx", "UniTask"];

    test.each([
      ["Tests/Editor/Benchmarks/WallstopStudios.DxMessaging.Tests.00.Editor.Benchmarks.asmdef"],
      ["Tests/Editor/Allocations/WallstopStudios.DxMessaging.Tests.Editor.Allocations.asmdef"]
    ])("%s does not require external comparison packages", (relPath) => {
      const parsed = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, relPath), "utf8"));
      for (const externalRef of externalComparisonRefs) {
        expect(parsed.references).not.toContain(externalRef);
      }
    });
  });

  describe("comparison benchmark asmdef", () => {
    const comparisonAsmdefPath = path.join(
      REPO_ROOT,
      "Tests",
      "Editor",
      "Comparisons",
      "WallstopStudios.DxMessaging.Tests.00.Editor.Comparisons.asmdef"
    );

    test("requires external comparison package symbols before Unity compiles it", () => {
      const parsed = JSON.parse(fs.readFileSync(comparisonAsmdefPath, "utf8"));
      expect(parsed.defineConstraints).toEqual(
        expect.arrayContaining([
          "MESSAGEPIPE_PRESENT",
          "UNIRX_PRESENT",
          "ZENJECT_PRESENT",
          "UNITASK_PRESENT"
        ])
      );
      expect(parsed.versionDefines).toEqual(
        expect.arrayContaining([
          expect.objectContaining({
            name: "com.cysharp.messagepipe",
            define: "MESSAGEPIPE_PRESENT"
          }),
          expect.objectContaining({ name: "com.svermeulen.extenject", define: "ZENJECT_PRESENT" }),
          expect.objectContaining({ name: "com.cysharp.unitask", define: "UNITASK_PRESENT" })
        ])
      );
    });
  });

  // Sanity: unused yaml import elsewhere would be dead weight; reference it
  // here so removing the dependency without updating other suites still trips
  // CI early. (Other tests use yaml extensively.)
  test("the yaml package is available for downstream YAML-shape suites", () => {
    expect(typeof yaml.parse).toBe("function");
  });

  describe("Unity 2021 compiler compatibility guards", () => {
    const tokenPath = path.join(REPO_ROOT, "Runtime", "Core", "MessageRegistrationToken.cs");
    const compilerHostProjects = [
      [
        "source generator",
        "SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators/WallstopStudios.DxMessaging.SourceGenerators.csproj"
      ],
      [
        "analyzer",
        "SourceGenerators/WallstopStudios.DxMessaging.Analyzer/WallstopStudios.DxMessaging.Analyzer.csproj"
      ]
    ];

    test("runtime sources avoid null-conditional out-var definite-assignment patterns", () => {
      const runtimeFiles = listTrackedRuntimeSources();
      expect(runtimeFiles.length).toBeGreaterThan(0);
      const violations = [];
      for (const relPath of runtimeFiles) {
        const source = fs.readFileSync(path.join(REPO_ROOT, relPath), "utf8");
        const pattern =
          /\?\.\s*[A-Za-z_][A-Za-z0-9_]*\s*\([^)]*\bout\s+(?:var|[A-Za-z_][A-Za-z0-9_<>,.?[\]\s]*)\s+[A-Za-z_][A-Za-z0-9_]*/g;
        if (pattern.test(source)) {
          violations.push(relPath);
        }
      }

      expect(violations).toEqual([]);
      const tokenSource = fs.readFileSync(tokenPath, "utf8");
      expect(tokenSource).toContain(
        "_deregistrations.Remove(handle, out Action deregistrationAction)"
      );
    });

    test.each(compilerHostProjects)(
      "%s production compiler host stays pinned to Roslyn 3.8 for Unity 2021",
      (_label, relPath) => {
        const source = fs.readFileSync(path.join(REPO_ROOT, relPath), "utf8");

        // Pinned to Roslyn 3.8 (not any 4.x). getPropertyValue/hasElement parse
        // structurally, so this stays correct regardless of XML line-wrapping or
        // attribute order. The equality check below also rules out any 4.x value.
        expect(getPropertyValue(source, "MicrosoftCodeAnalysisVersion")).toBe("3.8.0");
        expect(
          hasElement(source, {
            name: "PackageReference",
            attributes: { Include: "Microsoft.CodeAnalysis.CSharp", Version: "3.8.0" }
          })
        ).toBe(true);

        // Structural negative guard preserving the original `not.toMatch(/4\./)`
        // intent: NO Microsoft.CodeAnalysis.CSharp PackageReference may carry a
        // 4.x Version, and no <MicrosoftCodeAnalysisVersion> may be 4.x, even if a
        // stray second occurrence were added later. Enumerated via getElements so
        // it is invariant to wrapping/attribute order (the old regex was not).
        const csharpRefs = getElements(source, "PackageReference").filter(
          (element) => element.attributes.Include === "Microsoft.CodeAnalysis.CSharp"
        );
        expect(csharpRefs.length).toBeGreaterThan(0);
        for (const ref of csharpRefs) {
          expect(ref.attributes.Version).toBe("3.8.0");
        }
        // EVERY declared <MicrosoftCodeAnalysisVersion> (not just the first) must
        // be the 3.8 pin and never 4.x, mirroring the original GLOBAL negative
        // regex `not.toMatch(/<MicrosoftCodeAnalysisVersion>4\./)`: a stray second
        // 4.x element added later anywhere in the file is still caught. Read by
        // inner value via getElements so it is wrapping/CRLF-invariant.
        const versionElements = getElements(source, "MicrosoftCodeAnalysisVersion");
        expect(versionElements.length).toBeGreaterThan(0);
        for (const element of versionElements) {
          expect(element.value).toBe("3.8.0");
          expect(element.value).not.toMatch(/^4\./);
        }
      }
    );
  });
});

function listTrackedRuntimeSources() {
  const result = spawnSync("git", ["ls-files", "Runtime/**/*.cs"], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  expect(result.status).toBe(0);
  return result.stdout
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
}
