/**
 * @fileoverview Contract tests for the Unity headless runner CLI surface.
 *
 * scripts/unity/run-tests.sh, run-tests.ps1, and activate-license.sh are the
 * canonical local entry points for the headless Unity workflow. They are
 * invoked from documentation, the .claude allowlist, the devcontainer test
 * workflow, and (transitively) the activate-license skill page.
 *
 * Renaming a flag or dropping the docker-outside-of-docker (DooD) path
 * translation contract would silently break those callers, so we lock the
 * surface here with text-grep assertions. We deliberately avoid invoking
 * `bash` / `pwsh` on these scripts: they would try to talk to Docker and
 * Unity and the grep approach is sub-millisecond.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const UNITY_SCRIPTS = path.join(REPO_ROOT, "scripts", "unity");

function readScript(relPath) {
  const abs = path.join(REPO_ROOT, relPath);
  expect(fs.existsSync(abs)).toBe(true);
  return fs.readFileSync(abs, "utf8");
}

/**
 * Extract the source text of a top-level `function <name> { ... }` by bounding it
 * at the next top-level `\nfunction ` definition. Mirrors the slicing used by the
 * production-contract and idempotency tests. Returns "" when not found.
 */
function extractFunctionBody(scriptText, functionName) {
  const start = scriptText.indexOf(`function ${functionName}`);
  if (start < 0) {
    return "";
  }
  const after = scriptText.indexOf("\nfunction ", start + 1);
  return after === -1 ? scriptText.slice(start) : scriptText.slice(start, after);
}

/** Collapse all whitespace runs to single spaces so token-order assertions are
 *  resilient to harmless reformatting (line breaks, re-indentation, alignment). */
function normalizeWhitespace(text) {
  return text.replace(/\s+/g, " ").trim();
}

function hasExecutableBit(absPath) {
  const relativePath = path.relative(REPO_ROOT, absPath).split(path.sep).join("/");
  const result = childProcess.spawnSync("git", ["ls-files", "--stage", "--", relativePath], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });

  expect(result.status).toBe(0);
  const [mode] = result.stdout.trim().split(/\s+/, 1);
  expect(mode).toBe("100755");
  return true;
}

describe("scripts/unity/run-tests.sh contract", () => {
  const shPath = path.join(UNITY_SCRIPTS, "run-tests.sh");
  let content;

  beforeAll(() => {
    content = readScript("scripts/unity/run-tests.sh");
  });

  test("file exists and is executable", () => {
    expect(fs.existsSync(shPath)).toBe(true);
    expect(hasExecutableBit(shPath)).toBe(true);
  });

  test.each([
    ["--platform"],
    ["--unity-version"],
    ["--filter"],
    ["--include-perf"],
    ["--include-integrations"],
    ["--include-comparisons"],
    ["--results"],
    ["--help"]
  ])("help text references %s", (flag) => {
    expect(content).toContain(flag);
  });

  test("references LOCAL_WORKSPACE_FOLDER (DooD path translation)", () => {
    expect(content).toContain("LOCAL_WORKSPACE_FOLDER");
  });

  test("references DXM_HOST_REPO_ROOT override (DooD path translation)", () => {
    expect(content).toContain("DXM_HOST_REPO_ROOT");
  });

  test("prefers docker-inspected devcontainer mount before LOCAL_WORKSPACE_FOLDER", () => {
    expect(content.indexOf("if is_container_runtime")).toBeGreaterThan(
      content.indexOf("DXM_HOST_REPO_ROOT")
    );
    expect(content.indexOf("detect_host_repo_root_from_container")).toBeLessThan(
      content.indexOf("Ignoring LOCAL_WORKSPACE_FOLDER=")
    );
  });

  test("license contract supports serial activation plus ULF / local base64 ULF fallback", () => {
    expect(content).toContain("UNITY_LICENSE");
    expect(content).toContain("UNITY_LICENSE_B64");
    expect(content).toContain("UNITY_SERIAL");
    expect(content).toContain("UNITY_EMAIL");
    expect(content).toContain("UNITY_PASSWORD");
    expect(content).not.toContain('-serial ""');
    expect(content).not.toContain("personal-email");
  });

  test("runs Unity tests with Release code optimization locally", () => {
    expect(content).toContain("-releaseCodeOptimization");
  });

  test("is serial-FIRST with the .ulf fallback retained", () => {
    // The repo removed the self-hosted Unity Licensing Server and switched to
    // classic SERIAL activation. The local script prefers UNITY_SERIAL +
    // UNITY_EMAIL + UNITY_PASSWORD (-> -serial -username -password), then falls
    // back to a .ulf in UNITY_LICENSE (raw) or UNITY_LICENSE_B64 (base64). The
    // floating-server surface must be GONE.
    expect(content).toContain("-serial");
    expect(content).toContain("-username");
    expect(content).toContain("-password");
    expect(content).not.toContain("UNITY_LICENSING_SERVER");
    expect(content).not.toContain("services-config.json");
  });

  test("returns the serial seat on EVERY exit via the EXIT trap (no leaked seat)", () => {
    // Serial activation consumes the single shared seat, so a local run MUST
    // return it on EVERY exit path. The return runs inside the same EXIT trap as
    // the chown cleanup (gated to serial mode) so it fires even when the editor
    // failed, and is best-effort (|| true) so it cannot mask the real exit code.
    expect(content).toContain("trap cleanup_ownership EXIT");
    expect(content).toMatch(/-returnlicense[\s\S]*?-username "\$\{UNITY_EMAIL\}"/);
    // The return is gated to the serial license mode (not run for the .ulf paths).
    expect(content).toMatch(/LICENSE_MODE.*==.*"serial"/);
  });

  test("auto-loads common local Unity license files before failing", () => {
    expect(content).toContain("UNITY_LICENSE_FILE");
    expect(content).toContain("ProgramData");
    expect(content).toContain("LOCALAPPDATA");
    expect(content).toContain(".local/share/unity3d/Unity/Unity_lic.ulf");
    expect(content).toContain("Library/Application Support/Unity/Unity_lic.ulf");
    expect(content).toContain('UNITY_LICENSE="$(cat "${license_path}")"');
  });

  test("forwards the perf commit environment into Unity containers", () => {
    expect(content).toContain("-e DX_PERF_COMMIT");
    expect(content).toContain("-e DX_PERF_BASELINE");
    expect(content).toContain("-e DX_PERF_BASELINE_MODE");
  });

  test("standalone runs natively via the single editor command path (no two-pass)", () => {
    // standalone now shares the same single inner command as editmode/playmode,
    // mapping -testPlatform to StandaloneLinux64 (Unity builds AND runs the
    // IL2CPP player in one pass; IL2CPP backend from ProjectSettings).
    expect(content).toContain('test_platform="StandaloneLinux64"');
    expect(content).toContain("-runTests -testPlatform ${test_platform}");
    expect(content).toContain("-assemblyNames");
    expect(content).toContain("-testFilter");
    // The target-aware assembly list is threaded in for standalone.
    expect(content).toContain("target: '${target}'");
    expect(content).toContain("runtimeOnly: ${runtime_only}");
    expect(content).toContain('"${PLATFORM}" "${RUNTIME_ONLY}"');
  });

  test("does NOT contain the deleted two-pass standalone build/run symbols", () => {
    expect(content).not.toContain("build_standalone_build_cmd_inner");
    expect(content).not.toContain("build_standalone_run_cmd_inner");
    expect(content).not.toContain("DXM_IL2CPP_BUILD_PATH");
    expect(content).not.toContain("BuildIL2CPPTestPlayer");
    expect(content).not.toContain("-buildTarget StandaloneLinux64");
    expect(content).not.toContain("-executeMethod");
  });

  test("normalizes relative --results paths under the repo before validation", () => {
    expect(content).toMatch(/RESULTS_PATH=.*REPO_ROOT/);
    expect(content).toContain("${RESULTS_PATH#./}");
    expect(content).toContain("RESULTS_DIR_REAL=");
    expect(content).toContain("REPO_ROOT_REAL=");
    expect(content).toContain('${RESULTS_PATH#"${REPO_ROOT_REAL}/"}');
  });

  test("quotes caller-controlled inner bash arguments", () => {
    expect(content).toContain("printf '%q'");
    expect(content).toContain("filter_q=");
    expect(content).toContain("results_q=");
    expect(content).toContain("assemblies_q=");
  });

  test("returns Unity Library cache ownership to the invoking user", () => {
    expect(content).toContain("trap cleanup_ownership EXIT");
    expect(content).toContain("UNITY_LIBRARY_CACHE_SOURCE=");
    expect(content).toContain("dxm-unity-library-%s-%s");
    expect(content).toContain('chown -R "${USER_UID}:${USER_GID}" /workspace/.artifacts || true');
    expect(content).toContain('baseline_path="${DX_PERF_BASELINE}"');
    expect(content).toContain('baseline_path="/workspace/${baseline_path}"');
    expect(content).toContain('chown "${USER_UID}:${USER_GID}" "${baseline_path}"');
    expect(content).toContain('baseline_dir="$(dirname "${baseline_path}")"');
    expect(content).toContain(
      'chown -R "${USER_UID}:${USER_GID}" /workspace/.unity-test-project/Library || true'
    );
    expect(content).toContain(
      '-v "${UNITY_LIBRARY_CACHE_SOURCE}:/workspace/.unity-test-project/Library"'
    );
    expect(content).not.toContain("dxm-unity-library-cache:/workspace/.unity-test-project/Library");
  });
});

