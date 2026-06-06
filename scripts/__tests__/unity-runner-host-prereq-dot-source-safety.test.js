/**
 * @fileoverview Behavioral guard for the dot-source contract of
 * scripts/unity/bootstrap-windows-runner.ps1. Spawns pwsh to verify that:
 *
 *   (a) dot-sourcing the bootstrap script does NOT mutate the caller's
 *       session preferences: $ErrorActionPreference, Set-StrictMode, and
 *       $PSNativeCommandUseErrorActionPreference all stay at their pre-load
 *       values; the script reserves those mutations EXCLUSIVELY for the
 *       `if ($invokedAsScript)` dispatcher block at the bottom of the file.
 *   (b) Invoking the script with `-DetectOnly` on a non-Windows host exits
 *       0 and emits a `::notice::` containing "skipping" / "not Windows"
 *       (so tests + composites can call -DetectOnly cross-platform without
 *       short-circuiting on a hard error).
 *   (c) Invoking the script WITHOUT -DetectOnly on a non-Windows host exits
 *       1 and emits a `::error::` whose message contains "Windows-only"
 *       (so a misrouted dispatch to a Linux/macOS runner fails LOUDLY rather
 *       than silently no-op'ing).
 *
 * The static-contract test file pins the SOURCE shape; this file pins the
 * runtime BEHAVIOR. Both are necessary because a refactor could keep the
 * source tokens but change the behavior (e.g. moving a Set-StrictMode out
 * of the dispatcher would still source-match if it landed inside a function
 * body, yet the LIVE dot-source would still be safe). The two halves cross-
 * check each other.
 *
 * @cross-platform-regression -- this marker gates the file on the
 * ubuntu/windows/macos targeted-attribution step of
 * .github/workflows/cross-platform-preflight.yml; enforced by
 * scripts/__tests__/cross-platform-preflight-coverage.test.js. Platform
 * divergence is the WHOLE POINT (the Linux path emits a notice + exits 0,
 * the Windows path runs the real install paths), so first-failure
 * attribution on every OS is warranted.
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
    "[unity-runner-host-prereq-dot-source-safety] pwsh not found on PATH; skipping behavioral assertions. CI runners always have pwsh."
  );
}

// Combined stdout/stderr for assertion phrases. Mirrors the pwsh-output
// helper's combinedText semantics (we don't import it here because this
// suite needs only a thin string concatenation, not the wrap-normalization).
function combined(result) {
  return ((result.stdout || "") + "\n" + (result.stderr || "")).replace(/\r\n/g, "\n");
}

// Workspaces created by the suite; cleaned up in afterAll.
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

function makeWorkspace() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-bootstrap-dot-source-"));
  workspaces.push(dir);
  return dir;
}

function runPwshCommand(command) {
  return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command], {
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024
  });
}

function runPwshFile(args, env = process.env) {
  return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", BOOTSTRAP_SCRIPT, ...args], {
    env,
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024
  });
}

describe("scripts/unity/bootstrap-windows-runner.ps1 dot-source safety", () => {
  // Always runs: a rename/move cannot silently no-op the guard.
  test("the script under test exists", () => {
    expect(fs.existsSync(BOOTSTRAP_SCRIPT)).toBe(true);
  });

  if (!PWSH_PRESENT) {
    // Register skip placeholders so the suite reports a stable test count
    // even when pwsh is unavailable.
    test.skip("dot-source does NOT change $ErrorActionPreference", () => {});
    test.skip("dot-source does NOT change Set-StrictMode level", () => {});
    test.skip("-DetectOnly on non-Windows exits 0 + emits a 'skipping non-Windows' notice", () => {});
    test.skip("(no -DetectOnly) on non-Windows exits 1 + emits a 'Windows-only' error", () => {});
    return;
  }

  test("dot-source does NOT change $ErrorActionPreference", () => {
    // Caller sets EAP to 'Continue' (the default). Dot-source. Confirm EAP
    // is STILL 'Continue'. If the bootstrap script's $ErrorActionPreference
    // = 'Stop' assignment leaks out of the dispatcher (i.e. lives at the
    // top level), this assertion fails.
    const escapedScript = BOOTSTRAP_SCRIPT.replace(/'/g, "''");
    const script = [
      "$ErrorActionPreference = 'Continue'",
      "$before = $ErrorActionPreference",
      `. '${escapedScript}'`,
      "$after = $ErrorActionPreference",
      "if ($before -ne $after) {",
      '  Write-Host "FAIL: EAP leaked: before=$before after=$after"',
      "  exit 1",
      "}",
      "Write-Host 'OK'",
      "exit 0"
    ].join("; ");

    const result = runPwshCommand(script);
    const combinedOut = combined(result);
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect(combinedOut).toContain("OK");
    expect(combinedOut).not.toContain("FAIL");
  });

  test("dot-source does NOT change Set-StrictMode level", () => {
    // PowerShell exposes the active StrictMode level via $StrictModeVersion
    // ($null when StrictMode is off). We START WITH StrictMode OFF, dot-
    // source, and assert StrictMode is STILL OFF. If `Set-StrictMode -Version
    // Latest` leaks out of the dispatcher, $PSStrictModeVersion will be set
    // and the assertion fails.
    const escapedScript = BOOTSTRAP_SCRIPT.replace(/'/g, "''");
    // Use a script-file form rather than `-Command` because we need a
    // multi-statement body and `$Host.Runspace.SessionStateProxy` to read
    // the strict-mode state. The simplest cross-version check is to invoke
    // a function that uses `$null.Foo` -- under any StrictMode level that
    // throws -- and confirm it does NOT throw before dot-source AND does
    // NOT throw after dot-source.
    const workspace = makeWorkspace();
    const harness = path.join(workspace, "harness.ps1");
    fs.writeFileSync(
      harness,
      [
        "# Start with StrictMode OFF (default).",
        "$preLeak = $null",
        "try { $null = ($null).NoSuchProperty } catch { $preLeak = $_.Exception.Message }",
        "if ($null -ne $preLeak) {",
        '  Write-Host "FAIL_PRE: StrictMode was already on before dot-source: $preLeak"',
        "  exit 1",
        "}",
        `. '${escapedScript}'`,
        "$postLeak = $null",
        "try { $null = ($null).NoSuchProperty } catch { $postLeak = $_.Exception.Message }",
        "if ($null -ne $postLeak) {",
        '  Write-Host "FAIL_POST: StrictMode leaked from bootstrap dot-source: $postLeak"',
        "  exit 1",
        "}",
        "Write-Host 'OK'",
        "exit 0"
      ].join("\n"),
      "utf8"
    );

    const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harness], {
      encoding: "utf8",
      maxBuffer: 16 * 1024 * 1024
    });
    const combinedOut = combined(result);
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect(combinedOut).toContain("OK");
    expect(combinedOut).not.toContain("FAIL");
  });

  test("dot-source does NOT change $PSNativeCommandUseErrorActionPreference", () => {
    // PS 5.1 lacks this automatic variable; on pwsh 7+ it defaults to $true
    // (unless StrictMode-Latest in the host has masked it). We capture the
    // pre-load value and re-capture it post-dot-source.
    const escapedScript = BOOTSTRAP_SCRIPT.replace(/'/g, "''");
    const workspace = makeWorkspace();
    const harness = path.join(workspace, "harness.ps1");
    fs.writeFileSync(
      harness,
      [
        "# Capture the pre-load value (may be $null on PS 5.1).",
        "$before = $null",
        "try { $before = Get-Variable -Name 'PSNativeCommandUseErrorActionPreference' -ValueOnly -ErrorAction Stop } catch { }",
        `. '${escapedScript}'`,
        "$after = $null",
        "try { $after = Get-Variable -Name 'PSNativeCommandUseErrorActionPreference' -ValueOnly -ErrorAction Stop } catch { }",
        // Null parity is fine (both unset == OK); otherwise the two must agree.
        "if ($before -ne $after) {",
        "  Write-Host \"FAIL: PSNCUEAP leaked: before='$before' after='$after'\"",
        "  exit 1",
        "}",
        "Write-Host 'OK'",
        "exit 0"
      ].join("\n"),
      "utf8"
    );

    const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harness], {
      encoding: "utf8",
      maxBuffer: 16 * 1024 * 1024
    });
    const combinedOut = combined(result);
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect(combinedOut).toContain("OK");
    expect(combinedOut).not.toContain("FAIL");
  });

  // Platform-gate behavioral tests: ONLY exercise the non-Windows side
  // when actually on a non-Windows host. On a real Windows runner these
  // tests' fixtures would attempt real registry probes and we don't want
  // that side effect from a unit test (the runner-bootstrap.yml workflow
  // already exercises the Windows path end-to-end on the actual runner).
  const ON_NON_WINDOWS = process.platform !== "win32";

  // eslint-disable-next-line jest/no-conditional-expect
  if (ON_NON_WINDOWS) {
    test("-DetectOnly on non-Windows exits 0 + emits a 'skipping' / 'not Windows' notice", () => {
      const result = runPwshFile(["-DetectOnly"]);
      const combinedOut = combined(result);
      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      // Either phrase is acceptable; the message must NAME the non-Windows
      // skip outcome so the operator (or composite) can grep on it.
      expect(combinedOut).toMatch(/::notice::/);
      expect(combinedOut.toLowerCase()).toMatch(/skipping|non-windows|not windows/);
      // It MUST NOT emit an ::error:: on this path (DetectOnly is the safe
      // mode and a non-Windows host is a vacuous OK).
      expect(combinedOut).not.toMatch(/::error::/);
    });

    test("(no -DetectOnly) on non-Windows exits 1 + emits a 'Windows-only' error", () => {
      const result = runPwshFile([]);
      const combinedOut = combined(result);
      // The dispatcher hard-fails: not a Windows host AND not in DetectOnly
      // mode means a misrouted dispatch -- this MUST fail loudly.
      assertSpawnStatus(result, 1, expect.getState().currentTestName || "pwsh harness");
      expect(combinedOut).toMatch(/::error::/);
      expect(combinedOut).toMatch(/Windows-only/);
    });
  } else {
    test.skip("-DetectOnly on non-Windows exits 0 + emits a 'skipping' / 'not Windows' notice", () => {});
    test.skip("(no -DetectOnly) on non-Windows exits 1 + emits a 'Windows-only' error", () => {});
  }
});
