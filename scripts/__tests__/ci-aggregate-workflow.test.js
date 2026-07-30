"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { walkFiles } = require("../lib/repo-files.js");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOW_DIR = path.join(REPO_ROOT, ".github", "workflows");
const LOCK_ACTION_PREFIX =
  "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/";

// Build-lock pins are bumped by Dependabot, so they are derived here rather than
// written as literals. What must hold is that each action group stays immutably
// pinned and identical at every call site.
// prettier-ignore
function resolveLockActionPin(actionNames) {
  const shas = new Map(); const comments = new Set(); const label = actionNames.join(", ");
  for (const filePath of [WORKFLOW_DIR, path.join(REPO_ROOT, ".github", "actions")].flatMap((root) => walkFiles(root, { match: (file) => /\.ya?ml$/.test(file) }))) {
    const source = fs.readFileSync(filePath, "utf8");
    for (const name of actionNames) {
      for (const match of source.matchAll(new RegExp(`${escapeRegExp(LOCK_ACTION_PREFIX + name)}@([0-9a-f]{40})([^\\S\\n]+#[^\\n]*)?`, "g"))) {
        shas.set(match[1], [...(shas.get(match[1]) || []), `${path.relative(REPO_ROOT, filePath)}:${name}`]);
        if ((match[2] || "").trim() !== "") comments.add(match[2].trimEnd());
      }
    }
  }
  assert.equal(shas.size, 1, `${label} must share one SHA; found ${JSON.stringify([...shas])}`);
  assert.ok(comments.size <= 1, `${label} version comments disagree: ${[...comments]}`);
  return { sha: [...shas.keys()][0], comment: [...comments][0] || "" };
}

// Acquire, preflight, and the PR-head guard ship in the build-lock release.
// Return/classify/release/require-confirmed carry the centralized cleanup policy.
// prettier-ignore
const [LOCK_ACTION_PIN, CLEANUP_POLICY_PIN] = [["check-unity-runner-availability", "acquire-build-lock", "require-current-pr-head"], ["return-unity-license", "classify-unity-cleanup-evidence", "release-build-lock", "require-confirmed-unity-cleanup"]].map((group) => resolveLockActionPin(group));
const LOCK_ACTION_SHA = LOCK_ACTION_PIN.sha;
const ACQUIRE_ACTION_SHA = LOCK_ACTION_PIN.sha;
const CLEANUP_POLICY_SHA = CLEANUP_POLICY_PIN.sha;
const UNITY_LOCK_WINDOWS = [
  ["unity-tests.yml", "unity-tests", "Run Unity Test Runner", true],
  ["unity-benchmarks.yml", "benchmarks", "Run Unity Test Runner", true],
  ["release.yml", "unity-checks", "Run Unity Test Runner", true],
  ["release.yml", "unitypackage", "Export the .unitypackage", false],
  ["perf-numbers.yml", "perf-benchmarks", "Run Unity Test Runner", true]
];

const CONSOLIDATED_WORKFLOWS = [
  "actionlint.yml",
  "csharpier-check.yml",
  "dotnet-tests.yml",
  "json-format-check.yml",
  "lint-doc-links.yml",
  "markdownlint.yml",
  "script-tests.yml",
  "spellcheck.yml",
  "validate-banner.yml",
  "validate-docs.yml",
  "validate-llms-txt.yml",
  "yaml-format-lint.yml"
];

const AGGREGATED_JOBS = [
  "changes",
  "actionlint",
  "markdownlint",
  "csharpier",
  "dotnet",
  "json-format",
  "spellcheck",
  "validate-banner",
  "validate-llms-txt",
  "yaml-format-lint",
  "script-tests",
  "validate-docs",
  "lint-doc-links"
];

// cspell:ignore ACDMRT
const STATIC_CHILD_JOBS = [
  ["actionlint", "actionlint"],
  ["markdownlint", "markdown"],
  ["csharpier", "csharpier"],
  ["dotnet", "dotnet"],
  ["json-format", "json"],
  ["spellcheck", "spellcheck"],
  ["validate-banner", "banner"],
  ["validate-llms-txt", "llms"],
  ["yaml-format-lint", "yaml"],
  ["script-tests", "scripts"],
  ["validate-docs", "docs"],
  ["lint-doc-links", "docs_links"]
];

