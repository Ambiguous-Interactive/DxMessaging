/**
 * @fileoverview Data-driven Jest test for the native exit-code decoder in
 * scripts/unity/run-ci-tests.ps1 (the `$script:NativeExitCodeDescriptions` table
 * plus `ConvertTo-UnsignedExitHex`, `Test-NativeCrashExitCode`, and
 * `Get-NativeExitCodeDescription`).
 *
 * WHY THIS EXISTS:
 *   The Unity 6000.3 standalone CONFIGURE pass in CI run 72225120030 crashed in
 *   a background thread (the DirectoryMonitor file-watcher) DURING shutdown,
 *   AFTER DxmCiTestConfigurator.Apply completed, and exited with the native
 *   Windows access-violation status 0xC0000005. PowerShell surfaces that as the
 *   NEGATIVE Int32 -1073741819, so a naive numeric comparison against the
 *   `0xC0000005` token (which PowerShell ALSO parses as a negative Int32) is a
 *   silent int/uint conflation. The decoder normalizes to the canonical unsigned
 *   8-char hex and looks the status up in a single-source-of-truth table; this
 *   test pins both the conversion and the table with a data-driven case set so a
 *   regression cannot reintroduce the conflation or drop a status.
 *
 *   The benign-shutdown-crash TOLERANCE (the artifact, not the exit code, decides
 *   pass/fail) is proven end-to-end by unity-runner-strictmode-smoke.test.js;
 *   this test isolates the DIAGNOSTIC decoder so the exact bug exit code is pinned
 *   to its human-readable name regardless of process-exit truncation (POSIX exit
 *   codes are 8-bit, so a real 0xC0000005 process exit is only reproducible on
 *   Windows -- this test feeds the integer directly).
 *
 * IMPLEMENTATION NOTES:
 *   We extract the contiguous source region holding the table + the three pure
 *   functions and run it in a fresh pwsh via Invoke-Expression (the same
 *   technique as unity-accelerator-endpoint-normalization.test.js). Each case
 *   emits exactly one line `DESC:<...>;CRASH:<True|False>`, normalized through
 *   combinedText so Windows ConciseView word-wrap cannot break the assertions.
 *   pwsh is preinstalled on the CI runners; locally the per-case sub-tests skip
 *   when pwsh is absent. An always-on sanity test proves the region was found, so
 *   a rename/move cannot silently turn this guard into a no-op.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const { assertSpawnStatus, combinedText } = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const RUN_CI_TESTS = path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1");

const SCRIPT_TEXT = fs.existsSync(RUN_CI_TESTS) ? fs.readFileSync(RUN_CI_TESTS, "utf8") : "";

/**
 * Extract the contiguous block from the `$script:NativeExitCodeDescriptions`
 * table assignment through the end of `Get-NativeExitCodeDescription` (bounded
 * at the next top-level `\nfunction `). Returns "" when not found.
 */
function extractDecoderSource(scriptText) {
  const start = scriptText.indexOf("$script:NativeExitCodeDescriptions = [ordered]@{");
  if (start < 0) {
    return "";
  }
  const getDesc = scriptText.indexOf("function Get-NativeExitCodeDescription", start);
  if (getDesc < 0) {
    return "";
  }
  const after = scriptText.indexOf("\nfunction ", getDesc + 1);
  return after === -1 ? scriptText.slice(start) : scriptText.slice(start, after);
}

const DECODER_SOURCE = extractDecoderSource(SCRIPT_TEXT);

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

/**
 * Spawn pwsh, define the extracted decoder via Invoke-Expression under the same
 * StrictMode the production script uses, and decode the given integer exit code.
 * Emits exactly one line: `DESC:<description>;CRASH:<True|False>`.
 */
function decode(code) {
  const program = [
    "Set-StrictMode -Version Latest",
    "$ErrorActionPreference = 'Stop'",
    "Invoke-Expression $env:DXM_DECODE_SOURCE",
    "$code = [int]$env:DXM_DECODE_CODE",
    "$desc = Get-NativeExitCodeDescription -ExitCode $code",
    "$crash = Test-NativeCrashExitCode -ExitCode $code",
    "Write-Output ('DESC:' + $desc + ';CRASH:' + $crash)"
  ].join("\n");

  return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", program], {
    env: {
      ...process.env,
      DXM_DECODE_SOURCE: DECODER_SOURCE,
      DXM_DECODE_CODE: String(code)
    },
    encoding: "utf8",
    maxBuffer: 8 * 1024 * 1024
  });
}

