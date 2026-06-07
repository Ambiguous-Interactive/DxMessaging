/**
 * @fileoverview Tests for validator checks in scripts/validate-workflows.js
 * AND workflow-shape contracts for the stuck-job-recovery workflows.
 *
 * Validator-check suites (added after the unity matrix-eviction incident):
 *
 *   - findForbiddenSharedConcurrencyViolations  (sentinel guard)
 *   - findMatrixConcurrencyEvictionViolations   (matrix-without-expansion guard)
 *   - findSelfHostedLabelAllowlistViolations    (self-hosted label allowlist)
 *   - findDynamicRunsOnMissingNeedsViolations   (dynamic runs-on / needs guard)
 *   - extractEmittedLabelSetsFromBash + extractJobConcurrencyGroup /
 *     extractWorkflowConcurrencyGroup / extractJobMatrixMaxParallel /
 *     extractJobNeeds / parseInlineLabelArray (helper-extractor contracts)
 *
 * Each validator suite covers positive (clean), negative (each violation
 * form), and the order-insensitive equivalence required by the allowlist
 * comparison.
 *
 * Workflow-shape contract suites (added with the GitHub Actions dispatcher
 * stuck-run recovery workflows -- Community Discussion #186811):
 *
 *   - unstick-run.yml workflow contract  (manual one-click recovery
 *     workflow: trigger inputs, permissions, concurrency, and the inline
 *     bash safety guards)
 *   - stuck-job-watchdog.yml tightened thresholds  (cron `*\/5` and
 *     MIN_QUEUE_AGE_SECONDS=300 invariants so we cannot accidentally
 *     regress to the looser pre-tightening cadence)
 */

"use strict";

const fs = require("fs");
const path = require("path");
const yaml = require("yaml");

const {
  findForbiddenSharedConcurrencyViolations,
  findConcurrencyQueueViolations,
  findMatrixConcurrencyEvictionViolations,
  findGameCiTestRunnerInputViolations,
  findUnityGameCiLockAndPreflightViolations,
  findUnityNativeProvisioningViolations,
  findUnityReleaseModeViolations,
  findUnityLicenseReturnViolations,
  findForbiddenUnityLicenseSecretViolations,
  findRequiredUnityLicenseSecretViolations,
  findSelfHostedLabelAllowlistViolations,
  findDynamicRunsOnMissingNeedsViolations,
  extractEmittedLabelSetsFromBash,
  extractJobConcurrencyGroup,
  extractWorkflowConcurrencyGroup,
  extractJobMatrixMaxParallel,
  extractJobTimeoutMinutes,
  extractStepTimeoutMinutes,
  findUnityLockTimeoutViolations,
  extractJobNeeds,
  parseInlineLabelArray,
  extractJobs,
  extractJobSteps
} = require("../validate-workflows.js");

const REPO_ROOT_FOR_FILES = path.resolve(__dirname, "..", "..");
const WORKFLOWS_DIR_FOR_FILES = path.join(REPO_ROOT_FOR_FILES, ".github", "workflows");
const UNSTICK_RUN_PATH = path.join(WORKFLOWS_DIR_FOR_FILES, "unstick-run.yml");
const STUCK_WATCHDOG_PATH = path.join(WORKFLOWS_DIR_FOR_FILES, "stuck-job-watchdog.yml");

function loadWorkflowYamlFromPath(absPath) {
  const text = fs.readFileSync(absPath, "utf8");
  // The `yaml` package parses with the YAML 1.2 core schema, so the bare `on:`
  // trigger key stays the string `"on"` (no YAML 1.1 boolean coercion).
  return yaml.parse(text);
}

function getOnBlock(doc) {
  // YAML 1.2 core schema (the `yaml` package default) keeps `on:` a string key.
  if (doc && Object.prototype.hasOwnProperty.call(doc, "on")) {
    return doc.on;
  }
  return undefined;
}

function asLines(text) {
  // Strip a single leading blank line so test fixtures can start with `\n`.
  const trimmed = text.replace(/^\n/, "");
  return trimmed.split("\n");
}

describe("findForbiddenSharedConcurrencyViolations", () => {
  test("flags the wallstop-organization-builds sentinel group", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency:
      group: wallstop-organization-builds
      cancel-in-progress: false
    steps:
      - run: echo hi
`);

    const violations = findForbiddenSharedConcurrencyViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("wallstop-organization-builds");
    expect(violations[0].message).toContain("forbidden");
    // Line citation points at the group: line within the fixture.
    expect(violations[0].line).toBe(5);
  });

  test("clean workflow with no concurrency block produces no violations", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - run: echo hi
`);
    expect(findForbiddenSharedConcurrencyViolations("test.yml", lines)).toEqual([]);
  });

  test("clean workflow with a non-sentinel concurrency.group produces no violations", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency:
      group: \${{ github.workflow }}-\${{ github.ref }}
      cancel-in-progress: true
    steps:
      - run: echo hi
`);
    expect(findForbiddenSharedConcurrencyViolations("test.yml", lines)).toEqual([]);
  });

  test("flags sentinel in inline concurrency mapping form", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency: { group: wallstop-organization-builds, cancel-in-progress: false }
    steps:
      - run: echo hi
`);
    const violations = findForbiddenSharedConcurrencyViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("wallstop-organization-builds");
  });
});

describe("findMatrixConcurrencyEvictionViolations", () => {
  test("flags matrix job with a shared concurrency.group and no max-parallel declaration", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency:
      group: shared-unity-lock
      cancel-in-progress: false
    strategy:
      matrix:
        unity-version:
          - "2021.3.45f1"
          - "2022.3.45f1"
    steps:
      - run: echo hi
`);

    const violations = findMatrixConcurrencyEvictionViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("shared-unity-lock");
    expect(violations[0].message).toContain("\${{ matrix.* }}");
    expect(violations[0].message).toContain("max-parallel: 1");
    expect(violations[0].message).toContain("no strategy.max-parallel declaration");
  });

  test("flags matrix job with a shared concurrency.group and max-parallel > 1", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency:
      group: shared-unity-lock
      cancel-in-progress: false
    strategy:
      max-parallel: 2
      matrix:
        unity-version:
          - "2021.3.45f1"
          - "2022.3.45f1"
    steps:
      - run: echo hi
`);

    const violations = findMatrixConcurrencyEvictionViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("shared-unity-lock");
    expect(violations[0].message).toContain("strategy.max-parallel: 2");
  });

  test("allows matrix job whose concurrency.group expands ${{ matrix.unity-version }}", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency:
      group: unity-\${{ matrix.unity-version }}-\${{ matrix.test-mode }}
      cancel-in-progress: false
    strategy:
      matrix:
        unity-version:
          - "2021.3.45f1"
        test-mode:
          - editmode
    steps:
      - run: echo hi
`);
    expect(findMatrixConcurrencyEvictionViolations("test.yml", lines)).toEqual([]);
  });

  test("allows matrix job with shared concurrency.group when strategy.max-parallel: 1 is declared", () => {
    // This is the canonical Unity-Pro-license configuration: all four
    // Unity-credential-using jobs share `unity-pro-license` and rely on
    // matrix-internal serialization (`max-parallel: 1`) so matrix entries
    // never compete for the same group slot.
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency:
      group: unity-pro-license
      cancel-in-progress: false
    strategy:
      fail-fast: false
      max-parallel: 1
      matrix:
        unity-version:
          - "2021.3.45f1"
          - "2022.3.45f1"
        test-mode:
          - editmode
          - playmode
    steps:
      - run: echo hi
`);
    expect(findMatrixConcurrencyEvictionViolations("test.yml", lines)).toEqual([]);
  });

  test("allows matrix job with shared concurrency.group when queue max keeps pending entries", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency:
      group: shared-unity-lock
      cancel-in-progress: false
      queue: max
    strategy:
      matrix:
        unity-version:
          - "2021.3.45f1"
          - "2022.3.45f1"
    steps:
      - run: echo hi
`);
    expect(findMatrixConcurrencyEvictionViolations("test.yml", lines)).toEqual([]);
  });

  test("allows matrix job with no concurrency.group at all", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    strategy:
      matrix:
        unity-version:
          - "2021.3.45f1"
    steps:
      - run: echo hi
`);
    expect(findMatrixConcurrencyEvictionViolations("test.yml", lines)).toEqual([]);
  });

  test("allows non-matrix job with a static concurrency.group", () => {
    const lines = asLines(`
jobs:
  release:
    runs-on: ubuntu-latest
    concurrency:
      group: release-lock
      cancel-in-progress: false
    steps:
      - run: echo hi
`);
    expect(findMatrixConcurrencyEvictionViolations("test.yml", lines)).toEqual([]);
  });
});

describe("findConcurrencyQueueViolations", () => {
  test("flags queue max combined with cancel-in-progress true", () => {
    const lines = asLines(`
concurrency:
  group: ci
  cancel-in-progress: true
  queue: max
jobs:
  a:
    runs-on: ubuntu-latest
    steps:
      - run: echo
`);
    const violations = findConcurrencyQueueViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("cannot be combined");
  });
});

