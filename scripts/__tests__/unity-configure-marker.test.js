/**
 * @fileoverview Cross-platform data-driven Jest test for `Test-UnityConfigureMarker`
 * in scripts/unity/run-ci-tests.ps1 -- the source-of-truth gate for the standalone
 * CONFIGURE pass.
 *
 * WHY THIS EXISTS:
 *   The Unity 6000.3 standalone CONFIGURE pass in CI run 72225120030 crashed in a
 *   background thread (DirectoryMonitor) DURING shutdown, AFTER
 *   DxmCiTestConfigurator.Apply completed, exiting 0xC0000005. The runner now gates
 *   that pass on a SUCCESS MARKER Apply writes as its final action, not on the
 *   process exit code: a FRESH marker means the configuration succeeded (honor it
 *   even on a crash exit), a MISSING marker means Apply did not complete (fail), and
 *   a STALE marker (older than this run) must not be mistaken for success.
 *
 *   The end-to-end standalone scenarios in unity-runner-strictmode-smoke.test.js are
 *   win32-skipped (the build/player stubs must be Process.Start-launchable PE files),
 *   so the marker validation -- the exact logic guarding the bug's real Windows repro
 *   -- would otherwise be exercised only on Linux/macOS. This test isolates the pure
 *   `Test-UnityConfigureMarker` function and drives its fresh / stale / missing
 *   branches on EVERY platform (including win32) by extracting the function source and
 *   running it in a fresh pwsh, the same technique as
 *   unity-accelerator-endpoint-normalization.test.js.
 *
 *   pwsh is preinstalled on the CI runners; locally the per-case sub-tests skip when
 *   pwsh is absent. An always-on sanity test proves the function was found so a
 *   rename/move cannot silently turn this guard into a no-op.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const { assertSpawnStatus, combinedText } = require("../lib/pwsh-output");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const RUN_CI_TESTS = path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1");

const SCRIPT_TEXT = fs.existsSync(RUN_CI_TESTS) ? fs.readFileSync(RUN_CI_TESTS, "utf8") : "";

/**
 * Extract a top-level `function <name> { ... }` bounded at the next top-level
 * `\nfunction `. Mirrors unity-runner-script-contract.test.js's slicing.
 */
function extractFunction(scriptText, functionName) {
  const start = scriptText.indexOf(`function ${functionName}`);
  if (start < 0) {
    return "";
  }
  const after = scriptText.indexOf("\nfunction ", start + 1);
  return after === -1 ? scriptText.slice(start) : scriptText.slice(start, after);
}

const FUNCTION_SOURCE = extractFunction(SCRIPT_TEXT, "Test-UnityConfigureMarker");

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

/**
 * Spawn pwsh, define the extracted function via Invoke-Expression under the same
 * StrictMode the production script uses, set up the marker per `scenario`, and call
 * the function. The marker mtime is controlled relative to a fixed StartedUtc so the
 * 5-second freshness window is exercised deterministically (no real sleeps). Emits
 * exactly one line: `OK` (no problem) or `PROBLEM:<reason>`.
 *
 * scenarios:
 *   missing -> do not create the marker (Apply never completed)
 *   fresh   -> marker written AFTER StartedUtc (the normal success: a benign crash
 *              may follow, but the marker proves Apply ran to completion)
 *   stale   -> marker mtime well BEFORE StartedUtc - 5s (a prior run's leftover)
 *   boundary-> marker mtime exactly StartedUtc - 4s (inside the 5s tolerance => fresh)
 */
function runMarker(scenario) {
  const program = [
    "Set-StrictMode -Version Latest",
    "$ErrorActionPreference = 'Stop'",
    "Invoke-Expression $env:DXM_MARKER_SOURCE",
    "$markerPath = $env:DXM_MARKER_PATH",
    "$scenario = $env:DXM_MARKER_SCENARIO",
    // Fixed reference time so the freshness window is deterministic.
    "$started = [DateTime]::UtcNow",
    "if ($scenario -eq 'fresh') {",
    "  Set-Content -LiteralPath $markerPath -Value 'x' -Encoding UTF8",
    "  (Get-Item -LiteralPath $markerPath).LastWriteTimeUtc = $started.AddSeconds(2)",
    "} elseif ($scenario -eq 'stale') {",
    "  Set-Content -LiteralPath $markerPath -Value 'x' -Encoding UTF8",
    "  (Get-Item -LiteralPath $markerPath).LastWriteTimeUtc = $started.AddSeconds(-60)",
    "} elseif ($scenario -eq 'boundary') {",
    "  Set-Content -LiteralPath $markerPath -Value 'x' -Encoding UTF8",
    "  (Get-Item -LiteralPath $markerPath).LastWriteTimeUtc = $started.AddSeconds(-4)",
    "}",
    "$r = Test-UnityConfigureMarker -MarkerPath $markerPath -StartedUtc $started",
    "if ($r -eq '') { Write-Output 'OK' } else { Write-Output ('PROBLEM:' + $r) }"
  ].join("\n");

  const markerPath = path.join(makeTempDir("marker"), "configure-complete.marker");

  const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", program], {
    env: {
      ...process.env,
      DXM_MARKER_SOURCE: FUNCTION_SOURCE,
      DXM_MARKER_PATH: markerPath,
      DXM_MARKER_SCENARIO: scenario
    },
    encoding: "utf8",
    maxBuffer: 8 * 1024 * 1024
  });
  cleanupDir(path.dirname(markerPath));
  return result;
}

const CASES = [
  { label: "fresh marker (this run) -> success", scenario: "fresh", expectOk: true },
  { label: "marker mtime within 5s tolerance -> success", scenario: "boundary", expectOk: true },
  {
    label: "missing marker -> 'was not written'",
    scenario: "missing",
    expectOk: false,
    contains: "was not written"
  },
  {
    label: "stale marker (prior run) -> 'stale configure marker'",
    scenario: "stale",
    expectOk: false,
    contains: "stale configure marker"
  }
];

describe("run-ci-tests.ps1 Test-UnityConfigureMarker (configure source-of-truth gate)", () => {
  test("the function is extractable from the script", () => {
    expect(SCRIPT_TEXT).not.toBe("");
    expect(FUNCTION_SOURCE).toContain("function Test-UnityConfigureMarker");
    expect(FUNCTION_SOURCE).toContain("LastWriteTimeUtc");
    expect(FUNCTION_SOURCE).toContain("AddSeconds(-5)");
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[configure-marker] pwsh not found on PATH; skipping execution cases (CI runners have pwsh)."
    );
    test.skip.each(CASES)("$label", () => {});
    return;
  }

  test.each(CASES)("$label", ({ scenario, expectOk, contains }) => {
    const result = runMarker(scenario);
    const out = combinedText(result);
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    if (expectOk) {
      expect(out).toContain("OK");
      expect(out).not.toContain("PROBLEM:");
    } else {
      expect(out).toContain(`PROBLEM:`);
      expect(out).toContain(contains);
    }
  });
});