describe("scripts/unity/run-tests.ps1 contract", () => {
  const ps1Path = path.join(UNITY_SCRIPTS, "run-tests.ps1");
  let content;

  beforeAll(() => {
    content = readScript("scripts/unity/run-tests.ps1");
  });

  test("file exists", () => {
    expect(fs.existsSync(ps1Path)).toBe(true);
  });

  test("ValidateSet pins editmode/playmode/standalone (whitespace-tolerant)", () => {
    // Allow optional whitespace between members so contributors can run
    // PowerShell formatters without invalidating the contract test.
    expect(content).toMatch(
      /\[ValidateSet\(\s*'editmode'\s*,\s*'playmode'\s*,\s*'standalone'\s*\)\]/
    );
  });

  test.each([
    ["Platform"],
    ["UnityVersion"],
    ["IncludePerf"],
    ["IncludeIntegrations"],
    ["IncludeComparisons"],
    ["Runner"]
  ])("declares parameter %s (as $Name and -Name)", (paramName) => {
    // PowerShell convention: `param([type]$Name)` declares the parameter,
    // callers pass it as `-Name`. We require BOTH forms: the variable
    // declaration AND at least one caller-side reference (or .PARAMETER
    // doc) so a typo in either half fails loudly.
    const variableForm = new RegExp(`\\$${paramName}\\b`);
    const callerForm = new RegExp(`-${paramName}\\b`);
    const docForm = new RegExp(`\\.PARAMETER\\s+${paramName}\\b`);
    expect(content).toMatch(variableForm);
    // Either a caller-side `-Name` reference (in examples / Write-Host
    // help text) or a `.PARAMETER Name` doc block satisfies the
    // discoverability half of the contract.
    expect(callerForm.test(content) || docForm.test(content)).toBe(true);
  });

  test("references LOCAL_WORKSPACE_FOLDER (DooD path translation)", () => {
    expect(content).toContain("LOCAL_WORKSPACE_FOLDER");
  });

  test("references DXM_HOST_REPO_ROOT override (DooD path translation)", () => {
    expect(content).toContain("DXM_HOST_REPO_ROOT");
  });

  test("prefers docker-inspected devcontainer mount before LOCAL_WORKSPACE_FOLDER", () => {
    expect(content.indexOf("$InContainer = Test-ContainerRuntime")).toBeGreaterThan(
      content.indexOf("$env:DXM_HOST_REPO_ROOT")
    );
    expect(content.indexOf("$HostRepoRoot = Get-InspectedHostRepoRoot")).toBeLessThan(
      content.indexOf("Ignoring LOCAL_WORKSPACE_FOLDER=")
    );
  });

  test("license contract supports serial activation plus ULF / local base64 ULF fallback", () => {
    expect(content).toContain("UNITY_LICENSE");
    expect(content).toContain("UNITY_LICENSE_B64");
    expect(content).toContain("UNITY_SERIAL");
    expect(content).toContain("UNITY_EMAIL");
    expect(content).toContain("UNITY_PASSWORD");
    expect(content).not.toContain('-serial ""');
    expect(content).not.toContain("personal-email");
  });

  test("runs Unity tests with Release code optimization locally", () => {
    expect(content).toContain("'-releaseCodeOptimization'");
    expect(content).toContain("  -releaseCodeOptimization \\");
  });

  test("is serial-FIRST with the .ulf fallback retained", () => {
    // Parity with run-tests.sh: classic SERIAL activation (UNITY_SERIAL +
    // UNITY_EMAIL + UNITY_PASSWORD -> -serial -username -password), with the
    // .ulf fallback (UNITY_LICENSE / UNITY_LICENSE_B64) retained for offline use.
    // The floating-server surface must be GONE.
    expect(content).toContain("-serial");
    expect(content).toContain("-username");
    expect(content).toContain("-password");
    expect(content).not.toContain("UNITY_LICENSING_SERVER");
    expect(content).not.toContain("services-config.json");
  });

  test("returns the serial seat on EVERY exit via the EXIT trap (no leaked seat)", () => {
    // Parity with run-tests.sh: the generated docker bash payload returns the
    // serial seat inside its EXIT trap so a local run never leaks the single
    // shared seat.
    expect(content).toContain("trap cleanup_ownership EXIT");
    expect(content).toMatch(/-returnlicense[\s\S]*?-username "\$\{UNITY_EMAIL\}"/);
  });

  test("auto-loads common local Unity license files before failing", () => {
    expect(content).toContain("UNITY_LICENSE_FILE");
    expect(content).toContain("ProgramData");
    expect(content).toContain("LOCALAPPDATA");
    expect(content).toContain(".local/share/unity3d/Unity/Unity_lic.ulf");
    expect(content).toContain("Library/Application Support/Unity/Unity_lic.ulf");
    expect(content).toContain("Get-Content -LiteralPath $licensePath -Raw");
  });

  test("normalizes generated bash payloads to LF for docker bash", () => {
    expect(content).toContain("function ConvertTo-BashScriptText");
    expect(content).toContain('return $Value.Replace("`r`n", "`n")');
    expect(content).toContain("return ConvertTo-BashScriptText $sb.ToString()");
  });

  test("supports local Windows Unity execution for editmode and playmode", () => {
    expect(content).toMatch(/\[ValidateSet\(\s*'auto'\s*,\s*'docker'\s*,\s*'local'\s*\)\]/);
    expect(content).toContain("function Find-UnityEditorPath");
    expect(content).toContain("UNITY_EDITOR_PATH");
    expect(content).toContain("UNITY_PATH");
    expect(content).toContain("Unity/Hub/Editor/$Version/Editor/Unity.exe");
    expect(content).toContain("function Invoke-LocalUnityTests");
    expect(content).toContain("Launching local Unity");
    expect(content).toContain("$IsWindows -and $Platform -ne 'standalone'");
    expect(content).toContain("$ResolvedRunner = 'local'");
    expect(content).toContain("-Runner local does not support standalone");
  });

  test("forwards the perf commit environment into Unity containers", () => {
    expect(content).toContain("'-e', 'DX_PERF_COMMIT'");
    expect(content).toContain("'-e', 'DX_PERF_BASELINE'");
    expect(content).toContain("'-e', 'DX_PERF_BASELINE_MODE'");
  });

  test("standalone runs natively via the single editor command path (no two-pass)", () => {
    // Parity with run-tests.sh: standalone shares Get-EditorCommandInner and
    // maps -testPlatform to StandaloneLinux64 (single build+run pass; IL2CPP
    // backend from ProjectSettings).
    expect(content).toContain("StandaloneLinux64");
    expect(content).toContain("-assemblyNames");
    expect(content).toContain("-testFilter");
    // The target-aware assembly list is threaded in for standalone.
    expect(content).toContain("target: '$Target'");
    expect(content).toContain("runtimeOnly: $runtimeOnlyBool");
    expect(content).toContain("-Target $Platform");
    expect(content).toContain("-RuntimeOnlyFlag:($Platform -eq 'standalone')");
  });

  test("does NOT contain the deleted two-pass standalone build/run symbols", () => {
    expect(content).not.toContain("Get-StandaloneBuildCommandInner");
    expect(content).not.toContain("Get-StandaloneRunCommandInner");
    expect(content).not.toContain("DXM_IL2CPP_BUILD_PATH");
    expect(content).not.toContain("BuildIL2CPPTestPlayer");
    expect(content).not.toContain("-buildTarget StandaloneLinux64");
    expect(content).not.toContain("-executeMethod");
  });

  test("uses boundary-aware Results path validation", () => {
    expect(content).toContain("$RepoRootReal");
    expect(content).toContain("$ResultsDirReal");
    expect(content).toContain("$ResultsRel -eq $RepoRootReal");
    expect(content).toContain('$ResultsRel.StartsWith("$RepoRootReal/")');
    expect(content).toContain('$ResultsRel.StartsWith("$RepoRootReal\\")');
    expect(content).toContain("$Results = Join-Path $RepoRoot $Results");
    expect(content).not.toContain("TrimStart('.', '/', '\\')");
  });

  test("quotes caller-controlled inner bash arguments", () => {
    expect(content).toContain("ConvertTo-BashSingleQuotedString");
    expect(content).toContain("$filterQ");
    expect(content).toContain("$resultsQ");
    expect(content).toContain("$assembliesQ");
  });

  test("returns Unity Library cache ownership to the invoking user", () => {
    expect(content).toContain("trap cleanup_ownership EXIT");
    expect(content).toContain("$UnityLibraryCacheSource");
    expect(content).toContain("dxm-unity-library-$ImageTag-$Platform");
    expect(content).toContain('chown -R "${USER_UID}:${USER_GID}" /workspace/.artifacts || true');
    expect(content).toContain('baseline_path="${DX_PERF_BASELINE}"');
    expect(content).toContain('baseline_path="/workspace/${baseline_path}"');
    expect(content).toContain('chown "${USER_UID}:${USER_GID}" "${baseline_path}"');
    expect(content).toContain('baseline_dir="$(dirname "${baseline_path}")"');
    expect(content).toContain(
      'chown -R "${USER_UID}:${USER_GID}" /workspace/.unity-test-project/Library || true'
    );
    expect(content).toContain('"$UnityLibraryCacheSource`:/workspace/.unity-test-project/Library"');
    expect(content).not.toContain("dxm-unity-library-cache:/workspace/.unity-test-project/Library");
  });
});

