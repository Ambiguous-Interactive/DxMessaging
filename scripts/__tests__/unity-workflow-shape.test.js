/**
 * @fileoverview Contract tests for the shape of Unity-related GitHub Actions
 * workflow templates.
 *
 * These workflows have non-obvious invariants that, if violated, cause silent
 * regressions:
 *   - Licensed Unity jobs must only run for same-repo pull requests,
 *     protected branch pushes, schedules, and manual dispatch.
 *   - Required Unity workflows must use exact per-version/per-mode Library
 *     cache keys, with no broad restore keys, for the generated ephemeral
 *     test project.
 *   - The devcontainer workflows must override `eventFilterForPush: ""` to
 *     avoid devcontainers/ci@v0.3's silent push-skip on schedule/dispatch.
 *
 * We parse with the `yaml` package for the structural parts (the on: block in
 * particular) and fall back to text-grep for cross-cutting requirements that
 * are easier to verify line-by-line (cache key references, action versions).
 */

"use strict";

const fs = require("fs");
const path = require("path");
const yaml = require("yaml");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOWS_DIR = path.join(REPO_ROOT, ".github", "workflows");
const ACTIONS_DIR = path.join(REPO_ROOT, ".github", "actions");
const DISABLED_WORKFLOWS_DIR = path.join(REPO_ROOT, ".github", "workflows-disabled");
const ENSURE_EDITOR_SCRIPT = path.join(REPO_ROOT, "scripts", "unity", "ensure-editor.ps1");
const DIAGNOSTICS_ACTION = path.join(
  ACTIONS_DIR,
  "print-self-hosted-runner-diagnostics",
  "action.yml"
);
const UNITY_WORKFLOWS = ["unity-tests.yml", "unity-benchmarks.yml"];
const GAMECI_EXPERIMENT_WORKFLOW = "unity-gameci-experiment.yml";
const UNITY_VERSIONS = ["2021.3.45f1", "2022.3.45f1", "6000.3.16f1"];

function readWorkflow(name) {
  const abs = path.join(WORKFLOWS_DIR, name);
  expect(fs.existsSync(abs)).toBe(true);
  return fs.readFileSync(abs, "utf8");
}

function readDisabledWorkflow(name) {
  const abs = path.join(DISABLED_WORKFLOWS_DIR, name);
  expect(fs.existsSync(abs)).toBe(true);
  return fs.readFileSync(abs, "utf8");
}

function loadWorkflowYaml(name) {
  const text = readWorkflow(name);
  // The `yaml` package parses with the YAML 1.2 core schema, so the bare `on:`
  // trigger key stays the string `"on"` (no YAML 1.1 boolean coercion to
  // `true`); read it directly via `parsed.on`.
  return yaml.parse(text);
}

function loadDisabledWorkflowYaml(name) {
  return yaml.parse(readDisabledWorkflow(name));
}

function loadDiagnosticsAction() {
  expect(fs.existsSync(DIAGNOSTICS_ACTION)).toBe(true);
  return yaml.parse(fs.readFileSync(DIAGNOSTICS_ACTION, "utf8"));
}

function readEnsureEditorScript() {
  expect(fs.existsSync(ENSURE_EDITOR_SCRIPT)).toBe(true);
  return fs.readFileSync(ENSURE_EDITOR_SCRIPT, "utf8");
}

function collectActionMetadataFiles(root = ACTIONS_DIR) {
  const files = [];
  const stack = [root];
  while (stack.length > 0) {
    const dir = stack.pop();
    let entries;
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch (error) {
      throw new Error(`Unable to enumerate action metadata under ${dir}: ${error.message}`);
    }

    for (const entry of entries) {
      const abs = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        stack.push(abs);
      } else if (/^action\.ya?ml$/i.test(entry.name)) {
        files.push(abs);
      }
    }
  }
  return files.sort();
}

function readActionYamlForDiagnostics(actionPath) {
  try {
    return fs.readFileSync(actionPath, "utf8");
  } catch (error) {
    const dir = path.dirname(actionPath);
    let listing = "<unavailable>";
    try {
      listing = fs.readdirSync(dir).sort().join(", ");
    } catch (listingError) {
      listing = `<unavailable: ${listingError.message}>`;
    }
    throw new Error(
      `Unable to read action metadata ${actionPath}: ${error.message}. Directory entries: ${listing}`
    );
  }
}

function extractEnsureEditorDefault(functionName) {
  const escaped = functionName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const pattern = new RegExp(
    `function\\s+${escaped}\\s*\\{[\\s\\S]*?param\\(\\[int\\]\\$Default\\s*=\\s*([0-9]+)\\)`
  );
  const match = pattern.exec(readEnsureEditorScript());
  expect(match).not.toBeNull();
  return Number.parseInt(match[1], 10);
}

function collectSteps(parsed) {
  return Object.values(parsed.jobs || {}).flatMap((job) => job.steps || []);
}

function collectDevcontainerCiSteps(name) {
  const parsed = loadWorkflowYaml(name);
  return collectSteps(parsed).filter((step) => step.uses === "devcontainers/ci@v0.3");
}

function expectExactUnityLibraryCache(text) {
  expect(text).toContain("actions/cache@v4");
  expect(text).toContain("PACKAGE_HASH");
  expect(text).toContain(".artifacts/unity/projects/");
  expect(text).toContain(".artifacts/unity/cache/");
  expect(text).toContain("key: Library");
  expect(text).not.toContain("restore-keys:");
}

function getOnBlock(parsed) {
  // YAML 1.2 core schema (the `yaml` package default) keeps `on:` a string key.
  return parsed.on;
}

function expectUnityRunnerContract(job, expectation) {
  // The Unity serial's activation seat is shared across repositories. Native
  // GitHub concurrency is repository-scoped and would serialize whole jobs, so
  // the licensed section is protected by the central organization lock actions
  // immediately around the direct Unity runner instead of a job-level
  // concurrency block.
  expect(job.concurrency).toBeUndefined();

  // All Unity-credential-using jobs request the same static label set so
  // either Windows machine can pick up any job. Licensed Unity work is
  // serialized by the central lock action after the job starts.
  expect(job["runs-on"]).toEqual(["self-hosted", "Windows", "RAM-64GB"]);

  // Two-layer serialization. The shared Unity activation seat is serialized
  // to one-Unity-at-a-time ACROSS
  // runs/workflows/repos by the central organization lock action; WITHIN a run
  // the matrix is serialized by `strategy.max-parallel: 1` so the 9 (3 versions
  // x 3 modes) cells queue behind one another instead of spawning a thundering
  // herd that races the lock and burns idle job-timeout clocks. The two layers
  // are complementary, not redundant: max-parallel:1 alone cannot stop two
  // separate runs from racing, and the lock alone leaves the herd in place.
  // This is `max-parallel: 1` ONLY -- never a native concurrency group.
  if (expectation.hasMatrix) {
    expect(job.strategy).toBeDefined();
    expect(job.strategy["max-parallel"]).toBe(1);
  } else if (job.strategy !== undefined) {
    expect(job.strategy["max-parallel"]).toBeUndefined();
  }
}

function expectDiagnosticsStep(job) {
  expect(Array.isArray(job.steps)).toBe(true);
  const diagnosticsStep = job.steps.find(
    (step) => step && step.name === "Print runner diagnostics"
  );
  expect(diagnosticsStep).toBeDefined();
  // The diagnostics step uses a composite action that wraps PowerShell.
  // `shell: bash` previously failed on self-hosted Windows runners because
  // the agent's PATH resolves `bash` to C:\Windows\System32\bash.exe (the
  // WSL stub). See FIX #4 sweep in dev/wallstop/swap.
  expect(diagnosticsStep.uses).toBe("./.github/actions/print-self-hosted-runner-diagnostics");
}

function expectSameRepoAndProtectedBranchGuard(job) {
  expect(job.if).toContain("github.event_name != 'pull_request'");
  expect(job.if).toContain("github.event.pull_request.head.repo.full_name == github.repository");
  expect(job.if).toContain("github.event_name != 'push'");
  expect(job.if).toContain("github.ref_protected");
}

describe("Unity workflows are active GitHub workflows", () => {
  test.each(UNITY_WORKFLOWS)("%s exists under .github/workflows", (name) => {
    expect(fs.existsSync(path.join(WORKFLOWS_DIR, name))).toBe(true);
    expect(fs.existsSync(path.join(DISABLED_WORKFLOWS_DIR, name))).toBe(true);
  });
});

// Data-driven contract for every Unity-credential-using job across the Unity
// workflows. Anything that genuinely repeats (runner contract, concurrency
// contract, license secrets, Library cache contract, upload-artifact version,
// diagnostics step, same-repo + protected-branch guards) lives here.
const UNITY_LICENSED_JOBS = [
  {
    workflow: "unity-tests.yml",
    jobId: "unity-tests",
    requiresProtectedBranchGuard: true,
    requiresLibraryCache: true,
    requiresLicenseSecrets: true,
    hasMatrix: true,
    runnerScript: "scripts/unity/run-ci-tests.ps1"
  },
  {
    workflow: "unity-benchmarks.yml",
    jobId: "benchmarks",
    requiresProtectedBranchGuard: false,
    requiresLibraryCache: true,
    requiresLicenseSecrets: true,
    hasMatrix: true,
    runnerScript: "scripts/unity/run-ci-tests.ps1"
  },
  {
    workflow: "release.yml",
    jobId: "unity-checks",
    requiresProtectedBranchGuard: false,
    requiresLibraryCache: false,
    requiresLicenseSecrets: true,
    hasMatrix: false,
    runnerScript: "scripts/unity/run-ci-tests.ps1"
  }
];
const UNITY_PROVISIONED_JOBS = [
  ...UNITY_LICENSED_JOBS,
  {
    workflow: GAMECI_EXPERIMENT_WORKFLOW,
    jobId: "game-ci-experiment",
    hasMatrix: false,
    runStepName: "Run GameCI normal project mode"
  },
  {
    // The per-PR Performance Numbers workflow provisions a CI-managed editor
    // before the org lock exactly like the other Unity jobs, so it must carry
    // the same provisioning-diagnostics contract. It is NOT in
    // UNITY_LICENSED_JOBS because it pins the `fast` (ELI-MACHINE) label set and
    // has its own dedicated contract suite below.
    workflow: "perf-numbers.yml",
    jobId: "perf-benchmarks",
    hasMatrix: true,
    runnerScript: "scripts/unity/run-ci-tests.ps1"
  }
];