describe("findGameCiTestRunnerInputViolations", () => {
  test("flags unsupported game-ci/unity-test-runner inputs", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - uses: game-ci/unity-test-runner@v4
        with:
          projectPath: .unity-test-project
          targetPlatform: StandaloneWindows64
`);
    const violations = findGameCiTestRunnerInputViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("targetPlatform");
  });
});

describe("findUnityGameCiLockAndPreflightViolations", () => {
  const validUnityJob = `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Validate Unity license secrets
        uses: ./.github/actions/validate-unity-license
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: \${{ matrix.unity-version }}-\${{ matrix.test-mode }}
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run Unity Test Runner
        uses: game-ci/unity-test-runner@v4
        with:
          projectPath: .unity-test-project
      - name: Release organization Unity lock
        if: always()
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: \${{ matrix.unity-version }}-\${{ matrix.test-mode }}
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
`;

  test("allows game-ci when acquire, license preflight, and always release wrap it", () => {
    expect(findUnityGameCiLockAndPreflightViolations("test.yml", asLines(validUnityJob))).toEqual(
      []
    );
  });

  test("flags game-ci without acquire/preflight/release", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - uses: game-ci/unity-test-runner@v4
        with:
          projectPath: .unity-test-project
`);
    const violations = findUnityGameCiLockAndPreflightViolations("test.yml", lines);
    expect(violations).toHaveLength(3);
    expect(violations.map((violation) => violation.message).join("\n")).toContain(
      "central organization lock"
    );
    expect(violations.map((violation) => violation.message).join("\n")).toContain(
      "validating Unity license"
    );
    expect(violations.map((violation) => violation.message).join("\n")).toContain("if: always()");
  });

  test("flags lock preflight ordering, missing token env, and mismatched holder suffix", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: acquire
      - name: Validate Unity license secrets
        uses: ./.github/actions/validate-unity-license
      - name: Run Unity Test Runner
        uses: game-ci/unity-test-runner@v4
        with:
          projectPath: .unity-test-project
      - name: Release organization Unity lock
        if: always()
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: release
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
`);
    const messages = findUnityGameCiLockAndPreflightViolations("test.yml", lines)
      .map((violation) => violation.message)
      .join("\n");
    expect(messages).toContain("after acquiring the organization lock");
    expect(messages).toContain("acquire step must pass BUILD_LOCK_TOKEN");
    expect(messages).toContain("release holder-id-suffix must match");
  });
});

describe("findUnityReleaseModeViolations", () => {
  test.each([
    [
      "direct literal flags",
      `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -ReleaseCodeOptimization -ReleasePlayerBuild
`,
      []
    ],
    [
      "direct explicit true flags",
      `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -ReleaseCodeOptimization:$true -ReleasePlayerBuild:1
`,
      []
    ],
    [
      "hashtable splat flags",
      `
jobs:
  perf:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          $extra = @{
              ReleaseCodeOptimization = $true
              ReleasePlayerBuild = $true
          }
          ./scripts/unity/run-ci-tests.ps1 -TestMode '\${{ matrix.test-mode }}' @extra
`,
      []
    ],
    [
      "indexed hashtable splat flags",
      `
jobs:
  perf:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          $extra = @{}
          $extra['ReleaseCodeOptimization'] = $true
          $extra['ReleasePlayerBuild'] = $true
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode @extra
`,
      []
    ],
    [
      "game-ci custom parameter",
      `
jobs:
  experiment:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run GameCI
        uses: game-ci/unity-test-runner@v4
        with:
          customParameters: -nographics -releaseCodeOptimization
`,
      []
    ]
  ])("accepts %s", (_name, fixture, expected) => {
    expect(findUnityReleaseModeViolations("test.yml", asLines(fixture))).toEqual(expected);
  });

  test.each([
    [
      "direct run missing both Release flags",
      `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`,
      ["-ReleaseCodeOptimization", "-ReleasePlayerBuild"]
    ],
    [
      "direct run explicitly disables both Release flags",
      `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -ReleaseCodeOptimization:$false -ReleasePlayerBuild:0
`,
      ["-ReleaseCodeOptimization", "-ReleasePlayerBuild"]
    ],
    [
      "hashtable splat explicitly disables both Release flags",
      `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          $extra = @{
              ReleaseCodeOptimization = $false
              ReleasePlayerBuild = $false
          }
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode @extra
`,
      ["-ReleaseCodeOptimization", "-ReleasePlayerBuild"]
    ],
    [
      "indexed hashtable splat explicitly disables both Release flags",
      `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          $extra = @{}
          $extra['ReleaseCodeOptimization'] = $false
          $extra["ReleasePlayerBuild"] = $false
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode @extra
`,
      ["-ReleaseCodeOptimization", "-ReleasePlayerBuild"]
    ],
    [
      "hashtable splat uses nonliteral values",
      `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          $enabled = $env:UNITY_RELEASE_MODE
          $extra = @{
              ReleaseCodeOptimization = $enabled
              ReleasePlayerBuild = $enabled
          }
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode @extra
`,
      ["-ReleaseCodeOptimization", "-ReleasePlayerBuild"]
    ],
    [
      "direct run missing ReleasePlayerBuild",
      `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -ReleaseCodeOptimization
`,
      ["-ReleasePlayerBuild"]
    ],
    [
      "game-ci missing custom release optimization",
      `
jobs:
  experiment:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run GameCI
        uses: game-ci/unity-test-runner@v4
        with:
          customParameters: -nographics
`,
      ["-releaseCodeOptimization"]
    ]
  ])("flags %s", (_name, fixture, expectedSnippets) => {
    const messages = findUnityReleaseModeViolations("test.yml", asLines(fixture))
      .map((violation) => violation.message)
      .join("\n");
    for (const snippet of expectedSnippets) {
      expect(messages).toContain(snippet);
    }
  });

  test("skips disabled workflow archives", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);
    expect(findUnityReleaseModeViolations(".github/workflows-disabled/unity-tests.yml", lines)).toEqual(
      []
    );
  });
});

describe("findUnityLicenseReturnViolations", () => {
  const cleanRunCiJob = `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
      - name: Return Unity license
        if: always()
        uses: ./.github/actions/return-unity-license
        env:
          UNITY_EMAIL: \${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: \${{ secrets.UNITY_PASSWORD }}
      - name: Release organization Unity lock
        if: always()
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1
`;

  const cleanGameCiJob = `
jobs:
  experiment:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run GameCI
        uses: game-ci/unity-test-runner@v4
        with:
          projectPath: .unity-test-project
      - name: Return Unity license
        if: always()
        uses: ./.github/actions/return-unity-license
        env:
          UNITY_EMAIL: \${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: \${{ secrets.UNITY_PASSWORD }}
`;

  test("clean run-ci-tests.ps1 job with an ordered if:always() return -> []", () => {
    expect(findUnityLicenseReturnViolations("test.yml", asLines(cleanRunCiJob))).toEqual([]);
  });

  test("clean game-ci/unity-test-runner@v4 job with the return step -> []", () => {
    expect(findUnityLicenseReturnViolations("test.yml", asLines(cleanGameCiJob))).toEqual([]);
  });

  test("GenerateOnly-only run-ci-tests job does not require license return", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Generate ephemeral Unity project
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -GenerateOnly
`);
    expect(findUnityLicenseReturnViolations("test.yml", lines)).toEqual([]);
  });

  test("missing return step -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
      - name: Release organization Unity lock
        if: always()
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/release-build-lock@v1
`);
    const violations = findUnityLicenseReturnViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("no license return step");
  });

  test("case-varied run-ci-tests path still requires a license return step", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/RUN-CI-TESTS.ps1 -TestMode editmode
`);
    const violations = findUnityLicenseReturnViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("no license return step");
  });

  test("return step ordered BEFORE the Unity run -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Return Unity license
        if: always()
        uses: ./.github/actions/return-unity-license
        env:
          UNITY_EMAIL: \${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: \${{ secrets.UNITY_PASSWORD }}
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);
    const violations = findUnityLicenseReturnViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("BEFORE the Unity run");
  });

  test("return step without if: always() -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
      - name: Return Unity license
        uses: ./.github/actions/return-unity-license
        env:
          UNITY_EMAIL: \${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: \${{ secrets.UNITY_PASSWORD }}
`);
    const violations = findUnityLicenseReturnViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("if: always()");
  });

  // The return action runs Unity.exe -returnlicense -username $env:UNITY_EMAIL
  // -password $env:UNITY_PASSWORD; without those creds in env it warns + exit 0
  // (silent no-op) and the shared seat leaks. A correctly-ordered if:always()
  // step that is MISSING both creds must still be flagged.
  test("return step ordered + if:always() but MISSING the creds env -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
      - name: Return Unity license
        if: always()
        uses: ./.github/actions/return-unity-license
`);
    const violations = findUnityLicenseReturnViolations("test.yml", lines);
    // Both creds missing -> one violation per missing cred.
    expect(violations).toHaveLength(2);
    expect(violations[0].message).toContain("silently no-ops");
    expect(violations.map((v) => v.message).join("\n")).toContain("must pass UNITY_EMAIL");
    expect(violations.map((v) => v.message).join("\n")).toContain("must pass UNITY_PASSWORD");
  });

  test("return step missing only UNITY_PASSWORD -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
      - name: Return Unity license
        if: always()
        uses: ./.github/actions/return-unity-license
        env:
          UNITY_EMAIL: \${{ secrets.UNITY_EMAIL }}
`);
    const violations = findUnityLicenseReturnViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("must pass UNITY_PASSWORD");
    expect(violations[0].message).toContain("silently no-ops");
  });

  test("return step WITH both creds env -> no violation from this rule", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
      - name: Return Unity license
        if: always()
        uses: ./.github/actions/return-unity-license
        env:
          UNITY_EMAIL: \${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: \${{ secrets.UNITY_PASSWORD }}
`);
    expect(findUnityLicenseReturnViolations("test.yml", lines)).toEqual([]);
  });

  test("a job that does not run Unity natively -> []", () => {
    const lines = asLines(`
jobs:
  lint:
    runs-on: ubuntu-latest
    steps:
      - run: npm test
`);
    expect(findUnityLicenseReturnViolations("test.yml", lines)).toEqual([]);
  });

  test("disabled workflow path is exempt -> []", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);
    expect(
      findUnityLicenseReturnViolations(".github/workflows-disabled/unity-tests.yml", lines)
    ).toEqual([]);
  });
});

