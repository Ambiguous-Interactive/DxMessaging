/**
 * @fileoverview Live end-to-end test that the Unity Accelerator endpoint
 * masking lines fire BEFORE any downstream log line can echo the host:port.
 *
 * WHY THIS EXISTS:
 *   When UNITY_ACCELERATOR_ENDPOINT is a URL with userinfo/path/etc., the
 *   normalizer extracts a NEW substring (the canonical host:port). The GitHub
 *   Actions secret-mask the secret-storage applies to the ORIGINAL value does
 *   NOT propagate to substrings derived in the running step, so the extracted
 *   host:port would print UNMASKED to the runner log -- specifically through
 *   the `Write-Host "$EditorPath $($Arguments -join ' ')"` line in
 *   Invoke-UnityEditor (run-ci-tests.ps1), which would render the value to the
 *   streamed log.
 *   Get-AcceleratorArguments therefore re-registers BOTH the raw trimmed input
 *   AND the normalized form via `::add-mask::` at the top of its success path,
 *   before any caller could echo them.
 *
 *   This test stands up the SAME stub-editor harness as
 *   unity-runner-strictmode-smoke.test.js, spawns the WHOLE run-ci-tests.ps1
 *   under a real pwsh invocation, and asserts the two `::add-mask::` lines
 *   appear in stdout (case 1) and do NOT appear when the env var is unset
 *   (case 2). It also asserts the new "Unity Accelerator enabled ... value
 *   masked" notice when set, and the original "Unity Accelerator disabled"
 *   notice when unset.
 *
 *   pwsh is preinstalled on the CI runners; locally absent -> tests skip
 *   (mirrors unity-runner-strictmode-smoke.test.js). An always-on sanity test
 *   still proves the script under test exists, so a rename/move cannot
 *   silently disable the guard.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const { sandboxHostFolderEnv } = require("../lib/spawn-env-sandbox");
const { assertSpawnStatus, combinedText } = require("../lib/pwsh-output");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const RUN_CI_TESTS = path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1");

// Mirror the env-hygiene contract from unity-runner-strictmode-smoke.test.js:
// neutralize host folder vars hermetically, delete every env var that would
// route the script down a divergent path, then set the serial creds so the
// activate/return path runs. UNITY_ACCELERATOR_ENDPOINT is intentionally NOT
// in the always-delete list here -- each scenario sets it explicitly.
const ENV_TO_DELETE = [
  "UNITY_ACCELERATOR_ENDPOINT",
  "UNITY_LICENSE",
  "UNITY_LICENSE_B64",
  "UNITY_LICENSE_FILE",
  "GITHUB_WORKSPACE",
  "GITHUB_ACTIONS",
  "UNITY_LICENSING_SERVER"
];

const SERIAL_CREDS = {
  UNITY_SERIAL: "SC-TEST-SERIAL-0000",
  UNITY_EMAIL: "ci-bot@example.invalid",
  UNITY_PASSWORD: "not-a-real-password"
};

// Cross-platform stub editor. Branches on args:
//   - -returnlicense   -> append to the marker file, exit 0
//   - -runTests        -> write a passing NUnit results.xml, exit 0
//   - otherwise        -> exit 0 (activation / configure / probe no-op)
function stubEditorSource() {
  return [
    "[CmdletBinding()] param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Rest)",
    "$Rest = @($Rest)",
    "if ($Rest -contains '-returnlicense') {",
    "  $marker = $env:DXM_SMOKE_RETURN_MARKER",
    "  if ($marker) {",
    "    $d = Split-Path -Parent $marker",
    "    if ($d -and -not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
    "    Add-Content -LiteralPath $marker -Value ('returned ' + ($Rest -join ' '))",
    "  }",
    "  exit 0",
    "}",
    "if ($Rest -contains '-runTests') {",
    "  $i = [array]::IndexOf($Rest, '-testResults')",
    "  if ($i -ge 0 -and ($i + 1) -lt $Rest.Count) {",
    "    $out = $Rest[$i + 1]; $d = Split-Path -Parent $out",
    "    if ($d) { New-Item -ItemType Directory -Force -Path $d | Out-Null }",
    '    \'<?xml version="1.0" encoding="utf-8"?><test-run total="1" passed="1" failed="0" skipped="0" result="Passed"></test-run>\' | Set-Content -LiteralPath $out -Encoding UTF8',
    "  }",
    "  exit 0",
    "}",
    "exit 0",
    ""
  ].join("\n");
}

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

function cleanedEnv(sandboxRoot, extraEnv) {
  const env = sandboxHostFolderEnv(process.env, sandboxRoot);
  for (const key of ENV_TO_DELETE) {
    delete env[key];
  }
  Object.assign(env, SERIAL_CREDS);
  if (extraEnv) {
    Object.assign(env, extraEnv);
  }
  return env;
}

function makeWorkspace() {
  const base = makeTempDir("acc-mask");
  const repoRoot = path.join(base, "repo");
  const artifacts = path.join(base, "artifacts");
  const project = path.join(base, "project");

  fs.mkdirSync(path.join(repoRoot, "Runtime"), { recursive: true });
  fs.mkdirSync(path.join(repoRoot, "Editor", "Analyzers"), { recursive: true });
  fs.writeFileSync(path.join(repoRoot, "package.json"), "{}\n", "utf8");
  for (const dllName of [
    "WallstopStudios.DxMessaging.SourceGenerators.dll",
    "WallstopStudios.DxMessaging.Analyzer.dll"
  ]) {
    fs.writeFileSync(path.join(repoRoot, "Editor", "Analyzers", dllName), "stub", "utf8");
  }
  fs.mkdirSync(artifacts, { recursive: true });
  fs.mkdirSync(project, { recursive: true });

  const stubPath = path.join(base, "stub-editor.ps1");
  fs.writeFileSync(stubPath, stubEditorSource(), "utf8");

  const returnMarker = path.join(base, "returned.marker");

  return { base, repoRoot, artifacts, project, stubPath, returnMarker };
}

function runScript(ws, extraEnv) {
  const env = cleanedEnv(path.join(ws.base, "host-env-sandbox"), extraEnv);
  env.DXM_SMOKE_RETURN_MARKER = ws.returnMarker;

  return spawnSync(
    "pwsh",
    [
      "-NoProfile",
      "-NonInteractive",
      "-File",
      RUN_CI_TESTS,
      "-UnityVersion",
      "2021.3.45f1",
      "-TestMode",
      "editmode",
      "-AssemblyNames",
      "WallstopStudios.DxMessaging.Tests.Editor",
      "-ArtifactsPath",
      ws.artifacts,
      "-ProjectPath",
      ws.project,
      "-RepoRoot",
      ws.repoRoot,
      "-UnityEditorPath",
      ws.stubPath
    ],
    { env, encoding: "utf8", maxBuffer: 32 * 1024 * 1024 }
  );
}

const workspaces = [];

afterAll(() => {
  for (const ws of workspaces) {
    cleanupDir(ws.base);
  }
});

describe("run-ci-tests.ps1 Unity Accelerator endpoint masking", () => {
  // Always-on sanity test: proves the script under test exists, so a
  // rename/move cannot silently turn this guard into a no-op.
  test("the script under test exists", () => {
    expect(fs.existsSync(RUN_CI_TESTS)).toBe(true);
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[accelerator-masking] pwsh not found on PATH; skipping run assertions (CI runners have pwsh)."
    );
    test.skip("registers ::add-mask:: for both the raw and normalized endpoint when set", () => {});
    test.skip("passes the bare host:port form through to -cacheServerEndpoint unmodified", () => {});
    test.skip("emits NO ::add-mask:: and the disabled notice when unset", () => {});
    return;
  }

  test("registers ::add-mask:: for both the raw and normalized endpoint when set", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    const result = runScript(ws, {
      UNITY_ACCELERATOR_ENDPOINT: "http://accelerator.example.com:10080"
    });
    const combined = combinedText(result);

    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    // Both forms must be masked: the raw trimmed env value AND the derived
    // canonical host:port form. Invoke-UnityEditor in run-ci-tests.ps1 echoes
    // the assembled argument array (which includes the normalized host:port),
    // so the normalized form must be masked too.
    expect(combined).toContain("::add-mask::http://accelerator.example.com:10080");
    expect(combined).toContain("::add-mask::accelerator.example.com:10080");
    // The new "enabled" notice includes the namespace AND the masked-value
    // hint, proving the script took the success path.
    expect(combined).toContain("dxmessaging-2021.3.45f1-editmode");
    expect(combined).toContain("value masked");
    // REGRESSION: prove the NORMALIZED form is what reaches Unity's CLI, not
    // the raw URL (which is what failed CI run logs_70920965650). add-mask is
    // a no-op outside GHA, so the echoed argument array is visible in stdout
    // here. A mutation that swaps `$normalized` for `$Endpoint.Trim()` in the
    // returned arg array MUST fail this assertion.
    expect(combined).toContain("-cacheServerEndpoint accelerator.example.com:10080");
    expect(combined).not.toContain("-cacheServerEndpoint http://");

    // COVERAGE GUARD: pin that the raw URL (with scheme) appears EXACTLY ONCE
    // in the test output -- the documented `::add-mask::http://...` line. The
    // normalized host:port form legitimately appears in BOTH the mask line and
    // the `-cacheServerEndpoint ...` arg echo, so an exact count there is
    // fragile; the raw URL form, by contrast, lives only in the mask
    // registration line. A future edit that adds e.g.
    // `Write-Host "DEBUG: $Endpoint"` after the mask block would slip past the
    // `.toContain("::add-mask::http://...")` assertion above (the mask still
    // wins in production GHA because add-mask scrubs subsequent occurrences),
    // but this count assertion surfaces the new echo locally so a leak path
    // cannot ship unnoticed.
    const rawUrlOccurrences = (combined.match(/http:\/\/accelerator\.example\.com:10080/g) || [])
      .length;
    expect(rawUrlOccurrences).toBe(1);
  });

  test("passes the bare host:port form through to -cacheServerEndpoint unmodified", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    // Symmetric M1 case: bare host:port must reach Unity verbatim (no
    // accidental scheme prepending, no IPv6 mishandling on the bare path).
    const result = runScript(ws, {
      UNITY_ACCELERATOR_ENDPOINT: "127.0.0.1:10080"
    });
    const combined = combinedText(result);

    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect(combined).toContain("::add-mask::127.0.0.1:10080");
    expect(combined).toContain("-cacheServerEndpoint 127.0.0.1:10080");
    expect(combined).toContain("value masked");
  });

  test("emits NO ::add-mask:: and the disabled notice when unset", () => {
    const ws = makeWorkspace();
    workspaces.push(ws);

    // Explicitly leave UNITY_ACCELERATOR_ENDPOINT unset (it is in
    // ENV_TO_DELETE), so the empty-array branch is taken.
    const result = runScript(ws);
    const combined = combinedText(result);

    assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
    expect(combined).not.toContain("::add-mask::");
    expect(combined).toContain("Unity Accelerator disabled");
  });
});
