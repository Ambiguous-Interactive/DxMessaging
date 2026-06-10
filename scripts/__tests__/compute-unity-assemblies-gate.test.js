/**
 * @fileoverview Tests for the compute-unity-assemblies is-empty gate rule in
 * validate-workflows.js (findComputeUnityAssembliesGateViolations).
 *
 * The rule enforces the canonical is-empty gate from unity-tests.yml across
 * every workflow that uses ./.github/actions/compute-unity-assemblies: the
 * compute step must carry an id, and every license-consuming step in the same
 * job (editor provision, org Unity lock acquire, Unity run) must be gated on
 * steps.<id>.outputs.is-empty != 'true'. The parser is structural (the yaml
 * package) so the check is formatting-invariant; a YAML parse failure is a hard
 * error.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const yaml = require("yaml");

const {
  findComputeUnityAssembliesGateViolations,
  stepUsesComputeUnityAssemblies,
  stepConsumesUnityLicense,
  ifExpressionGatesOnComputeEmptiness
} = require("../validate-workflows.js");
const { singleJobWorkflow } = require("../lib/workflow-fixtures");

const WORKFLOWS_DIR = path.resolve(__dirname, "..", "..", ".github", "workflows");

function readWorkflow(name) {
  return fs.readFileSync(path.join(WORKFLOWS_DIR, name), "utf8");
}

function join(lines) {
  return lines.join("\n");
}

// Minimal building blocks for synthetic single-job workflows.
const COMPUTE_STEP_WITH_ID = [
  "      - name: Compute test assembly list",
  "        id: compute",
  "        uses: ./.github/actions/compute-unity-assemblies",
  "        with:",
  "          target: editmode"
];

const COMPUTE_STEP_WITHOUT_ID = [
  "      - name: Compute test assembly list",
  "        uses: ./.github/actions/compute-unity-assemblies",
  "        with:",
  "          target: editmode"
];

const GATE = "${{ steps.compute.outputs.is-empty != 'true' }}";

function provisionStep({ gate } = {}) {
  return [
    "      - name: Provision Unity Editor",
    ...(gate ? [`        if: ${gate}`] : []),
    "        shell: pwsh",
    "        run: |",
    "          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1'"
  ];
}

function acquireLockStep({ gate } = {}) {
  return [
    "      - name: Acquire organization Unity lock",
    ...(gate ? [`        if: ${gate}`] : []),
    "        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1"
  ];
}

function runTestsStep({ gate } = {}) {
  return [
    "      - name: Run Unity Test Runner",
    ...(gate ? [`        if: ${gate}`] : []),
    "        shell: pwsh",
    "        run: |",
    "          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode"
  ];
}

// Synthetic one-job workflow shared by the tests below: the fixed header/job
// shape comes from the shared builder; call sites pass step BLOCKS (arrays of
// step lines), flattened here.
function syntheticWorkflow(stepBlocks) {
  return singleJobWorkflow("unity", stepBlocks.flat(), {
    runsOn: "[self-hosted, Windows, RAM-64GB]",
    header: ["name: Synthetic", "on: workflow_dispatch"]
  });
}

describe("findComputeUnityAssembliesGateViolations", () => {
  describe("(a) real edited workflows pass after the is-empty gate edits", () => {
    const REAL_WORKFLOWS = [
      "unity-tests.yml",
      "release.yml",
      "unity-benchmarks.yml",
      "perf-numbers.yml",
      "unity-gameci-experiment.yml"
    ];

    test.each(REAL_WORKFLOWS)("%s has no compute-unity-assemblies gate violation", (name) => {
      const content = readWorkflow(name);
      const violations = findComputeUnityAssembliesGateViolations(name, content);
      expect(violations).toEqual([]);
    });
  });

  describe("(b) compute step without an id fails", () => {
    test("missing id on the compute step is reported", () => {
      const content = syntheticWorkflow([COMPUTE_STEP_WITHOUT_ID, provisionStep({ gate: GATE })]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toHaveLength(1);
      expect(violations[0].pattern).toBe("compute-unity-assemblies without id");
      expect(violations[0].message).toContain("must declare an 'id'");
      expect(violations[0].line).toBeGreaterThan(0);
    });
  });

  describe("(c) id present but a license-consuming step is not gated fails", () => {
    test("non-gated ensure-editor.ps1 provision step is reported", () => {
      const content = syntheticWorkflow([COMPUTE_STEP_WITH_ID, provisionStep()]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toHaveLength(1);
      expect(violations[0].pattern).toBe("Unity license-consuming step not gated on is-empty");
      expect(violations[0].message).toContain("is-empty != 'true'");
    });

    test("non-gated acquire-build-lock step is reported", () => {
      const content = syntheticWorkflow([COMPUTE_STEP_WITH_ID, acquireLockStep()]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toHaveLength(1);
      expect(violations[0].pattern).toBe("Unity license-consuming step not gated on is-empty");
    });

    test("non-gated run-ci-tests.ps1 run step is reported", () => {
      const content = syntheticWorkflow([COMPUTE_STEP_WITH_ID, runTestsStep()]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toHaveLength(1);
      expect(violations[0].pattern).toBe("Unity license-consuming step not gated on is-empty");
    });

    test("each non-gated license-consuming step is reported independently", () => {
      const content = syntheticWorkflow([
        COMPUTE_STEP_WITH_ID,
        provisionStep(),
        acquireLockStep(),
        runTestsStep()
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toHaveLength(3);
      for (const violation of violations) {
        expect(violation.pattern).toBe("Unity license-consuming step not gated on is-empty");
      }
    });
  });

  describe("(d) a correctly gated synthetic workflow passes", () => {
    test("all three license-consuming steps gated on the compute id", () => {
      const content = syntheticWorkflow([
        COMPUTE_STEP_WITH_ID,
        provisionStep({ gate: GATE }),
        acquireLockStep({ gate: GATE }),
        runTestsStep({ gate: GATE })
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toEqual([]);
    });

    test("gate ANDed with another condition still passes", () => {
      const combined = "${{ !cancelled() && steps.compute.outputs.is-empty != 'true' }}";
      const content = syntheticWorkflow([COMPUTE_STEP_WITH_ID, provisionStep({ gate: combined })]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toEqual([]);
    });

    test("double-quoted true literal in the gate still passes", () => {
      const doubleQuoted = '${{ steps.compute.outputs.is-empty != "true" }}';
      const content = syntheticWorkflow([
        COMPUTE_STEP_WITH_ID,
        provisionStep({ gate: doubleQuoted })
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toEqual([]);
    });
  });

  describe("(e) a workflow that does not use the action produces no violation", () => {
    test("no compute-unity-assemblies step means no gate is required", () => {
      const content = join([
        "name: NoAction",
        "on: push",
        "jobs:",
        "  build:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - run: echo build",
        "      - name: Provision",
        "        run: ./scripts/unity/ensure-editor.ps1 -X"
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toEqual([]);
    });
  });

  describe("robustness", () => {
    test("multiple compute steps in one job: each must carry an id", () => {
      const content = syntheticWorkflow([
        COMPUTE_STEP_WITH_ID,
        [
          "      - name: Compute playmode assembly list",
          "        uses: ./.github/actions/compute-unity-assemblies",
          "        with:",
          "          target: playmode"
        ],
        provisionStep({ gate: GATE })
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toHaveLength(1);
      expect(violations[0].pattern).toBe("compute-unity-assemblies without id");
    });

    test("gate may reference any compute id present in the job", () => {
      const secondGate = "${{ steps.compute2.outputs.is-empty != 'true' }}";
      const content = syntheticWorkflow([
        COMPUTE_STEP_WITH_ID,
        [
          "      - name: Compute playmode assembly list",
          "        id: compute2",
          "        uses: ./.github/actions/compute-unity-assemblies",
          "        with:",
          "          target: playmode"
        ],
        provisionStep({ gate: secondGate })
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toEqual([]);
    });

    test("a step gated on a foreign id is still reported", () => {
      const foreignGate = "${{ steps.other.outputs.is-empty != 'true' }}";
      const content = syntheticWorkflow([
        COMPUTE_STEP_WITH_ID,
        provisionStep({ gate: foreignGate })
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toHaveLength(1);
      expect(violations[0].pattern).toBe("Unity license-consuming step not gated on is-empty");
    });

    test("the gate is job-scoped: a compute id in another job does not satisfy it", () => {
      // The unity job's own compute step DOES carry an id, but it differs from
      // the `compute` id used in the gate (that id lives in the `resolve` job).
      // Because the gate must reference a compute id in the SAME job, the
      // provision step is reported as not gated.
      const content = join([
        "name: TwoJobs",
        "on: workflow_dispatch",
        "jobs:",
        "  resolve:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        ...COMPUTE_STEP_WITH_ID,
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        "      - name: Compute unity assembly list",
        "        id: unity-compute",
        "        uses: ./.github/actions/compute-unity-assemblies",
        "        with:",
        "          target: editmode",
        ...provisionStep({ gate: GATE })
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toHaveLength(1);
      expect(violations[0].pattern).toBe("Unity license-consuming step not gated on is-empty");
      expect(violations[0].message).toContain("'unity'");
    });

    test("a job whose only compute step lacks an id reports just the missing id", () => {
      // When the sole compute step in a job has no id, there is no id for the
      // license-consuming steps to reference; the missing-id violation is the
      // single, root-cause report (the gate-miss is not piled on top).
      const content = syntheticWorkflow([
        COMPUTE_STEP_WITHOUT_ID,
        provisionStep(),
        acquireLockStep()
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toHaveLength(1);
      expect(violations[0].pattern).toBe("compute-unity-assemblies without id");
    });

    test("a job that does not use the action contributes no violation", () => {
      const content = join([
        "name: Mixed",
        "on: workflow_dispatch",
        "jobs:",
        "  lint:",
        "    runs-on: ubuntu-latest",
        "    steps:",
        "      - run: ./scripts/unity/ensure-editor.ps1 -X",
        "  unity:",
        "    runs-on: [self-hosted, Windows, RAM-64GB]",
        "    steps:",
        ...COMPUTE_STEP_WITH_ID,
        ...provisionStep({ gate: GATE })
      ]);
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toEqual([]);
    });

    test("malformed YAML is a hard error, never a silent pass", () => {
      const content = "jobs:\n  unity:\n   - broken: : :\n  : nope";
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations.length).toBeGreaterThanOrEqual(1);
      expect(violations[0].severity).toBe("error");
      expect(violations[0].pattern).toBe("compute-unity-assemblies gate");
    });

    test("a workflow with no jobs key produces no violation", () => {
      const content = "name: Empty\non: push\n";
      const violations = findComputeUnityAssembliesGateViolations("synthetic.yml", content);
      expect(violations).toEqual([]);
    });

    test("an unresolvable 'yaml' package is a hard error, never a crash", () => {
      // Simulate the 'yaml' package vanishing from the module tree (e.g. a future
      // devDependency bump dropping the transitive). The validator must surface an
      // actionable policy Violation instead of dying with an uncaught
      // MODULE_NOT_FOUND. isolateModules gives the re-required validator a fresh
      // module-level yaml cache so the mocked require is exercised.
      jest.isolateModules(() => {
        jest.doMock("yaml", () => {
          const error = new Error("Cannot find module 'yaml'");
          error.code = "MODULE_NOT_FOUND";
          throw error;
        });

        const isolated = require("../validate-workflows.js");
        const content = syntheticWorkflow([COMPUTE_STEP_WITH_ID, provisionStep({ gate: GATE })]);
        const violations = isolated.findComputeUnityAssembliesGateViolations(
          "synthetic.yml",
          content
        );

        expect(violations).toHaveLength(1);
        expect(violations[0].severity).toBe("error");
        expect(violations[0].pattern).toBe("compute-unity-assemblies gate");
        expect(violations[0].message).toContain("yaml");
        expect(violations[0].message).toContain("npm ci");

        jest.dontMock("yaml");
      });
    });
  });
});

describe("stepUsesComputeUnityAssemblies", () => {
  test("matches the composite action regardless of leading ./ and case", () => {
    expect(
      stepUsesComputeUnityAssemblies({ uses: "./.github/actions/compute-unity-assemblies" })
    ).toBe(true);
    expect(
      stepUsesComputeUnityAssemblies({ uses: ".github/actions/Compute-Unity-Assemblies" })
    ).toBe(true);
  });

  test("does not match unrelated composites or missing uses", () => {
    expect(
      stepUsesComputeUnityAssemblies({ uses: "./.github/actions/validate-unity-license" })
    ).toBe(false);
    expect(stepUsesComputeUnityAssemblies({ run: "echo hi" })).toBe(false);
    expect(stepUsesComputeUnityAssemblies({})).toBe(false);
  });
});

describe("stepConsumesUnityLicense", () => {
  test("ensure-editor.ps1 in run marks a license-consuming step", () => {
    expect(stepConsumesUnityLicense({ run: "$e = ./scripts/unity/ensure-editor.ps1 -X" })).toBe(
      true
    );
  });

  test("run-ci-tests.ps1 in run marks a license-consuming step", () => {
    expect(
      stepConsumesUnityLicense({ run: "./scripts/unity/run-ci-tests.ps1 -GenerateOnly" })
    ).toBe(true);
  });

  test("acquire-build-lock in uses marks a license-consuming step", () => {
    expect(
      stepConsumesUnityLicense({
        uses: "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1"
      })
    ).toBe(true);
  });

  test("release-build-lock and unrelated steps are not license-consuming", () => {
    expect(
      stepConsumesUnityLicense({
        uses: "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1"
      })
    ).toBe(false);
    expect(stepConsumesUnityLicense({ run: "npm run test:scripts" })).toBe(false);
    expect(stepConsumesUnityLicense({ uses: "actions/checkout@v6" })).toBe(false);
  });
});

describe("ifExpressionGatesOnComputeEmptiness", () => {
  const ids = ["compute"];

  test("accepts single-quoted, double-quoted, and whitespace variants", () => {
    expect(
      ifExpressionGatesOnComputeEmptiness("${{ steps.compute.outputs.is-empty != 'true' }}", ids)
    ).toBe(true);
    expect(
      ifExpressionGatesOnComputeEmptiness('${{ steps.compute.outputs.is-empty != "true" }}', ids)
    ).toBe(true);
    expect(
      ifExpressionGatesOnComputeEmptiness("${{ steps.compute.outputs.is-empty!='true' }}", ids)
    ).toBe(true);
  });

  test("accepts the gate ANDed with other conditions", () => {
    expect(
      ifExpressionGatesOnComputeEmptiness(
        "${{ needs.x.result == 'success' && steps.compute.outputs.is-empty != 'true' }}",
        ids
      )
    ).toBe(true);
  });

  test("rejects the inverted comparison and foreign ids", () => {
    expect(
      ifExpressionGatesOnComputeEmptiness("${{ steps.compute.outputs.is-empty == 'true' }}", ids)
    ).toBe(false);
    expect(
      ifExpressionGatesOnComputeEmptiness("${{ steps.other.outputs.is-empty != 'true' }}", ids)
    ).toBe(false);
    expect(ifExpressionGatesOnComputeEmptiness("${{ always() }}", ids)).toBe(false);
    expect(ifExpressionGatesOnComputeEmptiness(undefined, ids)).toBe(false);
    expect(
      ifExpressionGatesOnComputeEmptiness("${{ steps.compute.outputs.is-empty != 'true' }}", [])
    ).toBe(false);
  });
});

// release.yml publish-job ordering invariant: a published GitHub Release must
// never exist without the matching npm version. Because npm publish is
// irreversible (a version cannot be re-published after an unpublish window),
// the publish step has to run BEFORE the GitHub Release is created/finalized.
// The workflow guards this with a code comment only; this test pins the step
// order structurally so a future reorder that recreates the
// "published Release without matching npm version" hazard fails CI.
describe("release.yml publish job ordering invariant", () => {
  function publishJobSteps() {
    const content = readWorkflow("release.yml");
    const doc = yaml.parse(content);
    expect(doc).toBeTruthy();
    expect(doc.jobs).toBeTruthy();
    expect(doc.jobs.publish).toBeTruthy();
    const steps = doc.jobs.publish.steps;
    expect(Array.isArray(steps)).toBe(true);
    return steps;
  }

  test("npm publish runs strictly before the GitHub Release step", () => {
    const steps = publishJobSteps();
    const publishIndex = steps.findIndex(
      (step) => step && step.name === "Publish to npm with provenance"
    );
    const releaseIndex = steps.findIndex(
      (step) => step && step.name === "Create or update GitHub Release"
    );

    // Both steps must exist; a rename that drops either side would otherwise
    // make the ordering assertion vacuously pass.
    expect(publishIndex).toBeGreaterThanOrEqual(0);
    expect(releaseIndex).toBeGreaterThanOrEqual(0);

    // The irreversible npm publish must precede the GitHub Release.
    expect(publishIndex).toBeLessThan(releaseIndex);
  });

  test("the npm publish step is re-runnable via a registry skip-if-exists guard", () => {
    const steps = publishJobSteps();
    const publishStep = steps.find(
      (step) => step && step.name === "Publish to npm with provenance"
    );
    expect(publishStep).toBeTruthy();
    const run = String(publishStep.run || "");

    // The publish must stay safely re-runnable: a prior run that published the
    // package before failing downstream must not abort this run on the
    // immutable-version error. The guard queries the registry and skips when the
    // exact name@version is already present.
    expect(run).toContain("npm view");
    expect(run).toContain("npm publish");
    expect(run).toContain("--provenance");
    expect(run).toContain("--access public");
    expect(run).toContain("set -euo pipefail");
  });
});
