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
// SYNC: Keep scripts/validate-unity-pr-policy.py LICENSED_LOCK_WINDOWS aligned.
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
  "line-endings",
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

function readWorkflow(file = "ci.yml") {
  return fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8");
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
  const source = readWorkflow();
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
  const source = readWorkflow();
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

test("script-test path detector covers harness and package contract inputs", () => {
  const source = readWorkflow();
  const scriptsPattern = new RegExp(extractShellPatternVariable(source, "scripts_pattern"));

  for (const path of [
    ".llm/index.md",
    ".llm/skills/github-workflow-consistency/references/workflow-consistency.md",
    "scripts/llm/harness.js",
    ".github/ISSUE_TEMPLATE/bug_report.yml",
    "docs/ops/release-operations.md",
    ".llm/context.md",
    ".llm/skills/package-publishing/references/unity-analyzer-shipping.md"
  ])
    assert.match(path, scriptsPattern);
});

test("static child jobs always report and fail closed on bad change detection", () => {
  const source = readWorkflow();

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

test("committed line endings are gated, not silently repaired", () => {
  const source = readWorkflow();
  const job = getJobBlock(source, "line-endings");

  // Every tracked text file is in scope, so this job is intentionally unfiltered.
  assert.doesNotMatch(job, /\n    needs:/, "line-endings must not be path-filtered");

  // `--renormalize` exposes committed bytes that disagree with .gitattributes.
  const check = getStepBlock(job, "Verify committed bytes match .gitattributes");
  assert.match(check, /git add --renormalize \./);
  assert.match(check, /git diff --cached --quiet/);
  assert.match(check, /\n          exit 1\n/, "drift must fail the job");

  // CSharpier may repair its worktree, but that cannot replace this fail-closed gate.
  const repair = getStepBlock(getJobBlock(source, "csharpier"), "Normalize line endings");
  assert.match(repair, /git reset --hard/);
  assert.doesNotMatch(repair, /exit 1/);
});

test(".gitattributes declares text=auto, never a bare text", () => {
  const source = fs.readFileSync(path.join(REPO_ROOT, ".gitattributes"), "utf8");

  // GitHub-API commits can contain CRLF blobs without applying attributes. A bare
  // `text` then makes a pristine clone appear modified; `text=auto` leaves the
  // blob stable while the line-endings job reports the drift.
  const bare = source
    .split(/\r?\n/)
    .filter((line) => !line.trimStart().startsWith("#"))
    .filter((line) => /^\S+\s+text(\s|$)/.test(line));

  assert.deepEqual(
    bare,
    [],
    `.gitattributes must use 'text=auto', not a bare 'text': ${bare.join("; ")}`
  );
  assert.ok(
    /^\*\s+text=auto\s+eol=lf$/m.test(source),
    ".gitattributes must keep the default '* text=auto eol=lf' rule"
  );
});

test("the stuck-job watchdog never materializes the default branch", () => {
  const source = fs.readFileSync(path.join(WORKFLOW_DIR, "stuck-job-watchdog.yml"), "utf8");

  // Clone the state branch directly. Switching to it from a content-dirty default
  // branch caused 13 consecutive watchdog failures on 2026-07-29.
  const clones = [...source.matchAll(/git(?:_auth)? clone [^\n]*/g)].map((match) =>
    match[0].trim()
  );
  assert.equal(clones.length, 1, `expected exactly one clone; found: ${clones.join(" | ")}`);
  assert.match(
    clones[0],
    /--single-branch --branch "\$\{STATE_BRANCH\}"/,
    "the clone must be scoped to the state branch"
  );
  assert.doesNotMatch(
    source,
    /git checkout -B/,
    "switching between two populated trees is the failure mode being retired"
  );
});

test("source marker scan is tracked-file scoped and cannot self-match workflow text", () => {
  const source = readWorkflow();
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
  const source = readWorkflow();
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
    ["runner-bootstrap.yml", '[["self-hosted","Windows","RAM-64GB"]]'],
    ["unity-benchmarks.yml", '[["self-hosted","Windows","RAM-64GB"]]'],
    ["unity-tests.yml", '[["self-hosted","Windows","RAM-64GB"]]']
  ]);
  const preflightAction = `${LOCK_ACTION_PREFIX}check-unity-runner-availability@${LOCK_ACTION_SHA}${LOCK_ACTION_PIN.comment}`;
  const workflowSources = fs
    .readdirSync(WORKFLOW_DIR)
    .filter((file) => /\.ya?ml$/.test(file))
    .map(readWorkflow);

  for (const [file, labels] of runnerLabels) {
    const preflight = getJobBlock(readWorkflow(file), "runner-preflight", file);
    const contracts = [
      /\n    name: Self-hosted runner registration preflight\n/,
      /\n    runs-on: ubuntu-latest\n/,
      new RegExp(`uses: ${escapeRegExp(preflightAction)}`),
      /reader-app-id: \$\{\{ secrets\.BUILD_LOCK_READER_APP_ID \}\}/,
      /reader-app-private-key: \$\{\{ secrets\.BUILD_LOCK_READER_APP_PRIVATE_KEY \}\}/,
      new RegExp(`required-label-sets: '${escapeRegExp(labels)}'`)
    ];
    for (const contract of contracts) assert.match(preflight, contract, file);
    assert.doesNotMatch(preflight, /RUNNER_AUDIT_PAT|Soft pass|soft-pass|Require an online/i, file);
    if (file === "runner-bootstrap.yml")
      assert.doesNotMatch(preflight, /\n        run:|\.status|\$\{\{ inputs\.runner-label/);
  }
  const runnerAudit = readWorkflow("runner-bootstrap.yml");
  const runnerAuditJob = getJobBlock(runnerAudit, "bootstrap", "runner-bootstrap.yml");
  assert.match(runnerAudit, /^name: Runner Audit \(Windows\)$/m);
  assert.doesNotMatch(runnerAudit, /^\s+detect-only:$/m);
  assert.match(runnerAuditJob, /\n\s+DetectOnly = \$true\n/);
  assert.match(runnerAuditJob, /Join-Path \$env:RUNNER_TOOL_CACHE 'u6-v3'/);
  assert.doesNotMatch(runnerAuditJob, /\binputs\.detect-only\b/);
  // prettier-ignore
  const hostPrereqAction = fs.readFileSync(path.join(WORKFLOW_DIR, "..", "actions", "assert-unity-host-prereqs", "action.yml"), "utf8");
  assert.doesNotMatch(hostPrereqAction, /^\s+auto-install:$/m);
  assert.match(hostPrereqAction, /& \$scriptPath -DetectOnly/);
  assert.doesNotMatch(hostPrereqAction, /& \$scriptPath\s*$/m);
  for (const action of [acquire, returnLicense, classify, release, gate]) {
    const count = workflowSources.reduce((sum, source) => sum + source.split(action).length - 1, 0);
    assert.equal(count, UNITY_LOCK_WINDOWS.length, action);
  }

  for (const [file, jobId, licensedWorkName, emptyAware] of UNITY_LOCK_WINDOWS) {
    const label = `${file}:${jobId}`;
    const licensedCondition = `${emptyAware ? "steps\\.compute\\.outputs\\.is-empty != 'true' && " : ""}steps\\.acquire_lock\\.outputs\\.acquired == 'true'`;
    const job = getJobBlock(readWorkflow(file), jobId, file);
    assert.match(job, /\n    timeout-minutes: 900\n/, `${label}: lifecycle budget`);
    if (["perf-numbers.yml", "unity-benchmarks.yml", "unity-tests.yml"].includes(file)) {
      assert.match(job, /\n      fail-fast: false\n      max-parallel: 1\n/, `${label} fairness`);
    }
    for (const action of [acquire, returnLicense, classify, release, gate]) {
      assert.equal(job.split(action).length - 1, 1, `${label}: ${action}`);
    }

    // prettier-ignore
    const lifecycleNames = ["Require manually installed Unity editor", "Bind and preserve validated Unity editor", "Validate Unity license secrets", "Acquire organization Unity lock", "Require acquired Unity lock", "Upload Unity editor validation diagnostics", licensedWorkName, "Return Unity license", "Classify Unity cleanup evidence", "Release organization Unity lock", "Require confirmed Unity cleanup"];
    const positions = lifecycleNames.map((name) => job.indexOf(`      - name: ${name}`));
    const sortedPositions = [...positions].sort((a, b) => a - b);
    assert.ok(
      positions.every((position) => position >= 0),
      `${label} lifecycle steps must all exist`
    );
    assert.deepEqual(positions, sortedPositions, `${label} lifecycle order`);

    // prettier-ignore
    const [validationStep, bindingStep, credentialStep, acquireStep, requireStep, uploadStep, workStep, returnStep, classifyStep, releaseStep, gateStep] = lifecycleNames.map((name) => getStepBlock(job, name));

    // prettier-ignore
    const contracts = [
      [validationStep, /\n        timeout-minutes: 10\n/],
      [validationStep, /shell: pwsh -NoProfile -NonInteractive -Command "\. '\{0\}'"/, `${label}: the gate must not inherit a runner profile`],
      [validationStep, /-InstallRoot \(Join-Path \$env:RUNNER_TOOL_CACHE 'u6-v3'\)[\s\S]*-CiManagedOnly[\s\S]*-RequireHealthyExisting/, `${label}: validation must pin the trusted editor root and refuse fallback installs`],
      [bindingStep, /ConvertFrom-Json[\s\S]*\[string\]::Equals\(\$actual, \$expected, \[StringComparison\]::OrdinalIgnoreCase\)[\s\S]*Copy-Item -LiteralPath \$source -Destination \$destination -Force[\s\S]*UNITY_EDITOR_PATH=\$expected/, `${label}: bind must prove the canonical editor, preserve the evidence outside the workspace, then export the path`],
      [uploadStep, /path: \$\{\{ runner\.temp \}\}\/dx-unity-editor-validation\n/, `${label}: evidence upload must not depend on a step that may never run`],
      [credentialStep, /uses: \.\/\.github\/actions\/validate-unity-license/],
      [acquireStep, /\n        id: acquire_lock\n/],
      [requireStep, /\n        if: \$\{\{ steps\.acquire_lock\.outputs\.acquired != 'true' \}\}\n[\s\S]*\n        run: exit 1\n/],
      [workStep, new RegExp(`\\n        if: \\$\\{\\{ ${licensedCondition} \\}\\}\\n`), label],
      [workStep, /-LicenseReturnOwner Central/, `${label}: the trusted central action must own the post-activation return`],
      [returnStep, /\n        id: return_unity_license\n        if: \$\{\{ always\(\) && steps\.acquire_lock\.outputs\.acquired == 'true' \}\}\n/],
      [classifyStep, /\n        id: cleanup_classification\n        if: \$\{\{ always\(\) && steps\.acquire_lock\.outputs\.acquired == 'true' \}\}\n/],
      [classifyStep, /return-log-digest: \$\{\{ steps\.return_unity_license\.outputs\.return-log-digest \}\}/],
      [releaseStep, /\n        id: release_unity_lock\n        if: always\(\)\n        timeout-minutes: 5\n/],
      [releaseStep, /resource-cleanup-status: \$\{\{ steps\.cleanup_classification\.outputs\.resource-cleanup-status \}\}/],
      [gateStep, /\n        if: always\(\)\n        timeout-minutes: 2\n/],
      [gateStep, /classification-complete: \$\{\{ steps\.cleanup_classification\.outputs\.classification-complete \}\}/],
      [gateStep, /release-outcome: \$\{\{ steps\.release_unity_lock\.outcome \}\}/],
      [validationStep, /\.ci\/unity-helpers\/scripts\/unity\/ensure-editor\.ps1/],
      ...(["perf-numbers.yml", "unity-benchmarks.yml", "unity-tests.yml"].includes(file) || jobId === "unity-checks" ? [[workStep, /-UnityInstallRoot \(Join-Path \$env:RUNNER_TOOL_CACHE 'u6-v3'\)/, `${label}: licensed work and central return must use the same trusted editor root`]] : [])
    ];
    for (const [actual, contract, message] of contracts) assert.match(actual, contract, message);

    assert.doesNotMatch(validationStep, /\n {8}if:|\b(?:install-modules|uninstall)\b/i, label);
    assert.doesNotMatch(returnStep, /continue-on-error:/);

    const acquireHolder = /holder-id-suffix: (.+)\n/.exec(acquireStep);
    const releaseHolder = /holder-id-suffix: (.+)\n/.exec(releaseStep);
    const acquireRunner = /runner-id: (.+)\n/.exec(acquireStep);
    const releaseRunner = /runner-id: (.+)\n/.exec(releaseStep);
    // prettier-ignore
    for (const [identity, name] of [[acquireHolder, "acquire holder"], [releaseHolder, "release holder"], [acquireRunner, "acquire runner"], [releaseRunner, "release runner"]]) assert.ok(identity, `${label} ${name} identity`);
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

test("Unity CI defers every post-activation return to the central action", () => {
  for (const file of ["run-ci-tests.ps1", "export-unitypackage.ps1"]) {
    const source = fs.readFileSync(path.join(REPO_ROOT, "scripts", "unity", file), "utf8");
    assert.match(source, /\[ValidateSet\('Local', 'Central'\)\]/, file);
    assert.match(source, /\[string\]\$LicenseReturnOwner = 'Local'/, file);
    assert.match(
      source,
      /if \(\$hasLicenseCreds -and \$LicenseReturnOwner -eq 'Local'\) \{\s+Invoke-UnityLicenseReturn/,
      file
    );
  }
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