// code: the integer PowerShell yields for the process exit (negative Int32 for a
// high-bit NTSTATUS, exactly as $LASTEXITCODE reports it). desc: the expected
// Get-NativeExitCodeDescription output. crash: expected Test-NativeCrashExitCode.
const CASES = [
  // THE BUG: 0xC0000005 surfaces as -1073741819. This is the exact configure-pass
  // crash exit code from CI run 72225120030.
  {
    label: "0xC0000005 ACCESS_VIOLATION (the bug)",
    code: -1073741819,
    desc: "0xC0000005 / STATUS_ACCESS_VIOLATION",
    crash: true
  },
  {
    label: "0xC000001D ILLEGAL_INSTRUCTION",
    code: -1073741795,
    desc: "0xC000001D / STATUS_ILLEGAL_INSTRUCTION",
    crash: true
  },
  {
    label: "0xC0000017 NO_MEMORY",
    code: -1073741801,
    desc: "0xC0000017 / STATUS_NO_MEMORY",
    crash: true
  },
  {
    label: "0xC00000FD STACK_OVERFLOW",
    code: -1073741571,
    desc: "0xC00000FD / STATUS_STACK_OVERFLOW",
    crash: true
  },
  {
    label: "0xC0000135 DLL_NOT_FOUND",
    code: -1073741515,
    desc: "0xC0000135 / STATUS_DLL_NOT_FOUND",
    crash: true
  },
  {
    label: "0xC0000139 ENTRYPOINT_NOT_FOUND",
    code: -1073741511,
    desc: "0xC0000139 / STATUS_ENTRYPOINT_NOT_FOUND",
    crash: true
  },
  {
    label: "0xC0000374 HEAP_CORRUPTION",
    code: -1073740940,
    desc: "0xC0000374 / STATUS_HEAP_CORRUPTION",
    crash: true
  },
  {
    label: "0xC0000409 STACK_BUFFER_OVERRUN",
    code: -1073740791,
    desc: "0xC0000409 / STATUS_STACK_BUFFER_OVERRUN",
    crash: true
  },
  {
    label: "0xC0000420 ASSERTION_FAILURE",
    code: -1073740768,
    desc: "0xC0000420 / STATUS_ASSERTION_FAILURE",
    crash: true
  },
  // A 0xC000xxxx status NOT in the table: decoded to bare hex, still classified a
  // crash (the 0xC000xxxx family => STATUS_SEVERITY_ERROR).
  {
    label: "0xC0000022 (unlisted crash status)",
    code: -1073741790,
    desc: "0xC0000022",
    crash: true
  },
  // A NON-error-severity NTSTATUS (top nibble 4 => STATUS_SEVERITY_INFORMATIONAL):
  // decoded to bare hex and NOT classified a crash.
  {
    label: "0x40000015 (informational status)",
    code: 1073741845,
    desc: "0x40000015",
    crash: false
  },
  // Ordinary application exit codes (0..255): bare hex, NOT crash codes.
  { label: "0 clean exit", code: 0, desc: "0x00000000", crash: false },
  { label: "1 generic failure", code: 1, desc: "0x00000001", crash: false },
  { label: "2 test-failure code", code: 2, desc: "0x00000002", crash: false },
  { label: "124 watchdog timeout sentinel", code: 124, desc: "0x0000007C", crash: false },
  { label: "134 stub abort code", code: 134, desc: "0x00000086", crash: false }
];

describe("run-ci-tests.ps1 native exit-code decoder", () => {
  test("the decoder region is extractable from the script", () => {
    expect(SCRIPT_TEXT).not.toBe("");
    expect(DECODER_SOURCE).toContain("$script:NativeExitCodeDescriptions");
    expect(DECODER_SOURCE).toContain("function ConvertTo-UnsignedExitHex");
    expect(DECODER_SOURCE).toContain("function Test-NativeCrashExitCode");
    expect(DECODER_SOURCE).toContain("function Get-NativeExitCodeDescription");
    // The single-source-of-truth table must carry the exact-bug status.
    expect(DECODER_SOURCE).toContain("'C0000005' = 'STATUS_ACCESS_VIOLATION'");
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[native-exit-code-decode] pwsh not found on PATH; skipping execution cases (CI runners have pwsh)."
    );
    test.skip.each(CASES)("$label -> $desc (crash=$crash)", () => {});
    return;
  }

  test.each(CASES)("$label -> $desc (crash=$crash)", ({ code, desc, crash }) => {
    const result = decode(code);
    const out = combinedText(result);
    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect(out).toContain(`DESC:${desc};CRASH:${crash ? "True" : "False"}`);
  });
});