describe("findUnityNativeProvisioningViolations", () => {
  test("direct run-ci-tests job with pre-lock CiManagedOnly provisioning -> []", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    expect(findUnityNativeProvisioningViolations("test.yml", lines)).toEqual([]);
  });

  test("case-varied provisioning command is accepted", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $Editor = ./scripts/unity/ENSURE-EDITOR.ps1 -UnityVersion '2022.3.45f1' -CIManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "unity_editor_path=$Editor" | Out-File -FilePath $env:github_env -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    expect(findUnityNativeProvisioningViolations("test.yml", lines)).toEqual([]);
  });

  test("direct run-ci-tests job without provisioning -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("without a prior CI-managed editor provisioning");
  });

  test("CiManagedOnly provisioning without an explicit profile -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations.map((v) => v.message).join("\n")).toContain(
      "without an explicit provisioning profile"
    );
  });

  test("bare CiManagedOnly ensure-editor step without profile -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Warm Unity editor
        shell: pwsh
        run: ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("without an explicit provisioning profile");
  });

  test("commented provisioning profile argument does not satisfy explicit profile contract", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          # TODO: pass -ProvisioningProfile EditorOnly once scoped provisioning lands.
          Write-Host '-ProvisioningProfile EditorOnly is still pending'
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations.map((v) => v.message).join("\n")).toContain(
      "without an explicit provisioning profile"
    );
  });

  test("same-line loose profile text does not satisfy explicit profile contract", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          Write-Host '-ProvisioningProfile EditorOnly is still pending'; $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations.map((v) => v.message).join("\n")).toContain(
      "without an explicit provisioning profile"
    );
  });

  test("each CiManagedOnly ensure-editor invocation must pass its own profile", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          ./scripts/unity/ensure-editor.ps1 -UnityVersion '2021.3.45f1' -CiManagedOnly
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations.map((v) => v.message).join("\n")).toContain(
      "without an explicit provisioning profile"
    );
  });

  test("comment after backtick prevents profile line from joining ensure-editor invocation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 \`
            -UnityVersion '2022.3.45f1' \`
            -CiManagedOnly \` # this is not a PowerShell line continuation
            -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations.map((v) => v.message).join("\n")).toContain(
      "without an explicit provisioning profile"
    );
  });

  test("standalone run without StandaloneWindowsIl2Cpp profile -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode standalone
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations.map((v) => v.message).join("\n")).toContain(
      "does not provide StandaloneWindowsIl2Cpp"
    );
  });

  test("dynamic test-mode provisioning must provide standalone when matrix values are unknown", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode '\${{ matrix.test-mode }}'
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations.map((v) => v.message).join("\n")).toContain(
      "does not provide StandaloneWindowsIl2Cpp"
    );
  });

  test("dynamic test-mode provisioning rejects reversed profile mapping", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    strategy:
      matrix:
        test-mode:
          - editmode
          - standalone
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $provisioningProfile = if ('\${{ matrix.test-mode }}' -eq 'standalone') {
            'EditorOnly'
          } else {
            'StandaloneWindowsIl2Cpp'
          }
          $editor = ./scripts/unity/ensure-editor.ps1 \`
            -UnityVersion '2022.3.45f1' \`
            -CiManagedOnly \`
            -ProvisioningProfile $provisioningProfile
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode '\${{ matrix.test-mode }}'
`);

    const messages = findUnityNativeProvisioningViolations("test.yml", lines)
      .map((v) => v.message)
      .join("\n");
    expect(messages).toContain("does not provide EditorOnly");
    expect(messages).toContain("does not provide StandaloneWindowsIl2Cpp");
  });

  test("dynamic test-mode provisioning rejects profile variable overwritten after correct mapping", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    strategy:
      matrix:
        test-mode:
          - editmode
          - standalone
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $provisioningProfile = if ('\${{ matrix.test-mode }}' -eq 'standalone') {
            'StandaloneWindowsIl2Cpp'
          } else {
            'EditorOnly'
          }
          $provisioningProfile = if ('\${{ matrix.test-mode }}' -eq 'standalone') {
            'EditorOnly'
          } else {
            'StandaloneWindowsIl2Cpp'
          }
          $editor = ./scripts/unity/ensure-editor.ps1 \`
            -UnityVersion '2022.3.45f1' \`
            -CiManagedOnly \`
            -ProvisioningProfile $provisioningProfile
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode '\${{ matrix.test-mode }}'
`);

    const messages = findUnityNativeProvisioningViolations("test.yml", lines)
      .map((v) => v.message)
      .join("\n");
    expect(messages).toContain("does not provide EditorOnly");
    expect(messages).toContain("does not provide StandaloneWindowsIl2Cpp");
  });

  test("dynamic test-mode provisioning rejects profile variable overwritten by unknown value", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    strategy:
      matrix:
        test-mode:
          - editmode
          - standalone
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $provisioningProfile = if ('\${{ matrix.test-mode }}' -eq 'standalone') {
            'StandaloneWindowsIl2Cpp'
          } else {
            'EditorOnly'
          }
          $provisioningProfile = Get-Content profile.txt
          $editor = ./scripts/unity/ensure-editor.ps1 \`
            -UnityVersion '2022.3.45f1' \`
            -CiManagedOnly \`
            -ProvisioningProfile $provisioningProfile
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode '\${{ matrix.test-mode }}'
`);

    const messages = findUnityNativeProvisioningViolations("test.yml", lines)
      .map((v) => v.message)
      .join("\n");
    expect(messages).toContain("does not provide EditorOnly");
    expect(messages).toContain("does not provide StandaloneWindowsIl2Cpp");
  });

  test("benchmark edit/play-only test-mode matrix allows EditorOnly provisioning", () => {
    const lines = asLines(`
jobs:
  benchmarks:
    name: Benchmarks \${{ matrix.unity-version }} \${{ matrix.test-mode }}
    runs-on: [self-hosted, Windows, RAM-64GB]
    strategy:
      fail-fast: false
      max-parallel: 1
      matrix:
        unity-version: \${{ fromJSON(needs.matrix-config.outputs.unity-versions) }}
        test-mode:
          - editmode
          - playmode
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode '\${{ matrix.test-mode }}'
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(0);
  });

  test("direct run-ci-tests job with provisioning after acquire lock -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("after acquiring the organization lock");
  });

  test("direct run-ci-tests before provisioning -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(2);
    expect(violations.map((v) => v.message).join("\n")).toContain(
      "runs run-ci-tests.ps1 before the CI-managed editor is provisioned"
    );
    expect(violations.map((v) => v.message).join("\n")).toContain(
      "provisions the Unity editor after acquiring the organization lock"
    );
  });

  test("non-GenerateOnly direct run-ci-tests before acquire lock -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("before acquiring the organization lock");
  });

  test("GenerateOnly run may happen before acquire lock when provisioned", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Generate ephemeral Unity project
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -GenerateOnly
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
`);

    expect(findUnityNativeProvisioningViolations("test.yml", lines)).toEqual([]);
  });

  test("lowercase GenerateOnly switch may happen before acquire lock when provisioned", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Generate ephemeral Unity project
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -generateonly
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
`);

    expect(findUnityNativeProvisioningViolations("test.yml", lines)).toEqual([]);
  });

  test("-GenerateOnly false still requires the organization lock", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -GenerateOnly:$false
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("before acquiring the organization lock");
  });

  test("case-varied run-ci-tests path still requires the organization lock", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/RUN-CI-TESTS.ps1 -TestMode editmode
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("before acquiring the organization lock");
  });

  test("mixed GenerateOnly and real run in one step still requires the organization lock", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: |
          $editor = ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1' -CiManagedOnly -RequireHealthyExisting -ProvisioningProfile EditorOnly
          "UNITY_EDITOR_PATH=$editor" | Out-File -FilePath $env:GITHUB_ENV -Append
      - name: Generate then run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -GenerateOnly
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("before acquiring the organization lock");
  });

  test("provisioning without CiManagedOnly or UNITY_EDITOR_PATH export -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Provision Unity Editor
        shell: pwsh
        run: ./scripts/unity/ensure-editor.ps1 -UnityVersion '2022.3.45f1'
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    const violations = findUnityNativeProvisioningViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("ensure-editor.ps1 -CiManagedOnly");
  });

  test("disabled workflow path is exempt -> []", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);

    expect(
      findUnityNativeProvisioningViolations(".github/workflows-disabled/unity-tests.yml", lines)
    ).toEqual([]);
  });
});

describe("findForbiddenUnityLicenseSecretViolations", () => {
  test("clean workflow referencing only the serial-activation secrets -> []", () => {
    const lines = asLines(`
jobs:
  unity:
    steps:
      - name: Run Unity Test Runner
        env:
          UNITY_SERIAL: \${{ secrets.UNITY_SERIAL }}
          UNITY_EMAIL: \${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: \${{ secrets.UNITY_PASSWORD }}
        run: ./scripts/unity/run-ci-tests.ps1
`);
    expect(findForbiddenUnityLicenseSecretViolations("test.yml", lines)).toEqual([]);
  });

  test("retired secrets.UNITY_LICENSING_SERVER reference -> violation", () => {
    const lines = asLines(`
jobs:
  unity:
    steps:
      - name: Run Unity Test Runner
        env:
          UNITY_LICENSING_SERVER: \${{ secrets.UNITY_LICENSING_SERVER }}
        run: ./scripts/unity/run-ci-tests.ps1
`);
    const violations = findForbiddenUnityLicenseSecretViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("secrets.UNITY_LICENSING_SERVER");
  });

  test("bare env-var name in prose (no secrets. prefix) -> []", () => {
    const lines = asLines(`
jobs:
  unity:
    steps:
      - run: echo "remove the retired UNITY_LICENSING_SERVER after migration"
`);
    expect(findForbiddenUnityLicenseSecretViolations("test.yml", lines)).toEqual([]);
  });

  test("disabled workflow path is exempt -> []", () => {
    const lines = asLines(`
jobs:
  unity:
    steps:
      - env:
          UNITY_LICENSING_SERVER: \${{ secrets.UNITY_LICENSING_SERVER }}
        run: echo hi
`);
    expect(
      findForbiddenUnityLicenseSecretViolations(".github/workflows-disabled/unity-tests.yml", lines)
    ).toEqual([]);
  });
});

describe("findRequiredUnityLicenseSecretViolations", () => {
  // A Unity-native job must plumb all three serial-activation secrets somewhere
  // (secrets.UNITY_SERIAL / UNITY_EMAIL / UNITY_PASSWORD) or activation fails at
  // runtime. Emits one Violation per missing secret.
  const allThreeWired = `
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        env:
          UNITY_SERIAL: \${{ secrets.UNITY_SERIAL }}
          UNITY_EMAIL: \${{ secrets.UNITY_EMAIL }}
          UNITY_PASSWORD: \${{ secrets.UNITY_PASSWORD }}
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`;

  test("Unity-native job referencing all three serial secrets -> []", () => {
    expect(findRequiredUnityLicenseSecretViolations("test.yml", asLines(allThreeWired))).toEqual(
      []
    );
  });

  test("GenerateOnly-only run-ci-tests job does not require serial secrets", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Generate ephemeral Unity project
        shell: pwsh
        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode -GenerateOnly
`);
    expect(findRequiredUnityLicenseSecretViolations("test.yml", lines)).toEqual([]);
  });

  test("Unity-native job missing one serial secret -> 1 violation naming it", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        env:
          UNITY_SERIAL: \${{ secrets.UNITY_SERIAL }}
          UNITY_EMAIL: \${{ secrets.UNITY_EMAIL }}
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);
    const violations = findRequiredUnityLicenseSecretViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("secrets.UNITY_PASSWORD");
  });

  test("Unity-native job missing all three serial secrets -> 3 violations", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);
    const violations = findRequiredUnityLicenseSecretViolations("test.yml", lines);
    expect(violations).toHaveLength(3);
    const joined = violations.map((v) => v.message).join("\n");
    expect(joined).toContain("secrets.UNITY_SERIAL");
    expect(joined).toContain("secrets.UNITY_EMAIL");
    expect(joined).toContain("secrets.UNITY_PASSWORD");
  });

  test("case-varied run-ci-tests path still requires serial secrets", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/RUN-CI-TESTS.ps1 -TestMode editmode
`);
    const violations = findRequiredUnityLicenseSecretViolations("test.yml", lines);
    expect(violations).toHaveLength(3);
  });

  test("game-ci/unity-test-runner@v4 job is treated as Unity-native too", () => {
    const lines = asLines(`
jobs:
  experiment:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run GameCI
        uses: game-ci/unity-test-runner@v4
        with:
          projectPath: .unity-test-project
`);
    const violations = findRequiredUnityLicenseSecretViolations("test.yml", lines);
    expect(violations).toHaveLength(3);
  });

  test("workflow with no Unity-native job -> [] (secrets not required)", () => {
    const lines = asLines(`
jobs:
  lint:
    runs-on: ubuntu-latest
    steps:
      - run: npm test
`);
    expect(findRequiredUnityLicenseSecretViolations("test.yml", lines)).toEqual([]);
  });

  test("disabled workflow path is exempt even when secrets are missing -> []", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        shell: pwsh
        run: |
          ./scripts/unity/run-ci-tests.ps1 -TestMode editmode
`);
    expect(
      findRequiredUnityLicenseSecretViolations(".github/workflows-disabled/unity-tests.yml", lines)
    ).toEqual([]);
  });
});