function findProvisionStep(steps) {
  return steps.find(
    (step) =>
      step.name === "Provision Unity Editor" &&
      step.shell === "pwsh" &&
      typeof step.run === "string" &&
      step.run.includes("./scripts/unity/ensure-editor.ps1")
  );
}

function expectProvisioningDiagnosticsContract(steps) {
  const provisionStep = findProvisionStep(steps);
  expect(provisionStep).toBeDefined();
  expect(provisionStep.run).toContain(
    "$diagnosticsFile = Join-Path $diagnosticsPath 'ensure-editor-summary.json'"
  );
  expect(provisionStep.run).toContain("-DiagnosticsPath $diagnosticsFile");
  expect(provisionStep.run).toContain("-ProvisioningProfile");
  expect(provisionStep.run).toContain("-RequireHealthyExisting");
  expect(provisionStep.run).toContain("New-Item -ItemType Directory -Force -Path $diagnosticsPath");

  const provisionIndex = steps.indexOf(provisionStep);
  const acquireIndex = steps.findIndex(
    (step) =>
      step.uses ===
      "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1"
  );
  const uploadIndex = steps.findIndex(
    (step) => step.name === "Upload Unity provisioning diagnostics"
  );

  expect(uploadIndex).toBeGreaterThan(provisionIndex);
  if (acquireIndex >= 0) {
    expect(uploadIndex).toBeLessThan(acquireIndex);
  }

  const uploadStep = steps[uploadIndex];
  expect(uploadStep.if).toBe("always()");
  expect(uploadStep.uses).toBe("actions/upload-artifact@v7");
  expect(uploadStep.with["if-no-files-found"]).toBe("warn");
  expect(uploadStep.with.path).toContain("/provisioning");
  expect(uploadStep.with.name).toContain("provisioning");
}

function expectProvisioningProfileForJob(workflow, jobId, expected) {
  const parsed = loadWorkflowYaml(workflow);
  const provisionStep = findProvisionStep(parsed.jobs[jobId].steps);
  expect(provisionStep).toBeDefined();
  if (expected instanceof RegExp) {
    expect(provisionStep.run).toMatch(expected);
  } else {
    expect(provisionStep.run).toContain(`-ProvisioningProfile ${expected}`);
  }
}

