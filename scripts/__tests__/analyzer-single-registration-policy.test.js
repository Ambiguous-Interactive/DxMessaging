/**
 * @fileoverview Guards for DxMessaging Unity test-assembly SELECTION and the
 * skip-on-empty CI contract.
 *
 * (1) asmdef-discovery must add ONLY DxMessaging-owned test asmdefs to the Unity
 *     `-assemblyNames` list, on every target. A foreign asmdef that happens to
 *     live under `Tests/` (for example one pulled in by an external comparison
 *     package) would not compile against the harness manifest and would fail the
 *     run for a reason unrelated to DxMessaging.
 * (2) When discovery resolves NO DxMessaging assembly for a target, the CI run is
 *     SKIPPED (not hard-failed). That contract spans three files: the compute
 *     action emits `is-empty` -> the workflow gates provision/lock/run on it ->
 *     the verify action treats the absence of results as an expected skip. These
 *     static pins lock the whole contract so a future refactor cannot silently
 *     delete one leg of it and leave the others dangling.
 *
 * (3) The analyzer/source-generator must be registered with the compiler EXACTLY
 *     ONCE in the CI ephemeral project. `Editor/SetupCscRsp.cs` copies those DLLs
 *     into the consuming project's `Assets/` ON PURPOSE (that is how the generator
 *     runs when the package lives under `Packages/`), so the harness must NOT add
 *     a second registration. It previously pre-wrote `Assets/csc.rsp` with `-a:`
 *     entries, which -- together with SetupCscRsp's Assets copy -- handed the SAME
 *     generator to the compiler from two paths (CS0102 / PrecompiledAssemblyException
 *     on 2021/2022 play/standalone). The harness now pre-creates ONLY the Assets
 *     copy and writes no csc.rsp; a runner-side diagnostic names any recurrence.
 *
 * Fast static + in-memory assertions only (no Unity, no spawn). The harness's
 * project generation is exercised end-to-end by unity-test-harness-contract.test.js
 * (the GenerateOnly spawn test); the verify-action skip behavior by
 * verify-unity-results-action.test.js.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const yaml = require("yaml");

const { tempDirTracker } = require("../lib/jest-fixtures");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const COMPUTE_ACTION = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "compute-unity-assemblies",
  "action.yml"
);
const VERIFY_ACTION = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "verify-unity-results",
  "action.yml"
);
const UNITY_WORKFLOW = path.join(REPO_ROOT, ".github", "workflows", "unity-tests.yml");
const RUN_CI_TESTS = path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1");

const asmdef = require("../unity/lib/asmdef-discovery.js");

function read(file) {
  return fs.readFileSync(file, "utf8");
}

// ---------------------------------------------------------------------------
// asmdef-discovery must never add a foreign (non-DxMessaging-owned) asmdef to
// the Unity -assemblyNames list, on any target. enumerateTestAsmdefs reads the
// live Tests/ tree, so these cases build throwaway repos with a controlled
// asmdef set to exercise the ownership gate in isolation.
// ---------------------------------------------------------------------------
describe("asmdef discovery excludes foreign assemblies", () => {
  const tempDirs = tempDirTracker();

  afterAll(() => {
    tempDirs.cleanup();
  });

  /**
   * Build a throwaway repo whose Tests/ tree contains exactly the given asmdefs.
   * @param {Array<{dir: string, name: string, includePlatforms?: string[]}>} entries
   * @returns {string} absolute repo root
   */
  function makeRepo(entries) {
    const root = tempDirs.make("asmdef-foreign");
    for (const entry of entries) {
      const dir = path.join(root, "Tests", entry.dir);
      fs.mkdirSync(dir, { recursive: true });
      const json = { name: entry.name };
      if (entry.includePlatforms) {
        json.includePlatforms = entry.includePlatforms;
      }
      fs.writeFileSync(path.join(dir, `${entry.name}.asmdef`), JSON.stringify(json));
    }
    return root;
  }

  // name -> expected isForeign. The foreign names deliberately include one that
  // ALSO matches a category regex (Zenject -> integration) to prove the
  // ownership gate wins over classification.
  const OWNERSHIP_CASES = [
    ["WallstopStudios.DxMessaging.Tests.Runtime", false],
    ["WallstopStudios.DxMessaging.Tests.Editor", false],
    ["AcmeCorp.External.Tests", true],
    ["SomeVendor.Zenject.Tests", true],
    ["DxMessaging.Tests.Runtime", true],
    ["WallstopStudios.DxMessagingExtras.Tests", true]
  ];

  test.each(OWNERSHIP_CASES)("'%s' isForeign === %s", (name, expectedForeign) => {
    const root = makeRepo([{ dir: name, name }]);
    const entries = asmdef.enumerateTestAsmdefs(root);
    expect(entries).toHaveLength(1);
    expect(entries[0].isForeign).toBe(expectedForeign);
  });

  test("a foreign asmdef is dropped from the include list but the owned one is kept", () => {
    const root = makeRepo([
      { dir: "Owned", name: "WallstopStudios.DxMessaging.Tests.Owned" },
      { dir: "Foreign", name: "AcmeCorp.External.Tests" },
      // Foreign AND integration-classified: must still be excluded.
      { dir: "ForeignZenject", name: "Vendor.Zenject.Tests" }
    ]);

    const included = asmdef.defaultIncludeAssemblies(root);
    expect(included).toEqual(["WallstopStudios.DxMessaging.Tests.Owned"]);

    const excluded = asmdef.defaultExcludeAssemblies(root);
    expect(excluded).toEqual(
      expect.arrayContaining(["AcmeCorp.External.Tests", "Vendor.Zenject.Tests"])
    );
    expect(excluded).not.toContain("WallstopStudios.DxMessaging.Tests.Owned");
  });

  test("a target with only foreign asmdefs yields an EMPTY include list (caller skips it)", () => {
    const root = makeRepo([
      { dir: "Foreign1", name: "AcmeCorp.External.Tests" },
      { dir: "Foreign2", name: "OtherVendor.Sample.Tests" }
    ]);
    expect(asmdef.defaultIncludeAssemblies(root)).toEqual([]);
  });
});

