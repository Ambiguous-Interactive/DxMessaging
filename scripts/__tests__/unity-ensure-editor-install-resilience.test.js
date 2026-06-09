/**
 * @fileoverview Behavioral + unit tests for the module-install RESILIENCE and
 * DIAGNOSTICS added to scripts/unity/ensure-editor.ps1.
 *
 * WHY THIS EXISTS: on the self-hosted Windows Unity runners, the standalone
 * Unity CLI's Android NDK module install has two failure modes that the bootstrap
 * could not survive:
 *   (a) it sometimes FAILS after spamming thousands of IDENTICAL
 *       `{"type":"progress","pct":96,"msg":"Installing Android NDK..."}` lines and
 *       then exits 6 -- so the thrown "CLI output tail" was thousands of copies of
 *       one line (unreadable); and
 *   (b) it sometimes HANGS so long the GitHub job is CANCELLED ("The operation was
 *       canceled"), so the retry never fires and NO diagnostics are produced.
 *
 * The fix added three things, pinned here:
 *   1. A TOTAL WALL-CLOCK TIMEOUT on the captured CLI invocation
 *      (Invoke-UnityCliCaptureWithTimeout, which Invoke-UnityCliCapture delegates
 *      to): a hung install is tree-killed, classified as a (retryable) failure
 *      with a sentinel exit code (124), and annotated with a wrap-immune
 *      `::error::`. Configurable via DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS.
 *   2. A pure de-dup tail formatter (Get-CollapsedCliOutputTail) that collapses a
 *      run of N identical lines to one line with a "(xN)" suffix, so a failure
 *      tail is READABLE.
 *   3. A wrap-immune failure summary (Write-ModuleInstallFailureDiagnostics) that
 *      names the version, verb/args, outcome (exit code OR "timed out after Ns"),
 *      the last progress message, and the install-drive free space.
 *
 * Strategy mirrors unity-ensure-editor-il2cpp-idempotency.test.js: AST-extract a
 * named function from the script (via Parser::ParseFile) into a throwaway temp
 * .ps1 and invoke it (the script has top-level executable code, so dot-sourcing
 * the whole file would have side effects); and for the timeout/diagnostics
 * behavior, run the WHOLE ensure-editor.ps1 against a fake `unity` CLI shim built
 * under os.tmpdir with a hermetic spawn env (sandboxHostFolderEnv). All phrase
 * assertions go through normalizePwshText (via combinedText) so they are immune to
 * the ConciseView word-wrap on the narrower Windows runner -- enforced repo-wide
 * by pwsh-output-assertion-policy.test.js.
 *
 * pwsh is preinstalled on GitHub's runners; when it is absent locally the
 * behavioral assertions are skipped, but always-on sanity assertions still run so
 * a zero-coverage regression cannot hide.
 *
 * @cross-platform-regression -- this marker requires the file to be gated on
 * ubuntu/windows/macos via the targeted step in
 * .github/workflows/cross-platform-preflight.yml; enforced by
 * scripts/__tests__/cross-platform-preflight-coverage.test.js. The timeout
 * process-tree-kill and the System.Diagnostics.Process drain are platform-
 * divergent, so first-failure attribution on every OS is warranted.
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

afterAll(() => {
  for (const ws of workspaces) {
    cleanupDir(ws);
  }
});

// AST-extract one or more named functions from ensure-editor.ps1 and Invoke-
// Expression their bodies, so an extracted function can be called in isolation
// without the script's top-level side effects.
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
  const base = makeTempDir("ensure-resilience-harness");
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
// Fake `unity` CLI shim, parameterized by a node "mode" so the SAME builder can
// produce a fast-success CLI, a slow/hanging CLI, or a failing-with-progress CLI.
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
// hermetically (host-folder vars sandboxed; node stub-startup probe skipped).
// `extraEnv` lets a test set DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS etc.
function runEnsureEditorWithFakeCli(bodyLines, installRoot, extraEnv = {}, extraArgs = []) {
  const base = makeTempDir("ensure-resilience-full");
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
      "6000.0.32f1",
      "-InstallRoot",
      installRoot,
      "-CiManagedOnly",
      ...extraArgs
    ],
    { env, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 }
  );
}

describe("ensure-editor.ps1 module-install resilience + diagnostics", () => {
  // Always runs (even without pwsh): a rename/move cannot silently no-op the guard.
  test("the script under test exists", () => {
    expect(fs.existsSync(ENSURE_EDITOR)).toBe(true);
  });

  // Static corroboration (no pwsh needed): the resilience surface is present in
  // the script. Cheap and always-on so a refactor that drops a piece is caught
  // even where the behavioral run is skipped.
  test("the resilience+diagnostics functions and the env knob exist in the script", () => {
    const text = fs.readFileSync(ENSURE_EDITOR, "utf8");
    expect(text).toContain("function Invoke-UnityCliCaptureWithTimeout");
    expect(text).toContain("function Get-EnsureEditorInstallTimeoutSeconds");
    expect(text).toContain("function Get-CollapsedCliOutputTail");
    expect(text).toContain("function Get-LastCliProgressMessage");
    expect(text).toContain("function Write-ModuleInstallFailureDiagnostics");
    expect(text).toContain("DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS");
    // The capturing invoker must DELEGATE to the timeout runner so the module-
    // install paths are bounded without changing the call sites (the AST contract
    // still routes -Arguments through Get-UnityCliModuleInstallArguments).
    const captureStart = text.indexOf("function Invoke-UnityCliCapture {");
    expect(captureStart).toBeGreaterThanOrEqual(0);
    const after = text.indexOf("\nfunction ", captureStart + 1);
    const body = after === -1 ? text.slice(captureStart) : text.slice(captureStart, after);
    expect(body).toContain("Invoke-UnityCliCaptureWithTimeout");
    // The timeout runner must tree-kill (the bool overload) and use the sentinel.
    const timeoutStart = text.indexOf("function Invoke-UnityCliCaptureWithTimeout {");
    const timeoutAfter = text.indexOf("\nfunction ", timeoutStart + 1);
    const timeoutBody = text.slice(timeoutStart, timeoutAfter);
    expect(timeoutBody).toContain("Kill($true)");
    expect(timeoutBody).toContain("124");
    expect(timeoutBody).toContain("TimeoutKnob");
    expect(timeoutBody).toContain("ConvertTo-ProcessArgumentLine");
    expect(timeoutBody).not.toContain(".ArgumentList");
    expect(timeoutBody).not.toMatch(/WaitForExit\(\)/);
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[install-resilience] pwsh not found on PATH; skipping behavioral probes (CI runners have pwsh)."
    );
    // Count-parity placeholders for the data-driven de-dup table below so the
    // suite shape is stable whether or not pwsh is present.
    test.skip.each([
      ["long run collapses to (xN)"],
      ["mixed runs preserved"],
      ["no repeated lines pass through unchanged"],
      ["cap is applied AFTER collapsing"],
      ["empty input yields the placeholder"],
      ["whitespace-only input yields the placeholder (not an empty string)"],
      ["a lone null element yields the placeholder"]
    ])("Get-CollapsedCliOutputTail: %s", () => {});
    return;
  }

  // --- #2 DE-DUP TAIL: unit-test the pure Get-CollapsedCliOutputTail helper. ---
  // Data-driven: each row feeds an input line array + MaxLines and asserts the
  // exact collapsed, capped, newline-joined result. The helper is AST-extracted
  // and invoked directly (it depends only on built-ins).
  describe("#2 de-dup tail (Get-CollapsedCliOutputTail, data-driven)", () => {
    // Build a tiny harness that extracts the helper, runs it on a JSON-encoded
    // input array, and prints the raw result bracketed so the test can read it
    // off the raw stdout (structure-preserving; not a phrase assertion).
    function collapseTail(inputLines, maxLines) {
      const inputJson = JSON.stringify(inputLines).replace(/'/g, "''");
      const out = runPwshScript(
        [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          extractEnsureEditorFunctions(["Get-CollapsedCliOutputTail"]),
          `$inputLines = @('${inputJson}' | ConvertFrom-Json)`,
          `$result = Get-CollapsedCliOutputTail -Output $inputLines -MaxLines ${maxLines}`,
          // Bracket the result so trailing/leading whitespace is unambiguous.
          "Write-Output ('<<<' + $result + '>>>')"
        ].join("\n")
      );
      expectPwshStatus(out, 0);
      const m = /<<<([\s\S]*)>>>/.exec(out.stdout || "");
      expect(m).not.toBeNull();
      return m[1];
    }

    test.each([
      {
        name: "long run collapses to (xN)",
        // 3847 identical NDK progress lines bracketed by distinct lines.
        input: ["START"]
          .concat(
            Array.from(
              { length: 3847 },
              () => '{"type":"progress","pct":96,"msg":"Installing Android NDK..."}'
            )
          )
          .concat(["INSTALL_FAILED"]),
        maxLines: 40,
        expected: [
          "START",
          '{"type":"progress","pct":96,"msg":"Installing Android NDK..."}  (x3847)',
          "INSTALL_FAILED"
        ].join("\n")
      },
      {
        name: "mixed runs preserved",
        input: ["x", "x", "y", "z", "z", "z"],
        maxLines: 40,
        expected: ["x  (x2)", "y", "z  (x3)"].join("\n")
      },
      {
        name: "no repeated lines pass through unchanged",
        input: ["one", "two", "three"],
        maxLines: 40,
        expected: ["one", "two", "three"].join("\n")
      },
      {
        name: "cap is applied AFTER collapsing (counts distinct runs)",
        // 'a','a' collapses to one entry; with MaxLines 2 we keep the LAST two
        // collapsed entries: 'd','e' (NOT raw duplicates of 'a').
        input: ["a", "a", "b", "c", "d", "e"],
        maxLines: 2,
        expected: ["d", "e"].join("\n")
      },
      {
        name: "empty input yields the placeholder",
        input: [],
        maxLines: 10,
        expected: "(no output captured)"
      },
      {
        // REGRESSION (MINOR-3): @($Output) on all-blank lines is NOT count-0
        // (each blank is a 1-element string), so a naive `.Count -eq 0` check let
        // whitespace-only input slip through and return '' instead of the
        // documented placeholder. The content probe now returns the placeholder.
        name: "whitespace-only input yields the placeholder (not an empty string)",
        input: ["   ", "\t", ""],
        maxLines: 10,
        expected: "(no output captured)"
      },
      {
        // REGRESSION (MINOR-3): @($null) is a ONE-element array whose element is
        // $null (NOT count-0), which previously returned '' rather than the
        // placeholder. JSON `null` round-trips through ConvertFrom-Json to $null,
        // exercising exactly that 1-element-$null path.
        name: "a lone null element yields the placeholder",
        input: [null],
        maxLines: 10,
        expected: "(no output captured)"
      }
    ])("Get-CollapsedCliOutputTail: $name", ({ input, maxLines, expected }) => {
      expect(collapseTail(input, maxLines)).toBe(expected);
    });

    test("a single identical pair collapses to (x2) but a lone line gets no suffix", () => {
      expect(collapseTail(["dup", "dup"], 40)).toBe("dup  (x2)");
      expect(collapseTail(["solo"], 40)).toBe("solo");
    });
  });

  // --- #1 TIMEOUT FIRES: a fake CLI that SLEEPS far longer than a tiny timeout
  //     is tree-killed; the run surfaces the wrap-immune ::error:: timeout
  //     annotation; the shim process does not survive. ---
  describe("#1 install timeout", () => {
    test("a hung module install is killed and surfaces the wrap-immune ::error:: timeout annotation", () => {
      const base = makeTempDir("ensure-resilience-timeout");
      workspaces.push(base);
      const installRoot = path.join(base, "configured-root");
      const pidFile = path.join(base, "cli.pid");
      const childPidFile = path.join(base, "cli-child.pid");

      // The fake CLI: on the module install it records its PID + a spawned child
      // PID, emits two progress lines, then HANGS (timers keep both alive). A
      // child is spawned so the test also proves TREE-kill, not just a parent
      // kill. Everything else (install-path getter, editors list) responds fast.
      const bodyLines = [
        `const pidFile = ${JSON.stringify(pidFile)};`,
        `const childPidFile = ${JSON.stringify(childPidFile)};`,
        `const installRoot = ${JSON.stringify(installRoot)};`,
        "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
        "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
        "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android'); exit(0); }",
        "if (args[0] === 'editors') { write(JSON.stringify({ editors: [] })); exit(0); }",
        // The base `install` (no editor on disk -> ensure-editor takes the install
        // path) is the one that hangs.
        "if (args[0] === 'install') {",
        "  fs.writeFileSync(pidFile, String(process.pid));",
        "  const { spawn } = require('child_process');",
        "  const child = spawn(process.execPath, ['-e', 'setInterval(()=>{}, 100000)'], { stdio: 'ignore' });",
        "  fs.writeFileSync(childPidFile, String(child.pid));",
        '  write(\'{"type":"progress","pct":10,"msg":"Installing Android SDK..."}\');',
        '  write(\'{"type":"progress","pct":96,"msg":"Installing Android NDK..."}\');',
        "  setInterval(() => {}, 100000);",
        "  return;",
        "}"
      ];

      const startedAt = Date.now();
      const out = runEnsureEditorWithFakeCli(bodyLines, installRoot, {
        // Tiny timeout so the hang is killed in ~2s. Retry delay is already 0.
        DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS: "2"
      });
      const elapsedMs = Date.now() - startedAt;
      const combined = combinedText(out);

      // It must NOT have run anywhere near forever. Two retry attempts at a 2s
      // timeout each (+ overhead) is comfortably under 60s; a regression that
      // disables the timeout would blow past this. (Also bounded by maxBuffer/the
      // jest per-test timeout, but assert explicitly for a clear failure.)
      expect(elapsedMs).toBeLessThan(60000);

      // The run fails (the install never produced a usable editor)...
      expectPwshFailure(out);
      // ...and the wrap-immune ::error:: timeout annotation is present, naming the
      // timeout, the env knob, and the last progress message seen before the kill.
      expect(combined).toContain("TIMED OUT after 2 second(s)");
      expect(combined).toContain("process tree was killed");
      expect(combined).toContain("DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS");
      expect(combined).toContain("Installing Android NDK...");

      // The shim process (and its child) must NOT survive the tree-kill. Allow a
      // brief grace period for OS reaping, then assert both are gone.
      const sleepFor = (ms) => {
        const until = Date.now() + ms;
        // Busy-wait is fine here (short, test-only) and avoids a foreground sleep.
        while (Date.now() < until) {
          /* spin */
        }
      };
      sleepFor(750);
      const stillAlive = (pidPath) => {
        if (!fs.existsSync(pidPath)) {
          return false;
        }
        const pid = Number(fs.readFileSync(pidPath, "utf8").trim());
        if (!Number.isInteger(pid) || pid <= 0) {
          return false;
        }
        try {
          process.kill(pid, 0); // signal 0 = liveness probe
          // It is alive; clean it up so a leaked hang process does not linger.
          try {
            process.kill(pid, "SIGKILL");
          } catch {
            /* ignore */
          }
          return true;
        } catch {
          return false; // ESRCH -> already dead
        }
      };
      expect(stillAlive(pidFile)).toBe(false);
      expect(stillAlive(childPidFile)).toBe(false);
    }, 90000);

    test("Get-EnsureEditorInstallTimeoutSeconds honors the env override and defaults/validates", () => {
      const out = runPwshScript(
        [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          extractEnsureEditorFunctions(["Get-EnsureEditorInstallTimeoutSeconds"]),
          // Default (unset).
          "$env:DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS = $null",
          "Write-Output ('DEFAULT=' + (Get-EnsureEditorInstallTimeoutSeconds))",
          // Valid override.
          "$env:DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS = '5'",
          "Write-Output ('OVERRIDE=' + (Get-EnsureEditorInstallTimeoutSeconds))",
          // Explicit opt-out (0 is allowed -> unbounded).
          "$env:DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS = '0'",
          "Write-Output ('OPTOUT=' + (Get-EnsureEditorInstallTimeoutSeconds))",
          // Invalid -> warn + default.
          "$env:DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS = 'nope'",
          "Write-Output ('INVALID=' + (Get-EnsureEditorInstallTimeoutSeconds))",
          // Negative -> warn + default.
          "$env:DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS = '-7'",
          "Write-Output ('NEGATIVE=' + (Get-EnsureEditorInstallTimeoutSeconds))"
        ].join("\n")
      );
      expectPwshStatus(out, 0);
      const stdout = out.stdout || "";
      // Default rationale documented in the script: 2700s (45 min).
      expect(stdout).toContain("DEFAULT=2700");
      expect(stdout).toContain("OVERRIDE=5");
      expect(stdout).toContain("OPTOUT=0");
      expect(stdout).toContain("INVALID=2700");
      expect(stdout).toContain("NEGATIVE=2700");
    });

    // --- #4 (optional retry tuning): the install-retry-attempts knob exists,
    //     defaults to 2 (unchanged), and is override-aware/validating. ---
    test("Get-EnsureEditorInstallRetryAttempts defaults to 2 and honors a valid override", () => {
      const out = runPwshScript(
        [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          extractEnsureEditorFunctions(["Get-EnsureEditorInstallRetryAttempts"]),
          "$env:DXM_ENSURE_EDITOR_INSTALL_RETRY_ATTEMPTS = $null",
          "Write-Output ('DEFAULT=' + (Get-EnsureEditorInstallRetryAttempts))",
          "$env:DXM_ENSURE_EDITOR_INSTALL_RETRY_ATTEMPTS = '3'",
          "Write-Output ('OVERRIDE=' + (Get-EnsureEditorInstallRetryAttempts))",
          // Below 1 is invalid (Invoke-WithRetry clamps, but the knob rejects) -> default.
          "$env:DXM_ENSURE_EDITOR_INSTALL_RETRY_ATTEMPTS = '0'",
          "Write-Output ('ZERO=' + (Get-EnsureEditorInstallRetryAttempts))",
          "$env:DXM_ENSURE_EDITOR_INSTALL_RETRY_ATTEMPTS = 'nope'",
          "Write-Output ('INVALID=' + (Get-EnsureEditorInstallRetryAttempts))"
        ].join("\n")
      );
      expectPwshStatus(out, 0);
      const stdout = out.stdout || "";
      // The default is intentionally UNCHANGED at 2.
      expect(stdout).toContain("DEFAULT=2");
      expect(stdout).toContain("OVERRIDE=3");
      expect(stdout).toContain("ZERO=2");
      expect(stdout).toContain("INVALID=2");
    });

    test("whole-run provisioning budget clamps CLI timeouts and can fail early", () => {
      const out = runPwshScript(
        [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          extractEnsureEditorFunctions([
            "Write-CiNotice",
            "Get-EnsureEditorProvisioningBudgetSeconds",
            "Initialize-UnityProvisioningBudget",
            "Get-RemainingUnityProvisioningBudgetSeconds",
            "Get-EffectiveUnityCliTimeoutSeconds",
            "Assert-UnityProvisioningBudgetCanFit"
          ]),
          "$env:DXM_ENSURE_EDITOR_PROVISIONING_BUDGET_SECONDS = '5'",
          "Initialize-UnityProvisioningBudget",
          "$effective = Get-EffectiveUnityCliTimeoutSeconds -RequestedSeconds 120",
          "Write-Output ('EFFECTIVE=' + $effective)",
          "try { Assert-UnityProvisioningBudgetCanFit -Operation 'large repair' -MinimumSeconds 60; Write-Output 'EARLY=False' }",
          "catch { Write-Output ('EARLY=True; MSG=' + $_.Exception.Message) }",
          "$env:DXM_ENSURE_EDITOR_PROVISIONING_BUDGET_SECONDS = '0'",
          "Initialize-UnityProvisioningBudget",
          "Write-Output ('OPTOUT=' + (Get-EffectiveUnityCliTimeoutSeconds -RequestedSeconds 120))"
        ].join("\n")
      );
      const combined = combinedText(out);
      expectPwshStatus(out, 0);
      expect(combined).toMatch(/EFFECTIVE=[1-5]\b/);
      expect(combined).toContain("EARLY=True");
      expect(combined).toContain("cannot fit 'large repair'");
      expect(combined).toContain("OPTOUT=120");
    });

    // --- The DEDICATED Android-tier install-retry knob (parallels the base-install
    //     attempts test above): defaults to 3 (one more than the base default of 2),
    //     honors a valid override, and rejects a below-1/non-integer override with a
    //     ::warning::, falling back to the default. ---
    test("Get-EnsureEditorAndroidInstallRetryAttempts defaults to 3 and honors a valid override", () => {
      const out = runPwshScript(
        [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          extractEnsureEditorFunctions(["Get-EnsureEditorAndroidInstallRetryAttempts"]),
          "$env:DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS = $null",
          "Write-Output ('DEFAULT=' + (Get-EnsureEditorAndroidInstallRetryAttempts))",
          "$env:DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS = '5'",
          "Write-Output ('OVERRIDE=' + (Get-EnsureEditorAndroidInstallRetryAttempts))",
          // Below 1 is invalid -> ::warning:: + fall back to the default (3).
          "$env:DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS = '0'",
          "Write-Output ('ZERO=' + (Get-EnsureEditorAndroidInstallRetryAttempts))",
          // Non-integer is invalid -> ::warning:: + fall back to the default (3).
          "$env:DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS = 'nope'",
          "Write-Output ('INVALID=' + (Get-EnsureEditorAndroidInstallRetryAttempts))"
        ].join("\n")
      );
      expectPwshStatus(out, 0);
      const stdout = out.stdout || "";
      const combined = combinedText(out);
      // The default is 3 (one more than the base-install default of 2).
      expect(stdout).toContain("DEFAULT=3");
      expect(stdout).toContain("OVERRIDE=5");
      // Invalid overrides fall back to the default AND emit a ::warning::.
      expect(stdout).toContain("ZERO=3");
      expect(stdout).toContain("INVALID=3");
      expect(combined).toContain("::warning::");
      expect(combined).toContain(
        "Ignoring invalid DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS"
      );
    });
  });

  // --- NORMAL COMPLETION IS UNAFFECTED: a fast fake CLI returns promptly with
  //     the correct exit code + full streamed output through the timeout runner. ---
  test("PowerShell 5.1-compatible process quoting preserves paths and args with spaces", () => {
    const base = makeTempDir("ensure args");
    workspaces.push(base);
    const cliScript = path.join(base, "fake unity cli.js");
    fs.writeFileSync(
      cliScript,
      [
        '"use strict";',
        'process.stdout.write(JSON.stringify(process.argv.slice(2)) + "\\n");',
        ""
      ].join("\n"),
      "utf8"
    );

    const cliScriptLiteral = cliScript.replace(/'/g, "''");
    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        extractEnsureEditorFunctions([
          "ConvertTo-ProcessArgumentLine",
          "Invoke-UnityCliCaptureWithTimeout",
          "Get-LastCliProgressMessage",
          // Required transitive deps for the heartbeat-stall detector + the
          // de-duplicating collapsed tail used in the wrap-immune ::error::.
          "Get-CliProgressTriple",
          "Get-EnsureEditorProgressStallSeconds",
          "Get-EnsureEditorProgressNoticeIntervalSeconds",
          "Get-CollapsedCliOutputTail"
        ]),
        "$script:UnityCliPath = 'node'",
        `$result = Invoke-UnityCliCaptureWithTimeout -Arguments @('${cliScriptLiteral}', 'arg with spaces', 'quote \" inside', 'backslash\\tail') -TimeoutSeconds 30`,
        'Write-Output ("EXIT=$($result.ExitCode)")',
        'Write-Output ("CAPTURE=" + ($result.Output | Select-Object -Last 1))'
      ].join("\n")
    );

    const combined = combinedText(out);
    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(out.stdout).toContain("EXIT=0");
    const capture = /^CAPTURE=(.*)$/m.exec(out.stdout || "");
    expect(capture).not.toBeNull();
    expect(JSON.parse(capture[1])).toEqual([
      "arg with spaces",
      'quote " inside',
      "backslash\\tail"
    ]);
  });

  // REGRESSION (Unity 6000.3.16f1 standalone, run 26701943540): the quarantine
  // could not proceed because the stale-process sweep "matched 0" while a handle was
  // held on ...\6000.3.16f1\Editor. The sweep is now VERSION-DIR scoped (never the
  // bare managed root) so it catches the editor's own image/loaded-modules under
  // THIS version dir without collateral-killing a concurrent SIBLING-version editor.
  // This behavioral test EXECUTES the sweep against a fabricated Win32_Process table
  // and asserts the version-dir-scoped lockers are killed while unrelated, out-of-
  // root, AND sibling-version processes are spared.
  const itSweep = PWSH_PRESENT ? test : test.skip;
  itSweep(
    "the stale-process sweep is VERSION-DIR scoped: kills this-version lockers, spares a concurrent different-version editor",
    () => {
      const base = makeTempDir("ensure-sweep");
      workspaces.push(base);
      // Host-native paths so the script's GetFullPath-based path containment is
      // correct on this OS (the assertion is about scoping logic, not Windows
      // drive-letter syntax, which the real runner exercises natively).
      const installRoot = path.join(base, "Editors");
      const version = "6000.3.16f1";
      const versionDir = path.join(installRoot, version);
      const imageInVersionDir = path.join(versionDir, "Editor", "Unity.exe");
      const unpackerInVersionDir = path.join(versionDir, "Editor", "Data", "unpacker.exe");
      const unityElsewhere = path.join(base, "elsewhere", "unity.exe");
      const avOutsideRoot = path.join(base, "Defender", "MsMpEng.exe");
      // HIGH-fix anchor: a DIFFERENT-version editor running concurrently UNDER THE
      // SAME managed root. The prior `imageInsideRoot && looksUnity` branch would
      // have force-killed this; the version-dir-scoped predicate must SPARE it.
      const siblingVersionDir = path.join(installRoot, "2022.3.45f1");
      const siblingEditorExe = path.join(siblingVersionDir, "Editor", "Unity.exe");
      const lit = (p) => p.replace(/'/g, "''");

      const out = runPwshScript(
        [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          extractEnsureEditorFunctions([
            "Stop-StaleUnityProvisioningProcesses",
            "Test-ProcessHasModuleUnderDirectory",
            "Test-IsPathInsideDirectory",
            "Add-ProvisioningProcessCleanupEvent",
            "Write-CiNotice"
          ]),
          // The function records cleanup events into this script-scoped list.
          "$script:ProvisioningProcessCleanupEvents = New-Object System.Collections.Generic.List[object]",
          // Capture kill requests instead of killing real processes.
          "$script:Killed = New-Object System.Collections.Generic.List[int]",
          "function Stop-Process { param([int]$Id, [switch]$Force, $ErrorAction) [void]$script:Killed.Add($Id) }",
          // Fabricated process table. 9001 is the cross-identity locker shape: a
          // Unity-named image OUTSIDE the managed root (the NetworkService CLI) with
          // an EMPTY CommandLine AND empty ExecutablePath -- but a LOADED MODULE
          // resolving under THIS version dir (the loaded-module fallback catches it).
          "function Get-CimInstance { param([string]$ClassName, $ErrorAction) @(",
          `  [pscustomobject]@{ ProcessId = 4242; Name = 'unity.exe'; CommandLine = ''; ExecutablePath = '${lit(imageInVersionDir)}' },`,
          `  [pscustomobject]@{ ProcessId = 5555; Name = 'explorer.exe'; CommandLine = 'explorer'; ExecutablePath = '${lit(path.join(base, "explorer"))}' },`,
          `  [pscustomobject]@{ ProcessId = 6666; Name = 'unity.exe'; CommandLine = 'unity install ${version}'; ExecutablePath = '${lit(unityElsewhere)}' },`,
          `  [pscustomobject]@{ ProcessId = 7777; Name = 'MsMpEng.exe'; CommandLine = ''; ExecutablePath = '${lit(avOutsideRoot)}' },`,
          `  [pscustomobject]@{ ProcessId = 8888; Name = 'someunpacker.exe'; CommandLine = ''; ExecutablePath = '${lit(unpackerInVersionDir)}' },`,
          `  [pscustomobject]@{ ProcessId = 9001; Name = 'unity.exe'; CommandLine = ''; ExecutablePath = '' },`,
          `  [pscustomobject]@{ ProcessId = 2245; Name = 'Unity.exe'; CommandLine = ''; ExecutablePath = '${lit(siblingEditorExe)}' }`,
          ") }",
          // Fake loaded-module table: only 9001 has a module under THIS version dir.
          `$env:DXM_UNITY_FAKE_PROCESS_MODULES = '9001=${lit(path.join(versionDir, "Editor", "Data", "Mono", "mono.dll"))}'`,
          `Stop-StaleUnityProvisioningProcesses -InstallRoot '${lit(installRoot)}' -Version '${version}' -Reason 'regression'`,
          "Write-Output ('KILLED=' + (($script:Killed | Sort-Object) -join ','))"
        ].join("\n")
      );

      const combined = combinedText(out);
      assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
      const killedMatch = /^KILLED=(.*)$/m.exec(out.stdout || "");
      expect(killedMatch).not.toBeNull();
      const killed = killedMatch[1].length ? killedMatch[1].split(",") : [];
      // Killed: empty-CommandLine image under the version dir (4242), the
      // version-scoped command line (6666 -> cmdline carries the version), the helper
      // running FROM the version dir (8888), and the cross-identity locker with a
      // loaded module under the version dir (9001 -> loaded-module fallback).
      expect(killed).toEqual(expect.arrayContaining(["4242", "6666", "8888", "9001"]));
      // SPARED: unrelated explorer (5555), out-of-root antivirus (7777), and --
      // critically for the HIGH collateral-kill fix -- the concurrent SIBLING-version
      // editor under the same root (2245), which the prior under-root branch killed.
      expect(killed).not.toContain("5555");
      expect(killed).not.toContain("7777");
      expect(killed).not.toContain("2245");
    }
  );

  // FINDING-1 (the REAL locker shape): a Unity-named image OUTSIDE the managed root
  // (the NetworkService CLI at C:\Windows\ServiceProfiles\...), with BOTH an empty
  // CommandLine and an empty ExecutablePath (PEB privilege-gated), and NO loaded
  // module readable under the version dir. This is the honest worst case: the sweep
  // has NO signal that ties it to our version, so it must be SPARED (matched 0) --
  // documenting plainly that this residual is not catchable in-tree.
  itSweep(
    "the real cross-identity locker (image outside root, empty cmdline+exe, no readable module) is NOT matched (honest residual)",
    () => {
      const base = makeTempDir("ensure-sweep-residual");
      workspaces.push(base);
      const installRoot = path.join(base, "Editors");
      const version = "6000.3.16f1";
      const cliOutsideRoot = path.join(
        base,
        "ServiceProfiles",
        "NetworkService",
        "AppData",
        "Local",
        "Unity",
        "bin",
        "unity.exe"
      );
      const lit = (p) => p.replace(/'/g, "''");

      const out = runPwshScript(
        [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          extractEnsureEditorFunctions([
            "Stop-StaleUnityProvisioningProcesses",
            "Test-ProcessHasModuleUnderDirectory",
            "Test-IsPathInsideDirectory",
            "Add-ProvisioningProcessCleanupEvent",
            "Write-CiNotice"
          ]),
          "$script:ProvisioningProcessCleanupEvents = New-Object System.Collections.Generic.List[object]",
          "$script:Killed = New-Object System.Collections.Generic.List[int]",
          "function Stop-Process { param([int]$Id, [switch]$Force, $ErrorAction) [void]$script:Killed.Add($Id) }",
          "function Get-CimInstance { param([string]$ClassName, $ErrorAction) @(",
          `  [pscustomobject]@{ ProcessId = 3110; Name = 'unity.exe'; CommandLine = ''; ExecutablePath = '${lit(cliOutsideRoot)}' }`,
          ") }",
          // No fake module table: the loaded-module probe returns no positive signal,
          // exactly like a process whose modules the querying identity cannot read.
          "$env:DXM_UNITY_FAKE_PROCESS_MODULES = $null",
          `Stop-StaleUnityProvisioningProcesses -InstallRoot '${lit(installRoot)}' -Version '${version}' -Reason 'residual'`,
          "Write-Output ('KILLED=' + (($script:Killed | Sort-Object) -join ','))"
        ].join("\n")
      );

      const combined = combinedText(out);
      assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
      const killedMatch = /^KILLED=(.*)$/m.exec(out.stdout || "");
      expect(killedMatch).not.toBeNull();
      const killed = killedMatch[1].length ? killedMatch[1].split(",") : [];
      // HONEST: this locker is uncatchable in-tree -- it must NOT be matched.
      expect(killed).not.toContain("3110");
      expect(killed).toEqual([]);
    }
  );

  test("#1 normal completion is unaffected: a fast install streams full output and resolves the editor", () => {
    const base = makeTempDir("ensure-resilience-fast");
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const diagnosticsPath = path.join(base, "custom-diagnostics", "provisioning.json");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");

    // A fast CLI that, on `install`, fabricates the full CI module layout on disk
    // and exits 0 promptly. install-modules is then a clean no-op (modules on
    // disk). This exercises the SAME timeout-wrapped capture path as a real
    // install, just with a fast child, proving no behavior change.
    const bodyLines = [
      "const path = require('path');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
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
      '  write(\'{"type":"progress","pct":50,"msg":"Downloading editor"}\');',
      "  createModules();",
      "  write('Editor installed successfully');",
      "  exit(0);",
      "}",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(
      bodyLines,
      installRoot,
      {
        // A generous timeout that a fast install never approaches.
        DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS: "120"
      },
      ["-DiagnosticsPath", diagnosticsPath]
    );
    const stdout = out.stdout || "";
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    // Resolution lands on the fabricated editor (structural read: last raw line).
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(editorExe);
    // Full output streamed through the timeout runner (both the progress line and
    // the success line are present)...
    expect(combined).toContain("Downloading editor");
    expect(combined).toContain("Editor installed successfully");
    // ...and NO timeout annotation fired on the healthy path.
    expect(combined).not.toContain("TIMED OUT");

    const summary = JSON.parse(fs.readFileSync(diagnosticsPath, "utf8"));
    const textSummary = fs.readFileSync(
      path.join(path.dirname(diagnosticsPath), "provisioning.txt"),
      "utf8"
    );
    expect(summary.finalClassification).toBe("success");
    expect(summary.cliPath).toContain("unity");
    expect(summary.installRoot).toBe(installRoot);
    expect(summary.editorPath).toBe(editorExe);
    expect(summary.attemptedCommandClasses).toContain("install/modules");
    expect(summary.provisioningProfile).toBe("Full");
    expect(summary.modulePresence["android-open-jdk"]).toBe(true);
    expect(summary.desiredModules).not.toContain("android-open-jdk");
    expect(summary.desiredModules).toContain("android-sdk-ndk-tools");
    expect(textSummary).toContain("classification=success");
    expect(textSummary).toContain("provisioningProfile=Full");
  }, 60000);

  test("EditorOnly fresh install never requests module installation or Android", () => {
    const base = makeTempDir("ensure-editoronly");
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const diagnosticsPath = path.join(base, "diagnostics", "ensure-editor-summary.json");
    const argsLog = path.join(base, "unity-cli-args.tsv");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");

    const bodyLines = [
      "const path = require('path');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
      `const argsLog = ${JSON.stringify(argsLog)};`,
      "const editorRoot = path.join(installRoot, '6000.0.32f1');",
      "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeFile(p, value) { mkdirp(path.dirname(p)); fs.writeFileSync(p, value); fs.chmodSync(p, 0o755); }",
      "function logArgs() { fs.appendFileSync(argsLog, args.join('\\t') + '\\n'); }",
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install') { logArgs(); writeFile(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n'); write('editor-only install completed'); exit(0); }",
      "if (args[0] === 'install-modules') { logArgs(); write('unexpected module install'); exit(9); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(bodyLines, installRoot, {}, [
      "-ProvisioningProfile",
      "EditorOnly",
      "-DiagnosticsPath",
      diagnosticsPath
    ]);
    const stdout = out.stdout || "";
    const combined = combinedText(out);

    assertSpawnStatus(out, 0, expect.getState().currentTestName || "pwsh harness");
    expect(stdout.trim().split(/\r?\n/).pop()).toBe(editorExe);
    expect(combined).toContain("editor-only install completed");
    expect(combined).toContain("requires the Unity editor only");
    const loggedArgs = fs.readFileSync(argsLog, "utf8").trim().split(/\r?\n/);
    expect(loggedArgs).toEqual(["install\t6000.0.32f1"]);
    const summary = JSON.parse(fs.readFileSync(diagnosticsPath, "utf8"));
    expect(summary.provisioningProfile).toBe("EditorOnly");
    expect(summary.desiredModules).toEqual([]);
    expect(summary.verifiedModules).toEqual([]);
    expect(summary.skippedModuleGroups).toEqual(
      expect.arrayContaining([
        "windows-il2cpp",
        "android",
        "android-sdk-ndk-tools",
        "android-open-jdk"
      ])
    );
    expect(Object.keys(summary.requiredModulePresence)).toEqual([]);
    expect(summary.modulePresence["android-sdk-ndk-tools"]).toBe(false);
  }, 60000);

  test("failed full repair summary preserves the resolved partial editor path", () => {
    const base = makeTempDir("ensure-resilience-repair-summary");
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");
    const diagnosticsPath = path.join(base, "diagnostics", "ensure-editor-summary.json");
    const editorRoot = path.join(installRoot, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    fs.mkdirSync(path.dirname(editorExe), { recursive: true });
    fs.writeFileSync(
      editorExe,
      ["#!/usr/bin/env sh", 'echo "Unity fake version"', "exit 0", ""].join("\n")
    );
    fs.chmodSync(editorExe, 0o755);

    const bodyLines = [
      "const path = require('path');",
      `const installRoot = ${JSON.stringify(installRoot)};`,
      "const editorRoot = path.join(installRoot, '6000.0.32f1');",
      "const editorExe = path.join(editorRoot, 'Editor', 'Unity.exe');",
      "function mkdirp(p) { fs.mkdirSync(p, { recursive: true }); }",
      "function writeEditorOnly() {",
      "  mkdirp(path.dirname(editorExe));",
      "  fs.writeFileSync(editorExe, '#!/usr/bin/env sh\\necho \"Unity fake version\"\\nexit 0\\n');",
      "  fs.chmodSync(editorExe, 0o755);",
      "}",
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android android-sdk-ndk-tools android-open-jdk linux-mono linux-il2cpp'); exit(0); }",
      "if (args[0] === 'install-modules') { write('Module installation is only supported for editors installed with Unity Hub.'); exit(6); }",
      "if (args[0] === 'uninstall') { write('metadata cleared'); exit(0); }",
      "if (args[0] === 'install') { writeEditorOnly(); write('repair install left modules missing'); exit(0); }",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [{ version: '6000.0.32f1', path: editorRoot }] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(bodyLines, installRoot, {}, [
      "-DiagnosticsPath",
      diagnosticsPath
    ]);
    const combined = combinedText(out);

    expectPwshFailure(out);
    expect(combined).toContain("repair install completed at");
    const summary = JSON.parse(fs.readFileSync(diagnosticsPath, "utf8"));
    const textSummary = fs.readFileSync(
      path.join(path.dirname(diagnosticsPath), "ensure-editor-summary.txt"),
      "utf8"
    );
    expect(summary.finalClassification).toContain("failed:");
    expect(summary.editorPath).toBe(editorExe);
    expect(summary.modulePresence["windows-il2cpp"]).toBe(false);
    expect(summary.modulePresence["android-sdk-ndk-tools"]).toBe(false);
    expect(textSummary).toContain(`editorPath=${editorExe}`);
  }, 60000);

  // --- #3 WRAP-IMMUNE DIAGNOSTICS: a failing fake CLI (exit 6 with JSON progress
  //     lines) surfaces the wrap-immune ::error:: naming the last progress msg +
  //     disk space. ---
  test("#3 a failing install surfaces the wrap-immune ::error:: with last progress msg + disk space", () => {
    const base = makeTempDir("ensure-resilience-diag");
    workspaces.push(base);
    const installRoot = path.join(base, "configured-root");

    // The fake CLI: `install` emits JSON progress lines (including the NDK line)
    // then FAILS exit 6 without ever producing a usable editor, and `editors`
    // reports none -- so resolution fails and the genuine-failure throw path runs,
    // which is preceded by Write-ModuleInstallFailureDiagnostics. Repair is
    // disabled so the run ends at the first failure (deterministic, fast).
    const bodyLines = [
      `const installRoot = ${JSON.stringify(installRoot)};`,
      "if (args.length === 1 && args[0] === 'install-path') { write(installRoot); exit(0); }",
      "if (args.length >= 1 && args[0] === 'install-path') { exit(0); }",
      "if (args[0] === 'install-modules' && args.includes('-l')) { write('windows-il2cpp webgl android'); exit(0); }",
      "if (args[0] === 'install') {",
      '  write(\'{"type":"progress","pct":10,"msg":"Installing Android SDK..."}\');',
      '  write(\'{"type":"progress","pct":96,"msg":"Installing Android NDK..."}\');',
      "  write('INSTALL_FAILED');",
      "  exit(6);",
      "}",
      "if (args[0] === 'editors') { write(JSON.stringify({ editors: [] })); exit(0); }"
    ];

    const out = runEnsureEditorWithFakeCli(bodyLines, installRoot, {
      DXM_UNITY_DISABLE_EDITOR_REPAIR: "1",
      DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS: "120"
    });
    const combined = combinedText(out);

    expectPwshFailure(out);
    // The wrap-immune failure summary names the version, the outcome, the LAST
    // progress message parsed from the JSON, and the install-drive free space.
    expect(combined).toContain("Unity 6000.0.32f1 module install FAILED");
    expect(combined).toContain("exit code 6");
    expect(combined).toContain("Installing Android NDK...");
    expect(combined).toContain("free space:");
  }, 60000);

  // --- Get-LastCliProgressMessage unit coverage (the parser the summary uses). ---
  test("Get-LastCliProgressMessage parses the last JSON msg, else the last non-empty line", () => {
    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        extractEnsureEditorFunctions(["Get-LastCliProgressMessage"]),
        // Last JSON msg wins (even when a later line has no msg). NOTE: none of
        // these lines carry "phase":"install", so the install-phase/max-pct
        // preference does not fire -- this exercises the last-msg FALLBACK.
        '$a = @(\'{"msg":"first"}\', \'{"type":"progress","msg":"Installing Android NDK..."}\', \'trailing plain line\')',
        "Write-Output ('JSON=' + (Get-LastCliProgressMessage -Output $a))",
        // No JSON msg -> last non-empty line.
        "$b = @('plain one', 'plain two', '', '   ')",
        "Write-Output ('PLAIN=' + (Get-LastCliProgressMessage -Output $b))",
        // Empty -> placeholder.
        "Write-Output ('EMPTY=' + (Get-LastCliProgressMessage -Output @()))"
      ].join("\n")
    );
    expectPwshStatus(out, 0);
    const stdout = out.stdout || "";
    expect(normalizePwshText(stdout)).toContain("JSON=Installing Android NDK...");
    expect(normalizePwshText(stdout)).toContain("PLAIN=plain two");
    expect(normalizePwshText(stdout)).toContain("EMPTY=(no output captured)");
  });

  // --- INSTALL-PHASE/MAX-PCT preference (the fix): an out-of-order download
  //     "Starting install..." line emitted AFTER the deepest install-phase line
  //     must NOT mask the real failing module. The summary must report the
  //     install-phase msg at the MAXIMUM pct seen, with the pct appended. ---
  test("Get-LastCliProgressMessage prefers the install-phase msg at the highest pct over out-of-order trailing lines", () => {
    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        extractEnsureEditorFunctions(["Get-LastCliProgressMessage"]),
        // Interleaved: an install-phase NDK line at pct 93 (the real failing
        // point), followed by an OUT-OF-ORDER download line "Starting install..."
        // The preference must surface the NDK line with its pct, not the trailer.
        "$a = @(",
        '  \'{"type":"progress","phase":"download","pct":12,"msg":"Downloading Android NDK"}\',',
        '  \'{"type":"progress","phase":"install","pct":40,"msg":"Installing Android SDK"}\',',
        '  \'{"type":"progress","phase":"install","pct":93,"msg":"Installing Android NDK"}\',',
        '  \'{"type":"progress","phase":"download","pct":5,"msg":"Starting install..."}\'',
        ")",
        "Write-Output ('INSTALL=' + (Get-LastCliProgressMessage -Output $a))",
        // When NO install-phase line is present, the fallback (last msg) still applies.
        '$b = @(\'{"phase":"download","pct":1,"msg":"only download"}\')',
        "Write-Output ('FALLBACK=' + (Get-LastCliProgressMessage -Output $b))"
      ].join("\n")
    );
    expectPwshStatus(out, 0);
    const stdout = out.stdout || "";
    expect(normalizePwshText(stdout)).toContain("INSTALL=Installing Android NDK (93%)");
    expect(normalizePwshText(stdout)).toContain("FALLBACK=only download");
  });

  // --- Clear-PartialAndroidModulePayload: removes NDK/SDK under the editor and
  //     stays strictly inside the editor directory (never touches siblings). ---
  test("Clear-PartialAndroidModulePayload removes NDK/SDK and stays inside the editor dir", () => {
    const base = makeTempDir("ensure-resilience-clear");
    workspaces.push(base);
    const editorRoot = path.join(base, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    const androidRoot = path.join(editorRoot, "Editor", "Data", "PlaybackEngines", "AndroidPlayer");
    const ndkFile = path.join(androidRoot, "NDK", "toolchains", "deep", "source.properties");
    const sdkFile = path.join(androidRoot, "SDK", "platform-tools", "adb.exe");
    // A sibling OUTSIDE AndroidPlayer that must SURVIVE (proves we stay scoped).
    const siblingFile = path.join(
      editorRoot,
      "Editor",
      "Data",
      "PlaybackEngines",
      "WebGLSupport",
      "UnityEditor.WebGL.Extensions.dll"
    );
    // A file OUTSIDE the editor dir entirely that must SURVIVE.
    const outsideFile = path.join(base, "outside.txt");
    for (const f of [editorExe, ndkFile, sdkFile, siblingFile, outsideFile]) {
      fs.mkdirSync(path.dirname(f), { recursive: true });
      fs.writeFileSync(f, "");
    }

    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        "$env:DXM_ENSURE_EDITOR_RETRY_DELAY_SECONDS = '0'",
        extractEnsureEditorFunctions([
          "Clear-PartialAndroidModulePayload",
          "Invoke-WithRetry",
          "Get-EnsureEditorRetryDelaySeconds"
        ]),
        `Clear-PartialAndroidModulePayload -EditorPath '${editorExe.replace(/'/g, "''")}'`,
        "Write-Output 'CLEARED'"
      ].join("\n")
    );
    expectPwshStatus(out, 0);
    expect(combinedText(out)).toContain("Cleared partial Android module payload");
    // NDK + SDK heavy payload dirs are gone...
    expect(fs.existsSync(path.join(androidRoot, "NDK"))).toBe(false);
    expect(fs.existsSync(path.join(androidRoot, "SDK"))).toBe(false);
    // ...while a sibling inside the editor and anything outside survive.
    expect(fs.existsSync(siblingFile)).toBe(true);
    expect(fs.existsSync(outsideFile)).toBe(true);
    expect(fs.existsSync(editorExe)).toBe(true);
  });

  // --- Write-UnityModuleInstallPostMortem: per-group present/MISSING lines, and
  //     the MAX_PATH warning when the deepest NDK path >= 240 and long paths are
  //     NOT enabled.
  //
  // HERMETICITY (cross-platform): the production guard fires only when
  // `deepestNdk >= 240 -and longPaths -ne $true`, and Test-WindowsLongPathSupport
  // reads the REAL HKLM registry on Windows -- uncontrolled by the test and
  // different per runner. So we FORCE the long-path side via the TEST-ONLY env
  // override DXM_UNITY_FAKE_LONGPATHS_ENABLED instead of depending on the host.
  // We ALSO avoid creating >260-char real paths on Windows (which can throw in
  // setup when long paths are not enabled): the physical deep-path creation + the
  // "hit the Windows MAX_PATH" assertion are gated to non-win32. On win32 we assert
  // only the OS-safe parts (the per-group lines + the LongPathsEnabled state line).
  // The @cross-platform-regression suite still exercises the full deep-path path on
  // ubuntu + macos. ---
  test("Write-UnityModuleInstallPostMortem emits per-group state and the long-path/MAX_PATH warning on a deep NDK path", () => {
    const base = makeTempDir("ensure-resilience-postmortem");
    workspaces.push(base);
    const editorRoot = path.join(base, "6000.0.32f1");
    const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
    const androidRoot = path.join(editorRoot, "Editor", "Data", "PlaybackEngines", "AndroidPlayer");
    const isWin = process.platform === "win32";

    // Always create the editor exe. On non-win32 also build a deliberately DEEP NDK
    // path (>= 240 absolute chars) so the MAX_PATH warning can fire; on win32 we
    // create only a shallow NDK leaf (a 260+ char path can throw at creation when
    // long paths are not enabled, which is exactly the host state we must not
    // depend on).
    const filesToCreate = [editorExe];
    if (isWin) {
      filesToCreate.push(path.join(androidRoot, "NDK", "source.properties"));
    } else {
      const deepSegment = "x".repeat(50);
      filesToCreate.push(
        path.join(
          androidRoot,
          "NDK",
          deepSegment,
          deepSegment,
          deepSegment,
          deepSegment,
          deepSegment,
          "source.properties"
        )
      );
    }
    for (const f of filesToCreate) {
      fs.mkdirSync(path.dirname(f), { recursive: true });
      fs.writeFileSync(f, "");
    }

    // FORCE long paths to "not enabled" so the warning side of the guard is
    // controlled (independent of the real HKLM value on a Windows runner).
    const env = { ...process.env, DXM_UNITY_FAKE_LONGPATHS_ENABLED: "0" };
    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        extractEnsureEditorFunctions([
          "Write-UnityModuleInstallPostMortem",
          "Get-UnityProvisioningProfile",
          "Assert-UnityProvisioningProfile",
          "Get-UnityCiModuleSpec",
          "Get-UnityCiModuleSpecForProfile",
          "Get-UnityCiVerifiedModuleGroups",
          "Test-UnityProvisioningProfileIncludesAndroid",
          "Test-UnityCiModuleGroupPresent",
          "Test-Il2CppModulePresent",
          "Test-AnyUnityLeafPresent",
          "Get-DeepestPathLengthUnder",
          "Test-WindowsLongPathSupport",
          "Get-InstallDriveFreeSpaceText"
        ]),
        `Write-UnityModuleInstallPostMortem -Version '6000.0.32f1' -EditorPath '${editorExe.replace(/'/g, "''")}' -Root '${base.replace(/'/g, "''")}'`,
        "Write-Output 'POSTMORTEM-DONE'"
      ].join("\n"),
      env
    );
    expectPwshStatus(out, 0);
    const combined = combinedText(out);
    expect(combined).toContain("module install post-mortem");
    // Per-group MISSING lines (no module leaves were fabricated besides the NDK
    // source.properties, so windows-il2cpp etc. are MISSING). OS-safe everywhere.
    expect(combined).toContain("module group 'windows-il2cpp': MISSING");
    expect(combined).toContain("AndroidPlayer\\NDK : exists");
    // The forced long-path-disabled state is reflected in the post-mortem line on
    // every OS (the override makes Test-WindowsLongPathSupport return $false).
    expect(combined).toContain("Windows long-path support (LongPathsEnabled): False");
    if (!isWin) {
      // The MAX_PATH warning fires on the deep NDK path with long paths "not enabled".
      expect(combined).toContain("hit the Windows MAX_PATH");
      expect(combined).toContain("docs/runbooks/unity-runners-after-transfer.md");
    }
    expect(combined).toContain("POSTMORTEM-DONE");
  });

  // --- Complementary guard-condition proof (non-win32): with long paths FORCED
  //     ENABLED via the TEST-ONLY override, the MAX_PATH warning does NOT fire even
  //     at a deep NDK path -- proving the warning's `longPaths -ne $true` condition.
  //     Gated to non-win32 for the same >260-char-path hermeticity reason above. ---
  const itLongPathEnabled = process.platform === "win32" ? test.skip : test;
  itLongPathEnabled(
    "Write-UnityModuleInstallPostMortem suppresses the MAX_PATH warning when long paths are enabled",
    () => {
      const base = makeTempDir("ensure-resilience-postmortem-longpaths");
      workspaces.push(base);
      const editorRoot = path.join(base, "6000.0.32f1");
      const editorExe = path.join(editorRoot, "Editor", "Unity.exe");
      const androidRoot = path.join(
        editorRoot,
        "Editor",
        "Data",
        "PlaybackEngines",
        "AndroidPlayer"
      );
      const deepSegment = "x".repeat(50);
      const deepNdkFile = path.join(
        androidRoot,
        "NDK",
        deepSegment,
        deepSegment,
        deepSegment,
        deepSegment,
        deepSegment,
        "source.properties"
      );
      for (const f of [editorExe, deepNdkFile]) {
        fs.mkdirSync(path.dirname(f), { recursive: true });
        fs.writeFileSync(f, "");
      }

      const env = { ...process.env, DXM_UNITY_FAKE_LONGPATHS_ENABLED: "1" };
      const out = runPwshScript(
        [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          extractEnsureEditorFunctions([
            "Write-UnityModuleInstallPostMortem",
            "Get-UnityProvisioningProfile",
            "Assert-UnityProvisioningProfile",
            "Get-UnityCiModuleSpec",
            "Get-UnityCiModuleSpecForProfile",
            "Get-UnityCiVerifiedModuleGroups",
            "Test-UnityProvisioningProfileIncludesAndroid",
            "Test-UnityCiModuleGroupPresent",
            "Test-Il2CppModulePresent",
            "Test-AnyUnityLeafPresent",
            "Get-DeepestPathLengthUnder",
            "Test-WindowsLongPathSupport",
            "Get-InstallDriveFreeSpaceText"
          ]),
          `Write-UnityModuleInstallPostMortem -Version '6000.0.32f1' -EditorPath '${editorExe.replace(/'/g, "''")}' -Root '${base.replace(/'/g, "''")}'`,
          "Write-Output 'POSTMORTEM-DONE'"
        ].join("\n"),
        env
      );
      expectPwshStatus(out, 0);
      const combined = combinedText(out);
      // The forced long-paths-enabled state is reflected...
      expect(combined).toContain("Windows long-path support (LongPathsEnabled): True");
      // ...and CRUCIALLY the MAX_PATH warning is suppressed despite the deep path.
      expect(combined).not.toContain("hit the Windows MAX_PATH");
      expect(combined).toContain("POSTMORTEM-DONE");
    }
  );

  // --- Test-WindowsLongPathSupport / Get-DeepestPathLengthUnder are SAFE on
  //     missing/unreadable paths (never throw, sensible sentinels). ---
  test("Test-WindowsLongPathSupport and Get-DeepestPathLengthUnder are safe on missing paths", () => {
    const out = runPwshScript(
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        extractEnsureEditorFunctions(["Test-WindowsLongPathSupport", "Get-DeepestPathLengthUnder"]),
        // Long-path probe never throws; on non-Windows it returns $null.
        "$lp = Test-WindowsLongPathSupport",
        "Write-Output ('LONGPATH=' + ($null -eq $lp))",
        // Deepest-path on a non-existent dir returns 0 (never throws).
        "Write-Output ('DEEPMISSING=' + (Get-DeepestPathLengthUnder -Directory '/definitely/not/a/real/dir/xyz'))",
        "Write-Output ('DEEPEMPTY=' + (Get-DeepestPathLengthUnder -Directory ''))"
      ].join("\n")
    );
    expectPwshStatus(out, 0);
    const stdout = out.stdout || "";
    // On the Linux/macOS CI legs the long-path probe returns $null (-> True here);
    // on Windows it returns a bool. Either way it must not have thrown (status 0).
    expect(normalizePwshText(stdout)).toMatch(/LONGPATH=(True|False)/);
    expect(stdout).toContain("DEEPMISSING=0");
    expect(stdout).toContain("DEEPEMPTY=0");
  });

  // --- LIVE PER-LINE STREAMING (BLOCKER-1): the timeout runner must echo each
  //     line the INSTANT it arrives, not buffer the whole stream until the child
  //     exits. A 45-minute install with a buffered reader is a blank CI console
  //     for 45 minutes -- an observer cannot tell a slow install from a hang.
  //
  // HOW IT IS PROVEN PORTABLY: spawnSync buffers a child's stdout and returns it
  // only at exit, so the PARENT cannot observe per-line timing. Instead the pwsh
  // harness AST-extracts Invoke-UnityCliCaptureWithTimeout, PROXIES Write-Host so
  // every echoed line is stamped with [DateTime]::UtcNow.Ticks into a timing file
  // (the stamp is taken INSIDE pwsh at echo time, immune to spawnSync buffering),
  // and runs the function against a CLI that prints "FIRST", sleeps, prints
  // "SECOND". A LIVE implementation stamps FIRST a sleep-interval BEFORE SECOND; a
  // ReadToEnd/buffer-until-exit implementation stamps both together at exit (gap
  // ~0). The CLI is plain `node <script>` (UnityCliPath = 'node') so it resolves
  // identically on every OS without a .sh/.cmd shim. ----------------------------
  describe("BLOCKER-1 live per-line streaming", () => {
    // Build a node CLI script the harness drives via `node <scriptPath> install`.
    function writeNodeCli(bodyJs) {
      const base = makeTempDir("ensure-resilience-livecli");
      workspaces.push(base);
      const scriptPath = path.join(base, "cli.js");
      fs.writeFileSync(
        scriptPath,
        ['"use strict";', "const fs = require('fs');", bodyJs, ""].join("\n"),
        "utf8"
      );
      return scriptPath;
    }

    // Run Invoke-UnityCliCaptureWithTimeout (AST-extracted) against `node
    // <cliScript> install`, proxying Write-Host to stamp each echoed line into a
    // timing file. Returns { status, stdout, stderr, timingPath, timingRows }
    // where timingRows is [{ ticks, text }] in echo order.
    function runCaptureWithTiming(cliScript, timeoutSeconds) {
      const base = makeTempDir("ensure-resilience-liverun");
      workspaces.push(base);
      const timingPath = path.join(base, "timing.tsv");
      const cliScriptLiteral = cliScript.replace(/'/g, "''");
      const timingLiteral = timingPath.replace(/'/g, "''");
      const out = runPwshScript(
        [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          extractEnsureEditorFunctions([
            "ConvertTo-ProcessArgumentLine",
            "Invoke-UnityCliCaptureWithTimeout",
            "Get-LastCliProgressMessage",
            // Required transitive deps for the heartbeat-stall detector + the
            // de-duplicating collapsed tail used in the wrap-immune ::error::.
            "Get-CliProgressTriple",
            "Get-EnsureEditorProgressStallSeconds",
            "Get-EnsureEditorProgressNoticeIntervalSeconds",
            "Get-CollapsedCliOutputTail"
          ]),
          // `node` resolves via PATH in ProcessStartInfo on every OS, so no shell
          // shim is needed; the CLI script path is the first -Argument.
          "$script:UnityCliPath = 'node'",
          `$timingFile = '${timingLiteral}'`,
          // Proxy Write-Host: stamp UtcNow.Ticks + the line into the timing file,
          // then forward to the real cmdlet so the live console behavior is
          // unchanged (and order is preserved -- all on the main thread).
          "function Write-Host { param([Parameter(ValueFromRemainingArguments=$true)]$msg)",
          "  $text = ($msg -join ' ')",
          '  Add-Content -LiteralPath $timingFile -Value ("$([DateTime]::UtcNow.Ticks)`t$text")',
          "  Microsoft.PowerShell.Utility\\Write-Host $text",
          "}",
          `$result = Invoke-UnityCliCaptureWithTimeout -Arguments @('${cliScriptLiteral}', 'install') -TimeoutSeconds ${timeoutSeconds}`,
          'Microsoft.PowerShell.Utility\\Write-Output ("RESULT Success=$($result.Success) Exit=$($result.ExitCode)")',
          "Microsoft.PowerShell.Utility\\Write-Output (\"OUTPUT=$($result.Output -join '|')\")"
        ].join("\n")
      );
      let timingRows = [];
      try {
        timingRows = fs
          .readFileSync(timingPath, "utf8")
          .split(/\r?\n/)
          .filter((l) => l.includes("\t"))
          .map((l) => {
            const tab = l.indexOf("\t");
            return { ticks: Number(l.slice(0, tab)), text: l.slice(tab + 1) };
          });
      } catch {
        // left empty; the assertions below surface a missing file clearly.
      }
      return {
        status: out.status,
        stdout: out.stdout || "",
        stderr: out.stderr || "",
        timingPath,
        timingRows
      };
    }

    function firstStamp(rows, text) {
      const row = rows.find((r) => r.text === text);
      return row ? row.ticks : null;
    }

    // Generous (2.5s) sleep keeps the timing margin huge so the assertion is robust
    // on a slow/loaded CI box, never flaky. A LIVE run stamps FIRST ~2.5s before
    // SECOND; a buffered run stamps them within a few ms of each other at exit.
    test("each line is echoed LIVE as it arrives (FIRST is streamed well before SECOND)", () => {
      const cli = writeNodeCli(
        [
          "console.log('FIRST');",
          "setTimeout(() => { console.log('SECOND'); process.exit(0); }, 2500);"
        ].join("\n")
      );
      const run = runCaptureWithTiming(cli, 30);

      // The function completed normally and captured both lines in arrival order.
      expect(combinedText(run)).toContain("RESULT Success=True Exit=0");
      expect(combinedText(run)).toContain("OUTPUT=FIRST|SECOND");

      const firstAt = firstStamp(run.timingRows, "FIRST");
      const secondAt = firstStamp(run.timingRows, "SECOND");
      // Both lines must have been individually echoed (so both were stamped).
      expect(firstAt).not.toBeNull();
      expect(secondAt).not.toBeNull();

      // THE LIVE-STREAMING PROOF: the gap between echoing FIRST and echoing SECOND
      // must be a large fraction of the 2.5s child sleep. A ReadToEnd/buffer-until-
      // exit regression stamps both together at exit, collapsing this to ~0ms
      // (empirically ~1ms) -- so a 1500ms floor cleanly separates live from
      // buffered with a >900ms safety margin. (1 tick = 100ns; 10000 ticks = 1ms.)
      const gapMs = (secondAt - firstAt) / 10000;
      expect(gapMs).toBeGreaterThan(1500);
    }, 60000);

    // Belt-and-braces (also live-distinguishing in the kill direction): with a
    // SHORT timeout that fires AFTER FIRST is printed but BEFORE SECOND, FIRST must
    // be present in the captured output -- i.e. it was drained as it arrived, not
    // lost. (This complements the timing proof above and the orphan-pipe test.)
    test("a short timeout firing between two prints still captures the line drained before the kill", () => {
      const cli = writeNodeCli(
        [
          // Print FIRST immediately, then HANG well past the timeout (never print
          // SECOND, never exit) so the deadline fires between the two.
          "console.log('FIRST');",
          "setTimeout(() => { console.log('SECOND'); }, 60000);",
          "setInterval(() => {}, 100000);"
        ].join("\n")
      );
      const run = runCaptureWithTiming(cli, 2);

      const combined = combinedText(run);
      // It timed out (sentinel 124) and was classified as a failure...
      expect(combined).toContain("RESULT Success=False Exit=124");
      // ...yet FIRST -- printed before the kill -- was streamed/captured live.
      expect(combined).toContain("OUTPUT=FIRST");
      expect(firstStamp(run.timingRows, "FIRST")).not.toBeNull();
      // SECOND was never reached, so it must NOT appear.
      expect(run.timingRows.some((r) => r.text === "SECOND")).toBe(false);
    }, 60000);
  });

  // --- ORPHAN-PIPE HANG (MAJOR-1): the OLD WaitForExit(timeoutMs)+ReadToEnd shape
  //     mis-handled a child that EXITS quickly while a grandchild inherits the
  //     redirected stdout and holds it open: WaitForExit returned TRUE (the direct
  //     child exited) so nothing was killed, ReadToEnd never completed (pipe still
  //     open), the run stalled, and -- the real defect -- it reported Success=TRUE
  //     with NO output. The poll loop fixes this structurally: EOF never arrives
  //     (the pipe stays open), so the loop runs to the DEADLINE, tree-kills, and
  //     classifies the run as a TIMEOUT failure (Success=$false / exit 124) with
  //     the pre-exit output captured -- never a silent false success.
  //
  // NOTE ON THE GRANDCHILD: once the direct child exits, the OS reparents the
  // grandchild to init (PID 1 on POSIX), so it is no longer in any process tree
  // rooted at the (dead) direct child -- Kill($true)/any tree walk provably cannot
  // reach a fully-orphaned grandchild on any platform. The load-bearing, portable
  // guarantee this test pins is therefore the CORRECTNESS one: the run is NOT a
  // false success and DOES hit the timeout path. The test cleans up the orphan it
  // intentionally creates so the suite leaves nothing behind. -------------------
  describe("MAJOR-1 orphan-pipe hang is classified as a timeout, not a false success", () => {
    test("a child that exits 0 but leaves a grandchild holding stdout hits the deadline (Success=$false / exit 124)", () => {
      const base = makeTempDir("ensure-resilience-orphan");
      workspaces.push(base);
      const gcPidFile = path.join(base, "grandchild.pid");

      // The CLI: spawn a DETACHED, unref'd grandchild that inherits this process's
      // stdout (fd 1) so the redirected pipe stays open after we exit; record its
      // pid; print one line; then exit 0 IMMEDIATELY. The held-open pipe means the
      // capture loop never sees stdout EOF.
      const cliScript = (() => {
        const dir = makeTempDir("ensure-resilience-orphancli");
        workspaces.push(dir);
        const p = path.join(dir, "cli.js");
        fs.writeFileSync(
          p,
          [
            '"use strict";',
            "const fs = require('fs');",
            "const { spawn } = require('child_process');",
            `const gcPidFile = ${JSON.stringify(gcPidFile)};`,
            // Grandchild inherits parent's stdout (fd 1) -> pipe stays open.
            "const gc = spawn(process.execPath, ['-e', 'setInterval(() => {}, 100000)'], { stdio: ['ignore', 1, 'ignore'], detached: true });",
            "fs.writeFileSync(gcPidFile, String(gc.pid));",
            "gc.unref();",
            "process.stdout.write('PARENT_EXITING\\n');",
            "process.exit(0);",
            ""
          ].join("\n"),
          "utf8"
        );
        return p;
      })();

      const startedAt = Date.now();
      const run = runCaptureViaAst(cliScript, 2);
      const elapsedMs = Date.now() - startedAt;
      const combined = combinedText(run);

      // It must NOT have run forever: a 2s deadline + a bounded reap is well under
      // 30s. The OLD code returned ~instantly with a WRONG success here; the new
      // code spends ~the deadline then reports failure -- both are << 30s, so the
      // load-bearing assertion is the OUTCOME (below), not the wall time.
      expect(elapsedMs).toBeLessThan(30000);

      // THE FIX: classified as a TIMEOUT failure (sentinel 124), NOT a silent
      // success-with-no-output.
      expect(combined).toContain("Success=False");
      expect(combined).toContain("Exit=124");
      expect(combined).toContain("TIMED OUT after 2 second(s)");
      // The line printed before the parent exited was drained live, not discarded
      // (the OLD ReadToEnd path discarded ALL output in this scenario).
      expect(combined).toContain("PARENT_EXITING");

      // The direct child (`node <cli>`) is gone (it exited on its own). Clean up
      // the intentionally-orphaned grandchild so the suite leaks nothing; assert it
      // existed so the scenario is real (its survival is an OS guarantee we do NOT
      // assert -- see the describe note).
      const reapOrphan = (pidPath) => {
        if (!fs.existsSync(pidPath)) {
          return;
        }
        const pid = Number(fs.readFileSync(pidPath, "utf8").trim());
        if (!Number.isInteger(pid) || pid <= 0) {
          return;
        }
        try {
          process.kill(pid, "SIGKILL");
        } catch {
          // already gone -- nothing to clean up.
        }
      };
      expect(fs.existsSync(gcPidFile)).toBe(true);
      reapOrphan(gcPidFile);
    }, 60000);
  });
});