function readCiWorkflow() {
  return fs.readFileSync(path.join(WORKFLOW_DIR, "ci.yml"), "utf8");
}

function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function getJobBlock(source, jobId, sourceName = "ci.yml") {
  const header = new RegExp(`^  ${escapeRegExp(jobId)}:\n`, "m");
  const match = header.exec(source);
  assert.ok(match, `${sourceName}:${jobId} job must exist`);

  const start = match.index;
  const rest = source.slice(start + match[0].length);
  const nextJob = /^  [A-Za-z0-9_-]+:\n/m.exec(rest);
  const end = nextJob ? start + match[0].length + nextJob.index : source.length;
  return source.slice(start, end);
}

function getStepBlock(jobBlock, stepName) {
  const marker = `      - name: ${stepName}\n`;
  const start = jobBlock.indexOf(marker);
  assert.notEqual(start, -1, `job must include step '${stepName}'`);

  const next = jobBlock.indexOf("\n      - name:", start + marker.length);
  return jobBlock.slice(start, next === -1 ? jobBlock.length : next);
}

function extractShellPatternVariable(source, variableName) {
  const initialPattern = new RegExp(`^\\s*${escapeRegExp(variableName)}='([^']*)'$`);
  const appendPattern = new RegExp(
    `^\\s*${escapeRegExp(variableName)}="\\$${escapeRegExp(variableName)}"'([^']*)'$`
  );

  const pieces = [];
  let collecting = false;
  for (const line of source.split(/\r?\n/)) {
    const initial = initialPattern.exec(line);
    if (initial) {
      pieces.push(initial[1]);
      collecting = true;
      continue;
    }

    if (!collecting) {
      continue;
    }

    const append = appendPattern.exec(line);
    if (!append) {
      break;
    }

    pieces.push(append[1]);
  }

  assert.ok(pieces.length > 0, `ci.yml must build ${variableName}`);
  return pieces.join("");
}

test("static CI checks stay consolidated behind CI Success", () => {
  const source = readCiWorkflow();
  const ciSuccess = getJobBlock(source, "ci-success");
  assert.match(ciSuccess, /\n    name: CI Success\n/);
  assert.match(ciSuccess, /\n    if: \$\{\{ always\(\) \}\}\n/);
  assert.match(ciSuccess, /uses: re-actors\/alls-green@[0-9a-f]{40}/);
  assert.match(ciSuccess, /allowed-skips: ""/);
  assert.match(ciSuccess, /allowed-failures: ""/);

  for (const job of AGGREGATED_JOBS) {
    assert.match(ciSuccess, new RegExp(`\\n      - ${job}\\n`), `CI Success must need ${job}`);
  }
});