// ---------------------------------------------------------------------------
// Skip-on-empty CI contract. When discovery resolves NO DxMessaging assembly
// for a target, the run is SKIPPED (not hard-failed). The contract spans three
// files; these static pins lock every leg so a refactor cannot silently break
// it. The verify-action behavior itself is exercised end-to-end by
// verify-unity-results-action.test.js.
// ---------------------------------------------------------------------------
describe("skip-on-empty CI contract", () => {
  test("compute-unity-assemblies exposes assemblies + is-empty outputs", () => {
    const action = yaml.parse(read(COMPUTE_ACTION));
    expect(action.outputs).toBeDefined();
    expect(action.outputs.assemblies).toBeDefined();
    expect(action.outputs["is-empty"]).toBeDefined();
  });

  test("compute-unity-assemblies SKIPS on empty and only hard-fails on a discovery error", () => {
    const run = yaml.parse(read(COMPUTE_ACTION)).runs.steps[0].run;
    // Empty branch: notice + is-empty=true + exit 0 (NOT exit 1).
    expect(run).toMatch(/::notice::/);
    expect(run).toMatch(/is-empty=true[\s\S]*exit 0/);
    // The only hard failure is the discovery-script-failed branch.
    expect(run).toMatch(/discovery script failed[\s\S]*exit 1/);
  });

  test("verify-unity-results honors expected-empty (input + DXM_EXPECTED_EMPTY env + early skip)", () => {
    const action = yaml.parse(read(VERIFY_ACTION));
    expect(action.inputs["expected-empty"]).toBeDefined();
    const step = action.runs.steps[0];
    expect(String(step.env.DXM_EXPECTED_EMPTY)).toContain("inputs.expected-empty");
    expect(step.run).toContain("$env:DXM_EXPECTED_EMPTY");
    // The verify step gate must stay exactly !cancelled() (pinned elsewhere too);
    // the skip is driven by the env signal, never by widening that gate.
    expect(String(step.if).replace(/\s|\$\{\{|\}\}/g, "")).toBe("!cancelled()");
  });

  test("unity-tests.yml gates provision/acquire/run on is-empty and passes expected-empty to verify", () => {
    const workflow = yaml.parse(read(UNITY_WORKFLOW));
    const steps = workflow.jobs["unity-tests"].steps;
    const byName = (name) => steps.find((step) => step.name === name);

    expect(byName("Compute test assembly list").id).toBe("compute");

    const gate = "steps.compute.outputs.is-empty != 'true'";
    for (const name of [
      "Provision Unity Editor",
      "Acquire organization Unity lock",
      "Run Unity Test Runner"
    ]) {
      expect(String(byName(name).if)).toContain(gate);
    }

    const verify = byName("Verify tests actually ran");
    const verifyIf = String(verify.if).replace(/\s|\$\{\{|\}\}/g, "");
    expect(verifyIf).toContain("steps.compute.outcome=='success'");
    expect(verifyIf).toContain("steps.compute.outputs.is-empty=='true'");
    expect(verifyIf).toContain("steps.run_tests.outcome!='skipped'");
    expect(String(verify.with["expected-empty"])).toContain("steps.compute.outputs.is-empty");
  });
});

