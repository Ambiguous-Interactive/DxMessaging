/**
 * @fileoverview Data-driven Jest test for the pure normalizer
 * `ConvertTo-NormalizedAcceleratorEndpoint` in scripts/unity/run-ci-tests.ps1.
 *
 * WHY THIS EXISTS:
 *   CI run logs_70920965650 had 9/9 Unity matrix jobs fail because the prior
 *   `Get-AcceleratorArguments` REJECTED any `UNITY_ACCELERATOR_ENDPOINT`
 *   containing a URL scheme. The fix introduces a pure normalizer that accepts
 *   both bare `host:port` and `scheme://host:port[/path]` URL forms, strips
 *   userinfo/path/query/fragment, keeps IPv6 literals bracketed, and emits the
 *   canonical `host:port` string that Unity's `-cacheServerEndpoint` wants. This
 *   test pins that behavior so a regression cannot reintroduce the URL
 *   rejection.
 *
 *   The normalizer is also the SECURITY boundary -- its error messages MUST
 *   NEVER echo the input value (the raw secret might just look like a URL even
 *   when it is something else). The leak-guard case at the bottom of CASES
 *   feeds in a value containing a synthetic token and asserts the token does
 *   NOT appear in any error output.
 *
 * IMPLEMENTATION NOTES:
 *   We extract the source text of `ConvertTo-NormalizedAcceleratorEndpoint`
 *   from run-ci-tests.ps1 using the same regex-slice technique as
 *   unity-runner-script-contract.test.js's `extractFunctionBody`. Each case
 *   spawns pwsh once with a script that defines the function via
 *   `Invoke-Expression` and calls it. The pwsh program emits exactly ONE line:
 *   `OK:<value>`, `EMPTY`, or `THROW:<message>`. Output is normalized through
 *   `combinedText` so the Windows-only ConciseView word-wrap (see
 *   scripts/lib/pwsh-output.js) can never break the assertions.
 *
 *   pwsh is preinstalled on the CI runners; when it is absent locally the
 *   per-case sub-tests skip (mirrors unity-runner-strictmode-smoke.test.js).
 *   An always-on sanity test still proves the function under test exists, so a
 *   rename/move cannot silently turn the whole guard into a no-op.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const { assertSpawnStatus, combinedText } = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const RUN_CI_TESTS = path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1");

/**
 * Extract the source text of a top-level `function <name> { ... }` by bounding
 * it at the next top-level `\nfunction ` definition. Mirrors the slicing used
 * by unity-runner-script-contract.test.js. Returns "" when not found.
 */
function extractFunctionBody(scriptText, functionName) {
  const start = scriptText.indexOf(`function ${functionName}`);
  if (start < 0) {
    return "";
  }
  const after = scriptText.indexOf("\nfunction ", start + 1);
  return after === -1 ? scriptText.slice(start) : scriptText.slice(start, after);
}

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

const SCRIPT_TEXT = fs.existsSync(RUN_CI_TESTS) ? fs.readFileSync(RUN_CI_TESTS, "utf8") : "";
const FUNCTION_BODY = extractFunctionBody(SCRIPT_TEXT, "ConvertTo-NormalizedAcceleratorEndpoint");

/**
 * Spawn pwsh, define the extracted function via Invoke-Expression, then call
 * the function with the given input. Emits exactly one stdout line:
 *   - "OK:<value>"  -- non-empty string return
 *   - "EMPTY"       -- $null/empty return
 *   - "THROW:<msg>" -- throw, message captured from $_.Exception.Message
 *
 * Set-StrictMode -Version Latest matches the production script's mode. The
 * function source is passed via an environment variable so the input value
 * (which CASES sometimes contains characters that would break a pwsh -Command
 * arg encoding) and the function body never collide.
 */
