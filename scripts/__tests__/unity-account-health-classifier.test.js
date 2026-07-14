"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const HEALTH_CLASSIFIER = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "return-unity-license",
  "classify_unity_account_health.py"
);
const CLEANUP_CLASSIFIER = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "return-unity-license",
  "Classify-UnityLicenseReturn.ps1"
);
function classify(logText) {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-unity-health-"));
  try {
    const logPath = path.join(tempRoot, "unity.log");
    const outputPath = path.join(tempRoot, "github-output.txt");
    fs.writeFileSync(logPath, logText, "utf8");
    const command = process.platform === "win32" ? process.env.ComSpec || "cmd.exe" : "python3";
    const args =
      process.platform === "win32"
        ? ["/d", "/s", "/c", "python3.bat", HEALTH_CLASSIFIER]
        : [HEALTH_CLASSIFIER];
    const result = spawnSync(command, args, {
      cwd: REPO_ROOT,
      encoding: "utf8",
      env: {
        ...process.env,
        EVIDENCE_PATHS: logPath,
        GITHUB_OUTPUT: outputPath,
        HEALTHY_REASON: "return-missing-positive-evidence"
      }
    });
    assert.equal(result.status, 0, result.stderr || result.stdout);
    return Object.fromEntries(
      fs
        .readFileSync(outputPath, "utf8")
        .trim()
        .split(/\r?\n/)
        .map((line) => line.split(/=(.*)/s).slice(0, 2))
    );
  } finally {
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
}

test("Unity account-health evidence classification is exact and table driven", () => {
  for (const fixture of [
    {
      name: "exact account limit",
      log: "Licensing failed with error code 20111\n",
      health: "blocked",
      reason: "unity-account-limit-20111"
    },
    {
      name: "bracketed account limit",
      log: "[Licensing] Error [20111]: activation limit reached\n",
      health: "blocked",
      reason: "unity-account-limit-20111"
    },
    {
      name: "calendar expiry remains local",
      log: "Licensing failed with error code 20113\n",
      health: "healthy",
      reason: "return-missing-positive-evidence"
    },
    {
      name: "return error remains local",
      log: "Licensing failed with error code 400006\n",
      health: "healthy",
      reason: "return-missing-positive-evidence"
    },
    {
      name: "numeric substring is not evidence",
      log: "Diagnostic identifier 1201119\n",
      health: "healthy",
      reason: "return-missing-positive-evidence"
    }
  ]) {
    const result = classify(fixture.log);
    assert.equal(result["resource-health"], fixture.health, fixture.name);
    assert.equal(result["resource-reason"], fixture.reason, fixture.name);
    assert.match(result["evidence-digest"], /^[0-9a-f]{64}$/, fixture.name);
  }
});

function cleanupIsConfirmed(exitCode, logText) {
  const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-unity-cleanup-"));
  try {
    const logPath = path.join(tempRoot, "return.log");
    fs.writeFileSync(logPath, logText, "utf8");
    const command = [
      ". $env:CLASSIFIER_PATH",
      "$safe = Test-UnityLicenseReturnResourceSafe -ExitCode ([int]$env:RETURN_EXIT_CODE) -LogPath $env:RETURN_LOG_PATH",
      "if ($safe) { exit 0 } else { exit 1 }"
    ].join("; ");
    return (
      spawnSync("pwsh", ["-NoProfile", "-Command", command], {
        cwd: REPO_ROOT,
        encoding: "utf8",
        env: {
          ...process.env,
          CLASSIFIER_PATH: CLEANUP_CLASSIFIER,
          RETURN_EXIT_CODE: String(exitCode),
          RETURN_LOG_PATH: logPath
        }
      }).status === 0
    );
  } finally {
    fs.rmSync(tempRoot, { recursive: true, force: true });
  }
}

test("Unity cleanup classification requires exact positive return evidence", () => {
  const exact =
    "Successfully returned the entitlement license\nSerial number unavailable for ULF return\n";
  const explicit =
    "[Licensing::Module] Successfully returned the entitlement license\n[Licensing::Client] Successfully returned ULF license with serial number: REDACTED\n";
  for (const [name, exitCode, log, expected] of [
    ["entitlement and legacy absence", 0, exact, true],
    ["entitlement and explicit ULF return", 0, explicit, true],
    ["exit zero alone", 0, "Exiting batchmode successfully now!\n", false],
    ["serial unavailable alone", 0, "Serial number unavailable for ULF return\n", false],
    ["entitlement alone", 0, "Successfully returned the entitlement license\n", false],
    ["terminated process", 143, exact, false]
  ]) {
    assert.equal(cleanupIsConfirmed(exitCode, log), expected, name);
  }
});