describe("extractJobMatrixMaxParallel", () => {
  function jobOf(text) {
    const lines = asLines(text);
    const jobs = extractJobs(lines);
    return { lines, job: jobs[0] };
  }

  test("returns the integer value for `max-parallel: 1`", () => {
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    strategy:
      max-parallel: 1
      matrix:
        unity-version:
          - "2021.3.45f1"
    steps:
      - run: echo hi
`);
    expect(extractJobMatrixMaxParallel(lines, job)).toBe(1);
  });

  test("returns the integer value for `max-parallel: 4`", () => {
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: ubuntu-latest
    strategy:
      max-parallel: 4
      matrix:
        node:
          - 20
          - 22
    steps:
      - run: echo hi
`);
    expect(extractJobMatrixMaxParallel(lines, job)).toBe(4);
  });

  test('returns the integer for a double-quoted scalar `max-parallel: "1"`', () => {
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: ubuntu-latest
    strategy:
      max-parallel: "1"
      matrix:
        node:
          - 20
    steps:
      - run: echo hi
`);
    expect(extractJobMatrixMaxParallel(lines, job)).toBe(1);
  });

  test("returns the integer for a single-quoted scalar `max-parallel: '1'`", () => {
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: ubuntu-latest
    strategy:
      max-parallel: '1'
      matrix:
        node:
          - 20
    steps:
      - run: echo hi
`);
    expect(extractJobMatrixMaxParallel(lines, job)).toBe(1);
  });

  test("returns null when `max-parallel:` is absent", () => {
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: ubuntu-latest
    strategy:
      matrix:
        node:
          - 20
    steps:
      - run: echo hi
`);
    expect(extractJobMatrixMaxParallel(lines, job)).toBeNull();
  });

  test("returns null when the value is non-integer (expression form)", () => {
    // We refuse to statically resolve ${{ ... }} expressions; a dynamic
    // value cannot be guaranteed to be 1.
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: ubuntu-latest
    strategy:
      max-parallel: \${{ vars.MAX_PARALLEL }}
      matrix:
        node:
          - 20
    steps:
      - run: echo hi
`);
    expect(extractJobMatrixMaxParallel(lines, job)).toBeNull();
  });

  test("returns null when there is no strategy block", () => {
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: ubuntu-latest
    steps:
      - run: echo hi
`);
    expect(extractJobMatrixMaxParallel(lines, job)).toBeNull();
  });
});

describe("extractJobTimeoutMinutes", () => {
  function jobOf(text) {
    const lines = asLines(text);
    const jobs = extractJobs(lines);
    return { lines, job: jobs[0] };
  }

  test("returns the integer value for a bare `timeout-minutes: 90`", () => {
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    timeout-minutes: 90
    steps:
      - run: echo hi
`);
    expect(extractJobTimeoutMinutes(lines, job)).toBe(90);
  });

  test('returns the integer value for a quoted `timeout-minutes: "120"`', () => {
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    timeout-minutes: "120"
    steps:
      - run: echo hi
`);
    expect(extractJobTimeoutMinutes(lines, job)).toBe(120);
  });

  test("returns null when no job-level timeout-minutes is declared", () => {
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - run: echo hi
`);
    expect(extractJobTimeoutMinutes(lines, job)).toBeNull();
  });

  test("is not confused by a deeper-indented strategy.max-parallel timeout sibling", () => {
    // The job-level scan must match ONLY a `timeout-minutes:` line at exactly
    // job.indent + 2; a deeper-indented strategy key (or a step-level
    // timeout) must not be mistaken for the job timeout.
    const { lines, job } = jobOf(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    strategy:
      max-parallel: 1
      matrix:
        unity-version:
          - "2021.3.45f1"
    steps:
      - name: Run Unity Test Runner
        timeout-minutes: 120
        run: echo hi
`);
    expect(extractJobTimeoutMinutes(lines, job)).toBeNull();
  });
});

describe("extractStepTimeoutMinutes", () => {
  function firstStep(text) {
    const lines = asLines(text);
    const jobs = extractJobs(lines);
    const steps = extractJobSteps(lines, jobs[0]);
    return { lines, step: steps[0] };
  }

  test("returns the integer value for a step `timeout-minutes: 120`", () => {
    const { lines, step } = firstStep(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        timeout-minutes: 120
        run: ./scripts/unity/run-ci-tests.ps1
`);
    expect(extractStepTimeoutMinutes(lines, step)).toBe(120);
  });

  test("returns null when the step declares no step-level timeout-minutes", () => {
    const { lines, step } = firstStep(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Run Unity Test Runner
        run: ./scripts/unity/run-ci-tests.ps1
`);
    expect(extractStepTimeoutMinutes(lines, step)).toBeNull();
  });

  test("is NOT confused by a deeper-indented with.timeout-minutes action input", () => {
    // The acquire step carries `with: { timeout-minutes: "300" }`. That deeper
    // input is the lock POLL budget, not the step's own GitHub-Actions clock, so
    // extractStepTimeoutMinutes (which scans only the step key indent) must
    // ignore it and return null.
    const { lines, step } = firstStep(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          timeout-minutes: "300"
`);
    expect(extractStepTimeoutMinutes(lines, step)).toBeNull();
  });
});