test("change detector considers current and previous paths", () => {
  const source = readCiWorkflow();
  assert.match(source, /--jq '\.\[\] \| \.filename, \(\.previous_filename \/\/ empty\)'/);
  assert.match(
    source,
    /git diff --name-status --find-renames --diff-filter=(?=[A-Z]*A)(?=[A-Z]*C)(?=[A-Z]*D)(?=[A-Z]*M)(?=[A-Z]*R)(?=[A-Z]*T)[A-Z]+\b/
  );
  assert.match(source, /awk -F '\\t'/);
  assert.match(source, /\$1 ~ \/\^\[RC\]\//);
  assert.match(source, /git fetch --no-tags --depth=1 origin "\$\{before\}"/);
  assert.doesNotMatch(
    source,
    /repos\/\$\{\{ github\.repository \}\}\/compare/,
    "push detection must not use GitHub's compare files list because it is capped"
  );
});

test("script-test path detector covers .llm harness inputs and its generated index", () => {
  const source = readCiWorkflow();
  const scriptsPattern = new RegExp(extractShellPatternVariable(source, "scripts_pattern"));

  assert.match(".llm/index.md", scriptsPattern);
  assert.match(
    ".llm/skills/github-workflow-consistency/references/workflow-consistency.md",
    scriptsPattern
  );
  assert.match("scripts/llm/harness.js", scriptsPattern);
});

test("script-test path detector covers package-script contract reference surfaces", () => {
  const source = readCiWorkflow();
  const scriptsPattern = new RegExp(extractShellPatternVariable(source, "scripts_pattern"));

  assert.match(".github/ISSUE_TEMPLATE/bug_report.yml", scriptsPattern);
  assert.match("docs/ops/release-operations.md", scriptsPattern);
  assert.match(".llm/context.md", scriptsPattern);
  assert.match(
    ".llm/skills/package-publishing/references/unity-analyzer-shipping.md",
    scriptsPattern
  );
});

test("static child jobs always report and fail closed on bad change detection", () => {
  const source = readCiWorkflow();

  for (const [jobId, output] of STATIC_CHILD_JOBS) {
    const jobBlock = getJobBlock(source, jobId);
    assert.match(jobBlock, /\n    needs: changes\n/, `${jobId} must depend on changes`);
    assert.match(jobBlock, /\n    if: \$\{\{ always\(\) \}\}\n/, `${jobId} must always report`);

    const guard = getStepBlock(jobBlock, "Validate change detection");
    assert.match(
      guard,
      new RegExp(
        `\\n        if: \\$\\{\\{ needs\\.changes\\.result != 'success' \\|\\| ` +
          `\\(needs\\.changes\\.outputs\\.${escapeRegExp(output)} != 'true' && ` +
          `needs\\.changes\\.outputs\\.${escapeRegExp(output)} != 'false'\\) \\}\\}\\n`
      ),
      `${jobId} must reject missing or malformed change-detection output`
    );
    assert.match(guard, /\n          exit 1\n/, `${jobId} must fail closed from the guard step`);
    assert.match(
      jobBlock,
      new RegExp(`needs\\.changes\\.outputs\\.${escapeRegExp(output)} == 'false'`),
      `${jobId} must have an explicit skip-success branch`
    );
    assert.match(
      jobBlock,
      new RegExp(`needs\\.changes\\.outputs\\.${escapeRegExp(output)} != 'false'`),
      `${jobId} must gate expensive steps internally`
    );
  }
});

test("source marker scan is tracked-file scoped and cannot self-match workflow text", () => {
  const source = readCiWorkflow();
  const dotnet = getJobBlock(source, "dotnet");
  const markerScan = getStepBlock(dotnet, "Check source marker policy");
  const includedPathspecs = [
    "Runtime/**",
    "Editor/**",
    "SourceGenerators/**",
    "Tests/**",
    "*.cs",
    "*.csproj",
    "*.sln"
  ];
  const excludedScopes = ["--no-ignore", ".github", ".llm"];

  assert.match(markerScan, /source_pathspecs=\(/);
  assert.match(markerScan, /source_file_count=\$\(git ls-files -- "\$\{source_pathspecs\[@\]\}"/);
  assert.match(markerScan, /Scanning \$\{source_file_count\} tracked source files/);
  assert.match(markerScan, /git grep -n -E -I "\(TODO\|FIXME\)" -- "\$\{source_pathspecs\[@\]\}"/);

  for (const pathspec of includedPathspecs) {
    assert.match(markerScan, new RegExp(`'${escapeRegExp(pathspec)}'`));
  }

  for (const scope of excludedScopes) {
    assert.doesNotMatch(markerScan, new RegExp(escapeRegExp(scope)));
  }
});

test("script validators run once while script tests stay cross-platform", () => {
  const source = readCiWorkflow();
  const scriptTests = getJobBlock(source, "script-tests");
  const setupDotnet = getStepBlock(scriptTests, "Setup .NET");
  const validators = getStepBlock(scriptTests, "Run validators");

  assert.match(
    scriptTests,
    /os:\n          - ubuntu-latest\n          - macos-latest\n          - windows-latest/
  );
  assert.match(setupDotnet, /matrix\.os == 'ubuntu-latest'/);
  assert.match(validators, /matrix\.os == 'ubuntu-latest'/);
  assert.match(validators, /\n        run: npm run validate:all\n/);
});

test("standalone static-check workflows are not reintroduced", () => {
  for (const workflow of CONSOLIDATED_WORKFLOWS) {
    assert.equal(
      fs.existsSync(path.join(WORKFLOW_DIR, workflow)),
      false,
      `${workflow} is consolidated into ci.yml; do not restore it as a separate required gate`
    );
  }
});

test("copyable build-lock documentation follows the runner and App credential contract", () => {
  for (const relativePath of [
    "docs/ops/ci-and-github-settings.md",
    "docs/ops/ambiguous-release-migration.md"
  ]) {
    const source = fs.readFileSync(path.join(REPO_ROOT, relativePath), "utf8");
    const acquireExample = new RegExp(
      `uses: ${escapeRegExp(LOCK_ACTION_PREFIX)}acquire-build-lock@${ACQUIRE_ACTION_SHA}${escapeRegExp(LOCK_ACTION_PIN.comment)}[\\s\\S]*?\`\`\``
    ).exec(source);

    assert.ok(acquireExample, `${relativePath} must contain a copyable acquire example`);
    for (const binding of [
      /runner-id: \$\{\{ runner\.name \}\}/,
      /github-token: \$\{\{ github\.token \}\}/,
      /pull-request-number: \$\{\{ github\.event\.pull_request\.number \}\}/,
      /expected-head-sha: \$\{\{ github\.event\.pull_request\.head\.sha \}\}/,
      /BUILD_LOCK_APP_ID: \$\{\{ secrets\.BUILD_LOCK_APP_ID \}\}/,
      /BUILD_LOCK_APP_PRIVATE_KEY: \$\{\{ secrets\.BUILD_LOCK_APP_PRIVATE_KEY \}\}/
    ]) {
      assert.match(acquireExample[0], binding, relativePath);
    }
    assert.match(source, /`BUILD_LOCK_APP_ID`/, `${relativePath} must list the App ID secret`);
    assert.match(
      source,
      /`BUILD_LOCK_APP_PRIVATE_KEY`/,
      `${relativePath} must list the App key secret`
    );
  }
});
test("every Unity lock window releases with explicit cleanup proof", () => {
  const acquire = `uses: ${LOCK_ACTION_PREFIX}acquire-build-lock@${ACQUIRE_ACTION_SHA}${LOCK_ACTION_PIN.comment}`;
  const returnLicense = `uses: ${LOCK_ACTION_PREFIX}return-unity-license@${CLEANUP_POLICY_SHA}`;
  const classify = `uses: ${LOCK_ACTION_PREFIX}classify-unity-cleanup-evidence@${CLEANUP_POLICY_SHA}`;
  const release = `uses: ${LOCK_ACTION_PREFIX}release-build-lock@${CLEANUP_POLICY_SHA}`;
  const gate = `uses: ${LOCK_ACTION_PREFIX}require-confirmed-unity-cleanup@${CLEANUP_POLICY_SHA}`;
  const runnerLabels = new Map([
    ["perf-numbers.yml", '[["self-hosted","Windows","RAM-64GB","fast"]]'],
    ["release.yml", '[["self-hosted","Windows","RAM-64GB"]]'],
    ["unity-benchmarks.yml", '[["self-hosted","Windows","RAM-64GB"]]'],
    ["unity-tests.yml", '[["self-hosted","Windows","RAM-64GB"]]']
  ]);
  const preflightAction =
    `${LOCK_ACTION_PREFIX}check-unity-runner-availability@${LOCK_ACTION_SHA}` +
    LOCK_ACTION_PIN.comment;
  const workflowSources = fs
    .readdirSync(WORKFLOW_DIR)
    .filter((file) => /\.ya?ml$/.test(file))
    .map((file) => fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8"));

  for (const [file, labels] of runnerLabels) {
    const source = fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8");
    const preflight = getJobBlock(source, "runner-preflight", file);
    assert.match(preflight, /\n    runs-on: ubuntu-latest\n/, file);
    assert.match(preflight, new RegExp(`uses: ${escapeRegExp(preflightAction)}`), file);
    assert.match(preflight, /reader-app-id: \$\{\{ secrets\.BUILD_LOCK_READER_APP_ID \}\}/, file);
    assert.match(
      preflight,
      /reader-app-private-key: \$\{\{ secrets\.BUILD_LOCK_READER_APP_PRIVATE_KEY \}\}/,
      file
    );
    assert.match(preflight, new RegExp(`required-label-sets: '${escapeRegExp(labels)}'`), file);
    assert.doesNotMatch(preflight, /RUNNER_AUDIT_PAT|Soft pass|soft-pass/i, file);
  }

  for (const action of [acquire, returnLicense, classify, release, gate]) {
    assert.equal(
      workflowSources.reduce((count, source) => count + source.split(action).length - 1, 0),
      UNITY_LOCK_WINDOWS.length,
      action
    );
  }

  for (const [file, jobId, licensedWorkName, emptyAware] of UNITY_LOCK_WINDOWS) {
    const label = `${file}:${jobId}`;
    const licensedCondition =
      `${emptyAware ? "steps\\.compute\\.outputs\\.is-empty != 'true' && " : ""}` +
      "steps\\.acquire_lock\\.outputs\\.acquired == 'true'";
    const source = fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8");
    const job = getJobBlock(source, jobId, file);
    if (["perf-numbers.yml", "unity-benchmarks.yml", "unity-tests.yml"].includes(file)) {
      assert.match(job, /\n      fail-fast: false\n      max-parallel: 1\n/, `${label} fairness`);
    }
    for (const action of [acquire, returnLicense, classify, release, gate]) {
      assert.equal(job.split(action).length - 1, 1, `${label}: ${action}`);
    }

    const lifecycleNames = [
      "Acquire organization Unity lock",
      "Require acquired Unity lock",
      licensedWorkName,
      "Return Unity license",
      "Classify Unity cleanup evidence",
      "Release organization Unity lock",
      "Require confirmed Unity cleanup"
    ];
    const positions = lifecycleNames.map((name) => job.indexOf(`      - name: ${name}`));
    assert.ok(
      positions.every((position) => position >= 0),
      `${label} lifecycle steps must all exist`
    );
    assert.deepEqual(
      positions,
      [...positions].sort((a, b) => a - b),
      `${label} lifecycle order`
    );

    const acquireStep = getStepBlock(job, "Acquire organization Unity lock");
    const provisionStep = getStepBlock(job, "Provision Unity Editor");
    const requireStep = getStepBlock(job, "Require acquired Unity lock");
    const workStep = getStepBlock(job, licensedWorkName);
    const returnStep = getStepBlock(job, "Return Unity license");
    const classifyStep = getStepBlock(job, "Classify Unity cleanup evidence");
    const releaseStep = getStepBlock(job, "Release organization Unity lock");
    const gateStep = getStepBlock(job, "Require confirmed Unity cleanup");

    assert.match(acquireStep, /\n        id: acquire_lock\n/);
    assert.match(
      provisionStep,
      /-InstallRoot \(Join-Path \$env:RUNNER_TOOL_CACHE 'u6-v3'\)/,
      `${label}: central cleanup and provisioning must use the same trusted editor root`
    );
    if (file === "unity-tests.yml") {
      assert.doesNotMatch(
        provisionStep,
        /-RequireHealthyExisting/,
        `${label}: provisioning must be able to populate the trusted editor root`
      );
    }
    assert.match(
      requireStep,
      /\n        if: \$\{\{ steps\.acquire_lock\.outputs\.acquired != 'true' \}\}\n[\s\S]*\n        run: exit 1\n/
    );
    assert.match(
      workStep,
      new RegExp(`\\n        if: \\$\\{\\{ ${licensedCondition} \\}\\}\\n`),
      label
    );
    if (file === "unity-tests.yml") {
      assert.match(
        workStep,
        /-UnityInstallRoot \(Join-Path \$env:RUNNER_TOOL_CACHE 'u6-v3'\)/,
        `${label}: licensed work and central return must use the same trusted editor root`
      );
      assert.match(
        workStep,
        /-LicenseReturnOwner Central/,
        `${label}: the trusted central action must own the post-activation return`
      );
    }
    assert.match(
      returnStep,
      /\n        id: return_unity_license\n        if: \$\{\{ always\(\) && steps\.acquire_lock\.outputs\.acquired == 'true' \}\}\n/
    );
    assert.doesNotMatch(returnStep, /continue-on-error:/);
    assert.match(
      classifyStep,
      /\n        id: cleanup_classification\n        if: \$\{\{ always\(\) && steps\.acquire_lock\.outputs\.acquired == 'true' \}\}\n/
    );
    assert.match(
      classifyStep,
      /return-log-digest: \$\{\{ steps\.return_unity_license\.outputs\.return-log-digest \}\}/
    );
    assert.match(releaseStep, /\n        id: release_unity_lock\n        if: always\(\)\n/);
    assert.match(
      releaseStep,
      /resource-cleanup-status: \$\{\{ steps\.cleanup_classification\.outputs\.resource-cleanup-status \}\}/
    );
    assert.match(gateStep, /\n        if: always\(\)\n/);
    assert.match(
      gateStep,
      /classification-complete: \$\{\{ steps\.cleanup_classification\.outputs\.classification-complete \}\}/
    );
    assert.match(gateStep, /release-outcome: \$\{\{ steps\.release_unity_lock\.outcome \}\}/);

    const acquireHolder = /holder-id-suffix: (.+)\n/.exec(acquireStep);
    const releaseHolder = /holder-id-suffix: (.+)\n/.exec(releaseStep);
    const acquireRunner = /runner-id: (.+)\n/.exec(acquireStep);
    const releaseRunner = /runner-id: (.+)\n/.exec(releaseStep);
    assert.ok(acquireHolder, `${label} acquire holder identity`);
    assert.ok(releaseHolder, `${label} release holder identity`);
    assert.ok(acquireRunner, `${label} acquire runner identity`);
    assert.ok(releaseRunner, `${label} release runner identity`);
    assert.equal(releaseHolder?.[1], acquireHolder?.[1], `${label} holder identity`);
    assert.equal(releaseRunner?.[1], acquireRunner?.[1], `${label} runner identity`);

    assert.doesNotMatch(
      job,
      /\n    environment:/,
      `${label} must not require environment approval`
    );
    assert.doesNotMatch(job, /Delete private Unity cleanup evidence/, label);
  }
});

test("Unity scripts retain bounded return-at-start evidence", () => {
  const sources = [
    path.join("scripts", "unity", "run-ci-tests.ps1"),
    path.join("scripts", "unity", "export-unitypackage.ps1")
  ].map((file) => fs.readFileSync(path.join(REPO_ROOT, file), "utf8"));
  const source = sources.join("\n");

  assert.equal((source.match(/unity-return-preflight-/g) || []).length, 2);
  assert.equal((source.match(/Remove-Item -LiteralPath \$returnLogPath -Force/g) || []).length, 2);
  assert.equal(
    (source.match(/Add-Content -LiteralPath \$LogPath -Value "exit_return_rc=\$exitCode"/g) || [])
      .length,
    2
  );
  assert.doesNotMatch(
    fs.readFileSync(path.join(REPO_ROOT, "package.json"), "utf8"),
    /validate:unity-license-classifiers/
  );
});

test("Unity test CI defers exactly one post-activation return to the central action", () => {
  const source = fs.readFileSync(
    path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1"),
    "utf8"
  );
  assert.match(source, /\[ValidateSet\('Local', 'Central'\)\]/);
  assert.match(source, /\[string\]\$LicenseReturnOwner = 'Local'/);
  assert.match(
    source,
    /if \(\$hasLicenseCreds -and \$LicenseReturnOwner -eq 'Local'\) \{\s+Invoke-UnityLicenseReturn/
  );
});

test("Unity Tests classifies Dependabot pull requests by immutable PR author", () => {
  const source = fs.readFileSync(path.join(WORKFLOW_DIR, "unity-tests.yml"), "utf8");
  const preflight = getJobBlock(source, "runner-preflight", "unity-tests.yml");
  const licensed = getJobBlock(source, "unity-tests", "unity-tests.yml");
  const aggregate = getJobBlock(source, "unity-ci-success", "unity-tests.yml");

  for (const job of [preflight, licensed]) {
    assert.match(job, /github\.event\.pull_request\.user\.login != 'dependabot\[bot\]'/);
    assert.doesNotMatch(job, /github\.actor != 'dependabot\[bot\]'/);
  }
  assert.match(
    aggregate,
    /DEPENDABOT_PR: \$\{\{ github\.event_name == 'pull_request' && github\.event\.pull_request\.user\.login == 'dependabot\[bot\]' \}\}/
  );
  assert.doesNotMatch(aggregate, /github\.actor == 'dependabot\[bot\]'/);
});
// prettier-ignore
test("active workflows pin external actions and scope licensed credentials", () => {
  for (const filePath of [WORKFLOW_DIR, path.join(REPO_ROOT, ".github", "actions")].flatMap((root) => walkFiles(root, { match: (file) => /\.ya?ml$/.test(file) }))) {
    const source = fs.readFileSync(filePath, "utf8");
    for (const match of source.matchAll(/^\s*uses:\s+([^\s#]+)(?:\s+#.*)?$/gm)) { const action = match[1]; if (!action.startsWith("./") && !action.startsWith("docker://")) assert.match(action, /@[0-9a-f]{40}$/, `${path.relative(REPO_ROOT, filePath)}: ${action} must be immutable`); }
  }
  const files = [...new Set(UNITY_LOCK_WINDOWS.map(([file]) => file))];
  for (const file of files) {
    const source = fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8");
    const credentialPattern = /secrets\.(?:UNITY_(?:SERIAL|EMAIL|PASSWORD)|BUILD_LOCK_APP_(?:ID|PRIVATE_KEY))/g;
    const sourceCredentialCount = [...source.matchAll(credentialPattern)].length;
    const jobs = UNITY_LOCK_WINDOWS.filter(([candidate]) => candidate === file).map(([, jobId]) => getJobBlock(source, jobId, file));
    const licensedCredentialCount = jobs.reduce((count, job) => count + [...job.matchAll(credentialPattern)].length, 0);
    assert.equal(sourceCredentialCount, licensedCredentialCount, `${file}: credentials must be scoped to protected licensed jobs`);
  }
  for (const [file, jobId] of UNITY_LOCK_WINDOWS.filter(([file]) => ["perf-numbers.yml", "unity-tests.yml"].includes(file))) {
    const source = fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8");
    if (file === "perf-numbers.yml") assert.doesNotMatch(source, /\n  pull_request:|comment-perf-doc/, file);
    else assert.match(getJobBlock(source, jobId, file), /github\.event_name != 'pull_request'/, `${file}:${jobId}`);
  }
});

test("release workflows pin App write scopes and denied-push diagnostics", () => {
  const release = fs.readFileSync(path.join(WORKFLOW_DIR, "release.yml"), "utf8");
  const prepare = fs.readFileSync(path.join(WORKFLOW_DIR, "release-prepare.yml"), "utf8");
  const tag = fs.readFileSync(path.join(WORKFLOW_DIR, "release-tag.yml"), "utf8");
  // prettier-ignore
  for (const [name, source, pattern] of [
    ["prepare App scopes", getStepBlock(getJobBlock(prepare, "prepare"), "Generate the auto-commit GitHub App token"), /permission-contents: write[\s\S]*permission-pull-requests: write/],
    ["prepare fatal formatting", getStepBlock(getJobBlock(prepare, "prepare"), "Validate the prepared tree"), /^(?![\s\S]*\n        continue-on-error:)(?:(?!\n          set \+e\n)[\s\S])*\n          set -euo pipefail\n(?:(?!\n          set \+e\n)[\s\S])*\n          npm run format:check\n/],
    ["prepare validation before publishing", getJobBlock(prepare, "prepare"), /^(?:(?!\n      - name: Push|\b(?:git\s+[^\n]*\bpush|push origin|gh pr create)\b)[\s\S])*- name: Validate the prepared tree[\s\S]*- name: Push the release branch and open the PR[\s\S]*\n          recovery_dir="artifacts\/release-prepare"\n[\s\S]*git format-patch -1 --stdout/],
    ["prepare diagnostics", prepare, /- name: Push the release branch and open the PR[\s\S]*release branch push failure[\s\S]*has Contents: write[\s\S]*ruleset or branch rule[\s\S]*recovery patch was written/],
    ["prepare recovery upload", prepare, /- name: Upload failed release preparation patch[\s\S]*\n          path: artifacts\/release-prepare\/\n          if-no-files-found: ignore\n/],
    ["tag App scope", tag, /- name: Generate the auto-commit GitHub App token[\s\S]*\n          permission-contents: write\n/],
    ["tag diagnostics", tag, /- name: Create and push the annotated release tag[\s\S]*\n          push_status=\$\{PIPESTATUS\[0\]\}\n[\s\S]*release tag push failure[\s\S]*Manual fallback:/]
  ]) assert.match(source, pattern, name);
  assert.match(
    getStepBlock(getJobBlock(release, "verify-tag"), "Verify semver tag matches package.json"),
    /if \[ "\$\{GITHUB_REF_TYPE\}" != "tag" \]; then[\s\S]*exit 1/
  );
  assert.doesNotMatch(prepare, /\.artifacts\/release-prepare/);
});