describe("Unity-credential-using jobs share the same runner + concurrency contract", () => {
  test.each(UNITY_LICENSED_JOBS)(
    "$workflow job '$jobId' uses static [self-hosted, Windows, RAM-64GB] runs-on and no native license concurrency",
    ({ workflow, jobId, hasMatrix }) => {
      const parsed = loadWorkflowYaml(workflow);
      expectUnityRunnerContract(parsed.jobs[jobId], { hasMatrix });
    }
  );

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow does not use native GitHub concurrency for the organization lock",
    ({ workflow }) => {
      const text = readWorkflow(workflow);
      expect(text).not.toMatch(/^\s*group:\s*unity-pro-license\b/gm);
      expect(text).not.toMatch(/^\s*group:\s*wallstop-organization-builds\b/gm);
    }
  );

  test("active Unity workflows acquire the central wallstop organization build lock by action input only", () => {
    for (const { workflow } of UNITY_LICENSED_JOBS) {
      const text = readWorkflow(workflow);
      expect(text).toContain("lock-name: wallstop-organization-builds");
      expect(text).not.toMatch(/^\s*group:\s*wallstop-organization-builds\b/gm);
    }
  });

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow job '$jobId' has a 'Print runner diagnostics' bash step",
    ({ workflow, jobId }) => {
      const parsed = loadWorkflowYaml(workflow);
      expectDiagnosticsStep(parsed.jobs[jobId]);
    }
  );

  test.each(UNITY_LICENSED_JOBS.filter((job) => job.requiresLicenseSecrets))(
    "$workflow references the three serial-activation secrets and no retired licensing-server secret",
    ({ workflow }) => {
      // The repo uses classic SERIAL activation (UNITY_SERIAL + UNITY_EMAIL +
      // UNITY_PASSWORD). Native Unity workflows must wire all three and must NOT
      // reference the retired floating-license-server secret. Scoped to the
      // `secrets.` prefix so it does not false-fire on prose.
      const text = readWorkflow(workflow);
      expect(text).toMatch(/secrets\.UNITY_SERIAL\b/);
      expect(text).toMatch(/secrets\.UNITY_EMAIL\b/);
      expect(text).toMatch(/secrets\.UNITY_PASSWORD\b/);
      expect(text).not.toMatch(/secrets\.UNITY_LICENSING_SERVER\b/);
    }
  );

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow job '$jobId' wires the optional Unity Accelerator from secrets, not vars",
    ({ workflow, jobId }) => {
      const parsed = loadWorkflowYaml(workflow);
      const runnerStep = parsed.jobs[jobId].steps.find(
        (step) => step.name === "Run Unity Test Runner"
      );

      expect(runnerStep).toBeDefined();
      expect(runnerStep.env).toBeDefined();
      expect(runnerStep.env.UNITY_ACCELERATOR_ENDPOINT).toBe(
        "${{ secrets.UNITY_ACCELERATOR_ENDPOINT }}"
      );
      expect(readWorkflow(workflow)).not.toContain("vars.UNITY_ACCELERATOR_ENDPOINT");
    }
  );

  test.each(UNITY_PROVISIONED_JOBS)(
    "$workflow job '$jobId' has ensure-editor provisioning diagnostics coverage",
    ({ workflow, jobId }) => {
      const parsed = loadWorkflowYaml(workflow);
      expectProvisioningDiagnosticsContract(parsed.jobs[jobId].steps);
    }
  );

  test("every active ensure-editor workflow job is covered by provisioning diagnostics shape tests", () => {
    const covered = new Set(
      UNITY_PROVISIONED_JOBS.map(({ workflow, jobId }) => `${workflow}:${jobId}`)
    );
    const uncovered = [];
    for (const workflow of fs.readdirSync(WORKFLOWS_DIR).filter((name) => /\.ya?ml$/i.test(name))) {
      const parsed = loadWorkflowYaml(workflow);
      for (const [jobId, job] of Object.entries(parsed.jobs || {})) {
        const hasEnsureEditor = (job.steps || []).some(
          (step) =>
            typeof step.run === "string" && step.run.includes("./scripts/unity/ensure-editor.ps1")
        );
        if (hasEnsureEditor && !covered.has(`${workflow}:${jobId}`)) {
          uncovered.push(`${workflow}:${jobId}`);
        }
      }
    }
    expect(uncovered).toEqual([]);
  });

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow job '$jobId' wraps direct Unity runner with org lock acquire/release and license preflight",
    ({ workflow, jobId }) => {
      const parsed = loadWorkflowYaml(workflow);
      const steps = parsed.jobs[jobId].steps;
      const acquireIndex = steps.findIndex(
        (step) =>
          step.uses ===
          "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1"
      );
      const preflightIndex = steps.findIndex(
        (step) => step.uses === "./.github/actions/validate-unity-license"
      );
      const provisionIndex = steps.findIndex(
        (step) =>
          step.name === "Provision Unity Editor" &&
          step.shell === "pwsh" &&
          typeof step.run === "string" &&
          step.run.includes("./scripts/unity/ensure-editor.ps1") &&
          step.run.includes("-CiManagedOnly") &&
          step.run.includes("-ProvisioningProfile") &&
          step.run.includes(
            "$diagnosticsFile = Join-Path $diagnosticsPath 'ensure-editor-summary.json'"
          ) &&
          step.run.includes("-DiagnosticsPath $diagnosticsFile") &&
          step.run.includes("New-Item -ItemType Directory -Force -Path $diagnosticsPath") &&
          step.run.includes("UNITY_EDITOR_PATH=$editor") &&
          step.run.includes("$env:GITHUB_ENV")
      );
      const runnerIndex = steps.findIndex(
        (step) =>
          step.name === "Run Unity Test Runner" &&
          step.shell === "pwsh" &&
          typeof step.run === "string" &&
          step.run.includes("./scripts/unity/run-ci-tests.ps1")
      );
      const releaseIndex = steps.findIndex(
        (step) =>
          step.uses ===
          "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1"
      );

      expect(acquireIndex).toBeGreaterThanOrEqual(0);
      expect(preflightIndex).toBeGreaterThanOrEqual(0);
      expect(provisionIndex).toBeGreaterThan(preflightIndex);
      expect(acquireIndex).toBeGreaterThan(provisionIndex);
      expect(runnerIndex).toBeGreaterThan(acquireIndex);
      expect(releaseIndex).toBeGreaterThan(runnerIndex);

      expect(steps[acquireIndex].with["lock-name"]).toBe("wallstop-organization-builds");
      expect(steps[releaseIndex].with["lock-name"]).toBe("wallstop-organization-builds");
      expect(steps[releaseIndex].with["holder-id-suffix"]).toBe(
        steps[acquireIndex].with["holder-id-suffix"]
      );
      expect(steps[releaseIndex].if).toBe("always()");
      expect(steps[acquireIndex].env.BUILD_LOCK_TOKEN).toBe("${{ secrets.ORG_BUILD_LOCK_TOKEN }}");
      expect(steps[releaseIndex].env.BUILD_LOCK_TOKEN).toBe("${{ secrets.ORG_BUILD_LOCK_TOKEN }}");
    }
  );

  // Timeout invariant: editor provisioning and the organization-lock poll wait
  // both charge against the job clock, so a job at the back of the serialized
  // queue is killed before its lock wait can finish unless
  //   job timeout-minutes >= provision timeout + acquire timeout + RUN_BUDGET(120).
  // The step-level run timeout (>= 120) protects the in-use seat from a hung
  // editor (the stuck-job-watchdog ignores any in_progress job) and must stay
  // strictly below the job timeout so the step fails first and releases the
  // lock instead of the whole job being cancelled.
  test.each(UNITY_LICENSED_JOBS)(
    "$workflow job '$jobId' job timeout covers acquire timeout + the 120m run budget",
    ({ workflow, jobId }) => {
      const parsed = loadWorkflowYaml(workflow);
      const job = parsed.jobs[jobId];
      const steps = job.steps;

      const acquireStep = steps.find(
        (step) =>
          step.uses ===
          "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1"
      );
      const provisionStep = steps.find((step) => step.name === "Provision Unity Editor");
      const runStep = steps.find((step) => step.name === "Run Unity Test Runner");

      expect(acquireStep).toBeDefined();
      expect(provisionStep).toBeDefined();
      expect(runStep).toBeDefined();

      const acquireTimeout = Number.parseInt(acquireStep.with["timeout-minutes"], 10);
      expect(Number.isInteger(acquireTimeout)).toBe(true);

      const provisionTimeout = provisionStep["timeout-minutes"];
      expect(Number.isInteger(provisionTimeout)).toBe(true);
      expect(provisionTimeout).toBeGreaterThanOrEqual(180);

      const jobTimeout = job["timeout-minutes"];
      expect(Number.isInteger(jobTimeout)).toBe(true);
      expect(jobTimeout).toBeGreaterThanOrEqual(provisionTimeout + acquireTimeout + 120);

      const runTimeout = runStep["timeout-minutes"];
      expect(Number.isInteger(runTimeout)).toBe(true);
      expect(runTimeout).toBeGreaterThanOrEqual(120);
      expect(runTimeout).toBeLessThan(jobTimeout);
    }
  );

  test.each(UNITY_PROVISIONED_JOBS)(
    "$workflow job '$jobId' provisioning timeout exceeds ensure-editor recovery budget",
    ({ workflow, jobId }) => {
      const installTimeoutSeconds = extractEnsureEditorDefault(
        "Get-EnsureEditorInstallTimeoutSeconds"
      );
      const retryAttempts = extractEnsureEditorDefault("Get-EnsureEditorInstallRetryAttempts");
      const retryDelaySeconds = extractEnsureEditorDefault("Get-EnsureEditorRetryDelaySeconds");
      const scriptRecoveryBudgetMinutes = Math.ceil(
        (installTimeoutSeconds * retryAttempts + retryDelaySeconds * (retryAttempts - 1)) / 60
      );

      const parsed = loadWorkflowYaml(workflow);
      const provisionStep = parsed.jobs[jobId].steps.find(
        (step) => step.name === "Provision Unity Editor"
      );

      expect(scriptRecoveryBudgetMinutes).toBeGreaterThan(0);
      expect(provisionStep["timeout-minutes"]).toBeGreaterThan(scriptRecoveryBudgetMinutes);
    }
  );

  test("unity-tests scopes editor provisioning by test mode", () => {
    expectProvisioningProfileForJob(
      "unity-tests.yml",
      "unity-tests",
      /if \('\$\{\{ matrix\.test-mode \}\}' -eq 'standalone'\)[\s\S]*StandaloneWindowsIl2Cpp[\s\S]*EditorOnly[\s\S]*-ProvisioningProfile \$provisioningProfile/
    );
  });

  test.each([
    ["unity-benchmarks.yml", "benchmarks"],
    ["release.yml", "unity-checks"]
  ])("%s job '%s' uses EditorOnly provisioning", (workflow, jobId) => {
    expectProvisioningProfileForJob(workflow, jobId, "EditorOnly");
  });

  test("GameCI experiment scopes editor provisioning by test mode", () => {
    expectProvisioningProfileForJob(
      GAMECI_EXPERIMENT_WORKFLOW,
      "game-ci-experiment",
      /if \('\$\{\{ inputs\.test-mode \}\}' -eq 'standalone'\)[\s\S]*StandaloneWindowsIl2Cpp[\s\S]*EditorOnly[\s\S]*-ProvisioningProfile \$provisioningProfile/
    );
  });

  test.each(UNITY_LICENSED_JOBS.filter((job) => job.requiresLibraryCache))(
    "$workflow declares an exact Library cache key with no broad restore-keys",
    ({ workflow }) => {
      expectExactUnityLibraryCache(readWorkflow(workflow));
    }
  );

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow uses actions/upload-artifact@v7 (matches repo baseline)",
    ({ workflow }) => {
      // unity-checks in release.yml does not currently upload its own
      // artifacts (the validate job does), so this assertion only applies
      // workflow-wide rather than per-job.
      const text = readWorkflow(workflow);
      if (workflow === "release.yml") {
        // release.yml uploads the packed npm artifact from the validate job.
        expect(text).toContain("actions/upload-artifact@v7");
      } else {
        expect(text).toContain("actions/upload-artifact@v7");
      }
    }
  );

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow runs required Unity work through the repo-owned direct runner",
    ({ workflow, runnerScript }) => {
      // Required CI owns Unity invocation directly on self-hosted Windows.
      // GameCI remains available only in the explicit non-required experiment
      // workflow because the Windows container path has repeatedly failed
      // before NUnit produced test results.
      const text = readWorkflow(workflow);
      expect(text).toContain(runnerScript);
      expect(text).not.toContain("uses: game-ci/unity-test-runner@v4");
      expect(text).not.toContain("-Runner docker");
    }
  );

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow does not rely on GameCI host-user artifact ownership",
    ({ workflow }) => {
      const text = readWorkflow(workflow);
      expect(text).not.toMatch(/runAsHostUser:/);
    }
  );

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow grants checks: write for Unity result annotations",
    ({ workflow, jobId }) => {
      const parsed = loadWorkflowYaml(workflow);
      if (workflow === "release.yml") {
        expect(parsed.jobs[jobId].permissions).toEqual({
          contents: "read",
          checks: "write"
        });
      } else {
        expect(parsed.permissions).toMatchObject({ checks: "write" });
      }
    }
  );

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow resolves its assembly list via the compute-unity-assemblies composite",
    ({ workflow }) => {
      // The duplicated inline `Compute test assembly list` pwsh steps were
      // extracted into a single composite action (single source of truth).
      // Every Unity workflow must reference it instead of re-implementing the
      // asmdef-discovery shell-out.
      const text = readWorkflow(workflow);
      expect(text).toContain("uses: ./.github/actions/compute-unity-assemblies");
    }
  );

  // Every required Unity job must use the shared verify composite so missing
  // or zero-test results are impossible to treat as green.
  test.each(UNITY_LICENSED_JOBS)(
    "$workflow validates tests actually ran via the verify-unity-results composite",
    ({ workflow }) => {
      const text = readWorkflow(workflow);
      expect(text).toContain("uses: ./.github/actions/verify-unity-results");
    }
  );

  // Pin the gating condition shape on every "Verify tests actually ran" step
  // across all workflows. A revert to `if: always()` would silently re-introduce
  // the race against user-initiated cancellations -- the verify step would run
  // even when the run was cancelled and emit a misleading "tests did not run"
  // error annotation. We forbid `always()` on every "Verify tests" step and
  // require the canonical `!cancelled()` shape on the REQUIRED Unity workflows
  // (unity-tests.yml, unity-benchmarks.yml, release.yml). The non-required
  // GameCI experiment uses a different `steps.<id>.outcome == 'success'` guard
  // by design (it is the only "Verify tests" step that intentionally skips on
  // an earlier step's failure), so we only require it not be `always()`.
  function collectVerifyTestsSteps() {
    const records = [];
    for (const workflow of fs.readdirSync(WORKFLOWS_DIR).filter((name) => /\.ya?ml$/i.test(name))) {
      const parsed = loadWorkflowYaml(workflow);
      for (const [jobId, job] of Object.entries(parsed.jobs || {})) {
        for (const step of job.steps || []) {
          if (typeof step.name === "string" && /verify\s+tests/i.test(step.name)) {
            records.push({ workflow, jobId, step });
          }
        }
      }
    }
    return records;
  }

  function isAlwaysCondition(ifValue) {
    if (typeof ifValue !== "string") {
      return false;
    }
    // Strip ${{ }} wrapper and ALL whitespace so we match `always()`,
    // `${{ always() }}`, `${{  always()  }}`, `${{ always () }}`, and any
    // future formatter that inserts spaces inside the call. This is purely a
    // robustness tweak -- the production workflows write `always()` exactly,
    // but yamlfmt / prettier could re-flow whitespace without changing
    // semantics and we don't want that to silently flip a guard test.
    const inner = ifValue
      .replace(/^\s*\$\{\{\s*/, "")
      .replace(/\s*\}\}\s*$/, "")
      .replace(/\s+/g, "");
    return inner === "always()";
  }

  function isCancelledGuard(ifValue) {
    if (typeof ifValue !== "string") {
      return false;
    }
    const inner = ifValue
      .replace(/^\s*\$\{\{\s*/, "")
      .replace(/\s*\}\}\s*$/, "")
      .replace(/\s+/g, "");
    return inner === "!cancelled()";
  }

  test("every 'Verify tests' step exists and was located by the scanner", () => {
    // ANTI-NO-OP: if a workflow rename causes the scan to silently turn up
    // nothing, the assertions below would vacuously pass. Pin the count to
    // catch that.
    const records = collectVerifyTestsSteps();
    expect(records.length).toBeGreaterThanOrEqual(3);
    const workflows = records.map((r) => r.workflow).sort();
    expect(workflows).toEqual(
      expect.arrayContaining(["release.yml", "unity-benchmarks.yml", "unity-tests.yml"])
    );
  });

  test("no 'Verify tests' step uses if: always() (catches revert to the racy gate)", () => {
    const records = collectVerifyTestsSteps();
    const offenders = records
      .filter(({ step }) => isAlwaysCondition(step.if))
      .map(({ workflow, jobId, step }) => `${workflow}:${jobId} -- if: ${step.if}`);
    expect(offenders).toEqual([]);
  });

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow 'Verify tests actually ran' step uses if: !cancelled() (not always(), not raw on/off)",
    ({ workflow }) => {
      const parsed = loadWorkflowYaml(workflow);
      const verifySteps = [];
      for (const job of Object.values(parsed.jobs || {})) {
        for (const step of job.steps || []) {
          if (typeof step.name === "string" && /verify\s+tests/i.test(step.name)) {
            verifySteps.push(step);
          }
        }
      }
      expect(verifySteps.length).toBeGreaterThan(0);
      for (const step of verifySteps) {
        expect(typeof step.if).toBe("string");
        expect(isAlwaysCondition(step.if)).toBe(false);
        expect(isCancelledGuard(step.if)).toBe(true);
      }
    }
  );

  // Canonical labels carried by both the verify-unity-results composite
  // action and scripts/unity/run-ci-tests.ps1's $script:CatastrophicPatterns
  // array. Both consumers must reference EVERY label so a Unity-side compile
  // catastrophe (PrecompiledAssemblyException, CompilationFailedException,
  // raw `error CS####` lines, the warning CS8032 analyzer-load-failure) gets
  // a top-of-summary `::error::` annotation regardless of which call site
  // fires first. Keeping this list co-located with the assertions keeps
  // catastrophic-pattern drift a one-line edit instead of a silent omission.
  const CATASTROPHIC_PATTERN_LABELS = [
    "PrecompiledAssemblyException",
    "CompilationFailedException",
    "Multiple precompiled assemblies with the same name",
    "error CS\\d+",
    "warning CS8032"
  ];

  test("verify-unity-results composite action carries the load-bearing guard logic", () => {
    // Pin the actual guard logic to the composite action file so it cannot be
    // hollowed out during a future refactor. This is the single source of
    // truth for the "0 tests ran" / silent-green guard.
    const actionPath = path.join(ACTIONS_DIR, "verify-unity-results", "action.yml");
    expect(fs.existsSync(actionPath)).toBe(true);
    const text = fs.readFileSync(actionPath, "utf8");
    expect(text).toContain("<test-run");
    expect(text).toMatch(/\$total\s*=\s*\[int\]/);
    expect(text).toMatch(/\$failed\s*=\s*\[int\]/);
    expect(text).toContain("$total -lt 1");
    expect(text).toContain("$failed -gt 0");
    expect(text).toContain("Provisioning diagnostics files");
    // Composite run: steps on self-hosted Windows MUST set shell: pwsh.
    expect(text).toMatch(/shell:\s*pwsh/);

    // Catastrophic-pattern helper must exist and be wired into the step;
    // a refactor that drops the helper would silently lose top-of-summary
    // `::error::` annotations for compile-time-catastrophe failures.
    expect(text).toContain("Write-UnityCatastrophicPatternHits");
    // Every canonical label must appear in the action.yml text (data drift
    // catch: if someone shrinks the pattern set in the action without
    // shrinking the runner script's set, the silent-killer guard becomes
    // half-blind).
    for (const label of CATASTROPHIC_PATTERN_LABELS) {
      expect(text).toContain(label);
    }
  });

  test("catastrophic patterns stay in sync between run-ci-tests.ps1, verify-unity-results/action.yml, and dump-unity-log-tail/action.yml", () => {
    // All THREE consumers MUST carry the same label set so a compile-time
    // catastrophe produces identical top-of-summary `::error::` annotations
    // regardless of which call site fires first:
    //   1) runner script during the Unity invocation
    //   2) verify-unity-results AFTER the run on the happy/failure path
    //   3) dump-unity-log-tail on failure() OR cancelled() (covers the
    //      Unity-2022-csc-hang scenario where the verify step is skipped
    //      because the cancel set the job state to cancelled).
    // Drift in any direction = half-blind diagnostics.
    const runnerPath = path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1");
    const verifyActionPath = path.join(ACTIONS_DIR, "verify-unity-results", "action.yml");
    const dumpActionPath = path.join(ACTIONS_DIR, "dump-unity-log-tail", "action.yml");
    expect(fs.existsSync(runnerPath)).toBe(true);
    expect(fs.existsSync(verifyActionPath)).toBe(true);
    expect(fs.existsSync(dumpActionPath)).toBe(true);

    const runnerText = fs.readFileSync(runnerPath, "utf8");
    const verifyActionText = fs.readFileSync(verifyActionPath, "utf8");
    const dumpActionText = fs.readFileSync(dumpActionPath, "utf8");

    // Heuristic co-presence check: ALL files must contain every canonical
    // label string. We deliberately do not try to parse the PowerShell
    // array literal (fragile across PS formatting) -- the label strings
    // appear verbatim in all three files inside `Label = '<text>'` (or
    // YAML's Label = '<text>') and inside the surrounding comment blocks,
    // so a string-contains check catches drift without requiring a parser.
    for (const label of CATASTROPHIC_PATTERN_LABELS) {
      expect(runnerText.includes(label)).toBe(true);
      expect(verifyActionText.includes(label)).toBe(true);
      expect(dumpActionText.includes(label)).toBe(true);
    }
  });

  // Pin the dump-unity-log-tail composite shape: it MUST exist, be a pwsh
  // composite, dump unity.log via Get-Content -Tail, and carry every
  // catastrophic-pattern label (verified by the co-sync test above too).
  // The `if: failure() || cancelled()` wiring lives on the CALLER (each
  // Unity workflow), not on this action; we pin the wiring separately in
  // the per-workflow "wires the dump-unity-log-tail step on failure or
  // cancellation" tests below.
  test("dump-unity-log-tail composite action carries the load-bearing diagnostic logic", () => {
    const actionPath = path.join(ACTIONS_DIR, "dump-unity-log-tail", "action.yml");
    expect(fs.existsSync(actionPath)).toBe(true);
    const text = fs.readFileSync(actionPath, "utf8");
    // Composite run: steps on self-hosted Windows MUST set shell: pwsh.
    expect(text).toMatch(/shell:\s*pwsh/);
    // The action MUST read unity.log from the supplied results-dir.
    expect(text).toContain("unity.log");
    // The action MUST emit a tail via Get-Content -Tail (the load-bearing
    // diagnostic so the operator has SOMETHING to look at on cancel).
    expect(text).toMatch(/Get-Content[^|]*-Tail/);
    // Best-effort posture: the action must NOT throw (operator's upstream
    // failure must remain the root cause, not a diagnostic helper crash).
    expect(text).toContain("results-dir");
    expect(text).toContain("label");
  });

  // The dump-unity-log-tail step MUST be wired with if: failure() ||
  // cancelled() on every required Unity workflow, BEFORE the verify step
  // (so its log-tail output appears above the verify step's annotation in
  // the GitHub summary).
  test.each(UNITY_LICENSED_JOBS)(
    "$workflow wires the dump-unity-log-tail step on failure() or cancelled() BEFORE the verify step",
    ({ workflow, jobId }) => {
      const parsed = loadWorkflowYaml(workflow);
      const steps = parsed.jobs[jobId].steps;

      const dumpIndex = steps.findIndex(
        (step) => step.uses === "./.github/actions/dump-unity-log-tail"
      );
      const verifyIndex = steps.findIndex(
        (step) => step.uses === "./.github/actions/verify-unity-results"
      );

      expect(dumpIndex).toBeGreaterThanOrEqual(0);
      expect(verifyIndex).toBeGreaterThanOrEqual(0);
      // The dump step provides catastrophic-pattern hits on cancel; placing
      // it BEFORE the verify step means the operator sees them above the
      // verify-step output in the GitHub summary.
      expect(dumpIndex).toBeLessThan(verifyIndex);

      const dumpStep = steps[dumpIndex];
      // The `if:` MUST include both failure() AND cancelled() so the step
      // runs on EITHER a step-timeout/red-test scenario (failure) OR a
      // user-initiated cancel.
      const condition = String(dumpStep.if || "")
        .replace(/^\s*\$\{\{\s*/, "")
        .replace(/\s*\}\}\s*$/, "")
        .replace(/\s+/g, "");
      expect(condition).toBe("failure()||cancelled()");
    }
  );

  test("compute-unity-assemblies composite action shells out to asmdef-discovery", () => {
    // Pin the single-source assembly resolution to the composite action file.
    const actionPath = path.join(ACTIONS_DIR, "compute-unity-assemblies", "action.yml");
    expect(fs.existsSync(actionPath)).toBe(true);
    const text = fs.readFileSync(actionPath, "utf8");
    expect(text).toContain("asmdef-discovery");
    expect(text).toContain("DXM_TEST_ASSEMBLIES");
    // Composite run: steps on self-hosted Windows MUST set shell: pwsh.
    expect(text).toMatch(/shell:\s*pwsh/);
  });

  test("validate-unity-license composite validates classic SERIAL activation (post-migration)", () => {
    // The directory name is retained, but the action validates classic SERIAL
    // activation: it requires UNITY_SERIAL/UNITY_EMAIL/UNITY_PASSWORD, ERRORS
    // when the retired UNITY_LICENSING_SERVER is still set (no silent fallback),
    // and prints a "preflight passed" notice. The OLD floating-server strings
    // must be gone.
    const actionPath = path.join(ACTIONS_DIR, "validate-unity-license", "action.yml");
    expect(fs.existsSync(actionPath)).toBe(true);
    const text = fs.readFileSync(actionPath, "utf8");
    expect(text).toContain("UNITY_SERIAL");
    expect(text).toContain("UNITY_EMAIL");
    expect(text).toContain("UNITY_PASSWORD");
    expect(text).toContain("Unity serial-activation preflight passed");
    // The retired licensing-server hard-fail (no silent fallback to the server).
    expect(text).toContain("UNITY_LICENSING_SERVER");
    expect(text).toMatch(/UNITY_LICENSING_SERVER is set but retired/);
    expect(text).toMatch(/shell:\s*pwsh/);
    // The OLD floating-license-server branch logic must NOT linger.
    expect(text).not.toContain("URL shape");
    expect(text).not.toContain("--acquire-floating");
    expect(text).not.toContain("Unity licensing server preflight passed");
  });

  test("return-unity-license composite exists and is a pwsh composite that runs -returnlicense", () => {
    // Pin the seat-return backstop action so a refactor cannot hollow it out: it
    // must be a composite running pwsh and actually return the seat via
    // `Unity.exe -returnlicense` (classic serial activation).
    const actionPath = path.join(ACTIONS_DIR, "return-unity-license", "action.yml");
    expect(fs.existsSync(actionPath)).toBe(true);
    const parsed = yaml.parse(fs.readFileSync(actionPath, "utf8"));
    expect(parsed.runs.using).toBe("composite");
    const text = fs.readFileSync(actionPath, "utf8");
    expect(text).toMatch(/shell:\s*pwsh/);
    expect(text).toContain("-returnlicense");
    // The old floating-license-client return surface must be GONE.
    expect(text).not.toContain("--return-floating");
  });

  test("composite action input defaults do not reference workflow env context", () => {
    const actionFiles = collectActionMetadataFiles();

    expect(actionFiles.length).toBeGreaterThan(0);
    for (const actionPath of actionFiles) {
      const parsed = yaml.parse(readActionYamlForDiagnostics(actionPath));
      if (!parsed || !parsed.inputs) {
        continue;
      }

      for (const [inputName, input] of Object.entries(parsed.inputs)) {
        if (
          !input ||
          typeof input !== "object" ||
          !Object.prototype.hasOwnProperty.call(input, "default")
        ) {
          continue;
        }
        expect(String(input.default)).not.toMatch(/\$\{\{\s*env\./);
      }
    }
  });

  test.each(UNITY_LICENSED_JOBS)(
    "$workflow job '$jobId' returns the seat via an if: always() step after the run and before the org-lock release",
    ({ workflow, jobId }) => {
      const parsed = loadWorkflowYaml(workflow);
      const steps = parsed.jobs[jobId].steps;

      const runnerIndex = steps.findIndex(
        (step) =>
          step.name === "Run Unity Test Runner" &&
          typeof step.run === "string" &&
          step.run.includes("./scripts/unity/run-ci-tests.ps1")
      );
      const returnIndex = steps.findIndex(
        (step) => step.uses === "./.github/actions/return-unity-license"
      );
      const releaseIndex = steps.findIndex(
        (step) =>
          step.uses ===
          "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1"
      );

      expect(runnerIndex).toBeGreaterThanOrEqual(0);
      expect(returnIndex).toBeGreaterThanOrEqual(0);
      expect(releaseIndex).toBeGreaterThanOrEqual(0);

      // The return step is the if:always() backstop for a killed editor; it must
      // sit AFTER the Unity run and BEFORE the org-lock release.
      expect(returnIndex).toBeGreaterThan(runnerIndex);
      expect(returnIndex).toBeLessThan(releaseIndex);
      expect(steps[returnIndex].if).toBe("always()");
    }
  );

  test.each(UNITY_LICENSED_JOBS.filter((job) => job.requiresProtectedBranchGuard))(
    "$workflow job '$jobId' guards same-repo + protected-branch execution",
    ({ workflow, jobId }) => {
      const parsed = loadWorkflowYaml(workflow);
      const text = readWorkflow(workflow);
      expectSameRepoAndProtectedBranchGuard(parsed.jobs[jobId]);
      expect(text).not.toContain("pull_request_target");
    }
  );
});