describe("findUnityLockTimeoutViolations", () => {
  // Builds a Unity-licensed job that acquires the lock then runs the Unity
  // execution step. The run step uses `run-ci-tests.ps1` so the step-timeout
  // rule recognizes it; pass runStepTimeout=null to omit the step timeout.
  function unityLockJob({ jobTimeout, acquireTimeout, runStepTimeout = 120 }) {
    const jobTimeoutLine = jobTimeout === null ? "" : `\n    timeout-minutes: ${jobTimeout}`;
    const runTimeoutLine =
      runStepTimeout === null ? "" : `\n        timeout-minutes: ${runStepTimeout}`;
    return asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]${jobTimeoutLine}
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: editmode
          timeout-minutes: "${acquireTimeout}"
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run Unity Test Runner${runTimeoutLine}
        run: ./scripts/unity/run-ci-tests.ps1
`);
  }

  test("clean pass when job timeout >= acquire timeout + run budget", () => {
    // Mirrors the real workflows: job 420 / acquire "300" / run step 120.
    const lines = unityLockJob({ jobTimeout: 420, acquireTimeout: 300, runStepTimeout: 120 });
    expect(findUnityLockTimeoutViolations("test.yml", lines)).toEqual([]);
  });

  test("case-varied run-ci-tests path is recognized for the step timeout guard", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    timeout-minutes: 420
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: editmode
          timeout-minutes: "300"
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run Unity Test Runner
        run: ./scripts/unity/RUN-CI-TESTS.ps1
`);
    const violations = findUnityLockTimeoutViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("step-level timeout-minutes");
  });

  test("flags a job whose timeout is below acquire + run budget", () => {
    // job 290 < acquire 180 + budget 120 (= 300) -> numeric violation. The run
    // step timeout (120) stays valid (>= budget and strictly below job 290), so
    // ONLY the numeric violation fires.
    const lines = unityLockJob({ jobTimeout: 290, acquireTimeout: 180, runStepTimeout: 120 });
    const violations = findUnityLockTimeoutViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("job_timeout (290)");
    expect(violations[0].message).toContain("acquire_timeout (180)");
    expect(violations[0].message).toContain("RUN_BUDGET (120)");
  });

  test("numeric violation cites the acquire step's timeout-minutes input line", () => {
    // B2: the citation must point at the acquire `timeout-minutes:` line, not
    // the acquire step header. In this fixture the acquire header is line 6 and
    // its `timeout-minutes:` input is line 11.
    const lines = unityLockJob({ jobTimeout: 290, acquireTimeout: 180, runStepTimeout: 120 });
    const violations = findUnityLockTimeoutViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(lines[violations[0].line - 1].trim()).toMatch(/^timeout-minutes:\s*"180"$/);
  });

  test("flags a job that acquires the lock but declares no job-level timeout", () => {
    const lines = unityLockJob({ jobTimeout: null, acquireTimeout: 300 });
    const violations = findUnityLockTimeoutViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("declares no");
    expect(violations[0].message).toContain("squat the shared");
  });

  test("ignores a job that does not acquire the organization lock", () => {
    const lines = asLines(`
jobs:
  build:
    runs-on: ubuntu-latest
    timeout-minutes: 5
    steps:
      - run: echo hi
`);
    expect(findUnityLockTimeoutViolations("test.yml", lines)).toEqual([]);
  });

  test("skips the numeric check when the acquire timeout is an unquoted ${{ }} expression", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    timeout-minutes: 420
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: editmode
          timeout-minutes: \${{ vars.LOCK_TIMEOUT }}
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run Unity Test Runner
        timeout-minutes: 120
        run: ./scripts/unity/run-ci-tests.ps1
`);
    expect(findUnityLockTimeoutViolations("test.yml", lines)).toEqual([]);
  });

  test("skips the numeric check when the acquire timeout is a QUOTED ${{ }} expression", () => {
    // C2(a): a quoted expression value must also be treated as non-numeric.
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    timeout-minutes: 420
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: editmode
          timeout-minutes: "\${{ vars.LOCK_TIMEOUT }}"
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run Unity Test Runner
        timeout-minutes: 120
        run: ./scripts/unity/run-ci-tests.ps1
`);
    expect(findUnityLockTimeoutViolations("test.yml", lines)).toEqual([]);
  });

  test("skips the numeric check when the acquire with: has no timeout-minutes key (job-timeout still enforced)", () => {
    // C2(b): no acquire timeout-minutes input -> numeric comparison cannot run,
    // but the job-timeout-presence error must still fire when it is missing.
    const withKeyButNoTimeout = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    timeout-minutes: 420
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: editmode
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run Unity Test Runner
        timeout-minutes: 120
        run: ./scripts/unity/run-ci-tests.ps1
`);
    // Numeric check skipped (no acquire timeout-minutes) -> clean here.
    expect(findUnityLockTimeoutViolations("test.yml", withKeyButNoTimeout)).toEqual([]);

    const missingJobTimeout = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: editmode
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run Unity Test Runner
        timeout-minutes: 120
        run: ./scripts/unity/run-ci-tests.ps1
`);
    const violations = findUnityLockTimeoutViolations("test.yml", missingJobTimeout);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("declares no");
  });

  // --- Step-level Unity-run timeout rule (B1) ---

  test("step-timeout clean pass: job 420 / acquire 300 / run step 120 -> []", () => {
    const lines = unityLockJob({ jobTimeout: 420, acquireTimeout: 300, runStepTimeout: 120 });
    expect(findUnityLockTimeoutViolations("test.yml", lines)).toEqual([]);
  });

  test("step-timeout: missing run-step timeout -> 1 error", () => {
    const lines = unityLockJob({ jobTimeout: 420, acquireTimeout: 300, runStepTimeout: null });
    const violations = findUnityLockTimeoutViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("no step-level timeout-minutes");
    expect(violations[0].message).toContain("squat the shared Unity seat");
  });

  test("step-timeout: run-step timeout below the run budget -> 1 error", () => {
    const lines = unityLockJob({ jobTimeout: 420, acquireTimeout: 300, runStepTimeout: 60 });
    const violations = findUnityLockTimeoutViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("is below the run budget");
    expect(violations[0].message).toContain("(60)");
  });

  test("step-timeout: run-step timeout >= job timeout -> 1 error", () => {
    // step 420, job 420 -> not < job, so the step clock cannot fire first.
    const lines = unityLockJob({ jobTimeout: 420, acquireTimeout: 300, runStepTimeout: 420 });
    const violations = findUnityLockTimeoutViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("STRICTLY less than the job timeout");
    expect(violations[0].message).toContain("(420)");
  });

  test("step-timeout boundary: step 120, budget 120, job 420 stays clean", () => {
    // 120 is NOT < 120 (>= budget, OK) and 120 < 420 (strictly below job, OK).
    const lines = unityLockJob({ jobTimeout: 420, acquireTimeout: 300, runStepTimeout: 120 });
    expect(findUnityLockTimeoutViolations("test.yml", lines)).toEqual([]);
  });

  // --- GameCI-form run step recognition (isUnityRunStep via `uses:`) ---
  // The GameCI experiment runs Unity via `uses: game-ci/unity-test-runner@v4`
  // instead of `run: run-ci-tests.ps1`, but it acquires the same shared seat,
  // so the step-timeout rule must recognize the `uses:` form too.
  //
  // Builds a job whose post-acquire Unity step is the game-ci runner. Pass
  // runStepTimeout=null to omit the step-level timeout.
  function gameCiLockJob({ jobTimeout, acquireTimeout, runStepTimeout = 120 }) {
    const jobTimeoutLine = jobTimeout === null ? "" : `\n    timeout-minutes: ${jobTimeout}`;
    const runTimeoutLine =
      runStepTimeout === null ? "" : `\n        timeout-minutes: ${runStepTimeout}`;
    return asLines(`
jobs:
  game-ci-experiment:
    runs-on: [self-hosted, Windows, RAM-64GB]${jobTimeoutLine}
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: game-ci-experiment
          timeout-minutes: "${acquireTimeout}"
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run GameCI normal project mode${runTimeoutLine}
        uses: game-ci/unity-test-runner@v4
        env:
          UNITY_LICENSE: \${{ secrets.UNITY_LICENSE }}
`);
  }

  test("game-ci run step (uses form) with no step timeout -> 1 step-timeout error", () => {
    // The post-acquire Unity step is `uses: game-ci/unity-test-runner@v4` with
    // NO step-level timeout-minutes, so isUnityRunStep recognizes it and the
    // missing-step-timeout error fires exactly once.
    const lines = gameCiLockJob({ jobTimeout: 420, acquireTimeout: 300, runStepTimeout: null });
    const violations = findUnityLockTimeoutViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("no step-level timeout-minutes");
    expect(violations[0].message).toContain("squat the shared Unity seat");
  });

  test("game-ci run step (uses form) WITH timeout 120 / job 420 / acquire 300 -> []", () => {
    const lines = gameCiLockJob({ jobTimeout: 420, acquireTimeout: 300, runStepTimeout: 120 });
    expect(findUnityLockTimeoutViolations("test.yml", lines)).toEqual([]);
  });

  // --- Per-job isolation across TWO acquire-lock jobs in one workflow ---

  test("two acquire-lock jobs in one workflow: only the non-compliant job is flagged", () => {
    // One compliant job (job 420 / acquire "300" / run step 120) and one
    // non-compliant job (same numbers but MISSING the run-step timeout). Exactly
    // one violation must fire, and it must name the bad job -- proving per-job
    // isolation (the validator iterates jobs independently).
    const lines = asLines(`
jobs:
  unity-good:
    runs-on: [self-hosted, Windows, RAM-64GB]
    timeout-minutes: 420
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: good
          timeout-minutes: "300"
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run Unity Test Runner
        timeout-minutes: 120
        run: ./scripts/unity/run-ci-tests.ps1
  unity-bad:
    runs-on: [self-hosted, Windows, RAM-64GB]
    timeout-minutes: 420
    steps:
      - name: Acquire organization Unity lock
        uses: Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/acquire-build-lock@v1
        with:
          lock-name: wallstop-organization-builds
          holder-id-suffix: bad
          timeout-minutes: "300"
        env:
          BUILD_LOCK_TOKEN: \${{ secrets.ORG_BUILD_LOCK_TOKEN }}
      - name: Run Unity Test Runner
        run: ./scripts/unity/run-ci-tests.ps1
`);
    const violations = findUnityLockTimeoutViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("Job 'unity-bad'");
    expect(violations[0].message).not.toContain("unity-good");
    expect(violations[0].message).toContain("no step-level timeout-minutes");
  });
});

