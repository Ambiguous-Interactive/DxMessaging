/**
 * @fileoverview End-to-end StrictMode + license-leak smoke test for
 * scripts/unity/run-ci-tests.ps1.
 *
 * WHY THIS EXISTS (StrictMode): run-ci-tests.ps1 runs under `Set-StrictMode
 * -Version Latest`. `Get-AcceleratorArguments` `return @()` on its empty path. A
 * PowerShell function returning an EMPTY array emits ZERO objects, so a bare
 * capture (`$x = Get-Foo`) assigns AutomationNull (it compares equal to $null) --
 * the empty array unwraps to nothing. The script then evaluated
 * `$acceleratorArgs.Count`, which under StrictMode Latest (and 2.0+, verified on
 * pwsh 7.6.1, the CI runtime) THROWS "The property 'Count' cannot be found on
 * this object." That property-access throw is the real bug. The fix `@()`-wraps
 * the capture at the source.
 *
 * WHY THIS EXISTS (license leak): the repo uses classic SERIAL activation. CI
 * activates the paid seat (`Unity.exe -serial -username -password`) and MUST
 * return it (`Unity.exe -returnlicense`) on EVERY exit path -- clean exit, throw,
 * or a killed editor that still unwinds the finally. This test is the LIVE
 * companion to unity-license-leak-safety.test.js's static finally-return guard:
 * it ACTUALLY RUNS the real script via pwsh under its real StrictMode with the
 * serial creds set, drives both a FAILING and a PASSING editor stub, and asserts
 * the seat is RETURNED in BOTH cases (the marker file the stub appends to on
 * `-returnlicense`). The return-at-start (before the try) AND the finally-return
 * are both exercised.
 *
 * A STUB editor stands in for Unity.exe (cross-platform pwsh) so the run never
 * touches a real Unity install: it branches on its args (-returnlicense ->
 * append to the marker; -runTests -> write/skip results.xml; otherwise -- serial
 * activation / configure -- a clean exit-0 no-op). Everything lives in OS temp
 * dirs; the repo tree is never polluted.
 *
 * pwsh is preinstalled on GitHub's runners (the script-tests.yml matrix), so
 * this runs in CI. When pwsh is absent locally the run assertions are skipped,
 * but an always-on sanity assertion still runs so a zero-coverage regression
 * (the test silently validating nothing) cannot hide.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");
const { sandboxHostFolderEnv } = require("../lib/spawn-env-sandbox");
const { assertSpawnNonZeroStatus, assertSpawnStatus, combinedText } = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const RUN_CI_TESTS = path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1");

function currentTestLabel() {
  const state = expect.getState();
  return state.currentTestName || "pwsh harness";
}

function expectPwshStatus(run, expectedStatus) {
  assertSpawnStatus(run, expectedStatus, currentTestLabel());
}

function expectPwshFailure(run) {
  assertSpawnNonZeroStatus(run, currentTestLabel());
}

// Env vars whose presence would route the script DOWN a non-empty/divergent
// path. We delete the accelerator endpoint so the empty-array branch (the one
// that triggered the StrictMode bug) is exercised. GITHUB_WORKSPACE is deleted
// so the default -RepoRoot does not point at the real repo (we pass an explicit
// temp RepoRoot). GITHUB_ACTIONS is deleted so the in-CI creds gate cannot fire
// under the Jest-in-CI harness (Jest itself runs with GITHUB_ACTIONS=true on the
// script-tests matrix); with the serial creds SET, $hasLicenseCreds is true and
// activate/return run regardless. UNITY_LICENSING_SERVER is deleted because the
// floating-server path was removed -- it must not exist in the test env.
//
// The host-default FOLDER vars (LOCALAPPDATA, ProgramFiles, ...) are NOT in this
// list: run-ci-tests.ps1 probes `$env:LOCALAPPDATA` (Unity caches) and
// `${env:ProgramFiles}` / `${env:ProgramFiles(x86)}` / `$env:LOCALAPPDATA` (the
// Unity Licensing Client). They are neutralized hermetically via
// sandboxHostFolderEnv (empty sandbox dirs) instead of a case-sensitive delete,
// so a real install on a Windows host can never leak in -- the same Windows
// case-insensitivity gotcha that bit ensure-editor.ps1's tests.
const ENV_TO_DELETE = [
  "UNITY_ACCELERATOR_ENDPOINT",
  "UNITY_LICENSE",
  "UNITY_LICENSE_B64",
  "UNITY_LICENSE_FILE",
  "GITHUB_WORKSPACE",
  "GITHUB_ACTIONS",
  "UNITY_LICENSING_SERVER"
];

// The serial-activation credentials. SETTING all three makes
// run-ci-tests.ps1's `$hasLicenseCreds` true, so:
//   - return-at-start runs BEFORE the try (reclaims any prior leaked seat),
//   - Invoke-UnityLicenseActivate runs as the first thing inside the try,
//   - Invoke-UnityLicenseReturn runs in the finally on EVERY exit path.
// They are bogus values: the STUB editor never validates them, it only branches
// on the presence of -serial / -returnlicense / -runTests in its args.
const SERIAL_CREDS = {
  UNITY_SERIAL: "SC-TEST-SERIAL-0000",
  UNITY_EMAIL: "ci-bot@example.invalid",
  UNITY_PASSWORD: "not-a-real-password"
};

// Cross-platform STUB editor body shared by all scenarios. It branches on args:
//   - `-returnlicense` (the seat return)     -> APPEND a line to the marker file
//     named by $env:DXM_SMOKE_RETURN_MARKER, then exit 0. This is how the test
//     proves the seat was returned (on return-at-start AND in the finally).
//   - `-runTests` AND `-quit` together       -> exit 0 WITHOUT writing
//     results.xml. This models the REAL Unity behavior: per
//     https://docs.unity3d.com/Manual/EditorCommandLineArguments.html the editor
//     QUITS IMMEDIATELY when -quit and -runTests are combined, before in-progress
//     tests can complete, exiting 0 with NO results file. This makes the
//     existing "fails LOUDLY when the editor exits 0 but produces no results.xml"
//     scenario a real regression detector for the -quit + -runTests combo --
//     pinned by the data-driven Unity.exe arg-array contract test in
//     scripts/__tests__/unity-runner-script-contract.test.js
//     ("run-ci-tests Unity.exe arg arrays obey -runTests excludes -quit").
//   - `-runTests` alone (the test run)       -> {RUNTESTS_BODY} (per scenario).
//   - otherwise (-serial activation, or the standalone configure -executeMethod
//     pass) -> a clean exit-0 no-op (activation "succeeds").
// {RUNTESTS_BODY} is substituted per scenario below.
function stubEditor(runTestsBody) {
  return [
    "[CmdletBinding()] param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Rest)",
    "$Rest = @($Rest)",
    "if ($Rest -contains '-returnlicense') {",
    "  $marker = $env:DXM_SMOKE_RETURN_MARKER",
    "  if ($marker) {",
    "    $d = Split-Path -Parent $marker",
    "    if ($d -and -not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
    "    Add-Content -LiteralPath $marker -Value ('returned ' + ($Rest -join ' '))",
    "  }",
    "  exit 0",
    "}",
    "if (($Rest -contains '-runTests') -and ($Rest -contains '-quit')) {",
    "  # Simulate Unity's documented behavior: -quit + -runTests => quit BEFORE",
    "  # tests complete, exit 0 with NO results.xml. See the stub comment above.",
    "  exit 0",
    "}",
    "if ($Rest -contains '-runTests') {",
    runTestsBody,
    "}",
    "exit 0",
    ""
  ].join("\n");
}

// PASSING test run: write a passing NUnit results.xml to the path after
// -testResults and exit 0.
const RUNTESTS_PASSING = [
  "  $i = [array]::IndexOf($Rest, '-testResults')",
  "  if ($i -ge 0 -and ($i + 1) -lt $Rest.Count) {",
  "    $out = $Rest[$i + 1]; $d = Split-Path -Parent $out",
  "    if ($d) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
  '    \'<?xml version="1.0" encoding="utf-8"?><test-run total="1" passed="1" failed="0" skipped="0" result="Passed"></test-run>\' | Set-Content -LiteralPath $out -Encoding UTF8',
  "  }",
  "  exit 0"
].join("\n");

// PASSING-THEN-CRASH test run: write a passing NUnit results.xml, THEN exit
// non-zero. Models Unity crashing in a background thread (the DirectoryMonitor
// teardown 0xC0000005) DURING shutdown AFTER RunFinished already wrote the file.
// The results FILE is the source of truth, so the harness must PASS (with a
// benign-crash warning), never turn a passing run red on the shutdown exit code.
// 134 is a generic non-zero code that survives 8-bit truncation on POSIX (a
// faithful 0xC0000005 is only reproducible as a real process exit on Windows).
const RUNTESTS_PASSING_THEN_CRASH = [
  "  $i = [array]::IndexOf($Rest, '-testResults')",
  "  if ($i -ge 0 -and ($i + 1) -lt $Rest.Count) {",
  "    $out = $Rest[$i + 1]; $d = Split-Path -Parent $out",
  "    if ($d) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
  '    \'<?xml version="1.0" encoding="utf-8"?><test-run total="1" passed="1" failed="0" skipped="0" result="Passed"></test-run>\' | Set-Content -LiteralPath $out -Encoding UTF8',
  "  }",
  "  exit 134"
].join("\n");

// LYING test run: exits 0 (a clean editor exit) but writes NO results.xml --
// the "Unity quietly produced nothing" failure mode. Test-NUnitResults must
// catch the missing file and fail LOUDLY rather than reporting a false green.
const RUNTESTS_NO_RESULTS = ["  exit 0"].join("\n");

// FAILING test run: exits non-zero and writes nothing. Stands in for a Unity run
// that crashes/times out AFTER the seat was activated. The leak-regression test
// asserts the seat is STILL returned (the finally fires) even though the run
// fails.
const RUNTESTS_FAILING = ["  exit 1"].join("\n");
const RUNTESTS_FAILING_CS8032 = [
  "  Write-Output \"warning CS8032: An instance of analyzer WallstopStudios.DxMessaging.SourceGenerators.DxMessageIdGenerator cannot be created from Editor/Analyzers/WallstopStudios.DxMessaging.SourceGenerators.dll : Could not load file or assembly 'Microsoft.CodeAnalysis, Version=4.2.0.0'.\"",
  "  Write-Output \"Tests/Runtime/Scripts/Messages/SimpleUntargetedMessage.cs(1,1): error CS0315: The type 'SimpleUntargetedMessage' cannot be used as type parameter 'T'.\"",
  "  exit 1"
].join("\n");

const STUB_EDITOR = stubEditor(RUNTESTS_PASSING);
const STUB_EDITOR_NO_RESULTS = stubEditor(RUNTESTS_NO_RESULTS);
const STUB_EDITOR_FAILING = stubEditor(RUNTESTS_FAILING);
const STUB_EDITOR_FAILING_CS8032 = stubEditor(RUNTESTS_FAILING_CS8032);
const STUB_EDITOR_PASSING_THEN_CRASH = stubEditor(RUNTESTS_PASSING_THEN_CRASH);

// ===========================================================================
// STANDALONE SPLIT-BUILD STUBS.
//
// The standalone path now (1) launches the editor BUILD via run-ci-tests'
// Invoke-ProcessWithTreeKillTimeout (System.Diagnostics.Process, NOT pwsh `&`),
// then (2) launches the editor-built player exe DIRECTLY via the same watchdog.
// Because both launches go through Process.Start (UseShellExecute=$false), the
// stub files must be DIRECTLY OS-executable: on Linux/macOS a `#!/usr/bin/env
// pwsh` shebang script with the executable bit set works (kernel shebang
// dispatch); on Windows CreateProcess rejects a non-PE file, so the standalone
// BEHAVIORAL scenarios are skipped on win32 (the editmode/playmode + license
// regressions, which still launch via pwsh `&`, keep their Windows coverage).
// This mirrors how the ensure-editor native-startup-probe tests skip their
// behavioral probe on win32.
//
// The standalone editor BUILD stub: when it sees a standalone build
// (-runTests AND -buildTarget), it WRITES the player stub to
// $env:DXM_PLAYER_BUILD_PATH (the path run-ci-tests will then launch directly)
// and exits 0 WITHOUT writing results.xml -- modeling Unity's real split build,
// where the editor produces the player and exits via PostBuildCleanup, and the
// results file is written later by the PLAYER. It also honors -returnlicense
// (append to the marker), the -serial activation, and the configure
// -executeMethod pass (clean exit-0 no-ops). The optional
// DXM_SMOKE_BUILD_SLEEP_SECONDS forces a hang so the BUILD watchdog fires (and in
// that case it does NOT write the player, so a survived watchdog would fail the
// post-build exe-exists assert anyway).
const SHEBANG = "#!/usr/bin/env pwsh";

// The PLAYER stub body (substituted into the player stub the build stub writes).
// It reads -dxmTestResults <path> and behaves per $env:DXM_SMOKE_PLAYER_MODE:
//   pass        -> write a passing <test-run total=1.../> and exit 0
//   fail        -> write a FAILING <test-run .../> (failed=1) and exit 1
//   no-results  -> exit 0 WITHOUT writing (models a player that died before
//                  RunFinished wrote the file)
//   sleep       -> sleep > the player watchdog window WITHOUT writing (no file)
//   write-then-hang -> write a PASSING file, THEN sleep > the watchdog window so the
//                  watchdog tree-kills it AFTER results exist (models Application.Quit
//                  deferred/ignored in -batchmode IL2CPP); the file is the source of
//                  truth, so the harness must treat this as a PASS (with a warning)
//   write-then-crash -> write a PASSING file, THEN exit NON-ZERO without hanging
//                  (models the player crashing in a background thread during shutdown
//                  AFTER RunFinished wrote the file -- the 0xC0000005 DirectoryMonitor
//                  class, but on the PLAYER). NOT a watchdog timeout: this exercises
//                  the player-exit-code (advisory) branch, so the harness honors the
//                  file and passes with the benign-shutdown-crash warning.
// The player NEVER reads an env results channel; only -dxmTestResults.
function playerStubBody() {
  return [
    SHEBANG,
    "[CmdletBinding()] param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Rest)",
    "$Rest = @($Rest)",
    "$mode = $env:DXM_SMOKE_PLAYER_MODE",
    "if ($mode -eq 'sleep') { Start-Sleep -Seconds 30; exit 0 }",
    "$i = [array]::IndexOf($Rest, '-dxmTestResults')",
    "if ($i -lt 0 -or ($i + 1) -ge $Rest.Count) {",
    "  [Console]::Error.WriteLine('player stub got no -dxmTestResults'); exit 2",
    "}",
    "if ($mode -eq 'no-results') { exit 0 }",
    "$out = $Rest[$i + 1]",
    "$d = Split-Path -Parent $out",
    "if ($d -and -not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
    "if ($mode -eq 'fail') {",
    '  \'<?xml version="1.0" encoding="utf-8"?><test-run id="2" total="1" passed="0" failed="1" inconclusive="0" skipped="0" asserts="1" result="Failed"><test-suite type="Assembly" result="Failed"><test-case name="Ns.WillFail" fullname="Ns.WillFail" result="Failed"><failure><message>stub player failure</message></failure></test-case></test-suite></test-run>\' | Set-Content -LiteralPath $out -Encoding UTF8',
    "  exit 1",
    "}",
    '\'<?xml version="1.0" encoding="utf-8"?><test-run id="2" total="1" passed="1" failed="0" inconclusive="0" skipped="0" asserts="1" result="Passed"><test-suite type="Assembly" result="Passed"><test-case name="Ns.WillPass" fullname="Ns.WillPass" result="Passed" /></test-suite></test-run>\' | Set-Content -LiteralPath $out -Encoding UTF8',
    // write-then-hang: results are written; now hang so the watchdog tree-kills the
    // player AFTER the file exists (models a deferred Application.Quit). The harness
    // must honor the file as the source of truth and pass.
    "if ($mode -eq 'write-then-hang') { Start-Sleep -Seconds 30; exit 0 }",
    // write-then-crash: results are written; now exit NON-ZERO immediately (a
    // background-thread shutdown crash, NOT a watchdog timeout). The harness honors
    // the file and passes with the benign-crash warning (player exit is advisory).
    "if ($mode -eq 'write-then-crash') { exit 134 }",
    "exit 0",
    ""
  ].join("\n");
}

// The standalone editor BUILD stub. A shebang script (Process.Start-launchable on
// Linux/macOS). Writes the player stub to $env:DXM_PLAYER_BUILD_PATH on a
// standalone build, unless DXM_SMOKE_BUILD_SLEEP_SECONDS forces a build hang.
//
// The CONFIGURE pass (-executeMethod DxmCiTestConfigurator.Apply, with -quit, NO
// -runTests) models the real DxmCiTestConfigurator.Apply: it writes the success
// MARKER to $env:DXM_CONFIGURE_MARKER_PATH and then exits per
// $env:DXM_SMOKE_CONFIGURE_MODE:
//   pass (default) -> write the marker, exit 0 (normal success)
//   crash          -> write the marker, then exit NON-ZERO (models Unity crashing
//                     in a background thread during shutdown AFTER Apply completed,
//                     e.g. the 0xC0000005 DirectoryMonitor teardown crash). The
//                     runner must honor the marker and pass with a warning.
//   no-marker      -> exit NON-ZERO WITHOUT writing the marker (models a configure
//                     that genuinely failed before completing). The runner must
//                     fail loudly.
function standaloneEditorStub() {
  return [
    SHEBANG,
    "[CmdletBinding()] param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Rest)",
    "$Rest = @($Rest)",
    "if ($Rest -contains '-returnlicense') {",
    "  $marker = $env:DXM_SMOKE_RETURN_MARKER",
    "  if ($marker) {",
    "    $d = Split-Path -Parent $marker",
    "    if ($d -and -not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
    "    Add-Content -LiteralPath $marker -Value ('returned ' + ($Rest -join ' '))",
    "  }",
    "  exit 0",
    "}",
    // The CONFIGURE pass: -executeMethod (with -quit), NOT a -runTests build.
    "if ($Rest -contains '-executeMethod') {",
    "  $mode = $env:DXM_SMOKE_CONFIGURE_MODE",
    "  if (-not $mode) { $mode = 'pass' }",
    "  if ($mode -ne 'no-marker') {",
    "    $cm = $env:DXM_CONFIGURE_MARKER_PATH",
    "    if ($cm) {",
    "      $d = Split-Path -Parent $cm",
    "      if ($d -and -not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
    "      Set-Content -LiteralPath $cm -Value 'DxmCiTestConfigurator.Apply completed' -Encoding UTF8",
    "    }",
    "  }",
    "  if ($mode -eq 'pass') { exit 0 }",
    // crash / no-marker: exit non-zero (a benign shutdown crash, or a real failure
    // when the marker was withheld). 134 is a generic abort-style non-zero code that
    // survives 8-bit truncation on POSIX.
    "  exit 134",
    "}",
    // The standalone editor BUILD: -runTests WITHOUT -quit, WITH -buildTarget.
    "if (($Rest -contains '-runTests') -and ($Rest -contains '-buildTarget')) {",
    "  $sleep = 0",
    "  if ([int]::TryParse($env:DXM_SMOKE_BUILD_SLEEP_SECONDS, [ref]$sleep) -and $sleep -gt 0) {",
    "    Start-Sleep -Seconds $sleep; exit 0",
    "  }",
    "  $exe = $env:DXM_PLAYER_BUILD_PATH",
    "  if ($exe) {",
    "    $d = Split-Path -Parent $exe",
    "    if ($d -and -not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
    "    $data = Join-Path $d (([System.IO.Path]::GetFileNameWithoutExtension($exe)) + '_Data')",
    "    New-Item -ItemType Directory -Force -Path $data | Out-Null",
    "    Set-Content -LiteralPath $exe -Value $env:DXM_SMOKE_PLAYER_STUB_BODY -Encoding UTF8",
    "    if (-not $IsWindows) { & chmod +x $exe }",
    "  }",
    // Optional: model a build that produces a VALID player exe but then exits
    // non-zero (a background-thread shutdown crash after the build finished). The
    // runner must honor the exe and pass with a warning.
    "  if ($env:DXM_SMOKE_BUILD_CRASH_AFTER_OUTPUT -eq '1') { exit 134 }",
    // The split build writes NO results.xml; the PLAYER writes it later.
    "  exit 0",
    "}",
    // Any other invocation (the -serial activation): a clean exit-0 no-op.
    "exit 0",
    ""
  ].join("\n");
}

const PLAYER_STUB_BODY = playerStubBody();
const STANDALONE_EDITOR_STUB = standaloneEditorStub();

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

// Build a clean env where the bug-triggering branches are hit AND the serial
// creds are present (so activate/return run). cleanedEnv neutralizes the
// host-default FOLDER vars hermetically (sandboxHostFolderEnv -> empty sandbox
// dirs under the per-run workspace, scrubbing every Windows case-variant),
// deletes the remaining path-diverging vars, then sets the three serial
// credentials.
function cleanedEnv(sandboxRoot) {
  const env = sandboxHostFolderEnv(process.env, sandboxRoot);
  for (const key of ENV_TO_DELETE) {
    delete env[key];
  }
  Object.assign(env, SERIAL_CREDS);
  return env;
}

// Stand up an isolated, self-contained workspace in the OS temp dir:
//   repoRoot/            -> package.json + Runtime/  (so Assert-RepoRoot passes)
//   artifacts/           -> -ArtifactsPath
//   project/             -> -ProjectPath
//   stub-editor.ps1      -> the passing -UnityEditorPath (writes results.xml)
//   stub-no-results.ps1  -> a stub that exits 0 but writes NO results.xml
//   stub-editor-failing.ps1 -> a stub whose -runTests exits non-zero
//   returned.marker      -> $env:DXM_SMOKE_RETURN_MARKER (return-proof file)
function makeWorkspace() {
  const base = makeTempDir("strictmode-smoke");
  const repoRoot = path.join(base, "repo");
  const artifacts = path.join(base, "artifacts");
  const project = path.join(base, "project");

  fs.mkdirSync(path.join(repoRoot, "Runtime"), { recursive: true });
  fs.mkdirSync(path.join(repoRoot, "Editor", "Analyzers"), { recursive: true });
  fs.writeFileSync(path.join(repoRoot, "package.json"), "{}\n", "utf8");
  for (const dllName of [
    "WallstopStudios.DxMessaging.SourceGenerators.dll",
    "WallstopStudios.DxMessaging.Analyzer.dll"
  ]) {
    fs.writeFileSync(path.join(repoRoot, "Editor", "Analyzers", dllName), "stub", "utf8");
  }
  fs.mkdirSync(artifacts, { recursive: true });
  fs.mkdirSync(project, { recursive: true });

  const stubPath = path.join(base, "stub-editor.ps1");
  fs.writeFileSync(stubPath, STUB_EDITOR, "utf8");

  const noResultsStubPath = path.join(base, "stub-no-results.ps1");
  fs.writeFileSync(noResultsStubPath, STUB_EDITOR_NO_RESULTS, "utf8");

  const failingStubPath = path.join(base, "stub-editor-failing.ps1");
  fs.writeFileSync(failingStubPath, STUB_EDITOR_FAILING, "utf8");

  const failingCs8032StubPath = path.join(base, "stub-editor-failing-cs8032.ps1");
  fs.writeFileSync(failingCs8032StubPath, STUB_EDITOR_FAILING_CS8032, "utf8");

  const passingThenCrashStubPath = path.join(base, "stub-editor-passing-then-crash.ps1");
  fs.writeFileSync(passingThenCrashStubPath, STUB_EDITOR_PASSING_THEN_CRASH, "utf8");

  // The standalone editor BUILD stub. Written WITHOUT a .ps1-via-`&` assumption:
  // run-ci-tests launches it via Process.Start (the watchdog), so it carries a
  // shebang and (on non-Windows) the executable bit. It writes the player stub to
  // $env:DXM_PLAYER_BUILD_PATH on a standalone build.
  const standaloneEditorStubPath = path.join(base, "stub-editor-standalone.exe");
  fs.writeFileSync(standaloneEditorStubPath, STANDALONE_EDITOR_STUB, "utf8");
  if (process.platform !== "win32") {
    fs.chmodSync(standaloneEditorStubPath, 0o755);
  }

  const returnMarker = path.join(base, "returned.marker");

  return {
    base,
    repoRoot,
    artifacts,
    project,
    stubPath,
    noResultsStubPath,
    failingStubPath,
    failingCs8032StubPath,
    passingThenCrashStubPath,
    standaloneEditorStubPath,
    returnMarker
  };
}

// Run run-ci-tests.ps1 against a given workspace. `editorPath` selects which stub
// editor stands in for Unity.exe (the passing one by default). The serial creds
// (set by cleanedEnv) make the script activate at start and return in the
// finally; DXM_SMOKE_RETURN_MARKER is where the stub records each -returnlicense.
function runScript(mode, ws, editorPath = ws.stubPath) {
  const env = cleanedEnv(path.join(ws.base, "host-env-sandbox"));
  env.DXM_SMOKE_RETURN_MARKER = ws.returnMarker;

  return spawnSync(
    "pwsh",
    [
      "-NoProfile",
      "-NonInteractive",
      "-File",
      RUN_CI_TESTS,
      "-UnityVersion",
      "2021.3.45f1",
      "-TestMode",
      mode,
      "-AssemblyNames",
      "WallstopStudios.DxMessaging.Tests.Editor",
      "-ArtifactsPath",
      ws.artifacts,
      "-ProjectPath",
      ws.project,
      "-RepoRoot",
      ws.repoRoot,
      "-UnityEditorPath",
      editorPath
    ],
    { env, encoding: "utf8", maxBuffer: 32 * 1024 * 1024 }
  );
}

// Run run-ci-tests.ps1 in -TestMode standalone against the SPLIT-BUILD stubs. The
// standalone editor stub (launched by the watchdog via Process.Start) writes the
// player stub to $env:DXM_PLAYER_BUILD_PATH; run-ci-tests then launches that
// player stub directly. `playerMode` drives the player stub
// (pass/fail/no-results/sleep). `buildSleepSeconds` (> 0) forces the BUILD stub to
// hang so the build watchdog fires (and it does NOT write the player). The
// watchdog windows are pinned SHORT (2s) so a hang scenario resolves quickly.
function runStandaloneScript(
  ws,
  {
    playerMode = "pass",
    buildSleepSeconds = 0,
    configureMode = "pass",
    buildCrashAfterOutput = false
  } = {}
) {
  const env = cleanedEnv(path.join(ws.base, "host-env-sandbox"));
  env.DXM_SMOKE_RETURN_MARKER = ws.returnMarker;
  env.DXM_SMOKE_PLAYER_STUB_BODY = PLAYER_STUB_BODY;
  env.DXM_SMOKE_PLAYER_MODE = playerMode;
  env.DXM_SMOKE_CONFIGURE_MODE = configureMode;
  if (buildCrashAfterOutput) {
    env.DXM_SMOKE_BUILD_CRASH_AFTER_OUTPUT = "1";
  }
  if (buildSleepSeconds > 0) {
    env.DXM_SMOKE_BUILD_SLEEP_SECONDS = String(buildSleepSeconds);
  }
  // Short, deterministic watchdog windows so the WATCHDOG scenarios tree-kill
  // quickly instead of waiting the production 30/45 minutes.
  env.DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS = "2";
  env.DXM_STANDALONE_BUILD_TIMEOUT_SECONDS = "2";
  // The standalone editor stub is a shebang file launched via Process.Start; on
  // non-Windows that is fine. (These scenarios are win32-skipped at the call site.)

  return spawnSync(
    "pwsh",
    [
      "-NoProfile",
      "-NonInteractive",
      "-File",
      RUN_CI_TESTS,
      "-UnityVersion",
      "2021.3.45f1",
      "-TestMode",
      "standalone",
      "-AssemblyNames",
      "WallstopStudios.DxMessaging.Tests.Runtime",
      "-ArtifactsPath",
      ws.artifacts,
      "-ProjectPath",
      ws.project,
      "-RepoRoot",
      ws.repoRoot,
      "-UnityEditorPath",
      ws.standaloneEditorStubPath
    ],
    { env, encoding: "utf8", maxBuffer: 32 * 1024 * 1024 }
  );
}

// Count the number of `-returnlicense` entries the stub recorded in the marker
// file (one line per return invocation). Zero when the file does not exist.
function countReturnEntries(ws) {
  if (!fs.existsSync(ws.returnMarker)) {
    return 0;
  }
  return fs
    .readFileSync(ws.returnMarker, "utf8")
    .split(/\r?\n/)
    .filter((line) => line.trim().length > 0).length;
}

const workspaces = [];

afterAll(() => {
  for (const ws of workspaces) {
    cleanupDir(ws.base);
  }
});

describe("run-ci-tests.ps1 StrictMode collection-safety smoke test", () => {
  // Always runs (even without pwsh): proves the script under test exists, so a
  // rename/move cannot silently turn this whole guard into a no-op.
  test("the script under test exists", () => {
    expect(fs.existsSync(RUN_CI_TESTS)).toBe(true);
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[strictmode-smoke] pwsh not found on PATH; skipping run assertions (CI runners have pwsh)."
    );
    test.skip.each(["editmode", "playmode"])(
      "%s: runs under StrictMode without the empty-collection .Count throw",
      () => {}
    );
    test.skip.each(["editmode", "playmode"])(
      "%s: fails LOUDLY when the editor exits 0 but produces no results.xml",
      () => {}
    );
    test.skip.each(["editmode", "playmode"])(
      "%s: editor writes passing results then crashes on shutdown -> honored, green + warning",
      () => {}
    );
    return;
  }

  // editmode/playmode share the single -runTests editor invocation (the generic
  // stubEditor() writes results.xml on the -runTests-without-quit branch). The
  // standalone split-build flow has its OWN describe block below. These still
  // exercise the `@()`-wrapped accelerator capture `.Count` read + `+` concat
  // under StrictMode.
  test.each(["editmode", "playmode"])(
    "%s: runs under StrictMode without the empty-collection .Count throw",
    (mode) => {
      const ws = makeWorkspace();
      workspaces.push(ws);

      const result = runScript(mode, ws);
      const combined = combinedText(result);

      // The exact StrictMode failure string must NOT appear on any stream.
      expect(combined).not.toContain("cannot be found on this object");
      // The script must reach a clean exit.
      expectPwshStatus(result, 0);
      // And it must have produced the NUnit results the stub wrote.
      expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(true);
    }
  );

  // Result-verification path: prove the runner fails LOUDLY when the editor
  // exits 0 but writes NO results.xml. This is the "Unity quietly produced
  // nothing" mode that the async-no-wait bug class could otherwise mask -- a
  // false-green pass is the worst outcome, so Test-NUnitResults MUST turn a
  // missing results file into a non-zero exit with a clear diagnostic. Data-
  // driven over the single-pass modes (editmode/playmode).
  test.each(["editmode", "playmode"])(
    "%s: fails LOUDLY when the editor exits 0 but produces no results.xml",
    (mode) => {
      const ws = makeWorkspace();
      workspaces.push(ws);

      const result = runScript(mode, ws, ws.noResultsStubPath);
      const combined = combinedText(result);

      // Must NOT silently succeed: a missing results.xml is a hard failure.
      expectPwshFailure(result);
      // The runner must surface a results-missing diagnostic (Test-NUnitResults
      // emits both the "::error::No NUnit results XML" annotation and the thrown
      // "did not produce NUnit results" message).
      expect(combined).toMatch(/did not produce NUnit results|No NUnit results XML/);
      // And it must NOT have fabricated a results.xml.
      expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(false);
    }
  );

  // Artifact-is-source-of-truth for editmode/playmode: when the editor writes a
  // PASSING results.xml and THEN exits non-zero (Unity crashing in a background
  // thread during shutdown -- the DirectoryMonitor 0xC0000005 class), the runner
  // must HONOR the file and pass (with a benign-crash warning), never turn a
  // passing run red on the shutdown exit code. Data-driven over editmode/playmode.
  test.each(["editmode", "playmode"])(
    "%s: editor writes passing results then crashes on shutdown -> honored, green + warning",
    (mode) => {
      const ws = makeWorkspace();
      workspaces.push(ws);

      const result = runScript(mode, ws, ws.passingThenCrashStubPath);
      const combined = combinedText(result);

      expect(combined).not.toContain("cannot be found on this object");
      // GREEN despite the non-zero editor exit, because the passing file exists.
      expectPwshStatus(result, 0);
      expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(true);
      // The shutdown crash was surfaced as a non-fatal warning.
      expect(combined).toMatch(/benign post-work shutdown crash/i);
    }
  );

  // ---------------------------------------------------------------------------
  // REGRESSION GUARD for the production bug: run-ci-tests.ps1 used to pass BOTH
  // `-quit` and `-runTests` to Unity.exe, which per the manual causes the editor
  // to exit immediately with NO results.xml -- silently breaking every Unity
  // 2021.3/2022.3 cell. The shared stubEditor() above now models the real Unity
  // behavior (exit 0 / no results on the combo); this test instruments the args
  // the script actually passed by logging them to a file and asserting that the
  // test-launch invocation NEVER mixes -quit with -runTests. The data-driven
  // contract test in unity-runner-script-contract.test.js pins the SOURCE shape;
  // this pins the RUNTIME shape (post-templating, post-StrictMode).
  // ---------------------------------------------------------------------------
  test("the test-launch invocation does NOT pass -quit alongside -runTests", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    // Build a custom stub that LOGS the full arg vector for any -runTests call,
    // then behaves like the passing stub (writes results.xml) so the run
    // reaches a clean exit and we can inspect the recorded args.
    const argsLogPath = path.join(ws.base, "runtests-args.log");
    const loggingRunTests = [
      "  $logPath = $env:DXM_SMOKE_RUNTESTS_ARGS_LOG",
      "  if ($logPath) {",
      "    $d = Split-Path -Parent $logPath",
      "    if ($d -and -not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
      "    Add-Content -LiteralPath $logPath -Value (($Rest -join '|'))",
      "  }",
      "  $i = [array]::IndexOf($Rest, '-testResults')",
      "  if ($i -ge 0 -and ($i + 1) -lt $Rest.Count) {",
      "    $out = $Rest[$i + 1]; $d = Split-Path -Parent $out",
      "    if ($d) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
      '    \'<?xml version="1.0" encoding="utf-8"?><test-run total="1" passed="1" failed="0" skipped="0" result="Passed"></test-run>\' | Set-Content -LiteralPath $out -Encoding UTF8',
      "  }",
      "  exit 0"
    ].join("\n");
    const loggingStubPath = path.join(ws.base, "stub-editor-logging.ps1");
    fs.writeFileSync(loggingStubPath, stubEditor(loggingRunTests), "utf8");

    // Run with the logging stub. We must pre-set DXM_SMOKE_RUNTESTS_ARGS_LOG so
    // the stub sees it; runScript merges DXM_SMOKE_RETURN_MARKER itself.
    const env = cleanedEnv(path.join(ws.base, "host-env-sandbox"));
    env.DXM_SMOKE_RETURN_MARKER = ws.returnMarker;
    env.DXM_SMOKE_RUNTESTS_ARGS_LOG = argsLogPath;
    const result = spawnSync(
      "pwsh",
      [
        "-NoProfile",
        "-NonInteractive",
        "-File",
        RUN_CI_TESTS,
        "-UnityVersion",
        "2021.3.45f1",
        "-TestMode",
        "editmode",
        "-AssemblyNames",
        "WallstopStudios.DxMessaging.Tests.Editor",
        "-ArtifactsPath",
        ws.artifacts,
        "-ProjectPath",
        ws.project,
        "-RepoRoot",
        ws.repoRoot,
        "-UnityEditorPath",
        loggingStubPath
      ],
      { env, encoding: "utf8", maxBuffer: 32 * 1024 * 1024 }
    );
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    expectPwshStatus(result, 0);
    expect(fs.existsSync(argsLogPath)).toBe(true);

    // Each line of the log is one -runTests invocation's pipe-joined args. The
    // PRODUCTION GUARD: every such invocation must contain -runTests and must
    // NOT contain -quit. (Multiple lines are possible if the standalone code
    // path were ever exercised; editmode produces a single test-launch.)
    const lines = fs
      .readFileSync(argsLogPath, "utf8")
      .split(/\r?\n/)
      .filter((l) => l.trim().length > 0);
    expect(lines.length).toBeGreaterThanOrEqual(1);
    for (const line of lines) {
      const args = line.split("|");
      expect(args).toContain("-runTests");
      // THE PRODUCTION GUARD: the editor would otherwise quit immediately, exit 0,
      // and write NO results.xml. See the stubEditor() comment + the docs link.
      expect(args).not.toContain("-quit");
    }
  });

  test("surfaces targeted diagnostics when the editor exits non-zero after CS8032", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const result = runScript("editmode", ws, ws.failingCs8032StubPath);
    const combined = combinedText(result);

    expectPwshFailure(result);
    expect(combined).toContain("Unity result failure diagnostics");
    expect(combined).toContain("Unity could not instantiate one or more DxMessaging");
    expect(combined).toContain(
      "Message fixture compile errors followed missing generated interfaces"
    );
    expect(fs.readFileSync(path.join(ws.artifacts, "unity.log"), "utf8")).toContain(
      "warning CS8032"
    );
  });
});

// ===========================================================================
// STANDALONE SPLIT-BUILD + FILE-BASED RESULTS behavioral smoke test.
//
// The standalone path: run-ci-tests (2a) BUILDS the player via the watchdog
// (editor stub writes a player stub to $env:DXM_PLAYER_BUILD_PATH, NO results.xml),
// (2b) RUNS that player stub directly via the watchdog (player writes NUnit XML to
// -dxmTestResults and exits 0/1/2), (2c) validates the FILE. Both launches go
// through Process.Start, so the shebang stubs are launchable on Linux/macOS but
// NOT on win32 (CreateProcess rejects a non-PE file) -- these BEHAVIORAL scenarios
// are win32-skipped (the editmode/playmode + license regressions keep Windows
// coverage). An always-on existence assertion keeps this from being a silent no-op.
// ===========================================================================
describe("run-ci-tests.ps1 standalone split-build file-based results", () => {
  test("the script under test exists", () => {
    expect(fs.existsSync(RUN_CI_TESTS)).toBe(true);
  });

  const STANDALONE_BEHAVIOR_ENABLED = PWSH_PRESENT && process.platform !== "win32";
  if (!STANDALONE_BEHAVIOR_ENABLED) {
    // eslint-disable-next-line no-console
    console.warn(
      `[standalone-smoke] skipping standalone behavioral scenarios (pwsh=${PWSH_PRESENT}, platform=${process.platform}); the player/editor stubs must be Process.Start-launchable, which requires a real PE on win32.`
    );
    test.skip("PASS: the editor builds the player, the player writes a passing file -> green", () => {});
    test.skip("FAIL: the player writes a failing file and exits 1 -> Test-NUnitResults throws", () => {});
    test.skip("NO-RESULTS: the player exits without writing -> the runner throws 'did not produce'", () => {});
    test.skip("PLAYER-WATCHDOG: a hung player is tree-killed and the run throws, no hang", () => {});
    test.skip("BUILD-WATCHDOG: a hung build is tree-killed and the run throws, no hang", () => {});
    test.skip("CONFIGURE-SHUTDOWN-CRASH: configure writes the marker then crashes -> honored, green + warning", () => {});
    test.skip("CONFIGURE-NO-MARKER: configure crashes WITHOUT writing the marker -> fails loudly", () => {});
    test.skip("BUILD-SHUTDOWN-CRASH: build produces a valid player then crashes -> honored, green + warning", () => {});
    test.skip("PLAYER-SHUTDOWN-CRASH: player writes passing results then exits non-zero (no timeout) -> honored, green + warning", () => {});
    return;
  }

  test("PASS: the editor builds the player, the player writes a passing file -> green", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const result = runStandaloneScript(ws, { playerMode: "pass" });
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    // The editor BUILD wrote the player stub to the stable project Build path.
    const builtExe = path.join(ws.project, "Build", "DxmTestPlayer", "DxmTestPlayer.exe");
    expect(fs.existsSync(builtExe)).toBe(true);
    // The PLAYER (not the editor build) wrote results.xml, and it parses green.
    expectPwshStatus(result, 0);
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(true);
    // The player log (small, uploaded) was captured under artifacts, not unity.log.
    expect(fs.existsSync(path.join(ws.artifacts, "player.log"))).toBe(true);
  });

  test("FAIL: the player writes a failing file and exits 1 -> Test-NUnitResults throws", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const result = runStandaloneScript(ws, { playerMode: "fail" });
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    // The failing file IS written (so the FILE is the source of truth), and
    // Test-NUnitResults turns failed>0 into a non-zero exit with "tests failed".
    expectPwshFailure(result);
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(true);
    expect(combined).toMatch(/tests failed/);
  });

  test("NO-RESULTS: the player exits without writing -> the runner throws 'did not produce'", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const result = runStandaloneScript(ws, { playerMode: "no-results" });
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    // The editor build DID produce the player exe (so the post-build assert
    // passes), but the player wrote no results, so Test-NUnitResults fails loudly.
    expectPwshFailure(result);
    expect(combined).toMatch(/did not produce NUnit results|No NUnit results XML/);
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(false);
  });

  test("PLAYER-WATCHDOG: a hung player is tree-killed and the run throws, no hang", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const startedAt = Date.now();
    // DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS=2; the player sleeps 30s. The watchdog
    // must tree-kill it well before that.
    const result = runStandaloneScript(ws, { playerMode: "sleep" });
    const elapsedMs = Date.now() - startedAt;
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    expectPwshFailure(result);
    // The run must NOT have waited the full 30s player sleep (proves the tree-kill
    // fired). A generous ceiling accounts for pwsh startup + the editor build stub.
    expect(elapsedMs).toBeLessThan(25000);
    expect(combined).toMatch(/player timed out|tree was killed/i);
    // No results were written by the killed player.
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(false);
  });

  test("DEFERRED-QUIT: player writes results then hangs -> watchdog kills it but the file is honored (green + warning)", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const startedAt = Date.now();
    // DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS=2; the player WRITES a passing results.xml
    // and THEN sleeps 30s (models Application.Quit deferred/ignored in -batchmode
    // IL2CPP). The watchdog tree-kills it, but the results file already exists, so the
    // harness must honor the file as the source of truth and PASS (with a warning),
    // never a spurious failure.
    const result = runStandaloneScript(ws, { playerMode: "write-then-hang" });
    const elapsedMs = Date.now() - startedAt;
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    // The tree-kill fired (did NOT wait the full 30s hang)...
    expect(elapsedMs).toBeLessThan(25000);
    // ...yet the run is GREEN because the player had already written a passing file.
    expectPwshStatus(result, 0);
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(true);
    // The deferred quit was surfaced as a non-fatal warning, NOT a failure.
    expect(combined).toMatch(/honoring that results file/i);
    expect(combined).not.toMatch(/did not produce NUnit results|No NUnit results XML/);
    // A watchdog timeout is NOT a native shutdown crash: the inline notice above is
    // the SINGLE warning for this case -- the FILE validator must not ALSO emit the
    // benign-shutdown-crash warning and mislabel the 124 timeout sentinel as a crash.
    expect(combined).not.toMatch(/benign post-work shutdown crash/i);
  });

  test("PLAYER-SHUTDOWN-CRASH: player writes passing results then exits non-zero (no timeout) -> honored, green + warning", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    // The player writes a passing results.xml and THEN exits non-zero WITHOUT
    // hanging (a background-thread shutdown crash on the player, not a watchdog
    // timeout). The player exit code is advisory: the FILE wins, so the run is
    // green with the benign-crash warning -- this exercises the
    // $playerExitForValidation = $playerResult.ExitCode (non-timeout) branch.
    const result = runStandaloneScript(ws, { playerMode: "write-then-crash" });
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    expectPwshStatus(result, 0);
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(true);
    // The non-zero player exit was honored as a benign shutdown crash (one warning),
    // NOT a failure and NOT a watchdog-timeout notice.
    expect(combined).toMatch(/benign post-work shutdown crash/i);
    expect(combined).not.toMatch(/tests failed|did not produce NUnit results/);
    expect(combined).not.toMatch(/exceeded the .* watchdog/i);
  });

  test("BUILD-WATCHDOG: a hung build is tree-killed and the run throws, no hang", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const startedAt = Date.now();
    // DXM_STANDALONE_BUILD_TIMEOUT_SECONDS=2; the BUILD stub sleeps 30s (and never
    // writes the player). The build watchdog must tree-kill it well before that.
    const result = runStandaloneScript(ws, { playerMode: "pass", buildSleepSeconds: 30 });
    const elapsedMs = Date.now() - startedAt;
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    expectPwshFailure(result);
    expect(elapsedMs).toBeLessThan(25000);
    expect(combined).toMatch(/build timed out|tree was killed/i);
    // The player was never built and never ran.
    const builtExe = path.join(ws.project, "Build", "DxmTestPlayer", "DxmTestPlayer.exe");
    expect(fs.existsSync(builtExe)).toBe(false);
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(false);
  });

  // ===========================================================================
  // THE REGRESSION for THIS investigation: the standalone CONFIGURE pass
  // (DxmCiTestConfigurator.Apply) completed and logged "Batchmode quit
  // successfully invoked", but Unity then crashed in its DirectoryMonitor
  // file-watcher thread DURING shutdown and returned 0xC0000005. The runner used
  // to treat that exit code as fatal and fail the whole job. Now the CONFIGURED
  // PROJECT (proven by the marker Apply writes as its final action) is the source
  // of truth: a crash AFTER the marker is honored (green + warning); a configure
  // that crashes WITHOUT writing the marker still fails loudly.
  // ===========================================================================
  test("CONFIGURE-SHUTDOWN-CRASH: configure writes the marker then crashes -> honored, green + warning", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    // Configure writes the marker and THEN exits non-zero; build + player succeed.
    const result = runStandaloneScript(ws, { playerMode: "pass", configureMode: "crash" });
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    // GREEN: the configure marker proves Apply ran to completion, so the benign
    // shutdown crash does not fail the job; the player then produced passing results.
    expectPwshStatus(result, 0);
    expect(fs.existsSync(path.join(ws.artifacts, "configure-complete.marker"))).toBe(true);
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(true);
    // The benign crash was surfaced as a non-fatal warning, NOT a failure.
    expect(combined).toMatch(/benign post-work shutdown crash/i);
    expect(combined).not.toMatch(/Configure standalone IL2CPP project failed/);
  });

  test("CONFIGURE-NO-MARKER: configure crashes WITHOUT writing the marker -> fails loudly", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    // Configure exits non-zero and withholds the marker (a real configure failure).
    const result = runStandaloneScript(ws, { playerMode: "pass", configureMode: "no-marker" });
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    // RED: no marker means Apply did not complete -> the run fails before building.
    expectPwshFailure(result);
    expect(fs.existsSync(path.join(ws.artifacts, "configure-complete.marker"))).toBe(false);
    expect(combined).toMatch(
      /configure marker was not written|Configure standalone IL2CPP project failed/
    );
    // It must NOT have proceeded to build/run a player.
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(false);
  });

  test("BUILD-SHUTDOWN-CRASH: build produces a valid player then crashes -> honored, green + warning", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    // The build writes a complete, fresh player exe and THEN exits non-zero (a
    // background-thread shutdown crash after the build finished). The built exe is
    // the source of truth, so the run proceeds and the player produces passing results.
    const result = runStandaloneScript(ws, { playerMode: "pass", buildCrashAfterOutput: true });
    const combined = combinedText(result);

    expect(combined).not.toContain("cannot be found on this object");
    expectPwshStatus(result, 0);
    const builtExe = path.join(ws.project, "Build", "DxmTestPlayer", "DxmTestPlayer.exe");
    expect(fs.existsSync(builtExe)).toBe(true);
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(true);
    expect(combined).toMatch(/benign post-work shutdown crash/i);
    expect(combined).not.toMatch(/Editor build produced invalid/);
  });
});

// ===========================================================================
// LEAK-REGRESSION: the serial-activated seat MUST be returned on EVERY exit
// path -- including a Unity run that FAILS after the seat was activated. This is
// the live companion to unity-license-leak-safety.test.js's static finally-
// return guard. With the serial creds set, run-ci-tests.ps1 returns the seat at
// START (before the try, reclaiming any prior leak) AND in the finally. We drive
// both a FAILING and a PASSING editor stub and assert the seat is returned (the
// marker file the stub appends to on `-returnlicense`) in BOTH cases, and that
// the return-at-start produced its own entry.
// ===========================================================================
describe("run-ci-tests.ps1 serial seat is always returned (leak regression)", () => {
  // Always-on sanity (even without pwsh): proves the script under test exists,
  // so a rename/move cannot silently turn this guard into a no-op.
  test("the script under test exists", () => {
    expect(fs.existsSync(RUN_CI_TESTS)).toBe(true);
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[leak-regression] pwsh not found on PATH; skipping run assertions (CI runners have pwsh)."
    );
    test.skip("returns the seat even when the editor FAILS (the leak regression)", () => {});
    test.skip("returns the seat on the SUCCESS path too", () => {});
    return;
  }

  test("returns the seat even when the editor FAILS (the leak regression)", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const result = runScript("editmode", ws, ws.failingStubPath);
    const combined = combinedText(result);

    // (1) The run FAILS loudly: a failing editor must propagate a non-zero exit
    // (the finally returns the seat, then the failure re-throws). And StrictMode
    // must not have tripped.
    expectPwshFailure(result);
    expect(combined).not.toContain("cannot be found on this object");

    // (2) THE leak regression: the seat was returned even though the editor
    // failed. The stub appends to the marker on each -returnlicense, and with a
    // failing run BOTH the return-at-start (before the try) AND the finally-return
    // fire, so there must be at least TWO entries -- proving return-on-failure
    // under StrictMode and the return-at-start backstop.
    expect(fs.existsSync(ws.returnMarker)).toBe(true);
    const entries = countReturnEntries(ws);
    // Return-at-start (1) + finally-return after the failure (1) = 2.
    expect(entries).toBeGreaterThanOrEqual(2);
  });

  test("returns the seat on the SUCCESS path too", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const result = runScript("editmode", ws, ws.stubPath);
    const combined = combinedText(result);

    // The passing editor stub wrote results.xml, so the run succeeds...
    expect(combined).not.toContain("cannot be found on this object");
    expectPwshStatus(result, 0);
    expect(fs.existsSync(path.join(ws.artifacts, "results.xml"))).toBe(true);
    // ...and the seat is STILL returned (return-at-start + the finally on the
    // clean path), so at least two return entries are recorded.
    expect(fs.existsSync(ws.returnMarker)).toBe(true);
    expect(countReturnEntries(ws)).toBeGreaterThanOrEqual(2);
  });
});