describe("print-self-hosted-runner-diagnostics composite action", () => {
  let action;
  let steps;

  beforeAll(() => {
    action = loadDiagnosticsAction();
    expect(action).toBeDefined();
    expect(action.runs).toBeDefined();
    expect(action.runs.using).toBe("composite");
    steps = action.runs.steps;
    expect(Array.isArray(steps)).toBe(true);
  });

  test("first step is a Windows PowerShell 5.1 pwsh preflight that fails fast", () => {
    // The self-hosted Windows Unity runners only ship Windows PowerShell 5.1
    // (`powershell`) until an operator installs PowerShell 7 (`pwsh`). The
    // remaining step here (and the Unity jobs that consume this action) use
    // `shell: pwsh`, so a missing pwsh would otherwise surface only as the
    // cryptic "##[error]pwsh: command not found". The FIRST step must run in
    // the always-present Windows PowerShell 5.1 and fail fast with a clear,
    // runbook-pointing error. See docs/runbooks/unity-runners-after-transfer.md.
    expect(steps.length).toBeGreaterThanOrEqual(2);

    const preflight = steps[0];
    expect(preflight).toBeDefined();
    // shell:powershell is Windows PowerShell 5.1, which is always present on
    // Windows runners — so this step runs even when pwsh is absent.
    expect(String(preflight.shell).toLowerCase()).toBe("powershell");
    expect(typeof preflight.run).toBe("string");
    // The preflight detects pwsh via Get-Command and exits non-zero when it
    // is missing.
    expect(preflight.run).toContain("Get-Command");
    expect(preflight.run).toContain("pwsh");
    expect(preflight.run).toContain("exit 1");
  });

  test("second step is the existing pwsh diagnostics emitter", () => {
    const diagnostics = steps[1];
    expect(diagnostics).toBeDefined();
    expect(diagnostics.name).toBe("Emit runner diagnostics");
    expect(String(diagnostics.shell).toLowerCase()).toBe("pwsh");
  });
});