describe("findSelfHostedLabelAllowlistViolations", () => {
  test("allows the standard inline-array Windows-64GB label set", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - run: echo hi
`);
    expect(findSelfHostedLabelAllowlistViolations("test.yml", lines)).toEqual([]);
  });

  test("allows the fast Windows-64GB label set", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB, fast]
    steps:
      - run: echo hi
`);
    expect(findSelfHostedLabelAllowlistViolations("test.yml", lines)).toEqual([]);
  });

  test("treats label order as insignificant (order-insensitive equivalence)", () => {
    const lines = asLines(`
jobs:
  a:
    runs-on: [Windows, RAM-64GB, self-hosted]
    steps:
      - run: echo a
  b:
    runs-on: [fast, Windows, RAM-64GB, self-hosted]
    steps:
      - run: echo b
`);
    expect(findSelfHostedLabelAllowlistViolations("test.yml", lines)).toEqual([]);
  });

  test("flags typo'd casing such as RAM-64Gb", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64Gb]
    steps:
      - run: echo hi
`);
    const violations = findSelfHostedLabelAllowlistViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].severity).toBe("error");
    expect(violations[0].message).toContain("RAM-64Gb");
    expect(violations[0].message).toContain("not in the documented allowlist");
  });

  test("ignores hosted runners (no self-hosted label)", () => {
    const lines = asLines(`
jobs:
  ubuntu:
    runs-on: ubuntu-latest
    steps:
      - run: echo hi
  ubuntu-array:
    runs-on: [ubuntu-latest, large]
    steps:
      - run: echo hi
`);
    expect(findSelfHostedLabelAllowlistViolations("test.yml", lines)).toEqual([]);
  });

  test("flags multi-line block list self-hosted label sets that drift from allowlist", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on:
      - self-hosted
      - Windows
      - RAM-128GB
    steps:
      - run: echo hi
`);
    const violations = findSelfHostedLabelAllowlistViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("RAM-128GB");
  });

  test("accepts dynamic ${{ fromJSON(...) }} runs-on whose emitter produces allowlisted sets", () => {
    const lines = asLines(`
jobs:
  matrix-config:
    runs-on: ubuntu-latest
    outputs:
      runner-labels: \${{ steps.runners.outputs.labels }}
    steps:
      - id: runners
        shell: bash
        run: |
          if [[ "\${{ github.event_name }}" == "pull_request" ]]; then
            echo 'labels=["self-hosted","Windows","RAM-64GB","fast"]' >> "$GITHUB_OUTPUT"
          else
            echo 'labels=["self-hosted","Windows","RAM-64GB"]' >> "$GITHUB_OUTPUT"
          fi
  unity:
    needs: matrix-config
    runs-on: \${{ fromJSON(needs.matrix-config.outputs.runner-labels) }}
    steps:
      - run: echo hi
`);
    expect(findSelfHostedLabelAllowlistViolations("test.yml", lines)).toEqual([]);
  });

  test("flags dynamic runs-on whose emitter produces a forbidden label set", () => {
    const lines = asLines(`
jobs:
  matrix-config:
    runs-on: ubuntu-latest
    outputs:
      runner-labels: \${{ steps.runners.outputs.labels }}
    steps:
      - id: runners
        shell: bash
        run: |
          if [[ "\${{ github.event_name }}" == "pull_request" ]]; then
            echo 'labels=["self-hosted","Windows","RAM-64Gb"]' >> "$GITHUB_OUTPUT"
          else
            echo 'labels=["self-hosted","Windows","RAM-64GB"]' >> "$GITHUB_OUTPUT"
          fi
  unity:
    needs: matrix-config
    runs-on: \${{ fromJSON(needs.matrix-config.outputs.runner-labels) }}
    steps:
      - run: echo hi
`);
    const violations = findSelfHostedLabelAllowlistViolations("test.yml", lines);
    expect(violations.length).toBeGreaterThanOrEqual(1);
    expect(violations.some((v) => v.message.includes("RAM-64Gb"))).toBe(true);
  });

  test("flags dynamic runs-on whose emitter produces no labels= lines", () => {
    const lines = asLines(`
jobs:
  matrix-config:
    runs-on: ubuntu-latest
    outputs:
      runner-labels: \${{ steps.runners.outputs.labels }}
    steps:
      - id: runners
        shell: bash
        run: |
          echo "no labels declared here"
  unity:
    needs: matrix-config
    runs-on: \${{ fromJSON(needs.matrix-config.outputs.runner-labels) }}
    steps:
      - run: echo hi
`);
    const violations = findSelfHostedLabelAllowlistViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("emits no 'labels=[...]'");
  });
});

describe("extractEmittedLabelSetsFromBash", () => {
  test("parses single-quoted labels= JSON arrays from echo statements", () => {
    const runText = [
      'if [[ "x" == "pull_request" ]]; then',
      '  echo \'labels=["self-hosted","Windows","RAM-64GB","fast"]\' >> "$GITHUB_OUTPUT"',
      "else",
      '  echo \'labels=["self-hosted","Windows","RAM-64GB"]\' >> "$GITHUB_OUTPUT"',
      "fi"
    ].join("\n");
    expect(extractEmittedLabelSetsFromBash(runText)).toEqual([
      ["self-hosted", "Windows", "RAM-64GB", "fast"],
      ["self-hosted", "Windows", "RAM-64GB"]
    ]);
  });

  test("parses a bare labels=... assignment without surrounding bash quotes", () => {
    const runText = 'labels=["self-hosted","Windows","RAM-64GB"]';
    expect(extractEmittedLabelSetsFromBash(runText)).toEqual([
      ["self-hosted", "Windows", "RAM-64GB"]
    ]);
  });

  test("returns empty array when no labels= line is present", () => {
    expect(extractEmittedLabelSetsFromBash("echo hi")).toEqual([]);
  });

  test("returns null entry for malformed JSON in a labels= assignment", () => {
    const runText = "labels=[not-json]";
    expect(extractEmittedLabelSetsFromBash(runText)).toEqual([null]);
  });

  test("tolerates a label literal that contains a `]` character inside a JSON string", () => {
    // The previous regex `[^\]]*` would stop at the first `]`. With balanced-
    // bracket scanning that respects JSON string spans, the inner `]` is
    // captured as part of the label literal.
    const runText = `labels=["self-hosted","weird]name","RAM-64GB"]`;
    expect(extractEmittedLabelSetsFromBash(runText)).toEqual([
      ["self-hosted", "weird]name", "RAM-64GB"]
    ]);
  });
});

describe("scalar shorthand concurrency form (extractJobConcurrencyGroup)", () => {
  function singleJobLines(concurrencyLine) {
    return [
      "jobs:",
      "  unity-tests:",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      `    ${concurrencyLine}`,
      "    steps:",
      "      - run: echo hi"
    ];
  }

  test("recognizes bare scalar concurrency: wallstop-organization-builds", () => {
    const lines = singleJobLines("concurrency: wallstop-organization-builds");
    const jobs = extractJobs(lines);
    const result = extractJobConcurrencyGroup(lines, jobs[0]);
    expect(result).not.toBeNull();
    expect(result.group).toBe("wallstop-organization-builds");
    expect(result.cancelInProgress).toBeUndefined();
  });

  test('recognizes double-quoted scalar concurrency: "wallstop-organization-builds"', () => {
    const lines = singleJobLines('concurrency: "wallstop-organization-builds"');
    const jobs = extractJobs(lines);
    const result = extractJobConcurrencyGroup(lines, jobs[0]);
    expect(result).not.toBeNull();
    expect(result.group).toBe("wallstop-organization-builds");
  });

  test("recognizes single-quoted scalar concurrency: 'wallstop-organization-builds'", () => {
    const lines = singleJobLines("concurrency: 'wallstop-organization-builds'");
    const jobs = extractJobs(lines);
    const result = extractJobConcurrencyGroup(lines, jobs[0]);
    expect(result).not.toBeNull();
    expect(result.group).toBe("wallstop-organization-builds");
  });

  test("returns null for `concurrency: ~` (YAML null) and bare empty value", () => {
    const linesNull = singleJobLines("concurrency: ~");
    const linesEmpty = [
      "jobs:",
      "  unity-tests:",
      "    runs-on: [self-hosted, Windows, RAM-64GB]",
      "    concurrency:",
      "    steps:",
      "      - run: echo hi"
    ];
    const jobsNull = extractJobs(linesNull);
    const jobsEmpty = extractJobs(linesEmpty);
    expect(extractJobConcurrencyGroup(linesNull, jobsNull[0])).toBeNull();
    expect(extractJobConcurrencyGroup(linesEmpty, jobsEmpty[0])).toBeNull();
  });
});

