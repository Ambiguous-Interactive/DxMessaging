/**
 * @fileoverview Static contract test pinning three production fixes in
 * scripts/unity/ensure-editor.ps1. These are AST/text contracts (no Unity, no
 * network); they are sub-millisecond and run on every host, so a regression that
 * silently drops any of the three is caught at pre-push time.
 *
 * The three fixes (see the branch's CI triage):
 *
 *   2a. EVERY module install passes `--accept-eula`, enforced by making a single
 *       helper the SOLE PRODUCER of install args. The standalone Unity CLI aborts
 *       a module install (`install -m ...` OR `install-modules -m ...`) with "One
 *       or more modules require license acceptance. Pass --accept-eula ..." when
 *       an EULA-bearing module (Android SDK/NDK/OpenJDK) is requested. The flag
 *       MUST be on EVERY module-install (`-m`) invocation and MUST NOT be added to
 *       the `-l` listing call (which takes no EULA).
 *
 *       The contract's PRIMARY guard is a pwsh-AST sole-producer invariant
 *       (runSoleProducerAst): Get-UnityCliModuleInstallArguments is the ONLY place
 *       in the script that constructs an `install`/`install-modules` ... `-m` ...
 *       argument vector, and every live CLI invoker that performs a module install
 *       resolves its `-Arguments` to that helper. Because the AST inspects PARSED
 *       array nodes, a future bypass written multi-line, with reordered elements,
 *       or routed through an inline-built variable is caught -- not just a
 *       single-line literal. It is therefore structurally impossible for one site
 *       to carry the flag while another omits it (the exact drift that broke every
 *       CI cell). A cheap, always-on single-line text scan + helper-body
 *       cross-check runs even when pwsh is absent; the AST guard `test.skip`s
 *       cleanly when pwsh is unavailable (CI runners have pwsh).
 *
 *       This contract FAILS against the pre-fix code: the top-level `install`
 *       (`@('install', $UnityVersion, '-m') + @(Get-UnityCiModuleIds)`) and the
 *       repair-path `install` (`@('install', $Version, '-m') + $moduleIds`) both
 *       built a `-m` vector WITHOUT `--accept-eula` AT THE CALL SITE (outside any
 *       helper); the AST sole-producer guard flags an install vector outside the
 *       helper, and the behavior check flags the missing flag.
 *
 *   2b. The quarantine `Move-Item` is wrapped in `Invoke-WithRetry`. Moving a
 *       Unity editor directory on Windows can fail transiently with "The process
 *       cannot access the file ... because it is being used by another process."
 *       (Unity/AV/indexer holding a handle). The move must retry with backoff and
 *       rethrow on persistent failure.
 *
 *   2c. The CI-managed install-root guard (Confirm-UnityCliManagedInstallRoot)
 *       emits a wrap-IMMUNE `::error::` annotation before each `throw`. A thrown
 *       message is word-wrapped by PowerShell's ConciseView formatter at the
 *       console width; a `Write-Host "::error::..."` line is not, so CI gets a
 *       clean annotation AND a stable assertion target.
 *
 * The contract is asserted two ways: an always-on static text scan of the
 * function bodies (extracted by bounding each `function <Name>` to the next
 * top-level `function`), and -- when pwsh is present -- a PowerShell-AST
 * cross-check that the extracted-by-name function bodies agree, so the text scan
 * cannot be fooled by a same-named string elsewhere in the file.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const { assertSpawnStatus, normalizePwshText } = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const ENSURE_EDITOR = path.join(REPO_ROOT, "scripts", "unity", "ensure-editor.ps1");

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

/**
 * Extract the source text of a top-level `function <name> { ... }` by bounding it
 * at the next top-level `\nfunction ` definition. Mirrors the slicing the
 * idempotency test uses. Returns "" when not found.
 */
function extractFunctionBody(scriptText, functionName) {
  const start = scriptText.indexOf(`function ${functionName}`);
  if (start < 0) {
    return "";
  }
  const after = scriptText.indexOf("\nfunction ", start + 1);
  return after === -1 ? scriptText.slice(start) : scriptText.slice(start, after);
}

/**
 * Strip PowerShell line comments (and trailing inline comments) so a contract
 * assertion targets CODE, not prose. The WHY/justification comments in
 * Get-UnityCiModuleIds legitimately MENTION 'android-open-jdk' to explain why it
 * is intentionally absent from the returned list; stripping comments lets us
 * assert against the actual returned ids. Conservative: this does not parse
 * strings, so a `#` inside a single-quoted string on a code line would be
 * mis-stripped -- none of the lines we assert against contain one.
 */