function runNormalizer(input) {
  const program = [
    "Set-StrictMode -Version Latest",
    "$ErrorActionPreference = 'Stop'",
    "Invoke-Expression $env:DXM_NORMALIZER_SOURCE",
    "try {",
    "  $r = ConvertTo-NormalizedAcceleratorEndpoint -Endpoint $env:DXM_NORMALIZER_INPUT",
    "  if ($null -eq $r -or $r -eq '') {",
    "    Write-Output 'EMPTY'",
    "  } else {",
    "    Write-Output ('OK:' + $r)",
    "  }",
    "} catch {",
    "  Write-Output ('THROW:' + $_.Exception.Message)",
    "  exit 0",
    "}"
  ].join("\n");

  return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", program], {
    env: {
      ...process.env,
      DXM_NORMALIZER_SOURCE: FUNCTION_BODY,
      DXM_NORMALIZER_INPUT: input
    },
    encoding: "utf8",
    maxBuffer: 8 * 1024 * 1024
  });
}

const CASES = [
  { label: "canonical host:port", input: "127.0.0.1:10080", expect: { ok: "127.0.0.1:10080" } },
  {
    label: "hostname:port",
    input: "accelerator.example.com:10080",
    expect: { ok: "accelerator.example.com:10080" }
  },
  {
    label: "http URL",
    input: "http://accelerator.example.com:10080",
    expect: { ok: "accelerator.example.com:10080" }
  },
  {
    label: "https URL with trailing slash",
    input: "https://accelerator.example.com:10080/",
    expect: { ok: "accelerator.example.com:10080" }
  },
  {
    label: "arbitrary scheme + path + query + fragment",
    input: "acc://accelerator.example.com:10080/p?x=1#f",
    expect: { ok: "accelerator.example.com:10080" }
  },
  {
    label: "URL with userinfo",
    input: "http://user:pw@accelerator.example.com:10080",
    expect: { ok: "accelerator.example.com:10080" }
  },
  { label: "IPv6 bare", input: "[::1]:10080", expect: { ok: "[::1]:10080" } },
  { label: "IPv6 in URL", input: "http://[::1]:10080/", expect: { ok: "[::1]:10080" } },
  {
    label: "leading/trailing whitespace",
    input: "  127.0.0.1:10080  ",
    expect: { ok: "127.0.0.1:10080" }
  },
  { label: "empty", input: "", expect: { empty: true } },
  { label: "whitespace only", input: "   ", expect: { empty: true } },
  {
    label: "URL missing explicit port (http default)",
    input: "http://accelerator.example.com",
    expect: { throwsContains: "missing an explicit :port" }
  },
  {
    label: "bare host no port",
    input: "accelerator.example.com",
    expect: { throwsContains: "expected host:port" }
  },
  {
    label: "garbage",
    input: "not a valid endpoint at all",
    expect: { throwsContains: "expected host:port" }
  },
  {
    label: "port out of range high",
    input: "host:99999",
    expect: { throwsContains: "port is out of range" }
  },
  { label: "port zero", input: "host:0", expect: { throwsContains: "port is out of range" } },
  {
    label: "non-numeric port",
    input: "host:abc",
    expect: { throwsContains: "expected host:port" }
  },
  // NEGATIVE LEAK GUARDS: input value MUST NOT appear in any error output.
  // Each of the four throw paths in ConvertTo-NormalizedAcceleratorEndpoint
  // is exercised so a regression that interpolates `$Endpoint` into ANY
  // error message can never ship. The fourth path (URL TryCreate failure)
  // is statically safe (no `$Endpoint` interpolation), and an inline comment
  // in run-ci-tests.ps1 documents that -- [System.Uri]::TryCreate is too
  // permissive to deterministically trigger this path from a Jest test.
  {
    label: "leak guard (URL with token, missing port)",
    input: "http://SECRET-LEAK-TOKEN.example.com",
    expect: { throwsContains: "missing an explicit :port", mustNotContain: "SECRET-LEAK-TOKEN" }
  },
  {
    label: "leak guard (bare malformed)",
    input: "SECRET-LEAK-B-no-port",
    expect: { throwsContains: "expected host:port", mustNotContain: "SECRET-LEAK-B" }
  },
  {
    label: "leak guard (port out of range)",
    input: "SECRET-LEAK-C.example.com:99999",
    expect: { throwsContains: "port is out of range", mustNotContain: "SECRET-LEAK-C" }
  },
  // NOTE: the bracket-content regex (`[0-9A-Fa-f:]+`) rejects non-hex letters,
  // so this input falls through to the bare-host throw, NOT the bracketed-IPv6
  // throw. The label reflects intent; the case still proves the form-only
  // invariant holds for malformed bracket-shaped input. The bracketed-IPv6
  // port-length guard is covered separately below by a HEX-ONLY case.
  {
    label: "leak guard (bracketed-shape malformed)",
    input: "[::SECRET-LEAK-D::]:99999",
    expect: { throwsContains: "expected host:port", mustNotContain: "SECRET-LEAK-D" }
  },
  // LEAK GUARD (Int32 overflow): a 12-digit port would historically crash the
  // `[int]$matches[2]` cast with a .NET exception text that echoes the digits
  // verbatim ("Cannot convert value "99999999999" to type ..."), contradicting
  // the form-only invariant. Both branches (bare host:port AND bracketed IPv6)
  // must pre-validate the digit length (>5 digits is always out of range, max
  // legal port is 65535) and throw the form-only message BEFORE the cast.
  // mustNotContain accepts a single string in this fixture, so we cover both
  // halves (port digits + host token) via two adjacent cases against the same
  // input. (Splitting into two cases is the minimally-invasive approach: the
  // alternative would change mustNotContain's semantics across the suite.)
  {
    label: "leak guard (Int32-overflow port)",
    input: "SECRET-LEAK-OVERFLOW.example.com:99999999999",
    expect: { throwsContains: "port is out of range", mustNotContain: "99999999999" }
  },
  {
    label: "leak guard (Int32-overflow port, host name leak)",
    input: "SECRET-LEAK-OVERFLOW.example.com:99999999999",
    expect: { throwsContains: "port is out of range", mustNotContain: "SECRET-LEAK-OVERFLOW" }
  },
  // LEAK GUARD: the bracketed-IPv6 branch has its OWN port-length pre-validation
  // guard that is unreachable from the malformed-bracket case above. This
  // HEX-ONLY input matches the bracketed regex (`[0-9A-Fa-f:]+`) and exercises
  // the bracketed-branch length guard. Without it, `[::1]:99999999999` would
  // crash the `[int]$matches[2]` cast and leak the digits via the .NET
  // exception text.
  {
    label: "leak guard (bracketed IPv6 Int32-overflow port)",
    input: "[::1]:99999999999",
    expect: { throwsContains: "port is out of range", mustNotContain: "99999999999" }
  }
];

