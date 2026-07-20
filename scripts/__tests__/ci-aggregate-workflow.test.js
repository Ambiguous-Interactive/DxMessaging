"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { walkFiles } = require("../lib/repo-files.js");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOW_DIR = path.join(REPO_ROOT, ".github", "workflows");
const LOCK_ACTION_SHA = "a00614ace745152a659c5c2654f7cefb68a5a628";
const ACQUIRE_ACTION_SHA = "a00614ace745152a659c5c2654f7cefb68a5a628";
const LOCK_ACTION_PREFIX =
  "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/";
const UNITY_LOCK_WINDOWS = [
  ["unity-tests.yml", "unity-tests", "Run Unity Test Runner", true],
  ["unity-gameci-experiment.yml", "game-ci-experiment", "Run GameCI normal project mode", true],
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

test("script-test path detector covers skills index inputs", () => {
  const source = readCiWorkflow();
  const scriptsPattern = new RegExp(extractShellPatternVariable(source, "scripts_pattern"));

  assert.match(".llm/skills/index.md", scriptsPattern);
  assert.match(".llm/skills/github-actions/workflow-consistency.md", scriptsPattern);
  assert.match("scripts/generate-skills-index.js", scriptsPattern);
});

test("script-test path detector covers package-script contract reference surfaces", () => {
  const source = readCiWorkflow();
  const scriptsPattern = new RegExp(extractShellPatternVariable(source, "scripts_pattern"));

  assert.match(".github/ISSUE_TEMPLATE/bug_report.yml", scriptsPattern);
  assert.match("docs/ops/release-operations.md", scriptsPattern);
  assert.match(".llm/context.md", scriptsPattern);
  assert.match(".llm/skills/packaging/unity-analyzer-shipping.md", scriptsPattern);
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
      `uses: ${escapeRegExp(LOCK_ACTION_PREFIX)}acquire-build-lock@${ACQUIRE_ACTION_SHA} # v1.9.1[\\s\\S]*?\`\`\``
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
// prettier-ignore
test("every Unity lock window releases with explicit cleanup proof", () => {
  const acquire = `uses: ${LOCK_ACTION_PREFIX}acquire-build-lock@${ACQUIRE_ACTION_SHA} # v1.9.1`;
  const release = `uses: ${LOCK_ACTION_PREFIX}release-build-lock@${LOCK_ACTION_SHA} # v1.9.1`;
  const runnerLabels = new Map([["perf-numbers.yml", '[["self-hosted","Windows","RAM-64GB","fast"]]'], ["release.yml", '[["self-hosted","Windows","RAM-64GB"]]'], ["unity-benchmarks.yml", '[["self-hosted","Windows","RAM-64GB"]]'], ["unity-gameci-experiment.yml", '[["self-hosted","Windows","RAM-64GB"]]'], ["unity-tests.yml", '[["self-hosted","Windows","RAM-64GB"]]']]); const preflightAction = `${LOCK_ACTION_PREFIX}check-unity-runner-availability@${LOCK_ACTION_SHA} # v1.9.1`; const workflowSources = fs.readdirSync(WORKFLOW_DIR).filter((file) => /\.ya?ml$/.test(file)).map((file) => fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8")); for (const [file, labels] of runnerLabels) { const source = fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8"); const preflight = getJobBlock(source, "runner-preflight", file); assert.match(preflight, /\n    runs-on: ubuntu-latest\n/, file); assert.match(preflight, new RegExp(`uses: ${escapeRegExp(preflightAction)}`), file); assert.match(preflight, /reader-app-id: \$\{\{ secrets\.BUILD_LOCK_READER_APP_ID \}\}/, file); assert.match(preflight, /reader-app-private-key: \$\{\{ secrets\.BUILD_LOCK_READER_APP_PRIVATE_KEY \}\}/, file); assert.match(preflight, new RegExp(`required-label-sets: '${escapeRegExp(labels)}'`), file); assert.doesNotMatch(preflight, /RUNNER_AUDIT_PAT|Soft pass|soft-pass/i, file); }
  assert.equal(workflowSources.reduce((count, source) => count + source.split(acquire).length - 1, 0), UNITY_LOCK_WINDOWS.length);
  assert.equal(workflowSources.reduce((count, source) => count + source.split(release).length - 1, 0), UNITY_LOCK_WINDOWS.length);
  for (const [file, jobId, licensedWorkName, emptyAware] of UNITY_LOCK_WINDOWS) {
    const label = `${file}:${jobId}`; const blockedCondition = `${emptyAware ? "steps\\.compute\\.outputs\\.is-empty != 'true' && " : ""}steps\\.acquire_lock\\.outputs\\.acquired != 'true'`; const licensedCondition = `${emptyAware ? "steps\\.compute\\.outputs\\.is-empty != 'true' && " : ""}steps\\.acquire_lock\\.outputs\\.acquired == 'true'`;
    const source = fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8");
    const job = getJobBlock(source, jobId, file);
    assert.equal(job.split(acquire).length - 1, 1, `${label} acquire count`);
    assert.equal(job.split(release).length - 1, 1, `${label} release count`);
    const positions = ["Acquire organization Unity lock", "Require acquired Unity lock", licensedWorkName, "Return Unity license", "Release organization Unity lock"].map((name) => job.indexOf(`      - name: ${name}`));
    assert.ok(positions.every((position) => position >= 0), `${label} lifecycle steps must all exist`);
    assert.deepEqual(positions, [...positions].sort((a, b) => a - b), `${label} lifecycle order`);
    const orderedContract = ["- name: Acquire organization Unity lock\\n        id: acquire_lock", escapeRegExp(acquire), "holder-id-suffix: (.+)\\n          runner-id: (.+)\\n", `- name: Require acquired Unity lock\\n        if: \\$\\{\\{ ${blockedCondition} \\}\\}\\n        shell: pwsh\\n        run: exit 1`, `- name: ${escapeRegExp(licensedWorkName)}[\\s\\S]*?if: \\$\\{\\{ ${licensedCondition} \\}\\}`, "- name: Return Unity license\\n        id: return_unity_license\\n        if: always\\(\\)\\n        timeout-minutes: 5\\n        continue-on-error: true\\n        uses: \\.\\/\\.github\\/actions\\/return-unity-license", `- name: Release organization Unity lock\\n        if: always\\(\\)\\n        ${escapeRegExp(release)}`, "holder-id-suffix: \\1\\n          runner-id: \\2\\n          resource-cleanup-status: \\$\\{\\{ steps\\.return_unity_license\\.outputs\\.resource-cleanup-status \\}\\}\\n          resource-health: \\$\\{\\{ steps\\.return_unity_license\\.outputs\\.resource-health \\}\\}\\n          resource-reason: \\$\\{\\{ steps\\.return_unity_license\\.outputs\\.resource-reason \\}\\}"].join("[\\s\\S]*?");
    assert.match(job, new RegExp(orderedContract), label);
    assert.doesNotMatch(job, /\n    environment:/, `${label} must not require environment approval`);
  }
});

// prettier-ignore
test("Unity return proof classifications remain fail closed and non-masking", () => {
  const actionSource = fs.readFileSync(path.join(REPO_ROOT, ".github", "actions", "return-unity-license", "action.yml"), "utf8"); const source = [actionSource, ...[path.join("scripts", "unity", "run-ci-tests.ps1"), path.join("scripts", "unity", "export-unitypackage.ps1")].map((file) => fs.readFileSync(path.join(REPO_ROOT, file), "utf8"))].join("\n");
  const classifications = [
    ["prior evidence is classified independently of command outcome", /function Test-PriorReturnEvidence[\s\S]*?Test-UnityLicenseReturnResourceSafe -ExitCode 0 -LogPath \$env:PRIOR_RETURN_LOG_PATH/],
    ["exact classifier", /Test-UnityLicenseReturnResourceSafe/],
    ["unknown default", /resource-cleanup-status=unknown/],
    ["confirmed helper", /function Set-ConfirmedCleanupOutput[\s\S]*?resource-cleanup-status=confirmed/],
    ["launch error falls back to exact prior evidence", /} catch \{[\s\S]*?Test-PriorReturnEvidence[\s\S]*?Set-ConfirmedCleanupOutput[\s\S]*?Unity license return hit an unexpected error/],
    ["non-masking", /Cleanup remains unknown and this runner will be quarantined\."[\s\S]*?exit 0/]
  ];
  for (const [classification, pattern] of classifications) assert.match(source, pattern, classification); assert.doesNotMatch(fs.readFileSync(path.join(REPO_ROOT, ".github", "workflows", "unity-benchmarks.yml"), "utf8"), /prior-command-succeeded/); assert.equal((source.match(/unity-return-preflight-/g) || []).length, 2); assert.equal((source.match(/Remove-Item -LiteralPath \$returnLogPath -Force/g) || []).length, 2);
  assert.ok(source.indexOf("resource-safe=false") < source.indexOf("$editorPath ="));
  assert.match(source, /resource-health[\s\S]*resource-reason/); assert.match(actionSource, /Classify-UnityAccountHealth\.ps1/); assert.doesNotMatch(actionSource, /Resolve-PythonApplication|python3?/i); assert.match(actionSource, /resource-safe:[\s\S]*?steps\.health\.outcome == 'success'\s+&&\s+steps\.health\.outputs\.resource-health == 'healthy'\s+&&\s+steps\.return_license\.outputs\.resource-safe == 'true'[\s\S]*?'true' \|\| 'false'/); assert.match(actionSource, /resource-cleanup-status:[\s\S]*?steps\.health\.outcome == 'success'\s+&&\s+steps\.health\.outputs\.resource-health == 'healthy'\s+&&\s+steps\.return_license\.outputs\.resource-cleanup-status == 'confirmed'[\s\S]*?'confirmed' \|\| 'unknown'/);
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
  assert.doesNotMatch(prepare, /\.artifacts\/release-prepare/);
});
