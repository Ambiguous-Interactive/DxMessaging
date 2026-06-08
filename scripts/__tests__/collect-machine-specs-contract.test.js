/**
 * @fileoverview Source-scanning contract test for collect-machine-specs.ps1.
 *
 * The script runs on the self-hosted Windows perf runner and emits HARDWARE
 * specs so the performance doc can attribute numbers to a machine profile
 * WITHOUT leaking the runner's identity. Because the probes need real CIM/WMI on
 * Windows, this test is SOURCE-SCAN based (like the other *contract* tests): it
 * asserts the script (a) queries the 4 Win32_* CIM classes, (b) emits JSON + a
 * one-line summary, (c) NEVER references the host name / runner name / serial /
 * MAC (privacy -- the whole point is to REPLACE the runner name), (d) always
 * exits 0, and (e) is StrictMode / Windows PowerShell 5.1 safe (no ?? / ?:).
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SCRIPT_PATH = path.join(REPO_ROOT, "scripts", "unity", "collect-machine-specs.ps1");

// Strip PowerShell comments so the "no ?? / no ternary" syntax-safety scan looks
// at CODE only -- the synopsis legitimately mentions "?? / ?:" in prose, and that
// documentation must not trip the operator check. Removes <# ... #> block
// comments first, then any `#`-to-end-of-line comment (the script has no `#`
// inside string literals, so a simple line strip is safe here).
function stripPowerShellComments(source) {
  const withoutBlocks = source.replace(/<#[\s\S]*?#>/g, " ");
  return withoutBlocks
    .split(/\r?\n/)
    .map((line) => {
      const hashIndex = line.indexOf("#");
      return hashIndex === -1 ? line : line.slice(0, hashIndex);
    })
    .join("\n");
}

describe("collect-machine-specs.ps1 contract", () => {
  let content;

  beforeAll(() => {
    expect(fs.existsSync(SCRIPT_PATH)).toBe(true);
    content = fs.readFileSync(SCRIPT_PATH, "utf8");
  });

  test("script and Unity .meta sibling exist", () => {
    expect(fs.existsSync(SCRIPT_PATH)).toBe(true);
    expect(fs.existsSync(`${SCRIPT_PATH}.meta`)).toBe(true);
  });

  test("uses LF line endings (no CRLF)", () => {
    expect(content).not.toContain("\r\n");
  });

  test("declares the optional output parameters", () => {
    expect(content).toContain("[string]$OutputJson");
    expect(content).toContain("[string]$OutputSummary");
  });

  test("queries all four Win32_* CIM classes via Get-CimInstance", () => {
    for (const className of [
      "Win32_Processor",
      "Win32_PhysicalMemory",
      "Win32_VideoController",
      "Win32_OperatingSystem"
    ]) {
      expect(content).toContain(`Get-CimInstance -ClassName ${className}`);
    }
  });

  test("reads the documented CIM fields for each class", () => {
    // Win32_Processor
    expect(content).toContain(".Name");
    expect(content).toContain(".NumberOfCores");
    expect(content).toContain(".NumberOfLogicalProcessors");
    expect(content).toContain(".MaxClockSpeed");
    // Win32_PhysicalMemory
    expect(content).toContain(".Capacity");
    expect(content).toContain(".Speed");
    // memory-type mapping
    expect(content).toContain("SMBIOSMemoryType");
    expect(content).toContain("MemoryType");
    expect(content).toContain("DDR4");
    expect(content).toContain("DDR5");
    // Win32_OperatingSystem
    expect(content).toContain(".Caption");
    expect(content).toContain(".Version");
  });

  test("emits a compact JSON object with the documented keys", () => {
    expect(content).toContain("ConvertTo-Json");
    expect(content).toContain("-Compress");
    for (const key of [
      "cpu",
      "physicalCores",
      "logicalCores",
      "clockMhz",
      "ramGb",
      "ramSpeedMhz",
      "ramType",
      "gpu",
      "os"
    ]) {
      expect(content).toContain(key);
    }
  });

  test("echoes a one-line summary to stdout and can write both output files", () => {
    expect(content).toContain("Format-MachineSpecsSummary");
    // Summary echoed to stdout regardless of output files.
    expect(content).toMatch(/Write-Host \$summary|Write-Host \$fallbackSummary/);
    // Writes JSON + summary files when their paths are supplied.
    expect(content).toContain("Write-TextFile -Path $OutputJson");
    expect(content).toContain("Write-TextFile -Path $OutputSummary");
  });

  test("PRIVACY: never references the host name, runner name, serial, MAC, or username", () => {
    // The whole purpose is to REPLACE the runner name with hardware specs, so
    // none of these identity SOURCES may be read or emitted by the code. Scan
    // CODE only -- the synopsis is free to describe what it avoids in prose.
    const code = stripPowerShellComments(content);
    const forbidden = [
      "Win32_ComputerSystem",
      "COMPUTERNAME",
      "RUNNER_NAME",
      "MACAddress",
      "SerialNumber",
      "hostname",
      "HostName",
      "$env:USERNAME",
      "UserName",
      "Win32_BIOS"
    ];
    for (const token of forbidden) {
      expect(code).not.toContain(token);
    }
  });

  test("ROBUSTNESS: wraps every probe in try/catch and emits warnings on failure", () => {
    const tryCount = (content.match(/\btry\s*\{/g) || []).length;
    const catchCount = (content.match(/\bcatch\b/g) || []).length;
    // One try/catch per probe (4) plus the write paths and the last-resort guard.
    expect(tryCount).toBeGreaterThanOrEqual(4);
    expect(catchCount).toBeGreaterThanOrEqual(4);
    expect(content).toContain("::warning::");
  });

  test("ALWAYS exits 0 (downstream JSON parsing must never crash)", () => {
    expect(content).toContain("exit 0");
    // There is no nonzero exit anywhere in the script.
    expect(content).not.toMatch(/exit\s+[1-9]/);
  });

  test("emits an 'unknown' fallback for failed probes so JSON stays valid", () => {
    expect(content).toContain("'unknown'");
    // A catastrophic-failure guard still writes a full fallback object.
    expect(content).toContain("$fallback");
  });

  test("is Windows PowerShell 5.1 + StrictMode safe (no pwsh-only syntax)", () => {
    expect(content).toContain("#Requires -Version 5.1");
    expect(content).toContain("Set-StrictMode -Version Latest");
    // Operator checks run against CODE only (comments legitimately mention ?? / ?:).
    const code = stripPowerShellComments(content);
    // No null-coalescing (PowerShell 5.1 lacks it).
    expect(code).not.toContain("??");
    // No ternary "<cond> ? <a> : <b>". The PowerShell 7 ternary operator is
    // whitespace-bounded, so require spaces around `?` and a later ` : ` -- this
    // does NOT false-match a regex inline flag like "(?i)".
    expect(code).not.toMatch(/\S\s+\?\s+\S[^\n]*\s+:\s+\S/);
    // No pwsh-7-only Clean block.
    expect(code).not.toMatch(/\bclean\s*\{/i);
  });

  test("is dot-source safe: the entrypoint runs only when invoked as a script", () => {
    expect(content).toContain(
      "$invokedAsScript = $MyInvocation.InvocationName -ne '' -and $MyInvocation.InvocationName -ne '.'"
    );
    expect(content).toContain("if ($invokedAsScript)");
  });
});