describe("scripts/unity direct CI runner contract", () => {
  const ensureEditor = readScript("scripts/unity/ensure-editor.ps1");
  const runCi = readScript("scripts/unity/run-ci-tests.ps1");

  test("ensure-editor installs through the standalone Unity CLI and resolves Unity.exe", () => {
    expect(ensureEditor).toContain("UnityVersion");
    expect(ensureEditor).toContain("Ensure-UnityCli");
    expect(ensureEditor).toContain("Set-UnityCliInstallPath");
    // NOTE: a bare `toContain("install-path")` was intentionally removed -- it
    // matched both the old positional bug shape and the new getter/`-s` form, so
    // it was tautological. The form-specific `'install-path', '-s'` SET and
    // `@('install-path')` getter assertions below cover the surface precisely.
    // The primary install builds its arg vector through the single source-of-truth
    // helper (which injects the mandatory --accept-eula), not a hand-built `-m`
    // vector that previously omitted the flag and broke every CI cell. The base
    // install now requests the selected provisioning profile atomically, so
    // Android dependencies are only resolved when the selected profile needs
    // Android.
    // argument. Asserted whitespace-tolerantly (token intent, not an exact line) so
    // a harmless reformat never breaks this contract; the deeper sole-producer
    // invariant is pinned by the production-contract AST test.
    expect(ensureEditor).toMatch(
      /\$installArgs\s*=\s*@\(\s*Get-UnityCliModuleInstallArguments\s+-Verb\s+'install'\s+-Version\s+\$UnityVersion\s+-ModuleIds\s+\(Get-UnityCiModuleIds -Profile \$ProvisioningProfile\)\s*\)/
    );
    expect(ensureEditor).toContain("ProvisioningProfile");
    expect(ensureEditor).toContain("StandaloneWindowsIl2Cpp");
    expect(ensureEditor).toContain("EditorOnly");
    expect(ensureEditor).toContain("install-modules");
    expect(ensureEditor).toContain("windows-il2cpp");
    expect(ensureEditor).toContain("Unity.exe");
    // Regression guard for the Unity CLI PATH fix: the installer updates only
    // the User-scope registry PATH, so the script must refresh the session
    // PATH from the registry and resolve the CLI via $script:UnityCliPath
    // (absolute-path fallback) instead of a bare `unity` on PATH.
    expect(ensureEditor).toContain("$script:UnityCliPath");
    expect(ensureEditor).toContain("GetEnvironmentVariable");
    // Structural guards (beyond token tripwires): the registry-refresh helper
    // must exist, the post-install loop must sleep AND re-probe `unity` after a
    // refresh, and an absolute-path fallback must resolve the installer's known
    // %LOCALAPPDATA%\Unity\bin\unity.exe target when the CLI never lands on PATH.
    expect(ensureEditor).toContain("Update-SessionPathFromRegistry");
    expect(ensureEditor).toContain("Start-Sleep");
    // Post-install retry: a `Get-Command unity` must appear AFTER the standalone
    // installer download so the script re-probes once the registry/PATH lands.
    const installIndex = ensureEditor.indexOf("install.ps1");
    expect(installIndex).toBeGreaterThan(-1);
    expect(ensureEditor.indexOf("Get-Command unity", installIndex)).toBeGreaterThan(installIndex);
    // Absolute-path fallback literal.
    expect(ensureEditor).toMatch(/Unity\\bin\\unity\.exe/);
  });

  test("ensure-editor is resilient to the unstable standalone Unity CLI surface", () => {
    // ----------------------------------------------------------------------
    // Regression guards for the "wrong argument counts" CI failure. The
    // standalone Unity CLI v0.1.0-beta.x is a moving surface: `install-path`
    // with NO args is a GETTER, and the SET flag is undocumented. The script
    // must (a) never pass a positional dir to install-path, (b) set the path
    // best-effort via -s, (c) resolve the install root from the getter, (d)
    // discover editors defensively, and (e) enforce profile-scoped provisioning.
    // ----------------------------------------------------------------------

    // (a) install-path SET must use the -s flag, NOT a positional directory.
    // The original bug was `Invoke-UnityCli -Arguments @('install-path', $Root)`
    // (positional dir). That exact shape must never reappear in ANY form.
    expect(ensureEditor).toContain("'install-path', '-s'");
    expect(ensureEditor).not.toMatch(/'install-path',\s*\$Root\b/);
    expect(ensureEditor).not.toMatch(/'install-path',\s*\$InstallRoot\b/);
    // Catch the un-quoted positional shapes too: `install-path $Root` and
    // `'install-path' $Root` (no comma) -- either would re-introduce the
    // "Expected 0 arguments but got 1" failure the getter design eliminated.
    expect(ensureEditor).not.toMatch(/'?install-path'?\s+\$Root\b/);
    expect(ensureEditor).not.toMatch(/'?install-path'?\s+\$InstallRoot\b/);
    // A SET fallback exists: try -s first, then --set, before giving up.
    expect(ensureEditor).toContain("'install-path', '--set'");
    // The throwing invoker must NOT be used for setting the install path
    // (set is an optimization, not a hard requirement). The \s after
    // Invoke-UnityCli excludes the legitimate Invoke-UnityCliSafe calls.
    expect(ensureEditor).not.toMatch(/Invoke-UnityCli\s+-Arguments\s+@\('install-path'/);

    // (b) A non-throwing best-effort invoker exists and is distinct from the
    // throwing Invoke-UnityCli, so optional CLI ops cannot abort the bootstrap.
    // Both the best-effort invoker AND the capturing getter invoker must exist;
    // the design depends on getter output staying OFF the success stream that
    // run-ci-tests.ps1 reads via `Select-Object -Last 1`.
    expect(ensureEditor).toContain("function Invoke-UnityCliSafe");
    expect(ensureEditor).toContain("function Get-UnityCliOutput");
    // Invoke-UnityCliSafe is non-throwing: it returns a boolean and delegates to
    // the timeout-capable process runner so optional probes cannot hang forever.
    expect(ensureEditor).toMatch(/function Invoke-UnityCliSafe[\s\S]*?return \(\$exit -eq 0\)/);
    expect(ensureEditor).toMatch(
      /function Invoke-UnityCliSafe[\s\S]*?Get-EnsureEditorProbeTimeoutSeconds[\s\S]*?Get-EffectiveUnityCliTimeoutSeconds -RequestedSeconds \$requestedTimeout[\s\S]*?Invoke-UnityCliCaptureWithTimeout -Arguments \$Arguments -TimeoutSeconds \$effectiveTimeout/
    );
    expect(ensureEditor).toContain("-TimeoutKnob 'DXM_ENSURE_EDITOR_PROBE_TIMEOUT_SECONDS'");
    // Set-UnityCliInstallPath routes through the best-effort invoker. Constrain
    // the gap with `[^}]*?` (no brace, so the match cannot cross out of the
    // function body) so a stray `Invoke-UnityCliSafe ... '-s' ...` in an unrelated
    // later function could not satisfy this assertion.
    expect(ensureEditor).toMatch(
      /Set-UnityCliInstallPath[^}]*?Invoke-UnityCliSafe -Arguments @\('install-path', '-s', \$Root\)/
    );
    // Failure to set the path emits a notice (not an error) and continues. Pin
    // the ACTUAL emit (the Write-CiNotice fallback line), not a bare `::notice::`
    // token: the old loose form was satisfied by the function's own comment, so
    // it stayed green even if the real notice-on-failure line were deleted.
    expect(ensureEditor).toMatch(
      /Set-UnityCliInstallPath[\s\S]*?Write-CiNotice "Could not set the Unity CLI install path/
    );

    // (c) Getter-based authoritative resolver: runs `unity install-path` with
    // NO extra args via the CAPTURING (non-throwing) invoker and uses path-like
    // output as the root. It must route through Get-UnityCliOutput, never the
    // throwing/echoing invokers, so getter chatter cannot leak to the success
    // stream or abort the bootstrap on a non-zero beta exit.
    expect(ensureEditor).toContain("function Get-UnityCliInstallRoot");
    expect(ensureEditor).toContain("Get-UnityCliOutput -Arguments @('install-path')");
    expect(ensureEditor).not.toMatch(/Invoke-UnityCli(Safe)?\s+-Arguments\s+@\('install-path'\)/);
    expect(ensureEditor).toMatch(
      /function Get-UnityCliOutput[\s\S]*?Get-EnsureEditorProbeTimeoutSeconds[\s\S]*?Get-EffectiveUnityCliTimeoutSeconds -RequestedSeconds \$requestedTimeout[\s\S]*?Invoke-UnityCliCaptureWithTimeout -Arguments \$Arguments -TimeoutSeconds \$effectiveTimeout/
    );
    expect(ensureEditor).toContain("Test-LooksLikeAbsolutePath");
    // The path-like guard accepts a Windows drive-letter or UNC path only, so a
    // beta banner line cannot be mistaken for the install root.
    expect(ensureEditor).toMatch(/function Test-LooksLikeAbsolutePath/);
    expect(ensureEditor).toMatch(/\^\[A-Za-z\]:\[\\\\\/\]/);

    // (d) Defensive JSON discovery: editors -i --format json read through the
    // capturing invoker and parsed inside a try/catch with ConvertFrom-Json so
    // malformed beta output returns $null (continues) instead of throwing.
    expect(ensureEditor).toContain("'editors', '-i', '--format', 'json'");
    expect(ensureEditor).toMatch(
      /Get-UnityCliOutput -Arguments @\('editors', '-i', '--format', 'json'\)/
    );
    expect(ensureEditor).toContain("ConvertFrom-Json");
    // The ConvertFrom-Json call sits in a try whose catch returns (does not
    // re-throw), proving malformed JSON is non-fatal. Constrain the catch-body
    // gap with `[^}]*?` so the `return` must live INSIDE the catch block (cannot
    // be satisfied by an empty catch followed by a `return` in a later function).
    expect(ensureEditor).toMatch(
      /try\s*\{[^}]*ConvertFrom-Json[\s\S]*?\}\s*catch\s*\{[^}]*?return/
    );

    // (e) CI module desired state: each provisioning profile is mapped to the
    // exact modules that workflow mode needs. Module repair must classify against
    // disk and reinstall a managed editor when module installation cannot modify it.
    //
    // SINGLE SOURCE OF TRUTH: the script keeps ONE spec (Get-UnityCiModuleSpec) from
    // which the REQUESTED ids passed to `-m` (Get-UnityCiModuleIds, which omits the
    // version-pinned 'android-open-jdk'), the VERIFIED-on-disk groups
    // (Get-UnityCiVerifiedModuleGroups, which INCLUDES 'android-open-jdk' because it
    // lands as an android-sdk-ndk-tools dependency), and the tier membership all
    // DERIVE. We assert this list DERIVED FROM the spec body (not a hardcoded copy)
    // so the contract cannot drift from the script; the requested-vs-verified +
    // tier split is pinned precisely by unity-ensure-editor-production-contract.test.js.
    expect(ensureEditor).toContain("function Get-UnityCiModuleSpec");
    expect(ensureEditor).toContain("function Get-UnityCiModuleIds");
    expect(ensureEditor).toContain("function Get-UnityCiVerifiedModuleGroups");
    // Derive the module ids from the spec function body's `Id = '<id>'` rows rather
    // than hardcoding them here, so a spec change is automatically reflected.
    const specBody = extractFunctionBody(ensureEditor, "Get-UnityCiModuleSpec");
    expect(specBody).not.toBe("");
    const specModuleIds = [...specBody.matchAll(/Id\s*=\s*'([^']+)'/g)].map((m) => m[1]);
    // The spec must cover the full expected CI module surface (sanity on the count
    // and the load-bearing ids) so a spec that silently drops a group is caught.
    expect(specModuleIds).toEqual(
      expect.arrayContaining([
        "windows-il2cpp",
        "webgl",
        "android",
        "android-sdk-ndk-tools",
        "android-open-jdk",
        "linux-mono",
        "linux-il2cpp"
      ])
    );
    for (const moduleId of specModuleIds) {
      expect(ensureEditor).toContain(`'${moduleId}'`);
    }
    expect(ensureEditor).toContain("function Ensure-UnityCiModules");
    expect(ensureEditor).toContain("function Repair-UnityEditorWithCiModules");
    expect(ensureEditor).toContain("function Move-UnityEditorInstallToQuarantine");
    expect(ensureEditor).toContain("function Move-UnityVersionInstallToQuarantine");
    expect(ensureEditor).toContain("function Invoke-UnityVersionUninstallForRepair");
    expect(ensureEditor).toContain("function Ensure-UnityNativeStartupHealthy");
    expect(ensureEditor).toContain("function Test-UnityNativeStartup");
    expect(ensureEditor).toContain("function Invoke-WithUnityInstallLock");
    expect(ensureEditor).toContain("function Confirm-UnityCliManagedInstallRoot");
    expect(ensureEditor).toContain("cannot mutate editors");
    // Module install verifies the ids via the -l listing before installing.
    expect(ensureEditor).toContain("'install-modules', '-e', $Version, '-l'");
    // The module INSTALL passes --accept-eula: the standalone CLI aborts with
    // "One or more modules require license acceptance. Pass --accept-eula ..."
    // for the Android SDK/NDK/OpenJDK modules otherwise. The flag is injected by
    // the single source-of-truth helper Get-UnityCliModuleInstallArguments (for
    // BOTH the `install` and `install-modules` verbs), never the -l listing call.
    //
    // INTENT-BASED contract (deliberately NOT an exact-literal pin): the helper's
    // `install-modules` return shape now lives in only one place, so pinning the
    // exact literal `@('install-modules', '-e', $Version, '--accept-eula', '-m') +
    // $moduleIds` would break on any harmless reformat. Instead we extract the
    // helper body, normalize whitespace, and require the tokens that ACTUALLY
    // matter to be present (verb + `-e $Version` + `--accept-eula` + `-m`), in any
    // formatting. The pwsh-AST production-contract test additionally EXECUTES the
    // helper and asserts the generated vector contents.
    {
      const helperBody = normalizeWhitespace(
        extractFunctionBody(ensureEditor, "Get-UnityCliModuleInstallArguments")
      );
      expect(helperBody).not.toBe("");
      // install-modules verb branch: targets an existing editor (`-e $Version`),
      // carries the mandatory EULA flag, and requests modules with `-m`.
      expect(helperBody).toContain("'install-modules'");
      expect(helperBody).toContain("'-e', $Version");
      expect(helperBody).toContain("'--accept-eula'");
      expect(helperBody).toContain("'--childModules'");
      expect(helperBody).toContain("'-m'");
      // install verb branch: positional version (no `-e`) plus the same EULA flag.
      expect(helperBody).toContain("'install', $Version");
      // The helper is the only place that owns these install-arg literals.
      expect(helperBody).toMatch(/ValidateSet\('install', 'install-modules'\)/);
    }
    // The -l listing is read non-throwing (best-effort verification). The module
    // install now runs through the CAPTURING (non-throwing) invoker so its exit
    // code AND output can be classified against the on-disk module layout: the
    // standalone beta CLI returns "No modules found to install." (exit 6) when
    // IL2CPP is ALREADY present, which is an idempotent no-op rather than a
    // failure. A genuine, non-benign failure with no on-disk corroboration STILL
    // throws (case 4 below), preserving the "real failure fails loudly" guarantee.
    expect(ensureEditor).toMatch(
      /Get-UnityCliOutput -Arguments @\('install-modules', '-e', \$Version, '-l'\)/
    );
    // The module-add call site routes the (EULA-bearing) arg vector through the
    // single source-of-truth helper Get-UnityCliModuleInstallArguments rather than
    // hand-building it, so the flag cannot drift between this and the `install`
    // call sites. The helper itself owns the literal `'--accept-eula', '-m'` shape.
    // The install-modules vector is captured ONCE into a variable and reused for
    // both the install call and the failure-annotation arg echo (no duplicate
    // helper call). The CORE-tier module-add scopes the vector via -ModuleIds
    // (Get-UnityCiModuleIdsForTier -Tier 'core' -Profile $Profile) for existing
    // editors; fresh and full-repair installs request Get-UnityCiModuleIds for
    // the selected profile. The dedicated Android-tier step
    // (Install-UnityAndroidModules) scopes its own via -ModuleIds $androidIds. Assert
    // the helper-routed assignment(s) + the variable-routed capturing invoke rather
    // than an inline-call regex that a refactor would break.
    expect(ensureEditor).toMatch(
      /\$installArgs = @\(Get-UnityCliModuleInstallArguments -Verb 'install-modules' -Version \$Version -ModuleIds \(Get-UnityCiModuleIdsForTier -Tier 'core' -Profile \$Profile\)\)/
    );
    // The dedicated Android tier step routes the android-scoped vector through the
    // same sole producer.
    expect(ensureEditor).toMatch(
      /\$installArgs = @\(Get-UnityCliModuleInstallArguments -Verb 'install-modules' -Version \$Version -ModuleIds \$androidIds\)/
    );
    expect(ensureEditor).toMatch(/Invoke-UnityCliCapture -Arguments \$installArgs/);
    expect(ensureEditor).toContain("function Get-UnityCliModuleInstallArguments");
    // Both `install` call sites (primary + repair) also route through the helper.
    expect(ensureEditor).toMatch(
      /Get-UnityCliModuleInstallArguments -Verb 'install' -Version \$UnityVersion/
    );
    expect(ensureEditor).toMatch(
      /Get-UnityCliModuleInstallArguments -Verb 'install' -Version \$Version/
    );
    expect(ensureEditor).toContain("Get-EffectiveUnityCliTimeoutSeconds");
    // The OLD unconditional throwing install of the module must NOT reappear: it
    // wrongly aborted standalone on the idempotent "No modules found to install"
    // no-op. (Invoke-UnityCli is the throwing invoker; Invoke-UnityCliCapture is
    // its capturing, classify-then-decide replacement for the module install.)
    expect(ensureEditor).not.toMatch(
      /Invoke-UnityCli -Arguments @\('install-modules', '-e', \$Version, '-m'/
    );
    // Idempotency classification surface: a disk-authoritative IL2CPP probe gates
    // the "treat the CLI no-op as success" path, and a benign "nothing to
    // install / already installed" message is recognized. Both must be present so
    // the idempotent contract cannot silently regress to a hard failure.
    expect(ensureEditor).toContain("function Test-Il2CppModulePresent");
    expect(ensureEditor).toContain("function Test-AnyUnityLeafPresent");
    expect(ensureEditor).toContain("function Get-MissingUnityCiModuleGroups");
    expect(ensureEditor).toMatch(/Test-UnityCiModuleGroupPresent -EditorPath/);
    expect(ensureEditor).toContain("'android-sdk-ndk-tools'");
    expect(ensureEditor).toContain("'android-open-jdk'");
    expect(ensureEditor).toContain("'linux-il2cpp'");
    expect(ensureEditor).toContain("'SDK'");
    expect(ensureEditor).toContain("'NDK'");
    expect(ensureEditor).toContain("'platform-tools\\adb.exe'");
    expect(ensureEditor).toContain("'source.properties'");
    expect(ensureEditor).toContain("'OpenJDK\\bin\\java.exe'");
    expect(ensureEditor).toContain("'BuildTools\\Emscripten\\emscripten\\emcc.py'");
    expect(ensureEditor).toContain("'BuildTools\\Emscripten\\emscripten\\emscripten-version.txt'");
    expect(ensureEditor).toContain("'UnityEditor.Android.Extensions.dll'");
    expect(ensureEditor).toContain("'UnityEditor.WebGL.Extensions.dll'");
    expect(ensureEditor).toContain("return $hasEditorExtension -and $hasEmscriptenToolchain");
    expect(ensureEditor).toContain("'linux64_player_development_mono\\LinuxPlayer'");
    expect(ensureEditor).toContain("'linux64_player_development_il2cpp\\LinuxPlayer'");
    expect(ensureEditor).toMatch(/LinuxStandaloneSupport[\s\S]*?Variations[\s\S]*?il2cpp/);
    expect(ensureEditor).toContain("DXM_UNITY_DISABLE_EDITOR_REPAIR");
    expect(ensureEditor).toMatch(/Quarantining unmanaged or partial Unity/);
    // The base editor install is RETRIED (it has failed flakily after a long run
    // with exit 6, AND has HUNG until the job is cancelled) and routed through the
    // capturing invoker so a final failure throws WITH the CLI output tail + exit
    // code for diagnosis. The attempt count is now sourced from the override-aware
    // helper (default 2, unchanged), and each attempt is bounded by the install
    // timeout because Invoke-UnityCliCapture delegates to the timeout runner.
    expect(ensureEditor).toContain("function Invoke-WithRetry");
    expect(ensureEditor).toContain("DXM_ENSURE_EDITOR_RETRY_DELAY_SECONDS");
    expect(ensureEditor).toContain("function Get-EnsureEditorInstallRetryAttempts");
    expect(ensureEditor).toContain("DXM_ENSURE_EDITOR_INSTALL_RETRY_ATTEMPTS");
    expect(ensureEditor).toMatch(
      /Invoke-WithRetry -MaxAttempts \$installRetryAttempts -DelaySeconds \$retryDelaySeconds -Action \{\s*\$installResult = Invoke-UnityCliCapture -Arguments \$installArgs/
    );
    // The install retry-attempts knob defaults to 2 (preserving the prior count).
    expect(ensureEditor).toMatch(
      /function Get-EnsureEditorInstallRetryAttempts[\s\S]*?param\(\[int\]\$Default = 2\)/
    );
    // The DEDICATED Android-tier install-retry knob defaults to 3 (one more than the
    // base-install default of 2): the Android NDK unpack is the specific flake this
    // editor-preserving loop targets, so an extra bounded attempt is cheap.
    expect(ensureEditor).toContain("function Get-EnsureEditorAndroidInstallRetryAttempts");
    expect(ensureEditor).toContain("DXM_ENSURE_EDITOR_ANDROID_INSTALL_RETRY_ATTEMPTS");
    expect(ensureEditor).toMatch(
      /function Get-EnsureEditorAndroidInstallRetryAttempts[\s\S]*?param\(\[int\]\$Default = 3\)/
    );

    // RESILIENCE: a total wall-clock TIMEOUT bounds every captured CLI invocation
    // so a hung module install (the Android NDK hang) is tree-killed and
    // classified as a retryable failure instead of running until the GitHub job is
    // cancelled. Invoke-UnityCliCapture DELEGATES to Invoke-UnityCliCaptureWithTimeout
    // (so the module-install call sites are bounded without changing the arg
    // vector), the timeout runner tree-kills (the bool Kill overload) and uses a
    // non-zero sentinel exit, and the limit comes from the override-aware helper.
    expect(ensureEditor).toContain("function Invoke-UnityCliCaptureWithTimeout");
    expect(ensureEditor).toContain("function Get-EnsureEditorInstallTimeoutSeconds");
    expect(ensureEditor).toContain("DXM_ENSURE_EDITOR_INSTALL_TIMEOUT_SECONDS");
    // The capturing invoker delegates to the timeout runner.
    expect(ensureEditor).toMatch(
      /function Invoke-UnityCliCapture\b[\s\S]*?Get-EnsureEditorInstallTimeoutSeconds[\s\S]*?Get-EffectiveUnityCliTimeoutSeconds -RequestedSeconds \$requestedTimeout[\s\S]*?Invoke-UnityCliCaptureWithTimeout -Arguments \$Arguments -TimeoutSeconds \$effectiveTimeout/
    );
    // The timeout runner tree-kills the whole process tree on a hang.
    expect(ensureEditor).toMatch(
      /function Invoke-UnityCliCaptureWithTimeout[\s\S]*?\$proc\.Kill\(\$true\)/
    );
    expect(ensureEditor).toContain("function ConvertTo-ProcessArgumentLine");
    expect(ensureEditor).not.toContain(".ArgumentList");
    expect(ensureEditor).not.toMatch(/WaitForExit\(\)/);
    // DIAGNOSTICS: the failure tail is de-duplicated (collapses identical lines),
    // and a wrap-immune ::error:: summary names the last progress msg + disk space.
    expect(ensureEditor).toContain("function Get-CollapsedCliOutputTail");
    expect(ensureEditor).toContain("function Write-ModuleInstallFailureDiagnostics");
    expect(ensureEditor).toMatch(/Get-CollapsedCliOutputTail -Output/);
    // The recovered-editor branch returns the resolved editor to the outer flow;
    // module verification runs after the install retry wrapper, so an Android-only
    // failure cannot multiply by the outer install retry count.
    expect(ensureEditor).toMatch(
      /Write-CiNotice "Verifying required CI modules after recovered editor install\."\s*return \$resolvedAfterFailure/
    );
    expect(ensureEditor).toMatch(
      /\$editor = Ensure-UnityCiModules -Version \$UnityVersion -EditorPath \$editor -InstallRoot \$InstallRoot -Profile \$ProvisioningProfile -ManagedOnly:\$CiManagedOnly/
    );

    // Layered discovery wiring: install branch resolves through the layered
    // resolver, not the bare candidate search alone. Resolve-InstalledEditor
    // must consult the getter root, the candidate search, AND the JSON parse.
    expect(ensureEditor).toContain("Resolve-InstalledEditor");
    expect(ensureEditor).toMatch(
      /function Resolve-InstalledEditor[\s\S]*?Get-UnityCliInstallRoot[\s\S]*?Find-UnityEditor[\s\S]*?Resolve-EditorFromCliJson/
    );
    // Pin the MAIN-FLOW call site (not just the function definition): the install
    // branch must actually invoke the layered resolver with the requested version
    // and configured root. A definition that nothing calls would be dead code.
    expect(ensureEditor).toMatch(
      /\$editor = Resolve-InstalledEditor -Version \$UnityVersion -Root \$InstallRoot/
    );
    // Pin the module call site: existing editors must be pushed through the same
    // desired-state module repair function with the requested version/root.
    expect(ensureEditor).toMatch(
      /\$editor = Ensure-UnityCiModules -Version \$UnityVersion -EditorPath \$editor -InstallRoot \$InstallRoot -Profile \$ProvisioningProfile -ManagedOnly:\$CiManagedOnly/
    );
    expect(ensureEditor).toMatch(
      /\$editor = Ensure-UnityNativeStartupHealthy -Version \$UnityVersion -EditorPath \$editor -InstallRoot \$InstallRoot -Profile \$ProvisioningProfile -ManagedOnly:\$CiManagedOnly/
    );
    expect(ensureEditor).toMatch(
      /Install-UnityEditorWithCiModules[\s\S]*?Resolve-InstalledEditor -Version \$Version -Root \$InstallRoot -ManagedOnly:\$ManagedOnly/
    );
    expect(ensureEditor).toMatch(
      /if \(\$ManagedOnly\) \{\s*Confirm-UnityCliManagedInstallRoot -Root \$InstallRoot \| Out-Null\s*\}/
    );
    expect(ensureEditor).toMatch(
      /Repair-UnityEditorWithCiModules[\s\S]*?Install-UnityEditorWithCiModules -Version \$Version -InstallRoot \$InstallRoot -Reason \$Reason -Profile \$Profile -ManagedOnly:\$ManagedOnly/
    );
    expect(ensureEditor).toMatch(
      /Repair-UnityEditorWithCiModules[\s\S]*?Move-UnityVersionInstallToQuarantine -Version \$Version -InstallRoot \$InstallRoot/
    );
    expect(ensureEditor).toMatch(
      /function Install-UnityEditorWithCiModules[\s\S]*?for \(\$attempt = 1; \$attempt -le 2; \$attempt\+\+\)[\s\S]*?Invoke-UnityVersionUninstallForRepair -Version \$Version[\s\S]*?Retrying Unity \$Version repair install/
    );
    expect(ensureEditor).toMatch(
      /already installed[\s\S]*?Invoke-UnityVersionUninstallForRepair -Version \$UnityVersion[\s\S]*?Move-UnityVersionInstallToQuarantine -Version \$UnityVersion -InstallRoot \$InstallRoot/
    );
    expect(ensureEditor).toMatch(
      /if \(\$CiManagedOnly\) \{\s*Confirm-UnityCliManagedInstallRoot -Root \$InstallRoot \| Out-Null\s*\}/
    );
    expect(ensureEditor).toMatch(
      /Find-UnityEditor -Version \$UnityVersion -Root \$InstallRoot -IncludeHostInstalls:\(-not \$CiManagedOnly\)/
    );
    expect(ensureEditor).toMatch(
      /Resolve-InstalledEditor -Version \$UnityVersion -Root \$InstallRoot -ManagedOnly:\$CiManagedOnly/
    );
  });

  test("run-ci-tests exposes the required CI surface", () => {
    for (const token of [
      "UnityVersion",
      "TestMode",
      "AssemblyNames",
      "ArtifactsPath",
      "ProjectPath",
      "TestCategory",
      "GenerateOnly"
    ]) {
      expect(runCi).toContain(token);
    }
    expect(runCi).toMatch(
      /\[ValidateSet\(\s*'editmode'\s*,\s*'playmode'\s*,\s*'standalone'\s*\)\]/
    );
    expect(runCi).toContain("function Invoke-UnityNativeStartupProbe");
    expect(runCi).toContain("after the pre-lock healthy-existing editor check");
    expect(runCi).toContain("scripts/unity/maintain-windows-runner.ps1");
    expect(runCi).toContain("$provisioningProfile = if ($TestMode -eq 'standalone')");
    expect(runCi).toContain("'StandaloneWindowsIl2Cpp'");
    expect(runCi).toContain("'EditorOnly'");
    expect(runCi).toContain("ProvisioningProfile = $provisioningProfile");
    expect(runCi).toContain("$ensureArgs.RequireHealthyExisting = $true");
    expect(runCi).not.toContain("WithWindowsIl2Cpp = $true");
  });

  test("run-ci-tests creates an ephemeral package host project", () => {
    expect(runCi).toContain("Initialize-EphemeralProject");
    expect(runCi).toContain("Packages\\manifest.json");
    expect(runCi).toContain("ProjectSettings\\ProjectVersion.txt");
    expect(runCi).toContain("testables = @($PackageName)");
    expect(runCi).toContain(".artifacts\\unity\\projects\\$Version-$Mode");
    expect(runCi).not.toContain("projectPath: .unity-test-project");
  });

  test("run-ci-tests owns cache, Accelerator, and result validation diagnostics", () => {
    expect(runCi).toContain("Initialize-UnityCacheEnvironment");
    expect(runCi).toContain("UPM_CACHE_ROOT");
    expect(runCi).toContain("UNITY_ACCELERATOR_ENDPOINT");
    expect(runCi).toContain("-EnableCacheServer");
    expect(runCi).toContain("Test-NUnitResults");
    expect(runCi).toContain("0 tests ran");
    expect(runCi).toContain("function ConvertTo-NormalizedAcceleratorEndpoint");
    // M2: pin BOTH add-mask Write-Host calls to their actual syntax, anchored
    // to start-of-line so a `# ` comment prefix never satisfies the match.
    // A bare `toContain("::add-mask::")` matches the documentation comment
    // block even if both Write-Host lines are deleted; this regex set matches
    // only the EXECUTABLE Write-Host invocations (multiline, leading
    // whitespace only -- no `#`).
    expect(runCi).toMatch(/^\s*Write-Host\s+"::add-mask::\$\(\$Endpoint\.Trim\(\)\)"/m);
    expect(runCi).toMatch(/^\s*Write-Host\s+"::add-mask::\$normalized"/m);
    // Regression (m3): the old URL-rejection throws MUST be gone forever.
    // Pin to `throw "..."` syntax via regex so a code-archaeology comment
    // that quotes the old message text doesn't unexpectedly fail this guard.
    expect(runCi).not.toMatch(/throw\s+"UNITY_ACCELERATOR_ENDPOINT must be host:port/);
  });

  // -------------------------------------------------------------------------
  // FAILED-TEST ENUMERATION DIAGNOSTIC (static contract).
  //
  // When Unity reports failures, the aggregate "$failed tests failed" line is
  // not actionable -- a real 2021.3 PlayMode run failed 1 of 697 tests and the
  // logs never named it. Test-NUnitResults must enumerate WHICH tests failed
  // (fullname + message + stack) via Write-UnityFailedTestAnnotations BEFORE the
  // existing Write-CiError + throw. We pin the helper's existence, its XPath
  // surface (leaf cases AND OneTimeSetUp/TearDown suite failures), its
  // single-line annotation collapse, its cap, and that the throw still follows.
  // The behavioral proof (it actually prints fullname + message + annotation)
  // lives in the spawn-based test below and in verify-unity-results-action.test.js.
  // -------------------------------------------------------------------------
  test("run-ci-tests Test-NUnitResults enumerates failed tests before throwing", () => {
    // The diagnostic helper exists and is the single-line collapse helper it
    // depends on.
    expect(runCi).toContain("function Write-UnityFailedTestAnnotations");
    expect(runCi).toContain("function ConvertTo-SingleLineDiagnostic");

    // It selects failed leaf cases AND failed suites (the OneTimeSetUp/TearDown
    // shape). Both XPath selectors must be present.
    expect(runCi).toContain("//test-case[@result='Failed']");
    expect(runCi).toContain("//test-suite[@result='Failed']");
    // The suite branch reports any suite with its OWN direct <failure> child
    // (deduped by fullname), so a suite's teardown message is not lost even when
    // it also has a failed child case.
    expect(runCi).toContain("SelectSingleNode('failure')");

    // FIX 1 (StrictMode safety): attribute reads on test nodes go through
    // XmlElement.GetAttribute (returns '' when absent, never throws) via the
    // Get-NUnitNodeFullName helper -- NOT the dynamic `$node.fullname` accessor,
    // which throws "property cannot be found" under Set-StrictMode for an absent
    // attribute and would degrade the whole enumeration to a generic warning.
    expect(runCi).toContain("function Get-NUnitNodeFullName");
    expect(runCi).toContain("$Node.GetAttribute('fullname')");
    expect(runCi).toContain("$Node.GetAttribute('name')");
    // The fragile dynamic accessors must NOT reappear in executable code.
    expect(runCi).not.toMatch(/^\s*\$fullName\s*=\s*\$node\.fullname\b/m);
    expect(runCi).not.toMatch(/^\s*\$fullName\s*=\s*\$node\.name\b/m);

    // FIX 3 (workflow-command injection): the raw multi-line message/stack dump
    // is fenced with ::stop-commands::<token> ... ::<token>:: so an injected
    // `::error::`/`::set-output::` line in an assertion message is neutralized.
    // The token is a FRESH random GUID per dump (defense-in-depth, mirroring
    // GitHub's own @actions/core), so the open and close fence lines reference
    // the same $script:WorkflowCommandStopToken value, which is (re)assigned from
    // the New-WorkflowCommandStopToken generator immediately before each dump.
    expect(runCi).toContain("$script:WorkflowCommandStopToken");
    expect(runCi).toMatch(/Write-Host "::stop-commands::\$script:WorkflowCommandStopToken"/);
    expect(runCi).toMatch(/Write-Host "::\$script:WorkflowCommandStopToken::"/);
    // The random-token generator exists and is invoked per dump; the old fixed
    // literal must NOT be the value emitted into the fence anymore.
    expect(runCi).toContain("function New-WorkflowCommandStopToken");
    expect(runCi).toMatch(/\[guid\]::NewGuid\(\)\.ToString\('N'\)/);
    expect(runCi).toMatch(/\$script:WorkflowCommandStopToken = New-WorkflowCommandStopToken/);
    // The token holder must not be initialized to the old fixed literal.
    expect(runCi).not.toMatch(
      /\$script:WorkflowCommandStopToken\s*=\s*'dxm-stop-commands-failed-test-dump'/
    );

    // Per-failure detail: message inner text and stack.
    expect(runCi).toContain("SelectSingleNode('message')");
    expect(runCi).toContain("SelectSingleNode('stack-trace')");

    // A single-line ::error:: annotation per failed test and a ::group:: console
    // block with the full multi-line detail.
    expect(runCi).toMatch(/::error::\$\{Label\} failed test:/);
    expect(runCi).toContain("::group::Failed test:");

    // Bounded output with a no-silent-cap truncation notice.
    expect(runCi).toContain("Select-Object -First $MaxFailures");
    expect(runCi).toMatch(/additional failed test\(s\) not shown/);

    // The enumeration runs BEFORE the existing Write-CiError + throw (it must
    // not replace or mask the real failure). Assert ordering by index.
    const enumIdx = runCi.indexOf("Write-UnityFailedTestAnnotations -Xml $xml -Label $Label");
    const throwIdx = runCi.indexOf('throw "$failed tests failed for $Label."');
    expect(enumIdx).toBeGreaterThan(-1);
    expect(throwIdx).toBeGreaterThan(-1);
    expect(enumIdx).toBeLessThan(throwIdx);

    // The aggregate Results: line, the ${Label} notice, and the "0 tests ran"
    // handling are all preserved alongside the new enumeration.
    expect(runCi).toContain(
      'Write-Host "Results: total=$total passed=$passed failed=$failed skipped=$skipped"'
    );
    expect(runCi).toContain('Write-CiNotice "${Label}: total=$total');
    expect(runCi).toContain('Write-CiError "0 tests ran for $Label');
  });

  // Behavioral proof for the runner's enumeration helper: extract the helper
  // (and the two functions it depends on) from the real script source, define
  // them in a fresh pwsh via Invoke-Expression, and run them against a synthetic
  // NUnit3 results.xml that contains BOTH a failed <test-case> AND a failed
  // <test-suite>/OneTimeTearDown. Mirrors the extract-and-run pattern in
  // unity-accelerator-endpoint-normalization.test.js. Skips when pwsh is absent
  // (CI runners have pwsh); an always-on sanity assertion keeps it from becoming
  // a silent no-op.
  describe("run-ci-tests Write-UnityFailedTestAnnotations behavioral enumeration", () => {
    const { combinedText: combinePwsh } = require("../lib/pwsh-output");
    const os = require("os");

    function pwshAvailable() {
      return (
        childProcess.spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
          encoding: "utf8"
        }).status === 0
      );
    }
    const PWSH_PRESENT = pwshAvailable();

    function extractHelperSources() {
      return [
        extractFunctionBody(runCi, "Write-CiNotice"),
        extractFunctionBody(runCi, "ConvertTo-SingleLineDiagnostic"),
        // The enumeration helper now depends on the StrictMode-safe
        // Get-NUnitNodeFullName resolver, the $script:WorkflowCommandStopToken
        // holder, and the New-WorkflowCommandStopToken random-token generator;
        // all must be defined in the extracted environment or the helper
        // degrades to its generic catch (no name / no fence). The token is now a
        // FRESH random GUID per dump, not a fixed literal, so we seed the holder
        // to $null and let the helper assign each dump's token via the generator.
        "$script:WorkflowCommandStopToken = $null",
        extractFunctionBody(runCi, "New-WorkflowCommandStopToken"),
        extractFunctionBody(runCi, "Get-NUnitNodeFullName"),
        extractFunctionBody(runCi, "Write-UnityFailedTestAnnotations")
      ].join("\n");
    }

    // Synthetic NUnit3 results.xml: one failed leaf case (assertion failure) and
    // one failed SetUpFixture suite whose own <failure> is a [OneTimeTearDown]
    // Assert.Fail (no failed child case) -- the SuiteWallClockBudgetTest shape.
    // The outer aggregate suites are result="Failed" too but must NOT be printed
    // (the Assembly suite has no direct <failure>; the fixture has a failed child).
    const FAILED_RESULTS_XML = [
      '<?xml version="1.0" encoding="utf-8"?>',
      '<test-run total="3" passed="1" failed="2" skipped="0" result="Failed">',
      '  <test-suite type="Assembly" name="Tests.dll" fullname="Tests.dll" result="Failed">',
      '    <test-suite type="SetUpFixture" name="SuiteWallClockBudgetTest"',
      '        fullname="DxMessaging.Tests.Runtime.SuiteWallClockBudgetTest" result="Failed">',
      "      <failure>",
      "        <message>OneTimeTearDown: Default suite wall clock (200.00s) exceeded the hard",
      "budget (180.0s). Reduce iteration counts.</message>",
      "        <stack-trace>at SuiteWallClockBudgetTest.EndSuiteTimer()</stack-trace>",
      "      </failure>",
      "    </test-suite>",
      '    <test-suite type="TestFixture" name="MessageBusTests"',
      '        fullname="DxMessaging.Tests.Runtime.Core.MessageBusTests" result="Failed">',
      '      <test-case name="DispatchOrder"',
      '          fullname="DxMessaging.Tests.Runtime.Core.MessageBusTests.DispatchOrder" result="Failed">',
      "        <failure>",
      "          <message>  Expected: 5\n  But was:  4</message>",
      "          <stack-trace>at MessageBusTests.DispatchOrder() in MessageBusTests.cs:line 42</stack-trace>",
      "        </failure>",
      "      </test-case>",
      '      <test-case name="PassingOne"',
      '          fullname="DxMessaging.Tests.Runtime.Core.MessageBusTests.PassingOne" result="Passed" />',
      "    </test-suite>",
      "  </test-suite>",
      "</test-run>"
    ].join("\n");

    function runEnumeration(xmlText) {
      const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-failed-enum-"));
      const xmlPath = path.join(tmpDir, "results.xml");
      fs.writeFileSync(xmlPath, xmlText, "utf8");
      const program = [
        "Set-StrictMode -Version Latest",
        "$ErrorActionPreference = 'Stop'",
        "Invoke-Expression $env:DXM_HELPER_SOURCE",
        "[xml]$doc = Get-Content -LiteralPath $env:DXM_RESULTS_XML -Raw",
        "Write-UnityFailedTestAnnotations -Xml $doc -Label 'Unity 2021.3 playmode'"
      ].join("\n");
      const result = childProcess.spawnSync(
        "pwsh",
        ["-NoProfile", "-NonInteractive", "-Command", program],
        {
          env: {
            ...process.env,
            DXM_HELPER_SOURCE: extractHelperSources(),
            DXM_RESULTS_XML: xmlPath
          },
          encoding: "utf8",
          maxBuffer: 16 * 1024 * 1024
        }
      );
      fs.rmSync(tmpDir, { recursive: true, force: true });
      return result;
    }

    test("the helper source is extractable from the script (sanity, always runs)", () => {
      expect(extractFunctionBody(runCi, "Write-UnityFailedTestAnnotations")).not.toBe("");
    });

    (PWSH_PRESENT ? test : test.skip)(
      "prints fullname + message + single-line annotation for a failed test-case AND a OneTimeTearDown suite",
      () => {
        const result = runEnumeration(FAILED_RESULTS_XML);
        const text = combinePwsh(result);

        // (1) Failed leaf case: fullname + collapsed single-line ::error:: + the
        // multi-line message recovered inside the ::group:: block.
        expect(text).toContain(
          "::error::Unity 2021.3 playmode failed test: DxMessaging.Tests.Runtime.Core.MessageBusTests.DispatchOrder"
        );
        expect(text).toContain(
          "::group::Failed test: DxMessaging.Tests.Runtime.Core.MessageBusTests.DispatchOrder"
        );
        expect(text).toContain("Expected: 5");
        expect(text).toContain("But was: 4");

        // (2) OneTimeTearDown suite failure: enumerated as its OWN failed node
        // (failed suite with a <failure> and no failed child case).
        expect(text).toContain(
          "::error::Unity 2021.3 playmode failed test: DxMessaging.Tests.Runtime.SuiteWallClockBudgetTest"
        );
        expect(text).toContain("exceeded the hard budget");

        // (3) The aggregate-only suites are NOT enumerated (no double-print):
        // the assembly suite has no annotation of its own, and the MessageBusTests
        // fixture is covered by its failed child case, not a separate suite entry.
        expect(text).not.toContain("failed test: Tests.dll");
        expect(text).not.toContain(
          "failed test: DxMessaging.Tests.Runtime.Core.MessageBusTests --"
        );

        // (4) The passing case is never reported.
        expect(text).not.toContain("PassingOne");
      }
    );

    (PWSH_PRESENT ? test : test.skip)(
      "caps output at 50 failed tests and prints a truncation notice (no silent cap)",
      () => {
        const cases = [];
        for (let i = 1; i <= 55; i++) {
          cases.push(
            `    <test-case name="T${i}" fullname="Ns.Big.T${i}" result="Failed">` +
              `<failure><message>fail ${i}</message></failure></test-case>`
          );
        }
        const xml = [
          '<?xml version="1.0" encoding="utf-8"?>',
          '<test-run total="55" passed="0" failed="55" skipped="0" result="Failed">',
          '  <test-suite type="TestFixture" name="Big" fullname="Ns.Big" result="Failed">',
          ...cases,
          "  </test-suite>",
          "</test-run>"
        ].join("\n");

        const result = runEnumeration(xml);
        const text = combinePwsh(result);

        const annotationCount = (text.match(/::error::Unity 2021\.3 playmode failed test:/g) || [])
          .length;
        expect(annotationCount).toBe(50);
        expect(text).toContain("5 additional failed test(s) not shown");
      }
    );

    // FIX 1 (StrictMode safety): a Failed <test-case> with NO `fullname`
    // attribute (only `name`). The enumeration helper runs under
    // `Set-StrictMode -Version Latest` (see runEnumeration's program preamble),
    // where the OLD dynamic `$node.fullname` accessor THROWS "The property
    // 'fullname' cannot be found" for an absent attribute -- degrading the whole
    // enumeration to the generic ::warning:: catch. With the GetAttribute-based
    // resolver it must instead print the NAME + annotation and NOT warn.
    (PWSH_PRESENT ? test : test.skip)(
      "enumerates a failed test-case that has only name (no fullname) without throwing under StrictMode",
      () => {
        const xml = [
          '<?xml version="1.0" encoding="utf-8"?>',
          '<test-run total="1" passed="0" failed="1" skipped="0" result="Failed">',
          '  <test-suite type="TestFixture" name="NoFullName" result="Failed">',
          '    <test-case name="OnlyHasName" result="Failed">',
          "      <failure><message>boom</message></failure>",
          "    </test-case>",
          "  </test-suite>",
          "</test-run>"
        ].join("\n");

        const result = runEnumeration(xml);
        const text = combinePwsh(result);

        // The name is used as the fullname fallback in BOTH the annotation and
        // the group header; the helper did not degrade to the generic warning.
        expect(text).toContain("::error::Unity 2021.3 playmode failed test: OnlyHasName");
        expect(text).toContain("::group::Failed test: OnlyHasName");
        expect(text).toContain("boom");
        expect(text).not.toContain("Could not enumerate failed tests");
        // The StrictMode "property cannot be found" failure must never surface.
        expect(text).not.toContain("cannot be found");
      }
    );

    // FIX 3 (workflow-command injection): a Failed test whose raw <message>
    // contains `::error::INJECTED` and `::set-output name=x::`. The raw
    // multi-line dump inside the ::group:: block must be wrapped in a
    // ::stop-commands::<token> ... ::<token>:: fence so GitHub does not execute
    // the injected directives. The token is a RANDOM per-dump GUID, so we
    // EXTRACT the actual token from the output and assert (a) the matching
    // `::<that-token>::` close exists, and (b) the injected directives fall
    // BETWEEN the opening fence and its close (neutralized).
    (PWSH_PRESENT ? test : test.skip)(
      "fences the raw failure message in ::stop-commands:: so an injected workflow command is neutralized",
      () => {
        const xml = [
          '<?xml version="1.0" encoding="utf-8"?>',
          '<test-run total="1" passed="0" failed="1" skipped="0" result="Failed">',
          '  <test-suite type="TestFixture" name="Inj" fullname="Ns.Inj" result="Failed">',
          '    <test-case name="Evil" fullname="Ns.Inj.Evil" result="Failed">',
          "      <failure>",
          "        <message>line one",
          "::error::INJECTED",
          "::set-output name=x::pwned</message>",
          "        <stack-trace>at Ns.Inj.Evil()</stack-trace>",
          "      </failure>",
          "    </test-case>",
          "  </test-suite>",
          "</test-run>"
        ].join("\n");

        const result = runEnumeration(xml);
        const text = combinePwsh(result);

        // Extract the ACTUAL random token emitted after `::stop-commands::` and
        // assert the matching `::<that-token>::` close line exists. The token
        // shape is `dxm-stop-commands-<32-hex-guid>` (a GUID 'N' form). The old
        // fixed literal must NOT appear.
        const openMatch = /::stop-commands::(dxm-stop-commands-[0-9a-fA-F]{32})/.exec(text);
        expect(openMatch).not.toBeNull();
        const token = openMatch[1];
        expect(token).not.toBe("dxm-stop-commands-failed-test-dump");
        const openMarker = `::stop-commands::${token}`;
        const closeMarker = `::${token}::`;
        expect(text).toContain(openMarker);
        expect(text).toContain(closeMarker);

        // The injected directives appear INSIDE the fence: after the opening
        // ::stop-commands:: marker and before its closing token. (combinedText
        // flattens newlines, so order is checked by index. The injected strings
        // ALSO appear earlier in the flattened single-line ::error:: annotation;
        // we search for the raw-body copy AFTER the opening fence.)
        const openIdx = text.indexOf(openMarker);
        const closeIdx = text.indexOf(closeMarker, openIdx + openMarker.length);
        const injectedErrorIdx = text.indexOf("::error::INJECTED", openIdx + 1);
        const injectedOutputIdx = text.indexOf("::set-output name=x::pwned", openIdx + 1);
        expect(openIdx).toBeGreaterThanOrEqual(0);
        expect(closeIdx).toBeGreaterThan(openIdx);
        expect(injectedErrorIdx).toBeGreaterThan(openIdx);
        expect(injectedErrorIdx).toBeLessThan(closeIdx);
        expect(injectedOutputIdx).toBeGreaterThan(openIdx);
        expect(injectedOutputIdx).toBeLessThan(closeIdx);
      }
    );

    // LOW (suite with BOTH its own failure AND a failed child): a fixture that
    // fails a test AND throws in [OneTimeTearDown] must surface BOTH the child
    // case's assertion message AND the suite's own teardown message. The suite
    // is reported on its direct <failure> regardless of failed descendants;
    // fullname de-dup keeps the suite distinct from its child case.
    (PWSH_PRESENT ? test : test.skip)(
      "reports a suite's own teardown failure even when it also has a failed child case",
      () => {
        const xml = [
          '<?xml version="1.0" encoding="utf-8"?>',
          '<test-run total="2" passed="0" failed="2" skipped="0" result="Failed">',
          '  <test-suite type="TestFixture" name="BothFix"',
          '      fullname="Ns.BothFix" result="Failed">',
          "    <failure>",
          "      <message>OneTimeTearDown: teardown blew up</message>",
          "      <stack-trace>at Ns.BothFix.TearDown()</stack-trace>",
          "    </failure>",
          '    <test-case name="ChildFail" fullname="Ns.BothFix.ChildFail" result="Failed">',
          "      <failure><message>child assert failed</message></failure>",
          "    </test-case>",
          "  </test-suite>",
          "</test-run>"
        ].join("\n");

        const result = runEnumeration(xml);
        const text = combinePwsh(result);

        // The child case is enumerated.
        expect(text).toContain("::error::Unity 2021.3 playmode failed test: Ns.BothFix.ChildFail");
        expect(text).toContain("child assert failed");
        // The suite's OWN teardown failure is ALSO enumerated (its fullname
        // differs from the child's, so no double-print).
        expect(text).toContain("::error::Unity 2021.3 playmode failed test: Ns.BothFix");
        expect(text).toContain("teardown blew up");
      }
    );
  });

  test(".llm/context.md describes Accelerator endpoint as URL-or-host:port (normalized)", () => {
    const ctx = fs.readFileSync(path.join(REPO_ROOT, ".llm", "context.md"), "utf8");
    // Pin to the actual normalizer function name (unambiguous, stable) rather
    // than the loose word "normalized" within an arbitrary char window. The
    // 800-char window is well above the current bullet's ~580 chars, leaving
    // headroom for documentation growth without fragility.
    expect(ctx).toMatch(
      /UNITY_ACCELERATOR_ENDPOINT[\s\S]{0,800}ConvertTo-NormalizedAcceleratorEndpoint/
    );
    expect(ctx).toMatch(/scheme:\/\/host:port/);
    // Inverse-claim guard (m2): the OLD wording must never come back.
    // Loosened to catch grammar variants ("not a http URL", "not an http URL",
    // "requires host:port format", "requires the host:port format", etc.).
    expect(ctx).not.toMatch(/not an? `?http:\/\/`? URL/i);
    expect(ctx).not.toMatch(/requires (?:the )?`?host:port`? format/i);
  });

  test("run-ci-tests uses classic serial activation with a guaranteed license return", () => {
    // The direct CI runner uses classic SERIAL activation: it activates the paid
    // seat from UNITY_SERIAL/UNITY_EMAIL/UNITY_PASSWORD (-serial -username
    // -password) via Invoke-UnityLicenseActivate (which THROWS on failure) and
    // returns it via Invoke-UnityLicenseReturn (best-effort, never throws,
    // `-returnlicense`). These reads see the RAW file text, so the in-array flag
    // literals (-serial / -returnlicense) are assertable directly here.
    expect(runCi).toContain("Invoke-UnityLicenseActivate");
    expect(runCi).toContain("Invoke-UnityLicenseReturn");
    expect(runCi).toContain("-returnlicense");
    expect(runCi).toContain("-serial");
    expect(runCi).toContain("-username");
    expect(runCi).toContain("-password");
    // The three serial credentials gate activation (all required in CI).
    expect(runCi).toContain("UNITY_SERIAL");
    expect(runCi).toContain("UNITY_EMAIL");
    expect(runCi).toContain("UNITY_PASSWORD");
    // The OLD floating-license-server surface must be GONE from the CI runner.
    expect(runCi).not.toContain("--acquire-floating");
    expect(runCi).not.toContain("--return-floating");
    expect(runCi).not.toContain("UNITY_LICENSING_SERVER");
    expect(runCi).not.toContain("services-config.json");
    expect(runCi).not.toContain("Write-UnityLicensingServerConfig");
    expect(runCi).not.toContain("Resolve-UnityLicensingClient");
  });

  // -------------------------------------------------------------------------
  // PRODUCTION GUARD (data-driven): every Unity.exe arg-array literal in
  // run-ci-tests.ps1 must obey the manual's rule that `-runTests` and `-quit`
  // are MUTUALLY EXCLUSIVE -- per
  // https://docs.unity3d.com/Manual/EditorCommandLineArguments.html the editor
  // QUITS IMMEDIATELY when both are present, before in-progress tests complete,
  // exiting 0 with NO results.xml. The non-test invocations (license activate /
  // return, native startup probe, configure -executeMethod) legitimately need
  // -quit; the test-launch array must not.
  //
  // We scan every top-level `@( ... )` array literal in the script that LOOKS
  // LIKE a Unity.exe argument vector (contains -batchmode OR -runTests OR
  // -quit OR -executeMethod as a quoted token) and assert the rule against
  // each one, so a NEWLY ADDED Unity.exe invocation is automatically
  // checked rather than silently skipped. The check is order-independent: any
  // matching array literal in the file is evaluated.
  // -------------------------------------------------------------------------
  describe("run-ci-tests Unity.exe arg arrays obey -runTests excludes -quit", () => {
    // Extract every top-level `@( ... )` array literal from the script source by
    // brace-matching the parentheses (so nested `@(...)` inside an array is
    // preserved within the outer extent rather than ending the slice prematurely).
    // Returns each match with its starting char index (so we can compute a line
    // number) and the raw body text BETWEEN the outer `@(` and `)`.
    function extractAtArrayLiterals(scriptText) {
      const matches = [];
      let i = 0;
      while (i < scriptText.length) {
        const open = scriptText.indexOf("@(", i);
        if (open < 0) {
          break;
        }
        let depth = 1;
        let j = open + 2;
        while (j < scriptText.length && depth > 0) {
          const ch = scriptText[j];
          if (ch === "(") {
            depth++;
          } else if (ch === ")") {
            depth--;
            if (depth === 0) {
              break;
            }
          }
          j++;
        }
        if (depth !== 0) {
          // Unbalanced -- bail out rather than report bogus violations.
          break;
        }
        matches.push({ index: open, body: scriptText.slice(open + 2, j) });
        i = j + 1;
      }
      return matches;
    }

    function lineNumberOf(text, charIndex) {
      let n = 1;
      for (let k = 0; k < charIndex && k < text.length; k++) {
        if (text[k] === "\n") {
          n++;
        }
      }
      return n;
    }

    // Collect all quoted tokens (single OR double quoted) inside an array
    // literal body. A "Unity.exe arg-vector" array is one whose tokens include
    // at least one of the Unity command-line flags the script uses.
    //
    // PowerShell `#`-to-EOL comments are stripped FIRST so a contributor who
    // disables a flag via `# '-quit',  <-- intentionally disabled` doesn't
    // trigger a false-positive contract failure. The stripper is
    // quote-aware: a `#` INSIDE a single- or double-quoted string is literal,
    // NOT a comment start, and is preserved verbatim.
    function stripPowerShellLineComments(body) {
      const lines = body.split("\n");
      for (let li = 0; li < lines.length; li++) {
        const line = lines[li];
        let inSingle = false;
        let inDouble = false;
        let commentAt = -1;
        for (let k = 0; k < line.length; k++) {
          const ch = line[k];
          if (inSingle) {
            if (ch === "'") {
              inSingle = false;
            }
            continue;
          }
          if (inDouble) {
            if (ch === '"') {
              inDouble = false;
            }
            continue;
          }
          if (ch === "'") {
            inSingle = true;
            continue;
          }
          if (ch === '"') {
            inDouble = true;
            continue;
          }
          if (ch === "#") {
            commentAt = k;
            break;
          }
        }
        if (commentAt >= 0) {
          lines[li] = line.slice(0, commentAt);
        }
      }
      return lines.join("\n");
    }

    const QUOTED_TOKEN_RE = /'([^']*)'|"([^"]*)"/g;
    function quotedTokensIn(body) {
      const stripped = stripPowerShellLineComments(body);
      const out = [];
      QUOTED_TOKEN_RE.lastIndex = 0;
      let m;
      while ((m = QUOTED_TOKEN_RE.exec(stripped)) !== null) {
        out.push(m[1] !== undefined ? m[1] : m[2]);
      }
      return out;
    }

    describe("quotedTokensIn helper is PowerShell-comment-aware", () => {
      test("strips '#'-to-EOL disabled-flag comments before tokenizing", () => {
        const body = [
          "    '-batchmode',",
          "    # '-quit', # intentionally disabled per docs/runbook",
          "    '-runTests'"
        ].join("\n");
        const tokens = quotedTokensIn(body);
        expect(tokens).toContain("-runTests");
        expect(tokens).toContain("-batchmode");
        expect(tokens).not.toContain("-quit");
      });

      test("preserves '#' that appears INSIDE a single-quoted string literal", () => {
        const body = ["    'has # in literal',", "    '-runTests'"].join("\n");
        const tokens = quotedTokensIn(body);
        expect(tokens).toContain("has # in literal");
        expect(tokens).toContain("-runTests");
      });

      test("preserves '#' that appears INSIDE a double-quoted string literal", () => {
        const body = ['    "tag #1",', "    '-runTests'"].join("\n");
        const tokens = quotedTokensIn(body);
        expect(tokens).toContain("tag #1");
        expect(tokens).toContain("-runTests");
      });
    });

    const UNITY_FLAG_MARKERS = new Set([
      "-batchmode",
      "-runTests",
      "-quit",
      "-executeMethod",
      "-returnlicense",
      "-serial"
    ]);

    function isUnityArgVector(tokens) {
      for (const t of tokens) {
        if (UNITY_FLAG_MARKERS.has(t)) {
          return true;
        }
      }
      return false;
    }

    test("every Unity.exe arg-array literal is scanned and at least one matches", () => {
      // Sanity: the extractor MUST find at least the six known Unity arg
      // vectors (activate, return, probe, configure, the editmode/playmode test
      // run, AND the standalone editor BUILD). A regression that turned the
      // extractor into a silent no-op would otherwise pass an empty table below.
      const literals = extractAtArrayLiterals(runCi);
      const unityVectors = literals.filter((l) => isUnityArgVector(quotedTokensIn(l.body)));
      expect(unityVectors.length).toBeGreaterThanOrEqual(6);
    });

    test("the standalone player-RUN array has '-dxmTestResults' and NEITHER -runTests NOR -quit", () => {
      // The directly-launched standalone player vector is
      // @('-batchmode','-nographics','-logFile','-','-dxmTestResults',$ResultsPath).
      // It IS recognized as a Unity-marker vector BECAUSE -batchmode is in
      // UNITY_FLAG_MARKERS, so the negative "-runTests excludes -quit" rule
      // already covers it; it passes that rule because it has NEITHER flag. Pin
      // its existence + shape so a refactor that dropped the file-based results
      // channel (or re-added a -runTests/-quit to the player launch) is caught.
      const literals = extractAtArrayLiterals(runCi);
      const playerRunArrays = literals
        .map((l) => quotedTokensIn(l.body))
        .filter((tokens) => tokens.includes("-dxmTestResults"));
      expect(playerRunArrays.length).toBeGreaterThanOrEqual(1);
      for (const tokens of playerRunArrays) {
        expect(tokens).toContain("-batchmode");
        expect(tokens).not.toContain("-runTests");
        expect(tokens).not.toContain("-quit");
      }
    });

    test("NO Unity.exe arg array contains both '-runTests' AND '-quit'", () => {
      const literals = extractAtArrayLiterals(runCi);
      const violations = [];
      for (const lit of literals) {
        const tokens = quotedTokensIn(lit.body);
        if (!isUnityArgVector(tokens)) {
          continue;
        }
        if (tokens.includes("-runTests") && tokens.includes("-quit")) {
          violations.push({
            line: lineNumberOf(runCi, lit.index),
            tokens
          });
        }
      }
      if (violations.length > 0) {
        const detail = violations
          .map(
            (v) =>
              `scripts/unity/run-ci-tests.ps1:${v.line} -- Unity arg array contains both '-runTests' and '-quit' (illegal per https://docs.unity3d.com/Manual/EditorCommandLineArguments.html). Tokens: ${JSON.stringify(v.tokens)}`
          )
          .join("\n");
        throw new Error(detail);
      }
      expect(violations).toEqual([]);
    });

    test("the test-launch array (contains '-runTests') exists, omits '-quit', and still has '-batchmode'", () => {
      // Positive coverage: prove the rule is enforced on an array that ACTUALLY
      // runs tests (so a refactor that accidentally deleted the only -runTests
      // array would still be caught -- otherwise the negative test above would
      // trivially pass on an empty set).
      const literals = extractAtArrayLiterals(runCi);
      const testArrays = literals
        .map((l) => ({ line: lineNumberOf(runCi, l.index), tokens: quotedTokensIn(l.body) }))
        .filter((entry) => entry.tokens.includes("-runTests"));
      expect(testArrays.length).toBeGreaterThanOrEqual(1);
      for (const entry of testArrays) {
        expect(entry.tokens).toContain("-batchmode");
        expect(entry.tokens).not.toContain("-quit");
      }
    });

    test("arrays containing '-executeMethod' MAY contain '-quit' (the standalone configure pass is the canonical case)", () => {
      // The standalone configure -executeMethod invocation NEEDS -quit so the
      // editor exits after applying the configurator (it is NOT a test run).
      // We don't REQUIRE -quit on every -executeMethod array, but we want to
      // PROVE the rule allows it: assert at least one such array exists and
      // its presence does NOT violate the negative rule above.
      const literals = extractAtArrayLiterals(runCi);
      const execMethodArrays = literals
        .map((l) => quotedTokensIn(l.body))
        .filter((tokens) => tokens.includes("-executeMethod") && !tokens.includes("-runTests"));
      expect(execMethodArrays.length).toBeGreaterThanOrEqual(1);
      // The canonical case bundles -quit with -executeMethod for a clean exit.
      const withQuit = execMethodArrays.filter((tokens) => tokens.includes("-quit"));
      expect(withQuit.length).toBeGreaterThanOrEqual(1);
    });
  });

  // -------------------------------------------------------------------------
  // STANDALONE SPLIT-BUILD + FILE-BASED RESULTS contract.
  //
  // The legacy `-runTests -testPlatform StandaloneWindows64` flow had the built
  // player stream NUnit results to the editor over PlayerConnection/TCP, which
  // hangs on the self-hosted runners' multi-NIC networks (errorcode 10060). The
  // fix splits the standalone path into (2a) an editor BUILD that severs the
  // player's outbound connections via a generated DxmCiStandaloneBuildModifier and
  // exits via IPostBuildCleanup, (2b) a DIRECT run of the built exe (whose
  // generated DxmCiStandaloneTestCallback writes NUnit XML to -dxmTestResults and
  // quits), and (2c) file-based validation -- all under a hard tree-kill watchdog.
  // These tests pin the new helpers, env-vars, sentinels, watchdog SHAPE, ordering,
  // and the generated-C# content; the behavioral proof is the strictmode-smoke
  // spawn test. editmode/playmode are untouched.
  // -------------------------------------------------------------------------
  describe("run-ci-tests standalone split-build + file-based results", () => {
    test("declares the new watchdog / timeout / player functions (existence tripwires)", () => {
      expect(runCi).toContain("function Invoke-ProcessWithTreeKillTimeout");
      expect(runCi).toContain("function Invoke-StandaloneTestPlayer");
      expect(runCi).toContain("function Get-StandaloneTestPlayerTimeoutSeconds");
      expect(runCi).toContain("function Get-StandaloneBuildTimeoutSeconds");
    });

    test("declares the standalone env-vars and sentinels (existence tripwires)", () => {
      expect(runCi).toContain("DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS");
      expect(runCi).toContain("DXM_STANDALONE_BUILD_TIMEOUT_SECONDS");
      expect(runCi).toContain("DXM_PLAYER_BUILD_PATH");
      expect(runCi).toContain("-dxmTestResults");
      expect(runCi).toContain("DxmTestPlayer.exe");
    });

    test("the timeout getters default to 30 min (player) and 45 min (build) and follow the env-parse convention", () => {
      expect(runCi).toMatch(
        /function Get-StandaloneTestPlayerTimeoutSeconds[\s\S]*?param\(\[int\]\$Default = 1800\)/
      );
      expect(runCi).toMatch(
        /function Get-StandaloneBuildTimeoutSeconds[\s\S]*?param\(\[int\]\$Default = 2700\)/
      );
      // Mirror ensure-editor's TryParse >= 0 with a ::warning:: on an invalid value.
      expect(runCi).toMatch(
        /function Get-StandaloneTestPlayerTimeoutSeconds[\s\S]*?\[int\]::TryParse\(\$env:DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS, \[ref\]\$parsed\)/
      );
      expect(runCi).toMatch(
        /function Get-StandaloneBuildTimeoutSeconds[\s\S]*?\[int\]::TryParse\(\$env:DXM_STANDALONE_BUILD_TIMEOUT_SECONDS, \[ref\]\$parsed\)/
      );
    });

    test("the watchdog mirrors the proven ensure-editor tree-kill SHAPE", () => {
      // SAME structural guards the pinned ensure-editor assertions use: a
      // System.Diagnostics.Process with a $proc.Kill($true) tree-kill, the
      // ConvertTo-ProcessArgumentLine helper (NOT .ArgumentList), and NO bare
      // WaitForExit() (the bounded WaitForExit(5000) reap is fine -- the negative
      // matches only the empty-parens form).
      expect(runCi).toMatch(
        /function Invoke-ProcessWithTreeKillTimeout[\s\S]*?\$proc\.Kill\(\$true\)/
      );
      expect(runCi).toContain("function ConvertTo-ProcessArgumentLine");
      expect(runCi).toMatch(
        /function Invoke-ProcessWithTreeKillTimeout[\s\S]*?ConvertTo-ProcessArgumentLine -Arguments \$Arguments/
      );
      expect(runCi).not.toContain(".ArgumentList");
      expect(runCi).not.toMatch(/WaitForExit\(\)/);
      // The launched process is held in a try/finally that tree-kills on any throw
      // (closes the orphaned-player-on-cancellation gap).
      expect(runCi).toMatch(
        /finally\s*\{[\s\S]*?if \(\$proc -and -not \$proc\.HasExited\)\s*\{\s*try \{ \$proc\.Kill\(\$true\) \}/
      );
    });

    test("uses a SINGLE results channel (-dxmTestResults), with no env handoff", () => {
      // The player reads -dxmTestResults only; there is NO env channel and NO
      // persistentDataPath fallback anywhere in the runner.
      expect(runCi).not.toContain("DXM_TEST_RESULTS");
      expect(runCi).not.toContain("persistentDataPath");
    });

    test("the player build goes UNDER project Build (not Temp or the uploaded artifact) and the player log under artifacts", () => {
      expect(runCi).toContain("Join-Path $ProjectPath 'Build\\DxmTestPlayer\\DxmTestPlayer.exe'");
      expect(runCi).not.toContain("Join-Path $ProjectPath 'Temp\\DxmTestPlayer\\DxmTestPlayer.exe'");
      expect(runCi).toContain("$playerLogPath = Join-Path $ArtifactsPath 'player.log'");
    });

    test("default CI category filtering is threaded into Unity test invocations", () => {
      expect(runCi).toContain("$TestCategory");
      expect(runCi).toContain("DXM_UNITY_TEST_CATEGORY");
      expect(runCi).toContain("'-testCategory', $TestCategory");
      expect(runCi).toContain(") + $categoryArgs + $acceleratorArgs");
    });

    test("Invoke-StandaloneTestPlayer builds the player run vector and maps exit 2 -> no results", () => {
      const body = extractFunctionBody(runCi, "Invoke-StandaloneTestPlayer");
      expect(body).not.toBe("");
      const normalized = normalizeWhitespace(body);
      // The exact player launch vector (single channel; NO -runTests/-quit).
      expect(normalized).toContain("'-batchmode',");
      expect(normalized).toContain("'-nographics',");
      expect(normalized).toContain("'-logFile', '-',");
      expect(normalized).toContain("'-dxmTestResults', $ResultsPath");
      expect(normalized).toContain("Invoke-ProcessWithTreeKillTimeout");
      // Exit-2 (no -dxmTestResults path) is thrown here; a watchdog TIMEOUT is NOT --
      // it is RETURNED (TimedOut) so the CALLER can honor the results FILE as the
      // source of truth. A player can write a valid results.xml in RunFinished and then
      // have Application.Quit deferred in -batchmode IL2CPP; treating that tree-kill as
      // a hard failure would turn a passing run red, so the file decides, not the exit.
      expect(normalized).toMatch(/\$result\.ExitCode -eq 2/);
      expect(normalized).toContain("TimedOut = $result.TimedOut");
      // The timeout env-knob handling now lives in the caller, not this function.
      expect(normalized).not.toContain("DXM_STANDALONE_PLAYER_TIMEOUT_SECONDS");
    });

    test("the standalone branch builds with -runTests + -buildTarget and NO -quit", () => {
      // The editor BUILD vector reuses -runTests (so PlayerLauncher's modify path
      // fires) + -buildTarget StandaloneWindows64, but must NOT carry -quit (the
      // editor must reach PostBuildCleanup to exit). It is automatically covered by
      // the -runTests-excludes-quit negative scan above; here we pin its presence.
      expect(runCi).toContain("$env:DXM_PLAYER_BUILD_PATH = $standaloneExe");
      expect(runCi).toMatch(
        /\$buildResult = Invoke-ProcessWithTreeKillTimeout[\s\S]*?-TimeoutSeconds \(Get-StandaloneBuildTimeoutSeconds\)/
      );
      // POST-BUILD validity tripwire: missing/stale exe or missing _Data fails
      // before launching a leftover player.
      expect(runCi).toContain("function Test-StandalonePlayerBuildOutput");
      expect(runCi).toContain("function Write-StandaloneBuildOutputDiagnostics");
      expect(runCi).toContain("$standaloneBuildStartedUtc = [DateTime]::UtcNow");
      expect(runCi).toContain("LastWriteTimeUtc");
      expect(runCi).toContain("missing player data directory");
      expect(runCi).toContain("Unity may have cleaned a Temp output");
      // The build-log missed-case scan for the non-redirected AutoRun signatures.
      expect(runCi).toContain("PlayerWithTests");
      expect(runCi).toContain("options\\.AutoRunPlayer = True");
    });

    test("the standalone branch runs the player then validates the FILE; editmode/playmode keep the single -runTests path", () => {
      // The standalone branch is guarded by `if ($TestMode -eq 'standalone')`, runs
      // the player BEFORE validating, and validates the FILE with the player log.
      const playerCallIdx = runCi.indexOf("Invoke-StandaloneTestPlayer `");
      const standaloneValidateIdx = runCi.indexOf(
        'Test-NUnitResults -Path $resultsPath -Label "Unity $UnityVersion standalone" -LogPath $playerLogPath'
      );
      // The standalone player path also threads the player's exit code into the
      // FILE validator as advisory (a passing results.xml with a non-zero player
      // exit is still green, with a benign-crash warning). On a watchdog TIMEOUT
      // the inline notice already explains the kill, so 0 is threaded instead (no
      // double-warning / no mislabeling the 124 sentinel as a crash).
      expect(runCi).toContain(
        'Test-NUnitResults -Path $resultsPath -Label "Unity $UnityVersion standalone" -LogPath $playerLogPath -Project $ProjectPath -UnityExitCode $playerExitForValidation'
      );
      expect(runCi).toContain("$playerExitForValidation = $playerResult.ExitCode");
      expect(runCi).toMatch(/if \(\$playerResult\.TimedOut\)[\s\S]*?\$playerExitForValidation = 0/);
      expect(playerCallIdx).toBeGreaterThan(-1);
      expect(standaloneValidateIdx).toBeGreaterThan(-1);
      // INDEX-ORDER: the player runs before the file validation.
      expect(playerCallIdx).toBeLessThan(standaloneValidateIdx);
      // editmode/playmode use the single -runTests editor invocation and validate
      // the FILE. The editor runs via Invoke-UnityEditor (which RETURNS the exit
      // code, NOT throws), and Test-NUnitResults is the gate -- it receives the
      // captured -UnityExitCode so the FILE, never the process exit code, decides
      // pass/fail (a passing results.xml with a non-zero shutdown-crash exit is
      // still green, with a benign-crash warning).
      expect(runCi).toContain(
        'Test-NUnitResults -Path $resultsPath -Label "Unity $UnityVersion $TestMode" -LogPath $logPath -Project $ProjectPath -UnityExitCode $runExit'
      );
      expect(runCi).toContain(
        '$runExit = Invoke-UnityEditor `'
      );
      // The retired throw-on-non-zero wrapper must be GONE as a DEFINITION (the
      // exit code is no longer a fatal trigger; the artifact is). A historical
      // mention in a comment is allowed; a live `function ...` definition is not.
      expect(runCi).not.toMatch(/function\s+Invoke-UnityEditorWithFailureDiagnostics\b/);

      // MAJOR-fix contract: on a player watchdog TIMEOUT the caller honors the results
      // FILE as the source of truth when one exists (a deferred Application.Quit after
      // RunFinished already wrote it) and throws the timeout ONLY when no file exists,
      // so a passing run is never turned red by a deferred quit. The timeout guard sits
      // BETWEEN the player run and the file validation.
      const timeoutGuardIdx = runCi.indexOf("if ($playerResult.TimedOut)");
      expect(timeoutGuardIdx).toBeGreaterThan(playerCallIdx);
      expect(timeoutGuardIdx).toBeLessThan(standaloneValidateIdx);
      const guardRegion = runCi.slice(timeoutGuardIdx, standaloneValidateIdx);
      expect(guardRegion).toMatch(/Test-Path -LiteralPath \$resultsPath/);
      expect(guardRegion).toMatch(/honoring that results file/i);
      expect(guardRegion).toMatch(/timed out after \$playerTimeoutSeconds/);
    });

    test("the standalone configure step (New-ConfiguratorSource) parameterizes the backend + NET_Standard + Release codeOpt + stripping, then writes a success marker -- no waitForManagedDebugger / ConnectWithProfiler", () => {
      // All connection suppression lives in the generated build modifier; the
      // configurator switches the build target, sets the PARAMETERIZED scripting
      // backend (IL2CPP or Mono2x via -Backend), the non-deprecated
      // ApiCompatibilityLevel.NET_Standard (NOT the deprecated _2_0 form), Release
      // code optimization, and disables managed stripping so the test code survives
      // a Release Mono player build, PLUS writes the success marker as its FINAL
      // action. A profiler/debugger PlayerSetting here would be the rejected,
      // cross-version-fragile design.
      const body = extractFunctionBody(runCi, "New-ConfiguratorSource");
      expect(body).not.toBe("");
      expect(body).not.toContain("waitForManagedDebugger");
      expect(body).not.toContain("ConnectWithProfiler");
      expect(body).toContain("SwitchActiveBuildTarget");
      // PARAMETERIZED backend (not a pinned IL2CPP/Mono2x member).
      expect(body).toContain("ScriptingImplementation.$Backend");
      // The non-deprecated ApiCompatibilityLevel.NET_Standard (.NET Standard 2.1);
      // the deprecated _2_0 form must be GONE from the configurator.
      expect(body).toContain("ApiCompatibilityLevel.NET_Standard");
      expect(body).not.toContain("ApiCompatibilityLevel.NET_Standard_2_0");
      // The pinned-IL2CPP dead comment reference must be GONE (it existed only to
      // satisfy the old contract); the live code is parameterized.
      expect(body).not.toContain("ScriptingImplementation.IL2CPP");
      // Release editor code optimization is proven for the perf legs.
      expect(body).toContain("CompilationPipeline.codeOptimization");
      expect(body).toContain("CodeOptimization.Release");
      // Managed stripping is disabled so IncludeTestAssemblies + the [Preserve]
      // standalone TestRunCallback survive a non-development (Release) Mono player.
      expect(body).toContain("SetManagedStrippingLevel");
      expect(body).toContain("ManagedStrippingLevel.Disabled");
      // SOURCE-OF-TRUTH MARKER: Apply writes the marker handed in via
      // DXM_CONFIGURE_MARKER_PATH as its final action, so a benign Unity shutdown
      // crash AFTER configuration cannot fail the job (the runner gates on the
      // marker, not the process exit code).
      expect(body).toContain("DXM_CONFIGURE_MARKER_PATH");
      expect(body).toContain("File.WriteAllText(markerPath");
      // And the success-marker write must remain the FINAL action -- after every
      // PlayerSettings mutation (it proves Apply ran to COMPLETION). Pin it against
      // the LAST PlayerSettings call (managed stripping) so the ordering invariant
      // survives the migration off the deprecated NET_Standard_2_0 anchor.
      expect(body.indexOf("File.WriteAllText(markerPath")).toBeGreaterThan(
        body.indexOf("SetManagedStrippingLevel")
      );
    });

    test("New-ConfiguratorSource is the LAST configurator write, with the success marker as its final C# action", () => {
      // The success-marker write must be the FINAL action of the generated Apply()
      // (everything else -- target switch, backend, api level, code optimization,
      // managed stripping, the proof Debug.Log -- precedes it). Pin it against EACH
      // PlayerSettings mutation so a reordering that moved the marker earlier fails.
      const body = extractFunctionBody(runCi, "New-ConfiguratorSource");
      const markerIdx = body.indexOf("File.WriteAllText(markerPath");
      expect(markerIdx).toBeGreaterThan(-1);
      for (const earlier of [
        "SwitchActiveBuildTarget",
        "SetScriptingBackend",
        "SetApiCompatibilityLevel",
        "CompilationPipeline.codeOptimization",
        "SetManagedStrippingLevel"
      ]) {
        const idx = body.indexOf(earlier);
        expect(idx).toBeGreaterThan(-1);
        expect(idx).toBeLessThan(markerIdx);
      }
    });

    test("run-ci-tests declares the comparison/Release/backend perf params", () => {
      // Slice 3b wires the comparison + Mono + Release perf legs through these new
      // top-level script parameters; the perf-numbers workflow splats them. Pin the
      // typed param DECLARATION for each (the [switch]/[string] form) so a renamed
      // or dropped param fails loudly. The -Name caller form lives in the workflow
      // that invokes the script, not in the script itself.
      expect(runCi).toMatch(/\[switch\]\$IncludeComparisons\b/);
      expect(runCi).toMatch(/\[switch\]\$ReleaseCodeOptimization\b/);
      expect(runCi).toMatch(/\[string\]\$StandaloneScriptingBackend\b/);
      expect(runCi).toMatch(/\[switch\]\$ReleasePlayerBuild\b/);
      // StandaloneScriptingBackend is constrained to the two valid backends.
      expect(runCi).toMatch(/\[ValidateSet\(\s*'IL2CPP'\s*,\s*'Mono2x'\s*\)\]/);
      // The params are threaded into the ephemeral-project + run wiring (not dead):
      // the backend flows into Initialize-EphemeralProject, while Release player
      // and Release code optimization are now the unconditional Unity CI contract.
      expect(runCi).toContain("-Backend $StandaloneScriptingBackend");
      expect(runCi).toContain("$UseReleaseCodeOptimization = $true");
      expect(runCi).toContain("$UseReleasePlayerBuild = $true");
      expect(runCi).toContain("-DevelopmentBuild:(-not $UseReleasePlayerBuild)");
      expect(runCi).toContain("-IncludeComparisons:$IncludeComparisons");
      expect(runCi).not.toContain("if ($ReleaseCodeOptimization)");
    });

    test("New-ManifestJson injects comparison deps + scopedRegistries ONLY under -IncludeComparisons", () => {
      // The comparison legs add the OpenUPM scoped registry, external package pins,
      // and Unity built-in packages from the single source
      // .github/comparison-packages.json; non-comparison legs must stay byte-for-byte
      // identical (no scopedRegistries key and no comparison-only dependencies).
      const body = extractFunctionBody(runCi, "New-ManifestJson");
      expect(body).not.toBe("");
      const guardIdx = body.indexOf("if ($IncludeComparisons)");
      // Match the ASSIGNMENT (not the comment prose that also says
      // "scopedRegistries"): the manifest key is set only inside the guard block.
      const scopedIdx = body.indexOf("$manifest['scopedRegistries']");
      const builtInIdx = body.indexOf("unityBuiltInPackages");
      expect(guardIdx).toBeGreaterThan(-1);
      expect(scopedIdx).toBeGreaterThan(guardIdx);
      expect(builtInIdx).toBeGreaterThan(guardIdx);
      // The comparison source is read from the single-source JSON, not duplicated.
      expect(body).toContain("Get-ComparisonPackages");
    });

    test("-releaseCodeOptimization is only added to -runTests arg arrays that have NO -quit", () => {
      // -runTests + -quit is mutually exclusive per the Unity manual. The Release
      // code-optimization CLI flag must therefore live in Unity invocations that
      // carry -runTests but NOT in the standalone configure pass, which carries
      // -quit. Bound checks to the array literals so explanatory comments cannot
      // create false positives.
      const arrayRegion = (variableName) => {
        const arrayStart = runCi.indexOf(`${variableName} = @(`);
        expect(arrayStart).toBeGreaterThan(-1);
        const arrayEnd = runCi.indexOf("\n        )", arrayStart);
        expect(arrayEnd).toBeGreaterThan(arrayStart);
        return runCi.slice(arrayStart, arrayEnd);
      };

      for (const variableName of ["$buildArgs", "$testArgs"]) {
        const region = arrayRegion(variableName);
        expect(region).toContain("'-runTests'");
        expect(region).toContain("'-releaseCodeOptimization'");
        expect(region).not.toContain("'-quit'");
      }

      const configureRegion = arrayRegion("$configureArgs");
      expect(configureRegion).toContain("'-quit'");
      expect(configureRegion).not.toContain("'-releaseCodeOptimization'");
    });

    test("New-StandaloneBuildModifierSource emits the dual-attribute modifier + cleanup", () => {
      const body = extractFunctionBody(runCi, "New-StandaloneBuildModifierSource");
      expect(body).not.toBe("");
      expect(body).toContain("ITestPlayerBuildModifier");
      expect(body).toContain("IPostBuildCleanup");
      expect(body).toContain("[assembly: TestPlayerBuildModifier(typeof(");
      expect(body).toContain("[assembly: PostBuildCleanup(typeof(");
      // The three outbound-connection sources are cleared; test assemblies kept.
      expect(body).toContain("&= ~BuildOptions.AutoRunPlayer");
      expect(body).toContain("&= ~BuildOptions.ConnectToHost");
      expect(body).toContain("&= ~BuildOptions.ConnectWithProfiler");
      expect(body).toContain("|= BuildOptions.IncludeTestAssemblies");
      // PostBuildCleanup arms the editor exit gated on -runTests.
      expect(body).toContain("EditorApplication.Exit(0)");
      expect(body).toContain("-runTests");
      expect(body).toContain("DXM_PLAYER_BUILD_PATH");
    });

    test("New-StandaloneTestCallbackSource emits the player TestRunCallback writing NUnit XML", () => {
      const body = extractFunctionBody(runCi, "New-StandaloneTestCallbackSource");
      expect(body).not.toBe("");
      expect(body).toContain("[assembly: TestRunCallback(typeof(");
      expect(body).toContain(": ITestRunCallback");
      expect(body).toContain("RunFinished");
      expect(body).toContain("result.ToXml(true)");
      expect(body).toContain('new TNode("test-run")');
      expect(body).toContain('AddAttribute("total",');
      expect(body).toContain('AddAttribute("passed",');
      expect(body).toContain('AddAttribute("failed",');
      // ResultState is a NUnit OBJECT on the player; must be .ToString()'d.
      expect(body).toContain("result.ResultState.ToString()");
      expect(body).toContain("Application.Quit");
      expect(body).toContain("[Preserve]");
      expect(body).toContain("-dxmTestResults");
      // NEGATIVE: single channel only -- no env, no persistentDataPath fallback.
      expect(body).not.toContain("persistentDataPath");
      expect(body).not.toContain("DXM_TEST_RESULTS");
    });

    test("New-StandaloneTestCallbackAsmdef references UnityEngine.TestRunner with nunit + UNITY_INCLUDE_TESTS", () => {
      const body = extractFunctionBody(runCi, "New-StandaloneTestCallbackAsmdef");
      expect(body).not.toBe("");
      expect(body).toContain("UnityEngine.TestRunner");
      expect(body).toContain("nunit.framework.dll");
      expect(body).toContain("UNITY_INCLUDE_TESTS");
      expect(body).toContain("overrideReferences");
    });

    test("Initialize-EphemeralProject writes the three standalone files ONLY for standalone, idempotently", () => {
      const body = extractFunctionBody(runCi, "Initialize-EphemeralProject");
      expect(body).not.toBe("");
      // Gated on $Mode -eq 'standalone'.
      expect(body).toContain("if ($Mode -eq 'standalone')");
      expect(body).toContain("Assets\\Editor\\DxmCiStandaloneBuildModifier.cs");
      expect(body).toContain("Assets\\DxmCiStandaloneTestCallback\\DxmCiStandaloneTestCallback.cs");
      expect(body).toContain(
        "Assets\\DxmCiStandaloneTestCallback\\DxmCiStandaloneTestCallback.asmdef"
      );
      // Idempotent write-when-missing-or-changed (like Copy-DxMessagingAnalyzersToAssets).
      expect(body).toMatch(/Test-Path -LiteralPath \$file\.Path -PathType Leaf/);
      expect(body).toContain("New-StandaloneBuildModifierSource");
      expect(body).toContain("New-StandaloneTestCallbackSource");
      expect(body).toContain("New-StandaloneTestCallbackAsmdef");
    });
  });
});

