/**
 * @fileoverview Tests for the WORKFLOW_POLICY_CHECKS dispatch registry in
 * validate-workflows.js.
 *
 * validateWorkflow dispatches its per-workflow policy checks from a frozen,
 * ordered registry (TABLE ORDER IS OUTPUT ORDER). The two oracle suites pin
 * each policy's behavior; this suite pins the dispatch layer itself:
 *
 *   1. Registry order + identity: the 32 entries match the historical call
 *      order verbatim and reference the exported policy functions.
 *   2. Completeness in both directions: every exported find*Violations policy
 *      is dispatched except the two known non-per-workflow exceptions, and
 *      every registry entry is an exported policy.
 *   3. Adapter wiring through validateWorkflow: the lockfile adapter's
 *      isIgnoredPathFn evaluation stays inside the try (it can throw), and the
 *      content-taking policies receive the raw file content.
 *   4. Output ordering: violations from different policies appear in registry
 *      order, not file-line order.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const validator = require("../validate-workflows.js");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const { WORKFLOW_POLICY_CHECKS, validateWorkflow } = validator;

// The dispatch order of validateWorkflow's try block before the registry
// extraction, transcribed verbatim. Reordering the registry is a behavior
// change and must update this literal deliberately.
const EXPECTED_REGISTRY_ORDER = [
  "findIgnoredPathViolations",
  "findComparisonPackageValidationTriggerViolations",
  "findLockfileInstallViolations",
  "findPreCommitInstallHookWriterViolations",
  "findWindowsBashPortabilityViolations",
  "findForbiddenRunsOnGroupViolations",
  "findForbiddenSharedConcurrencyViolations",
  "findConcurrencyQueueViolations",
  "findMatrixConcurrencyEvictionViolations",
  "findGameCiTestRunnerInputViolations",
  "findUnityGameCiLockAndPreflightViolations",
  "findUnityNativeProvisioningViolations",
  "findUnityReleaseModeViolations",
  "findPerfDeltasCommentGateViolations",
  "findUnityLicenseReturnViolations",
  "findForbiddenUnityLicenseSecretViolations",
  "findRequiredUnityLicenseSecretViolations",
  "findSelfHostedLabelAllowlistViolations",
  "findDynamicRunsOnMissingNeedsViolations",
  "findChangelogCoverageCheckoutViolations",
  "findCheckoutCredentialPersistenceViolations",
  "findSelfHostedWindowsCheckoutLongPathViolations",
  "findTokenizedGitRemoteCredentialViolations",
  "findPersistentGitExtraheaderCredentialViolations",
  "findAutoCommitAppCredentialWarningViolations",
  "findGitHubAppAutoCommitRobustnessViolations",
  "findSelfHostedRunnerPreflightViolations",
  "findForbidPlainShellBashOnSelfHostedWindowsViolations",
  "findUnityLockTimeoutViolations",
  "findCrossPlatformPreflightTargetedGateViolations",
  "findLycheeActionPolicyViolations",
  "findComputeUnityAssembliesGateViolations"
];

// The non-uniform signatures that need a run(ctx) adapter instead of the
// uniform (relativePath, lines) call.
const ADAPTER_POLICY_NAMES = [
  "findIgnoredPathViolations",
  "findLockfileInstallViolations",
  "findLycheeActionPolicyViolations",
  "findComputeUnityAssembliesGateViolations"
];

// Exported find*Violations policies that are intentionally NOT dispatched per
// workflow file: findRequiredWorkflowFileViolations runs once over the whole
// workflow directory listing, and findWorkflowLineLengthViolations runs before
// the registry with the resolved line-length policy arguments.
const NON_DISPATCHED_FIND_EXPORTS = [
  "findRequiredWorkflowFileViolations",
  "findWorkflowLineLengthViolations"
];

describe("registry order and identity", () => {
  test("policy names match the historical dispatch order verbatim", () => {
    expect(WORKFLOW_POLICY_CHECKS.map((check) => check.policy.name)).toEqual(
      EXPECTED_REGISTRY_ORDER
    );
  });

  test("every entry references the exported policy function", () => {
    for (const check of WORKFLOW_POLICY_CHECKS) {
      expect(check.policy).toBe(validator[check.policy.name]);
    }
  });

  test("the registry table and every entry are frozen", () => {
    expect(Object.isFrozen(WORKFLOW_POLICY_CHECKS)).toBe(true);
    for (const check of WORKFLOW_POLICY_CHECKS) {
      expect(Object.isFrozen(check)).toBe(true);
    }
  });

  test("exactly the non-uniform policies carry a run adapter, and each is a function", () => {
    const adapterNames = WORKFLOW_POLICY_CHECKS.filter((check) => "run" in check).map(
      (check) => check.policy.name
    );
    expect(adapterNames).toEqual(ADAPTER_POLICY_NAMES);
    for (const check of WORKFLOW_POLICY_CHECKS) {
      if ("run" in check) {
        expect(typeof check.run).toBe("function");
      }
    }
  });
});

describe("registry completeness against module exports", () => {
  test("every exported find*Violations policy is dispatched except the known exceptions", () => {
    const exported = Object.keys(validator).filter((name) => /^find.*Violations$/.test(name));
    const registryNames = new Set(WORKFLOW_POLICY_CHECKS.map((check) => check.policy.name));
    const missing = exported.filter((name) => !registryNames.has(name)).sort();
    expect(missing).toEqual(NON_DISPATCHED_FIND_EXPORTS);
  });

  test("every registry policy is an exported find*Violations function", () => {
    for (const check of WORKFLOW_POLICY_CHECKS) {
      expect(check.policy.name).toMatch(/^find.*Violations$/);
      expect(typeof validator[check.policy.name]).toBe("function");
    }
  });
});

describe("adapter wiring through validateWorkflow", () => {
  test("lockfile adapter evaluates isIgnoredPathFn inside the try even without paths: filters", () => {
    const tempDir = makeTempDir("validate-workflows-dispatch-throw");
    try {
      const workflowPath = path.join(tempDir, "adapter-throw.yml");
      fs.writeFileSync(
        workflowPath,
        [
          "name: Adapter Throw",
          "on: push",
          "jobs:",
          "  noop:",
          "    runs-on: ubuntu-latest",
          "    steps:",
          "      - run: echo ok"
        ].join("\n"),
        "utf8"
      );

      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: () => {
          throw new Error("mock ignore failure");
        }
      });

      expect(
        violations.some((violation) =>
          violation.message.includes("Workflow validation failed while evaluating ignore policy")
        )
      ).toBe(true);
    } finally {
      cleanupDir(tempDir);
    }
  });

  test("earlier checks' violations survive a mid-dispatch throw from the lockfile probe", () => {
    const tempDir = makeTempDir("validate-workflows-dispatch-partial");
    try {
      const workflowPath = path.join(tempDir, "partial-dispatch.yml");
      fs.writeFileSync(
        workflowPath,
        [
          "name: Partial Dispatch",
          "on:",
          "  push:",
          "    paths:",
          "      - package.json",
          "jobs:",
          "  noop:",
          "    runs-on: ubuntu-latest",
          "    steps:",
          "      - run: echo ok"
        ].join("\n"),
        "utf8"
      );

      // The injected isIgnoredPathFn reports package.json as ignored (so check
      // #1, findIgnoredPathViolations, emits an ignored-path violation) but
      // throws for the package-lock.json probe that only the check-#3 lockfile
      // adapter performs. Seeing BOTH violations proves the lockfile flag is
      // evaluated lazily at dispatch time (a context-build evaluation would
      // throw before check #1 ever ran) and that each check's violations are
      // pushed incrementally (a collect-then-push rewrite would lose check
      // #1's output when check #3 throws).
      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: (_repoRoot, candidatePath) => {
          if (candidatePath === "package-lock.json") {
            throw new Error("mock lockfile ignore failure");
          }
          return candidatePath === "package.json";
        }
      });

      expect(
        violations.some(
          (violation) =>
            violation.pattern === "package.json" &&
            violation.message.includes("is ignored by git and cannot trigger this workflow")
        )
      ).toBe(true);
      expect(
        violations.some((violation) =>
          violation.message.includes("Workflow validation failed while evaluating ignore policy")
        )
      ).toBe(true);
    } finally {
      cleanupDir(tempDir);
    }
  });

  test("content-taking policies receive the raw file content", () => {
    const tempDir = makeTempDir("validate-workflows-dispatch-content");
    try {
      const workflowPath = path.join(tempDir, "compute-gate.yml");
      fs.writeFileSync(
        workflowPath,
        [
          "name: Compute Gate",
          "on: workflow_dispatch",
          "jobs:",
          "  unity:",
          "    runs-on: ubuntu-latest",
          "    steps:",
          "      - name: Compute test assembly list",
          "        id: compute",
          "        uses: ./.github/actions/compute-unity-assemblies",
          "        with:",
          "          target: editmode",
          "      - name: Provision Unity Editor",
          "        shell: pwsh",
          "        run: |",
          "          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1'"
        ].join("\n"),
        "utf8"
      );

      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: () => false
      });

      // Only findComputeUnityAssembliesGateViolations emits this pattern, and
      // it parses the raw content string (not the split lines) via the yaml
      // package -- so seeing it proves ctx.content reached the adapter.
      expect(
        violations.some(
          (violation) => violation.pattern === "Unity license-consuming step not gated on is-empty"
        )
      ).toBe(true);
    } finally {
      cleanupDir(tempDir);
    }
  });
});

describe("registry order drives output order", () => {
  test("runs-on-group violation precedes UNITY_LICENSING_SERVER violation despite later file position", () => {
    const tempDir = makeTempDir("validate-workflows-dispatch-order");
    try {
      const workflowPath = path.join(tempDir, "ordering.yml");
      // The retired-secret reference sits on an EARLIER file line than the
      // forbidden runs-on group, so line-ordered dispatch would report it
      // first. Registry order (runs-on-group at #6, licensing secret at #16)
      // must win.
      fs.writeFileSync(
        workflowPath,
        [
          "name: Ordering",
          "on: push",
          "env:",
          "  UNITY_LICENSE_HOST: ${{ secrets.UNITY_LICENSING_SERVER }}",
          "jobs:",
          "  build:",
          "    runs-on: { group: builds }",
          "    steps:",
          "      - run: echo ok"
        ].join("\n"),
        "utf8"
      );

      const violations = validateWorkflow(workflowPath, {
        repoRoot: tempDir,
        isIgnoredPathFn: () => false
      });

      const runsOnGroupIndex = violations.findIndex((violation) =>
        violation.message.includes("runs-on.group is forbidden")
      );
      const licensingSecretIndex = violations.findIndex((violation) =>
        violation.message.includes("secrets.UNITY_LICENSING_SERVER")
      );

      expect(runsOnGroupIndex).toBeGreaterThanOrEqual(0);
      expect(licensingSecretIndex).toBeGreaterThanOrEqual(0);
      expect(runsOnGroupIndex).toBeLessThan(licensingSecretIndex);
    } finally {
      cleanupDir(tempDir);
    }
  });
});