describe(".github/workflows/unity-tests.yml", () => {
  let text;
  let parsed;

  beforeAll(() => {
    text = readWorkflow("unity-tests.yml");
    parsed = loadWorkflowYaml("unity-tests.yml");
  });

  test("runs for same-repo PRs, protected branch pushes, schedules, and dispatch", () => {
    const onBlock = getOnBlock(parsed);
    expect(Object.keys(onBlock).sort()).toEqual([
      "pull_request",
      "push",
      "schedule",
      "workflow_dispatch"
    ]);
    expect(text).not.toContain("pull_request_target");
  });

  test("uses the full Unity version x test mode matrix incl. standalone", () => {
    // The version list is no longer hardcoded here: matrix-config reads the
    // canonical single source (.github/unity-versions.json) at runtime via jq,
    // so the workflow must carry ZERO version literals and instead read the
    // file. See scripts/validate-unity-versions.js (no-literals policy).
    for (const unityVersion of UNITY_VERSIONS) {
      expect(text).not.toContain(unityVersion);
    }
    expect(text).toContain("jq -c '.all' .github/unity-versions.json");
    expect(text).toContain("uses: actions/checkout@v6");
    expect(text).toContain('modes=\'["editmode","playmode","standalone"]\'');
    // workflow_dispatch test-mode choice must offer standalone too.
    expect(text).toContain("- standalone");
  });

  test("standalone is handled by the direct Unity runner, not a separate builder", () => {
    expect(text).toContain("-TestMode '${{ matrix.test-mode }}'");
    expect(text).not.toContain("buildMethod");
    expect(text).not.toContain("BuildIL2CPPTestPlayer");
    expect(text).not.toContain("game-ci/unity-builder@v4");
  });

  test("standalone uses the runtime-only assembly list and direct Windows player path", () => {
    // EditMode tests cannot run inside a player, so standalone passes
    // runtime-only: true to the compute-unity-assemblies composite. The direct
    // runner maps standalone to StandaloneWindows64 and configures IL2CPP in
    // the generated project.
    expect(text).toMatch(
      /runtime-only:\s*"\$\{\{ matrix\.test-mode == 'standalone' && 'true' \|\| 'false' \}\}"/
    );
    expect(text).not.toMatch(/^\s*targetPlatform:/m);
    expect(text).not.toMatch(/customImage:/);
  });
});

describe(".github/workflows/unity-gameci-experiment.yml", () => {
  let text;
  let parsed;

  beforeAll(() => {
    text = readWorkflow(GAMECI_EXPERIMENT_WORKFLOW);
    parsed = loadWorkflowYaml(GAMECI_EXPERIMENT_WORKFLOW);
  });

  test("is non-required by construction", () => {
    const onBlock = getOnBlock(parsed);
    expect(Object.keys(onBlock).sort()).toEqual(["workflow_dispatch"]);
    expect(text).not.toContain("pull_request");
    expect(text).not.toContain("push:");
    expect(parsed.jobs["game-ci-experiment"]["continue-on-error"]).toBe(true);
  });

  test("uses GameCI only in normal project mode against the generated project", () => {
    expect(text).toContain("uses: game-ci/unity-test-runner@v4");
    expect(text).toContain("packageMode: false");
    expect(text).toContain("-GenerateOnly");
    expect(text).toContain(".artifacts/unity/game-ci-projects/");
    expect(text).not.toContain("packageMode: true");
    expect(text).not.toContain("projectPath: .unity-test-project");
  });

  test("job timeout covers provision + acquire timeout + the 120m run budget on the GameCI run step", () => {
    // The non-required GameCI experiment also acquires the shared Unity seat,
    // so it obeys the same timeout invariant: job >= acquire + RUN_BUDGET(120)
    // and a step-level guard >= 120 and strictly below the job timeout.
    const job = parsed.jobs["game-ci-experiment"];
    const steps = job.steps;

    const acquireStep = steps.find(
      (step) =>
        step.uses ===
        "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1"
    );
    const provisionStep = steps.find((step) => step.name === "Provision Unity Editor");
    const runStep = steps.find((step) => step.name === "Run GameCI normal project mode");

    expect(acquireStep).toBeDefined();
    expect(provisionStep).toBeDefined();
    expect(runStep).toBeDefined();

    const acquireTimeout = Number.parseInt(acquireStep.with["timeout-minutes"], 10);
    expect(Number.isInteger(acquireTimeout)).toBe(true);

    const provisionTimeout = provisionStep["timeout-minutes"];
    expect(Number.isInteger(provisionTimeout)).toBe(true);
    expect(provisionTimeout).toBeGreaterThanOrEqual(180);

    const jobTimeout = job["timeout-minutes"];
    expect(Number.isInteger(jobTimeout)).toBe(true);
    expect(jobTimeout).toBeGreaterThanOrEqual(provisionTimeout + acquireTimeout + 120);

    const runTimeout = runStep["timeout-minutes"];
    expect(Number.isInteger(runTimeout)).toBe(true);
    expect(runTimeout).toBeGreaterThanOrEqual(120);
    expect(runTimeout).toBeLessThan(jobTimeout);
  });
});

