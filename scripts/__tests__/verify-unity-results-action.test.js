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
const RUN_CI_TESTS_PATH = path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1");

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
const RETRY_LOG = "unity.first-attempt.log";

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

function labelsInBlock(filePath, startNeedle, endNeedle) {
  const text = fs.readFileSync(filePath, "utf8");
  const start = text.indexOf(startNeedle);
  assert.notEqual(start, -1, `${filePath} must contain ${startNeedle}`);
  const end = text.indexOf(endNeedle, start);
  assert.notEqual(end, -1, `${filePath} must close the diagnostic pattern block`);
  return [...text.slice(start, end).matchAll(/Label\s*=\s*'([^']+)'/g)].map((match) => match[1]);
}

test("Unity catastrophic diagnostic labels stay in sync", () => {
  const expected = labelsInBlock(VERIFY_ACTION_PATH, "$patterns = @(", "\n          )");
  const cases = [
    [DUMP_ACTION_PATH, "$patterns = @(", "\n        )"],
    [RUN_CI_TESTS_PATH, "$script:CatastrophicPatterns = @(", "\n)"]
  ];
  for (const [filePath, startNeedle, endNeedle] of cases) {
    assert.deepEqual(labelsInBlock(filePath, startNeedle, endNeedle), expected, filePath);
  }
});

test("Unity result actions scan retry logs alongside or instead of unity.log", (t) => {
  if (!HAS_PWSH) {
    t.skip("PowerShell is not available");
    return;
  }

  const cases = [
    {
      name: "verify final plus retry",
      run: runVerifyUnityResultsAction,
      finalLog: "Final attempt failed later.\n",
      retryLog: [
        "Cancelled resolving packages",
        "PrecompiledAssemblyException: Multiple precompiled assemblies with the same name"
      ].join("\n"),
      status: 1,
      patterns: [
        /Unity Package Manager canceled package resolution before tests started/,
        /Pattern detected -- PrecompiledAssemblyException/,
        /unity\.first-attempt\.log/
      ]
    },
    {
      name: "verify retry only",
      run: runVerifyUnityResultsAction,
      retryLog:
        "Cancelled resolving packages before retry could write a final log\n" +
        "error CS1069: type forwarded to assembly 'UnityEngine.AIModule'\n",
      status: 1,
      patterns: [
        /Unity Package Manager canceled package resolution before tests started/,
        /CS1069 forwarded type/,
        /Remediation -- .*optional Unity engine module/,
        /unity\.first-attempt\.log/
      ]
    },
    {
      name: "dump final plus retry",
      run: runDumpUnityLogTailAction,
      finalLog: "Final attempt failed later.\n",
      retryLog: "CompilationFailedException: first attempt stopped before retry\n",
      status: 0,
      patterns: [/Pattern detected -- CompilationFailedException/, /unity\.first-attempt\.log/]
    },
    {
      name: "dump retry only",
      run: runDumpUnityLogTailAction,
      retryLog:
        "CompilationFailedException: first attempt stopped before final log\n" +
        "error CS1069: type forwarded to assembly 'UnityEngine.AIModule'\n",
      status: 0,
      patterns: [
        /no unity\.log/,
        /Pattern detected -- CompilationFailedException/,
        /CS1069 forwarded type/,
        /Remediation -- .*optional Unity engine module/,
        /unity\.first-attempt\.log/
      ]
    }
  ];

  for (const fixture of cases) {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "unity-result-action-"));
    try {
      if (fixture.finalLog) {
        fs.writeFileSync(path.join(tempDir, "unity.log"), fixture.finalLog, "utf8");
      }
      fs.writeFileSync(path.join(tempDir, RETRY_LOG), fixture.retryLog, "utf8");

      const result = fixture.run(tempDir);
      const output = `${result.stdout}\n${result.stderr}`;
      assert.equal(result.status, fixture.status, `${fixture.name}\n${output}`);
      for (const pattern of fixture.patterns) {
        assert.match(output, pattern, fixture.name);
      }
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
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
