/**
 * @fileoverview Behavioral + unit tests for the HEARTBEAT-STALL detector added
 * to scripts/unity/ensure-editor.ps1's Invoke-UnityCliCaptureWithTimeout.
 *
 * WHY THIS EXISTS: the Unity 6.3 (6000.3.16f1) install hung in production by
 * streaming ~4,672 byte-identical
 * `{"type":"progress","pct":50,"msg":"Installing Unity (6000.3.16f1)...","phase":"install"}`
 * lines over 20 minutes with NO (pct, phase, msg) triple advance, then the
 * GitHub job was cancelled by the outer wall. The 45-minute total wall-clock
 * timeout (Get-EnsureEditorInstallTimeoutSeconds, sentinel exit 124) is the
 * fallback; the surgical fix is a HEARTBEAT-STALL DETECTOR in the poll loop
 * that tree-kills when the last observed triple has been unchanged for
 * >= DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS (profile-aware default: 900s for
 * EditorOnly, 1800s for the heavier install profiles), and surfaces a
 * distinct sentinel exit (125) so callers + tests can attribute "killed for
 * stall" separately from "elapsed the total wall-clock budget."
 *
 * The detector is paired with a periodic ::notice:: every 60s (gated on
 * wall-clock time, NOT per line) so the live CI log is human-readable mid-
 * flight (the alternative was a 4,672-line dupe wall). Tests pin both.
 *
 * Strategy mirrors unity-ensure-editor-install-resilience.test.js: AST-extract
 * the helper into a throwaway temp .ps1 for unit coverage; for behavioral
 * coverage, run the WHOLE ensure-editor.ps1 against a fake `unity` CLI shim
 * built under os.tmpdir with a hermetic spawn env (sandboxHostFolderEnv). All
 * phrase assertions go through normalizePwshText (via combinedText) so they
 * are immune to the ConciseView word-wrap on the narrower Windows runner --
 * enforced repo-wide by pwsh-output-assertion-policy.test.js.
 *
 * pwsh is preinstalled on GitHub's runners; when it is absent locally the
 * behavioral assertions are skipped, but always-on sanity assertions still run
 * so a zero-coverage regression cannot hide.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const { prependPathEnv, sandboxHostFolderEnv } = require("../lib/spawn-env-sandbox");
const {
  assertSpawnNonZeroStatus,
  assertSpawnStatus,
  combinedText,
  normalizePwshText
} = require("../lib/pwsh-output");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const ENSURE_EDITOR = path.join(REPO_ROOT, "scripts", "unity", "ensure-editor.ps1");

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
    cleanupDir(ws);
  }
});

// AST-extract one or more named functions from ensure-editor.ps1 and Invoke-
// Expression their bodies, so an extracted function can be called in isolation
// without the script's top-level side effects. (Mirrors the helper in
// unity-ensure-editor-install-resilience.test.js.)
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

function runPwshScript(scriptText, env = process.env) {
  const base = makeTempDir("progress-stall-harness");
  workspaces.push(base);
  const harnessPath = path.join(base, "harness.ps1");
  fs.writeFileSync(harnessPath, scriptText, "utf8");
  return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harnessPath], {
    env,
    encoding: "utf8",
    maxBuffer: 32 * 1024 * 1024
  });
}

// ---------------------------------------------------------------------------
// Fake `unity` CLI shim, parameterized by a body so the same builder can
// produce a stuck stream, an advancing stream, etc. (Same shim shape as
// unity-ensure-editor-install-resilience.test.js so the two suites share a
// mental model.)
// ---------------------------------------------------------------------------
function makeFakeUnityCli(binDir, bodyLines) {
  fs.mkdirSync(binDir, { recursive: true });
  const scriptPath = path.join(binDir, "fake-unity-cli.js");
  fs.writeFileSync(
    scriptPath,
    [
      "#!/usr/bin/env node",
      '"use strict";',
      "const fs = require('fs');",
      "const args = process.argv.slice(2);",
      "function write(line) { process.stdout.write(`${line}\\n`); }",
      "function exit(code) { process.exit(code); }",
      "if (args.includes('-version')) { write('Unity fake version'); exit(0); }",
      ...bodyLines,
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

// Run the WHOLE ensure-editor.ps1 against a fake CLI built from `bodyLines`,
// hermetically. `extraEnv` lets a test set DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS etc.
function runEnsureEditorWithFakeCli(bodyLines, installRoot, extraEnv = {}, extraArgs = []) {
  const base = makeTempDir("progress-stall-full");
  workspaces.push(base);
  const binDir = path.join(base, "bin");
  makeFakeUnityCli(binDir, bodyLines);

  const sandboxRoot = path.join(base, "host-env-sandbox");
  const env = prependPathEnv(sandboxHostFolderEnv(process.env, sandboxRoot), binDir);
  delete env.UNITY_EDITOR_INSTALL_ROOT;
  env.DXM_ENSURE_EDITOR_RETRY_DELAY_SECONDS = "0";
  env.DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE = "1";
  for (const [k, v] of Object.entries(extraEnv)) {
    env[k] = v;
  }

  return spawnSync(
    "pwsh",
    [
      "-NoProfile",
      "-NonInteractive",
      "-File",
      ENSURE_EDITOR,
      "-UnityVersion",
      "6000.3.16f1",
      "-InstallRoot",
      installRoot,
      "-CiManagedOnly",
      ...extraArgs
    ],
    { env, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 }
  );
}

describe("ensure-editor.ps1 heartbeat-stall detector + periodic notice", () => {
  // Always runs (even without pwsh): a rename/move cannot silently no-op the guard.
  test("the script under test exists", () => {
    expect(fs.existsSync(ENSURE_EDITOR)).toBe(true);
  });

  // Static corroboration (no pwsh needed): the heartbeat surface is present in
  // the script. Cheap and always-on so a refactor that drops a piece is caught
  // even where the behavioral run is skipped.
  test("heartbeat-stall surface (helper + sentinel 125 + env knob) exists in the script", () => {
    const text = fs.readFileSync(ENSURE_EDITOR, "utf8");
    expect(text).toContain("function Get-EnsureEditorProgressStallSeconds");
    expect(text).toContain("function Get-CliProgressTriple");
    expect(text).toContain("DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS");
    // The poll loop tracks the last triple + last advance, and uses sentinel 125
    // for the stall kill (distinct from the wall-clock sentinel 124).
    const timeoutStart = text.indexOf("function Invoke-UnityCliCaptureWithTimeout {");
    expect(timeoutStart).toBeGreaterThanOrEqual(0);
    const timeoutAfter = text.indexOf("\nfunction ", timeoutStart + 1);
    const timeoutBody = text.slice(timeoutStart, timeoutAfter);
    expect(timeoutBody).toContain("stallExitCode");
    expect(timeoutBody).toContain("125");
    expect(timeoutBody).toContain("Get-CliProgressTriple");
    expect(timeoutBody).toContain("HEARTBEAT STALLED");
    // Periodic notice cadence is wall-clock-gated (NOT per-line).
    expect(timeoutBody).toContain("Unity CLI install heartbeat:");
    // The stall kill reuses the existing tree-kill path (Kill($true)) so
    // descendants are reaped (the Unity installer spawns child processes).
    expect(timeoutBody).toContain("Kill($true)");
    // All install-retry classifiers route through the WRAPPER-DRIVEN kill
    // state (StallKilled / TimedOutWallClock) rather than the raw exit code,
    // so a NATIVE 124/125 from the Unity CLI is not misattributed as a kill.
    // Each site reads both fields off the result hashtable returned by
    // Invoke-UnityCliCaptureWithTimeout; at least three sites do so. The
    // wrapper itself ALSO publishes both fields on the return shape.
    const stallKilledMatches = (text.match(/\$\w+Result?\.StallKilled|StallKilled\s*=/g) || [])
      .length;
    expect(stallKilledMatches).toBeGreaterThanOrEqual(3);
    expect(text).toContain("TimedOutWallClock");
    expect(text).toContain("StallKilled       = [bool]$stalled");
    expect(text).toContain("TimedOutWallClock = [bool]($timedOut -and -not $stalled)");
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[progress-stall] pwsh not found on PATH; skipping behavioral probes (CI runners have pwsh)."
    );
    test.skip("a stuck (byte-identical) stream is killed and surfaces sentinel exit 125", () => {});
    test.skip("an advancing stream does NOT trip the stall detector", () => {});
    test.skip("DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS=0 opts out (no kill on the stuck stream)", () => {});
    test.skip("negative + non-integer values warn and fall back to default", () => {});
    test.skip("Get-EnsureEditorProgressStallSeconds honors the env override and the PROFILE-AWARE defaults", () => {});
    test.skip("the periodic ::notice:: is emitted at least once during a long advancing run", () => {});
    test.skip("Get-EnsureEditorProgressNoticeIntervalSeconds honors the env override and defaults/validates", () => {});
    test.skip("native exit 125 from the Unity CLI is NOT misattributed as a heartbeat stall", () => {});
    test.skip("the periodic notice's 'no progress line observed yet' branch fires when no triple has been seen", () => {});
    return;
  }

  // -------------------------------------------------------------------------
  // Pure unit coverage for the env-knob helper. Mirrors the matching test in
  // unity-ensure-editor-install-resilience.test.js for the wall-clock helper.
  // -------------------------------------------------------------------------
  test("Get-EnsureEditorProgressStallSeconds honors the env override and the PROFILE-AWARE defaults", () => {
    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        // The profile-aware default reads the script-scoped provisioning profile via
        // Get-UnityProvisioningProfile, so it must be extracted alongside the helper.
        extractEnsureEditorFunctions([
          "Get-EnsureEditorProgressStallSeconds",
          "Get-UnityProvisioningProfile"
        ]),
        "$env:DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS = $null",
        // Profile-aware default: EditorOnly -> 900s (15 min); the heavy install
        // profiles -> 1800s (30 min). Evidence: the EditorOnly install froze at the
        // monolithic 'Installing Unity...' triple for 600s on a HEALTHY run and was
        // falsely killed; the il2cpp/standalone profile unpacks more and freezes
        // longer, so a flat 600s was a false-positive threshold for both.
        "$script:UnityProvisioningProfile = 'EditorOnly'",
        "Write-Output ('DEFAULT_EDITORONLY=' + (Get-EnsureEditorProgressStallSeconds))",
        "$script:UnityProvisioningProfile = 'StandaloneWindowsIl2Cpp'",
        "Write-Output ('DEFAULT_IL2CPP=' + (Get-EnsureEditorProgressStallSeconds))",
        "$script:UnityProvisioningProfile = 'Full'",
        "Write-Output ('DEFAULT_FULL=' + (Get-EnsureEditorProgressStallSeconds))",
        // An explicitly-pinned -Default still wins over the profile-aware path.
        "Write-Output ('PINNED=' + (Get-EnsureEditorProgressStallSeconds -Default 42))",
        "$env:DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS = '5'",
        "Write-Output ('OVERRIDE=' + (Get-EnsureEditorProgressStallSeconds))",
        // Explicit opt-out (0 is allowed -> heartbeat disabled).
        "$env:DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS = '0'",
        "Write-Output ('OPTOUT=' + (Get-EnsureEditorProgressStallSeconds))",
        // Invalid -> warn + profile-aware default (il2cpp profile still set).
        "$env:DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS = 'nope'",
        "Write-Output ('INVALID=' + (Get-EnsureEditorProgressStallSeconds))",
        // Negative -> warn + profile-aware default.
        "$env:DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS = '-7'",
        "Write-Output ('NEGATIVE=' + (Get-EnsureEditorProgressStallSeconds))"
      ].join("\n")
    );
    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    const stdout = out.stdout || "";
    const combined = combinedText(out);
    // Profile-aware defaults documented in the script.
    expect(stdout).toContain("DEFAULT_EDITORONLY=900");
    expect(stdout).toContain("DEFAULT_IL2CPP=1800");
    expect(stdout).toContain("DEFAULT_FULL=1800");
    expect(stdout).toContain("PINNED=42");
    expect(stdout).toContain("OVERRIDE=5");
    expect(stdout).toContain("OPTOUT=0");
    // Invalid/negative fall back to the profile-aware default (Full profile -> 1800).
    expect(stdout).toContain("INVALID=1800");
    expect(stdout).toContain("NEGATIVE=1800");
    // Invalid + negative both emit ::warning::.
    expect(combined).toContain("::warning::");
    expect(combined).toContain("Ignoring invalid DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS");
  });

  // -------------------------------------------------------------------------
  // Pure unit coverage for the triple parser used by the detector. Validates
  // that real progress lines parse, malformed lines return $null, and the
  // empty/missing-field paths behave per the contract (used for stall key).
  // -------------------------------------------------------------------------
  test("Get-CliProgressTriple parses real progress lines and returns $null for non-progress", () => {
    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        extractEnsureEditorFunctions(["Get-CliProgressTriple"]),
        // The real Unity 6.3 hang shape.
        '$line = \'{"type":"progress","pct":50,"msg":"Installing Unity (6000.3.16f1)...","phase":"install"}\'',
        "$t = Get-CliProgressTriple -Line $line",
        "Write-Output ('PCT=' + $t.Pct + ' PHASE=' + $t.Phase + ' MSG=' + $t.Msg)",
        // Non-progress -> $null.
        "$null2 = Get-CliProgressTriple -Line 'not a json progress line'",
        "Write-Output ('NULL=' + ($null -eq $null2))",
        // Empty line -> $null.
        "$null3 = Get-CliProgressTriple -Line ''",
        "Write-Output ('EMPTY=' + ($null -eq $null3))"
      ].join("\n")
    );
    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    // Normalize via combinedText so a ConciseView word-wrap on a narrow CI
    // console cannot split the long PCT/PHASE/MSG line (enforced repo-wide by
    // pwsh-output-assertion-policy.test.js).
    const stdout = combinedText(out);
    expect(stdout).toContain("PCT=50 PHASE=install MSG=Installing Unity (6000.3.16f1)...");
    expect(stdout).toContain("NULL=True");
    expect(stdout).toContain("EMPTY=True");
  });

  // -------------------------------------------------------------------------
  // THE STALL DETECTOR FIRES: a fake CLI that streams 30 byte-identical
  // progress lines then sleeps forever is killed by the heartbeat detector
  // with sentinel exit 125. Pinned via the wrap-immune ::error:: that names
  // the stall and via the install-retry classifier reaching 125 -> retryable.
  // -------------------------------------------------------------------------
  test("a stuck (byte-identical) stream is killed and surfaces sentinel exit 125 (NOT 124)", () => {
    const base = makeTempDir("progress-stall-stuck");
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const pidFile = path.join(base, "cli.pid");

    // The fake CLI: on `install` it records its PID, emits 30 byte-identical
    // progress lines, then HANGS. The wall-clock timeout is large (so we know
    // the kill came from the STALL detector, not from the fallback wall).
    const bodyLines = [
      `const pidFile = ${JSON.stringify(pidFile)};`,
      `const installRoot = ${JSON.stringify(installRoot)};`,
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android'); exit(0); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [] })); exit(0); }",
      "if (args[0] === 'install') {",
      "  fs.writeFileSync(pidFile, String(process.pid));",
      "  for (let i = 0; i < 30; i++) {",
      '    write(\'{"type":"progress","pct":50,"msg":"Installing Unity (6000.3.16f1)...","phase":"install"}\');',
      "  }",
      "  setInterval(() => {}, 100000);",
      "  return;",
      "}"
    ];

    const startedAt = Date.now();
    const out = runEnsureEditorWithFakeCli(bodyLines, installRoot, {
      // 2s heartbeat stall window so the test fires fast. Wall-clock left
      // deliberately HIGH (60s) so a regression that disabled the heartbeat
      // would NOT silently pass via the wall-clock catching the hang.
      DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS: "2",
      DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS: "60"
    });
    const elapsedMs = Date.now() - startedAt;
    const combined = combinedText(out);

    // Sanity: the run failed (no usable editor) and finished WELL under the 60s
    // wall-clock, proving the STALL detector did the kill (the wall would have
    // taken at least 60s per attempt). Two install retries (default) * (2s
    // stall + small overhead) is comfortably under 30s.
    assertSpawnNonZeroStatus(out, expect.getState().currentTestName || "pwsh harness");
    expect(elapsedMs).toBeLessThan(45000);

    // The wrap-immune ::error:: from the poll loop names the stall, the env
    // knob, and the sentinel exit. The classifier downstream still produces the
    // standard module-install FAILED line; we assert BOTH so a regression in
    // either half is caught.
    expect(combined).toContain("HEARTBEAT STALLED after 2 second(s)");
    expect(combined).toContain("DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS");
    expect(combined).toContain("sentinel exit 125");
    expect(combined).toContain("Installing Unity (6000.3.16f1)...");
    // The wall-clock annotation must NOT have fired (would name 124, not 125).
    expect(combined).not.toContain("TIMED OUT after 60 second(s)");
    expect(combined).not.toContain("sentinel exit 124");

    // The stuck shim process must NOT survive the tree-kill.
    const sleepFor = (ms) => {
      const until = Date.now() + ms;
      while (Date.now() < until) {
        /* busy-wait, short, test-only */
      }
    };
    sleepFor(750);
    if (fs.existsSync(pidFile)) {
      const pid = Number(fs.readFileSync(pidFile, "utf8").trim());
      if (Number.isInteger(pid) && pid > 0) {
        let alive = false;
        try {
          process.kill(pid, 0);
          alive = true;
          try {
            process.kill(pid, "SIGKILL");
          } catch {
            /* ignore */
          }
        } catch {
          alive = false;
        }
        expect(alive).toBe(false);
      }
    }
  }, 120000);

  // -------------------------------------------------------------------------
  // ADVANCING STREAM is NOT killed: a fake CLI that emits DIFFERENT triples
  // every ~1s for 4 lines under a 10s stall window completes normally (the
  // installer's exit code drives the outcome, not the stall detector).
  // -------------------------------------------------------------------------
  test("an advancing (changing-triple) stream does NOT trip the stall detector", () => {
    const base = makeTempDir("progress-stall-advance");
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const editorRoot = path.join(installRoot, "6000.3.16f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");

    // The fake CLI: on `install`, emit 4 progress lines with ADVANCING pct
    // (each separated by ~1s, totalling under 10s -- well under the configured
    // stall window of 10s for any single triple), fabricate the editor, exit 0.
    // The advance keeps the heartbeat clock RESET on each new triple.
    const bodyLines = [
      "const path = require('path');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
      "const editorRoot = path.join(installRoot, '6000.3.16f1');",
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
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'NDK', 'toolchains', 'llvm', 'prebuilt', 'linux-x86_64', 'bin', 'clang++'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'AndroidPlayer', 'OpenJDK', 'bin', 'java'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_mono', 'LinuxPlayer'), '');",
      "  writeFile(path.join(editorRoot, 'Editor', 'Data', 'PlaybackEngines', 'LinuxStandaloneSupport', 'Variations', 'linux64_player_development_il2cpp', 'LinuxPlayer'), '');",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
      "if (args[0] === 'install-modules') { write('No modules found to install'); exit(6); }",
      "if (args[0] === 'install') {",
      "  let i = 0;",
      "  const advancingPercentages = [10, 30, 60, 90];",
      "  const tick = () => {",
      "    if (i < advancingPercentages.length) {",
      '      write(\'{"type":"progress","pct":\' + advancingPercentages[i] + \',"phase":"install","msg":"Installing Unity step \' + i + \'"}\');',
      "      i++;",
      "      setTimeout(tick, 1000);",
      "    } else {",
      "      createModules();",
      "      write('Editor installed successfully');",
      "      exit(0);",
      "    }",
      "  };",
      "  tick();",
      "  return;",
      "}",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.3.16f1', path: editorRoot }] })); exit(0); }"
    ];

    const startedAt = Date.now();
    const out = runEnsureEditorWithFakeCli(bodyLines, installRoot, {
      // 10s stall window. The 4 advances at ~1s each total <5s so the window
      // is never reached; the kill must NOT fire.
      DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS: "10",
      DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS: "60"
    });
    const elapsedMs = Date.now() - startedAt;
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    // No stall annotation fired (the triple advanced fast enough).
    expect(combined).not.toContain("HEARTBEAT STALLED");
    expect(combined).not.toContain("sentinel exit 125");
    // And it actually streamed the advancing lines.
    expect(combined).toContain("Installing Unity step 0");
    expect(combined).toContain("Installing Unity step 3");
    // Sanity: completed in reasonable time (~5s advance + overhead).
    expect(elapsedMs).toBeLessThan(45000);
  }, 90000);

  // -------------------------------------------------------------------------
  // OPT-OUT: DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS=0 skips the heartbeat
  // detector entirely; only the wall-clock fallback can kill. With a stuck
  // stream + tiny wall-clock, the wall sentinel (124) must fire, not 125.
  // -------------------------------------------------------------------------
  test("DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS=0 opts out (no heartbeat kill even on the stuck stream)", () => {
    const base = makeTempDir("progress-stall-optout");
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");

    const bodyLines = [
      `const installRoot = ${JSON.stringify(installRoot)};`,
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android'); exit(0); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [] })); exit(0); }",
      "if (args[0] === 'install') {",
      "  for (let i = 0; i < 10; i++) {",
      '    write(\'{"type":"progress","pct":50,"msg":"Installing Unity (6000.3.16f1)...","phase":"install"}\');',
      "  }",
      "  setInterval(() => {}, 100000);",
      "  return;",
      "}"
    ];

    const out = runEnsureEditorWithFakeCli(bodyLines, installRoot, {
      // Heartbeat OFF. Wall-clock 2s so the test still terminates fast via the
      // fallback. The kill MUST be attributed to the wall-clock (124), NOT the
      // heartbeat (125).
      DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS: "0",
      DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS: "2"
    });
    const combined = combinedText(out);

    assertSpawnNonZeroStatus(out, expect.getState().currentTestName || "pwsh harness");
    expect(combined).toContain("TIMED OUT after 2 second(s)");
    expect(combined).toContain("sentinel exit 124");
    // The heartbeat path must NOT have fired (opt-out).
    expect(combined).not.toContain("HEARTBEAT STALLED");
    expect(combined).not.toContain("sentinel exit 125");
  }, 90000);

  // -------------------------------------------------------------------------
  // PERIODIC ::notice:: is emitted at least once during a long advancing run.
  // The notice is wall-clock-gated (default 60s); we force the cadence by
  // running a stream long enough to cross the threshold under a small stall
  // window that the test ALSO drives the stall path -- in production the user
  // wants the diagnostic mid-flight even when nothing is wrong.
  //
  // Note: testing the 60s cadence directly would lengthen the suite too much.
  // Instead we set the stall window large so the stuck stream's kill happens
  // via wall-clock (not heartbeat), keep the wall-clock large enough that the
  // 60s notice cadence has a chance to fire, and assert at least one notice
  // appeared. This is mutation-test-stable: removing the notice emission turns
  // this assertion red without exposing the test to flake on a fast runner.
  // -------------------------------------------------------------------------
  test("the periodic ::notice:: heartbeat is emitted at least once during a long advancing run", () => {
    // The notice cadence in production is 60s; tests drive it to a few seconds
    // via DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS so the suite does
    // not have to wait a real minute to prove the notice fires. We pair a small
    // notice cadence with a SLIGHTLY LARGER stall window: a stuck stream is
    // killed by the stall path, but the cadence has fired at least once before
    // the kill, so the assertion is on PRESENCE (mutation-test-stable: removing
    // the notice emission turns this red without exposing it to flake on a
    // fast runner).
    const base = makeTempDir("progress-stall-notice");
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");

    const bodyLines = [
      `const installRoot = ${JSON.stringify(installRoot)};`,
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android'); exit(0); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [] })); exit(0); }",
      "if (args[0] === 'install') {",
      // Emit one distinct triple so the notice body has a real (pct, phase, msg)
      // to report, then hang so the notice cadence has time to fire before the
      // stall kill.
      '  write(\'{"type":"progress","pct":42,"msg":"Installing some module","phase":"install"}\');',
      "  setInterval(() => {}, 100000);",
      "  return;",
      "}"
    ];

    const out = runEnsureEditorWithFakeCli(bodyLines, installRoot, {
      // Notice cadence small (2s) so the cadence fires fast; stall window also
      // small (5s) so the stall kill ends the run fast. Total runtime ~ a few
      // seconds instead of the 70s the un-parameterized default would force.
      DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS: "2",
      DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS: "5",
      DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS: "60",
      // Skip the retry so a single attempt drives the proof.
      DXM_ENSURE_EDITOR_INSTALL_RETRY_ATTEMPTS: "1"
    });
    const combined = combinedText(out);

    assertSpawnNonZeroStatus(out, expect.getState().currentTestName || "pwsh harness");
    // The wrap-immune heartbeat ::notice:: must be present at least once with
    // the expected shape (the script emits "Unity CLI install heartbeat:" with
    // pct + phase + msg + elapsed + stallElapsed). Anchor on the prefix so any
    // value combination passes; presence is what proves the cadence fires.
    expect(combined).toContain("Unity CLI install heartbeat:");
    // The notice's body carries the elapsed + stallElapsed timing fields.
    expect(combined).toMatch(/Unity CLI install heartbeat:[\s\S]*?elapsed=\d+s/);
    expect(combined).toMatch(/Unity CLI install heartbeat:[\s\S]*?stallElapsed=\d+s/);
    // An advancing triple has been observed, so the notice's body should NAME
    // the triple's pct/phase/msg rather than the "no progress line observed
    // yet" fallback branch.
    expect(combined).toMatch(/Unity CLI install heartbeat:[\s\S]*?pct=42/);
    expect(combined).not.toContain("no progress line observed yet");
  }, 90000);

  // -------------------------------------------------------------------------
  // ITEM 4: env-knob coverage for the new periodic-notice interval helper.
  // Mirrors the matching test for Get-EnsureEditorProgressStallSeconds above
  // -- same four cases (default, explicit valid, explicit 0 opt-out, explicit
  // negative, explicit non-integer). The helper is the SOLE source of truth
  // for the cadence; if a refactor changes its name or breaks the env contract
  // this test goes red before the cadence test does.
  // -------------------------------------------------------------------------
  test("Get-EnsureEditorProgressNoticeIntervalSeconds honors the env override and defaults/validates", () => {
    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        extractEnsureEditorFunctions(["Get-EnsureEditorProgressNoticeIntervalSeconds"]),
        "$env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS = $null",
        "Write-Output ('DEFAULT=' + (Get-EnsureEditorProgressNoticeIntervalSeconds))",
        "$env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS = '7'",
        "Write-Output ('OVERRIDE=' + (Get-EnsureEditorProgressNoticeIntervalSeconds))",
        // Explicit opt-out (0 is allowed -> notice disabled).
        "$env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS = '0'",
        "Write-Output ('OPTOUT=' + (Get-EnsureEditorProgressNoticeIntervalSeconds))",
        // Invalid -> warn + default.
        "$env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS = 'nope'",
        "Write-Output ('INVALID=' + (Get-EnsureEditorProgressNoticeIntervalSeconds))",
        // Negative -> warn + default.
        "$env:DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS = '-3'",
        "Write-Output ('NEGATIVE=' + (Get-EnsureEditorProgressNoticeIntervalSeconds))"
      ].join("\n")
    );
    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    const stdout = out.stdout || "";
    const combined = combinedText(out);
    expect(stdout).toContain("DEFAULT=60");
    expect(stdout).toContain("OVERRIDE=7");
    expect(stdout).toContain("OPTOUT=0");
    expect(stdout).toContain("INVALID=60");
    expect(stdout).toContain("NEGATIVE=60");
    expect(combined).toContain("::warning::");
    expect(combined).toContain(
      "Ignoring invalid DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS"
    );
  });

  // -------------------------------------------------------------------------
  // ITEM 3: a NATIVE exit 125 from the Unity CLI (no kill from this wrapper)
  // MUST NOT be misattributed as a heartbeat-stall kill by the diagnostic
  // wording. The kill-state booleans on the result hashtable are the source
  // of truth, NOT the raw exit code (which a flaky CLI could coincidentally
  // emit). The wrapper's wrap-immune ::error:: ALSO must not fire on a
  // native non-killed exit (the annotation only fires when the wrapper
  // itself killed the process).
  // -------------------------------------------------------------------------
  test("native exit 125 from the Unity CLI is NOT misattributed as a heartbeat stall", () => {
    const base = makeTempDir("progress-stall-native125");
    workspaces.push(base);
    const binDir = path.join(base, "bin");
    // A fake CLI that exits NATIVELY with 125 immediately (no streaming, no
    // hang, no stall). The wrapper must return ExitCode=125 / StallKilled=$false
    // / TimedOutWallClock=$false, and the wrap-immune ::error:: line that names
    // "HEARTBEAT STALLED" must NOT fire.
    makeFakeUnityCli(binDir, [
      "// Native-125 fake: exits with raw 125 the instant it is invoked, with",
      "// no triple, no hang, no streaming. The wrapper's kill-state booleans",
      "// must both be FALSE because the wrapper itself never killed anything.",
      "if (args[0] === 'install-path') { exit(0); }",
      "exit(125);"
    ]);

    // Use the AST-extracted wrapper so we can read its return shape directly
    // (and so the test does not depend on the full ensure-editor.ps1
    // resolution pipeline succeeding).
    const cliPath =
      process.platform === "win32" ? path.join(binDir, "unity.cmd") : path.join(binDir, "unity");
    const cliPathEscaped = cliPath.replace(/'/g, "''");
    const harness = [
      "Set-StrictMode -Version Latest",
      "$ErrorActionPreference = 'Stop'",
      // Stub helpers the wrapper looks up via Get-Command so a stale shim is
      // not required. Default $StallSeconds large so the heartbeat detector
      // doesn't kill (it shouldn't have anything to kill -- the CLI exits
      // before the first poll tick).
      "function Register-UnityCliCommandAttempt { param([string[]]$Arguments) }",
      "function Get-CliProgressTriple { param([string]$Line) return $null }",
      "function Get-LastCliProgressMessage { param([string[]]$Output) return '(none)' }",
      "function Get-CollapsedCliOutputTail { param([string[]]$Output, [int]$MaxLines) return '' }",
      "function ConvertTo-ProcessArgumentLine { param([string[]]$Arguments) return ($Arguments -join ' ') }",
      "function Add-ProvisioningTimeoutEvent { param([string[]]$Arguments, [int]$TimeoutSeconds) }",
      `$script:UnityCliPath = '${cliPathEscaped}'`,
      extractEnsureEditorFunctions([
        "Get-EnsureEditorProgressStallSeconds",
        "Get-EnsureEditorProgressNoticeIntervalSeconds",
        "Invoke-UnityCliCaptureWithTimeout"
      ]),
      // StallSeconds explicit so a env-default of 0 cannot accidentally
      // re-enable the heartbeat detector on this fixture; the native exit
      // arrives before the first poll tick anyway.
      "$result = Invoke-UnityCliCaptureWithTimeout -Arguments @('install') -TimeoutSeconds 60 -StallSeconds 600",
      "Write-Output ('EXIT=' + $result.ExitCode)",
      "Write-Output ('STALL=' + $result.StallKilled)",
      "Write-Output ('WALL=' + $result.TimedOutWallClock)",
      "Write-Output ('SUCCESS=' + $result.Success)"
    ].join("\n");

    const out = runPwshScript(harness);
    const combined = combinedText(out);
    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    const stdout = out.stdout || "";
    expect(stdout).toContain("EXIT=125");
    expect(stdout).toContain("STALL=False");
    expect(stdout).toContain("WALL=False");
    expect(stdout).toContain("SUCCESS=False");
    // The wrapper's wrap-immune ::error:: must not have fired (it only fires
    // when the wrapper itself killed the process). NO "stall" wording.
    expect(combined).not.toContain("HEARTBEAT STALLED");
    expect(combined).not.toContain("TIMED OUT after");
    expect(combined).not.toContain("sentinel exit 125");
    expect(combined).not.toContain("sentinel exit 124");
  }, 60000);

  // -------------------------------------------------------------------------
  // ITEM 5: the periodic notice's "no progress line observed yet" branch fires
  // when the cadence elapses BEFORE any progress line has been observed. A
  // separate fixture (different from the advancing/stuck-triple tests above)
  // exercises this branch so a refactor that breaks the "no triple yet" path
  // cannot silently pass.
  // -------------------------------------------------------------------------
  test("the periodic notice's 'no progress line observed yet' branch fires when no triple has been seen", () => {
    const base = makeTempDir("progress-stall-no-triple");
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");

    // The fake CLI's `install` writes NOTHING then sleeps. The notice cadence
    // must cross (2s) BEFORE the stall kill (5s) and emit the no-triple
    // variant of the heartbeat line.
    const bodyLines = [
      `const installRoot = ${JSON.stringify(installRoot)};`,
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android'); exit(0); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [] })); exit(0); }",
      "if (args[0] === 'install') {",
      // Silence: no writes, just hang. The notice cadence ticks at 2s; with
      // no triple ever observed, the "no progress line observed yet" branch
      // must fire.
      "  setInterval(() => {}, 100000);",
      "  return;",
      "}"
    ];

    const out = runEnsureEditorWithFakeCli(bodyLines, installRoot, {
      DXM_ENSURE_EDITOR_PROGRESS_NOTICE_INTERVAL_SECONDS: "2",
      DXM_ENSURE_EDITOR_PROGRESS_STALL_SECONDS: "5",
      DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS: "60",
      DXM_ENSURE_EDITOR_INSTALL_RETRY_ATTEMPTS: "1"
    });
    const combined = combinedText(out);

    assertSpawnNonZeroStatus(out, expect.getState().currentTestName || "pwsh harness");
    // The exact phrase from the no-triple branch of the periodic notice.
    expect(combined).toContain("Unity CLI install heartbeat: no progress line observed yet");
    expect(combined).toMatch(/no progress line observed yet elapsed=\d+s/);
    expect(combined).toMatch(/no progress line observed yet[\s\S]*?stallElapsed=\d+s/);
  }, 90000);
});