describe(".github/workflows/unity-benchmarks.yml", () => {
  let text;
  let parsed;

  beforeAll(() => {
    text = readWorkflow("unity-benchmarks.yml");
    parsed = loadWorkflowYaml("unity-benchmarks.yml");
  });

  test("`on:` block has ONLY schedule and workflow_dispatch", () => {
    // YAML 1.1 turns the bare `on` key into `true`; check both keys to
    // tolerate either representation across yaml/parser versions.
    const onBlock = getOnBlock(parsed);
    expect(onBlock).toBeDefined();
    expect(typeof onBlock).toBe("object");

    const triggerKeys = Object.keys(onBlock).sort();
    expect(triggerKeys).toEqual(["schedule", "workflow_dispatch"]);

    // Belt-and-suspenders text grep: a stray `pull_request:` or `push:`
    // anywhere in the on: block (even commented-out or otherwise missed
    // by the structural walk above) should not appear at column-2
    // indentation.
    const onLineMatch = text.match(/^on:[\s\S]*?(?=^\w)/m);
    expect(onLineMatch).not.toBeNull();
    const onSection = onLineMatch[0];
    expect(onSection).not.toMatch(/^\s{2}pull_request:/m);
    expect(onSection).not.toMatch(/^\s{2}push:/m);
  });

  test("opts the assembly computation into perf assemblies", () => {
    // Benchmarks must include the perf (Benchmarks/Allocations) assemblies.
    // The migrated workflow now opts in by passing include-perf: "true" to the
    // shared compute-unity-assemblies composite (which forwards
    // { includePerf: true } to asmdef-discovery) rather than computing it
    // inline or calling run-tests.ps1.
    expect(text).toContain("uses: ./.github/actions/compute-unity-assemblies");
    expect(text).toContain('include-perf: "true"');
    expect(text).not.toContain("pull_request_target");
  });
});

// The documented dispatch-throughput numbers are now owned entirely by the
// per-PR Performance Numbers workflow (.github/workflows/perf-numbers.yml).
// unity-benchmarks.yml no longer maintains docs/architecture/performance.md, so
// its old update-perf-doc job (and the per-version success markers that gated
// it) were removed. Pin that removal so a revert that re-introduces a SECOND
// doc-maintenance mechanism (two writers racing the same AUTOGENERATED region)
// fails here.
describe(".github/workflows/unity-benchmarks.yml no longer maintains the perf doc", () => {
  let text;
  let parsed;
  let benchmarksJob;

  beforeAll(() => {
    text = readWorkflow("unity-benchmarks.yml");
    parsed = loadWorkflowYaml("unity-benchmarks.yml");
    benchmarksJob = parsed.jobs.benchmarks;
  });

  test("the update-perf-doc job and its success markers are gone", () => {
    expect(parsed.jobs).not.toHaveProperty("update-perf-doc");
    // The render/commit machinery and the marker steps that only existed to
    // gate it must be gone too.
    expect(text).not.toContain("render-perf-doc.js");
    expect(text).not.toContain("git-auto-commit-action");
    expect(text).not.toContain("Mark latest-version benchmark success");
    expect(text).not.toContain("perf-latest-ok");
    // The single source of the doc table now lives in the per-PR workflow.
    expect(fs.existsSync(path.join(WORKFLOWS_DIR, "perf-numbers.yml"))).toBe(true);
  });

  test("top-level permissions stay least-privilege with no doc-PR write scope", () => {
    // With the docs-PR writer gone, the workflow keeps only the read +
    // annotation + benchmark-failure-issue scopes; nothing grants write.
    expect(parsed.permissions).toEqual({
      contents: "read",
      checks: "write",
      issues: "write"
    });
    expect(parsed.permissions).not.toHaveProperty("pull-requests");
    expect(parsed.permissions.contents).toBe("read");
    // No job grants contents:write or pull-requests:write anymore.
    for (const job of Object.values(parsed.jobs)) {
      if (!job.permissions) {
        continue;
      }
      expect(job.permissions.contents).not.toBe("write");
      expect(job.permissions).not.toHaveProperty("pull-requests");
    }
    // benchmarks declares no job-level permissions, so it inherits the
    // read-only top level.
    expect(benchmarksJob.permissions).toBeUndefined();
  });
});