describe("ConvertTo-NormalizedAcceleratorEndpoint normalization", () => {
  // Always-on sanity test: proves the function under test exists. Guards
  // against a silent zero-coverage regression if the function is renamed.
  test("the script defines ConvertTo-NormalizedAcceleratorEndpoint", () => {
    expect(fs.existsSync(RUN_CI_TESTS)).toBe(true);
    expect(SCRIPT_TEXT).toContain("function ConvertTo-NormalizedAcceleratorEndpoint");
    expect(FUNCTION_BODY).toContain("function ConvertTo-NormalizedAcceleratorEndpoint");
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[accelerator-normalization] pwsh not found on PATH; skipping per-case assertions (CI runners have pwsh)."
    );
    test.skip.each(CASES)("$label", () => {});
    return;
  }

  test.each(CASES)("$label", ({ input, expect: expected }) => {
    const result = runNormalizer(input);
    const combined = combinedText(result);

    if (expected.ok !== undefined) {
      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      expect(combined).toContain(`OK:${expected.ok}`);
    } else if (expected.empty) {
      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      expect(combined).toContain("EMPTY");
    } else if (expected.throwsContains !== undefined) {
      // Throw cases EXIT 0 (the runner script catches and writes THROW:<msg>),
      // so status==0 is correct here.
      assertSpawnStatus(result, 0, expect.getState().currentTestName || "pwsh harness");
      expect(combined).toContain("THROW:");
      expect(combined).toContain(expected.throwsContains);
      if (expected.mustNotContain !== undefined) {
        expect(combined).not.toContain(expected.mustNotContain);
      }
    } else {
      throw new Error(`Test case for "${input}" has no expectation`);
    }
  });
});