function stripPwshComments(body) {
  return body
    .split(/\r?\n/)
    .map((line) => {
      const trimmed = line.trim();
      if (trimmed.startsWith("#")) {
        return "";
      }
      return line.replace(/#.*$/, "");
    })
    .join("\n");
}

/**
 * FIX-2 sole-producer invariant, enforced via the PowerShell AST.
 *
 * Parses `scriptPath` and asserts:
 *
 *   (A) SOLE PRODUCER. Every array node (an array literal `@(...)` OR a bare
 *       `a, b, c` comma list) whose STRING-CONSTANT elements include an install
 *       verb (`install`/`install-modules`) AND the `-m` flag -- i.e. a Unity-CLI
 *       module-install argument vector -- lies INSIDE the body of
 *       Get-UnityCliModuleInstallArguments. Because the check is over PARSED
 *       array nodes (not source lines), it catches a bypass written multi-line,
 *       with reordered elements, or assigned to a variable -- any inline build of
 *       an `-m` install vector at a call site fails it. There must be >= 2 such
 *       vectors (the helper's own `install` + `install-modules` returns).
 *
 *   (B) ROUTING. Every live CLI invoker call (Invoke-UnityCliCapture /
 *       Invoke-UnityCliSafe / Get-UnityCliOutput) that targets a module install
 *       resolves its `-Arguments` to the helper -- either by calling the helper
 *       inline, or by passing a variable that was assigned from the helper. A
 *       call site whose `-Arguments` contains an inline `-m` install vector is a
 *       bypass (also flagged by (A)). At least 3 invoker call sites must route
 *       through the helper (primary install, repair install, install-modules add).
 *
 *   (C) BEHAVIOR. Executing the extracted helper produces an `--accept-eula` +
 *       `-m` vector for BOTH verbs, so the contract pins behavior, not just text.
 *
 * Returns { ok, text } where `text` is the normalized pwsh stdout/stderr. The
 * caller throws on !ok so the failing assertion names the offending line.
 */
function runSoleProducerAst(scriptPath) {
  const harness = [
    "Set-StrictMode -Version Latest",
    "$ErrorActionPreference = 'Stop'",
    `$src = '${scriptPath.replace(/'/g, "''")}'`,
    "$tokens = $null; $errs = $null",
    "$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errs)",
    "if ($errs -and $errs.Count -gt 0) { Write-Host 'FAIL parse errors'; exit 3 }",
    "$helperName = 'Get-UnityCliModuleInstallArguments'",
    "$helperHits = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $helperName }, $true))",
    "if ($helperHits.Count -lt 1) { Write-Host 'FAIL no helper fn'; exit 4 }",
    "$helper = $helperHits[0]",
    "$hStart = $helper.Extent.StartOffset",
    "$hEnd = $helper.Extent.EndOffset",
    // Collect the string-constant element values of an array node (literal or @()).
    "function Get-VectorStrings { param($node)",
    "  $elements = @()",
    "  if ($node -is [System.Management.Automation.Language.ArrayLiteralAst]) { $elements = $node.Elements }",
    "  elseif ($node -is [System.Management.Automation.Language.ArrayExpressionAst]) {",
    "    $inner = @($node.SubExpression.Statements | ForEach-Object {",
    "      if ($_ -is [System.Management.Automation.Language.PipelineAst]) {",
    "        $_.PipelineElements | ForEach-Object { if ($_ -is [System.Management.Automation.Language.CommandExpressionAst]) { $_.Expression } }",
    "      } })",
    "    $flat = @()",
    "    foreach ($e in $inner) { if ($e -is [System.Management.Automation.Language.ArrayLiteralAst]) { $flat += $e.Elements } else { $flat += $e } }",
    "    $elements = $flat",
    "  }",
    "  return @($elements | Where-Object { $_ -is [System.Management.Automation.Language.StringConstantExpressionAst] } | ForEach-Object { $_.Value })",
    "}",
    "function Test-IsInstallVector { param($node)",
    "  $strs = @(Get-VectorStrings $node)",
    "  $hasVerb = ($strs -contains 'install') -or ($strs -contains 'install-modules')",
    "  $hasM = ($strs -contains '-m')",
    "  return ($hasVerb -and $hasM)",
    "}",
    // (A) sole producer.
    "$arrayNodes = @($ast.FindAll({ param($n) ($n -is [System.Management.Automation.Language.ArrayLiteralAst]) -or ($n -is [System.Management.Automation.Language.ArrayExpressionAst]) }, $true))",
    "$installVectors = @($arrayNodes | Where-Object { Test-IsInstallVector $_ })",
    "$outside = @($installVectors | Where-Object { -not ($_.Extent.StartOffset -ge $hStart -and $_.Extent.EndOffset -le $hEnd) })",
    "$ok = $true",
    "if ($installVectors.Count -lt 2) { Write-Host 'FAIL fewer than 2 install vectors (helper should hold install + install-modules)'; $ok = $false }",
    "foreach ($o in $outside) { Write-Host ('FAIL install-vector OUTSIDE helper at line {0}: {1}' -f $o.Extent.StartLineNumber, ($o.Extent.Text -replace '\\s+', ' ')); $ok = $false }",
    // (B) routing: build a var-name -> assigned-from-helper map, then inspect invoker calls.
    "$invokerNames = @('Invoke-UnityCliCapture', 'Invoke-UnityCliSafe', 'Get-UnityCliOutput')",
    "$cmds = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))",
    "$helperVars = @{}",
    "$assignments = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true))",
    "foreach ($a in $assignments) {",
    "  if ($a.Left -is [System.Management.Automation.Language.VariableExpressionAst]) {",
    "    $varName = $a.Left.VariablePath.UserPath",
    "    $rhsCalls = @($a.Right.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))",
    "    foreach ($rc in $rhsCalls) { if ($rc.GetCommandName() -eq $helperName) { $helperVars[$varName] = $true } }",
    "  }",
    "}",
    "function Get-ArgumentsValueAst { param($cmd)",
    "  $els = $cmd.CommandElements",
    "  for ($i = 0; $i -lt $els.Count; $i++) {",
    "    $e = $els[$i]",
    "    if ($e -is [System.Management.Automation.Language.CommandParameterAst] -and $e.ParameterName -eq 'Arguments') {",
    "      if ($e.Argument) { return $e.Argument }",
    "      if (($i + 1) -lt $els.Count) { return $els[$i + 1] }",
    "    }",
    "  }",
    "  return $null",
    "}",
    "$routed = 0",
    "foreach ($c in $cmds) {",
    "  if ($invokerNames -notcontains $c.GetCommandName()) { continue }",
    "  $argVal = Get-ArgumentsValueAst $c",
    "  if (-not $argVal) { continue }",
    "  $argArrays = @($argVal.FindAll({ param($n) ($n -is [System.Management.Automation.Language.ArrayLiteralAst]) -or ($n -is [System.Management.Automation.Language.ArrayExpressionAst]) }, $true))",
    "  $hasInlineInstallVector = (@($argArrays | Where-Object { Test-IsInstallVector $_ }).Count -gt 0)",
    "  $argCalls = @($argVal.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true))",
    "  $callsHelperInline = (@($argCalls | Where-Object { $_.GetCommandName() -eq $helperName }).Count -gt 0)",
    "  $isHelperVar = $false",
    "  if ($argVal -is [System.Management.Automation.Language.VariableExpressionAst]) { if ($helperVars.ContainsKey($argVal.VariablePath.UserPath)) { $isHelperVar = $true } }",
    "  if ($hasInlineInstallVector) { Write-Host ('FAIL module-install invoker at line {0} builds an inline -m vector instead of routing through {1}' -f $c.Extent.StartLineNumber, $helperName); $ok = $false; continue }",
    "  if ($callsHelperInline -or $isHelperVar) { $routed++ }",
    "}",
    "if ($routed -lt 3) { Write-Host ('FAIL fewer than 3 routed module-install invokers (got {0})' -f $routed); $ok = $false }",
    // (C) behavior: execute the helper + ids fns (and the spec the ids derive from)
    // and check both verbs, both default and a tier subset.
    "$dependencyNames = @('Get-UnityProvisioningProfile','Assert-UnityProvisioningProfile','Get-UnityCiModuleSpec','Get-UnityCiModuleSpecForProfile','Get-UnityCiModuleIds','Get-UnityCiModuleIdsForTier')",
    "$dependencyFns = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $dependencyNames -contains $n.Name }, $true))",
    "foreach ($name in $dependencyNames) { if (-not ($dependencyFns | Where-Object { $_.Name -eq $name })) { Write-Host ('FAIL missing dependency fn ' + $name); exit 9 } }",
    "foreach ($name in $dependencyNames) { $fn = ($dependencyFns | Where-Object { $_.Name -eq $name })[0]; Invoke-Expression $fn.Extent.Text }",
    "Invoke-Expression $helper.Extent.Text",
    "$installArgs = @(Get-UnityCliModuleInstallArguments -Verb 'install' -Version '6000.0.32f1')",
    "$modArgs = @(Get-UnityCliModuleInstallArguments -Verb 'install-modules' -Version '6000.0.32f1')",
    "$coreArgs = @(Get-UnityCliModuleInstallArguments -Verb 'install' -Version '6000.0.32f1' -ModuleIds (Get-UnityCiModuleIdsForTier -Tier 'core'))",
    "$androidArgs = @(Get-UnityCliModuleInstallArguments -Verb 'install-modules' -Version '6000.0.32f1' -ModuleIds (Get-UnityCiModuleIdsForTier -Tier 'android'))",
    "if ($installArgs -notcontains '--accept-eula') { Write-Host 'FAIL install verb missing eula'; $ok = $false }",
    "if ($modArgs -notcontains '--accept-eula') { Write-Host 'FAIL install-modules verb missing eula'; $ok = $false }",
    "if ($installArgs -notcontains '-m') { Write-Host 'FAIL install verb missing -m'; $ok = $false }",
    "if ($modArgs -notcontains '-m') { Write-Host 'FAIL install-modules verb missing -m'; $ok = $false }",
    // A tier-scoped subset STILL carries --accept-eula + -m (the sole producer owns the shape).
    "if ($coreArgs -notcontains '--accept-eula') { Write-Host 'FAIL tier subset missing eula'; $ok = $false }",
    "if ($coreArgs -notcontains '-m') { Write-Host 'FAIL tier subset missing -m'; $ok = $false }",
    "if ($coreArgs -contains 'android') { Write-Host 'FAIL core subset contains android tier id'; $ok = $false }",
    "if ($installArgs -notcontains '--childModules') { Write-Host 'FAIL full install missing childModules'; $ok = $false }",
    "if ($androidArgs -notcontains '--childModules') { Write-Host 'FAIL android install-modules missing childModules'; $ok = $false }",
    "if ($coreArgs -contains '--childModules') { Write-Host 'FAIL core subset should not include childModules'; $ok = $false }",
    "if ($androidArgs -contains 'android-open-jdk') { Write-Host 'FAIL android subset requests android-open-jdk'; $ok = $false }",
    "if ($ok) { Write-Output 'FIX2-OK' } else { exit 7 }"
  ].join("\n");

  const run = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", harness], {
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024
  });
  const text = normalizePwshText(`${run.stdout || ""}\n${run.stderr || ""}`);
  return { ok: run.status === 0, text };
}

