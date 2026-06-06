/**
 * @fileoverview Focused behavioral test for ensure-editor.ps1's IL2CPP module
 * idempotency fix.
 *
 * WHY THIS EXISTS: the standalone Unity beta CLI's `install-modules` returns "No
 * modules found to install." with exit code 6 when the Windows IL2CPP module is
 * ALREADY present -- an idempotent no-op, NOT a failure. The previous code routed
 * the module install through the THROWING `Invoke-UnityCli`, so that benign
 * non-zero exit aborted the whole bootstrap on any re-run of an editor that
 * already had IL2CPP. The fix added `Test-Il2CppModulePresent` (a disk-
 * authoritative probe) and made `Add-WindowsIl2CppModule` classify the install
 * result against the disk via the NON-throwing capturing invoker, so an
 * already-installed module is treated as success instead of a fatal error.
 *
 * This test pins the core of that fix: the `Test-Il2CppModulePresent` PROBE. We
 * extract just that function from ensure-editor.ps1 via the PowerShell AST
 * (`Parser::ParseFile`) into a throwaway temp .ps1 and dot-source it -- the
 * script has top-level executable code (it creates the install root and runs the
 * install flow on load), so dot-sourcing the whole file would have side effects;
 * AST-extracting only the function avoids that entirely. We then assert the probe
 * returns $false for a non-existent editor and $true for a fabricated on-disk
 * IL2CPP variations tree, exactly the disk evidence the idempotency branch relies
 * on. Everything lives under os.tmpdir and is cleaned up.
 *
 * A cheap static corroboration also runs (always, even without pwsh): it proves
 * Add-WindowsIl2CppModule no longer routes the module install through the
 * throwing Invoke-UnityCli and DOES classify against Test-Il2CppModulePresent, so
 * a refactor that reintroduces the throwing path is caught even where the
 * behavioral run is skipped.
 *
 * pwsh is preinstalled on GitHub's runners; when it is absent locally the
 * behavioral assertions are skipped, but an always-on sanity assertion still runs
 * so a zero-coverage regression cannot hide.
 *
 * @cross-platform-regression -- this marker requires the file to be gated on
 * ubuntu/windows/macos via the targeted step in
 * .github/workflows/cross-platform-preflight.yml; enforced by
 * scripts/__tests__/cross-platform-preflight-coverage.test.js.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");

const { prependPathEnv, sandboxHostFolderEnv } = require("../lib/spawn-env-sandbox");
const {
  assertPwshContains,
  assertSpawnNonZeroStatus,
  assertSpawnStatus,
  combinedText
} = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const ENSURE_EDITOR = path.join(REPO_ROOT, "scripts", "unity", "ensure-editor.ps1");

// The IL2CPP variations leaf the probe treats as conclusive evidence (the exact
// path called out in the task: a fabricated win64_player_development_il2cpp dir).
const IL2CPP_VARIATION_REL = path.join(
  "Editor",
  "Data",
  "PlaybackEngines",
  "windowsstandalonesupport",
  "Variations",
  "win64_player_development_il2cpp"
);

function writeFakeUnityEditor(editorExe) {
  fs.mkdirSync(path.dirname(editorExe), { recursive: true });
  fs.writeFileSync(
    editorExe,
    ["#!/usr/bin/env sh", 'echo "Unity fake version"', "exit 0", ""].join("\n"),
    "utf8"
  );
  fs.chmodSync(editorExe, 0o755);
}

function createCiModuleLayout(editorExe) {
  const dataRoot = path.join(path.dirname(editorExe), "Data");
  const writeModuleLeaf = (leaf) => {
    fs.mkdirSync(path.dirname(leaf), { recursive: true });
    fs.writeFileSync(leaf, "");
  };
  writeModuleLeaf(
    path.join(
      dataRoot,
      "PlaybackEngines",
      "windowsstandalonesupport",
      "Variations",
      "win64_player_development_il2cpp",
      "UnityPlayer.dll"
    )
  );
  writeModuleLeaf(
    path.join(dataRoot, "PlaybackEngines", "WebGLSupport", "UnityEditor.WebGL.Extensions.dll")
  );
  writeModuleLeaf(
    path.join(
      dataRoot,
      "PlaybackEngines",
      "WebGLSupport",
      "BuildTools",
      "Emscripten",
      "emscripten",
      "emcc.py"
    )
  );
  writeModuleLeaf(
    path.join(dataRoot, "PlaybackEngines", "AndroidPlayer", "UnityEditor.Android.Extensions.dll")
  );
  writeModuleLeaf(
    path.join(dataRoot, "PlaybackEngines", "AndroidPlayer", "SDK", "platform-tools", "adb.exe")
  );
  writeModuleLeaf(
    path.join(dataRoot, "PlaybackEngines", "AndroidPlayer", "NDK", "source.properties")
  );
  writeModuleLeaf(
    path.join(
      dataRoot,
      "PlaybackEngines",
      "AndroidPlayer",
      "NDK",
      "toolchains",
      "llvm",
      "prebuilt",
      "windows-x86_64",
      "bin",
      "clang++.exe"
    )
  );
  writeModuleLeaf(
    path.join(dataRoot, "PlaybackEngines", "AndroidPlayer", "OpenJDK", "bin", "java.exe")
  );
  writeModuleLeaf(
    path.join(
      dataRoot,
      "PlaybackEngines",
      "LinuxStandaloneSupport",
      "Variations",
      "linux64_player_development_mono",
      "LinuxPlayer"
    )
  );
  writeModuleLeaf(
    path.join(
      dataRoot,
      "PlaybackEngines",
      "LinuxStandaloneSupport",
      "Variations",
      "linux64_player_development_il2cpp",
      "LinuxPlayer"
    )
  );
}

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

const workspaces = [];

afterAll(() => {
  for (const ws of workspaces) {
    try {
      fs.rmSync(ws, { recursive: true, force: true });
    } catch {
      // best-effort temp cleanup; never fail the suite on teardown.
    }
  }
});

// AST-extract a named function body from ensure-editor.ps1 into a temp .ps1,
// dot-source it, invoke it against $EditorPath, and print the boolean result.
// Returns the trimmed stdout (e.g. "True" / "False") plus exit status.
function probeIl2Cpp(editorPath) {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-il2cpp-probe-"));
  workspaces.push(base);
  const harnessPath = path.join(base, "harness.ps1");

  // The harness extracts ONLY Test-Il2CppModulePresent (it depends solely on
  // built-in cmdlets, so no other local function is needed), writes it to a temp
  // file, dot-sources it, and prints the probe result for the given editor path.
  const harness = [
    "Set-StrictMode -Version Latest",
    "$ErrorActionPreference = 'Stop'",
    `$src = '${ENSURE_EDITOR.replace(/'/g, "''")}'`,
    "$tokens = $null; $errs = $null",
    "$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errs)",
    "$fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Test-Il2CppModulePresent' }, $true)",
    "if (-not $fn -or $fn.Count -lt 1) { Write-Error 'Test-Il2CppModulePresent not found'; exit 3 }",
    `$tmpFn = Join-Path '${base.replace(/'/g, "''")}' 'fn.ps1'`,
    "Set-Content -LiteralPath $tmpFn -Value $fn[0].Extent.Text -Encoding UTF8",
    ". $tmpFn",
    `$result = Test-Il2CppModulePresent -EditorPath '${editorPath.replace(/'/g, "''")}'`,
    "Write-Output ([bool]$result)"
  ].join("\n");

  fs.writeFileSync(harnessPath, harness, "utf8");

  const run = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harnessPath], {
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024
  });
  run.stdout = (run.stdout || "").trim();
  return run;
}

function runPwshScript(scriptText) {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-harness-"));
  workspaces.push(base);
  const harnessPath = path.join(base, "harness.ps1");
  fs.writeFileSync(harnessPath, scriptText, "utf8");
  return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harnessPath], {
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024
  });
}

function extractEnsureEditorFunctions(functionNames) {
  const escapedPath = ENSURE_EDITOR.replace(/'/g, "''");
  const names = functionNames.map((name) => `'${name.replace(/'/g, "''")}'`).join(", ");
  return [
    `$src = '${escapedPath}'`,
    `$wanted = @(${names})`,
    "$tokens = $null; $errs = $null",
    "$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errs)",
    "$functions = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $n.Name }, $true)",
    'foreach ($name in $wanted) { if (-not ($functions | Where-Object { $_.Name -eq $name })) { Write-Error "Function $name not found"; exit 3 } }',
    "foreach ($fn in $functions) { Invoke-Expression $fn.Extent.Text }"
  ].join("\n");
}

function runAddWindowsIl2CppModuleHarness(editorPath, outputText) {
  const outputLiteral = outputText.replace(/'/g, "''");
  const editorLiteral = editorPath.replace(/'/g, "''");
  return runPwshScript(
    [
      "Set-StrictMode -Version Latest",
      "$ErrorActionPreference = 'Stop'",
      'function Write-CiNotice { param([string]$Message) Write-Host "::notice::$Message" }',
      "function Get-UnityCliOutput { param([string[]]$Arguments) return @() }",
      "function Invoke-UnityCliCapture {",
      "  param([string[]]$Arguments)",
      // The stub mirrors the FULL return shape published by
      // Invoke-UnityCliCaptureWithTimeout so downstream classifiers (which now
      // consult StallKilled / TimedOutWallClock to attribute the failure mode)
      // see a deterministic "neither wrapper-killed" result under StrictMode.
      `  return @{ Success = $false; ExitCode = 6; Output = @('${outputLiteral}'); StallKilled = $false; TimedOutWallClock = $false }`,
      "}",
      extractEnsureEditorFunctions([
        "Test-Il2CppModulePresent",
        "Test-AnyUnityLeafPresent",
        // Single source of truth for the CI module set; every list/tier function
        // derives from it.
        "Get-UnityProvisioningProfile",
        "Assert-UnityProvisioningProfile",
        "Get-UnityCiModuleSpec",
        "Get-UnityCiModuleSpecForProfile",
        "Get-UnityCiModuleIds",
        // Tier helpers (core/android partition + per-id tier lookup) the tier-aware
        // Ensure-UnityCiModules uses to scope installs and partition missing groups.
        "Get-UnityCiModuleIdsForTier",
        "Get-UnityCiModuleTier",
        // VERIFIED groups (decoupled from requested ids) -- iterated by
        // Get-MissingUnityCiModuleGroups for on-disk verification.
        "Get-UnityCiVerifiedModuleGroups",
        // Single source-of-truth arg-vector builder used by Ensure-UnityCiModules
        // (the install-modules call + the failure-annotation arg echo).
        "Get-UnityCliModuleInstallArguments",
        // Targeted failure annotation emitted on a genuine module-install failure.
        "Write-UnityCliInstallFailureAnnotation",
        // Resilience+diagnostics helpers Ensure-UnityCiModules now calls on the
        // failure path: the de-dup tail formatter, the wrap-immune failure
        // summary, and the helpers that summary depends on (last-progress-message
        // parser, install-drive free-space probe, install-timeout knob).
        "Get-CollapsedCliOutputTail",
        "Get-LastCliProgressMessage",
        "Get-InstallDriveFreeSpaceText",
        "Get-EnsureEditorInstallTimeoutSeconds",
        // The classifiers pass the StallSeconds knob into the diagnostic
        // formatter so the wrap-immune ::error:: can name the heartbeat-stall
        // window when applicable; the helper itself must be in scope.
        "Get-EnsureEditorProgressStallSeconds",
        "Write-ModuleInstallFailureDiagnostics",
        // Dedicated, bounded Android tier install + its helpers, reached when only
        // the android tier is missing before any managed repair escalation.
        "Get-EnsureEditorRetryDelaySeconds",
        "Get-EnsureEditorAndroidInstallRetryAttempts",
        "Invoke-WithRetry",
        "Clear-PartialAndroidModulePayload",
        "Get-DeepestPathLengthUnder",
        "Test-WindowsLongPathSupport",
        "Write-UnityModuleInstallPostMortem",
        "Install-UnityAndroidModules",
        "Test-UnityCiModuleGroupPresent",
        "Get-MissingUnityCiModuleGroups",
        "Ensure-UnityCiModules",
        "Add-WindowsIl2CppModule"
      ]),
      "try {",
      `  Add-WindowsIl2CppModule -Version '6000.0.32f1' -EditorPath '${editorLiteral}'`,
      "  Write-Output 'SUCCESS'",
      "} catch {",
      "  Write-Output ('ERROR: ' + $_.Exception.Message)",
      "  exit 7",
      "}"
    ].join("\n")
  );
}

function makeFakeUnityCli(binDir, handlerLines) {
  fs.mkdirSync(binDir, { recursive: true });
  const scriptPath = path.join(binDir, "fake-unity-cli.js");
  fs.writeFileSync(
    scriptPath,
    [
      "#!/usr/bin/env node",
      '"use strict";',
      "const args = process.argv.slice(2);",
      "function write(line) { process.stdout.write(`${line}\\n`); }",
      "function exit(code) { process.exit(code); }",
      "if (args.includes('-version')) { write('Unity fake version'); exit(0); }",
      ...handlerLines,
      "exit(0);",
      ""
    ].join("\n"),
    "utf8"
  );
  fs.chmodSync(scriptPath, 0o755);

  if (process.platform === "win32") {
    const unityCmdPath = path.join(binDir, "unity.cmd");
    fs.writeFileSync(
      unityCmdPath,
      ["@echo off", 'node "%~dp0fake-unity-cli.js" %*', ""].join("\r\n"),
      "utf8"
    );
    return unityCmdPath;
  }

  const unityPath = path.join(binDir, "unity");
  fs.writeFileSync(
    unityPath,
    ["#!/usr/bin/env sh", 'exec node "$(dirname "$0")/fake-unity-cli.js" "$@"', ""].join("\n"),
    "utf8"
  );
  fs.chmodSync(unityPath, 0o755);
  return unityPath;
}

function assertFakeUnityCliResolves(env, expectedPath) {
  const probe = spawnSync(
    "pwsh",
    ["-NoProfile", "-NonInteractive", "-Command", "(Get-Command unity -ErrorAction Stop).Source"],
    { env, encoding: "utf8", maxBuffer: 16 * 1024 * 1024 }
  );

  assertSpawnStatus(probe, 0, expect.getState().currentTestName || "pwsh harness");
  const actual = path.normalize((probe.stdout || "").trim());
  const expected = path.normalize(expectedPath);
  expect(process.platform === "win32" ? actual.toLowerCase() : actual).toBe(
    process.platform === "win32" ? expected.toLowerCase() : expected
  );
}

function runEnsureEditorWithFakeCli(handlerLines, installRoot, baseEnv = process.env) {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-full-"));
  workspaces.push(base);
  const binDir = path.join(base, "bin");
  const unityPath = makeFakeUnityCli(binDir, handlerLines);

  // Hermetic host-discovery: ensure-editor.ps1 probes the host-default folder
  // vars (`${env:ProgramFiles}\Unity\Hub\Editor\<ver>\Editor\Unity.exe`, ...).
  // sandboxHostFolderEnv removes EVERY case-variant of those names (a plain
  // `delete env.ProgramFiles` misses `PROGRAMFILES` on case-insensitive Windows)
  // and points each at an EMPTY sandbox dir so a real Unity install on the host
  // can never leak into resolution. The sandbox dirs live under this run's temp
  // workspace.
  const sandboxRoot = path.join(base, "host-env-sandbox");
  const env = prependPathEnv(sandboxHostFolderEnv(baseEnv, sandboxRoot), binDir);
  delete env.UNITY_EDITOR_INSTALL_ROOT;
  env.DXM_ENSURE_EDITOR_RETRY_DELAY_SECONDS = "0";
  // Stub Unity.exe binaries written here use a sh shebang body that Linux/macOS execute via kernel
  // shebang dispatch, but Windows CreateProcess rejects as not a valid PE. Skip the native startup
  // probe by default; a caller that needs the real probe can disable the default by passing a
  // `baseEnv` that already defines `DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE` (set to anything other than
  // `undefined`, including an empty string, to bypass the conditional below).
  if (env.DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE === undefined) {
    env.DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE = "1";
  }
  assertFakeUnityCliResolves(env, unityPath);
  return spawnSync(
    "pwsh",
    [
      "-NoProfile",
      "-NonInteractive",
      "-File",
      ENSURE_EDITOR,
      "-UnityVersion",
      "6000.0.32f1",
      "-InstallRoot",
      installRoot,
      "-CiManagedOnly"
    ],
    { env, encoding: "utf8", maxBuffer: 32 * 1024 * 1024 }
  );
}

describe("ensure-editor.ps1 IL2CPP module idempotency", () => {
  // Always runs (even without pwsh): proves the script under test exists, so a
  // rename/move cannot silently turn this whole guard into a no-op.
  test("the script under test exists", () => {
    expect(fs.existsSync(ENSURE_EDITOR)).toBe(true);
  });

  // Static corroboration of the fix shape (no pwsh needed): the module install in
  // Add-WindowsIl2CppModule must NOT go through the throwing Invoke-UnityCli, and
  // the function MUST classify against Test-Il2CppModulePresent.
  test("Add-WindowsIl2CppModule classifies via Test-Il2CppModulePresent, not the throwing Invoke-UnityCli", () => {
    const text = fs.readFileSync(ENSURE_EDITOR, "utf8");
    const start = text.indexOf("function Ensure-UnityCiModules");
    expect(start).toBeGreaterThanOrEqual(0);
    // Bound the slice at the next top-level function definition.
    const after = text.indexOf("\nfunction ", start + 1);
    const body = after === -1 ? text.slice(start) : text.slice(start, after);

    // It must consult the disk probe through the shared CI-module group checker...
    expect(body).toContain("Get-MissingUnityCiModuleGroups");
    // ...attempt the install via the non-throwing capturing invoker...
    expect(body).toContain("Invoke-UnityCliCapture");
    // ...and NEVER route the module install through the throwing invoker.
    expect(body).not.toMatch(/Invoke-UnityCli\b(?!Capture|Safe)/);
  });

  // Static corroboration (no pwsh): the ROOT-CAUSE SIDESTEP surface exists. When the
  // CLI reports the editor is not module-manageable, Ensure-UnityCiModules must
  // PROACTIVELY route to the atomic reinstall (Install-UnityEditorModulesViaAtomicReinstall)
  // instead of the install-modules -> exit 6 -> uninstall -> quarantine path.
  test("the proactive not-module-manageable -> atomic-reinstall routing exists in the script", () => {
    const text = fs.readFileSync(ENSURE_EDITOR, "utf8");
    expect(text).toContain("function Test-TextIndicatesEditorNotModuleManageable");
    expect(text).toContain("function Test-UnityEditorModuleManageable");
    expect(text).toContain("function Install-UnityEditorModulesViaAtomicReinstall");
    const start = text.indexOf("function Ensure-UnityCiModules");
    const after = text.indexOf("\nfunction ", start + 1);
    const body = text.slice(start, after === -1 ? undefined : after);
    // The proactive branch consults the manageability probe and routes to the atomic
    // reinstall BEFORE the install-modules core path.
    expect(body).toContain("Test-UnityEditorModuleManageable");
    expect(body).toContain("Install-UnityEditorModulesViaAtomicReinstall");
    const probeIdx = body.indexOf("Test-UnityEditorModuleManageable");
    const coreInstallIdx = body.indexOf("Get-UnityCliModuleInstallArguments");
    expect(probeIdx).toBeGreaterThanOrEqual(0);
    expect(coreInstallIdx).toBeGreaterThan(probeIdx);
    // The atomic-reinstall helper attempts the in-place atomic install FIRST and only
    // FALLS BACK to the quarantine+reinstall repair.
    const helperStart = text.indexOf("function Install-UnityEditorModulesViaAtomicReinstall");
    const helperAfter = text.indexOf("\nfunction ", helperStart + 1);
    const helperBody = text.slice(helperStart, helperAfter);
    const atomicIdx = helperBody.indexOf("Install-UnityEditorWithCiModules");
    const fallbackIdx = helperBody.indexOf("Repair-UnityEditorWithCiModules");
    expect(atomicIdx).toBeGreaterThanOrEqual(0);
    expect(fallbackIdx).toBeGreaterThan(atomicIdx);
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[il2cpp-idempotency] pwsh not found on PATH; skipping behavioral probe (CI runners have pwsh)."
    );
    // A `test.skip` placeholder is registered ONLY for the two Test-Il2CppModulePresent
    // probe cases (count parity for the parametrized .each above). Every behavioral
    // test AFTER this `return` (the Add-WindowsIl2CppModule cases, the recover/partial
    // cases, and the ProgramFiles leak regression) is intentionally left
    // unregistered when pwsh is absent -- they are consistent with each other, and
    // the always-on "the script under test exists" sanity test (above) guarantees a
    // zero-coverage regression cannot hide.
    test.skip.each([
      ["non-existent editor path", false],
      ["fabricated IL2CPP variations tree", true]
    ])("Test-Il2CppModulePresent returns %s -> %s", () => {});
    return;
  }

  test("Test-Il2CppModulePresent returns $false for a non-existent editor path", () => {
    const fake = path.join(os.tmpdir(), "dxm-does-not-exist", "Editor", "Unity.exe");
    const out = probeIl2Cpp(fake);
    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(out.stdout).toBe("False");
  });

  test("Test-Il2CppModulePresent requires a concrete IL2CPP player leaf", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-il2cpp-editor-"));
    workspaces.push(base);

    // Build ...\<version>\Editor\Data\PlaybackEngines\...\Variations\
    //   win64_player_development_il2cpp, then place a (non-existent-on-disk-as-
    //   a-file is fine) Unity.exe path whose PARENT is the Editor dir so the
    //   probe's editorDir -> Data resolution lands on the fabricated tree.
    const variationDir = path.join(base, "6000.0.32f1", IL2CPP_VARIATION_REL);
    fs.mkdirSync(variationDir, { recursive: true });
    const editorExe = path.join(base, "6000.0.32f1", "Editor", "Unity.exe");
    writeFakeUnityEditor(editorExe);

    const emptyOut = probeIl2Cpp(editorExe);
    assertSpawnStatus(emptyOut, 0, expect.getState().currentTestName || "pwsh harness");
    expect(emptyOut.stdout).toBe("False");

    fs.writeFileSync(path.join(variationDir, "UnityPlayer.dll"), "");
    const populatedOut = probeIl2Cpp(editorExe);
    assertSpawnStatus(populatedOut, 0, expect.getState().currentTestName || "pwsh harness");
    expect(populatedOut.stdout).toBe("True");
  });

  // Data-driven classification of the `install-modules` "No modules found to
  // install." (exit 6) no-op against the on-disk module layout. Each scenario
  // sets up a distinct disk state, then asserts the resulting exit status and the
  // (wrap-normalized) diagnostic phrases. Every row maps 1:1 to a former discrete
  // assertion, so coverage is preserved exactly. All rows drive the SAME
  // Add-WindowsIl2CppModule harness with the same CLI output ("No modules found
  // to install").
  test.each([
    {
      name: "fatal when IL2CPP is not on disk",
      // No layout at all: the probe must classify the no-op as a real failure.
      // With tiering, the CORE tier is missing and (no InstallRoot -> no repair)
      // the core branch throws naming the missing core groups + the CLI output.
      setup: () => path.join(os.tmpdir(), "dxm-no-il2cpp", "Editor", "Unity.exe"),
      expectedStatus: 7,
      expectedPhrases: [
        "required CORE CI module groups are missing",
        "windows-il2cpp",
        "No modules found to install"
      ]
    },
    {
      name: "success only when ALL CI module groups are on disk",
      setup: () => {
        const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-il2cpp-editor-"));
        workspaces.push(base);
        const editorExe = path.join(base, "6000.0.32f1", "Editor", "Unity.exe");
        writeFakeUnityEditor(editorExe);
        createCiModuleLayout(editorExe);
        return editorExe;
      },
      expectedStatus: 0,
      // With every group already on disk, the tier-aware Ensure-UnityCiModules
      // returns early (nothing to install) without invoking the CLI at all.
      expectedPhrases: [
        "All required Unity CI module groups for provisioning profile 'Full' already present on disk",
        "SUCCESS"
      ]
    },
    {
      name: "WebGL extension-only leftovers rejected without Emscripten toolchain proof",
      setup: () => {
        const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-webgl-leftover-editor-"));
        workspaces.push(base);
        const editorExe = path.join(base, "6000.0.32f1", "Editor", "Unity.exe");
        writeFakeUnityEditor(editorExe);
        fs.mkdirSync(
          path.join(path.dirname(editorExe), "Data", "PlaybackEngines", "WebGLSupport"),
          { recursive: true }
        );
        fs.writeFileSync(
          path.join(
            path.dirname(editorExe),
            "Data",
            "PlaybackEngines",
            "WebGLSupport",
            "UnityEditor.WebGL.Extensions.dll"
          ),
          ""
        );
        return editorExe;
      },
      // webgl is a CORE-tier group, so it routes through the core branch throw.
      expectedStatus: 7,
      expectedPhrases: ["required CORE CI module groups are missing", "webgl"]
    }
  ])(
    "Add-WindowsIl2CppModule with 'No modules found': $name",
    ({ setup, expectedStatus, expectedPhrases }) => {
      const editorExe = setup();
      const out = runAddWindowsIl2CppModuleHarness(editorExe, "No modules found to install");
      const combined = combinedText(out);

      assertSpawnStatus(out, expectedStatus, expect.getState().currentTestName || "pwsh harness");
      for (const phrase of expectedPhrases) {
        expect(combined).toContain(phrase);
      }
    }
  );

  test("managed editor missing CI modules is quarantined and reinstalled with the full module set", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-repair-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    const repairInstallMarker = path.join(base, "repair-install-attempts.txt");
    const repairUninstallMarker = path.join(base, "repair-uninstall-called.txt");
    writeFakeUnityEditor(editorExe);

    const cliBody = [
      "const fs = require('fs');",
      "const path = require('path');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
      `const repairInstallMarker = ${JSON.stringify(repairInstallMarker)};`,
      `const repairUninstallMarker = ${JSON.stringify(repairUninstallMarker)};`,
      "const editorRoot = path.join(installRoot, '6000.0.32f1');",
      "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeFile(p, value) { mkdirp(path.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
      "function createModules() {",
      "  writeFile(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'windowsstandalonesupport', 'Variations', 'win64_player_development_il2cpp', 'UnityPlayer.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'UnityEditor.WebGL.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'BuildTools', 'Emscripten', 'emscripten', 'emcc.py'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'UnityEditor.Android.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'SDK', 'platform-tools', 'adb.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'source.properties'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'windows-x86_64', 'bin', 'clang++.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
      "if (args[0] === 'install-modules') { write('Module installation is only supported for editors installed with Unity Hub.'); exit(6); }",
      "if (args[0] === 'uninstall') { fs.writeFileSync(repairUninstallMarker, '1'); write('uninstall could not remove unmanaged editor'); exit(6); }",
      "if (args[0] === 'install') {",
      "  const attempts = fs.existsSync(repairInstallMarker) ? Number(fs.readFileSync(repairInstallMarker, 'utf8')) : 0;",
      "  fs.writeFileSync(repairInstallMarker, String(attempts + 1));",
      "  if (!fs.existsSync(repairUninstallMarker) || attempts === 0) { write('Editor already installed'); exit(6); }",
      "  createModules(); write('installed editor with CI modules'); exit(0);",
      "}",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot);
    const stdout = out.stdout || "";
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(editorExe);
    expect(combined).toContain("Quarantining unmanaged or partial Unity 6000.0.32f1 install");
    expect(combined).toContain(
      "Repairing Unity 6000.0.32f1 by installing a fresh CLI-managed editor with provisioning profile 'Full' modules"
    );
    expect(combined).toContain("Retrying Unity 6000.0.32f1 repair install");
    expect(Number(fs.readFileSync(repairInstallMarker, "utf8"))).toBe(2);
    expect(
      fs.existsSync(
        path.join(
          editorRoot,
          "Editor",
          "Data",
          "PlaybackEngines",
          "WebGLSupport",
          "BuildTools",
          "Emscripten",
          "emscripten",
          "emcc.py"
        )
      )
    ).toBe(true);
    expect(
      fs.existsSync(
        path.join(
          editorRoot,
          "Editor",
          "Data",
          "PlaybackEngines",
          "AndroidPlayer",
          "SDK",
          "platform-tools",
          "adb.exe"
        )
      )
    ).toBe(true);
    expect(
      fs.existsSync(
        path.join(
          editorRoot,
          "Editor",
          "Data",
          "PlaybackEngines",
          "LinuxStandaloneSupport",
          "Variations",
          "linux64_player_development_il2cpp",
          "LinuxPlayer"
        )
      )
    ).toBe(true);
    expect(fs.readdirSync(path.join(installRoot, "_quarantine")).length).toBeGreaterThan(0);
  });

  // ROOT-CAUSE SIDESTEP (Unity 6000.3.16f1 standalone, run 26701943540): when
  // `install-modules -e <v> -l` reports the editor is NOT module-manageable
  // ("Module installation is only supported for editors installed with Unity Hub"),
  // the script must PROACTIVELY route to the atomic `install -m` reinstallation,
  // which adds the modules IN PLACE -- so it NEVER runs the `install-modules` -m add
  // (which exits 6), NEVER runs `uninstall`, and NEVER quarantines the (potentially
  // locked) version directory. This is the fix that sidesteps the locker entirely.
  test("a not-module-manageable editor is repaired by the atomic in-place reinstall WITHOUT install-modules/uninstall/quarantine", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-proactive-atomic-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    const moduleAddMarker = path.join(base, "install-modules-add-called.txt");
    const uninstallMarker = path.join(base, "uninstall-called.txt");
    const atomicInstallMarker = path.join(base, "atomic-install-called.txt");
    // The editor exists on disk but WITHOUT the CI module payload (so step 1 finds
    // missing module groups and the proactive probe runs).
    writeFakeUnityEditor(editorExe);

    const cliBody = [
      "const fs = require('fs');",
      "const path = require('path');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
      `const moduleAddMarker = ${JSON.stringify(moduleAddMarker)};`,
      `const uninstallMarker = ${JSON.stringify(uninstallMarker)};`,
      `const atomicInstallMarker = ${JSON.stringify(atomicInstallMarker)};`,
      "const editorRoot = path.join(installRoot, '6000.0.32f1');",
      "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeFile(p, value) { mkdirp(path.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
      "function createModules() {",
      "  writeFile(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'windowsstandalonesupport', 'Variations', 'win64_player_development_il2cpp', 'UnityPlayer.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'UnityEditor.WebGL.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'BuildTools', 'Emscripten', 'emscripten', 'emcc.py'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'UnityEditor.Android.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'SDK', 'platform-tools', 'adb.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'source.properties'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'windows-x86_64', 'bin', 'clang++.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      // The LISTING carries the not-module-manageable signal verbatim (as in the
      // real failing CI run). This is what the proactive probe keys on.
      "if (args[0] === 'install-modules' && args.includes('-l')) {",
      "  write('Error: No modules found for this editor.');",
      "  write('Module installation is only supported for editors installed with Unity Hub.');",
      "  exit(0);",
      "}",
      // The install-modules ADD path is the DOOMED one: if it is ever taken, record
      // it and exit 6. The test asserts this marker is NEVER written.
      "if (args[0] === 'install-modules') { fs.writeFileSync(moduleAddMarker, '1'); write('Module installation is only supported for editors installed with Unity Hub.'); exit(6); }",
      // uninstall is part of the quarantine path; assert it is NEVER called.
      "if (args[0] === 'uninstall') { fs.writeFileSync(uninstallMarker, '1'); write('uninstalled'); exit(0); }",
      // The ATOMIC install verb succeeds IN PLACE and writes the module payload.
      "if (args[0] === 'install') { fs.writeFileSync(atomicInstallMarker, '1'); createModules(); write('installed editor with CI modules'); exit(0); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot);
    const stdout = out.stdout || "";
    const combined = combinedText(out);
    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");

    // The script resolved the editor (last stdout line is the editor path).
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(editorExe);
    // It announced the proactive route...
    expect(combined).toContain("not module-manageable");
    expect(combined).toContain("reinstalling atomically with the required modules");
    // ...the ATOMIC install ran...
    expect(fs.existsSync(atomicInstallMarker)).toBe(true);
    // ...and the il2cpp module payload is now on disk.
    expect(
      fs.existsSync(
        path.join(
          editorRoot,
          "Editor",
          "Data",
          "PlaybackEngines",
          "windowsstandalonesupport",
          "Variations",
          "win64_player_development_il2cpp",
          "UnityPlayer.dll"
        )
      )
    ).toBe(true);
    // CRITICAL: the doomed install-modules ADD, the uninstall, and the quarantine
    // were all SIDESTEPPED -- the locker is never contended.
    expect(fs.existsSync(moduleAddMarker)).toBe(false);
    expect(fs.existsSync(uninstallMarker)).toBe(false);
    expect(fs.existsSync(path.join(installRoot, "_quarantine"))).toBe(false);
  }, 90000);

  // HONEST FALLBACK (objective B): when the in-place atomic install genuinely cannot
  // deliver the modules (e.g. it cannot overlay the existing tree), the proactive
  // route FALLS BACK to the quarantine+reinstall path. The first `install` call here
  // fails to write the payload (simulating "could not overlay"); the second `install`
  // (post-quarantine) succeeds. We assert the fallback warning fired, the quarantine
  // happened, and the editor was ultimately repaired.
  test("atomic in-place reinstall that cannot deliver modules FALLS BACK to quarantine+reinstall", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-proactive-fallback-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    const installAttemptsMarker = path.join(base, "install-attempts.txt");
    writeFakeUnityEditor(editorExe);

    const cliBody = [
      "const fs = require('fs');",
      "const path = require('path');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
      `const installAttemptsMarker = ${JSON.stringify(installAttemptsMarker)};`,
      "const editorRoot = path.join(installRoot, '6000.0.32f1');",
      "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeFile(p, value) { mkdirp(path.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
      "function createModules() {",
      "  writeFile(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'windowsstandalonesupport', 'Variations', 'win64_player_development_il2cpp', 'UnityPlayer.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'UnityEditor.WebGL.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'BuildTools', 'Emscripten', 'emscripten', 'emcc.py'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'UnityEditor.Android.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'SDK', 'platform-tools', 'adb.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'source.properties'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'windows-x86_64', 'bin', 'clang++.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) {",
      "  write('Module installation is only supported for editors installed with Unity Hub.');",
      "  exit(0);",
      "}",
      "if (args[0] === 'install-modules') { write('only supported for editors installed with Unity Hub'); exit(6); }",
      "if (args[0] === 'uninstall') { write('uninstalled'); exit(0); }",
      "if (args[0] === 'install') {",
      "  const n = fs.existsSync(installAttemptsMarker) ? Number(fs.readFileSync(installAttemptsMarker, 'utf8')) : 0;",
      "  fs.writeFileSync(installAttemptsMarker, String(n + 1));",
      // First atomic attempt 'succeeds' (exit 0) but writes NO module payload, so
      // the post-install module verification fails -> Install-UnityEditorWithCiModules
      // throws -> the helper falls back to quarantine+reinstall. The post-quarantine
      // attempt writes the payload.
      "  if (n === 0) { write('installed (but modules missing)'); exit(0); }",
      "  createModules(); write('installed editor with CI modules'); exit(0);",
      "}",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot);
    const stdout = out.stdout || "";
    const combined = combinedText(out);
    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(editorExe);
    // The fallback warning fired and the quarantine happened.
    expect(combined).toContain("falling back to quarantine + reinstall");
    expect(fs.existsSync(path.join(installRoot, "_quarantine"))).toBe(true);
    expect(fs.readdirSync(path.join(installRoot, "_quarantine")).length).toBeGreaterThan(0);
    // More than one install attempt was made (in-place, then post-quarantine).
    expect(Number(fs.readFileSync(installAttemptsMarker, "utf8"))).toBeGreaterThanOrEqual(2);
  }, 90000);

  test("repair install retries when a successful install leaves no Unity.exe", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-repair-no-editor-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    const repairInstallMarker = path.join(base, "repair-install-attempts.txt");
    writeFakeUnityEditor(editorExe);

    const cliBody = [
      "const fs = require('fs');",
      "const path = require('path');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
      `const repairInstallMarker = ${JSON.stringify(repairInstallMarker)};`,
      "const editorRoot = path.join(installRoot, '6000.0.32f1');",
      "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeFile(p, value) { mkdirp(path.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
      "function createModules() {",
      "  writeFile(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'windowsstandalonesupport', 'Variations', 'win64_player_development_il2cpp', 'UnityPlayer.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'UnityEditor.WebGL.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'BuildTools', 'Emscripten', 'emscripten', 'emcc.py'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'UnityEditor.Android.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'SDK', 'platform-tools', 'adb.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'source.properties'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'windows-x86_64', 'bin', 'clang++.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
      "if (args[0] === 'install-modules') { write('Module installation is only supported for editors installed with Unity Hub.'); exit(6); }",
      "if (args[0] === 'uninstall') { write('metadata cleared'); exit(0); }",
      "if (args[0] === 'install') {",
      "  const attempts = fs.existsSync(repairInstallMarker) ? Number(fs.readFileSync(repairInstallMarker, 'utf8')) : 0;",
      "  fs.writeFileSync(repairInstallMarker, String(attempts + 1));",
      "  if (attempts === 0) { write('install succeeded without Unity.exe'); exit(0); }",
      "  createModules(); write('fresh install after success-without-editor retry'); exit(0);",
      "}",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot);
    const stdout = out.stdout || "";
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(editorExe);
    expect(combined).toContain("install succeeded without Unity.exe");
    expect(combined).toContain(
      "Retrying Unity 6000.0.32f1 repair install after successful CLI install left no resolvable Unity.exe"
    );
    expect(combined).toContain("fresh install after success-without-editor retry");
    expect(Number(fs.readFileSync(repairInstallMarker, "utf8"))).toBe(2);
  });

  test("repair-disabled env var fails without quarantining a managed editor missing CI modules", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-no-repair-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    fs.mkdirSync(path.dirname(editorExe), { recursive: true });
    writeFakeUnityEditor(editorExe);

    const cliBody = [
      `const installRoot = ${JSON.stringify(installRoot)};`,
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android linux-il2cpp'); exit(0); }",
      "if (args[0] === 'install-modules') { write('No modules found to install'); exit(6); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: installRoot + '/6000.0.32f1' }] })); exit(0); }"
    ];
    const env = { ...process.env, DXM_UNITY_DISABLE_EDITOR_REPAIR: "1" };

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot, env);
    const combined = combinedText(out);

    assertSpawnNonZeroStatus(out, expect.getState().currentTestName || "pwsh harness");
    expect(combined).toContain("DXM_UNITY_DISABLE_EDITOR_REPAIR=1 disabled");
    expect(combined).toContain("auto-repair");
    expect(fs.existsSync(path.join(installRoot, "_quarantine"))).toBe(false);
    expect(fs.existsSync(editorExe)).toBe(true);
  });

  // TIERING REGRESSION: when ONLY the Android tier is missing (core modules are
  // all present on disk) and the Android install keeps failing, the dedicated,
  // bounded Android step emits an Android-specific post-mortem, then escalates to
  // a managed quarantine/reinstall with the selected provisioning profile.
  test("an Android-tier-only failure escalates to managed quarantine and profile reinstall", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-android-only-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    const argsLog = path.join(base, "unity-cli-args.tsv");
    writeFakeUnityEditor(editorExe);

    // Fabricate ONLY the CORE tier on disk (windows-il2cpp, webgl, linux-mono,
    // linux-il2cpp). The Android tier is deliberately absent, the bounded
    // Android-only repair never delivers it, and the subsequent profile-scoped
    // reinstall does.
    const dataRoot = path.join(editorRoot, "Editor", "Data");
    const writeLeaf = (rel) => {
      const p = path.join(dataRoot, rel);
      fs.mkdirSync(path.dirname(p), { recursive: true });
      fs.writeFileSync(p, "");
    };
    writeLeaf(
      path.join(
        "PlaybackEngines",
        "windowsstandalonesupport",
        "Variations",
        "win64_player_development_il2cpp",
        "UnityPlayer.dll"
      )
    );
    writeLeaf(path.join("PlaybackEngines", "WebGLSupport", "UnityEditor.WebGL.Extensions.dll"));
    writeLeaf(
      path.join(
        "PlaybackEngines",
        "WebGLSupport",
        "BuildTools",
        "Emscripten",
        "emscripten",
        "emcc.py"
      )
    );
    writeLeaf(
      path.join(
        "PlaybackEngines",
        "LinuxStandaloneSupport",
        "Variations",
        "linux64_player_development_mono",
        "LinuxPlayer"
      )
    );
    writeLeaf(
      path.join(
        "PlaybackEngines",
        "LinuxStandaloneSupport",
        "Variations",
        "linux64_player_development_il2cpp",
        "LinuxPlayer"
      )
    );

    const cliBody = [
      "const fs = require('fs');",
      "const path2 = require('path');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
      `const argsLog = ${JSON.stringify(argsLog)};`,
      "const editorRoot = path2.join(installRoot, '6000.0.32f1');",
      "const editorExe = path2.join(editorRoot, 'Editor', 'Unity.exe');",
      "const dataRoot = path2.join(editorRoot, 'Editor', 'Data');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeFile(p, value) { mkdirp(path2.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
      "function logArgs() { fs.appendFileSync(argsLog, args.join('\\t') + '\\n'); }",
      "function createFullModules() {",
      "  writeFile(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'windowsstandalonesupport', 'Variations', 'win64_player_development_il2cpp', 'UnityPlayer.dll'), '');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'WebGLSupport', 'UnityEditor.WebGL.Extensions.dll'), '');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'WebGLSupport', 'BuildTools', 'Emscripten', 'emscripten', 'emcc.py'), '');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'UnityEditor.Android.Extensions.dll'), '');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'SDK', 'platform-tools', 'adb.exe'), '');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'source.properties'), '');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'windows-x86_64', 'bin', 'clang++.exe'), '');",
      "  writeFile(path2.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java.exe'), '');",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
      // The Android module install always fails (exit 6) and never writes the NDK/SDK.
      'if (args[0] === \'install-modules\') { logArgs(); write(\'{"type":"progress","phase":"install","pct":93,"msg":"Installing Android NDK"}\'); write(\'INSTALL_FAILED\'); exit(6); }',
      "if (args[0] === 'uninstall') { write('metadata cleared'); exit(0); }",
      "if (args[0] === 'install') { logArgs(); createFullModules(); write('full reinstall completed'); exit(0); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];
    // Two bounded Android attempts with zero retry delay keeps the test fast.
    const env = {
      ...process.env,
      DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS: "2",
      DXM_ENSURE_EDITOR_RETRY_DELAY_SECONDS: "0"
    };

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot, env);
    const stdout = out.stdout || "";
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(editorExe);
    expect(combined).toContain("Android-only repair exhausted after 2 attempt(s)");
    expect(combined).toContain("escalating to managed quarantine/reinstall");
    expect(combined).toContain("provisioning profile 'Full'");
    expect(combined).toContain("android-sdk-ndk-tools");
    expect(combined).toContain("--childModules");
    const moduleCommandArgs = fs
      .readFileSync(argsLog, "utf8")
      .trim()
      .split(/\r?\n/)
      .map((line) => line.split("\t"))
      .filter((args) => args.includes("-m"));
    expect(moduleCommandArgs.length).toBeGreaterThanOrEqual(1);
    expect(moduleCommandArgs.some((args) => args.includes("android-open-jdk"))).toBe(false);
    // The post-mortem ran before escalation, reporting core present and Android missing.
    expect(combined).toContain("module install post-mortem");
    expect(combined).toContain("module group 'windows-il2cpp': present");
    expect(combined).toContain("module group 'android-sdk-ndk-tools': MISSING");
    // The stale process cleanup hook ran, the partial editor was quarantined, and
    // the replacement full install delivered Android.
    expect(combined).toContain("Stale Unity provisioning process cleanup");
    expect(fs.existsSync(path.join(installRoot, "_quarantine"))).toBe(true);
    expect(fs.existsSync(editorExe)).toBe(true);
    expect(
      fs.existsSync(
        path.join(
          dataRoot,
          "PlaybackEngines",
          "windowsstandalonesupport",
          "Variations",
          "win64_player_development_il2cpp",
          "UnityPlayer.dll"
        )
      )
    ).toBe(true);
    expect(
      fs.existsSync(
        path.join(dataRoot, "PlaybackEngines", "AndroidPlayer", "OpenJDK", "bin", "java.exe")
      )
    ).toBe(true);
  });

  test("an Android-tier-only failure with repair disabled does not quarantine or reinstall", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-android-no-repair-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    const installMarker = path.join(base, "unexpected-full-install.txt");
    const argsLog = path.join(base, "unity-cli-args.tsv");
    writeFakeUnityEditor(editorExe);
    createCiModuleLayout(editorExe);
    fs.rmSync(path.join(editorRoot, "Editor", "Data", "PlaybackEngines", "AndroidPlayer"), {
      recursive: true,
      force: true
    });

    const cliBody = [
      "const fs = require('fs');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
      `const installMarker = ${JSON.stringify(installMarker)};`,
      `const argsLog = ${JSON.stringify(argsLog)};`,
      "function logArgs() { fs.appendFileSync(argsLog, args.join('\\t') + '\\n'); }",
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
      "if (args[0] === 'install-modules') { logArgs(); write('android install failed'); exit(6); }",
      "if (args[0] === 'install') { logArgs(); fs.writeFileSync(installMarker, args.join(' ')); write('unexpected full install'); exit(0); }",
      "if (args[0] === 'uninstall') { write('unexpected uninstall'); exit(0); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: installRoot + '/6000.0.32f1' }] })); exit(0); }"
    ];
    const env = {
      ...process.env,
      DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS: "2",
      DXM_ENSURE_EDITOR_RETRY_DELAY_SECONDS: "0",
      DXM_UNITY_DISABLE_EDITOR_REPAIR: "1"
    };

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot, env);
    const combined = combinedText(out);

    assertSpawnNonZeroStatus(out, expect.getState().currentTestName || "pwsh harness");
    expect(combined).toContain("Android CI module install FAILED after 2 attempt(s)");
    expect(combined).toContain("DXM_UNITY_DISABLE_EDITOR_REPAIR=1 disabled escalation");
    const moduleCommandArgs = fs
      .readFileSync(argsLog, "utf8")
      .trim()
      .split(/\r?\n/)
      .map((line) => line.split("\t"))
      .filter((args) => args.includes("-m"));
    expect(moduleCommandArgs.length).toBe(2);
    expect(moduleCommandArgs.some((args) => args.includes("android-open-jdk"))).toBe(false);
    expect(fs.existsSync(path.join(installRoot, "_quarantine"))).toBe(false);
    expect(fs.existsSync(installMarker)).toBe(false);
    expect(fs.existsSync(editorExe)).toBe(true);
  });

  // Native-startup repair must preserve Android coverage. Managed repair now
  // requests the selected profile atomically, so a Full-profile repaired editor
  // should already contain Android before the re-probe.
  //
  // STATIC corroboration (always-on, OS-independent): the production
  // Ensure-UnityNativeStartupHealthy must call Ensure-UnityCiModules AFTER the
  // Repair-UnityEditorWithCiModules call (so a refactor that drops the re-ensure is
  // caught even on win32, where the behavioral probe cannot run a non-PE exe).
  test("Ensure-UnityNativeStartupHealthy re-ensures all tiers after a repair (Android not dropped)", () => {
    const text = fs.readFileSync(ENSURE_EDITOR, "utf8");
    const start = text.indexOf("function Ensure-UnityNativeStartupHealthy");
    expect(start).toBeGreaterThanOrEqual(0);
    const after = text.indexOf("\nfunction ", start + 1);
    const body = after === -1 ? text.slice(start) : text.slice(start, after);
    // The repair, THEN a re-ensure-all-tiers, THEN the re-probe -- in that order.
    expect(body).toMatch(
      /\$repaired = Repair-UnityEditorWithCiModules[\s\S]*?\$repaired = Ensure-UnityCiModules -Version \$Version -EditorPath \$repaired -InstallRoot \$InstallRoot -Profile \$Profile -ManagedOnly:\$ManagedOnly[\s\S]*?Test-UnityNativeStartup -EditorPath \$repaired/
    );
  });

  // BEHAVIORAL proof (non-win32 only: the probe `& $EditorPath -version` cannot run
  // a non-PE exe stub on Windows, exactly why the harness skips the probe there by
  // default). The @cross-platform-regression suite still runs this on ubuntu+macos.
  const itNativeRepair = process.platform === "win32" ? test.skip : test;
  itNativeRepair(
    "a failed final native-startup probe repairs with the full module set atomically",
    () => {
      const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-native-repair-"));
      workspaces.push(base);
      const installRoot = path.join(base, "configured-root");
      const editorRoot = path.join(installRoot, "6000.0.32f1");
      const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
      // Markers: distinguish the pre-repair editor (probe fails) from the
      // post-repair editor (probe succeeds), and prove the full install args.
      const repairedMarker = path.join(base, "repaired.txt");
      const repairInstallMarker = path.join(base, "repair-install-args.txt");

      // The PRE-EXISTING editor + the FULL CI layout already on disk, so
      // Ensure-UnityCiModules returns early (nothing to install) and the run
      // reaches the FINAL native-startup probe. The pre-existing editor stub FAILS
      // the `-version` probe (exit 3) because the "repaired" marker is absent.
      const preRepairEditorBody = [
        "#!/usr/bin/env node",
        '"use strict";',
        "const fs = require('fs');",
        `const repairedMarker = ${JSON.stringify(repairedMarker)};`,
        "const args = process.argv.slice(2);",
        "if (args.includes('-version')) {",
        // Fail the probe until the repair install has run (marker written).
        "  if (fs.existsSync(repairedMarker)) { process.stdout.write('Unity fake version\\n'); process.exit(0); }",
        "  process.stderr.write('native startup failure (pre-repair)\\n'); process.exit(3);",
        "}",
        "process.stdout.write('Unity fake version\\n'); process.exit(0);",
        ""
      ].join("\n");
      fs.mkdirSync(path.dirname(editorExe), { recursive: true });
      fs.writeFileSync(editorExe, preRepairEditorBody);
      fs.chmodSync(editorExe, 0o755);
      // Seed the full module layout (core + android) so the first
      // Ensure-UnityCiModules pass is a clean no-op and the run reaches the probe.
      createCiModuleLayout(editorExe);

      const cliBody = [
        "const fs = require('fs');",
        "const path = require('path');",
        `const installRoot = ${JSON.stringify(installRoot)};`,
        `const repairedMarker = ${JSON.stringify(repairedMarker)};`,
        `const repairInstallMarker = ${JSON.stringify(repairInstallMarker)};`,
        "const editorRoot = path.join(installRoot, '6000.0.32f1');",
        "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
        "const dataRoot = path.join(editorRoot, 'Editor', 'Data');",
        "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
        "function writeFile(p, value) { mkdirp(path.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
        // After repair, the editor stub PASSES the probe (marker present).
        "function writeRepairedEditor() {",
        '  writeFile(editorExe, \'#!/usr/bin/env node\\n"use strict";\\nprocess.stdout.write("Unity fake version\\\\n");\\nprocess.exit(0);\\n\');',
        "}",
        "function writeFullModules() {",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'windowsstandalonesupport', 'Variations', 'win64_player_development_il2cpp', 'UnityPlayer.dll'), '');",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'WebGLSupport', 'UnityEditor.WebGL.Extensions.dll'), '');",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'WebGLSupport', 'BuildTools', 'Emscripten', 'emscripten', 'emcc.py'), '');",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'UnityEditor.Android.Extensions.dll'), '');",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'SDK', 'platform-tools', 'adb.exe'), '');",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'source.properties'), '');",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'windows-x86_64', 'bin', 'clang++.exe'), '');",
        "  writeFile(path.join(dataRoot, 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java.exe'), '');",
        "}",
        "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
        "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
        "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
        "if (args[0] === 'install-modules') { write('No modules found to install'); exit(6); }",
        "if (args[0] === 'uninstall') { write('metadata cleared'); exit(0); }",
        "if (args[0] === 'install') { fs.writeFileSync(repairedMarker, '1'); fs.writeFileSync(repairInstallMarker, args.join(' ')); writeRepairedEditor(); writeFullModules(); write('repaired editor with full modules'); exit(0); }",
        "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
      ];

      // Run the REAL native-startup probe (bypass the harness default skip by
      // defining the var as an empty string). Zero retry delay keeps it fast.
      const env = {
        ...process.env,
        DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE: "",
        DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS: "2",
        DXM_ENSURE_EDITOR_RETRY_DELAY_SECONDS: "0"
      };

      const out = runEnsureEditorWithFakeCli(cliBody, installRoot, env);
      const stdout = out.stdout || "";
      const combined = combinedText(out);

      assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
      // The run succeeds, resolving the repaired editor.
      expect(stdout.trim().split(/\r?\n/).pop()).toBe(editorExe);
      // The native-startup probe failed before the lock and triggered a managed reinstall.
      expect(combined).toContain("native startup probe failed before the license lock");
      // The full repair install requested Android through the single helper.
      expect(fs.existsSync(repairInstallMarker)).toBe(true);
      const repairArgs = fs.readFileSync(repairInstallMarker, "utf8");
      expect(repairArgs).toContain("--childModules");
      expect(repairArgs).toContain("android-sdk-ndk-tools");
      expect(repairArgs).not.toContain("android-open-jdk");
      // Android payload is present immediately after the full repair.
      expect(
        fs.existsSync(
          path.join(
            editorRoot,
            "Editor",
            "Data",
            "PlaybackEngines",
            "AndroidPlayer",
            "NDK",
            "source.properties"
          )
        )
      ).toBe(true);
      expect(
        fs.existsSync(
          path.join(
            editorRoot,
            "Editor",
            "Data",
            "PlaybackEngines",
            "AndroidPlayer",
            "OpenJDK",
            "bin",
            "java.exe"
          )
        )
      ).toBe(true);
    }
  );

  test("Invoke-UnityCliCapture captures spawn failures with exit code -1", () => {
    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        // Invoke-UnityCliCapture now delegates to the timeout-capable runner, so
        // extract the delegate and the helpers it consults (the timeout-knob and
        // the last-progress parser used in the timeout annotation).
        extractEnsureEditorFunctions([
          "Invoke-UnityCliCapture",
          "Invoke-UnityCliCaptureWithTimeout",
          "Get-EnsureEditorInstallTimeoutSeconds",
          "Get-LastCliProgressMessage",
          // The timeout runner also consults the heartbeat-stall env helper and
          // uses the de-duplicating tail formatter for its wrap-immune ::error::.
          "Get-EnsureEditorProgressStallSeconds",
          "Get-EnsureEditorProgressNoticeIntervalSeconds",
          "Get-CliProgressTriple",
          "Get-CollapsedCliOutputTail"
        ]),
        "$script:UnityCliPath = '/definitely/not/a/unity-cli'",
        "$result = Invoke-UnityCliCapture -Arguments @('install', '6000.0.32f1')",
        "Write-Output ('SUCCESS=' + $result.Success)",
        "Write-Output ('EXIT=' + $result.ExitCode)",
        "Write-Output ('OUTPUT=' + ($result.Output -join '|'))"
      ].join("\n")
    );
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(combined).toContain("SUCCESS=False");
    expect(combined).toContain("EXIT=-1");
    expect(combined).toContain("Unity CLI capture invoker threw");
  });

  test("base editor install failure recovers when Unity.exe resolves afterward", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-recover-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const cliRoot = installRoot;
    const resolvedEditor = path.join(cliRoot, "6000.0.32f1", "Editor", "Unity.exe");

    const cliBody = [
      "const fs = require('fs');",
      "const path = require('path');",
      `const cliRoot = ${JSON.stringify(cliRoot)};`,
      `const editorRoot = ${JSON.stringify(path.dirname(path.dirname(resolvedEditor)))};`,
      "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeFile(p, value) { mkdirp(path.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
      "function createModules() {",
      "  writeFile(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'windowsstandalonesupport', 'Variations', 'win64_player_development_il2cpp', 'UnityPlayer.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'UnityEditor.WebGL.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'BuildTools', 'Emscripten', 'emscripten', 'emcc.py'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'UnityEditor.Android.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'SDK', 'platform-tools', 'adb.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'source.properties'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'windows-x86_64', 'bin', 'clang++.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(cliRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args.length >= 1 && args[0] === 'install') { createModules(); write('Editor already installed'); exit(6); }",
      "if (args.length >= 1 && args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot);
    const stdout = out.stdout || "";
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    // Structural read uses the RAW stream (last line = resolved editor path);
    // the phrase assertion uses the wrap-normalized text.
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(resolvedEditor);
    expect(combined).toContain("Unity.exe is resolvable afterward");
  });

  test("already-installed base editor failure without Unity.exe quarantines and retries", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-partial-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const cliRoot = installRoot;
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const resolvedEditor = path.join(editorRoot, "Editor", "Unity.exe");
    const attemptMarker = path.join(base, "install-attempts.txt");
    const uninstallMarker = path.join(base, "uninstall-called.txt");
    fs.mkdirSync(editorRoot, { recursive: true });
    fs.writeFileSync(path.join(editorRoot, "partial-marker.txt"), "partial", "utf8");

    const cliBody = [
      "const fs = require('fs');",
      "const path = require('path');",
      `const cliRoot = ${JSON.stringify(cliRoot)};`,
      `const editorRoot = ${JSON.stringify(editorRoot)};`,
      `const attemptMarker = ${JSON.stringify(attemptMarker)};`,
      `const uninstallMarker = ${JSON.stringify(uninstallMarker)};`,
      "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeFile(p, value) { mkdirp(path.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
      "function createModules() {",
      "  writeFile(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'windowsstandalonesupport', 'Variations', 'win64_player_development_il2cpp', 'UnityPlayer.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'UnityEditor.WebGL.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'BuildTools', 'Emscripten', 'emscripten', 'emcc.py'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'UnityEditor.Android.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'SDK', 'platform-tools', 'adb.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'source.properties'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'windows-x86_64', 'bin', 'clang++.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(cliRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args.length >= 1 && args[0] === 'install') {",
      "  const attempts = fs.existsSync(attemptMarker) ? Number(fs.readFileSync(attemptMarker, 'utf8')) : 0;",
      "  fs.writeFileSync(attemptMarker, String(attempts + 1));",
      "  if (!fs.existsSync(uninstallMarker)) { write('Editor already installed'); write('partial install marker'); exit(6); }",
      "  createModules(); write('fresh install after quarantine'); exit(0);",
      "}",
      "if (args[0] === 'uninstall') { fs.writeFileSync(uninstallMarker, '1'); write('metadata cleared'); exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
      "if (args[0] === 'install-modules') { write('No modules found to install'); exit(6); }",
      "if (args.length >= 1 && args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot);
    const stdout = out.stdout || "";
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(resolvedEditor);
    expect(combined).toContain("Unity editor resolution diagnostics");
    expect(combined).toContain("partial install marker");
    expect(combined).toContain("metadata cleared");
    expect(combined).toContain("Quarantining unmanaged or partial Unity 6000.0.32f1 install");
    expect(combined).toContain("fresh install after quarantine");
    expect(Number(fs.readFileSync(attemptMarker, "utf8"))).toBe(2);
    expect(fs.readFileSync(uninstallMarker, "utf8")).toBe("1");
    expect(fs.readdirSync(path.join(installRoot, "_quarantine")).length).toBeGreaterThan(0);
  });

  test("already-installed repair quarantines both configured and CLI getter roots", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-nested-cli-root-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const cliRoot = path.join(installRoot, "cli-root");
    const configuredPartialRoot = path.join(installRoot, "6000.0.32f1");
    const cliPartialRoot = path.join(cliRoot, "6000.0.32f1");
    const editorRoot = cliPartialRoot;
    const resolvedEditor = path.join(editorRoot, "Editor", "Unity.exe");
    const attemptMarker = path.join(base, "install-attempts.txt");
    const uninstallMarker = path.join(base, "uninstall-called.txt");
    const configuredPartialMarker = path.join(configuredPartialRoot, "partial-marker.txt");
    const cliPartialMarker = path.join(cliPartialRoot, "partial-marker.txt");
    fs.mkdirSync(configuredPartialRoot, { recursive: true });
    fs.mkdirSync(cliPartialRoot, { recursive: true });
    fs.writeFileSync(configuredPartialMarker, "configured partial", "utf8");
    fs.writeFileSync(cliPartialMarker, "cli partial", "utf8");

    const cliBody = [
      "const fs = require('fs');",
      "const path = require('path');",
      `const cliRoot = ${JSON.stringify(cliRoot)};`,
      `const editorRoot = ${JSON.stringify(editorRoot)};`,
      `const attemptMarker = ${JSON.stringify(attemptMarker)};`,
      `const uninstallMarker = ${JSON.stringify(uninstallMarker)};`,
      "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeFile(p, value) { mkdirp(path.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
      "function createModules() {",
      "  writeFile(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'windowsstandalonesupport', 'Variations', 'win64_player_development_il2cpp', 'UnityPlayer.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'UnityEditor.WebGL.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'WebGLSupport', 'BuildTools', 'Emscripten', 'emscripten', 'emcc.py'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'UnityEditor.Android.Extensions.dll'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'SDK', 'platform-tools', 'adb.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'source.properties'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'windows-x86_64', 'bin', 'clang++.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java.exe'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(cliRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args.length >= 1 && args[0] === 'install') {",
      "  const attempts = fs.existsSync(attemptMarker) ? Number(fs.readFileSync(attemptMarker, 'utf8')) : 0;",
      "  fs.writeFileSync(attemptMarker, String(attempts + 1));",
      "  if (!fs.existsSync(uninstallMarker)) { write('Editor already installed'); write('nested partial install marker'); exit(6); }",
      "  createModules(); write('fresh install into nested CLI root'); exit(0);",
      "}",
      "if (args[0] === 'uninstall') { fs.writeFileSync(uninstallMarker, '1'); write('metadata cleared'); exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
      "if (args[0] === 'install-modules') { write('No modules found to install'); exit(6); }",
      "if (args.length >= 1 && args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot);
    const stdout = out.stdout || "";
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(resolvedEditor);
    expect(combined).toContain("nested partial install marker");
    expect(combined).toContain("fresh install into nested CLI root");
    expect(fs.existsSync(configuredPartialMarker)).toBe(false);
    expect(fs.existsSync(cliPartialMarker)).toBe(false);
    expect(Number(fs.readFileSync(attemptMarker, "utf8"))).toBe(2);
    expect(fs.readdirSync(path.join(installRoot, "_quarantine"))).toHaveLength(2);
  });

  test("managed-only mode refuses CLI mutations when the getter root is external", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-external-root-"));
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const externalRoot = path.join(base, "external-cli-root");
    const mutationMarker = path.join(base, "mutation-attempted.txt");

    const cliBody = [
      "const fs = require('fs');",
      `const externalRoot = ${JSON.stringify(externalRoot)};`,
      `const mutationMarker = ${JSON.stringify(mutationMarker)};`,
      "if (args.length === 1 && args[0] === 'install-path') { write(externalRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (['install', 'install-modules', 'uninstall'].includes(args[0])) { fs.writeFileSync(mutationMarker, args.join(' ')); write('mutation attempted'); exit(9); }",
      "if (args.length >= 1 && args[0] === 'editors') { write(JSON.stringify({ editors: [] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot);

    assertSpawnNonZeroStatus(out, expect.getState().currentTestName || "pwsh harness");
    // The guard reason and the "outside the managed root" phrase are wrap-prone:
    // PowerShell's ConciseView word-wraps the throw at the host console width, so a
    // raw substring check flakes on the narrower Windows runner. assertPwshContains
    // normalizes first (rejoins the gutter, strips ANSI) and, on a miss, prints a
    // diagnostic that shows whether the gutter/ANSI were present. The externalRoot
    // PATH is recovered via production's wrap-immune `Write-Host "::error::..."` line.
    assertPwshContains(out, "cannot mutate editors");
    assertPwshContains(out, "outside the managed root");
    assertPwshContains(out, externalRoot);
    expect(fs.existsSync(mutationMarker)).toBe(false);
  });

  // END-TO-END leak regression. A real Unity Hub install at
  // `<fakePF>\Unity\Hub\Editor\<ver>\Editor\Unity.exe` is exactly what
  // ensure-editor.ps1's host-default probe finds when `${env:ProgramFiles}` is
  // populated. We inject ProgramFiles=<fakePF> and run through the sandbox helper;
  // resolution MUST land on the controlled JSON-reported editor and NEVER on the
  // fake install -- proving removal + sandboxing works through the REAL ps1.
  //
  // What this test does and does NOT prove (be precise about the casing claim):
  //   - It proves the sandbox helper is actually wired and effective end-to-end:
  //     a TOTAL bypass (e.g. passing process.env straight through, so ProgramFiles
  //     is left populated) makes the fake install leak in and FAILS this test on
  //     ANY OS.
  //   - It does NOT, on Linux, distinguish the case-SENSITIVE-delete bug from the
  //     correct fix. We inject a case-variant PROGRAMFILES=<fakePF> alongside the
  //     canonical ProgramFiles, but Linux pwsh reads env-var names CASE-SENSITIVELY,
  //     so a surviving PROGRAMFILES is invisible to `${env:ProgramFiles}` in the
  //     child -- under a case-sensitive `delete env.ProgramFiles` simulation this
  //     test stays GREEN on Linux. The case-INSENSITIVE-removal proof (the actual
  //     crux of the fix) lives in the golden unit test
  //     scripts/lib/__tests__/spawn-env-sandbox.test.js, which asserts directly
  //     that every case-variant key is removed. The PROGRAMFILES injection is kept
  //     because it is harmless here and MEANINGFUL on Windows (where it reproduces
  //     the exact case-miss), but it does not strengthen the Linux assertion.
  test("a host Unity install in ProgramFiles never leaks into resolution (hermetic sandbox)", () => {
    const base = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-leak-"));
    workspaces.push(base);

    const installRoot = path.join(base, "configured-root");
    const cliRoot = installRoot;

    // The CONTROLLED editor we WANT resolution to land on. It lives under the
    // managed install root, while a fake host install also exists under ProgramFiles.
    const managedEditorRoot = path.join(installRoot, "6000.0.32f1");
    const controlledEditor = path.join(managedEditorRoot, "Editor", "Unity.exe");
    fs.mkdirSync(path.dirname(controlledEditor), { recursive: true });
    writeFakeUnityEditor(controlledEditor);
    createCiModuleLayout(controlledEditor);

    // The FAKE host Unity Hub install the script's ProgramFiles probe would find.
    // Built with OS-native separators because pwsh's Join-Path normalizes the
    // backslash segments to the native separator on every platform, so this is
    // the literal path Test-Path -LiteralPath probes.
    const fakeProgramFiles = path.join(base, "fake-program-files");
    const leakEditor = path.join(
      fakeProgramFiles,
      "Unity",
      "Hub",
      "Editor",
      "6000.0.32f1",
      "Editor",
      "Unity.exe"
    );
    fs.mkdirSync(path.dirname(leakEditor), { recursive: true });
    writeFakeUnityEditor(leakEditor);

    const cliBody = [
      `const cliRoot = ${JSON.stringify(cliRoot)};`,
      "if (args.length === 1 && args[0] === 'install-path') { write(cliRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
      "if (args[0] === 'install-modules') { write('No modules found to install'); exit(6); }",
      "if (args.length >= 1 && args[0] === 'editors') { write(JSON.stringify({ editors: [] })); exit(0); }"
    ];

    // Inject the leak-source var in BOTH the canonical and the ALL-CAPS casing.
    // The canonical ProgramFiles is what makes this test meaningful on every OS;
    // the ALL-CAPS PROGRAMFILES reproduces the exact Windows case-insensitive miss
    // (it is invisible to Linux pwsh, so it only adds discriminating power on
    // Windows -- see the test's header comment). The sandbox helper inside
    // runEnsureEditorWithFakeCli must scrub every casing.
    const leakyBaseEnv = {
      ...process.env,
      ProgramFiles: fakeProgramFiles,
      PROGRAMFILES: fakeProgramFiles
    };

    const out = runEnsureEditorWithFakeCli(cliBody, installRoot, leakyBaseEnv);
    const stdout = out.stdout || "";
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    // Resolution lands on the controlled JSON-reported path...
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(controlledEditor);
    // ...and the fake host install NEVER appears anywhere in the output.
    expect(combined).not.toContain(leakEditor);
    expect(combined).not.toContain(fakeProgramFiles);
  });
});
