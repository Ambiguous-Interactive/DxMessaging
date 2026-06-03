/**
 * @fileoverview Behavioral + AST-style guard for the 0xC0000135 /
 * STATUS_DLL_NOT_FOUND short-circuit in
 * scripts/unity/ensure-editor.ps1's `Ensure-UnityNativeStartupHealthy`.
 *
 * WHY THIS EXISTS: when the Windows loader cannot resolve a DLL Unity.exe
 * imports (overwhelmingly the Microsoft Visual C++ 2015-2022 Redistributable),
 * Unity.exe exits with -1073741515 / 0xC0000135. The previous behavior of
 * Ensure-UnityNativeStartupHealthy was to retry a MANAGED REINSTALL of Unity
 * on the failure -- futile, because the missing DLL is ON THE OS, not in the
 * Unity install tree. The fix is a SHORT-CIRCUIT: when
 * Test-IsNativeDllNotFound classifies the exit code as the canonical
 * STATUS_DLL_NOT_FOUND, ensure-editor.ps1:
 *   1. Emits a wrap-immune `::error::` annotation (Write-UnityHostPrereqAnnotation)
 *      that NAMES the operator-actionable remediation (run the bootstrap
 *      script, or trigger the runner-bootstrap workflow_dispatch) AND lists
 *      the DLLs Unity.exe imports so the missing prereq is obvious at a glance.
 *   2. Throws WITHOUT attempting the futile managed reinstall. The throw
 *      message also carries the canonical "0xC0000135 / STATUS_DLL_NOT_FOUND"
 *      tokens + the bootstrap script path + the runbook reference.
 * The short-circuit fires on BOTH the first-probe failure AND the post-repair
 * probe failure (a probe that succeeded the first time but failed
 * 0xC0000135 after a managed reinstall would otherwise be silently swallowed
 * by the repair flow; the post-repair short-circuit emits the same annotation
 * with `-RepairAttempted` so the message reflects the failed-reinstall context).
 *
 * STRATEGY:
 *   1. STATIC: the source MUST contain the short-circuit branch (Test-
 *      IsNativeDllNotFound call gating Write-UnityHostPrereqAnnotation +
 *      throw), with the right tokens, BEFORE the managed-reinstall branch
 *      AND inside the post-repair branch. AST-style assertions on the
 *      Ensure-UnityNativeStartupHealthy function body.
 *   2. BEHAVIORAL: dot-source the helpers and invoke
 *      Test-IsNativeDllNotFound + Write-UnityHostPrereqAnnotation directly
 *      with the canonical -1073741515 exit code. We CANNOT exercise the
 *      end-to-end short-circuit via a fake Unity.exe stub on Linux because
 *      OS exec truncates a negative exit code to 0..255 (53 in this case
 *      since 0xC0000135 mod 256 = 0x35) -- the probe would never see
 *      0xC0000135. Direct invocation of the helpers IS the behavioral
 *      coverage; it pins:
 *        - Test-IsNativeDllNotFound returns $true for -1073741515 (the int
 *          form of 0xC0000135) AND for the positive uint32 form.
 *        - Test-IsNativeDllNotFound returns $false for benign exit codes.
 *        - Write-UnityHostPrereqAnnotation emits the load-bearing tokens:
 *          ::error title=, "Unity", "STATUS_DLL_NOT_FOUND" via
 *          Get-NativeExitCodeDescription, "bootstrap-windows-runner.ps1",
 *          AND respects DXM_RUNNER_PREREQ_INSTALLED for the context-aware
 *          cause phrasing.
 *        - DXM_UNITY_FAKE_IMPORTS injects the comma-separated names into
 *          the annotation's "Unity.exe imports:" segment.
 *
 * pwsh is preinstalled on GitHub runners; locally-absent pwsh -> behavioral
 * tests skip with a console.warn; the always-on static assertions still
 * guarantee a zero-coverage regression cannot hide.
 *
 * @cross-platform-regression -- this marker gates the file on the
 * ubuntu/windows/macos targeted-attribution step of
 * .github/workflows/cross-platform-preflight.yml; enforced by
 * scripts/__tests__/cross-platform-preflight-coverage.test.js. The
 * DXM_UNITY_FAKE_IMPORTS / DXM_UNITY_FAKE_MISSING_IMPORTS split is
 * specifically Windows-sensitive because native Windows can resolve VC++ DLLs
 * from System32 and KERNEL32 from KnownDLLs.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");
const { maskCommentsAndStrings } = require("../lib/source-stripping");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const ENSURE_EDITOR = path.join(REPO_ROOT, "scripts", "unity", "ensure-editor.ps1");

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

if (!PWSH_PRESENT) {
  // eslint-disable-next-line no-console
  console.warn(
    "[unity-ensure-editor-host-prereq-shortcircuit] pwsh not found on PATH; skipping behavioral assertions."
  );
}

const workspaces = [];

afterAll(() => {
  for (const ws of workspaces) {
    try {
      fs.rmSync(ws, { recursive: true, force: true });
    } catch {
      // best-effort temp cleanup; never fail the suite on teardown.
    }
  }
});

function makeWorkspace() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-ensure-editor-shortcircuit-"));
  workspaces.push(dir);
  return dir;
}

function combined(result) {
  return ((result.stdout || "") + "\n" + (result.stderr || "")).replace(/\r\n/g, "\n");
}

// AST-extract a PowerShell function body by name via balanced brace scan.
// Mirrors the pattern in unity-native-startup-probe-isolation.test.js.
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

function listTestFilesUnder(root) {
  const files = [];
  if (!fs.existsSync(root)) {
    return files;
  }

  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    const absolutePath = path.join(root, entry.name);
    if (entry.isDirectory()) {
      files.push(...listTestFilesUnder(absolutePath));
    } else if (entry.isFile() && entry.name.endsWith(".test.js")) {
      files.push(absolutePath);
    }
  }

  return files;
}

function extractJestTestBlocks(source) {
  const blocks = [];
  const masked = maskCommentsAndStrings(source);
  const testCall = /\b(?:test|it)(?:\.(?:only|skip|todo|each))?\s*\(/g;
  for (const match of masked.matchAll(testCall)) {
    const openBrace = masked.indexOf("{", match.index);
    if (openBrace < 0) {
      continue;
    }

    let depth = 0;
    for (let i = openBrace; i < masked.length; i += 1) {
      const ch = masked[i];
      if (ch === "{") {
        depth += 1;
      } else if (ch === "}") {
        depth -= 1;
        if (depth === 0) {
          blocks.push(source.slice(match.index, i + 1));
          break;
        }
      }
    }
  }

  return blocks;
}

function getFakeImportsFromBlock(block) {
  const match = /DXM_UNITY_FAKE_IMPORTS\s*:\s*(["'`])([\s\S]*?)\1/.exec(block);
  if (!match) {
    return [];
  }

  return match[2]
    .split(",")
    .map((name) => name.trim())
    .filter(Boolean);
}

function isHostResolvableWindowsImport(name) {
  return /^(?:api-ms-win-[a-z0-9-]+|bcrypt|crypt32|d3d\d+|dxgi|gdi32|kernel32|msvcp140|ntdll|ole32|shell32|ucrtbase|user32|vcruntime140(?:_1)?)\.dll$/i.test(
    name
  );
}

let SCRIPT_TEXT;
beforeAll(() => {
  SCRIPT_TEXT = fs.readFileSync(ENSURE_EDITOR, "utf8");
});

// ===========================================================================
// STATIC source-shape guards (always run, even without pwsh)
// ===========================================================================
describe("ensure-editor.ps1 0xC0000135 short-circuit source shape", () => {
  test("the script under test exists", () => {
    expect(fs.existsSync(ENSURE_EDITOR)).toBe(true);
  });

  test("declares Test-IsNativeDllNotFound, Get-UnityNativeImports, Write-UnityHostPrereqAnnotation, Test-VcRedistGeneration", () => {
    expect(extractPowerShellFunction(SCRIPT_TEXT, "Test-IsNativeDllNotFound")).not.toBeNull();
    expect(extractPowerShellFunction(SCRIPT_TEXT, "Get-UnityNativeImports")).not.toBeNull();
    expect(
      extractPowerShellFunction(SCRIPT_TEXT, "Write-UnityHostPrereqAnnotation")
    ).not.toBeNull();
    // Test-VcRedistGeneration is the load-bearing classifier that decides
    // which VC++ generation to name in the cause-line (2010 vs 2015-2022 vs
    // both vs neither). Pinning its existence as an AST contract prevents
    // a future refactor from inlining the classification logic into
    // Write-UnityHostPrereqAnnotation without test coverage.
    expect(extractPowerShellFunction(SCRIPT_TEXT, "Test-VcRedistGeneration")).not.toBeNull();
  });

  test("Test-VcRedistGeneration classifies the four cases via MSVCP100/MSVCR100 + MSVCP140/VCRUNTIME140* patterns", () => {
    // Static-shape guard for the classifier's pattern set. A refactor that
    // dropped MSVCR100 (the C runtime DLL, paired with MSVCP100) from the
    // vc2010 pattern would silently mis-classify a host where MSVCR100 was
    // the missing DLL. Pin every load-bearing identifier here.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-VcRedistGeneration");
    expect(body).not.toBeNull();
    // The 2010-generation marker DLLs MUST appear in the classifier.
    expect(body).toContain("MSVCP100");
    expect(body).toContain("MSVCR100");
    // The modern-generation marker DLLs MUST appear in the classifier.
    expect(body).toContain("MSVCP140");
    expect(body).toContain("VCRUNTIME140");
    expect(body).toContain("VCRUNTIME140_1");
    // The four classification outputs must all be present.
    expect(body).toMatch(/'vc2010'/);
    expect(body).toMatch(/'vcmodern'/);
    expect(body).toMatch(/'both'/);
    expect(body).toMatch(/'neither'/);
  });

  test("Test-IsNativeDllNotFound compares against 'C0000135' hex string (NOT the 0xC0000135 int literal)", () => {
    // Compare on the canonical 8-char hex of the uint32 value -- comparing
    // [uint32]$normalized -eq 0xC0000135 silently coerces to Int32 because
    // PowerShell parses 0xC0000135 as the negative Int32 -1073741515 and the
    // [uint32] cast collapses the equality. The hex-string compare is the
    // bug-immune shape; this assertion guards against a refactor that
    // "simplifies" back to the buggy form.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-IsNativeDllNotFound");
    expect(body).not.toBeNull();
    expect(body).toMatch(/'C0000135'/);
    expect(body).toMatch(/\.ToString\('X8'\)/);
    // The function must compute the unsigned form so a negative Int32
    // (-1073741515) normalizes to the matching uint32 (3221225781).
    expect(body).toMatch(/\[uint32\]\s*\(\$ExitCode\s*\+\s*4294967296\)/);
  });

  test("Write-UnityHostPrereqAnnotation emits ::error title= and names bootstrap-windows-runner.ps1", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Write-UnityHostPrereqAnnotation");
    expect(body).not.toBeNull();
    // The wrap-immune annotation MUST use the GitHub Actions
    // ::error title=... syntax so it survives ConciseView's word wrap.
    expect(body).toContain("::error title=Unity");
    // The remediation MUST NAME the bootstrap script (so the operator can
    // copy-paste the path).
    expect(body).toContain("bootstrap-windows-runner.ps1");
    // The runbook MUST be linked.
    expect(body).toContain("docs/runbooks/unity-runners-after-transfer.md");
    // The annotation must branch on the preflight env var (context-aware
    // cause phrasing).
    expect(body).toMatch(/DXM_RUNNER_PREREQ_INSTALLED\s*-eq\s*'1'/);
    // The -RepairAttempted switch MUST be wired so the post-repair branch
    // can pass it in.
    expect(body).toMatch(/\[switch\]\s*\$RepairAttempted/);
  });

  test("Ensure-UnityNativeStartupHealthy short-circuits on first-probe 0xC0000135 BEFORE attempting repair", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Ensure-UnityNativeStartupHealthy");
    expect(body).not.toBeNull();

    // The first-probe short-circuit must fire BEFORE the
    // DXM_UNITY_DISABLE_EDITOR_REPAIR check (the comments document this
    // explicitly: "we therefore short-circuit BEFORE the DXM_UNITY_DISABLE_EDITOR_REPAIR
    // check too"). We anchor on the CODE form
    // `if ($env:DXM_UNITY_DISABLE_EDITOR_REPAIR -eq '1')` to avoid matching
    // a comment that mentions the env var name in prose.
    const firstProbeIdx = body.indexOf("Test-IsNativeDllNotFound -ExitCode $result.ExitCode");
    const disableRepairMatch = /if\s*\(\$env:DXM_UNITY_DISABLE_EDITOR_REPAIR\s*-eq\s*'1'\)/.exec(
      body
    );
    const repairCallMatch = /\$repaired\s*=\s*Repair-UnityEditorWithCiModules\b/.exec(body);
    expect(firstProbeIdx).toBeGreaterThan(-1);
    expect(disableRepairMatch).not.toBeNull();
    expect(repairCallMatch).not.toBeNull();
    expect(firstProbeIdx).toBeLessThan(disableRepairMatch.index);
    expect(firstProbeIdx).toBeLessThan(repairCallMatch.index);
  });

  test("first-probe short-circuit calls Write-UnityHostPrereqAnnotation WITHOUT -RepairAttempted, then throws", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Ensure-UnityNativeStartupHealthy");
    expect(body).not.toBeNull();
    // Single-line regex: the first-probe branch annotates THEN throws, in
    // that order, both inside the `if (Test-IsNativeDllNotFound ...)` block.
    // The first-probe call passes $result.ExitCode (not $repairProbe.ExitCode)
    // and MUST NOT carry -RepairAttempted.
    const firstProbePattern =
      /if \(Test-IsNativeDllNotFound -ExitCode \$result\.ExitCode\)\s*\{[\s\S]*?Write-UnityHostPrereqAnnotation[^}]*?\$result\.ExitCode[^}]*?\$result\.Description[\s\S]*?throw\s+"[\s\S]*?0xC0000135[\s\S]*?STATUS_DLL_NOT_FOUND[\s\S]*?\}/;
    expect(body).toMatch(firstProbePattern);
    // The first-probe Write-UnityHostPrereqAnnotation call must NOT include
    // -RepairAttempted (that switch is reserved for the post-repair branch).
    const firstProbeMatch = firstProbePattern.exec(body);
    expect(firstProbeMatch).not.toBeNull();
    expect(firstProbeMatch[0]).not.toContain("-RepairAttempted");
  });

  test("post-repair short-circuit calls Write-UnityHostPrereqAnnotation WITH -RepairAttempted, then throws", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Ensure-UnityNativeStartupHealthy");
    expect(body).not.toBeNull();
    // Single-line regex: the post-repair branch annotates with
    // -RepairAttempted THEN throws.
    const postRepairPattern =
      /if \(Test-IsNativeDllNotFound -ExitCode \$repairProbe\.ExitCode\)\s*\{[\s\S]*?Write-UnityHostPrereqAnnotation[\s\S]*?-RepairAttempted[\s\S]*?throw\s+"[\s\S]*?managed reinstall[\s\S]*?\}/;
    expect(body).toMatch(postRepairPattern);
  });

  test("throw messages name the bootstrap path AND the runbook", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Ensure-UnityNativeStartupHealthy");
    expect(body).not.toBeNull();
    // Both throws MUST name the operator-actionable bootstrap script.
    const throwBootstrapPattern = /throw\s+"[^"]*bootstrap-windows-runner\.ps1/g;
    const matches = body.match(throwBootstrapPattern) || [];
    expect(matches.length).toBeGreaterThanOrEqual(2);
    // Both throws MUST reference the runbook.
    expect(body).toMatch(/throw\s+"[^"]*unity-runners-after-transfer\.md/);
  });

  test("Test-UnityImportResolution function exists with the expected param shape", () => {
    // The resolver MUST accept -EditorPath and -Imports (the two pieces of
    // state the caller funnels in). A regression that renamed either
    // parameter would silently break the annotation pipeline; the AST guard
    // anchors the contract.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-UnityImportResolution");
    expect(body).not.toBeNull();
    // -EditorPath MUST be mandatory but accept empty strings (the resolver
    // is best-effort and must not throw when the caller has no path).
    expect(body).toMatch(
      /\[Parameter\(Mandatory\s*=\s*\$true\)\]\[AllowEmptyString\(\)\]\[string\]\$EditorPath/
    );
    // -Imports MUST accept empty collections (a partial PE walk may return
    // zero imports; the resolver should still return its empty-bucket
    // hashtable without throwing).
    expect(body).toMatch(/\[AllowEmptyCollection\(\)\]\[string\[\]\]\$Imports/);
    // The result hashtable MUST carry the documented buckets (the annotation
    // pulls each key by name; renaming any of them would silently drop a
    // resolved-count column).
    expect(body).toMatch(/missing\s*=\s*@\(\)/);
    expect(body).toMatch(/systemResolved\s*=\s*@\{\}/);
    expect(body).toMatch(/windowsResolved\s*=\s*@\{\}/);
    expect(body).toMatch(/unityResolved\s*=\s*@\{\}/);
    expect(body).toMatch(/pathResolved\s*=\s*@\{\}/);
    expect(body).toMatch(/knownDllsResolved\s*=\s*@\{\}/);
    // DXM_UNITY_FAKE_MISSING_IMPORTS test override MUST be honored.
    expect(body).toContain("DXM_UNITY_FAKE_MISSING_IMPORTS");
    // KnownDLLs lookup must be Windows-gated (DirectorySeparatorChar branch)
    // to keep the helper safe on Linux/macOS pwsh.
    expect(body).toMatch(/HKLM:\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\KnownDLLs/);
  });

  test("Test-UnityImportLooksUnityShipped exists and DOES NOT match OS-prereq DLLs", () => {
    // The Unity-shipped heuristic MUST exist (the annotation uses it to
    // route the "install corrupt" hint). The AST guard pins the patterns
    // for libfbxsdk / optix / OpenImageDenoise so a refactor that removes
    // them cannot silently regress the "set DXM_UNITY_FORCE_REINSTALL=1"
    // operator-actionable hint.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-UnityImportLooksUnityShipped");
    expect(body).not.toBeNull();
    expect(body).toContain("libfbxsdk");
    expect(body).toMatch(/optix/i);
    expect(body).toMatch(/openimagedenoise/i);
    // The heuristic must NOT match OS prereqs (those should still go to the
    // bootstrap-script remediation). We anchor on the absence of explicit
    // VCRUNTIME / MSVCP entries in the patterns array.
    expect(body).not.toMatch(/'\^vcruntime/i);
    expect(body).not.toMatch(/'\^msvcp/i);
  });

  test("Ensure-UnityNativeStartupHealthy short-circuit honors DXM_UNITY_FORCE_REINSTALL=1", () => {
    // Operator-opt-out: when the named-missing-DLL annotation has identified a
    // Unity-shipped DLL as the missing import, the operator can set
    // DXM_UNITY_FORCE_REINSTALL=1 to bypass the 0xC0000135 short-circuit and
    // re-trigger the managed reinstall. The bypass MUST be inside the
    // Test-IsNativeDllNotFound branch (no value when the failure is something
    // else), MUST be guarded by the literal "1" string (so a stray value
    // cannot accidentally bypass), and MUST emit a CI notice so the override
    // is visible in the log.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Ensure-UnityNativeStartupHealthy");
    expect(body).not.toBeNull();
    expect(body).toMatch(/\$env:DXM_UNITY_FORCE_REINSTALL\s*-eq\s*'1'/);
    // The bypass must NOT short-circuit -- it falls through to the existing
    // repair pipeline. Anchor that the throw is in the `else` branch of the
    // bypass check (i.e. when the env var is NOT '1').
    const bypassPattern =
      /if\s*\(\$env:DXM_UNITY_FORCE_REINSTALL\s*-eq\s*'1'\)\s*\{[\s\S]*?Write-CiNotice[\s\S]*?\}\s*else\s*\{[\s\S]*?Write-UnityHostPrereqAnnotation[\s\S]*?throw\s+"[\s\S]*?\}/;
    expect(body).toMatch(bypassPattern);
    // The first-probe throw must mention DXM_UNITY_FORCE_REINSTALL so the
    // operator sees the override hint at the moment the short-circuit fires.
    const firstThrowMatch = bypassPattern.exec(body);
    expect(firstThrowMatch).not.toBeNull();
    expect(firstThrowMatch[0]).toContain("DXM_UNITY_FORCE_REINSTALL");
  });

  test("Get-UnityNativeImports walks BOTH the import and delay-import directories", () => {
    // Delay-loaded imports surface 0xC0000135 the same as regular imports
    // when the OS loader cannot resolve them at module-init time. The walk
    // MUST inspect IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT (the
    // DelayImportTableDirectory accessor on PEHeader); a regression that
    // dropped the delay-import pass would silently hide a category of
    // missing DLLs from the annotation.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Get-UnityNativeImports");
    expect(body).not.toBeNull();
    expect(body).toContain("ImportTableDirectory");
    expect(body).toContain("DelayImportTableDirectory");
    // The descriptor walk must be 32 bytes per entry for the delay-import
    // directory (8 uint32 fields).
    expect(body).toMatch(/RemainingBytes\s*-ge\s*32/);
  });

  test("host-resolvable fake imports do not assert missing-DLL output without the fake-missing override", () => {
    const testsRoot = path.join(REPO_ROOT, "scripts", "__tests__");
    const violations = [];
    for (const filePath of listTestFilesUnder(testsRoot)) {
      const source = fs.readFileSync(filePath, "utf8");
      for (const block of extractJestTestBlocks(source)) {
        if (!/MISSING DLL\(s\)/.test(block) || /DXM_UNITY_FAKE_MISSING_IMPORTS\s*:/.test(block)) {
          continue;
        }

        const imports = getFakeImportsFromBlock(block);
        if (imports.length === 0 || !imports.every(isHostResolvableWindowsImport)) {
          continue;
        }

        violations.push(path.relative(REPO_ROOT, filePath).split(path.sep).join("/"));
      }
    }

    expect(violations).toEqual([]);
  });
});

// ===========================================================================
// BEHAVIORAL guards (only when pwsh is present)
// ===========================================================================
describe("ensure-editor.ps1 0xC0000135 helpers (behavioral)", () => {
  if (!PWSH_PRESENT) {
    test.skip("Test-IsNativeDllNotFound classifies -1073741515 as $true", () => {});
    test.skip("Test-IsNativeDllNotFound returns $false for benign exit codes", () => {});
    test.skip("Get-NativeExitCodeDescription maps 0xC0000135 to STATUS_DLL_NOT_FOUND", () => {});
    test.skip("Write-UnityHostPrereqAnnotation emits the canonical tokens", () => {});
    test.skip("Write-UnityHostPrereqAnnotation honors DXM_RUNNER_PREREQ_INSTALLED for cause phrasing", () => {});
    test.skip("Write-UnityHostPrereqAnnotation injects DXM_UNITY_FAKE_IMPORTS into the imports list", () => {});
    test.skip("Test-UnityImportResolution honors DXM_UNITY_FAKE_MISSING_IMPORTS", () => {});
    test.skip("Test-UnityImportResolution returns hashtable with every documented bucket", () => {});
    test.skip("Test-UnityImportResolution NEVER throws on a bogus editor path or empty imports", () => {});
    test.skip("Annotation: Unity-shipped missing DLL surfaces 'corrupt install' hint + DXM_UNITY_FORCE_REINSTALL", () => {});
    test.skip("Annotation: when ALL imports resolve, switches to transitive-dependency hint", () => {});
    test.skip("Annotation: 'Resolved: ...' diagnostic is always present and well-formed", () => {});
    test.skip("Annotation: missing MSVCP100 surfaces VC++ 2010 cause line (NOT 2015-2022)", () => {});
    test.skip("Annotation: missing BOTH 2010 + 2015-2022 markers surfaces BOTH cause line", () => {});
    test.skip("Annotation: missing only VCRUNTIME140 keeps the existing 2015-2022 cause line", () => {});
    test.skip("Test-VcRedistGeneration classifies vc2010 / vcmodern / both / neither correctly", () => {});
    return;
  }

  // AST-extract the diagnostic helpers from ensure-editor.ps1 and dot-source
  // ONLY those functions into a temp .ps1 (the full file has top-level
  // executable code, so dot-sourcing the whole file has side effects we want
  // to avoid). Pattern mirrors unity-ensure-editor-il2cpp-idempotency.test.js.
  function buildHarness(bodyLines) {
    const escapedPath = ENSURE_EDITOR.replace(/'/g, "''");
    const wanted = [
      "Test-IsNativeDllNotFound",
      "Get-NativeExitCodeDescription",
      "Get-UnityNativeImports",
      "Test-UnityImportResolution",
      "Test-UnityImportLooksUnityShipped",
      "Write-UnityHostPrereqAnnotation",
      "Test-VcRedistGeneration",
      "Write-CiNotice"
    ];
    const wantedLiteral = wanted.map((n) => `'${n}'`).join(", ");
    return [
      "Set-StrictMode -Version Latest",
      "$ErrorActionPreference = 'Stop'",
      `$src = '${escapedPath}'`,
      `$wanted = @(${wantedLiteral})`,
      "$tokens = $null; $errs = $null",
      "$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errs)",
      "$functions = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $n.Name }, $true)",
      'foreach ($name in $wanted) { if (-not ($functions | Where-Object { $_.Name -eq $name })) { Write-Error "Function $name not found"; exit 3 } }',
      "foreach ($fn in $functions) { Invoke-Expression $fn.Extent.Text }",
      ...bodyLines
    ].join("\n");
  }

  function runHarness(bodyLines, extraEnv = {}) {
    const workspace = makeWorkspace();
    const harness = path.join(workspace, "harness.ps1");
    fs.writeFileSync(harness, buildHarness(bodyLines), "utf8");
    return spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", harness], {
      encoding: "utf8",
      maxBuffer: 16 * 1024 * 1024,
      env: { ...process.env, ...extraEnv }
    });
  }

  test("Test-IsNativeDllNotFound classifies -1073741515 (canonical Int32 form) as $true", () => {
    const result = runHarness([
      "Write-Output ([bool](Test-IsNativeDllNotFound -ExitCode -1073741515))"
    ]);
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    expect((result.stdout || "").trim()).toBe("True");
  });

  test("Test-IsNativeDllNotFound returns $false for benign exit codes (0, 1, 6, 53)", () => {
    // 53 is what `exit -1073741515` truncates to on a Linux OS exec --
    // we MUST NOT match it (false positive would let unrelated failures
    // silently take the short-circuit path).
    const result = runHarness([
      "Write-Output ([bool](Test-IsNativeDllNotFound -ExitCode 0))",
      "Write-Output ([bool](Test-IsNativeDllNotFound -ExitCode 1))",
      "Write-Output ([bool](Test-IsNativeDllNotFound -ExitCode 6))",
      "Write-Output ([bool](Test-IsNativeDllNotFound -ExitCode 53))"
    ]);
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const lines = (result.stdout || "").trim().split(/\r?\n/);
    expect(lines).toEqual(["False", "False", "False", "False"]);
  });

  test("Get-NativeExitCodeDescription maps -1073741515 to '0xC0000135 / STATUS_DLL_NOT_FOUND'", () => {
    const result = runHarness([
      "Write-Output (Get-NativeExitCodeDescription -ExitCode -1073741515)"
    ]);
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = (result.stdout || "").trim();
    expect(out).toContain("0xC0000135");
    expect(out).toContain("STATUS_DLL_NOT_FOUND");
  });

  test("Write-UnityHostPrereqAnnotation (preflight NOT run) emits VC++ remediation tokens", () => {
    // Reproduces the FIRST-PROBE failure case (preflight never ran or
    // failed). The annotation MUST name VC++ Redistributable as the most
    // likely cause AND direct the operator to run the bootstrap script.
    // We funnel Write-Host through a captured 6>&1 stream so we can grep
    // the emitted ::error:: line.
    const result = runHarness(
      [
        "$out = & {",
        "  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '/nonexistent/Unity.exe' -ProbeLog '/tmp/probe.log'",
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      { DXM_RUNNER_PREREQ_INSTALLED: "" }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    // Wrap-immune ::error title= annotation.
    expect(out).toMatch(/::error title=Unity 6000\.0\.32f1 host prerequisite missing::/);
    // First-probe causal phrasing: VC++ Redistributable is the suspect.
    expect(out).toContain("Microsoft Visual C++");
    expect(out).toContain("Redistributable");
    // The remediation directs the operator at the bootstrap script.
    expect(out).toContain("bootstrap-windows-runner.ps1");
    expect(out).toContain("runner-bootstrap.yml");
    // The runbook is linked.
    expect(out).toContain("unity-runners-after-transfer.md");
  });

  test("Write-UnityHostPrereqAnnotation (preflight DID run) flips to 'DIFFERENT missing DLL' phrasing", () => {
    // Preflight already installed VC++ this job (the composite would have
    // set DXM_RUNNER_PREREQ_INSTALLED=1); the annotation MUST NOT tell the
    // operator to run the bootstrap script again -- it must instead point
    // at the imports list and the runbook for further investigation.
    const result = runHarness(
      [
        "$out = & {",
        "  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '/nonexistent/Unity.exe'",
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      { DXM_RUNNER_PREREQ_INSTALLED: "1" }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    expect(out).toContain("Preflight ran successfully");
    expect(out).toContain("DIFFERENT missing DLL");
    // The "run the bootstrap" phrasing MUST be absent on this path
    // (running it again wouldn't help -- VC++ is already installed).
    expect(out).not.toMatch(/Fix: run scripts\/unity\/bootstrap-windows-runner\.ps1/);
    expect(out).not.toMatch(/Fix: run .* bootstrap-windows-runner\.ps1/);
    // The runbook is still linked.
    expect(out).toContain("unity-runners-after-transfer.md");
  });

  test("Write-UnityHostPrereqAnnotation -RepairAttempted phrases as 'managed reinstall already ran'", () => {
    // Post-repair short-circuit: the managed reinstall already ran and did
    // not help (as expected for 0xC0000135 -- the missing DLL is on the
    // OS, not in the Unity install). The annotation must say so.
    const result = runHarness(
      [
        "$out = & {",
        "  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '/nonexistent/Unity.exe' -RepairAttempted",
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      { DXM_RUNNER_PREREQ_INSTALLED: "" }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    expect(out).toContain("managed reinstall already ran and did not help");
    expect(out).toContain("0xC0000135");
  });

  test("DXM_UNITY_FAKE_IMPORTS and DXM_UNITY_FAKE_MISSING_IMPORTS make missing-DLL annotation deterministic", () => {
    // The test override (documented in Get-UnityNativeImports) lets a
    // hermetic Linux/macOS test prove the annotation surfaces the import
    // list WITHOUT smuggling a real PE binary into the repo.
    //
    // Important: DXM_UNITY_FAKE_IMPORTS only supplies the import list. On a
    // native Windows runner, VCRUNTIME/MSVCP can legitimately resolve from
    // System32 and KERNEL32 can resolve via KnownDLLs. DXM_UNITY_FAKE_MISSING_IMPORTS
    // is the host-independent control that forces this test down the
    // "MISSING DLL(s): <names>" branch.
    const result = runHarness(
      [
        "$out = & {",
        "  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '/nonexistent/Unity.exe'",
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      {
        DXM_UNITY_FAKE_IMPORTS: "VCRUNTIME140.dll,VCRUNTIME140_1.dll,MSVCP140.dll,KERNEL32.dll",
        DXM_UNITY_FAKE_MISSING_IMPORTS:
          "VCRUNTIME140.dll,VCRUNTIME140_1.dll,MSVCP140.dll,KERNEL32.dll",
        DXM_RUNNER_PREREQ_INSTALLED: ""
      }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    // The "MISSING DLL(s):" segment must name every forced-missing import
    // regardless of the host OS or installed redistributable packages.
    expect(out).toMatch(/MISSING DLL\(s\):/);
    expect(out).toContain("VCRUNTIME140.dll");
    expect(out).toContain("VCRUNTIME140_1.dll");
    expect(out).toContain("MSVCP140.dll");
    expect(out).toContain("KERNEL32.dll");
    // The "Resolved:" diagnostic must always appear with the total import count.
    expect(out).toMatch(/Resolved:.*out of 4 total imports/);
  });

  test("Test-UnityImportResolution honors DXM_UNITY_FAKE_MISSING_IMPORTS", () => {
    // R3 (round-3 review minor) STRENGTHENED: the previous version of this
    // test set the override and asserted alpha+gamma appear in .missing -- but
    // on Linux ALL imports route to .missing via the real probe (no System32 /
    // Windows / Unity install dir), so the assertion passed for the wrong
    // reason (a mutation that removed the override entirely still passed).
    //
    // This version uses a synthetic editor workspace where alpha.dll and
    // gamma.dll EXIST on disk alongside the editor binary, so the real probe
    // would route them to .unityResolved. The override has to be active for
    // them to land in .missing -- which is exactly what we want to assert.
    const workspace = makeWorkspace();
    const editorPath = path.join(workspace, "editor.bin");
    fs.writeFileSync(editorPath, "stub", "utf8");
    // alpha.dll and gamma.dll exist in the editor dir, so the real loader
    // probe would resolve them. beta.dll does NOT exist; on Linux it would
    // route to .missing via the real probe regardless of the override.
    fs.writeFileSync(path.join(workspace, "alpha.dll"), "stub", "utf8");
    fs.writeFileSync(path.join(workspace, "gamma.dll"), "stub", "utf8");

    const escapedPath = editorPath.replace(/'/g, "''");

    // Baseline run: NO override. alpha + gamma must resolve via the editor
    // dir; only beta should be missing. Validates the real probe works.
    const baseline = runHarness([
      `$res = Test-UnityImportResolution -EditorPath '${escapedPath}' -Imports @('alpha.dll', 'beta.dll', 'gamma.dll')`,
      "Write-Output ('missing=' + (($res.missing) -join ','))",
      "Write-Output ('unityResolved=' + ([string[]]($res.unityResolved.Keys) -join ','))"
    ]);
    if (baseline.status !== 0) {
      throw new Error(combined(baseline));
    }
    const baselineOut = (baseline.stdout || "").trim();
    expect(baselineOut).toContain("missing=beta.dll");
    expect(baselineOut).toContain("alpha.dll");
    expect(baselineOut).toContain("gamma.dll");
    expect(baselineOut).not.toMatch(/missing=[^\n]*alpha\.dll/);
    expect(baselineOut).not.toMatch(/missing=[^\n]*gamma\.dll/);

    // Override run: alpha.dll and gamma.dll are EXPLICITLY forced into
    // .missing. The override must fire BEFORE the real probe -- so even
    // though both files exist on disk, they land in .missing not
    // .unityResolved. Mutation-safe: a future regression that removes the
    // override entirely makes this case fail because the real probe would
    // resolve alpha + gamma via the editor dir.
    const overridden = runHarness(
      [
        `$res = Test-UnityImportResolution -EditorPath '${escapedPath}' -Imports @('alpha.dll', 'beta.dll', 'gamma.dll')`,
        "Write-Output ('missing=' + (($res.missing) -join ','))",
        "Write-Output ('unityResolved=' + ([string[]]($res.unityResolved.Keys) -join ','))"
      ],
      { DXM_UNITY_FAKE_MISSING_IMPORTS: "alpha.dll,gamma.dll" }
    );
    if (overridden.status !== 0) {
      throw new Error(combined(overridden));
    }
    const overriddenOut = (overridden.stdout || "").trim();
    expect(overriddenOut).toContain("alpha.dll");
    expect(overriddenOut).toContain("gamma.dll");
    // Critical: alpha + gamma must NOT appear in unityResolved (the override
    // short-circuited the real probe).
    expect(overriddenOut).not.toMatch(/unityResolved=[^\n]*alpha\.dll/);
    expect(overriddenOut).not.toMatch(/unityResolved=[^\n]*gamma\.dll/);
  });

  test("Test-UnityImportResolution returns hashtable with every documented bucket", () => {
    // The result shape MUST include every key the annotation reads
    // (.missing, .systemResolved, .windowsResolved, .unityResolved,
    // .pathResolved, .knownDllsResolved). Under StrictMode (Set-StrictMode
    // -Version Latest), reading an undefined property throws; the helper
    // therefore must pre-initialize ALL keys.
    const result = runHarness([
      "$res = Test-UnityImportResolution -EditorPath '/tmp/Unity.exe' -Imports @()",
      "Write-Output ('keys=' + (($res.Keys | Sort-Object) -join ','))"
    ]);
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = (result.stdout || "").trim();
    // Sorted alphabetically: knownDllsResolved, missing, pathResolved,
    // systemResolved, unityResolved, windowsResolved.
    expect(out).toContain("knownDllsResolved");
    expect(out).toContain("missing");
    expect(out).toContain("pathResolved");
    expect(out).toContain("systemResolved");
    expect(out).toContain("unityResolved");
    expect(out).toContain("windowsResolved");
  });

  test("Test-UnityImportResolution NEVER throws on a bogus editor path or empty imports", () => {
    // Best-effort contract: any failure inside the resolver must NOT throw.
    // A pwsh failure here would mask the underlying 0xC0000135 throw the
    // caller is about to raise. Probe three paths:
    //   (a) empty EditorPath + empty Imports list -> resolver must
    //       short-circuit with empty missing.
    //   (b) bogus editor path + one import -> the import doesn't resolve
    //       anywhere, so it lands in .missing.
    //   (c) empty EditorPath + one import -> same as (b); the resolver MUST
    //       NOT throw when -EditorPath is the empty string (the parameter
    //       is declared with [AllowEmptyString()] for exactly this reason).
    const result = runHarness([
      "$res = Test-UnityImportResolution -EditorPath '' -Imports @()",
      "$res2 = Test-UnityImportResolution -EditorPath '/no/such/path/Unity.exe' -Imports @('foo.dll')",
      "$res3 = Test-UnityImportResolution -EditorPath '' -Imports @('alpha.dll')",
      "Write-Output ('a=' + $res.missing.Count)",
      "Write-Output ('b=' + $res2.missing.Count)",
      "Write-Output ('c=' + $res3.missing.Count)"
    ]);
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const lines = (result.stdout || "").trim().split(/\r?\n/);
    // Empty imports -> .missing is empty; bogus path + one import -> .missing
    // has the one entry (it doesn't resolve anywhere); empty path + one
    // import -> same (the resolver doesn't crash on missing $editorDir).
    expect(lines).toContain("a=0");
    expect(lines).toContain("b=1");
    expect(lines).toContain("c=1");
  });

  test("Annotation: Unity-shipped missing DLL surfaces 'corrupt install' hint + DXM_UNITY_FORCE_REINSTALL", () => {
    // When a missing DLL matches the Unity-shipped heuristic (libfbxsdk,
    // optix, etc.), the annotation MUST suggest the install is corrupt AND
    // name DXM_UNITY_FORCE_REINSTALL as the override. This pins the
    // operator-actionable bypass for the 0xC0000135 short-circuit.
    const result = runHarness(
      [
        "$out = & {",
        "  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '/nonexistent/Unity.exe'",
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      {
        DXM_UNITY_FAKE_IMPORTS: "libfbxsdk.dll,VCRUNTIME140.dll,KERNEL32.dll",
        DXM_RUNNER_PREREQ_INSTALLED: ""
      }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    // "MISSING DLL(s):" segment present with the Unity-shipped name.
    expect(out).toMatch(/MISSING DLL\(s\):/);
    expect(out).toContain("libfbxsdk.dll");
    // Unity-shipped sub-hint: corrupt install + reinstall guidance.
    expect(out).toMatch(/Unity-shipped third-party libraries/);
    expect(out).toMatch(/partial or corrupt/);
    // The override env var MUST be surfaced in the annotation so the
    // operator can copy-paste it.
    expect(out).toContain("DXM_UNITY_FORCE_REINSTALL=1");
  });

  test("Annotation: when ALL imports resolve, switches to transitive-dependency hint", () => {
    // When every import resolves on the loader search path yet the OS loader
    // still failed, the annotation MUST point at transitive deps / EDR /
    // malformed binary. We synthesize the all-resolve state by dropping a
    // file with the import name next to a synthetic editor binary so the
    // unityResolved bucket catches it. The synthetic editor file is named
    // `editor.bin` so the unity-native-startup-probe-isolation guard does
    // not flag this test as a fake-stub harness -- the test dot-sources the
    // diagnostic helpers and never executes the editor; the resolver only
    // takes Split-Path -Parent of the path string.
    const workspace = makeWorkspace();
    fs.writeFileSync(path.join(workspace, "alpha.dll"), "fake", "utf8");
    fs.writeFileSync(path.join(workspace, "editor.bin"), "fake", "utf8");
    const editorPath = path.join(workspace, "editor.bin");
    const editorPathLiteral = editorPath.replace(/'/g, "''");
    const result = runHarness(
      [
        "$out = & {",
        `  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '${editorPathLiteral}'`,
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      {
        // alpha.dll lives next to Unity.exe -> resolves via the
        // unityResolved bucket. No missing entries -> annotation flips to
        // the all-resolve phrasing.
        DXM_UNITY_FAKE_IMPORTS: "alpha.dll",
        DXM_RUNNER_PREREQ_INSTALLED: ""
      }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    expect(out).toMatch(/All Unity\.exe imports resolve on the loader search path/);
    expect(out).toMatch(/transitive dependency/);
    expect(out).toMatch(/gflags\.exe -i Unity\.exe \+sls/);
    // No "MISSING DLL(s):" on this path.
    expect(out).not.toMatch(/MISSING DLL\(s\):/);
  });

  test("Annotation: 'Resolved: ...' diagnostic is always present and well-formed", () => {
    // The "Resolved:" segment is a load-bearing single-line tally the
    // operator uses to validate "how much of the import list resolved" at
    // a glance. Pin its shape: must contain "Resolved:" + the per-bucket
    // counts (system/editor/Windows/PATH/KnownDLLs) + "out of N total
    // imports".
    const result = runHarness(
      [
        "$out = & {",
        "  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '/nonexistent/Unity.exe'",
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      {
        DXM_UNITY_FAKE_IMPORTS: "foo.dll,bar.dll,baz.dll",
        DXM_RUNNER_PREREQ_INSTALLED: ""
      }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    expect(out).toMatch(
      /Resolved:.*system.*editor.*Windows.*PATH.*KnownDLLs.*out of 3 total imports/
    );
  });

  // ===========================================================================
  // VC++ generation classification (Test-VcRedistGeneration + cause-line phrasing)
  //
  // Production run 70874414898 identified MSVCP100.dll (from the VC++ 2010 SP1
  // generation) as the load-bearing missing DLL on both self-hosted Windows
  // runners. The previous annotation hard-coded "missing VC++ 2015-2022
  // Redistributable" which was wrong for MSVCP100. The 2010 generation is a
  // SEPARATE Microsoft package -- the bootstrap's `vcredist-2015-2022` step
  // alone does NOT install MSVCP100. The tests below pin the four
  // classification outcomes (vc2010 / vcmodern / both / neither) AND the
  // exact wording each branch produces so a refactor that flipped the cause
  // line (or dropped a marker DLL from the pattern set) fails loudly.
  // ===========================================================================

  test("Test-VcRedistGeneration classifies the four cases (vc2010 / vcmodern / both / neither)", () => {
    // Direct behavioral test of the classifier. Tests every documented case
    // with both `.dll`-suffixed and bare names + a case-insensitive variant
    // to cover the AllowEmptyCollection / case-insensitive contract.
    const result = runHarness([
      "Write-Output ('case1=' + (Test-VcRedistGeneration -MissingDlls @('MSVCP100.dll')))",
      "Write-Output ('case2=' + (Test-VcRedistGeneration -MissingDlls @('msvcr100')))",
      "Write-Output ('case3=' + (Test-VcRedistGeneration -MissingDlls @('VCRUNTIME140_1.dll')))",
      "Write-Output ('case4=' + (Test-VcRedistGeneration -MissingDlls @('MSVCP140.dll')))",
      "Write-Output ('case5=' + (Test-VcRedistGeneration -MissingDlls @('MSVCP100.dll', 'VCRUNTIME140_1.dll')))",
      "Write-Output ('case6=' + (Test-VcRedistGeneration -MissingDlls @('KERNEL32.dll', 'CRYPT32.dll')))",
      "Write-Output ('case7=' + (Test-VcRedistGeneration -MissingDlls @()))"
    ]);
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const lines = (result.stdout || "").trim().split(/\r?\n/);
    expect(lines).toContain("case1=vc2010"); // MSVCP100.dll alone
    expect(lines).toContain("case2=vc2010"); // bare 'msvcr100' (case-insensitive, no .dll)
    expect(lines).toContain("case3=vcmodern"); // VCRUNTIME140_1.dll
    expect(lines).toContain("case4=vcmodern"); // MSVCP140.dll
    expect(lines).toContain("case5=both"); // MSVCP100 + VCRUNTIME140_1
    expect(lines).toContain("case6=neither"); // OS DLLs (KERNEL32, CRYPT32)
    expect(lines).toContain("case7=neither"); // empty input
  });

  test("Annotation: missing MSVCP100 surfaces VC++ 2010 cause line (NOT 2015-2022)", () => {
    // The load-bearing fix for production run 70874414898: when the
    // resolver identifies MSVCP100 as the missing DLL, the annotation MUST
    // name the VC++ 2010 Redistributable -- NOT the 2015-2022 generation
    // (which is a separate Microsoft package that won't help here). The
    // operator copy-pastes this remediation; getting the generation wrong
    // sends them down a fruitless install path.
    const result = runHarness(
      [
        "$out = & {",
        "  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '/nonexistent/Unity.exe' -ProbeLog '/tmp/probe.log'",
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      {
        DXM_UNITY_FAKE_IMPORTS: "MSVCP100.dll,MSVCR100.dll",
        DXM_UNITY_FAKE_MISSING_IMPORTS: "MSVCP100.dll,MSVCR100.dll",
        DXM_RUNNER_PREREQ_INSTALLED: ""
      }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    // The cause line MUST name VC++ 2010 Redistributable.
    expect(out).toContain("Visual C++ 2010 Redistributable");
    // And MUST NOT name VC++ 2015-2022 Redistributable as the cause
    // (because the resolver knows the missing DLL is 2010-era; mis-naming
    // the generation here is the exact production bug we're guarding).
    // We anchor the negative on "Most likely cause" prefix to allow the
    // "BOTH" / "neither" fallback wording (which can mention 2015-2022
    // alongside 2010) but reject the standalone vcmodern cause line.
    expect(out).not.toMatch(
      /Most likely cause:\s*missing Microsoft Visual C\+\+ 2015-2022 Redistributable \(x64\)\.\s+Run/
    );
    // The MISSING DLL annotation must still name MSVCP100.
    expect(out).toContain("MSVCP100.dll");
  });

  test("Annotation: missing BOTH 2010 + 2015-2022 markers surfaces BOTH cause line", () => {
    // When the resolver identifies marker DLLs from BOTH generations, the
    // annotation tells the operator BOTH packages are missing -- this is the
    // worst-case host state and the bootstrap installs both in one pass.
    const result = runHarness(
      [
        "$out = & {",
        "  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '/nonexistent/Unity.exe' -ProbeLog '/tmp/probe.log'",
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      {
        DXM_UNITY_FAKE_IMPORTS: "MSVCP100.dll,VCRUNTIME140_1.dll,MSVCP140.dll",
        DXM_UNITY_FAKE_MISSING_IMPORTS: "MSVCP100.dll,VCRUNTIME140_1.dll,MSVCP140.dll",
        DXM_RUNNER_PREREQ_INSTALLED: ""
      }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    // Must surface BOTH generations explicitly.
    expect(out).toMatch(/BOTH 2010 AND 2015-2022/);
    expect(out).toContain("install both");
    expect(out).toContain("MSVCP100.dll");
    expect(out).toContain("VCRUNTIME140_1.dll");
  });

  test("Annotation: missing only modern (VCRUNTIME140) keeps the existing VC++ 2015-2022 cause line", () => {
    // Backward-compatibility pin: when the missing DLLs are entirely from
    // the modern generation, the cause line MUST keep the existing
    // "missing Microsoft Visual C++ 2015-2022 Redistributable (x64)"
    // wording (so existing operator runbooks / training materials remain
    // accurate). The original annotation phrasing for this branch is
    // unchanged; we test it specifically to guard the existing case.
    const result = runHarness(
      [
        "$out = & {",
        "  Write-UnityHostPrereqAnnotation -Version '6000.0.32f1' -ExitCode -1073741515 -Description '0xC0000135 / STATUS_DLL_NOT_FOUND' -EditorPath '/nonexistent/Unity.exe' -ProbeLog '/tmp/probe.log'",
        "} *>&1 | Out-String",
        "Write-Output $out"
      ],
      {
        DXM_UNITY_FAKE_IMPORTS: "VCRUNTIME140.dll,VCRUNTIME140_1.dll,MSVCP140.dll",
        DXM_UNITY_FAKE_MISSING_IMPORTS: "VCRUNTIME140.dll,VCRUNTIME140_1.dll,MSVCP140.dll",
        DXM_RUNNER_PREREQ_INSTALLED: ""
      }
    );
    if (result.status !== 0) {
      throw new Error(combined(result));
    }
    const out = combined(result);
    // Keep the existing 2015-2022 wording exactly.
    expect(out).toContain("Visual C++ 2015-2022 Redistributable");
    // The 2010 wording MUST NOT appear when only modern DLLs are missing
    // (only the 2010-specific branch's MOST-LIKELY-CAUSE phrasing; we still
    // allow incidental 2010 mention elsewhere if the comment text grows).
    expect(out).not.toMatch(
      /Most likely cause:\s*missing Microsoft Visual C\+\+ 2010 Redistributable \(x64\)/
    );
  });
});
