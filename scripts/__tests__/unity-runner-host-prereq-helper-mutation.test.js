/**
 * @fileoverview Mutation-resistance behavioral tests for the host-prereq
 * helper functions in scripts/unity/bootstrap-windows-runner.ps1.
 *
 * WHY: per the adversarial-review-loop working style, every guard must be
 * MUTATION-TESTED -- a refactor that swaps `return $true` for `return $false`
 * (or that drops the regex anchor that rejects single-drive roots) must fail
 * loudly. The static-contract test pins the SOURCE shape; this file pins the
 * RUNTIME BEHAVIOR by dot-sourcing the bootstrap script into a hermetic pwsh
 * session and invoking each helper with adversarial inputs.
 *
 * Mirrors the AST-extract-then-dot-source pattern used by
 * unity-ensure-editor-il2cpp-idempotency.test.js (which also targets
 * top-level-side-effect-free function inspection), but we go one step lighter
 * here: the bootstrap script DECLARES its dot-source-safety contract
 * explicitly (see scripts/unity/bootstrap-windows-runner.ps1's "Import
 * contract" section in the .DESCRIPTION block) and the companion suite
 * unity-runner-host-prereq-dot-source-safety.test.js verifies that contract
 * holds. So we can dot-source the WHOLE file rather than AST-extracting one
 * function at a time -- the contract makes that side-effect-free.
 *
 * Each test runs in one pwsh process (a single dot-source + a sequence of
 * Write-Host outcomes) so the total wall-clock cost is one spawn per test
 * description, not one per assertion. The single-process pattern mirrors the
 * style used elsewhere in this directory and keeps the suite fast.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");
const { assertSpawnStatus } = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const BOOTSTRAP_SCRIPT = path.join(REPO_ROOT, "scripts", "unity", "bootstrap-windows-runner.ps1");

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

if (!PWSH_PRESENT) {
  // eslint-disable-next-line no-console
  console.warn(
    "[unity-runner-host-prereq-helper-mutation] pwsh not found on PATH; skipping behavioral assertions. CI runners always have pwsh."
  );
}

const workspaces = [];

afterAll(() => {
  for (const ws of workspaces) {
    try {
      fs.rmSync(ws, { recursive: true, force: true });
    } catch {
      // best-effort cleanup; never fail the suite on teardown.
    }
  }
});

function makeWorkspace() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-bootstrap-helper-mutation-"));
  workspaces.push(dir);
  return dir;
}

function combined(result) {
  return ((result.stdout || "") + "\n" + (result.stderr || "")).replace(/\r\n/g, "\n");
}

// Run a pwsh script that dot-sources the bootstrap script, then executes the
// given body. Returns the spawn result.
function runHarness(body) {
  const escapedScript = BOOTSTRAP_SCRIPT.replace(/'/g, "''");
  const workspace = makeWorkspace();
  const harness = path.join(workspace, "harness.ps1");
  fs.writeFileSync(
    harness,
    [
      "# Dot-source contract is verified by unity-runner-host-prereq-dot-source-safety.test.js;",
      "# we rely on it here so we can invoke individual helpers without AST extraction.",
      `. '${escapedScript}'`,
      body
    ].join("\n"),
    "utf8"
  );
  return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harness], {
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024
  });
}

describe("scripts/unity/bootstrap-windows-runner.ps1 helper mutation resistance", () => {
  test("the script under test exists", () => {
    expect(fs.existsSync(BOOTSTRAP_SCRIPT)).toBe(true);
  });

  if (!PWSH_PRESENT) {
    test.skip("Test-IsWindowsHost on Linux returns $false", () => {});
    test.skip("Test-IsAdministrator on Linux returns $false (no throw)", () => {});
    test.skip("Test-IsAccessDeniedException returns $false for benign exceptions", () => {});
    test.skip("Test-DefenderExclusionPathAllowed rejects 'C:\\'", () => {});
    test.skip("Test-DefenderExclusionPathAllowed rejects empty string", () => {});
    test.skip("Test-DefenderExclusionPathAllowed accepts 'C:\\Unity\\Editors'", () => {});
    test.skip("Test-DefenderExclusionPathAllowed accepts a runner _work fragment", () => {});
    test.skip("Test-DefenderExclusionPathAllowed rejects 'C:\\WINDOWS'", () => {});
    test.skip("Test-VcRedistModernFilesOnDisk on Linux returns @{present=$true; missing=@()}", () => {});
    test.skip("Test-VcRedist2010FilesOnDisk on Linux returns @{present=$true; missing=@()}", () => {});
    test.skip("Test-VcRedist2010Installed on Linux returns @{installed=$true; reason=...}", () => {});
    return;
  }

  const ON_NON_WINDOWS = process.platform !== "win32";

  test("Test-IsWindowsHost on Linux returns $false", () => {
    if (!ON_NON_WINDOWS) {
      // On a real Windows host this MUST return $true; assert it positively
      // so the test still catches a regression that flipped the boolean.
      const result = runHarness("Write-Output ([bool](Test-IsWindowsHost))");
      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      expect((result.stdout || "").trim()).toBe("True");
      return;
    }
    const result = runHarness("Write-Output ([bool](Test-IsWindowsHost))");
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("False");
  });

  test("Test-IsAdministrator on Linux returns $false without throwing", () => {
    if (!ON_NON_WINDOWS) {
      // On Windows, the function may return $true or $false depending on
      // elevation; we cannot assert the value, only that it does NOT throw
      // and the exit code is 0. We still assert it returned a boolean so a
      // mutation that returned a non-bool is caught.
      const result = runHarness("$r = Test-IsAdministrator; Write-Output ($r -is [bool])");
      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      expect((result.stdout || "").trim()).toBe("True");
      return;
    }
    // On Linux: $false. Crucially MUST NOT throw -- WindowsIdentity::GetCurrent
    // is .NET Core safe but the WindowsBuiltInRole check requires the Windows
    // identity stack; an unguarded call throws PlatformNotSupportedException.
    const result = runHarness("Write-Output ([bool](Test-IsAdministrator))");
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("False");
  });

  test("Test-IsAccessDeniedException returns $false for benign exceptions", () => {
    // Construct a benign ArgumentException error record and feed it through
    // the classifier. It must NOT match access-denied (the helper exists
    // specifically to avoid false-positive English regex matches on
    // unrelated error text).
    const result = runHarness(
      [
        "$ex = New-Object System.ArgumentException('benign argument exception')",
        "$er = New-Object System.Management.Automation.ErrorRecord(",
        "  $ex, 'BenignErr', [System.Management.Automation.ErrorCategory]::InvalidArgument, $null)",
        "Write-Output ([bool](Test-IsAccessDeniedException -ErrorRecord $er))"
      ].join("\n")
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("False");
  });

  test("Test-IsAccessDeniedException returns $true for UnauthorizedAccessException", () => {
    // Symmetric mutation guard: the classifier MUST positively classify the
    // canonical .NET shape. A mutation that always returned $false (the
    // simplest sabotage) would pass the previous test but fail this one.
    const result = runHarness(
      [
        "$ex = New-Object System.UnauthorizedAccessException('access denied (test)')",
        "$er = New-Object System.Management.Automation.ErrorRecord(",
        "  $ex, 'UnauthorizedAccess', [System.Management.Automation.ErrorCategory]::PermissionDenied, $null)",
        "Write-Output ([bool](Test-IsAccessDeniedException -ErrorRecord $er))"
      ].join("\n")
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("True");
  });

  // ---------------------------------------------------------------------
  // Test-DefenderExclusionPathAllowed: the load-bearing allow-list guard
  // against a hostile/misconfigured RUNNER_WORKSPACE that would otherwise
  // exclude `C:\` from Defender on-access scanning.
  // ---------------------------------------------------------------------
  test("Test-DefenderExclusionPathAllowed rejects 'C:\\' (single drive root)", () => {
    const result = runHarness(
      "Write-Output ([bool](Test-DefenderExclusionPathAllowed -Path 'C:\\' -UnityInstallRoot 'C:\\Unity\\Editors'))"
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("False");
  });

  test("Test-DefenderExclusionPathAllowed rejects 'C:' (no separator)", () => {
    const result = runHarness(
      "Write-Output ([bool](Test-DefenderExclusionPathAllowed -Path 'C:' -UnityInstallRoot 'C:\\Unity\\Editors'))"
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("False");
  });

  test("Test-DefenderExclusionPathAllowed rejects whitespace-only path", () => {
    // The function declares its $Path parameter as Mandatory; pwsh 7's
    // cmdlet binder ALREADY rejects an empty string at param-binding time
    // (the inner `IsNullOrWhiteSpace($Path)` guard handles whitespace-only
    // input that DOES bind). A whitespace-only string is the input that
    // EXERCISES the inner guard, which is what we are mutation-testing.
    const result = runHarness(
      "Write-Output ([bool](Test-DefenderExclusionPathAllowed -Path '   ' -UnityInstallRoot 'C:\\Unity\\Editors'))"
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("False");
  });

  test("Test-DefenderExclusionPathAllowed accepts 'C:\\Unity\\Editors' (matches UnityInstallRoot prefix)", () => {
    const result = runHarness(
      "Write-Output ([bool](Test-DefenderExclusionPathAllowed -Path 'C:\\Unity\\Editors' -UnityInstallRoot 'C:\\Unity\\Editors'))"
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("True");
  });

  test("Test-DefenderExclusionPathAllowed accepts 'D:\\actions-runner\\_work\\foo' (workspace fragment)", () => {
    const result = runHarness(
      "Write-Output ([bool](Test-DefenderExclusionPathAllowed -Path 'D:\\actions-runner\\_work\\foo' -UnityInstallRoot 'C:\\Unity\\Editors'))"
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("True");
  });

  test("Test-DefenderExclusionPathAllowed rejects 'C:\\WINDOWS' (not under UnityInstallRoot, no _work fragment)", () => {
    const result = runHarness(
      "Write-Output ([bool](Test-DefenderExclusionPathAllowed -Path 'C:\\WINDOWS' -UnityInstallRoot 'C:\\Unity\\Editors'))"
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("False");
  });

  test("Test-DefenderExclusionPathAllowed rejects '\\\\server\\share' (UNC root without subpath)", () => {
    // UNC roots like `\\server\share` would exclude an entire network share;
    // mirroring the single-drive reject for the UNC case. A `\\server\share\sub`
    // path is OK because the third segment proves a subpath was supplied.
    const result = runHarness(
      "Write-Output ([bool](Test-DefenderExclusionPathAllowed -Path '\\\\server\\share' -UnityInstallRoot 'C:\\Unity\\Editors'))"
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("False");
  });

  test("Test-VcRedistModernFilesOnDisk on Linux returns the vacuous-OK shape (present=$true)", () => {
    if (!ON_NON_WINDOWS) {
      // On Windows this depends on the host install; not asserted here.
      return;
    }
    // Non-Windows hosts have no System32 dir; the helper SHORT-CIRCUITS with
    // `present = $true; missing = @()` so callers can compose it without
    // crashing on Linux (this same pattern is exercised by the production-
    // contract dot-source check). The early-return is load-bearing for the
    // dot-source test harness on Linux pwsh.
    const result = runHarness(
      [
        "$r = Test-VcRedistModernFilesOnDisk",
        "Write-Output ('present=' + [bool]$r.present)",
        "Write-Output ('missing.count=' + ($r.missing.Count))"
      ].join("\n")
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    const out = (result.stdout || "").trim();
    expect(out).toContain("present=True");
    expect(out).toContain("missing.count=0");
  });

  test("Test-VcRedist2010FilesOnDisk on Linux returns the vacuous-OK shape (present=$true)", () => {
    if (!ON_NON_WINDOWS) {
      // On Windows this depends on the host install; not asserted here
      // (the file probe is OS-dependent and we don't want this test to
      // become a host-state-dependent oracle on a real Windows runner).
      return;
    }
    // Mirror of the modern probe's Linux behavior: non-Windows hosts have
    // no System32 dir so the helper MUST short-circuit with the vacuous-OK
    // shape (present=$true; missing=@()) so callers can compose it without
    // crashing on Linux. The early-return is load-bearing for the dot-source
    // test harness AND for any future Linux-side dispatcher test (the
    // dispatcher already short-circuits on non-Windows but Test-IsWindowsHost
    // is the only thing keeping the file probes safe to dot-source on Linux).
    const result = runHarness(
      [
        "$r = Test-VcRedist2010FilesOnDisk",
        "Write-Output ('present=' + [bool]$r.present)",
        "Write-Output ('missing.count=' + ($r.missing.Count))"
      ].join("\n")
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    const out = (result.stdout || "").trim();
    expect(out).toContain("present=True");
    expect(out).toContain("missing.count=0");
  });

  test("Test-VcRedist2010Installed on Linux returns @{installed=$true; reason=...} (vacuous OK)", () => {
    if (!ON_NON_WINDOWS) {
      // On Windows the result depends on the host install; not asserted here.
      return;
    }
    // The composite Test-VcRedist2010Installed must short-circuit to the
    // vacuous-OK shape on non-Windows so the dispatcher's DetectFn on Linux
    // returns truthy (the dispatcher uses
    //   { ([bool]((Test-VcRedist2010Installed).installed)) }
    // as the detection scriptblock, which must NOT throw on Linux when the
    // bootstrap is dot-sourced or run in DetectOnly mode for parity tests).
    const result = runHarness(
      [
        "$r = Test-VcRedist2010Installed",
        "Write-Output ('installed=' + [bool]$r.installed)",
        // The reason is a string -- assert it is non-null/non-empty so a
        // mutation that nulled it out would be caught.
        "Write-Output ('reason-nonempty=' + (-not [string]::IsNullOrWhiteSpace([string]$r.reason)))"
      ].join("\n")
    );
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    const out = (result.stdout || "").trim();
    expect(out).toContain("installed=True");
    expect(out).toContain("reason-nonempty=True");
  });

  test("Test-LongPathsEnabled honors DXM_UNITY_FAKE_LONGPATHS_ENABLED on every OS", () => {
    // The test override is symmetric with the override in ensure-editor.ps1;
    // exercising it here lets a future Linux-side test verify a Windows-only
    // branch deterministically. We MUST scope the env-var mutation so the
    // suite's other tests do not see it leak (we pass the env to spawnSync).
    const escapedScript = BOOTSTRAP_SCRIPT.replace(/'/g, "''");
    const workspace = makeWorkspace();
    const harness = path.join(workspace, "harness.ps1");
    fs.writeFileSync(
      harness,
      [`. '${escapedScript}'`, "Write-Output ([bool](Test-LongPathsEnabled))"].join("\n"),
      "utf8"
    );

    // Override = '1' -> $true (regardless of OS).
    const trueResult = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harness], {
      encoding: "utf8",
      maxBuffer: 16 * 1024 * 1024,
      env: { ...process.env, DXM_UNITY_FAKE_LONGPATHS_ENABLED: "1" }
    });
    assertSpawnStatus(trueResult, 0, expect.getState().currentTestName || "pwsh harness");
    expect((trueResult.stdout || "").trim()).toBe("True");

    // Override = '0' -> $false (regardless of OS).
    const falseResult = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harness], {
      encoding: "utf8",
      maxBuffer: 16 * 1024 * 1024,
      env: { ...process.env, DXM_UNITY_FAKE_LONGPATHS_ENABLED: "0" }
    });
    assertSpawnStatus(falseResult, 0, expect.getState().currentTestName || "pwsh harness");
    expect((falseResult.stdout || "").trim()).toBe("False");
  });
});

// Helper: dot-source the bootstrap, run `body`, with arbitrary extra env. Mirrors
// `runHarness` but threads through env-var overrides (the LongPaths test does
// this inline; centralizing keeps the new tiering assertions terse).
function runHarnessWithEnv(body, extraEnv) {
  const escapedScript = BOOTSTRAP_SCRIPT.replace(/'/g, "''");
  const workspace = makeWorkspace();
  const harness = path.join(workspace, "harness.ps1");
  fs.writeFileSync(harness, [`. '${escapedScript}'`, body].join("\n"), "utf8");
  return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harness], {
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024,
    env: { ...process.env, ...extraEnv }
  });
}

describe("bootstrap-windows-runner.ps1 best-effort tiering (Defender skip on non-admin)", () => {
  // Behavioral coverage of the 2026-05-26 production regression (run
  // 70852733615). Static surface assertions live in unity-runner-host-prereq-
  // contract.test.js; THIS suite exercises the helpers via dot-source-and-call
  // so a future refactor that keeps the static tokens intact but inverts the
  // runtime branch (e.g. accidentally returning $true from Test-IsAdministrator
  // when DXM_RUNNER_FAKE_IS_ADMIN='0') still fails loudly. Mutation-tested
  // during the round-2 adversarial review (D1).
  //
  // EVERY assertion here relies on DXM_RUNNER_FAKE_IS_ADMIN, the same-spirit
  // hermeticity override as DXM_UNITY_FAKE_LONGPATHS_ENABLED -- a test-only
  // knob that lets the non-admin branch be exercised deterministically on
  // any OS without a real admin / non-admin shell.

  test("Test-IsAdministrator honors DXM_RUNNER_FAKE_IS_ADMIN=0 (returns $false)", () => {
    if (!PWSH_PRESENT) {
      return;
    }
    const result = runHarnessWithEnv("Write-Output ([bool](Test-IsAdministrator))", {
      DXM_RUNNER_FAKE_IS_ADMIN: "0"
    });
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("False");
  });

  test("Test-IsAdministrator honors DXM_RUNNER_FAKE_IS_ADMIN=1 (returns $true)", () => {
    if (!PWSH_PRESENT) {
      return;
    }
    const result = runHarnessWithEnv("Write-Output ([bool](Test-IsAdministrator))", {
      DXM_RUNNER_FAKE_IS_ADMIN: "1"
    });
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect((result.stdout || "").trim()).toBe("True");
  });

  test("Invoke-DefenderBootstrap short-circuits to skipped-non-admin when non-admin", () => {
    // The exact production scenario from run 70852733615: bootstrap runs as
    // NETWORK SERVICE (non-admin). Before the fix, Invoke-DefenderBootstrap
    // attempted Add-MpPreference, failed the admin pre-check, and returned
    // finalState='install-failed' -- which the dispatcher (incorrectly)
    // treated as a critical failure. After the fix, this branch returns
    // skipped-non-admin + critical=$false BEFORE any state mutation is
    // attempted.
    if (!PWSH_PRESENT) {
      return;
    }
    const body = [
      "$result = Invoke-DefenderBootstrap -Paths @('C:\\Unity\\Editors')",
      'Write-Output "name=$($result.name)"',
      'Write-Output "finalState=$($result.finalState)"',
      'Write-Output "critical=$($result.critical)"'
    ].join("\n");
    const result = runHarnessWithEnv(body, { DXM_RUNNER_FAKE_IS_ADMIN: "0" });
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    const stdout = (result.stdout || "").trim();
    expect(stdout).toContain("name=defender-exclusion");
    expect(stdout).toContain("finalState=skipped-non-admin");
    expect(stdout).toContain("critical=False");
  });

  test("Invoke-BootstrapStep -Critical $false propagates the flag to its result", () => {
    // Pins the contract surface for future prereqs added via the generic
    // wrapper. A future maintainer who adds a new best-effort prereq must
    // be able to pass -Critical $false and trust that the dispatcher sees
    // it. Mutation-tested: flipping the default to $false would still pass
    // an explicit $false test, so we also need the implicit-$true test
    // below for full coverage.
    if (!PWSH_PRESENT) {
      return;
    }
    const body = [
      "$r1 = Invoke-BootstrapStep -Name 'fake-best-effort' -DetectFn { $true } -InstallFn { @{} } -Critical $false",
      "$r2 = Invoke-BootstrapStep -Name 'fake-critical-explicit' -DetectFn { $true } -InstallFn { @{} } -Critical $true",
      "$r3 = Invoke-BootstrapStep -Name 'fake-critical-default' -DetectFn { $true } -InstallFn { @{} }",
      'Write-Output "r1.critical=$($r1.critical)"',
      'Write-Output "r2.critical=$($r2.critical)"',
      'Write-Output "r3.critical=$($r3.critical)"'
    ].join("\n");
    const result = runHarnessWithEnv(body, {});
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    const stdout = (result.stdout || "").trim();
    expect(stdout).toContain("r1.critical=False");
    expect(stdout).toContain("r2.critical=True");
    expect(stdout).toContain("r3.critical=True");
  });

  test("Format-BootstrapSummary marks non-critical entries with '*' suffix", () => {
    // The summary-line legend is the operator-visible signal that a
    // best-effort prereq failed without gating the cell. Mutation-tested:
    // dropping the suffix logic produces a summary line indistinguishable
    // from a critical-failure scenario, which would defeat the operator UX
    // benefit of the tier flag.
    if (!PWSH_PRESENT) {
      return;
    }
    const body = [
      "$mixed = @(",
      "  @{ name = 'critical-ok'; finalState = 'ok'; critical = $true },",
      "  @{ name = 'best-effort-skipped'; finalState = 'skipped-non-admin'; critical = $false }",
      ")",
      "Format-BootstrapSummary -Results $mixed"
    ].join("\n");
    const result = runHarnessWithEnv(body, {});
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    const out = combined(result);
    // Critical entries have NO suffix; best-effort entries have '*'.
    expect(out).toMatch(/critical-ok=ok(?!\*)/);
    expect(out).toMatch(/best-effort-skipped=skipped-non-admin\*/);
  });
});
