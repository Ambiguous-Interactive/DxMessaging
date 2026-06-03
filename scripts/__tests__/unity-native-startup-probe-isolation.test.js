/**
 * @fileoverview Static guard: every Jest test that drives `ensure-editor.ps1`
 * against a fake `Unity.exe` stub MUST opt out of the native startup probe via
 * `DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE=1`. Linux/macOS execute a shebang `.exe`
 * via kernel exec dispatch, but Windows `CreateProcess()` rejects it as not a
 * valid PE binary ("specified executable is not a valid application for this OS
 * platform"). The probe path in `Ensure-UnityNativeStartupHealthy` therefore
 * fails the whole bootstrap on Windows for any stub-based test.
 *
 * This file is the regression-class guard for that footgun:
 *   (a) `ensure-editor.ps1` MUST expose an early-return gate keyed on the
 *       literal `DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE` inside
 *       `Ensure-UnityNativeStartupHealthy`, BEFORE the call to the probe.
 *   (b) The il2cpp-idempotency harness's `runEnsureEditorWithFakeCli` MUST
 *       set that env var (so existing tests keep passing on Windows).
 *   (c) Any OTHER `.test.js` under `scripts/__tests__/` that references
 *       `ensure-editor.ps1` AND writes a fake `Unity.exe` stub MUST also
 *       reference `DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE`. A future test that
 *       legitimately exercises the probe against a real editor can opt out via
 *       the comment marker `// @allow-unity-native-probe`.
 *
 * @cross-platform-regression -- gated on ubuntu/windows/macos via the targeted
 * regression step of .github/workflows/cross-platform-preflight.yml; enforced
 * by scripts/__tests__/cross-platform-preflight-coverage.test.js.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const ENSURE_EDITOR = path.join(REPO_ROOT, "scripts", "unity", "ensure-editor.ps1");
const TEST_DIR = path.join(REPO_ROOT, "scripts", "__tests__");
const HARNESS_TEST = path.join(TEST_DIR, "unity-ensure-editor-il2cpp-idempotency.test.js");

const SKIP_ENV = "DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE";
const ALLOW_MARKER = "@allow-unity-native-probe";
const SELF_BASENAME = "unity-native-startup-probe-isolation.test.js";

function readUtf8(filePath) {
  return fs.readFileSync(filePath, "utf8");
}

// Extract a PowerShell function body by name via balanced brace scan. Returns
// the substring from the `function <Name>` header through the matching closing
// brace, or `null` if the function is not found / braces are unbalanced.
function extractPowerShellFunction(source, name) {
  const headerPattern = new RegExp(`(^|\\n)function\\s+${name}\\b`);
  const headerMatch = headerPattern.exec(source);
  if (!headerMatch) {
    return null;
  }
  const headerStart = headerMatch.index + (headerMatch[1] ? headerMatch[1].length : 0);
  const openBrace = source.indexOf("{", headerStart);
  if (openBrace < 0) {
    return null;
  }
  let depth = 0;
  for (let i = openBrace; i < source.length; i += 1) {
    const ch = source[i];
    if (ch === "{") {
      depth += 1;
    } else if (ch === "}") {
      depth -= 1;
      if (depth === 0) {
        return source.slice(headerStart, i + 1);
      }
    }
  }
  return null;
}

// Slice the body of a top-level JS `function <name>(...) { ... }` via balanced
// brace scan starting at the first `{` after the header.
function extractJsFunction(source, name) {
  const headerPattern = new RegExp(`(^|\\n)function\\s+${name}\\s*\\(`);
  const headerMatch = headerPattern.exec(source);
  if (!headerMatch) {
    return null;
  }
  const headerStart = headerMatch.index + (headerMatch[1] ? headerMatch[1].length : 0);
  const openBrace = source.indexOf("{", headerStart);
  if (openBrace < 0) {
    return null;
  }
  let depth = 0;
  for (let i = openBrace; i < source.length; i += 1) {
    const ch = source[i];
    if (ch === "{") {
      depth += 1;
    } else if (ch === "}") {
      depth -= 1;
      if (depth === 0) {
        return source.slice(headerStart, i + 1);
      }
    }
  }
  return null;
}

describe("Unity native startup probe isolation guard", () => {
  test("ensure-editor.ps1 gates Ensure-UnityNativeStartupHealthy on DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE", () => {
    const source = readUtf8(ENSURE_EDITOR);
    const body = extractPowerShellFunction(source, "Ensure-UnityNativeStartupHealthy");
    expect(body).not.toBeNull();

    // The gate must mention the env var name AND return $EditorPath BEFORE the
    // first Test-UnityNativeStartup call. We also require the early-return to
    // immediately follow the env-var check (no intervening logic) so that a
    // future refactor that reorders the function cannot satisfy the guard
    // vacuously by leaving an unrelated `return $EditorPath` above the probe.
    const envIdx = body.indexOf(SKIP_ENV);
    const probeIdx = body.indexOf("Test-UnityNativeStartup");
    expect(envIdx).toBeGreaterThan(-1);
    expect(probeIdx).toBeGreaterThan(-1);
    expect(envIdx).toBeLessThan(probeIdx);
    const afterEnv = body.slice(envIdx);
    const returnIdxLocal = afterEnv.indexOf("return $EditorPath");
    expect(returnIdxLocal).toBeGreaterThan(-1);
    // The first `return $EditorPath` after the env-var literal must precede the
    // probe call AND be within a small window (the gate is a 3-line `if` block).
    expect(envIdx + returnIdxLocal).toBeLessThan(probeIdx);
    expect(returnIdxLocal).toBeLessThan(400);
  });

  test("runEnsureEditorWithFakeCli sets DXM_UNITY_SKIP_NATIVE_STARTUP_PROBE", () => {
    const source = readUtf8(HARNESS_TEST);
    const body = extractJsFunction(source, "runEnsureEditorWithFakeCli");
    expect(body).not.toBeNull();
    expect(body.includes(SKIP_ENV)).toBe(true);
  });

  test("every other test that drives ensure-editor.ps1 with a Unity.exe stub also skips the probe", () => {
    const entries = fs.readdirSync(TEST_DIR, { withFileTypes: true });
    const offenders = [];
    // A genuine fake-Unity-stub test both writes a `Unity.exe` file AND makes it
    // executable via chmodSync (so the kernel-shebang trick works on Unix). Tests
    // that merely mention the string `Unity.exe` in comments or assertions (e.g.
    // workflow-shape / script-contract tests that readScript the ps1) are NOT
    // stub harnesses and must not be flagged.
    // Three flavours of stub-write:
    //   - The shared helper `writeFakeUnityEditor` (preferred; ships with chmod).
    //   - Hand-rolled `chmodSync` near a `Unity.exe` literal (Unix-style stub).
    //   - Hand-rolled `writeFileSync` near a `Unity.exe` literal (chmod is a
    //     no-op on Windows; a future author may legitimately omit it). Without
    //     this third branch the guard has a silent false-negative window for
    //     Windows-targeted stub tests.
    const STUB_WRITE_PATTERN =
      /writeFakeUnityEditor\b|chmodSync\s*\([\s\S]{0,400}Unity\.exe|Unity\.exe[\s\S]{0,400}chmodSync\s*\(|writeFileSync\s*\([\s\S]{0,400}Unity\.exe|Unity\.exe[\s\S]{0,400}writeFileSync\s*\(/;
    for (const entry of entries) {
      if (!entry.isFile() || !entry.name.endsWith(".test.js")) {
        continue;
      }
      if (entry.name === SELF_BASENAME) {
        continue;
      }
      const filePath = path.join(TEST_DIR, entry.name);
      const source = readUtf8(filePath);
      if (!source.includes("ensure-editor.ps1")) {
        continue;
      }
      if (!STUB_WRITE_PATTERN.test(source)) {
        continue;
      }
      if (source.includes(ALLOW_MARKER)) {
        continue;
      }
      if (!source.includes(SKIP_ENV)) {
        offenders.push(entry.name);
      }
    }
    expect(offenders).toEqual([]);
  });
});