// EFFECTIVE-behavior contract for the per-PR Performance Numbers workflow. The
// owner decision: CI regenerates the dispatch-throughput numbers on ELI-MACHINE
// only (the `fast` label), at the LATEST Unity version only, on every
// pull_request change to master/main. On pull_request it posts a non-blocking
// sticky PR comment (never pushing to the contributor branch -- that would block
// merge); on push to master it commits the refreshed doc. These assertions parse
// the YAML and check EFFECTIVE
// behavior (pinned `fast` label, latest-version-only matrix, license preflight,
// org-lock acquire+release, if:always() return-license inside the lock window,
// same-repo gate, loop-guard sentinel) rather than brittle string proxies.
describe(".github/workflows/perf-numbers.yml CI-owned dispatch-throughput numbers", () => {
  const PERF_AUTOUPDATE_SENTINEL = "[perf-autoupdate]";
  let text;
  let parsed;
  let preflightJob;
  let loopGuardJob;
  let perfJob;
  let commentJob;
  let commitJob;
  let perfSteps;

  beforeAll(() => {
    text = readWorkflow("perf-numbers.yml");
    parsed = loadWorkflowYaml("perf-numbers.yml");
    preflightJob = parsed.jobs["runner-preflight"];
    loopGuardJob = parsed.jobs["loop-guard"];
    perfJob = parsed.jobs["perf-benchmarks"];
    commentJob = parsed.jobs["comment-perf-doc"];
    commitJob = parsed.jobs["commit-perf-doc"];
    perfSteps = perfJob.steps;
  });

  test("triggers on pull_request (each change), push to master/main, plus dispatch", () => {
    const onBlock = getOnBlock(parsed);
    // PR events drive the non-blocking comment; push (post-merge) drives the
    // master doc commit; dispatch for manual runs.
    expect(Object.keys(onBlock).sort()).toEqual(["pull_request", "push", "workflow_dispatch"]);
    expect(onBlock.pull_request.branches.sort()).toEqual(["main", "master"]);
    // Each change to the PR re-runs the numbers: opened + synchronize + reopened.
    expect(onBlock.pull_request.types.sort()).toEqual(["opened", "reopened", "synchronize"]);
    expect(onBlock.push.branches.sort()).toEqual(["main", "master"]);
    expect(text).not.toContain("pull_request_target");
  });

  test("top-level permissions are least-privilege; write scopes are per-job and minimal", () => {
    expect(parsed.permissions).toEqual({ contents: "read", checks: "write" });
    // The licensed Unity job must NOT inherit or declare write scope.
    expect(perfJob.permissions).toBeUndefined();
    // The PR-comment job gets pull-requests:write only (it never pushes); the
    // master-commit job gets contents:write only (it never comments).
    expect(commentJob.permissions).toEqual({ contents: "read", "pull-requests": "write" });
    expect(commitJob.permissions).toEqual({ contents: "write" });
  });

  test("a runner-preflight gates the self-hosted job and requires the fast (ELI-MACHINE) label set", () => {
    // Preflight runs on a hosted runner and probes the runners endpoint.
    expect(preflightJob["runs-on"]).toBe("ubuntu-latest");
    const probe = preflightJob.steps.find(
      (step) => typeof step.run === "string" && /actions\/runners\b/.test(step.run)
    );
    expect(probe).toBeDefined();
    // The required label set pins ELI-MACHINE specifically (it carries `fast`).
    expect(probe.env.REQUIRED_LABELS).toBe("self-hosted,Windows,RAM-64GB,fast");
    // The self-hosted perf job depends on the preflight so a missing ELI-MACHINE
    // fails fast instead of queuing forever.
    expect(perfJob.needs).toEqual(expect.arrayContaining(["runner-preflight"]));
  });

  test("the Unity run is pinned to ELI-MACHINE via the fast label", () => {
    // runs-on must carry `fast` so only ELI-MACHINE (the sole fast runner) picks
    // up the job, and the label set must be the allowlisted fast set.
    expect(perfJob["runs-on"]).toEqual(["self-hosted", "Windows", "RAM-64GB", "fast"]);
  });

  test("the matrix is LATEST-version only and runs BOTH editmode and playmode legs", () => {
    const matrix = perfJob.strategy.matrix;
    // The single latest version is resolved from the canonical source
    // (.github/unity-versions.json) via loop-guard, so the matrix references the
    // loop-guard output rather than a hardcoded literal (no version can drift).
    expect(matrix["unity-version"]).toBe(
      "${{ fromJSON(needs.loop-guard.outputs.unity-versions) }}"
    );
    // No historical version literals remain in the workflow text.
    expect(text).not.toContain("2021.3.45f1");
    expect(text).not.toContain("2022.3.45f1");
    expect(text).not.toContain("6000.3.16f1");
    // loop-guard resolves the latest entry of `all` from the canonical file and
    // emits both the scalar latest-version and the single-element matrix list.
    expect(loopGuardJob.outputs["latest-version"]).toBe(
      "${{ steps.versions.outputs.latest-version }}"
    );
    expect(loopGuardJob.outputs["unity-versions"]).toBe(
      "${{ steps.versions.outputs.unity-versions }}"
    );
    const versionsStep = loopGuardJob.steps.find((step) => step.id === "versions");
    expect(versionsStep).toBeDefined();
    expect(versionsStep.run).toContain("jq -r '.all[-1]' \"${file}\"");
    expect(versionsStep.run).toContain("jq -c '[.all[-1]]' \"${file}\"");
    // render-perf-doc.js consumes both legs, so both must run.
    expect(matrix["test-mode"].sort()).toEqual(["editmode", "playmode"]);
    // One seat, serialized within the run.
    expect(perfJob.strategy["max-parallel"]).toBe(1);
  });

  test("license preflight + provisioning + org-lock acquire/run/return/release are correctly ordered", () => {
    const preflightIndex = perfSteps.findIndex(
      (step) => step.uses === "./.github/actions/validate-unity-license"
    );
    const provisionIndex = perfSteps.findIndex(
      (step) =>
        step.name === "Provision Unity Editor" &&
        step.shell === "pwsh" &&
        typeof step.run === "string" &&
        step.run.includes("./scripts/unity/ensure-editor.ps1") &&
        step.run.includes("-CiManagedOnly") &&
        step.run.includes("-ProvisioningProfile EditorOnly")
    );
    const acquireIndex = perfSteps.findIndex(
      (step) =>
        step.uses ===
        "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1"
    );
    const runIndex = perfSteps.findIndex(
      (step) =>
        step.name === "Run Unity Test Runner" &&
        step.shell === "pwsh" &&
        typeof step.run === "string" &&
        step.run.includes("./scripts/unity/run-ci-tests.ps1")
    );
    const returnIndex = perfSteps.findIndex(
      (step) => step.uses === "./.github/actions/return-unity-license"
    );
    const releaseIndex = perfSteps.findIndex(
      (step) =>
        step.uses ===
        "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1"
    );

    // license preflight -> provision (OUTSIDE the lock) -> acquire -> run ->
    // return (if:always(), INSIDE the lock window) -> release (if:always()).
    expect(preflightIndex).toBeGreaterThanOrEqual(0);
    expect(provisionIndex).toBeGreaterThan(preflightIndex);
    expect(acquireIndex).toBeGreaterThan(provisionIndex);
    expect(runIndex).toBeGreaterThan(acquireIndex);
    expect(returnIndex).toBeGreaterThan(runIndex);
    expect(releaseIndex).toBeGreaterThan(returnIndex);

    // The return-license is the if:always() backstop INSIDE the lock window
    // (after the run, before the release).
    expect(perfSteps[returnIndex].if).toBe("always()");
    expect(perfSteps[returnIndex].env.UNITY_EMAIL).toBe("${{ secrets.UNITY_EMAIL }}");
    expect(perfSteps[returnIndex].env.UNITY_PASSWORD).toBe("${{ secrets.UNITY_PASSWORD }}");
    expect(perfSteps[releaseIndex].if).toBe("always()");

    // The lock acquire/release use the central org lock with matching holder ids.
    expect(perfSteps[acquireIndex].with["lock-name"]).toBe("wallstop-organization-builds");
    expect(perfSteps[releaseIndex].with["lock-name"]).toBe("wallstop-organization-builds");
    expect(perfSteps[releaseIndex].with["holder-id-suffix"]).toBe(
      perfSteps[acquireIndex].with["holder-id-suffix"]
    );

    // Perf opts into the Benchmarks/Allocations assemblies, like unity-benchmarks.
    expect(text).toContain("uses: ./.github/actions/compute-unity-assemblies");
    expect(text).toContain('include-perf: "true"');
  });

  test("the Unity run uses pwsh and serial-activation secrets, never the retired server secret", () => {
    const runStep = perfSteps.find((step) => step.name === "Run Unity Test Runner");
    expect(runStep.shell).toBe("pwsh");
    expect(text).toMatch(/secrets\.UNITY_SERIAL\b/);
    expect(text).toMatch(/secrets\.UNITY_EMAIL\b/);
    expect(text).toMatch(/secrets\.UNITY_PASSWORD\b/);
    expect(text).not.toMatch(/secrets\.UNITY_LICENSING_SERVER\b/);
  });

  test("the licensed Unity job is gated to same-repo PRs", () => {
    // Forks cannot run licensed Unity or be pushed to. Mirror unity-tests.yml's
    // same-repo expression.
    expect(perfJob.if).toContain(
      "github.event.pull_request.head.repo.full_name == github.repository"
    );
    expect(perfJob.if).toContain("github.event_name != 'pull_request'");
  });

  test("a loop-guard reads the head commit for the perf-autoupdate sentinel and skips the expensive run", () => {
    // The loop-guard job inspects the PR head commit message and emits a
    // should-run output.
    expect(loopGuardJob["runs-on"]).toBe("ubuntu-latest");
    expect(loopGuardJob.outputs["should-run"]).toContain("should-run");
    const guardStep = loopGuardJob.steps.find((step) => step.id === "guard");
    expect(guardStep).toBeDefined();
    // It reads the head commit message and checks for the sentinel.
    expect(guardStep.run).toContain("git log -1 --pretty=%B");
    expect(guardStep.env.PERF_AUTOUPDATE_SENTINEL).toBe(PERF_AUTOUPDATE_SENTINEL);
    // The expensive Unity run is gated on should-run == 'true'.
    expect(perfJob.needs).toEqual(expect.arrayContaining(["loop-guard"]));
    expect(perfJob.if).toContain("needs.loop-guard.outputs.should-run == 'true'");
    // The commit job is likewise gated on the loop guard.
    expect(commitJob.if).toContain("needs.loop-guard.outputs.should-run == 'true'");
  });

  test("the bot commit carries the sentinel and is NOT [skip ci]", () => {
    const commitStep = commitJob.steps.find(
      (step) =>
        typeof step.uses === "string" &&
        step.uses.startsWith("stefanzweifel/git-auto-commit-action@")
    );
    expect(commitStep).toBeDefined();
    // The commit message must carry the loop-breaking sentinel.
    expect(commitStep.with.commit_message).toContain(PERF_AUTOUPDATE_SENTINEL);
    // The commit message must NOT use [skip ci] (that would skip required checks
    // on the final, numbers-fresh commit). Scoped to the commit_message, not the
    // whole file, because the loop-guard comment legitimately MENTIONS [skip ci]
    // to explain why it is deliberately avoided.
    expect(commitStep.with.commit_message).not.toContain("skip ci");
  });

  test("the PR-comment job posts a NON-BLOCKING sticky comment and NEVER pushes to the PR branch", () => {
    // Merge-safe path: on pull_request events, render the numbers and post a sticky
    // PR comment. It must NOT push onto the contributor's branch -- a GITHUB_TOKEN
    // push does not re-trigger the required pull_request checks, so the new head
    // would have no successful required checks and merge would be blocked.
    expect(commentJob.if).toContain("github.event_name == 'pull_request'");
    expect(commentJob.if).toContain(
      "github.event.pull_request.head.repo.full_name == github.repository"
    );
    expect(commentJob.if).toContain("needs.perf-benchmarks.result == 'success'");

    // Renders into the working tree (no commit), prettier --write, then creates
    // or updates a sticky PR comment via github-script.
    const render = commentJob.steps.find((step) => step.id === "render");
    expect(render).toBeDefined();
    expect(render.run).toContain("node scripts/unity/render-perf-doc.js");
    expect(render.run).toContain("node scripts/run-managed-prettier.js --write");
    const comment = commentJob.steps.find(
      (step) => typeof step.uses === "string" && step.uses.startsWith("actions/github-script@")
    );
    expect(comment).toBeDefined();
    expect(comment.with.script).toContain("issues.createComment");
    expect(comment.with.script).toContain("issues.updateComment");
    // It must NOT push to the PR branch (no git-auto-commit, no head.ref checkout).
    const commentText = JSON.stringify(commentJob);
    expect(commentText).not.toContain("git-auto-commit-action");
    expect(commentText).not.toContain("github.event.pull_request.head.ref");
  });

  test("the PR comment references the rendered PR head commit, not the merge commit", () => {
    // The numbers reflect the PR head commit the job checked out and rendered
    // against. The comment must link THAT commit so a reviewer can see exactly
    // which code produced the numbers. It must NOT use context.sha, which on
    // pull_request events is the transient merge commit that is not navigable
    // on the branch (the open PR feedback this guards against).
    const checkout = commentJob.steps.find((step) => step.uses === "actions/checkout@v6");
    expect(checkout.with.ref).toBe("${{ github.event.pull_request.head.sha }}");

    const comment = commentJob.steps.find(
      (step) => typeof step.uses === "string" && step.uses.startsWith("actions/github-script@")
    );
    // The head SHA is passed through env and the body builds a /commit/<sha> link.
    expect(comment.env.PERF_HEAD_SHA).toBe("${{ github.event.pull_request.head.sha }}");
    expect(comment.with.script).toContain("PERF_HEAD_SHA");
    expect(comment.with.script).toContain("/commit/");
    // Never reference the transient merge commit.
    expect(comment.with.script).not.toContain("context.sha");
  });

  test("the master-commit job commits the rendered doc to master ONLY on push, loop-guarded", () => {
    // The committed doc is refreshed on push to master (post-merge): a bot push to
    // the default branch blocks no PR.
    expect(commitJob.if).toContain("github.event_name == 'push'");
    expect(commitJob.if).toContain("needs.perf-benchmarks.result == 'success'");
    expect(commitJob.if).toContain("needs.loop-guard.outputs.should-run == 'true'");
    // It does NOT key off pull_request (that path comments, never commits).
    expect(commitJob.if).not.toContain("github.event_name == 'pull_request'");

    // Renders, prettier --write, computes changed from git diff, --check guards the
    // required prettier gate, then git-auto-commit pushes to master.
    const render = commitJob.steps.find((step) => step.id === "render");
    expect(render).toBeDefined();
    expect(render.run).toMatch(
      /render-perf-doc\.js[\s\S]*run-managed-prettier\.js --write[\s\S]*git diff --quiet/
    );
    const verify = commitJob.steps.find(
      (step) => step.run && /run-managed-prettier\.js --check/.test(step.run)
    );
    expect(verify).toBeDefined();
    const install = commitJob.steps.find(
      (step) => step.run && /npm ci/.test(step.run) && /package-lock\.json/.test(step.run)
    );
    expect(install).toBeDefined();
    // It must NOT check out / push the PR head branch (that would block merge).
    const checkout = commitJob.steps.find((step) => step.uses === "actions/checkout@v6");
    expect(checkout && checkout.with ? checkout.with.ref : undefined).not.toBe(
      "${{ github.event.pull_request.head.ref }}"
    );
  });
});

