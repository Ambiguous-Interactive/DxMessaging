"use strict";

// Issue #410: a step regression stayed green for two days because nothing in CI
// looks at how long a step takes. `SuiteWallClockBudgetTest` already logs the
// suite's elapsed time and its budgets; `Write-SuiteWallClockSummary` lifts that
// one line into the job summary and warns when it is over the soft budget.
//
// These tests dot-source the function out of run-ci-tests.ps1 and drive it with
// fixture logs, so every branch runs without Unity.

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const RUN_CI_SCRIPT_PATH = path.join(__dirname, "..", "unity", "run-ci-tests.ps1");

function commandExists(command) {
  // prettier-ignore
  const result = spawnSync(command, ["-NoLogo", "-NoProfile", "-Command", "$PSVersionTable.PSVersion"],
    { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] });
  return !result.error && result.status === 0;
}

const HAS_PWSH = commandExists("pwsh");

// The function is defined inside a script that runs a whole Unity leg when
// executed, so the body is extracted by name rather than dot-sourced.
function extractFunction(source, name) {
  const start = source.indexOf(`function ${name} {`);
  assert.notEqual(start, -1, `${name} must exist in run-ci-tests.ps1`);
  const end = source.indexOf("\n}\n", start);
  assert.notEqual(end, -1, `${name} must be a complete function`);
  return source.slice(start, end + 3);
}

function runSummary({ logText, label = "editmode" }) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-wallclock-"));
  try {
    const logPath = path.join(root, "unity.log");
    fs.writeFileSync(logPath, logText, "utf8");
    const summaryPath = path.join(root, "summary.md");
    const source = fs.readFileSync(RUN_CI_SCRIPT_PATH, "utf8");
    const scriptPath = path.join(root, "probe.ps1");
    fs.writeFileSync(
      scriptPath,
      [
        "param([string]$Label)",
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        extractFunction(source, "Write-SuiteWallClockSummary"),
        `Write-SuiteWallClockSummary -LogPath '${logPath}' -Label $Label`
      ].join("\n"),
      "utf8"
    );

    // The workflow runs this script once per test mode, so each leg is its own
    // process. Driving it the same way is what proves the header is written once
    // per job rather than once per leg.
    const legs = [label, `${label}-second`];
    const runs = legs.map((leg) =>
      spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-File", scriptPath, "-Label", leg], {
        encoding: "utf8",
        env: { ...process.env, GITHUB_STEP_SUMMARY: summaryPath }
      })
    );
    const summary = fs.existsSync(summaryPath) ? fs.readFileSync(summaryPath, "utf8") : "";
    return {
      stdout: runs.map((r) => r.stdout ?? "").join(""),
      stderr: runs.map((r) => r.stderr ?? "").join(""),
      status: runs.find((r) => r.status !== 0)?.status ?? 0,
      summary
    };
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

const UNDER_BUDGET =
  "DxMessaging suite wall clock: 36.30s (soft budget 60.0s, hard budget 180.0s for Unity 6000.4.6f1).\n";
const OVER_BUDGET =
  "DxMessaging suite wall clock: 113.80s (soft budget 60.0s, hard budget 180.0s for Unity 6000.4.6f1).\n";

test("a run under its soft budget is reported without a warning", { skip: !HAS_PWSH }, () => {
  const run = runSummary({ logText: `noise\n${UNDER_BUDGET}more noise\n` });

  assert.equal(run.status, 0, run.stderr);
  assert.match(run.summary, /\| editmode \| 36\.30s \| 60\.0s \| 180\.0s \|/);
  assert.doesNotMatch(run.stdout, /::warning::/);
});

test("a run over its soft budget warns and names both numbers", { skip: !HAS_PWSH }, () => {
  const run = runSummary({ logText: OVER_BUDGET });

  assert.equal(run.status, 0, run.stderr);
  assert.match(
    run.stdout,
    /::warning::editmode suite wall clock 113\.80s is over its 60\.0s soft budget/
  );
  assert.match(run.summary, /\| editmode \| 113\.80s \| 60\.0s \| 180\.0s \|/);
});

test(
  "the summary table header is written once per job, not once per leg",
  { skip: !HAS_PWSH },
  () => {
    const run = runSummary({ logText: UNDER_BUDGET });

    const headers = run.summary.match(/### Suite wall clock/g) ?? [];
    assert.equal(headers.length, 1, run.summary);
    assert.match(run.summary, /\| editmode-second \|/);
  }
);

test("a log with no wall-clock line reports nothing", { skip: !HAS_PWSH }, () => {
  const run = runSummary({ logText: "Unity started\nUnity finished\n" });

  assert.equal(run.status, 0, run.stderr);
  assert.equal(run.summary, "");
  assert.doesNotMatch(run.stdout, /::warning::/);
});

test("a missing log file reports nothing instead of failing the leg", { skip: !HAS_PWSH }, () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-wallclock-"));
  try {
    const source = fs.readFileSync(RUN_CI_SCRIPT_PATH, "utf8");
    const scriptPath = path.join(root, "probe.ps1");
    fs.writeFileSync(
      scriptPath,
      [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        extractFunction(source, "Write-SuiteWallClockSummary"),
        `Write-SuiteWallClockSummary -LogPath '${path.join(root, "absent.log")}' -Label 'playmode'`
      ].join("\n"),
      "utf8"
    );
    const result = spawnSync("pwsh", ["-NoLogo", "-NoProfile", "-File", scriptPath], {
      encoding: "utf8"
    });
    assert.equal(result.status, 0, result.stderr);
    assert.doesNotMatch(result.stdout ?? "", /::warning::/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("the wall-clock line the harness parses is the one the suite emits", () => {
  // The producing side is C#, the consuming side PowerShell. Neither compiles
  // the other, so the shared shape is pinned here.
  const budgetTest = fs.readFileSync(
    path.join(__dirname, "..", "..", "Tests", "Runtime", "Core", "SuiteWallClockBudgetTest.cs"),
    "utf8"
  );
  assert.match(
    budgetTest,
    /DxMessaging suite wall clock: \{elapsed\.TotalSeconds\.ToString\("0\.00", invariant\)\}s/
  );
  assert.match(
    budgetTest,
    /soft budget \{SoftBudget\.TotalSeconds\.ToString\("0\.0", invariant\)\}s/
  );
  assert.match(
    budgetTest,
    /hard budget \{HardBudget\.TotalSeconds\.ToString\("0\.0", invariant\)\}s/
  );

  const harness = fs.readFileSync(RUN_CI_SCRIPT_PATH, "utf8");
  assert.match(harness, /DxMessaging suite wall clock:\\s\*\(\[0-9\.\]\+\)s/);
});