/**
 * SPEC-CONSISTENCY invariant, enforced by EXECUTING the spec + derived functions.
 *
 * AST-extracts Get-UnityCiModuleSpec and the functions derived from it
 * (Get-UnityCiModuleIds, Get-UnityCiVerifiedModuleGroups, Get-UnityCiModuleIdsForTier,
 * Get-UnityCiModuleTier) plus Test-UnityCiModuleGroupPresent, Invoke-Expression's
 * them, and asserts (against the RETURNED values, not text):
 *
 *   - requested ids EXCLUDE 'android-open-jdk' and INCLUDE 'android-sdk-ndk-tools';
 *   - verified groups INCLUDE 'android-open-jdk';
 *   - verified == requested + verified-only (i.e. the spec rows are internally
 *     consistent: every requested id is verified here too);
 *   - the core/android tier ids partition the requested ids (no overlap, union ==
 *     requested);
 *   - every spec id is handled by the Test-UnityCiModuleGroupPresent switch and
 *     every switch case maps to a spec id (no drift in EITHER direction).
 *
 * Returns { ok, text } where text is the normalized pwsh stdout/stderr.
 */
function runSpecConsistency(scriptPath) {
  const harness = [
    "Set-StrictMode -Version Latest",
    "$ErrorActionPreference = 'Stop'",
    `$src = '${scriptPath.replace(/'/g, "''")}'`,
    "$tokens = $null; $errs = $null",
    "$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errs)",
    "if ($errs -and $errs.Count -gt 0) { Write-Host 'FAIL parse errors'; exit 3 }",
    "$wanted = @('Get-UnityProvisioningProfile','Assert-UnityProvisioningProfile','Get-UnityCiModuleSpec','Get-UnityCiModuleSpecForProfile','Get-UnityCiModuleIds','Get-UnityCiVerifiedModuleGroups','Get-UnityCiModuleIdsForTier','Get-UnityCiModuleTier','Test-UnityCiModuleGroupPresent')",
    "$fns = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $n.Name }, $true))",
    "foreach ($w in $wanted) { if (-not ($fns | Where-Object { $_.Name -eq $w })) { Write-Host ('FAIL missing fn ' + $w); exit 4 } }",
    "foreach ($f in $fns) { Invoke-Expression $f.Extent.Text }",
    "$ok = $true",
    "$requested = @(Get-UnityCiModuleIds)",
    "$verified = @(Get-UnityCiVerifiedModuleGroups)",
    "$core = @(Get-UnityCiModuleIdsForTier -Tier 'core')",
    "$android = @(Get-UnityCiModuleIdsForTier -Tier 'android')",
    "$editorOnly = @(Get-UnityCiVerifiedModuleGroups -Profile 'EditorOnly')",
    "$standalone = @(Get-UnityCiVerifiedModuleGroups -Profile 'StandaloneWindowsIl2Cpp')",
    "$androidProfile = @(Get-UnityCiVerifiedModuleGroups -Profile 'Android')",
    "$specIds = @(Get-UnityCiModuleSpec | ForEach-Object { $_.Id })",
    "if ($requested -contains 'android-open-jdk') { Write-Host 'FAIL requested includes android-open-jdk'; $ok = $false }",
    "if ($requested -notcontains 'android-sdk-ndk-tools') { Write-Host 'FAIL requested missing android-sdk-ndk-tools'; $ok = $false }",
    "if ($verified -notcontains 'android-open-jdk') { Write-Host 'FAIL verified missing android-open-jdk'; $ok = $false }",
    // verified == requested + verified-only (every requested id is also verified).
    "foreach ($r in $requested) { if ($verified -notcontains $r) { Write-Host ('FAIL requested id not verified: ' + $r); $ok = $false } }",
    // tier partition: core + android == requested, no overlap.
    "$tierUnion = @($core + $android)",
    "foreach ($r in $requested) { if ($tierUnion -notcontains $r) { Write-Host ('FAIL requested id in no tier: ' + $r); $ok = $false } }",
    "foreach ($t in $tierUnion) { if ($requested -notcontains $t) { Write-Host ('FAIL tier id not requested: ' + $t); $ok = $false } }",
    "foreach ($c in $core) { if ($android -contains $c) { Write-Host ('FAIL id in both tiers: ' + $c); $ok = $false } }",
    "if ($editorOnly.Count -ne 0) { Write-Host 'FAIL EditorOnly should verify no module groups'; $ok = $false }",
    "if ($standalone.Count -ne 1 -or $standalone[0] -ne 'windows-il2cpp') { Write-Host ('FAIL standalone profile groups: ' + ($standalone -join ',')); $ok = $false }",
    "foreach ($id in @('android','android-sdk-ndk-tools','android-open-jdk')) { if ($androidProfile -notcontains $id) { Write-Host ('FAIL Android profile missing ' + $id); $ok = $false } }",
    "foreach ($id in @('windows-il2cpp','webgl','linux-mono','linux-il2cpp')) { if ($androidProfile -contains $id) { Write-Host ('FAIL Android profile includes non-Android group ' + $id); $ok = $false } }",
    // Get-UnityCiModuleTier round-trips for every spec id.
    "foreach ($id in $specIds) { $tier = Get-UnityCiModuleTier $id; if ($tier -notin @('core','android')) { Write-Host ('FAIL bad tier for ' + $id + ': ' + $tier); $ok = $false } }",
    // Switch <-> spec parity: every spec id is a switch case, and vice-versa. The
    // switch clauses are string-constant labels inside Test-UnityCiModuleGroupPresent.
    "$switchFn = ($fns | Where-Object { $_.Name -eq 'Test-UnityCiModuleGroupPresent' })[0]",
    "$switchNodes = @($switchFn.FindAll({ param($n) $n -is [System.Management.Automation.Language.SwitchStatementAst] }, $true))",
    "if ($switchNodes.Count -lt 1) { Write-Host 'FAIL no switch in Test-UnityCiModuleGroupPresent'; exit 5 }",
    "$caseLabels = New-Object System.Collections.Generic.List[string]",
    "foreach ($sw in $switchNodes) { foreach ($clause in $sw.Clauses) { if ($clause.Item1 -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $caseLabels.Add($clause.Item1.Value) } } }",
    "$cases = @($caseLabels.ToArray())",
    "foreach ($id in $specIds) { if ($cases -notcontains $id) { Write-Host ('FAIL spec id not handled by switch: ' + $id); $ok = $false } }",
    "foreach ($c in $cases) { if ($specIds -notcontains $c) { Write-Host ('FAIL switch case not in spec: ' + $c); $ok = $false } }",
    "if ($ok) { Write-Output 'SPEC-OK' } else { exit 7 }"
  ].join("\n");

  const run = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", harness], {
    encoding: "utf8",
    maxBuffer: 16 * 1024 * 1024
  });
  const text = normalizePwshText(`${run.stdout || ""}\n${run.stderr || ""}`);
  return { ok: run.status === 0, text };
}