// ---------------------------------------------------------------------------
// Single analyzer registration in the CI ephemeral project. The harness must
// reproduce the consumer's SINGLE registration (the SetupCscRsp Assets copy) and
// must NOT add a second one via csc.rsp -- that double-registered the generator
// and broke 2021/2022 play/standalone. (The behavioral proof is the GenerateOnly
// spawn test in unity-test-harness-contract.test.js; these are fast static pins
// plus the recurrence diagnostic.)
// ---------------------------------------------------------------------------
describe("single analyzer registration (CI project generation)", () => {
  test("run-ci-tests.ps1 pre-creates the Assets copy and writes NO csc.rsp", () => {
    const run = read(RUN_CI_TESTS);
    expect(run).toContain("function Copy-DxMessagingAnalyzersToAssets");
    expect(run).toContain("Assets\\Plugins\\Editor\\WallstopStudios.DxMessaging");
    // The duplicate-registration channel is gone: no csc.rsp generation.
    expect(run).not.toContain("function New-CscRspContent");
    expect(run).not.toContain("Join-Path $project 'Assets\\csc.rsp'");
    expect(run).not.toContain('-a:`"$analyzerPath`"');
  });

  test("run-ci-tests.ps1 carries the duplicate-analyzer recurrence diagnostic", () => {
    const run = read(RUN_CI_TESTS);
    expect(run).toContain("function Write-DuplicateAnalyzerDiagnostics");
    // Wired into the failure-diagnostics path so a regression is named loudly.
    expect(run).toContain("Write-DuplicateAnalyzerDiagnostics -LogPath");
  });
});

