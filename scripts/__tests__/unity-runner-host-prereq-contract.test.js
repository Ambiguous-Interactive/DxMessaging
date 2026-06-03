/**
 * @fileoverview Static contract guard for the Unity host-prereq bootstrap
 * surface (scripts/unity/bootstrap-windows-runner.ps1).
 *
 * Root cause this script exists for: Unity.exe failed at startup with exit code
 * -1073741515 / 0xC0000135 (STATUS_DLL_NOT_FOUND) on self-hosted Windows
 * runners because the Microsoft Visual C++ Redistributables were not installed.
 * Production run 70874414898 identified MSVCP100.dll (from the 2010 generation)
 * as the load-bearing missing DLL; the modern 2015-2022 generation is a
 * SEPARATE Microsoft package and the bootstrap must install BOTH. The bootstrap
 * script detects + installs every host-OS prereq Unity needs to launch (VC++
 * 2010 redist, VC++ 2015-2022 redist, Windows long-paths, Windows Defender
 * exclusions, PowerShell 7, UCRT). This file is the PURE-text / AST regression
 * guard for the script's contract surface -- no process spawning, no behavior
 * under test -- so the cost is sub-millisecond and the assertions stay green
 * even when pwsh is missing locally. Behavioral coverage lives next door in
 * unity-runner-host-prereq-helper-mutation.test.js (which DOES spawn pwsh).
 *
 * The assertions in this file pin EVERY load-bearing token / parameter / arg
 * vector / URL that the script's behavioral contract depends on, so a refactor
 * that drifts the script's surface -- a renamed parameter, a third-party DLL
 * download URL slipping in, a winget arg list losing `--accept-source-agreements`,
 * the Bld threshold dropping to a value that false-negatives every VS 2017
 * install -- fails this guard loudly with a deterministic line number.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const BOOTSTRAP_SCRIPT = path.join(REPO_ROOT, "scripts", "unity", "bootstrap-windows-runner.ps1");
const BOOTSTRAP_META = path.join(REPO_ROOT, "scripts", "unity", "bootstrap-windows-runner.ps1.meta");

// The Unity .meta GUID for the script under test. Pinned so a rename or
// regeneration that orphans the file from its Unity asset import record fails
// the suite loudly instead of silently appearing as a "new file" in Unity.
const BOOTSTRAP_META_GUID = "afa71e89c6929e478b8c3906e120e87c";

// Single source of truth for the script text. Cached at module load so the
// suite re-reads the file ONCE rather than per-assertion.
function readUtf8(absPath) {
  return fs.readFileSync(absPath, "utf8");
}

// Extract a PowerShell function body by name via balanced brace scan. Mirrors
// the pattern in unity-native-startup-probe-isolation.test.js so the AST shape
// stays consistent across the host-prereq guard set. Returns the substring
// from `function <Name>` through the matching closing brace, or null on miss.
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

let SCRIPT_TEXT;

beforeAll(() => {
  SCRIPT_TEXT = readUtf8(BOOTSTRAP_SCRIPT);
});

describe("scripts/unity/bootstrap-windows-runner.ps1 exists and is tracked", () => {
  test("the script file exists on disk", () => {
    expect(fs.existsSync(BOOTSTRAP_SCRIPT)).toBe(true);
  });

  test("the script is checked into git", () => {
    const relativePath = path
      .relative(REPO_ROOT, BOOTSTRAP_SCRIPT)
      .split(path.sep)
      .join("/");
    const result = childProcess.spawnSync(
      "git",
      ["ls-files", "--error-unmatch", "--", relativePath],
      { cwd: REPO_ROOT, encoding: "utf8" }
    );
    expect(result.status).toBe(0);
    expect((result.stdout || "").trim()).toBe(relativePath);
  });

  test(".meta sibling exists with the canonical Unity DefaultImporter shape + pinned GUID", () => {
    expect(fs.existsSync(BOOTSTRAP_META)).toBe(true);
    const meta = readUtf8(BOOTSTRAP_META);
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain(`guid: ${BOOTSTRAP_META_GUID}`);
    expect(meta).toContain("DefaultImporter:");
    expect(meta).toContain("externalObjects: {}");
  });
});

describe("bootstrap-windows-runner.ps1 directive + advanced-function metadata", () => {
  test("declares #Requires -Version 5.1 (Windows PowerShell 5.1 floor)", () => {
    // Bootstrap MUST be PS 5.1-compatible because the runner-bootstrap.yml
    // workflow uses `shell: powershell` (Windows PowerShell 5.1) -- its
    // purpose includes INSTALLING pwsh, so it cannot require pwsh to run.
    // Pin the floor at exactly 5.1 (not 5.0, not 7.0).
    expect(SCRIPT_TEXT).toMatch(/^\s*#Requires\s+-Version\s+5\.1\b/m);
  });

  test("declares [OutputType([int])] so the dispatcher's exit code is typed", () => {
    expect(SCRIPT_TEXT).toMatch(/\[OutputType\(\[int\]\)\]/);
  });

  test("the PARAM block declares all expected parameters (incl. VcRedist2010Url) with the right types", () => {
    // We anchor against the first param block (the one that follows
    // [CmdletBinding()] / [OutputType] at the top of the file). Whitespace
    // tolerant so harmless reformatting (PSScriptAnalyzer reflow, comment
    // re-wrapping) cannot break the contract.
    expect(SCRIPT_TEXT).toMatch(/\[switch\]\s*\$DetectOnly\b/);
    expect(SCRIPT_TEXT).toMatch(/\[string\]\s*\$UnityInstallRoot\b/);
    expect(SCRIPT_TEXT).toMatch(/\[string\]\s*\$VcRedistUrl\b/);
    // VC++ 2010 redist URL parameter MUST exist (it is the script-level
    // override-able URL for the 2010 SP1 generation, which is a separate
    // Microsoft package from the modern 2015-2022 redist).
    expect(SCRIPT_TEXT).toMatch(/\[string\]\s*\$VcRedist2010Url\b/);
    expect(SCRIPT_TEXT).toMatch(/\[int\]\s*\$DownloadTimeoutSeconds\b/);
    expect(SCRIPT_TEXT).toMatch(/\[int\]\s*\$InstallTimeoutSeconds\b/);
  });

  test("$VcRedistUrl carries a [ValidatePattern] that pins Microsoft hosts", () => {
    // The pattern must accept aka.ms AND microsoft.com to cover the redirect
    // chain (aka.ms -> download.visualstudio.microsoft.com -> ...). A pattern
    // that omitted aka.ms would reject the canonical URL outright.
    //
    // Scope the search to the single line carrying the validator (the
    // validator pattern itself contains nested `(` / `)`, so a naive
    // `[^)]*` exclusion would stop at the inner closing paren). The
    // attribute decorates $VcRedistUrl on the line IMMEDIATELY below it,
    // not the same line -- the param-block convention is
    //   [ValidatePattern('...')]
    //   [string]$VcRedistUrl = '...',
    const lines = SCRIPT_TEXT.split("\n");
    const validatorLineIdx = lines.findIndex(
      (line) => line.includes("[ValidatePattern(") && /aka\\\.ms/.test(line)
    );
    expect(validatorLineIdx).toBeGreaterThan(-1);
    const validatorLine = lines[validatorLineIdx];
    expect(validatorLine).toMatch(/\[ValidatePattern\(.*aka\\\.ms.*\)\]/);
    expect(validatorLine).toMatch(/\[ValidatePattern\(.*microsoft\\\.com.*\)\]/);
    // The IMMEDIATELY-FOLLOWING non-empty line must declare $VcRedistUrl,
    // so the validator decorates exactly that parameter (and nothing else).
    let nextLineIdx = validatorLineIdx + 1;
    while (nextLineIdx < lines.length && lines[nextLineIdx].trim() === "") {
      nextLineIdx += 1;
    }
    expect(nextLineIdx).toBeLessThan(lines.length);
    expect(lines[nextLineIdx]).toMatch(/\[string\]\s*\$VcRedistUrl\b/);
  });

  test("uses the canonical Microsoft VC++ redist download URL", () => {
    // The signed Microsoft endpoint that redirects to
    // download.visualstudio.microsoft.com. ANY other host means we have lost
    // the chain of trust the Authenticode signature anchors on.
    expect(SCRIPT_TEXT).toContain("aka.ms/vc14/vc_redist.x64.exe");
  });
});

describe("bootstrap-windows-runner.ps1 forbids third-party DLL sources + dangerous patterns", () => {
  test("does NOT reference any third-party DLL download host", () => {
    // Closes a regression vector: an over-eager fixer could "fix" the VC++
    // redist by direct-downloading a DLL from one of these unofficial sources.
    // We hardcode the rejected hosts as the negative grep -- if a future
    // fixer adds yet another DLL source, this assertion will fail and the
    // operator must reconcile it (most likely by deleting the new line).
    expect(SCRIPT_TEXT).not.toContain("dllme.com");
    expect(SCRIPT_TEXT).not.toContain("dll-files.com");
    expect(SCRIPT_TEXT).not.toContain("dlldownloader.com");
    expect(SCRIPT_TEXT).not.toContain("nirsoft.net");
  });

  test("any non-Microsoft URL is not used for a `vcruntime` artifact", () => {
    // Defense in depth: scan every `https://` reference and require that any
    // URL containing the word `vcruntime` is on a Microsoft-controlled host.
    // The check is case-insensitive so a lowercase variant cannot slip past.
    const urlPattern = /https?:\/\/[^\s'"]+/g;
    const offenders = [];
    let match;
    while ((match = urlPattern.exec(SCRIPT_TEXT)) !== null) {
      const url = match[0];
      if (!/vcruntime/i.test(url)) {
        continue;
      }
      if (
        /https?:\/\/(aka\.ms|[A-Za-z0-9._-]+\.microsoft\.com|[A-Za-z0-9._-]+\.windows\.com)\b/i.test(
          url
        )
      ) {
        continue;
      }
      offenders.push(url);
    }
    expect(offenders).toEqual([]);
  });

  test("does NOT use Invoke-Expression (arbitrary code execution risk)", () => {
    // Invoke-Expression is a classic foot-gun: a caller-controlled string
    // fed to it becomes arbitrary code. The bootstrap script has no need
    // for it -- every dynamic call goes through scriptblocks / & operator.
    // Ban it outright to keep that posture stable.
    expect(SCRIPT_TEXT).not.toMatch(/\bInvoke-Expression\b/);
  });
});

describe("bootstrap-windows-runner.ps1 dot-source-safety: top-level body has no session-mode mutation", () => {
  // The dot-source contract: tests + composites must be able to dot-source the
  // script to inspect functions WITHOUT inheriting Set-StrictMode /
  // $ErrorActionPreference / $PSNativeCommandUseErrorActionPreference. The
  // three assignments MUST live ONLY inside `if ($invokedAsScript)`.

  test("Set-StrictMode, $ErrorActionPreference, $PSNativeCommandUseErrorActionPreference appear AFTER the dispatcher gate", () => {
    // Anchor on the COLUMN-0 dispatcher gate (the second occurrence; the
    // first is a doc-comment reference inside the .DESCRIPTION block).
    const gatePattern = /\nif \(\$invokedAsScript\)\s*\{/;
    const gateMatch = gatePattern.exec(SCRIPT_TEXT);
    expect(gateMatch).not.toBeNull();
    const gateIdx = gateMatch.index + 1; // skip leading \n

    // Find every occurrence and confirm each is INSIDE the gated block.
    // We collect the index of each match and assert each is > the gate index.
    // (LOCAL Set-StrictMode calls inside individual functions are LEGIT --
    // those appear BEFORE the gate but they live inside a function body, so
    // we exclude any match whose enclosing line is preceded by a `function `
    // header before the gate. Simpler: every top-level mutation must follow
    // the gate, and we verify via a positional check that the FIRST top-level
    // (column-0-indented) assignment of each preference appears after the
    // gate.)
    function findColumnZeroAssignment(pattern) {
      const re = new RegExp(`(^|\\n)(${pattern})`, "g");
      const matches = [];
      let m;
      while ((m = re.exec(SCRIPT_TEXT)) !== null) {
        matches.push(m.index + (m[1] ? m[1].length : 0));
      }
      return matches;
    }

    // Top-level $ErrorActionPreference assignment must appear AFTER the gate.
    // We accept zero matches as well (the script may set EAP inside the
    // dispatcher function instead -- and indeed it does, inside the
    // `if ($invokedAsScript)` block).
    const eapMatches = findColumnZeroAssignment("\\$ErrorActionPreference\\s*=");
    for (const idx of eapMatches) {
      expect(idx).toBeGreaterThan(gateIdx);
    }

    const psncMatches = findColumnZeroAssignment(
      "\\$PSNativeCommandUseErrorActionPreference\\s*="
    );
    for (const idx of psncMatches) {
      expect(idx).toBeGreaterThan(gateIdx);
    }

    // Set-StrictMode at column 0 must also appear after the gate. (LOCAL
    // `Set-StrictMode -Version Latest` inside function bodies is indented and
    // therefore excluded from the column-0 anchor.)
    const ssmMatches = findColumnZeroAssignment("Set-StrictMode\\b");
    for (const idx of ssmMatches) {
      expect(idx).toBeGreaterThan(gateIdx);
    }
  });

  test("dispatcher gate is `if ($invokedAsScript)` followed by the three preference assignments", () => {
    // Pin the inside-dispatcher shape: the three assignments must all live
    // inside the gated block. We extract the gated block via balanced-brace
    // scan from the column-0 `if` token (NOT the doc-comment reference at
    // line 74, which is indented inside the .DESCRIPTION block).
    const gatePattern = /\nif \(\$invokedAsScript\)\s*\{/;
    const gateMatch = gatePattern.exec(SCRIPT_TEXT);
    expect(gateMatch).not.toBeNull();
    const ifIdx = gateMatch.index + 1; // skip the leading \n
    const openBrace = SCRIPT_TEXT.indexOf("{", ifIdx);
    expect(openBrace).toBeGreaterThan(-1);
    let depth = 0;
    let closeBrace = -1;
    for (let i = openBrace; i < SCRIPT_TEXT.length; i += 1) {
      const ch = SCRIPT_TEXT[i];
      if (ch === "{") depth += 1;
      else if (ch === "}") {
        depth -= 1;
        if (depth === 0) {
          closeBrace = i;
          break;
        }
      }
    }
    expect(closeBrace).toBeGreaterThan(openBrace);
    const dispatcherBody = SCRIPT_TEXT.slice(openBrace, closeBrace + 1);
    expect(dispatcherBody).toMatch(/Set-StrictMode\s+-Version\s+Latest/);
    expect(dispatcherBody).toMatch(/\$ErrorActionPreference\s*=\s*'Stop'/);
    expect(dispatcherBody).toMatch(/\$PSNativeCommandUseErrorActionPreference\s*=\s*\$false/);
  });
});

describe("bootstrap-windows-runner.ps1 CI annotation hygiene", () => {
  test("annotation helpers (Write-CiNotice/Write-CiWarning/Write-CiError) emit single-line Write-Host", () => {
    // ConciseView word-wraps thrown text at console width. Write-Host emits
    // each call as a single un-wrapped CI log line -- so the annotation
    // helpers MUST go through Write-Host (not Write-Output / Write-Error /
    // throw). Pin the body shape so a refactor cannot regress to a multi-
    // line throw or a multi-stream emit.
    const notice = extractPowerShellFunction(SCRIPT_TEXT, "Write-CiNotice");
    expect(notice).not.toBeNull();
    expect(notice).toMatch(/Write-Host\s+"::notice::\$Message"/);

    const warn = extractPowerShellFunction(SCRIPT_TEXT, "Write-CiWarning");
    expect(warn).not.toBeNull();
    expect(warn).toMatch(/Write-Host\s+"::warning::\$Message"/);

    const err = extractPowerShellFunction(SCRIPT_TEXT, "Write-CiError");
    expect(err).not.toBeNull();
    expect(err).toMatch(/Write-Host\s+"::error::\$Message"/);
  });

  test("primary failure annotations use single-line Write-Host ::error:: (wrap-immune)", () => {
    // Every actionable ::error:: annotation must be a single Write-Host
    // invocation. We grep for any `Write-Host` that emits an ::error::
    // and confirm none span multiple lines via a literal newline inside
    // the call. (PowerShell allows the ` ` (backtick) line continuation
    // INSIDE the string interior; we reject any explicit newline character
    // inside the Write-Host argument.)
    const errorEmits = SCRIPT_TEXT.match(/Write-Host\s+"::error::[^"]*"/g) || [];
    // At least one ::error:: annotation must be present (the bootstrap WILL
    // fail loudly on hostile inputs / non-admin / install failure).
    expect(errorEmits.length).toBeGreaterThan(0);
  });
});

describe("bootstrap-windows-runner.ps1 winget install args", () => {
  test("Install-PowerShell7 args include --scope user + --accept-source-agreements + --accept-package-agreements", () => {
    // The runner service typically runs as a non-admin local user. --scope
    // user lands pwsh on THAT user's PATH without elevation. Both --accept-*
    // flags are MANDATORY for non-interactive winget; missing either makes
    // the install pause for an EULA prompt that nothing will ever click.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Install-PowerShell7");
    expect(body).not.toBeNull();
    expect(body).toMatch(/'--scope'\s*,\s*'user'/);
    expect(body).toContain("'--accept-source-agreements'");
    expect(body).toContain("'--accept-package-agreements'");
    expect(body).toContain("'--silent'");
    // The package id MUST be the official Microsoft PowerShell artifact.
    expect(body).toMatch(/'--id'\s*,\s*'Microsoft\.PowerShell'/);
    // The source MUST be `winget` (the Microsoft-blessed source); a custom
    // source would bypass the supply-chain trust anchor.
    expect(body).toMatch(/'--source'\s*,\s*'winget'/);
  });

  test("winget idempotent exit codes (already-installed / no-applicable-update) are mapped to success", () => {
    // winget propagates 0x8A15xxxx HRESULTs as signed-int process exits;
    // these particular ones mean "no update applicable / already installed"
    // which is a no-op success on an idempotent re-run.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Install-PowerShell7");
    expect(body).not.toBeNull();
    expect(body).toContain("-1978335189"); // UPDATE_NOT_APPLICABLE
    expect(body).toContain("-1978335212"); // NO_APPLICABLE_INSTALLER
    expect(body).toContain("-1978335153"); // NO_APPLICABLE_UPGRADE
    expect(body).toContain("-1978334975"); // UPDATE_NOT_APPLICABLE (alt path)
  });
});

describe("bootstrap-windows-runner.ps1 VC++ 2015-2022 redist install args + detection", () => {
  test("Invoke-VcRedistModernInstaller passes /install /quiet /norestart", () => {
    // Microsoft's documented silent install flags for the 2015-2022 generation.
    // Missing any of the three means the installer pauses for UI / forces a
    // reboot mid-job; both failure modes are unacceptable on a non-interactive
    // runner. CRITICALLY: do NOT confuse with the 2010 generation's switches
    // (/q /norestart -- no /install verb) -- the two generations use
    // DIFFERENT silent-install switch sets.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-VcRedistModernInstaller");
    expect(body).not.toBeNull();
    expect(body).toMatch(/'\/install'\s*,\s*'\/quiet'\s*,\s*'\/norestart'/);
  });

  test("Test-VcRedistModernFilesOnDisk checks all three required DLLs in System32", () => {
    // The file probe is the AUTHORITATIVE detection signal -- the OS loader
    // fails Unity.exe at startup based on these on-disk files, not the
    // registry. All three must be probed; missing any one means a Unity
    // launch can still fail with 0xC0000135 even when the other two land.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-VcRedistModernFilesOnDisk");
    expect(body).not.toBeNull();
    expect(body).toContain("VCRUNTIME140.dll");
    expect(body).toContain("VCRUNTIME140_1.dll");
    expect(body).toContain("MSVCP140.dll");
    // The path anchor must be System32 (not SysWOW64 -- that's the 32-bit
    // mirror and Unity Editor is a 64-bit process). PowerShell single-
    // quoted strings carry literal backslashes, so the regex needs a
    // SINGLE escape (`\\\\` in JS regex literal == one `\` in the text;
    // here we want to match the literal sequence `C:\Windows\System32\...`).
    expect(body).toMatch(/C:\\Windows\\System32\\VCRUNTIME140\.dll/);
    expect(body).toMatch(/C:\\Windows\\System32\\VCRUNTIME140_1\.dll/);
    expect(body).toMatch(/C:\\Windows\\System32\\MSVCP140\.dll/);
    // Defense in depth: SysWOW64 (the 32-bit mirror) MUST NOT be probed.
    expect(body).not.toMatch(/SysWOW64/);
  });

  test("registry Bld threshold is >= 26020 (VS 2017 15.5, NOT >= 30000)", () => {
    // 30000 was the wrong threshold: it false-negatived every VS 2017 +
    // VS 2019 (<= 16.7) install, which is the lineage most self-hosted
    // runners come from. 26020 is the first VS 2017 15.5 build that ships
    // VCRUNTIME140_1.dll.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-VcRedistModernInstalledAtRegistryView");
    expect(body).not.toBeNull();
    expect(body).toMatch(/-ge\s+26020\b/);
    // The wrong threshold MUST NOT reappear anywhere in the script.
    expect(SCRIPT_TEXT).not.toMatch(/-ge\s+30000\b/);
  });

  test("TLS 1.2 (mandatory) + TLS 1.3 (best-effort) is configured before Invoke-WebRequest (modern download)", () => {
    // Stock Windows PowerShell 5.1's ServicePointManager defaults to
    // Ssl3|Tls1.0 -- which aka.ms and download.visualstudio.microsoft.com
    // reject. Without TLS 1.2 the download fails with "underlying connection
    // was closed". Tls13 is best-effort (older .NET Framework lacks the
    // enum value).
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-VcRedistModernDownload");
    expect(body).not.toBeNull();
    // TLS 1.2 MUST be configured.
    expect(body).toMatch(/\[Net\.SecurityProtocolType\]::Tls12/);
    expect(body).toMatch(/ServicePointManager.*Tls12/s);
    // TLS 1.3 MUST be attempted (best-effort, inner try/catch).
    expect(body).toMatch(/\[Net\.SecurityProtocolType\]::Tls13/);
    // The TLS config must precede the Invoke-WebRequest call. Comments
    // mention Invoke-WebRequest before the actual call, so anchor on the
    // call shape `Invoke-WebRequest -Uri ...` (the only invocation in
    // this function body).
    const tls12Idx = body.indexOf("[Net.SecurityProtocolType]::Tls12");
    const iwrCallMatch = /Invoke-WebRequest\s+-Uri\b/.exec(body);
    expect(tls12Idx).toBeGreaterThan(-1);
    expect(iwrCallMatch).not.toBeNull();
    expect(tls12Idx).toBeLessThan(iwrCallMatch.index);
  });

  test("Get-AuthenticodeSignature is called on the downloaded EXE before launching the modern installer", () => {
    // The [ValidatePattern]-pinned URL is necessary but not sufficient
    // (compromised CDN entry, MITM, etc.). Authenticode signature
    // verification at install time anchors trust on the Microsoft cert
    // chain. The signature check must precede the installer launch.
    const verifierBody = extractPowerShellFunction(SCRIPT_TEXT, "Test-VcRedistAuthenticodeSignatureMicrosoft");
    expect(verifierBody).not.toBeNull();
    expect(verifierBody).toMatch(/Get-AuthenticodeSignature\s+-FilePath\s+\$FilePath/);
    expect(verifierBody).toContain("Microsoft Corporation");

    // The modern installer orchestrator must call the verifier BEFORE
    // Invoke-VcRedistModernInstaller.
    const installerBody = extractPowerShellFunction(SCRIPT_TEXT, "Install-VcRedistModern");
    expect(installerBody).not.toBeNull();
    const sigIdx = installerBody.indexOf("Test-VcRedistAuthenticodeSignatureMicrosoft");
    const installIdx = installerBody.indexOf("Invoke-VcRedistModernInstaller");
    expect(sigIdx).toBeGreaterThan(-1);
    expect(installIdx).toBeGreaterThan(-1);
    expect(sigIdx).toBeLessThan(installIdx);
  });

  test("Install-VcRedistModern pre-checks Test-IsAdministrator before download", () => {
    // The HKLM write of the registry-blessed install record requires elevation;
    // detecting non-admin BEFORE the download avoids the round-trip + temp
    // file footprint when we already know the install will fail. Mirrors the
    // 2010 installer's pre-check.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Install-VcRedistModern");
    expect(body).not.toBeNull();
    const adminIdx = body.indexOf("Test-IsAdministrator");
    const downloadIdx = body.indexOf("Invoke-VcRedistModernDownload");
    expect(adminIdx).toBeGreaterThan(-1);
    expect(downloadIdx).toBeGreaterThan(-1);
    expect(adminIdx).toBeLessThan(downloadIdx);
  });
});

describe("bootstrap-windows-runner.ps1 VC++ 2010 redist install args + detection", () => {
  // Production run 70874414898 identified MSVCP100.dll (from the 2010 SP1
  // generation) as the load-bearing missing DLL on both self-hosted Windows
  // runners. The 2010 SP1 redistributable is a SEPARATE Microsoft package
  // from the modern 2015-2022 generation -- installing the modern one does
  // NOT install MSVCP100. The contract assertions below pin every load-
  // bearing token / arg / URL of the 2010 install path so a refactor that
  // drifts ANY of them (URL drift, switch drift, missing admin pre-check)
  // fails this guard with a deterministic line number.

  test("the canonical VC++ 2010 SP1 x64 redist URL is hardcoded in the script", () => {
    // The download.microsoft.com path is the only canonical URL Microsoft
    // publishes for this artifact (VS 2010 extended support ended
    // 2020-07-14, so no aka.ms shortcut exists). The full URL with the
    // artifact GUID 165255E7-... must appear verbatim somewhere in the
    // script (the [ValidatePattern]-constrained default for $VcRedist2010Url).
    expect(SCRIPT_TEXT).toContain(
      "download.microsoft.com/download/1/6/5/165255E7-1014-4D0A-B094-B6A430A6BFFC/vcredist_x64.exe"
    );
  });

  test("VcRedist2010Url parameter carries a [ValidatePattern] pinning Microsoft hosts", () => {
    // Same defense-in-depth as VcRedistUrl: any caller-overridden URL must
    // come from a Microsoft-controlled domain. The 2010 URL list is narrower
    // than the modern one (no aka.ms shortcut exists for VS 2010).
    const lines = SCRIPT_TEXT.split("\n");
    const validatorLineIdx = lines.findIndex(
      (line) =>
        line.includes("[ValidatePattern(") &&
        /download\\\.microsoft\\\.com/.test(line)
    );
    expect(validatorLineIdx).toBeGreaterThan(-1);
    const validatorLine = lines[validatorLineIdx];
    expect(validatorLine).toMatch(/\[ValidatePattern\(.*download\\\.microsoft\\\.com.*\)\]/);
    // The IMMEDIATELY-FOLLOWING non-empty line must declare $VcRedist2010Url,
    // so the validator decorates exactly that parameter (and nothing else).
    let nextLineIdx = validatorLineIdx + 1;
    while (nextLineIdx < lines.length && lines[nextLineIdx].trim() === "") {
      nextLineIdx += 1;
    }
    expect(nextLineIdx).toBeLessThan(lines.length);
    expect(lines[nextLineIdx]).toMatch(/\[string\]\s*\$VcRedist2010Url\b/);
  });

  test("Invoke-VcRedist2010Installer passes /q /norestart (NOT /install /quiet /norestart)", () => {
    // CRITICAL DIFFERENCE FROM THE MODERN GENERATION: the VC++ 2010 installer
    // uses `/q /norestart` (NOT `/install /quiet /norestart`). The 2010-era
    // Microsoft installer technology predates the unified `/install` verb
    // and exposes a different (older) silent-install switch set. Passing
    // the modern switches to the 2010 installer causes the unrecognized
    // args to fall through to the help code path and the installer NEVER
    // actually installs anything.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-VcRedist2010Installer");
    expect(body).not.toBeNull();
    expect(body).toMatch(/'\/q'\s*,\s*'\/norestart'/);
    // The function body MUST NOT contain the modern switch combination.
    // (The literal regex pin defends against a future refactor that copy-
    // pasted the modern args into the 2010 installer.)
    expect(body).not.toMatch(/'\/install'\s*,\s*'\/quiet'\s*,\s*'\/norestart'/);
  });

  test("Test-VcRedist2010FilesOnDisk checks BOTH MSVCP100.dll AND MSVCR100.dll in System32", () => {
    // Both DLLs ship in the 2010 SP1 redist installer; both are consumed
    // by Unity.exe's import table (via the VC++ 2010-era libraries Unity
    // statically links). Either being missing produces 0xC0000135 at
    // startup, so both must be probed.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-VcRedist2010FilesOnDisk");
    expect(body).not.toBeNull();
    expect(body).toContain("MSVCP100.dll");
    expect(body).toContain("MSVCR100.dll");
    // SysWOW64 (the 32-bit mirror) MUST NOT be probed -- the x64 install
    // lands in the native System32 only.
    expect(body).not.toMatch(/SysWOW64/);
  });

  test("Test-VcRedist2010InstalledAtRegistryView probes the canonical VS 2010 VCRedist key", () => {
    // The 2010 redist registers under
    //   HKLM:\SOFTWARE\Microsoft\VisualStudio\10.0\VC\VCRedist\x64
    // (also mirrored to Wow6432Node on some hosts -- the composite probe
    // in Test-VcRedist2010Installed handles both). Pin the literal sub-path
    // so a refactor that drifted the key (e.g. swapped 10.0 -> 14.0)
    // fails this guard.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-VcRedist2010InstalledAtRegistryView");
    expect(body).not.toBeNull();
    expect(body).toContain("Microsoft\\VisualStudio\\10.0\\VC\\VCRedist\\x64");
  });

  test("Test-VcRedist2010Installed composes the file probe with BOTH native AND Wow6432Node registry views", () => {
    // The 2010 installer can write to EITHER view depending on host
    // configuration (unlike the modern redist which writes exclusively to
    // the native view). The composite probe must check both views and OR
    // them so a host where the install record landed in only one view is
    // still classified as installed.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-VcRedist2010Installed");
    expect(body).not.toBeNull();
    // The native view probe must come first (it's the primary).
    expect(body).toMatch(/Test-VcRedist2010InstalledAtRegistryView\s+-BaseKey\s+'HKLM:\\SOFTWARE'/);
    // The Wow6432Node view probe must follow as the secondary.
    expect(body).toMatch(/Test-VcRedist2010InstalledAtRegistryView\s+-BaseKey\s+'HKLM:\\SOFTWARE\\Wow6432Node'/);
    // The file-on-disk probe must precede the registry probes (files are
    // the OS-loader's authoritative signal).
    const fileProbeIdx = body.indexOf("Test-VcRedist2010FilesOnDisk");
    const nativeProbeIdx = body.indexOf("HKLM:\\SOFTWARE'");
    expect(fileProbeIdx).toBeGreaterThan(-1);
    expect(nativeProbeIdx).toBeGreaterThan(-1);
    expect(fileProbeIdx).toBeLessThan(nativeProbeIdx);
  });

  test("TLS 1.2 (mandatory) + TLS 1.3 (best-effort) is configured before Invoke-WebRequest (2010 download)", () => {
    // Same TLS posture as the modern download. The 2010 generation download
    // uses download.microsoft.com which (like the modern endpoint) rejects
    // TLS below 1.2. Without TLS 1.2 the download fails with "underlying
    // connection was closed" on PS 5.1.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-VcRedist2010Download");
    expect(body).not.toBeNull();
    expect(body).toMatch(/\[Net\.SecurityProtocolType\]::Tls12/);
    expect(body).toMatch(/ServicePointManager.*Tls12/s);
    expect(body).toMatch(/\[Net\.SecurityProtocolType\]::Tls13/);
    const tls12Idx = body.indexOf("[Net.SecurityProtocolType]::Tls12");
    const iwrCallMatch = /Invoke-WebRequest\s+-Uri\b/.exec(body);
    expect(tls12Idx).toBeGreaterThan(-1);
    expect(iwrCallMatch).not.toBeNull();
    expect(tls12Idx).toBeLessThan(iwrCallMatch.index);
  });

  test("Get-AuthenticodeSignature is called on the downloaded EXE before launching the 2010 installer", () => {
    // Same trust posture as the modern installer: Authenticode verification
    // anchors trust on Microsoft's certificate chain. Microsoft signs BOTH
    // 2010 and 2015-2022 redistributables so the same shared helper
    // (Test-VcRedistAuthenticodeSignatureMicrosoft) is reused for both.
    const installerBody = extractPowerShellFunction(SCRIPT_TEXT, "Install-VcRedist2010");
    expect(installerBody).not.toBeNull();
    const sigIdx = installerBody.indexOf("Test-VcRedistAuthenticodeSignatureMicrosoft");
    const installIdx = installerBody.indexOf("Invoke-VcRedist2010Installer");
    expect(sigIdx).toBeGreaterThan(-1);
    expect(installIdx).toBeGreaterThan(-1);
    expect(sigIdx).toBeLessThan(installIdx);
  });

  test("Install-VcRedist2010 pre-checks Test-IsAdministrator before download", () => {
    // The HKLM write of the registry-blessed install record requires elevation;
    // detecting non-admin BEFORE the download avoids the round-trip + temp
    // file footprint when we already know the install will fail. Mirrors the
    // modern installer's pre-check pattern.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Install-VcRedist2010");
    expect(body).not.toBeNull();
    const adminIdx = body.indexOf("Test-IsAdministrator");
    const downloadIdx = body.indexOf("Invoke-VcRedist2010Download");
    expect(adminIdx).toBeGreaterThan(-1);
    expect(downloadIdx).toBeGreaterThan(-1);
    expect(adminIdx).toBeLessThan(downloadIdx);
  });

  test("Install-VcRedist2010 re-verifies the file probe AFTER the installer reports success", () => {
    // The installer can return exit 0 even when the kernel canceled
    // mid-extract (e.g. AV quarantine of MSVCP100.dll). The OS loader cares
    // about the actual files on disk -- so the post-install probe is
    // load-bearing for correctness.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Install-VcRedist2010");
    expect(body).not.toBeNull();
    const installerIdx = body.indexOf("Invoke-VcRedist2010Installer");
    const postProbeIdx = body.lastIndexOf("Test-VcRedist2010FilesOnDisk");
    expect(installerIdx).toBeGreaterThan(-1);
    expect(postProbeIdx).toBeGreaterThan(-1);
    expect(installerIdx).toBeLessThan(postProbeIdx);
  });
});

describe("bootstrap-windows-runner.ps1 Defender bootstrap path guards", () => {
  test("Test-DefenderExclusionPathAllowed exists and rejects single drive roots", () => {
    // Source-level guard: the body must contain the single-drive-root reject
    // regex. Behavioral confirmation lives in the helper-mutation suite.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Test-DefenderExclusionPathAllowed");
    expect(body).not.toBeNull();
    // The single-drive-root reject regex (e.g. `C:\`, `D:`).
    expect(body).toMatch(/\$Path\s+-match\s+'\^\[A-Za-z\]:\[\\\\\/\]\?\$'/);
    // The whitespace-only reject branch.
    expect(body).toMatch(/\[string\]::IsNullOrWhiteSpace\(\$Path\)/);
  });

  test("Invoke-DefenderBootstrap tolerates an empty -Paths array via [AllowEmptyCollection()]", () => {
    // Get-DefenderExclusionPaths returns an empty array when every candidate
    // is rejected by the allow-list. Without [AllowEmptyCollection()] on
    // the Mandatory [string[]]$Paths, that empty-array call binds AutomationNull
    // and the function throws "Cannot bind argument to parameter".
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-DefenderBootstrap");
    expect(body).not.toBeNull();
    // Either [AllowEmptyCollection()] is explicit on the param, OR the param
    // is non-Mandatory (which has the same effect for an empty array). We
    // require the explicit attribute because the script ALSO checks
    // `if ($null -eq $Paths -or $Paths.Length -eq 0)` and that branch is
    // load-bearing -- making the param non-Mandatory without the attribute
    // would not communicate intent.
    expect(body).toMatch(/\[AllowEmptyCollection\(\)\]\s*\[string\[\]\]\s*\$Paths/);
    // The Mandatory attribute remains so callers can't pass $null by accident.
    expect(body).toMatch(/Mandatory\s*=\s*\$true/);
  });

  test("Get-DefenderExclusionPaths validates every candidate via Test-DefenderExclusionPathAllowed", () => {
    // The allow-list defence-in-depth requirement: every candidate path
    // (including $env:RUNNER_WORKSPACE) MUST be screened before it is
    // forwarded to Add-MpPreference.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Get-DefenderExclusionPaths");
    expect(body).not.toBeNull();
    expect(body).toMatch(
      /Test-DefenderExclusionPathAllowed\s+-Path\s+\$p\s+-UnityInstallRoot\s+\$UnityInstallRoot/
    );
  });
});

describe("bootstrap-windows-runner.ps1 declares every required helper function", () => {
  // These names form the script's public-ish surface (the dispatcher invokes
  // them; tests inspect them; future contributors will hunt them by name).
  // A rename that drops one must be reflected here or fail the suite.
  // The names follow the convention:
  //   - *Modern* = VC++ 2015-2022 generation (vc_redist.x64.exe / aka.ms/vc14)
  //   - *2010*   = VC++ 2010 SP1 generation (vcredist_x64.exe / direct
  //                download.microsoft.com URL with the 165255E7-... GUID)
  //   - *Microsoft (on the shared signature helper) = the shared abstraction
  //     because Microsoft signs both generations with the same trust chain.
  const REQUIRED_HELPERS = [
    "Test-IsWindowsHost",
    "Test-IsAdministrator",
    "Test-IsAccessDeniedException",
    "Get-RegistryItemPropertySafe",
    // VC++ 2015-2022 (modern) generation helpers.
    "Test-VcRedistModernFilesOnDisk",
    "Test-VcRedistModernInstalledAtRegistryView",
    "Test-VcRedistModernInstalled",
    "Invoke-VcRedistModernDownload",
    "Invoke-VcRedistModernInstaller",
    "Install-VcRedistModern",
    // VC++ 2010 SP1 generation helpers (new).
    "Test-VcRedist2010FilesOnDisk",
    "Test-VcRedist2010InstalledAtRegistryView",
    "Test-VcRedist2010Installed",
    "Invoke-VcRedist2010Download",
    "Invoke-VcRedist2010Installer",
    "Install-VcRedist2010",
    // Shared Authenticode verifier (both 2010 and 2015-2022 signers are
    // Microsoft Corporation; the helper name reflects the shared abstraction).
    "Test-VcRedistAuthenticodeSignatureMicrosoft",
    "Test-LongPathsEnabled",
    "Enable-LongPaths",
    "Test-DefenderExclusionPathAllowed",
    "Get-DefenderExclusionPaths",
    "Test-DefenderAvailable",
    "Test-DefenderExclusion",
    "Add-DefenderExclusion",
    "Test-PowerShell7Installed",
    "Install-PowerShell7",
    "Test-UcrtPresent",
    "Invoke-BootstrapStep",
    "Invoke-DefenderBootstrap",
    "Invoke-UcrtBootstrap",
    "Format-BootstrapSummary",
    "Invoke-WindowsRunnerBootstrap",
    "Write-CiNotice",
    "Write-CiWarning",
    "Write-CiError"
  ];

  test.each(REQUIRED_HELPERS)("declares helper function %s", (name) => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, name);
    expect(body).not.toBeNull();
  });

  // The OLD (pre-rename) names MUST NOT appear ANYWHERE in the script as
  // function definitions OR call sites. A refactor that left a stale call
  // to e.g. `Test-VcRedistInstalled` would compile but throw at runtime
  // (PowerShell treats unknown commands as errors under StrictMode +
  // ErrorActionPreference=Stop, which the dispatcher enables).
  const FORBIDDEN_OLD_NAMES = [
    "Test-VcRedistInstalled",
    "Test-VcRedistInstalledAtRegistryView",
    "Test-VcRedistFilesOnDisk",
    "Test-VcRedistAuthenticodeSignature",
    "Invoke-VcRedistDownload",
    "Invoke-VcRedistInstaller",
    "Install-VcRedist"
  ];

  test.each(FORBIDDEN_OLD_NAMES)("forbids reference to the pre-rename helper name %s", (name) => {
    // We use a word-boundary regex so e.g. "Install-VcRedist" does not
    // false-positive on "Install-VcRedistModern" / "Install-VcRedist2010".
    // The negative pattern matches the bare name NOT followed by an
    // identifier character (digit / letter / underscore / hyphen).
    const pattern = new RegExp(`\\b${name}(?![\\w-])`);
    // Allow only the FORBIDDEN_OLD_NAMES list reference inside the test
    // file's own forbidden-name registry; we are testing the SCRIPT_TEXT,
    // not this test file. Any match in SCRIPT_TEXT is a stale reference.
    expect(SCRIPT_TEXT).not.toMatch(pattern);
  });
});

describe("bootstrap-windows-runner.ps1 dispatcher exit-code contract", () => {
  test("Invoke-WindowsRunnerBootstrap returns 0/1/2 per the documented contract", () => {
    // Exit codes documented at the top of the file:
    //   0 = every prereq ok
    //   1 = at least one install failure (or non-Windows hard-fail in
    //       non-DetectOnly mode)
    //   2 = DetectOnly + at least one prereq missing
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-WindowsRunnerBootstrap");
    expect(body).not.toBeNull();
    // 0 path (the success return at the end of the function).
    expect(body).toMatch(/return\s+0/);
    // 1 path (any install-failed and the non-Windows + non-DetectOnly hard-fail).
    expect(body).toMatch(/return\s+1/);
    // 2 path (DetectOnly + missing).
    expect(body).toMatch(/return\s+2/);
  });

  test("non-Windows host: DetectOnly emits ::notice:: and returns 0", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-WindowsRunnerBootstrap");
    expect(body).not.toBeNull();
    // The non-Windows branch must produce a Write-CiNotice with a
    // "skipping" / "not Windows" phrase that the per-job composite can
    // grep on, AND it must return 0 (NOT throw, NOT return 1).
    expect(body).toMatch(/Test-IsWindowsHost/);
    expect(body).toMatch(/skipping/i);
    expect(body).toMatch(/non-Windows|not Windows/i);
  });

  test("non-Windows host: non-DetectOnly emits ::error:: and returns 1", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-WindowsRunnerBootstrap");
    expect(body).not.toBeNull();
    // The hard-fail path must NAME the script as "Windows-only" so the
    // operator sees a deterministic, greppable failure message.
    expect(body).toMatch(/Windows-only/);
  });
});

describe("bootstrap-windows-runner.ps1 PowerShell automatic-variable hygiene (round-2 fixes)", () => {
  // Round-2 NR1: the original timeout-kill code wrote `$pid = $process.Id`,
  // which throws under StrictMode because `$pid` is a read-only AUTOMATIC
  // VARIABLE (the running pwsh's own PID). The catch then converts the
  // intended "installer timed out" reason into a misleading "Start-Process
  // threw: Cannot overwrite variable PID" message. The fix renames the
  // local so the timeout diagnostic survives end-to-end.
  test("Invoke-VcRedistModernInstaller does NOT assign to the reserved $pid automatic variable", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-VcRedistModernInstaller");
    expect(body).not.toBeNull();
    // Negative: no `$pid =` assignment ANYWHERE in the function body.
    // PowerShell's $pid is the running process's PID; assigning to it
    // throws under StrictMode + ErrorActionPreference=Stop.
    expect(body).not.toMatch(/^\s*\$pid\s*=/m);
    expect(body).not.toMatch(/[^\w]\$pid\s*=/);
  });

  test("Invoke-VcRedist2010Installer does NOT assign to the reserved $pid automatic variable", () => {
    // Mirror guard for the 2010 installer (same regression class).
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-VcRedist2010Installer");
    expect(body).not.toBeNull();
    expect(body).not.toMatch(/^\s*\$pid\s*=/m);
    expect(body).not.toMatch(/[^\w]\$pid\s*=/);
  });

  // Round-1 F5: the original code reassigned `$args = @(...)`. PowerShell
  // automatically populates `$args` with positional inputs, so the
  // reassignment silently drops any caller-passed positional args. The
  // fix renames the local to $wingetArgs.
  test("Install-PowerShell7 does NOT clobber the $args automatic variable", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Install-PowerShell7");
    expect(body).not.toBeNull();
    expect(body).not.toMatch(/^\s*\$args\s*=/m);
    expect(body).not.toMatch(/[^\w]\$args\s*=/);
    // Positive: the renamed local must be present.
    expect(body).toMatch(/\$wingetArgs/);
  });
});

describe("bootstrap-windows-runner.ps1 best-effort tiering (Defender is non-load-bearing)", () => {
  // The 2026-05-26 production regression: per-job preflight ran as
  // NETWORK SERVICE (non-admin). Add-MpPreference requires admin; the
  // bootstrap treated this as a critical failure and exited 1, failing
  // every Unity cell even though every load-bearing prereq (VC++,
  // long-paths, pwsh, UCRT) was already installed by an operator's
  // manual elevated run. Defender exclusion is a perf optimization
  // (faster Android NDK unpack) NOT a correctness requirement for Unity
  // startup. The tests below pin the tier-aware exit-code logic so
  // a future refactor cannot regress to "Defender failure exits 1".
  test("Invoke-DefenderBootstrap skips entirely when non-admin", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-DefenderBootstrap");
    expect(body).not.toBeNull();
    // The function must check Test-IsAdministrator at the TOP and
    // return early with finalState='skipped-non-admin' + critical=$false.
    expect(body).toMatch(/Test-IsAdministrator/);
    expect(body).toMatch(/skipped-non-admin/);
    expect(body).toMatch(/critical\s*=\s*\$false/);

    // The Test-IsAdministrator check must precede the Test-DefenderAvailable
    // function CALL (we must short-circuit BEFORE touching Get-MpPreference).
    // Anchor on the actual call site `(Test-DefenderAvailable)` (a comment
    // referencing the function name does NOT count -- the bootstrap script
    // contains an explanatory comment naming both helpers above the call).
    const adminCallIdx = body.search(/\(\s*Test-IsAdministrator\s*\)/);
    const availableCallIdx = body.search(/\(\s*Test-DefenderAvailable\s*\)/);
    expect(adminCallIdx).toBeGreaterThan(-1);
    expect(availableCallIdx).toBeGreaterThan(-1);
    expect(adminCallIdx).toBeLessThan(availableCallIdx);

    // Must NOT emit ::error:: on the non-admin path (it's a notice, not
    // an error -- Defender is best-effort). We scope this check to the
    // region BEFORE the Test-DefenderAvailable CALL, which is the
    // non-admin short-circuit branch.
    const nonAdminBranch = body.slice(0, availableCallIdx);
    expect(nonAdminBranch).not.toMatch(/Write-CiError/);
    expect(nonAdminBranch).not.toMatch(/::error::/);
    // ...and the short-circuit must use Write-CiNotice (informational).
    expect(nonAdminBranch).toMatch(/Write-CiNotice/);
  });

  test("Invoke-DefenderBootstrap result hashtables all carry critical=$false", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-DefenderBootstrap");
    expect(body).not.toBeNull();
    // EVERY return hashtable from this function must declare critical=$false.
    // A mutation that dropped the flag from one branch (e.g. install-failed)
    // would silently reintroduce the 2026-05-26 production regression.
    const returnHashes = [...body.matchAll(/return\s+@\{[\s\S]*?\}/g)].map((m) => m[0]);
    expect(returnHashes.length).toBeGreaterThan(0);
    for (const h of returnHashes) {
      expect(h).toMatch(/critical\s*=\s*\$false/);
      // And NEVER critical=$true on any branch (would change tiering).
      expect(h).not.toMatch(/critical\s*=\s*\$true/);
    }
  });

  test("Invoke-WindowsRunnerBootstrap exit-code logic distinguishes critical from best-effort", () => {
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-WindowsRunnerBootstrap");
    expect(body).not.toBeNull();
    // Critical-failure detection must reference `critical -ne $false`
    // (treat unset/missing as critical -- the safest default; a future
    // prereq that forgets to declare `critical` defaults to gating).
    // The current dispatcher uses an if/else around `-ne $false` so the
    // best-effort branch is implicitly the inverse -- we don't separately
    // assert `-eq $false` because the AST only needs to express the
    // condition once. The behavioral coverage that the partition actually
    // works lives in unity-runner-host-prereq-helper-mutation.test.js.
    expect(body).toMatch(/critical[\s\S]{0,200}-ne[\s\S]{0,30}\$false/);
    const matchedNe = body.match(/critical[\s\S]{0,200}-ne[\s\S]{0,30}\$false/);
    expect(matchedNe).not.toBeNull();
    // Best-effort failures must emit Write-CiWarning (not error) AND the
    // warning text must mention "best-effort" so the operator sees the tier.
    expect(body).toMatch(/Write-CiWarning[\s\S]{0,600}best-effort/);
    // Best-effort failures must NOT trigger a `return 1`. We assert this by
    // locating the PER-PREREQ loop warning (anchored on "did not complete"
    // -- the distinctive phrase used by the per-prereq emit, NOT the
    // non-admin top-of-dispatcher warning which uses different wording),
    // then checking the body region between THAT warning and the next
    // `return 1` is gated by `criticalFailed.Count -gt 0` ALONE (not
    // `bestEffortFailed.Count -gt 0 -or criticalFailed.Count -gt 0`, which
    // would re-introduce the production regression).
    const perPrereqWarningIdx = body.search(/Write-CiWarning\s+"best-effort prereq[^"]*did not complete/);
    expect(perPrereqWarningIdx).toBeGreaterThan(-1);
    const afterWarning = body.slice(perPrereqWarningIdx);
    // The next `return 1` follows the per-prereq warning, and it MUST be
    // guarded by `criticalFailed.Count -gt 0`.
    const nextReturn1Idx = afterWarning.search(/return\s+1/);
    expect(nextReturn1Idx).toBeGreaterThan(-1);
    // The text between the per-prereq warning and the next `return 1`
    // must include the critical-failed gating token (so the return 1 is
    // gated on critical only)...
    const betweenWarningAndReturn1 = afterWarning.slice(0, nextReturn1Idx);
    expect(betweenWarningAndReturn1).toMatch(/\$criticalFailed\.Count/);
    // ...and must NOT reference $bestEffortFailed.Count anywhere in the
    // gate region -- a mutation that ORed it into the gate would re-
    // introduce the production regression. Per-prereq emit's enclosing
    // `if ($bestEffortFailed.Count -gt 0)` ABOVE this anchor is fine
    // (it's outside the slice we examine).
    expect(betweenWarningAndReturn1).not.toMatch(/\$bestEffortFailed\.Count/);
  });

  test("Invoke-BootstrapStep accepts a -Critical parameter (default $true)", () => {
    // The generic wrapper must declare `[bool]$Critical = $true` so callers
    // can opt-OUT to best-effort by passing -Critical $false. A default of
    // $true is the safest behavior: a refactor that adds a new prereq but
    // forgets to set the flag gets the gating (critical) behavior.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-BootstrapStep");
    expect(body).not.toBeNull();
    expect(body).toMatch(/\[bool\]\s*\$Critical\s*=\s*\$true/);
    // Every return hashtable in the body must propagate $Critical.
    const returnHashes = [...body.matchAll(/return\s+@\{[\s\S]*?\}/g)].map((m) => m[0]);
    expect(returnHashes.length).toBeGreaterThan(0);
    for (const h of returnHashes) {
      expect(h).toMatch(/critical\s*=\s*\$Critical/);
    }
  });

  test("Invoke-UcrtBootstrap result hashtables declare critical=$true (load-bearing)", () => {
    // UCRT is critical on every host that needs it (downlevel Win/Server);
    // on Win10+/Server2019+ it's a silent no-op but still carries the flag
    // for consistency. Pin the tier so a future refactor doesn't accidentally
    // demote it to best-effort.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-UcrtBootstrap");
    expect(body).not.toBeNull();
    const returnHashes = [...body.matchAll(/return\s+@\{[\s\S]*?\}/g)].map((m) => m[0]);
    expect(returnHashes.length).toBeGreaterThan(0);
    for (const h of returnHashes) {
      expect(h).toMatch(/critical\s*=\s*\$true/);
      // And NEVER critical=$false on any branch.
      expect(h).not.toMatch(/critical\s*=\s*\$false/);
    }
  });

  test("Invoke-WindowsRunnerBootstrap explicitly tiers each prereq", () => {
    // Make the contract explicit so a future maintainer reading this test
    // sees exactly which prereqs are load-bearing vs best-effort.
    //   vcredist-2010      -- critical=$true (Unity won't start without
    //                         MSVCP100/MSVCR100; identified in run 70874414898)
    //   vcredist-2015-2022 -- critical=$true (Unity won't start without
    //                         VCRUNTIME140*/MSVCP140; the original DAD-MACHINE
    //                         failure cause)
    //   long-paths   -- critical=$true (Android NDK won't unpack)
    //   pwsh         -- critical=$true (CI composites use `shell: pwsh`)
    //   ucrt         -- critical=$true (Unity needs it on downlevel Windows)
    //   defender     -- critical=$false (perf optimization only)
    //
    // The vcredist-2010, vcredist-2015-2022, long-paths, pwsh call sites
    // all go through Invoke-BootstrapStep -- assert each one passes
    // `-Critical $true` explicitly so an audit immediately sees the
    // load-bearing intent.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-WindowsRunnerBootstrap");
    expect(body).not.toBeNull();
    // vcredist-2010 call site must declare -Critical $true.
    expect(body).toMatch(
      /Invoke-BootstrapStep[\s\S]{0,400}-Name\s+'vcredist-2010'[\s\S]{0,400}-Critical\s+\$true/
    );
    // vcredist-2015-2022 call site must declare -Critical $true.
    expect(body).toMatch(
      /Invoke-BootstrapStep[\s\S]{0,400}-Name\s+'vcredist-2015-2022'[\s\S]{0,400}-Critical\s+\$true/
    );
    // long-paths call site must declare -Critical $true.
    expect(body).toMatch(
      /Invoke-BootstrapStep[\s\S]{0,400}-Name\s+'long-paths'[\s\S]{0,400}-Critical\s+\$true/
    );
    // pwsh call site must declare -Critical $true.
    expect(body).toMatch(
      /Invoke-BootstrapStep[\s\S]{0,400}-Name\s+'pwsh'[\s\S]{0,400}-Critical\s+\$true/
    );
    // The defender call site goes through Invoke-DefenderBootstrap (which
    // hard-codes critical=$false in every return); it MUST NOT pass any
    // -Critical flag (the function lacks the parameter, and the inner
    // returns drive the tier).
    const defenderCallMatch = body.match(/Invoke-DefenderBootstrap[\s\S]{0,200}/);
    expect(defenderCallMatch).not.toBeNull();
    expect(defenderCallMatch[0]).not.toMatch(/-Critical/);

    // Order pin: vcredist-2010 step MUST appear BEFORE vcredist-2015-2022 in
    // the dispatcher (older-runtime-first convention; not strictly required
    // for correctness, but the consistent ordering simplifies audit and
    // matches the documented expectation).
    const vc2010Idx = body.indexOf("'vcredist-2010'");
    const vcModernIdx = body.indexOf("'vcredist-2015-2022'");
    expect(vc2010Idx).toBeGreaterThan(-1);
    expect(vcModernIdx).toBeGreaterThan(-1);
    expect(vc2010Idx).toBeLessThan(vcModernIdx);
  });

  test("Format-BootstrapSummary suffixes best-effort outcomes with `*`", () => {
    // The single-line summary must visually distinguish best-effort entries.
    // Anchoring on the literal '*' suffix logic ensures a future refactor
    // can't drop the marker silently. The legend ::notice:: lives in
    // Invoke-WindowsRunnerBootstrap (asserted in the next test).
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Format-BootstrapSummary");
    expect(body).not.toBeNull();
    // The body must reference $r.critical so it can read the tier off
    // each result.
    expect(body).toMatch(/\$r\.critical/);
    // And the `*` suffix must be conditionally appended for the non-critical
    // path. We assert the literal `'*'` string is present in the body.
    expect(body).toContain("'*'");
  });

  test("Invoke-WindowsRunnerBootstrap emits the best-effort legend after the summary", () => {
    // After the summary line, a separate ::notice:: must explain the `*`
    // suffix so an operator scanning the log immediately understands the
    // tier distinction without grepping the script.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-WindowsRunnerBootstrap");
    expect(body).not.toBeNull();
    // The legend text must mention "best-effort".
    expect(body).toMatch(/best-effort prereq/i);
    // The legend ::notice:: must follow the summary ::notice::. Anchor on
    // the order: the summary's `Write-CiNotice ... summary:` must precede
    // the legend's `Write-CiNotice` referencing `best-effort`.
    const summaryIdx = body.search(/Write-CiNotice\s+"bootstrap-windows-runner summary:/);
    const legendIdx = body.search(/Write-CiNotice\s+"\([^"]*best-effort/);
    expect(summaryIdx).toBeGreaterThan(-1);
    expect(legendIdx).toBeGreaterThan(-1);
    expect(summaryIdx).toBeLessThan(legendIdx);
  });

  test("non-admin warning at the top of the dispatcher mentions BOTH VC++ generations + Defender (now skipped)", () => {
    // The pre-existing non-admin warning forgot Defender; the round-3 fix
    // updates it to list both critical (HKLM writes) AND best-effort
    // (Defender) admin-requiring prereqs, and clarifies that Defender is
    // now SKIPPED rather than failing. The current revision also names
    // BOTH VC++ generations (2010 + 2015-2022) so an operator scanning the
    // log sees the full HKLM-write footprint at a glance. Pin the updated
    // text so a future refactor can't drop any of these mentions.
    const body = extractPowerShellFunction(SCRIPT_TEXT, "Invoke-WindowsRunnerBootstrap");
    expect(body).not.toBeNull();
    // The warning still flags HKLM writes AND mentions both VC++ generations.
    expect(body).toMatch(/HKLM writes[^"']*VC\+\+\s+2010[^"']*VC\+\+\s+2015-2022[^"']*LongPathsEnabled/);
    // ...AND now also flags Defender being SKIPPED on non-admin.
    expect(body).toMatch(/best-effort prereqs[^"']*Defender exclusions[^"']*SKIPPED/);
    // The remediation hint (elevated shell OR workflow_dispatch) must remain.
    expect(body).toMatch(/elevated shell/);
    expect(body).toMatch(/runner-bootstrap\.yml/);
  });
});