// ---------------------------------------------------------------------------
// Regression guards (data-driven over BOTH runner scripts).
//
// These lock in the fix for the "CI mode short-circuit" bug: both run-tests.sh
// and run-tests.ps1 used to detect CI=true, print would-be
// game-ci/unity-test-runner@v4 parameters, and exit 0 WITHOUT running Unity.
// Every CI job went green having run ZERO tests. The principle (.llm/context.md:
// never silently default to permissive behavior) is: a runner must NEVER report
// success without a results.xml proving tests ran (total > 0), on EVERY path.
//
// We assert against BOTH scripts at once so a regression in either half — or a
// reintroduction of the no-op stub — fails loudly. Assertions stay simple and
// string-based to match this file's existing grep style.
// ---------------------------------------------------------------------------
describe("Unity runner scripts never short-circuit success without running tests", () => {
  const RUNNER_SCRIPTS = [
    { name: "run-tests.sh", content: readScript("scripts/unity/run-tests.sh") },
    { name: "run-tests.ps1", content: readScript("scripts/unity/run-tests.ps1") }
  ];

  test.each(RUNNER_SCRIPTS)(
    "$name does NOT contain the no-op CI short-circuit stub strings",
    ({ content }) => {
      // The old stub printed these exact strings before exiting 0 without
      // spawning Unity. None may ever reappear.
      expect(content).not.toContain("skipping local docker invocation");
      expect(content).not.toContain("CI mode detected");
      expect(content).not.toContain("game-ci/unity-test-runner@v4 parameters");
      // A case-insensitive belt-and-suspenders check for the most distinctive
      // fragment (catches re-wordings like "Skipping local docker run").
      expect(content).not.toMatch(/skipping local docker/i);
    }
  );

  test.each(RUNNER_SCRIPTS)(
    "$name routes results through the shared parse-test-results.py validator",
    ({ content }) => {
      // Success can only be reported via Write-ResultsSummary /
      // print_results_summary, which delegate to the shared parser.
      expect(content).toContain("parse-test-results.py");
      // The total==0 guard text is the load-bearing "tests actually ran"
      // assertion; it must be present in both scripts.
      expect(content).toContain("0 tests ran");
    }
  );

  test.each(RUNNER_SCRIPTS)(
    "$name emits CI annotations via the CI env var (annotations, not control flow)",
    ({ content }) => {
      // CI is still referenced — but ONLY to emit ::error::/::notice::
      // GitHub Actions annotations, never to gate execution.
      expect(content).toContain("::error::");
      expect(content).toContain("::notice::");
    }
  );
});