describe("findForbiddenSharedConcurrencyViolations: shorthand and workflow-level", () => {
  test("flags scalar shorthand at job level (bare)", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency: wallstop-organization-builds
    steps:
      - run: echo hi
`);
    const violations = findForbiddenSharedConcurrencyViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("wallstop-organization-builds");
    expect(violations[0].message).toContain("forbidden");
  });

  test("flags scalar shorthand at job level (double-quoted)", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency: "wallstop-organization-builds"
    steps:
      - run: echo hi
`);
    const violations = findForbiddenSharedConcurrencyViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("wallstop-organization-builds");
  });

  test("flags scalar shorthand at job level (single-quoted)", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency: 'wallstop-organization-builds'
    steps:
      - run: echo hi
`);
    const violations = findForbiddenSharedConcurrencyViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("wallstop-organization-builds");
  });

  test("flags workflow-level inline mapping with sentinel name", () => {
    const lines = asLines(`
name: Unity Tests
concurrency:
  group: wallstop-organization-builds
  cancel-in-progress: false
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - run: echo hi
`);
    const violations = findForbiddenSharedConcurrencyViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("Workflow-level");
    expect(violations[0].message).toContain("wallstop-organization-builds");
  });

  test("flags workflow-level scalar shorthand with sentinel name", () => {
    const lines = asLines(`
name: Unity Tests
concurrency: wallstop-organization-builds
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - run: echo hi
`);
    const violations = findForbiddenSharedConcurrencyViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("Workflow-level");
    expect(violations[0].message).toContain("wallstop-organization-builds");
  });

  test("allows workflow-level non-sentinel concurrency", () => {
    const lines = asLines(`
name: Unity Tests
concurrency:
  group: \${{ github.workflow }}-\${{ github.ref }}
  cancel-in-progress: true
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    steps:
      - run: echo hi
`);
    expect(findForbiddenSharedConcurrencyViolations("test.yml", lines)).toEqual([]);
  });
});

describe("extractWorkflowConcurrencyGroup", () => {
  test("returns group + line for inline mapping workflow-level concurrency", () => {
    const lines = asLines(`
name: Unity Tests
concurrency: { group: foo, cancel-in-progress: false }
jobs:
  a:
    runs-on: ubuntu-latest
    steps:
      - run: echo
`);
    const result = extractWorkflowConcurrencyGroup(lines);
    expect(result).not.toBeNull();
    expect(result.group).toBe("foo");
  });

  test("returns null when no workflow-level concurrency exists", () => {
    const lines = asLines(`
name: Unity Tests
jobs:
  a:
    runs-on: ubuntu-latest
    concurrency: foo
    steps:
      - run: echo
`);
    expect(extractWorkflowConcurrencyGroup(lines)).toBeNull();
  });
});

describe("findMatrixConcurrencyEvictionViolations: scalar shorthand on a matrix job", () => {
  test("flags scalar shorthand on a matrix job missing matrix expansion", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency: shared-unity-lock
    strategy:
      matrix:
        unity-version:
          - "2021.3.45f1"
          - "2022.3.45f1"
    steps:
      - run: echo hi
`);
    const violations = findMatrixConcurrencyEvictionViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("shared-unity-lock");
  });

  test("allows scalar shorthand on a matrix job whose group expands ${{ matrix.* }}", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency: unity-\${{ matrix.unity-version }}
    strategy:
      matrix:
        unity-version:
          - "2021.3.45f1"
    steps:
      - run: echo hi
`);
    expect(findMatrixConcurrencyEvictionViolations("test.yml", lines)).toEqual([]);
  });
});

describe("findSelfHostedLabelAllowlistViolations: extra coverage", () => {
  test("flags `runs-on: 'self-hosted'` scalar quoted form for missing modifiers", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: 'self-hosted'
    steps:
      - run: echo hi
`);
    const violations = findSelfHostedLabelAllowlistViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("not in the documented allowlist");
  });

  test('flags `runs-on: "self-hosted"` scalar double-quoted form for missing modifiers', () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: "self-hosted"
    steps:
      - run: echo hi
`);
    const violations = findSelfHostedLabelAllowlistViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("not in the documented allowlist");
  });

  test("flags `runs-on: self-hosted` scalar bare form for missing modifiers", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: self-hosted
    steps:
      - run: echo hi
`);
    const violations = findSelfHostedLabelAllowlistViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("not in the documented allowlist");
  });

  test("flags trailing-comma inline label array with a clear error", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB,]
    steps:
      - run: echo hi