// Run Invoke-UnityCliCaptureWithTimeout (AST-extracted) against `node <cliScript>
// install` WITHOUT the Write-Host timing proxy -- a plain capture run used where
// only the returned Success/Exit/Output and the streamed annotations matter.
function runCaptureViaAst(cliScript, timeoutSeconds) {
  const cliScriptLiteral = cliScript.replace(/'/g, "''");
  const out = runPwshScript(
    [
      "Set-StrictMode -Version Latest",
      "$ErrorActionPreference = 'Stop'",
      extractEnsureEditorFunctions([
        "ConvertTo-ProcessArgumentLine",
        "Invoke-UnityCliCaptureWithTimeout",
        "Get-LastCliProgressMessage",
        // Required transitive deps for the heartbeat-stall detector + the
        // de-duplicating collapsed tail used in the wrap-immune ::error::.
        "Get-CliProgressTriple",
        "Get-EnsureEditorProgressStallSeconds",
        "Get-EnsureEditorProgressNoticeIntervalSeconds",
        "Get-CollapsedCliOutputTail"
      ]),
      "$script:UnityCliPath = 'node'",
      `$result = Invoke-UnityCliCaptureWithTimeout -Arguments @('${cliScriptLiteral}', 'install') -TimeoutSeconds ${timeoutSeconds}`,
      'Write-Output ("RESULT Success=$($result.Success) Exit=$($result.ExitCode)")',
      "Write-Output (\"OUTPUT=$($result.Output -join '|')\")"
    ].join("\n")
  );
  return { status: out.status, stdout: out.stdout || "", stderr: out.stderr || "" };
}