describe(".github/workflows/release.yml", () => {
  let text;
  let parsed;

  beforeAll(() => {
    text = readWorkflow("release.yml");
    parsed = loadWorkflowYaml("release.yml");
  });

  test("is tag-triggered only and validates exact semver tags", () => {
    const onBlock = getOnBlock(parsed);
    expect(Object.keys(onBlock)).toEqual(["push"]);
    expect(onBlock.push).toEqual({ tags: ["v[0-9]*.[0-9]*.[0-9]*"] });
    expect(text).toContain("^v[0-9]+\\.[0-9]+\\.[0-9]+$");
    expect(text).toContain("Tag ${tag} does not match package.json version");
    expect(text).not.toContain("workflow_dispatch");
  });

  test("validates, runs Unity checks, packs, attests, releases, and publishes with provenance", () => {
    expect(Object.keys(parsed.jobs).sort()).toEqual([
      "publish",
      // Added for Bug 3: runner-preflight is an ubuntu-latest job that
      // probes self-hosted runner availability before the unity-checks
      // self-hosted job queues. See docs/runbooks/unity-runners-after-transfer.md.
      "runner-preflight",
      "unity-checks",
      "validate",
      "verify-tag"
    ]);
    expect(parsed.jobs.publish["runs-on"]).toBe("ubuntu-latest");
    expect(text).toContain("npm run validate:npm-meta");
    expect(text).toContain("npm run test:unity-contracts");
    expect(text).toContain("npm pack --json");
    expect(text).toContain("actions/attest-build-provenance@v3");
    expect(text).toContain("actions/upload-artifact@v7");
    expect(text).toContain("gh release create");
    expect(text).toContain("npx --yes --package=npm@^11.5.1 npm publish");
    expect(text).toContain("--provenance");
    expect(text).not.toContain("NPM_TOKEN");
  });

  test("attestation job grants provenance permissions and validates from a full checkout", () => {
    const attestationJobEntry = Object.entries(parsed.jobs).find(([, job]) =>
      job.steps.some((step) => step.uses === "actions/attest-build-provenance@v3")
    );
    expect(attestationJobEntry).toBeDefined();

    const [, attestationJob] = attestationJobEntry;
    expect(attestationJob.permissions).toEqual({
      attestations: "write",
      contents: "read",
      "id-token": "write"
    });

    const checkoutIndex = attestationJob.steps.findIndex(
      (step) => step.uses === "actions/checkout@v6"
    );
    const validateAllIndex = attestationJob.steps.findIndex(
      (step) => step.run && step.run.includes("npm run validate:all")
    );

    expect(checkoutIndex).toBeGreaterThanOrEqual(0);
    expect(validateAllIndex).toBeGreaterThan(checkoutIndex);

    const checkoutStep = attestationJob.steps[checkoutIndex];
    expect(checkoutStep).toEqual(
      expect.objectContaining({
        with: expect.objectContaining({
          "fetch-depth": 0
        })
      })
    );
  });

  test("publish job has Trusted Publishing permissions", () => {
    expect(parsed.jobs.publish.permissions).toEqual({
      attestations: "write",
      contents: "write",
      "id-token": "write"
    });
  });
});

describe(".github/workflows/devcontainer-test.yml", () => {
  let text;
  let devcontainerCiSteps;

  beforeAll(() => {
    text = readWorkflow("devcontainer-test.yml");
    devcontainerCiSteps = collectDevcontainerCiSteps("devcontainer-test.yml");
  });

  test('contains eventFilterForPush: "" exactly (overrides devcontainers/ci@v0.3 default)', () => {
    // The default of "push" silently skips publishing on schedule /
    // workflow_dispatch — empty string makes the explicit `push:` knob
    // the single source of truth. Match either flow style or block style.
    expect(text).toMatch(/eventFilterForPush:\s*""/);
  });

  test("has one publishing build step and one smoke-test step", () => {
    expect(devcontainerCiSteps).toHaveLength(2);
    expect(devcontainerCiSteps.map((step) => step.with.push)).toEqual(["filter", "never"]);
    expect(devcontainerCiSteps[0].with.refFilterForPush).toBe("refs/heads/master");
    expect(devcontainerCiSteps[0].with.eventFilterForPush).toBe("");
    expect(devcontainerCiSteps[1].with.runCmd).toContain(
      "=== All devcontainer smoke checks passed ==="
    );
  });
});

describe(".github/workflows/devcontainer-prebuild.yml", () => {
  let text;
  let devcontainerCiSteps;

  beforeAll(() => {
    text = readWorkflow("devcontainer-prebuild.yml");
    devcontainerCiSteps = collectDevcontainerCiSteps("devcontainer-prebuild.yml");
  });

  test("builds locally, pushes explicitly, then verifies GHCR", () => {
    const pushNeverIndex = text.indexOf("push: never");
    const dockerPushIndex = text.indexOf('docker push "${IMAGE}"');
    const dockerPullIndex = text.indexOf('docker pull "${IMAGE}"');

    expect(pushNeverIndex).toBeGreaterThanOrEqual(0);
    expect(dockerPushIndex).toBeGreaterThan(pushNeverIndex);
    expect(dockerPullIndex).toBeGreaterThan(dockerPushIndex);
  });

  test("does not rely on devcontainers/ci post-action publishing", () => {
    expect(devcontainerCiSteps).toHaveLength(1);
    expect(devcontainerCiSteps[0].with.push).toBe("never");
    expect(devcontainerCiSteps[0].with.eventFilterForPush).toBeUndefined();
    expect(text).toContain('docker manifest inspect "${IMAGE}" > "${manifest_file}"');
  });
});

describe("devcontainer workflow GHCR publishing contract", () => {
  const expectedImage = "ghcr.io/${{ steps.repo.outputs.repository_lowercase }}/devcontainer";
  const workflowCases = [
    {
      name: "devcontainer-prebuild.yml",
      expectedActionPush: "never",
      requiresExplicitPushBeforePull: true,
      requiresEventFilterOverride: false
    },
    {
      name: "devcontainer-test.yml",
      expectedActionPush: "filter",
      requiresExplicitPushBeforePull: false,
      requiresEventFilterOverride: true
    }
  ];

  test.each(workflowCases)(
    "$name uses a lowercase GHCR image and current GHCR login action",
    ({ name }) => {
      const workflowText = readWorkflow(name);
      const devcontainerCiSteps = collectDevcontainerCiSteps(name);

      expect(workflowText).toContain("uses: docker/login-action@v4");
      expect(workflowText).toContain("id: repo");
      expect(workflowText).toContain("repository_lowercase=${repo_lower}");
      expect(workflowText).toContain("tr '[:upper:]' '[:lower:]'");
      expect(devcontainerCiSteps.map((step) => step.with.imageName)).toEqual(
        devcontainerCiSteps.map(() => expectedImage)
      );
      expect(devcontainerCiSteps.map((step) => step.with.cacheFrom)).toEqual(
        devcontainerCiSteps.map(() => expectedImage)
      );
      expect(workflowText).not.toContain("ghcr.io/${{ github.repository }}");
      expect(workflowText).not.toContain("ghcr.io/${{ github.repository_owner }}/");
      expect(workflowText).not.toContain("docker/login-action@v3");
    }
  );

  test.each(workflowCases)(
    "$name keeps devcontainers/ci push behavior compatible with verification",
    ({ name, expectedActionPush, requiresExplicitPushBeforePull, requiresEventFilterOverride }) => {
      const workflowText = readWorkflow(name);
      const devcontainerCiSteps = collectDevcontainerCiSteps(name);

      expect(devcontainerCiSteps.some((step) => step.with.push === expectedActionPush)).toBe(true);

      if (requiresEventFilterOverride) {
        expect(devcontainerCiSteps.some((step) => step.with.eventFilterForPush === "")).toBe(true);
      } else {
        expect(
          devcontainerCiSteps.every((step) => step.with.eventFilterForPush === undefined)
        ).toBe(true);
      }

      const dockerPushIndex = workflowText.indexOf('docker push "${IMAGE}"');
      const dockerPullIndex = workflowText.indexOf('docker pull "${IMAGE}"');

      if (requiresExplicitPushBeforePull) {
        expect(dockerPushIndex).toBeGreaterThanOrEqual(0);
        expect(dockerPullIndex).toBeGreaterThan(dockerPushIndex);
      } else {
        expect(dockerPushIndex).toBe(-1);
      }
    }
  );
});
