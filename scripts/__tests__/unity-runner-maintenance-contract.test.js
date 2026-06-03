/**
 * @fileoverview Static contract tests for Windows runner maintenance.
 *
 * Runner maintenance is the only CI-supported path that may install or repair
 * Unity editors/modules. Ordinary Unity jobs stay detect-only through
 * -RequireHealthyExisting.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const yaml = require("yaml");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const MAINTAIN_SCRIPT = path.join(REPO_ROOT, "scripts", "unity", "maintain-windows-runner.ps1");
const INSTALL_TASK_SCRIPT = path.join(
  REPO_ROOT,
  "scripts",
  "unity",
  "install-runner-maintenance-task.ps1"
);
const RUNNER_BOOTSTRAP_WORKFLOW = path.join(
  REPO_ROOT,
  ".github",
  "workflows",
  "runner-bootstrap.yml"
);
const PRINT_DIAGNOSTICS_ACTION = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "print-self-hosted-runner-diagnostics",
  "action.yml"
);
const UNITY_TESTS_WORKFLOW = path.join(REPO_ROOT, ".github", "workflows", "unity-tests.yml");

const ACTIVE_UNITY_VERSIONS = ["2021.3.45f1", "2022.3.45f1", "6000.3.16f1"];

function readUtf8(absPath) {
  return fs.readFileSync(absPath, "utf8");
}

function readYaml(absPath) {
  return yaml.parse(readUtf8(absPath));
}

describe("maintain-windows-runner.ps1 contract", () => {
  let content;

  beforeAll(() => {
    content = readUtf8(MAINTAIN_SCRIPT);
  });

  test("script and Unity .meta sibling exist", () => {
    expect(fs.existsSync(MAINTAIN_SCRIPT)).toBe(true);
    expect(fs.existsSync(`${MAINTAIN_SCRIPT}.meta`)).toBe(true);
  });

  test("is PowerShell 5.1 compatible and dot-source safe", () => {
    expect(content).toContain("#Requires -Version 5.1");
    expect(content).toContain("$invokedAsScript = $MyInvocation.InvocationName -ne '' -and $MyInvocation.InvocationName -ne '.'");
    expect(content).toContain("if ($invokedAsScript)");
    expect(content).toContain("Set-StrictMode -Version Latest");
  });

  test("defaults cover every active Unity test matrix version", () => {
    for (const version of ACTIVE_UNITY_VERSIONS) {
      expect(content).toContain(`'${version}'`);
    }
    expect(content).toContain("[string]$ProvisioningProfile = 'StandaloneWindowsIl2Cpp'");
    expect(content).toContain("[string[]]$UnityVersions = @('2021.3.45f1', '2022.3.45f1', '6000.3.16f1')");
    expect(content).toContain("[string]$InstallRoot = $(if ($env:UNITY_EDITOR_INSTALL_ROOT)");
  });

  test("takes the required maintenance inputs", () => {
    for (const parameter of [
      "[switch]$DetectOnly",
      "[string[]]$UnityVersions",
      "[string]$ProvisioningProfile",
      "[string]$InstallRoot",
      "[switch]$Force",
      "[string]$DiagnosticsRoot"
    ]) {
      expect(content).toContain(parameter);
    }
  });

  test("uses a named mutex and busy-runner guard with explicit exit classes", () => {
    expect(content).toContain("Global\\DxMessagingUnityRunnerMaintenance");
    expect(content).toContain("WaitOne(0)");
    expect(content).toContain("Get-RunnerBusyProcesses");
    expect(content).toContain("'Runner.Worker'");
    expect(content).toContain("'Unity'");
    expect(content).toContain("runner-busy");
    expect(content).toContain("mutex-busy");
    expect(content).toContain("busy-skipped");
  });

  test("runs bootstrap before Unity editor provisioning", () => {
    const bootstrapIndex = content.indexOf("& $bootstrap");
    const ensureIndex = content.indexOf("& $ensureEditor");

    expect(bootstrapIndex).toBeGreaterThanOrEqual(0);
    expect(ensureIndex).toBeGreaterThan(bootstrapIndex);
  });

  test("clears stale native exit codes before child PowerShell script calls", () => {
    const bootstrapIndex = content.indexOf("& $bootstrap");
    const ensureIndex = content.indexOf("& $ensureEditor");
    const bootstrapResetIndex = content.lastIndexOf("$global:LASTEXITCODE = 0", bootstrapIndex);
    const ensureResetIndex = content.lastIndexOf("$global:LASTEXITCODE = 0", ensureIndex);

    expect(bootstrapResetIndex).toBeGreaterThanOrEqual(0);
    expect(ensureResetIndex).toBeGreaterThan(bootstrapIndex);
    expect(ensureResetIndex).toBeLessThan(ensureIndex);
  });

  test("trusts provisioning summary success instead of stale native exit code", () => {
    expect(content).toContain("$hasProvisioningSummary = $null -ne $provisioningSummary");
    expect(content).toContain(
      "if ($classification -ne 'success' -or (-not $hasProvisioningSummary -and $versionExit -ne 0))"
    );
    expect(content).not.toContain("if ($versionExit -ne 0 -or $classification -ne 'success')");
  });

  test("repair mode omits RequireHealthyExisting while audit mode includes it", () => {
    expect(content).toContain("if ($DetectOnly)");
    expect(content).toContain("$ensureArgs.RequireHealthyExisting = $true");
    expect(content).not.toContain("$ensureArgs.RequireHealthyExisting = $false");
  });

  test("writes transcript-ready JSON/text diagnostics with required fields", () => {
    for (const token of [
      "runner-maintenance-summary.json",
      "runner-maintenance-summary.txt",
      "runnerName",
      "machineName",
      "isAdmin",
      "repoSha",
      "repoRef",
      "mode",
      "startedUtc",
      "finishedUtc",
      "hostBootstrapExit",
      "finalClassification",
      "missingModules",
      "startupProbeLogPath",
      "exitClass"
    ]) {
      expect(content).toContain(token);
    }
  });

  test("does not require Unity license secrets or organization build lock", () => {
    expect(content).not.toContain("UNITY_SERIAL");
    expect(content).not.toContain("UNITY_EMAIL");
    expect(content).not.toContain("UNITY_PASSWORD");
    expect(content).not.toContain("wallstop-organization-builds");
  });
});

describe("install-runner-maintenance-task.ps1 contract", () => {
  let content;

  beforeAll(() => {
    content = readUtf8(INSTALL_TASK_SCRIPT);
  });

  test("script and Unity .meta sibling exist", () => {
    expect(fs.existsSync(INSTALL_TASK_SCRIPT)).toBe(true);
    expect(fs.existsSync(`${INSTALL_TASK_SCRIPT}.meta`)).toBe(true);
  });

  test("creates a dedicated maintenance clone under ProgramData", () => {
    expect(content).toContain("C:\\ProgramData\\DxMessaging\\runner-maintenance");
    expect(content).toContain("$repoDir = Join-Path $MaintenanceRoot 'repo'");
    expect(content).toContain("git");
    expect(content).toContain("clone");
    expect(content).toContain("fetch");
    expect(content).toContain("--prune");
    expect(content).toContain("pull");
    expect(content).toContain("--ff-only");
    expect(content).not.toContain("--hard");
  });

  test("runs existing-clone git updates inside the maintenance clone", () => {
    expect(content).toContain("function Invoke-GitChecked");
    expect(content).toContain("Push-Location -LiteralPath $WorkingDirectory");
    expect(content).toContain("Pop-Location");
    expect(content).toContain("Invoke-GitChecked -Arguments @('fetch', '--prune', 'origin', $Branch) -WorkingDirectory $repoDir");
    expect(content).toContain("Invoke-GitChecked -Arguments @('checkout', $Branch) -WorkingDirectory $repoDir");
    expect(content).toContain("Invoke-GitChecked -Arguments @('pull', '--ff-only', 'origin', $Branch) -WorkingDirectory $repoDir");
  });

  test("registers startup and daily highest-privilege scheduled task", () => {
    expect(content).toContain("New-ScheduledTaskTrigger -AtStartup");
    expect(content).toContain("New-ScheduledTaskTrigger -Daily");
    expect(content).toContain("New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest");
    expect(content).toContain("Register-ScheduledTask");
    expect(content).toContain("-Force");
  });

  test("scheduled task invokes maintain-windows-runner.ps1 with diagnostics", () => {
    expect(content).toContain("maintain-windows-runner.ps1");
    expect(content).toContain("-DiagnosticsRoot");
    expect(content).toContain("-ProvisioningProfile");
    expect(content).toContain("-UnityVersions");
  });

  test("scheduled task passes Unity versions as separate PowerShell array values", () => {
    expect(content).toContain("'-UnityVersions'");
    expect(content).toContain(") + $versionArgs + @(");
    expect(content).not.toContain("$versionArgs -join ','");
  });
});

describe("runner-bootstrap workflow maintenance wrapper", () => {
  let workflow;

  beforeAll(() => {
    workflow = readUtf8(RUNNER_BOOTSTRAP_WORKFLOW);
  });

  test("keeps workflow_dispatch and wrong-target hard fail", () => {
    expect(workflow).toMatch(/^on:\s*\n\s+workflow_dispatch:/m);
    expect(workflow).toContain("Confirm runner identity");
    expect(workflow).toContain("::error::Bootstrap dispatched");
    expect(workflow).toMatch(/exit\s+1\b/);
  });

  test("wraps maintain-windows-runner.ps1 and uploads maintenance summaries", () => {
    expect(workflow).toContain("scripts\\unity\\maintain-windows-runner.ps1");
    expect(workflow).toContain(". $script");
    expect(workflow).toContain("Invoke-WindowsRunnerMaintenance");
    expect(workflow).toContain("$unityVersions = @('2021.3.45f1', '2022.3.45f1', '6000.3.16f1')");
    expect(workflow).toContain("$provisioningProfile = 'StandaloneWindowsIl2Cpp'");
    expect(workflow).toContain("$installRoot = if ($env:UNITY_EDITOR_INSTALL_ROOT)");
    expect(workflow).toContain("UnityVersions = $unityVersions");
    expect(workflow).toContain("ProvisioningProfile = $provisioningProfile");
    expect(workflow).toContain("InstallRoot = $installRoot");
    expect(workflow).toContain("DiagnosticsRoot = $artifactDir");
    expect(workflow).toContain("maintenance-");
    expect(workflow).toContain(".artifacts/runner-bootstrap/**");
    expect(workflow).toContain("actions/upload-artifact@v7");
  });
});

describe("ordinary Unity jobs stay detect-only", () => {
  test("runner diagnostics action disables host auto-install for Unity jobs", () => {
    const action = readYaml(PRINT_DIAGNOSTICS_ACTION);
    const assertStep = action.runs.steps.find(
      (step) => step.name === "Assert Unity host prerequisites"
    );

    expect(assertStep).toBeDefined();
    expect(assertStep.with["auto-install"]).toBe("false");
  });

  test("active Unity test workflow provision step requires healthy existing editor", () => {
    const workflow = readUtf8(UNITY_TESTS_WORKFLOW);

    expect(workflow).toContain("-RequireHealthyExisting");
    expect(workflow).toContain("-ProvisioningProfile $provisioningProfile");
    expect(workflow).not.toContain("maintain-windows-runner.ps1");
  });
});