let scriptText;

beforeAll(() => {
  expect(fs.existsSync(ENSURE_EDITOR)).toBe(true);
  scriptText = fs.readFileSync(ENSURE_EDITOR, "utf8");
});

describe("ensure-editor.ps1 production contract", () => {
  test("the script under test exists and requires PowerShell 5.1 and StrictMode", () => {
    // The fixes must remain compatible with the script's declared baseline.
    expect(scriptText).toContain("#Requires -Version 5.1");
    expect(scriptText).toContain("Set-StrictMode -Version Latest");
    expect(scriptText).toContain("function ConvertTo-ProcessArgumentLine");
    expect(scriptText).not.toContain(".ArgumentList");
  });

  // --- 2a: --accept-eula on EVERY module INSTALL call, not the listing call. ---
  describe("2a: every module install passes --accept-eula", () => {
    // INVARIANT (the contract this block enforces):
    //   Get-UnityCliModuleInstallArguments is the SOLE PRODUCER of a Unity-CLI
    //   module-install argument vector (an `install`/`install-modules` ... `-m` ...
    //   vector). Every live CLI invocation that performs a module install derives
    //   its `-Arguments` from that helper. Because the flag is injected in exactly
    //   one place, it is structurally impossible for one call site to carry
    //   `--accept-eula` while another omits it -- the exact drift that broke every
    //   CI cell.
    //
    // The PRIMARY guard is a pwsh-AST scan (below) that finds EVERY array node
    // (literal `@(...)` or bare `a, b, c`) whose string elements include an
    // install verb AND `-m`, and asserts they ALL live inside the helper's body.
    // The AST sees the parsed vector regardless of how it is written, so a
    // multi-line, reordered, or variable-assigned bypass at a future call site is
    // caught -- not just a single-line literal.
    //
    // A complementary single-line TEXT scan (`enumerateModuleInstallLines`) drives
    // a per-vector table for at-a-glance reporting. After the single-source-of-truth
    // refactor the ONLY single-line install vectors in the file are the helper's own
    // two `return` lines, so this scan is intentionally NARROW (single-line only);
    // it does NOT claim to catch every bypass shape -- that is the AST guard's job.
    // The `-l` LISTING call is deliberately excluded (it carries `-l`, never `-m`):
    // it takes no license, so `--accept-eula` there would be wrong.
    function enumerateModuleInstallLines(text) {
      const out = [];
      const lines = text.split(/\r?\n/);
      for (let i = 0; i < lines.length; i += 1) {
        const line = lines[i];
        const trimmed = line.trim();
        // Skip pure-comment lines so prose mentioning "-m" or "install" never
        // counts as an invocation.
        if (trimmed.startsWith("#")) {
          continue;
        }
        // Strip a trailing inline comment so a `# ...` tail cannot smuggle tokens.
        const code = line.replace(/#.*$/, "");
        // Must construct an install/install-modules verb literal AND carry the `-m`
        // module flag literal on the SAME line. NOTE: this single-line matcher is a
        // reporting convenience only -- the AST guard below is the real invariant
        // and covers multi-line / reordered / variable-routed shapes this misses.
        const hasInstallVerb = /'install'|'install-modules'/.test(code);
        const hasModuleFlag = /'-m'/.test(code);
        if (hasInstallVerb && hasModuleFlag) {
          out.push({ lineNumber: i + 1, code: code.trim() });
        }
      }
      return out;
    }

    let moduleInstallLines;
    beforeAll(() => {
      moduleInstallLines = enumerateModuleInstallLines(scriptText);
    });

    test("at least one single-line install vector exists to report against", () => {
      // Guard against a scan that silently matches nothing (which would make the
      // per-line assertions vacuously pass). After the single-source-of-truth
      // refactor the helper alone contributes two (`install` + `install-modules`).
      expect(moduleInstallLines.length).toBeGreaterThanOrEqual(2);
    });

    test("EVERY single-line install (-m) vector includes --accept-eula", () => {
      // If any single-line `-m` install vector lacks the flag, this fails and names
      // the exact line. (The AST guard additionally covers non-single-line shapes.)
      const offenders = moduleInstallLines.filter((entry) => !entry.code.includes("--accept-eula"));
      const detail = offenders.map((o) => `  line ${o.lineNumber}: ${o.code}`).join("\n");
      expect(offenders.length === 0 ? "" : detail).toBe("");
    });

    describe("each single-line install vector (table-driven, reporting only)", () => {
      // Snapshot the table at module-load time so test.each has a static table;
      // the always-on beforeAll re-derivation above guards the same source.
      const table = enumerateModuleInstallLines(fs.readFileSync(ENSURE_EDITOR, "utf8"));
      test.each(table)("line $lineNumber carries --accept-eula", ({ code }) => {
        expect(code).toContain("--accept-eula");
      });
    });

    test("every single-line install vector lives inside the helper body (text cross-check)", () => {
      // A cheap, always-on text complement to the AST sole-producer guard: each
      // single-line install vector text must be a substring of the helper body. This
      // runs even when pwsh is absent; the AST guard below is the authoritative,
      // shape-independent version.
      const helperBody = extractFunctionBody(scriptText, "Get-UnityCliModuleInstallArguments");
      expect(helperBody).not.toBe("");
      const outside = moduleInstallLines.filter((entry) => !helperBody.includes(entry.code));
      const detail = outside.map((o) => `  line ${o.lineNumber}: ${o.code}`).join("\n");
      expect(outside.length === 0 ? "" : detail).toBe("");
    });

    test("the LISTING (-l) call does NOT include --accept-eula", () => {
      // The `-l` listing call takes no license; the flag there would be wrong.
      const listCall = /install-modules'[^\n]*'-l'[^\n]*/.exec(scriptText);
      expect(listCall).not.toBeNull();
      expect(listCall[0]).not.toContain("--accept-eula");
    });

    // --- single source of truth: the helper exists and every call site routes
    // through it, so the EULA flag cannot drift between call sites. ---
    test("the single source-of-truth helper Get-UnityCliModuleInstallArguments exists", () => {
      expect(scriptText).toContain("function Get-UnityCliModuleInstallArguments");
      const body = extractFunctionBody(scriptText, "Get-UnityCliModuleInstallArguments");
      expect(body).not.toBe("");
      // The helper is the one place the flag is injected; it must mention it for
      // BOTH verbs it handles.
      expect(body).toContain("--accept-eula");
      expect(body).toContain("install-modules");
      expect(body).toMatch(/ValidateSet\('install', 'install-modules'\)/);
    });

    test("all three module-install call sites route through the helper", () => {
      // Top-level primary install, repair-path install, and the install-modules
      // module-add must each call Get-UnityCliModuleInstallArguments rather than
      // hand-building a `-m` vector. We require at least three routed call sites
      // (the install-modules vector is captured once and reused, so it appears once).
      const routed =
        scriptText.match(
          /Get-UnityCliModuleInstallArguments\s+-Verb\s+'(install|install-modules)'/g
        ) || [];
      expect(routed.length).toBeGreaterThanOrEqual(3);
      // Both verbs are exercised (install for the two install paths; install-modules
      // for the module-add path).
      expect(routed.some((m) => /'install'/.test(m))).toBe(true);
      expect(routed.some((m) => /'install-modules'/.test(m))).toBe(true);
    });

    // --- PRIMARY sole-producer invariant: pwsh-AST, shape-independent. ---
    // This is the strong guard the single-line text scan cannot be: it parses the
    // script and inspects PARSED array nodes, so it catches a future bypass written
    // multi-line, reordered, or routed through an inline-built variable.
    if (!PWSH_PRESENT) {
      // eslint-disable-next-line no-console
      console.warn(
        "[ensure-editor-production-contract] pwsh not found; skipping AST sole-producer guard (CI runners have pwsh)."
      );
      test.skip("pwsh AST: Get-UnityCliModuleInstallArguments is the SOLE PRODUCER of -m install vectors", () => {});
    } else {
      test("pwsh AST: Get-UnityCliModuleInstallArguments is the SOLE PRODUCER of -m install vectors", () => {
        const result = runSoleProducerAst(ENSURE_EDITOR);
        if (!result.ok) {
          throw new Error(result.text);
        }
        expect(result.text).toContain("FIX2-OK");
      });
    }
  });

  // --- 2a-bis: requested ids vs verified disk groups are cleanly decoupled, and
  //     both DERIVE from the single source of truth Get-UnityCiModuleSpec. ---
  // These tests EXECUTE the spec/derived functions via pwsh (not a body-text scan)
  // so the assertions pin the actual RETURNED ids, immune to how the spec is
  // formatted. When pwsh is absent they skip cleanly (CI runners have pwsh); a
  // cheap always-on text sanity check below guards the zero-coverage case.
  describe("2a-bis: android-open-jdk is verified on disk but not requested from the CLI", () => {
    // Always-on, pwsh-free sanity: the spec function and the derived list functions
    // exist, and the spec carries the two id strings the split hinges on. The
    // EXECUTION-based assertions below are the authoritative ones.
    test("the single source of truth Get-UnityCiModuleSpec and the derived list functions exist", () => {
      expect(scriptText).toContain("function Get-UnityCiModuleSpec");
      expect(scriptText).toContain("function Get-UnityCiModuleSpecForProfile");
      expect(scriptText).toContain("function Get-UnityCiModuleIds");
      expect(scriptText).toContain("function Get-UnityCiVerifiedModuleGroups");
      const specBody = extractFunctionBody(scriptText, "Get-UnityCiModuleSpec");
      const specCode = stripPwshComments(specBody);
      expect(specCode).toContain("'android-sdk-ndk-tools'");
      expect(specCode).toContain("'android-open-jdk'");
      // The derived list functions must DERIVE from the spec (no hardcoded ids).
      expect(extractFunctionBody(scriptText, "Get-UnityCiModuleIds")).toContain(
        "Get-UnityCiModuleSpecForProfile"
      );
      expect(extractFunctionBody(scriptText, "Get-UnityCiVerifiedModuleGroups")).toContain(
        "Get-UnityCiModuleSpecForProfile"
      );
    });

    if (!PWSH_PRESENT) {
      // eslint-disable-next-line no-console
      console.warn(
        "[ensure-editor-production-contract] pwsh not found; skipping spec-execution module-set guards (CI runners have pwsh)."
      );
      test.skip("pwsh: requested/verified ids and the switch derive consistently from the spec", () => {});
    } else {
      test("pwsh: requested/verified ids and the switch derive consistently from the spec", () => {
        const result = runSpecConsistency(ENSURE_EDITOR);
        if (!result.ok) {
          throw new Error(result.text);
        }
        expect(result.text).toContain("SPEC-OK");
      });
    }

    // Get-UnityCiModuleIdsForTier must return the right ids for the known tiers and
    // THROW on an unknown one (so a bogus tier can never silently yield an empty,
    // id-less `-m` vector). EXECUTION-based (AST-extract the spec + the tier helper).
    if (!PWSH_PRESENT) {
      test.skip("pwsh: Get-UnityCiModuleIdsForTier returns tier ids and throws on an unknown tier", () => {});
    } else {
      test("pwsh: Get-UnityCiModuleIdsForTier returns tier ids and throws on an unknown tier", () => {
        const harness = [
          "Set-StrictMode -Version Latest",
          "$ErrorActionPreference = 'Stop'",
          `$src = '${ENSURE_EDITOR.replace(/'/g, "''")}'`,
          "$tokens = $null; $errs = $null",
          "$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errs)",
          "if ($errs -and $errs.Count -gt 0) { Write-Host 'FAIL parse errors'; exit 3 }",
          "$wanted = @('Get-UnityProvisioningProfile','Assert-UnityProvisioningProfile','Get-UnityCiModuleSpec','Get-UnityCiModuleSpecForProfile','Get-UnityCiModuleIdsForTier')",
          "$fns = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $wanted -contains $n.Name }, $true))",
          "foreach ($w in $wanted) { if (-not ($fns | Where-Object { $_.Name -eq $w })) { Write-Host ('FAIL missing fn ' + $w); exit 4 } }",
          "foreach ($f in $fns) { Invoke-Expression $f.Extent.Text }",
          // Known tiers return the expected ids (joined for a stable assertion).
          "Write-Output ('CORE=' + (@(Get-UnityCiModuleIdsForTier -Tier 'core') -join ','))",
          "Write-Output ('ANDROID=' + (@(Get-UnityCiModuleIdsForTier -Tier 'android') -join ','))",
          // An unknown tier MUST throw (never return an empty list).
          "try { $null = Get-UnityCiModuleIdsForTier -Tier 'bogus'; Write-Output 'THREW=False' }",
          "catch { Write-Output ('THREW=True; MSG=' + $_.Exception.Message) }"
        ].join("\n");
        const run = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", harness], {
          encoding: "utf8",
          maxBuffer: 16 * 1024 * 1024
        });
        const text = normalizePwshText(`${run.stdout || ""}\n${run.stderr || ""}`);
        assertSpawnStatus(run, 0, expect.getState().currentTestName || "pwsh harness");
        // Core ids: the four reliable-provisioning groups, in spec order.
        expect(text).toContain("CORE=windows-il2cpp,webgl,linux-mono,linux-il2cpp");
        // Android ids: the requested android-tier groups (android-open-jdk is
        // verified-only -> NOT requested, so it must be absent here).
        expect(text).toContain("ANDROID=android,android-sdk-ndk-tools");
        expect(text).not.toContain("android-open-jdk");
        // The unknown tier threw with the documented message.
        expect(text).toContain("THREW=True");
        expect(text).toContain("Unknown Unity CI module tier 'bogus'.");
      });
    }

    test("the disk-verification iterator uses the VERIFIED groups, not the requested ids", () => {
      const body = extractFunctionBody(scriptText, "Get-MissingUnityCiModuleGroups");
      expect(body).not.toBe("");
      expect(body).toContain("Get-UnityCiVerifiedModuleGroups");
    });

    test("the on-disk switch still verifies the android-open-jdk OpenJDK leaf", () => {
      const body = extractFunctionBody(scriptText, "Test-UnityCiModuleGroupPresent");
      expect(body).toContain("'android-open-jdk'");
      expect(body).toContain("OpenJDK");
    });
  });

  // --- 2b: the quarantine Move-Item is wrapped in Invoke-WithRetry. ---
  describe("2b: quarantine Move-Item is retried", () => {
    let quarantineBody;
    let cleanupBody;

    beforeAll(() => {
      quarantineBody = extractFunctionBody(scriptText, "Move-UnityInstallDirectoryToQuarantine");
      cleanupBody = extractFunctionBody(scriptText, "Stop-StaleUnityProvisioningProcesses");
    });

    test("the quarantine helper exists and performs the Move-Item", () => {
      expect(quarantineBody).not.toBe("");
      expect(quarantineBody).toContain("Move-Item -LiteralPath $InstallDirectory");
    });

    test("the Move-Item is wrapped in Invoke-WithRetry with multiple attempts", () => {
      // Invoke-WithRetry must appear, declare more than one attempt, and the
      // Move-Item must sit inside its -Action block (i.e. the retry precedes the
      // move within the body).
      expect(quarantineBody).toMatch(/Invoke-WithRetry\b/);
      expect(quarantineBody).toMatch(/Invoke-WithRetry[^\n]*-MaxAttempts\s+([2-9]|\d{2,})/);
      const retryIndex = quarantineBody.indexOf("Invoke-WithRetry");
      const moveIndex = quarantineBody.indexOf("Move-Item -LiteralPath $InstallDirectory");
      expect(retryIndex).toBeGreaterThanOrEqual(0);
      expect(moveIndex).toBeGreaterThan(retryIndex);
    });

    test("the retry delay is sourced from the shared override-aware helper", () => {
      // So tests can zero the backoff via DXM_ENSURE_EDITOR_RETRY_DELAY_SECONDS
      // and CI keeps the production default.
      expect(quarantineBody).toContain("Get-EnsureEditorRetryDelaySeconds");
    });

    test("stale Unity process cleanup runs before managed quarantine", () => {
      expect(cleanupBody).not.toBe("");
      expect(cleanupBody).toContain("Get-CimInstance Win32_Process");
      expect(cleanupBody).toContain("Stop-Process");
      expect(cleanupBody).toContain("Add-ProvisioningProcessCleanupEvent");
      const cleanupIndex = quarantineBody.indexOf("Stop-StaleUnityProvisioningProcesses");
      const moveIndex = quarantineBody.indexOf("Move-Item -LiteralPath $InstallDirectory");
      expect(cleanupIndex).toBeGreaterThanOrEqual(0);
      expect(moveIndex).toBeGreaterThan(cleanupIndex);
    });

    // REGRESSION (Unity 6000.3.16f1 standalone, run 26701943540): the quarantine
    // Move-Item failed all 3 retries with "...because it is being used by another
    // process." on C:\Unity\Editors\6000.3.16f1\Editor, while the stale-process
    // sweep "matched 0". The sweep scopes a locker by its IMAGE PATH and by its
    // LOADED MODULES, but ONLY within THIS version's directory -- never the bare
    // managed root. These assertions pin that the sweep reads ExecutablePath, scopes
    // by an under-VERSION-DIR image path, and consults the loaded-module fallback.
    test("the stale-process sweep scopes by executable image path under the VERSION dir", () => {
      expect(cleanupBody).toContain("ExecutablePath");
      // The image-path scoping is expressed via the shared path-containment helper.
      expect(cleanupBody).toMatch(/Test-IsPathInsideDirectory[^\n]*\$executablePath/);
      // It must consider an image under the VERSION directory (the signal that
      // survives an empty CommandLine).
      expect(cleanupBody).toContain("imageInsideVersionDir");
      // Defense-in-depth cross-identity signal: loaded modules under the version dir.
      expect(cleanupBody).toContain("Test-ProcessHasModuleUnderDirectory");
    });

    // HIGH collateral-kill fix (the rework's non-negotiable): the sweep must NOT
    // force-kill a Unity-named binary merely because its image sits SOMEWHERE under
    // the shared managed root (a concurrent SIBLING-version editor). The unconditional
    // image/module kill is VERSION-DIR scoped; a broad match requires the command
    // line to tie the process to THIS version. Pin that the dropped, dangerous
    // `imageInsideRoot && looksUnity` kill branch is gone.
    test("the sweep does NOT kill a Unity-named binary by bare under-ROOT image alone (no cross-version collateral)", () => {
      // Assert against CODE, not prose: the explanatory comments legitimately name
      // the removed `imageInsideRoot` branch to document why it is gone.
      const cleanupCode = stripPwshComments(cleanupBody);
      // The bare-root scoping variable must no longer exist as a kill input.
      expect(cleanupCode).not.toContain("imageInsideRoot");
      // The final scoping decision pairs looksUnity with commandLineScoped only.
      const scopeMatch = /\$isScoped\s*=([^\n]*)/.exec(cleanupCode);
      expect(scopeMatch).not.toBeNull();
      const scopeExpr = scopeMatch[1];
      expect(scopeExpr).toContain("imageInsideVersionDir");
      expect(scopeExpr).toContain("commandLineScoped");
      expect(scopeExpr).not.toContain("imageInsideRoot");
    });

    test("the quarantine re-sweeps stale processes between move retries and annotates a persistent lock", () => {
      // The sweep is invoked at least twice in the helper body: once up-front and
      // once inside the retry action (so a transient AV/installer/indexer handle
      // gets multiple shots to release as the move is re-attempted).
      const sweepCalls = quarantineBody.match(/Stop-StaleUnityProvisioningProcesses\b/g) || [];
      expect(sweepCalls.length).toBeGreaterThanOrEqual(2);
      // A persistent lock surfaces a wrap-immune ::error:: (Write-Host, not a bare
      // throw whose message ConciseView would word-wrap) before re-throwing.
      expect(quarantineBody).toMatch(/Write-Host\s+\([^\n]*::error::Could not quarantine Unity/);
    });
  });

  // --- 2c: the managed-root guard emits a wrap-immune ::error:: before throwing. ---
  describe("2c: managed-root guard emits a wrap-immune ::error:: annotation", () => {
    let guardBody;

    beforeAll(() => {
      guardBody = extractFunctionBody(scriptText, "Confirm-UnityCliManagedInstallRoot");
    });

    test("the guard exists and still throws on an external/absent CLI root", () => {
      expect(guardBody).not.toBe("");
      // Throw semantics preserved (additive change only).
      expect(guardBody).toContain('throw "CI-managed Unity provisioning cannot mutate editors');
      expect(guardBody).toContain("outside the managed root");
    });

    test("each guard throw is preceded by a single-line ::error:: Write-Host with the same reason", () => {
      // Both failure branches (no CLI root; CLI root outside the managed root)
      // must emit a Write-Host "::error::..." annotation. Write-Host content is
      // not subject to ConciseView word-wrap, so it is a stable CI annotation.
      const errorAnnotations =
        guardBody.match(
          /Write-Host\s+"::error::CI-managed Unity provisioning cannot mutate editors/g
        ) || [];
      expect(errorAnnotations.length).toBeGreaterThanOrEqual(2);

      // The "outside the managed root" branch specifically must annotate before
      // it throws (annotation index < throw index for that phrase).
      const annotateIndex = guardBody.indexOf(
        'Write-Host "::error::CI-managed Unity provisioning cannot mutate editors because the Unity CLI install root is outside the managed root'
      );
      const throwIndex = guardBody.indexOf(
        'throw "CI-managed Unity provisioning cannot mutate editors because the Unity CLI install root is outside the managed root'
      );
      expect(annotateIndex).toBeGreaterThanOrEqual(0);
      expect(throwIndex).toBeGreaterThan(annotateIndex);
    });
  });

  // --- pwsh-AST cross-check: the by-name function bodies are real functions. ---
  // This guards the text-slice approach from being fooled by a same-named token
  // appearing in a string/comment elsewhere: the PowerShell parser confirms each
  // contract-relevant function is a genuine FunctionDefinitionAst whose own body
  // text carries the asserted content.
  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[ensure-editor-production-contract] pwsh not found; skipping AST cross-check (CI runners have pwsh)."
    );
    test.skip("pwsh AST confirms the contract-relevant functions and their bodies", () => {});
  } else {
    test("pwsh AST confirms the contract-relevant functions and their bodies", () => {
      const harness = [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        `$src = '${ENSURE_EDITOR.replace(/'/g, "''")}'`,
        "$tokens = $null; $errs = $null",
        "$ast = [System.Management.Automation.Language.Parser]::ParseFile($src, [ref]$tokens, [ref]$errs)",
        "if ($errs -and $errs.Count -gt 0) { Write-Error 'parse errors'; exit 3 }",
        "function Get-Fn([string]$name) {",
        "  $hits = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)",
        "  if (-not $hits -or $hits.Count -lt 1) { return $null }",
        "  return $hits[0].Extent.Text",
        "}",
        "$quarantine = Get-Fn 'Move-UnityInstallDirectoryToQuarantine'",
        "$guard = Get-Fn 'Confirm-UnityCliManagedInstallRoot'",
        "$ensure = Get-Fn 'Ensure-UnityCiModules'",
        "$helper = Get-Fn 'Get-UnityCliModuleInstallArguments'",
        "$ids = Get-Fn 'Get-UnityCiModuleIds'",
        "$spec = Get-Fn 'Get-UnityCiModuleSpec'",
        "$profile = Get-Fn 'Get-UnityProvisioningProfile'",
        "$assertProfile = Get-Fn 'Assert-UnityProvisioningProfile'",
        "$profileSpec = Get-Fn 'Get-UnityCiModuleSpecForProfile'",
        "if (-not $spec) { Write-Error 'no module-spec fn'; exit 10 }",
        "if (-not $profile) { Write-Error 'no provisioning-profile fn'; exit 11 }",
        "if (-not $assertProfile) { Write-Error 'no profile assert fn'; exit 12 }",
        "if (-not $profileSpec) { Write-Error 'no profile-spec fn'; exit 13 }",
        "if (-not $quarantine) { Write-Error 'no quarantine fn'; exit 4 }",
        "if (-not $guard) { Write-Error 'no guard fn'; exit 5 }",
        "if (-not $ensure) { Write-Error 'no ensure fn'; exit 6 }",
        "if (-not $helper) { Write-Error 'no module-install helper fn'; exit 8 }",
        "if (-not $ids) { Write-Error 'no module-ids fn'; exit 9 }",
        "$ok = $true",
        "if ($quarantine -notmatch 'Invoke-WithRetry') { Write-Host 'FAIL quarantine retry'; $ok = $false }",
        "if ($quarantine -notmatch 'Move-Item') { Write-Host 'FAIL quarantine move'; $ok = $false }",
        "if ($guard -notmatch '::error::') { Write-Host 'FAIL guard annotation'; $ok = $false }",
        // The install-modules call site routes through the single-source-of-truth
        // helper (it no longer hand-builds the EULA flag inline).
        "if ($ensure -notmatch 'Get-UnityCliModuleInstallArguments') { Write-Host 'FAIL ensure routes via helper'; $ok = $false }",
        // STRONGEST check: actually EXECUTE the extracted helper + ids fn and assert
        // the generated argument vectors carry --accept-eula for BOTH verbs (so the
        // contract enforces behavior, not just text), and that the requested ids do
        // NOT include the bare android-open-jdk id the beta CLI rejects.
        "Invoke-Expression $profile",
        "Invoke-Expression $assertProfile",
        "Invoke-Expression $spec",
        "Invoke-Expression $profileSpec",
        "Invoke-Expression $ids",
        "Invoke-Expression $helper",
        "$installArgs = @(Get-UnityCliModuleInstallArguments -Verb 'install' -Version '6000.0.32f1')",
        "$modArgs = @(Get-UnityCliModuleInstallArguments -Verb 'install-modules' -Version '6000.0.32f1')",
        "if ($installArgs -notcontains '--accept-eula') { Write-Host 'FAIL install verb eula'; $ok = $false }",
        "if ($modArgs -notcontains '--accept-eula') { Write-Host 'FAIL install-modules verb eula'; $ok = $false }",
        "if ($installArgs -notcontains '-m') { Write-Host 'FAIL install verb -m'; $ok = $false }",
        "if ($modArgs -notcontains '-m') { Write-Host 'FAIL install-modules verb -m'; $ok = $false }",
        "if ((Get-UnityCiModuleIds) -contains 'android-open-jdk') { Write-Host 'FAIL requested ids include android-open-jdk'; $ok = $false }",
        "if ($ok) { Write-Output 'CONTRACT-OK' } else { exit 7 }"
      ].join("\n");

      const run = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", harness], {
        encoding: "utf8",
        maxBuffer: 16 * 1024 * 1024
      });

      // Normalize before the phrase assertion: the harness can emit a thrown
      // (ConciseView-wrapped) error on the failure path, so route the output
      // through normalizePwshText for a width-independent assertion.
      const combined = normalizePwshText(`${run.stdout || ""}\n${run.stderr || ""}`);
      assertSpawnStatus(run, 0, expect.getState().currentTestName || "pwsh harness");
      expect(combined).toContain("CONTRACT-OK");
    });
  }
});