describe("scripts/unity/activate-license.sh contract", () => {
  const licPath = path.join(UNITY_SCRIPTS, "activate-license.sh");
  let content;

  beforeAll(() => {
    content = readScript("scripts/unity/activate-license.sh");
  });

  test("file exists and is executable", () => {
    expect(fs.existsSync(licPath)).toBe(true);
    expect(hasExecutableBit(licPath)).toBe(true);
  });

  test("exposes --check mode (diagnostic / default)", () => {
    expect(content).toContain("--check");
  });

  test("exposes --apply mode (Pro .ulf encoder)", () => {
    expect(content).toContain("--apply");
  });

  test("references LOCAL_WORKSPACE_FOLDER (DooD path translation)", () => {
    expect(content).toContain("LOCAL_WORKSPACE_FOLDER");
  });

  test("references DXM_HOST_REPO_ROOT override (DooD path translation)", () => {
    expect(content).toContain("DXM_HOST_REPO_ROOT");
  });

  test("prefers docker-inspected devcontainer mount before LOCAL_WORKSPACE_FOLDER", () => {
    expect(content.indexOf("if is_container_runtime")).toBeGreaterThan(
      content.indexOf("DXM_HOST_REPO_ROOT")
    );
    expect(content.indexOf("detect_host_repo_root_from_container")).toBeLessThan(
      content.indexOf("Ignoring LOCAL_WORKSPACE_FOLDER=")
    );
  });

  test("does not advertise email/password-only Personal activation", () => {
    expect(content).toContain("UNITY_LICENSE_B64");
    expect(content).toContain("UNITY_SERIAL");
    expect(content).not.toContain('-serial ""');
    expect(content).not.toContain("Personal activation succeeded");
  });
});
