"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const VERIFY_ACTION_PATH = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "verify-unity-results",
  "action.yml"
);
const DUMP_ACTION_PATH = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "dump-unity-log-tail",
  "action.yml"
);

function commandExists(command) {
  const result = spawnSync(
    command,
    ["-NoLogo", "-NoProfile", "-Command", "$PSVersionTable.PSVersion"],
    {
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"]
    }
  );
  return !result.error && result.status === 0;
}

const HAS_PWSH = commandExists("pwsh");

function toPowerShellSingleQuoted(value) {
  return `'${String(value).replaceAll("'", "''")}'`;
}

function extractActionScript(actionPath) {
  const action = fs.readFileSync(actionPath, "utf8").replace(/\r\n/g, "\n");
  const runMarker = "\n      run: |\n";
  const markerIndex = action.indexOf(runMarker);
  assert.notEqual(markerIndex, -1, `${actionPath} should contain a run block`);

  return action
    .slice(markerIndex + runMarker.length)
    .split("\n")
    .map((line) => (line.startsWith("        ") ? line.slice(8) : line))
    .join("\n");
}

function runVerifyUnityResultsAction(resultsDir) {
  let script = extractActionScript(VERIFY_ACTION_PATH);
  script = script.replace(
    '$dir = "${{ inputs.results-dir }}"',
    `$dir = ${toPowerShellSingleQuoted(resultsDir)}`
  );
  script = script.replace('$label = "${{ inputs.label }}"', "$label = 'Regression fixture'");

  const scriptPath = path.join(resultsDir, "verify-unity-results.ps1");
  fs.writeFileSync(scriptPath, script, "utf8");

  return spawnSync(
    "pwsh",
    ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
    {
      cwd: resultsDir,
      encoding: "utf8",
      env: {
        ...process.env,
        DXM_EXPECTED_EMPTY: ""
      }
    }
  );
}

function runDumpUnityLogTailAction(resultsDir, scriptDir = resultsDir || os.tmpdir()) {
  let script = extractActionScript(DUMP_ACTION_PATH);
  script = script.replace(
    '$dir = "${{ inputs.results-dir }}"',
    `$dir = ${toPowerShellSingleQuoted(resultsDir)}`
  );
  script = script.replace('$label = "${{ inputs.label }}"', "$label = 'Regression fixture'");
  script = script.replace(
    '$tailLines = [int]"${{ inputs.tail-lines }}"',
    "$tailLines = [int]'200'"
  );

  const scriptPath = path.join(scriptDir, `dump-unity-log-tail-${process.pid}-${Date.now()}.ps1`);
  fs.writeFileSync(scriptPath, script, "utf8");

  try {
    return spawnSync(
      "pwsh",
      [
        "-NoLogo",
        "-NoProfile",
        "-NonInteractive",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        scriptPath
      ],
      {
        cwd: scriptDir,
        encoding: "utf8"
      }
    );
  } finally {
    fs.rmSync(scriptPath, { force: true });
  }
}

test("verify-unity-results scans retry logs when final unity.log exists", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "verify-unity-results-"));
  try {
    fs.writeFileSync(path.join(tempDir, "unity.log"), "Final attempt failed later.\n", "utf8");
    fs.writeFileSync(
      path.join(tempDir, "unity.first-attempt.log"),
      [
        "Cancelled resolving packages",
        "PrecompiledAssemblyException: Multiple precompiled assemblies with the same name"
      ].join("\n"),
      "utf8"
    );

    const result = runVerifyUnityResultsAction(tempDir);
    const output = `${result.stdout}\n${result.stderr}`;
    assert.equal(result.status, 1, output);
    assert.match(output, /Unity Package Manager canceled package resolution before tests started/);
    assert.match(output, /Pattern detected -- PrecompiledAssemblyException/);
    assert.match(output, /unity\.first-attempt\.log/);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test("verify-unity-results scans retry logs when final unity.log is absent", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "verify-unity-results-retry-only-"));
  try {
    fs.writeFileSync(
      path.join(tempDir, "unity.first-attempt.log"),
      "Cancelled resolving packages before retry could write a final log\n",
      "utf8"
    );

    const result = runVerifyUnityResultsAction(tempDir);
    const output = `${result.stdout}\n${result.stderr}`;
    assert.equal(result.status, 1, output);
    assert.match(output, /Unity Package Manager canceled package resolution before tests started/);
    assert.match(output, /unity\.first-attempt\.log/);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test("dump-unity-log-tail scans retry logs when final unity.log exists", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dump-unity-log-tail-"));
  try {
    fs.writeFileSync(path.join(tempDir, "unity.log"), "Final attempt failed later.\n", "utf8");
    fs.writeFileSync(
      path.join(tempDir, "unity.first-attempt.log"),
      "CompilationFailedException: first attempt stopped before retry\n",
      "utf8"
    );

    const result = runDumpUnityLogTailAction(tempDir);
    const output = `${result.stdout}\n${result.stderr}`;
    assert.equal(result.status, 0, output);
    assert.match(output, /Pattern detected -- CompilationFailedException/);
    assert.match(output, /unity\.first-attempt\.log/);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test("dump-unity-log-tail scans retry logs when final unity.log is absent", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dump-unity-log-tail-retry-only-"));
  try {
    fs.writeFileSync(
      path.join(tempDir, "unity.first-attempt.log"),
      "CompilationFailedException: first attempt stopped before final log\n",
      "utf8"
    );

    const result = runDumpUnityLogTailAction(tempDir);
    const output = `${result.stdout}\n${result.stderr}`;
    assert.equal(result.status, 0, output);
    assert.match(output, /no unity\.log/);
    assert.match(output, /Pattern detected -- CompilationFailedException/);
    assert.match(output, /unity\.first-attempt\.log/);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test("dump-unity-log-tail tolerates an empty results directory input", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dump-unity-log-tail-empty-"));
  try {
    const result = runDumpUnityLogTailAction("", tempDir);
    const output = `${result.stdout}\n${result.stderr}`;
    assert.equal(result.status, 0, output);
    assert.match(output, /no unity\.log/);
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});