`);
    const violations = findSelfHostedLabelAllowlistViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("Trailing or duplicate comma");
  });

  test("matrix-include-only fixture: matrix.include emits allowlisted self-hosted entries", () => {
    // Even when the matrix uses include-only syntax (no top-level matrix
    // dimensions), the self-hosted label allowlist still applies to the
    // job's static runs-on declaration.
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB, fast]
    strategy:
      matrix:
        include:
          - unity-version: "2021.3.45f1"
            test-mode: editmode
          - unity-version: "2022.3.45f1"
            test-mode: playmode
    steps:
      - run: echo hi
`);
    expect(findSelfHostedLabelAllowlistViolations("test.yml", lines)).toEqual([]);
  });

  test("multiple \${{ matrix.* }} tokens in one expansion are accepted for matrix-eviction check", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency:
      group: unity-\${{ matrix.unity-version }}-\${{ matrix.test-mode }}-\${{ matrix.platform }}
      cancel-in-progress: false
    strategy:
      matrix:
        unity-version:
          - "2021.3.45f1"
        test-mode:
          - editmode
        platform:
          - windows
    steps:
      - run: echo hi
`);
    expect(findMatrixConcurrencyEvictionViolations("test.yml", lines)).toEqual([]);
  });
});

describe("parseInlineLabelArray: trailing comma rejection", () => {
  test("throws for `[a, b, c,]` trailing comma", () => {
    expect(() => parseInlineLabelArray("[a, b, c,]")).toThrow(/Trailing or duplicate comma/);
  });

  test("throws for `[a,,b]` duplicate comma", () => {
    expect(() => parseInlineLabelArray("[a,,b]")).toThrow(/Trailing or duplicate comma/);
  });

  test("accepts well-formed `[a, b, c]`", () => {
    expect(parseInlineLabelArray("[a, b, c]")).toEqual(["a", "b", "c"]);
  });
});

describe("extractJobNeeds", () => {
  function jobsFrom(text) {
    return extractJobs(asLines(text));
  }

  test("returns [] when no needs declared", () => {
    const lines = asLines(`
jobs:
  a:
    runs-on: ubuntu-latest
    steps:
      - run: echo
`);
    expect(
      extractJobNeeds(
        lines,
        jobsFrom(`
jobs:
  a:
    runs-on: ubuntu-latest
    steps:
      - run: echo
`)[0]
      )
    ).toEqual([]);
  });

  test("parses scalar form needs: matrix-config", () => {
    const text = `
jobs:
  unity:
    needs: matrix-config
    runs-on: ubuntu-latest
    steps:
      - run: echo
`;
    const lines = asLines(text);
    expect(extractJobNeeds(lines, jobsFrom(text)[0])).toEqual(["matrix-config"]);
  });

  test("parses inline-array form needs: [a, b]", () => {
    const text = `
jobs:
  unity:
    needs: [matrix-config, validate]
    runs-on: ubuntu-latest
    steps:
      - run: echo
`;
    const lines = asLines(text);
    expect(extractJobNeeds(lines, jobsFrom(text)[0])).toEqual(["matrix-config", "validate"]);
  });

  test("parses multi-line block list form", () => {
    const text = `
jobs:
  unity:
    needs:
      - matrix-config
      - validate
    runs-on: ubuntu-latest
    steps:
      - run: echo
`;
    const lines = asLines(text);
    expect(extractJobNeeds(lines, jobsFrom(text)[0])).toEqual(["matrix-config", "validate"]);
  });
});

describe("findDynamicRunsOnMissingNeedsViolations", () => {
  test("flags dynamic runs-on whose target job is not in needs", () => {
    const lines = asLines(`
jobs:
  matrix-config:
    runs-on: ubuntu-latest
    outputs:
      runner-labels: \${{ steps.runners.outputs.labels }}
    steps:
      - id: runners
        run: |
          echo 'labels=["self-hosted","Windows","RAM-64GB"]' >> "$GITHUB_OUTPUT"
  unity:
    runs-on: \${{ fromJSON(needs.matrix-config.outputs.runner-labels) }}
    steps:
      - run: echo hi
`);
    const violations = findDynamicRunsOnMissingNeedsViolations("test.yml", lines);
    expect(violations).toHaveLength(1);
    expect(violations[0].message).toContain("matrix-config");
    expect(violations[0].message).toContain("not in the job's needs:");
  });

  test("accepts dynamic runs-on whose target job is in needs (scalar)", () => {
    const lines = asLines(`
jobs:
  matrix-config:
    runs-on: ubuntu-latest
    outputs:
      runner-labels: \${{ steps.runners.outputs.labels }}
    steps:
      - id: runners
        run: |
          echo 'labels=["self-hosted","Windows","RAM-64GB"]' >> "$GITHUB_OUTPUT"
  unity:
    needs: matrix-config
    runs-on: \${{ fromJSON(needs.matrix-config.outputs.runner-labels) }}
    steps:
      - run: echo hi
`);
    expect(findDynamicRunsOnMissingNeedsViolations("test.yml", lines)).toEqual([]);
  });

  test("accepts dynamic runs-on whose target job is in needs (inline array)", () => {
    const lines = asLines(`
jobs:
  matrix-config:
    runs-on: ubuntu-latest
    outputs:
      runner-labels: \${{ steps.runners.outputs.labels }}
    steps:
      - id: runners
        run: echo
  validate:
    runs-on: ubuntu-latest
    steps:
      - run: echo
  unity:
    needs: [matrix-config, validate]
    runs-on: \${{ fromJSON(needs.matrix-config.outputs.runner-labels) }}
    steps:
      - run: echo hi
`);
    expect(findDynamicRunsOnMissingNeedsViolations("test.yml", lines)).toEqual([]);
  });

  test("ignores jobs that do not use the dynamic fromJSON pattern", () => {
    const lines = asLines(`
jobs:
  unity:
    runs-on: ubuntu-latest
    steps:
      - run: echo hi
`);
    expect(findDynamicRunsOnMissingNeedsViolations("test.yml", lines)).toEqual([]);
  });
});

describe("historical sentinel string remains searchable in error messages", () => {
  // The reviewer asked that future log readers be able to grep CI failure
  // text for the historical 'wallstop-organization-builds' incident name.
  // This test asserts at least one validator code path mentions it.
  test("at least one violation message in the sentinel-guard pipeline contains the incident name", () => {
    const lines = asLines(`
jobs:
  unity-tests:
    runs-on: [self-hosted, Windows, RAM-64GB]
    concurrency:
      group: wallstop-organization-builds
      cancel-in-progress: false
    steps:
      - run: echo hi
`);
    const violations = findForbiddenSharedConcurrencyViolations("test.yml", lines);
    expect(violations.some((v) => v.message.includes("wallstop-organization-builds"))).toBe(true);
  });
});

describe("unstick-run.yml workflow contract", () => {
  let doc;
  let raw;

  beforeAll(() => {
    raw = fs.readFileSync(UNSTICK_RUN_PATH, "utf8");
    doc = loadWorkflowYamlFromPath(UNSTICK_RUN_PATH);
  });

  test("file exists at .github/workflows/unstick-run.yml", () => {
    expect(fs.existsSync(UNSTICK_RUN_PATH)).toBe(true);
  });

  test("parses cleanly as YAML", () => {
    expect(doc).toBeTruthy();
    expect(typeof doc).toBe("object");
  });

  test("declares workflow_dispatch trigger with a required string run_id input", () => {
    const onBlock = getOnBlock(doc);
    expect(onBlock).toBeTruthy();
    expect(onBlock).toHaveProperty("workflow_dispatch");

    const dispatch = onBlock.workflow_dispatch;
    expect(dispatch).toBeTruthy();
    expect(dispatch.inputs).toBeTruthy();
    expect(dispatch.inputs.run_id).toBeTruthy();
    expect(dispatch.inputs.run_id.required).toBe(true);
    expect(dispatch.inputs.run_id.type).toBe("string");
  });

  test("run_id input has a non-empty description (so the GitHub UI dispatch dialog guides operators)", () => {
    // Positive smoke: the GitHub Actions "Run workflow" dropdown renders
    // input descriptions as helper text under each field. An empty or
    // missing description leaves the operator guessing what value to
    // paste, which is exactly the failure mode this workflow exists to
    // prevent (a wrong run id, dispatched at the wrong moment, cancels
    // the wrong run).
    const onBlock = getOnBlock(doc);
    const runIdInput = onBlock.workflow_dispatch.inputs.run_id;
    expect(typeof runIdInput.description).toBe("string");
    expect(runIdInput.description.trim().length).toBeGreaterThan(0);
  });

  test("declares optional force_redispatch and bypass_exclusion boolean inputs", () => {
    const onBlock = getOnBlock(doc);
    const inputs = onBlock.workflow_dispatch.inputs;
    expect(inputs.force_redispatch).toBeTruthy();
    expect(inputs.force_redispatch.type).toBe("boolean");
    expect(inputs.force_redispatch.default).toBe(false);
    expect(inputs.bypass_exclusion).toBeTruthy();
    expect(inputs.bypass_exclusion.type).toBe("boolean");
    expect(inputs.bypass_exclusion.default).toBe(false);
  });

  test("runs on ubuntu-latest", () => {
    expect(doc.jobs).toBeTruthy();
    const jobIds = Object.keys(doc.jobs);
    expect(jobIds).toHaveLength(1);
    const job = doc.jobs[jobIds[0]];
    expect(job["runs-on"]).toBe("ubuntu-latest");
  });

  test("declares exact permissions: { actions: write, contents: read }", () => {
    expect(doc.permissions).toEqual({
      actions: "write",
      contents: "read"
    });
  });

  test("does NOT declare contents: write (unstick workflow must never touch the state branch)", () => {
    // Defense in depth: even if the permissions block were ever rewritten,
    // a bare `contents: write` would be a regression.
    expect(doc.permissions).not.toHaveProperty("contents", "write");
    expect(raw).not.toMatch(/contents:\s*write/);
  });

  test("declares a concurrency group keyed on inputs.run_id with cancel-in-progress: false", () => {
    expect(doc.concurrency).toBeTruthy();
    expect(doc.concurrency.group).toMatch(/unstick-run-\$\{\{\s*inputs\.run_id\s*\}\}/);
    expect(doc.concurrency["cancel-in-progress"]).toBe(false);
  });

  test("inline bash references MIN_AGE_SECONDS=30 (fresh-run guard)", () => {
    expect(raw).toMatch(/MIN_AGE_SECONDS:\s*"30"/);
  });

  test("inline bash invokes 'gh run cancel' (the recovery action)", () => {
    expect(raw).toMatch(/gh run cancel\s+"\$\{run_id\}"/);
  });

  test("inline bash validates run_id with a positive-integer regex", () => {
    expect(raw).toMatch(/\[\[\s+"\$\{run_id\}"\s+=~\s+\^\[0-9\]\+\$\s+\]\]/);
  });

  test("inline bash respects the workflow exclusion list with a bypass_exclusion opt-out", () => {
    expect(raw).toMatch(/DEFAULT_EXCLUDED_WORKFLOWS:\s*"release\.yml"/);
    expect(raw).toMatch(/bypass_exclusion/);
  });

  test("optional REST re-dispatch is gated behind force_redispatch=true", () => {
    expect(raw).toMatch(/\$\{force_redispatch\}.*==.*"true"/s);
    expect(raw).toMatch(/actions\/workflows\/\$\{run_workflow_id\}\/dispatches/);
  });
});

describe("stuck-job-watchdog.yml tightened thresholds", () => {
  let raw;

  beforeAll(() => {
    raw = fs.readFileSync(STUCK_WATCHDOG_PATH, "utf8");
  });

  test("file exists", () => {
    expect(fs.existsSync(STUCK_WATCHDOG_PATH)).toBe(true);
  });

  test("schedule cron is exactly '*/5 * * * *' (tightened from */10)", () => {
    // Use a line-level regex so accidental whitespace or alternate cron
    // entries are still caught.
    expect(raw).toMatch(/-\s*cron:\s*"\*\/5 \* \* \* \*"/);
    // Negative assertion to catch a regression to the looser schedule.
    expect(raw).not.toMatch(/-\s*cron:\s*"\*\/10 \* \* \* \*"/);
  });

  test("MIN_QUEUE_AGE_SECONDS env value is 300 (tightened from 600)", () => {
    expect(raw).toMatch(/MIN_QUEUE_AGE_SECONDS:\s*"300"/);
    expect(raw).not.toMatch(/MIN_QUEUE_AGE_SECONDS:\s*"600"/);
  });

  test("does not self-cancel an in-flight watchdog audit", () => {
    // A later scheduled watchdog run must not cancel the currently-running
    // audit after it has cancelled a stuck run but before it has pushed the
    // state-branch counter. GitHub concurrency still limits execution to one
    // running audit while coalescing pending schedules.
    expect(raw).toMatch(
      /concurrency:[\s\S]*group:\s*stuck-job-watchdog[\s\S]*cancel-in-progress:\s*false/
    );
  });

  test("state branch fetch creates a remote-tracking ref before checkout", () => {
    // `git fetch origin watchdog-state` writes only FETCH_HEAD. The watchdog
    // must fetch an explicit refspec and then checkout that remote-tracking
    // ref, otherwise a cold runner fails with "pathspec 'watchdog-state' did
    // not match any file(s) known to git" before it can audit anything.
    expect(raw).toContain(
      'git_auth fetch origin "${STATE_BRANCH}:refs/remotes/origin/${STATE_BRANCH}"'
    );
    expect(raw).toContain(
      'git checkout -B "${STATE_BRANCH}" "refs/remotes/origin/${STATE_BRANCH}"'
    );
    expect(raw).not.toContain('git checkout "${STATE_BRANCH}"');
  });

  test("persists state immediately after each successful cancel", () => {
    expect(raw).toContain("persist_state_changes()");
    expect(raw).toContain('persist_state_changes "record cancel for run ${run_id}" || true');
    expect(raw).toContain('persist_state_changes "final state sync" || true');
    expect(raw).toContain("git diff-index --cached --quiet HEAD --");
    expect(raw).toContain('git rebase "refs/remotes/origin/${STATE_BRANCH}"');
  });

  test("validates watchdog state counters before bash arithmetic", () => {
    // Valid JSON can still be semantically corrupt, e.g.
    // {"cancels":"x","last_cancel":"y"}. Those values must reset rather than
    // flowing into `$(( ... ))` under `set -e`.
    expect(raw).toContain("is_nonnegative_integer()");
    expect(raw).toContain('is_nonnegative_integer "${parsed_cancels}"');
    expect(raw).toContain('is_nonnegative_integer "${parsed_last}"');
    expect(raw).toContain("state file corrupt; resetting");
  });
});