// ---------------------------------------------------------------------------
// Analyzer DLLs must be NON-precompiled (excluded from every platform, Editor
// included) and activated SOLELY by the RoslynAnalyzer asset label. The Unity
// 2021 "Multiple precompiled assemblies with the same name" abort happened
// because the analyzer DLLs were imported Editor-ENABLED, so Unity registered
// them as managed precompiled assemblies; with the same-named DLL importable
// from both the package's Editor/Analyzers and the harness Assets copy, 2021
// rejects the duplicate. The Roslyn runtime deps in the same folder ship
// excluded-from-all-platforms and never collide -- the analyzer DLLs now match
// that proven-safe shape. Assertions on the shipped metas use REGEX/substring,
// never the YAML parser: the metas use Unity's ": Any" empty-key platform
// block, and a narrow substring assertion is clearer and more stable here than
// walking the parsed object graph.
// ---------------------------------------------------------------------------
describe("analyzer DLLs are Editor-disabled (non-precompiled) and label-activated", () => {
  const ANALYZERS_DIR = path.join(REPO_ROOT, "Editor", "Analyzers");
  const SG_META = path.join(ANALYZERS_DIR, "WallstopStudios.DxMessaging.SourceGenerators.dll.meta");
  const ANALYZER_META = path.join(ANALYZERS_DIR, "WallstopStudios.DxMessaging.Analyzer.dll.meta");
  const CONTROL_META = path.join(ANALYZERS_DIR, "Microsoft.CodeAnalysis.dll.meta");
  const SETUP_CSC_RSP = path.join(REPO_ROOT, "Editor", "SetupCscRsp.cs");

  // "enabled: 1" appears ONLY inside a platformData "second" block; the other
  // "...: 1" lines are isOverridable/validateReferences (different keys), so this
  // matches a platform-enabled (precompiled-assembly) DLL and nothing else.
  const PLATFORM_ENABLED = /enabled:\s*1/;
  // The Editor platform specifically enabled (\s spans newlines).
  const EDITOR_ENABLED = /Editor:\s+Editor\s+second:\s+enabled:\s*1/;
  // A RoslynAnalyzer label list entry.
  const ROSLYN_LABEL = /^\s*-\s*RoslynAnalyzer\s*$/m;

  function collectMetaFiles(dir, acc) {
    if (!fs.existsSync(dir)) {
      return acc;
    }
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (
          [
            "node_modules",
            ".artifacts",
            "Library",
            "Temp",
            "obj",
            "bin",
            ".git",
            "site",
            ".venv",
            "coverage"
          ].includes(entry.name)
        ) {
          continue;
        }
        collectMetaFiles(full, acc);
      } else if (entry.isFile() && entry.name.endsWith(".meta")) {
        acc.push(full);
      }
    }
    return acc;
  }

  test("both shipped analyzer metas are RoslynAnalyzer-labeled AND excluded from every platform", () => {
    for (const meta of [SG_META, ANALYZER_META]) {
      const text = read(meta);
      expect(text).toMatch(ROSLYN_LABEL);
      expect(text).not.toMatch(PLATFORM_ENABLED);
      expect(text).not.toMatch(EDITOR_ENABLED);
    }
  });

  test("the analyzer metas match the proven-safe Roslyn-dependency control shape", () => {
    // The Roslyn runtime dependency ships to the same two locations with two
    // copies and never collides because it is excluded from every platform. It is
    // the empirical proof that platform-exclusion -- not the label -- is what
    // keeps a DLL out of the precompiled-assembly set.
    const control = read(CONTROL_META);
    expect(control).not.toMatch(PLATFORM_ENABLED);
    expect(control).not.toMatch(ROSLYN_LABEL); // a dependency, not an analyzer
  });

  test("CLASS lock: every RoslynAnalyzer-labeled .meta in the package is excluded from every platform", () => {
    const metas = [];
    for (const root of ["Editor", "Runtime", "SourceGenerators", "Tests", "Samples~"]) {
      collectMetaFiles(path.join(REPO_ROOT, root), metas);
    }
    const labeled = metas.filter((meta) => ROSLYN_LABEL.test(read(meta)));
    // The two analyzer DLLs are present and labeled.
    expect(labeled.length).toBeGreaterThanOrEqual(2);
    // A future labeled DLL that re-enables ANY platform would reintroduce the
    // precompiled-assembly duplicate class; fail loudly with the offending path.
    const offenders = labeled.filter((meta) => PLATFORM_ENABLED.test(read(meta)));
    expect(offenders).toEqual([]);
  });

  test("run-ci-tests.ps1 fallback meta heredoc is Editor-disabled", () => {
    // The hand-authored fallback meta (used by the GenerateOnly path when a
    // source DLL has no .meta) must also exclude the Editor platform.
    expect(read(RUN_CI_TESTS)).not.toMatch(EDITOR_ENABLED);
  });

  test("SetupCscRsp excludes the Assets analyzer copy from the Editor platform via the effective API", () => {
    const setup = read(SETUP_CSC_RSP);
    // The old no-op (a no-op once CompatibleWithAnyPlatform is false, and backwards
    // in intent -- it tried to Editor-INCLUDE the analyzer) is gone.
    expect(setup).not.toContain('SetExcludeFromAnyPlatform("Editor", false)');
    // Editor is disabled through the effective API; the label remains the
    // activation mechanism.
    expect(setup).toContain("SetCompatibleWithEditor(false)");
    expect(setup).toContain("RoslynAnalyzer");
  });

  test("SetupCscRsp uses RoslynAnalyzer labels only and removes stale DxMessaging -a lines", () => {
    const setup = read(SETUP_CSC_RSP);
    expect(setup).not.toContain("GetAnalyzerArguments");
    expect(setup).not.toContain('yield return $"-a:');
    expect(setup).not.toContain("new( GetAnalyzerArguments");
    expect(setup).toContain("foundStaleEntries = true");
    expect(setup).toContain('StartsWith("-a:"');
    expect(setup).toContain("RoslynAnalyzer-labeled");
  });
});
