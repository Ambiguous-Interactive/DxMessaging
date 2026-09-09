"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const YAML = require("yaml");
const { spawnSync } = require("node:child_process");
const { walkFiles } = require("../lib/repo-files.js");
const {
  UNITY_LOCK_WINDOWS,
  UNITY_EDITOR_PROFILES,
  UNITY_EDITOR_CONSUMERS,
  DOCS_ONLY_PATH_IGNORES,
  STATIC_CHILD_JOBS,
  CORRECTNESS_CATEGORY_FILTERS
} = require("./workflow-test-vectors.json");
const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOW_DIR = path.join(REPO_ROOT, ".github", "workflows");
const LOCK_ACTION_PREFIX =
  "Ambiguous-Interactive/ambiguous-organization-build-lock/.github/actions/";
const FORBIDDEN_UNITY_HELPERS =
  /(?:^|[^a-z0-9-])(?:ambiguous-interactive|wallstop)\/unity-helpers(?:[^a-z0-9-]|$)/i;
// Build-lock pins are excluded from Dependabot (the github-actions ignore block
// in .github/dependabot.yml), so humans bump them together with the docs examples
// that copy the same immutable commits. Each group stays immutably pinned and
// identical at every call site.
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

// Acquire, the editor gate, preflight, and the PR-head guard ship in the
// build-lock release. Return/classify/release/require-confirmed carry the
// centralized cleanup policy.
// prettier-ignore
const [LOCK_ACTION_PIN, CLEANUP_POLICY_PIN] = [["check-unity-runner-availability", "acquire-build-lock", "release-build-lock", "require-current-pr-head", "ensure-unity-editor"], ["return-unity-license", "classify-unity-cleanup-evidence", "require-confirmed-unity-cleanup"]].map((group) => resolveLockActionPin(group));
const LOCK_ACTION_SHA = LOCK_ACTION_PIN.sha;
const ACQUIRE_ACTION_SHA = LOCK_ACTION_PIN.sha;
const CLEANUP_POLICY_SHA = CLEANUP_POLICY_PIN.sha;
// SYNC: workflow-test-vectors.json UNITY_LOCK_WINDOWS mirrors scripts/validate-unity-pr-policy.py LICENSED_LOCK_WINDOWS.

// prettier-ignore
const CONSOLIDATED_WORKFLOWS = ["actionlint.yml", "csharpier-check.yml", "dotnet-tests.yml", "json-format-check.yml", "lint-doc-links.yml", "markdownlint.yml", "script-tests.yml", "spellcheck.yml", "validate-banner.yml", "validate-docs.yml", "validate-llms-txt.yml", "yaml-format-lint.yml"];

// prettier-ignore
const AGGREGATED_JOBS = ["changes", "actionlint", "markdownlint", "csharpier", "dotnet", "json-format", "line-endings", "spellcheck", "validate-banner", "validate-llms-txt", "yaml-format-lint", "script-tests", "validate-docs", "lint-doc-links"];

// cspell:ignore ACDMRT

const readWorkflow = (file = "ci.yml") => fs.readFileSync(path.join(WORKFLOW_DIR, file), "utf8");

const readWorkflowDocument = (file) => YAML.parseDocument(readWorkflow(file));

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
  for (const line of source.split(/\r?\n/)) {
    const match = initialPattern.exec(line) || (pieces.length && appendPattern.exec(line));
    if (match) {
      pieces.push(match[1]);
    } else if (pieces.length) {
      break;
    }
  }

  assert.ok(pieces.length > 0, `ci.yml must build ${variableName}`);
  return pieces.join("");
}

test("active workflows keep the shared safety contract", () => {
  const workflowFiles = fs
    .readdirSync(WORKFLOW_DIR)
    .filter((file) => /\.ya?ml$/.test(file))
    .sort();

  for (const file of workflowFiles) {
    const document = readWorkflowDocument(file);
    assert.equal(document.errors.length, 0, `${file} must parse as YAML`);

    const keys = document.contents.items.map((item) => String(item.key.value));
    assert.deepEqual(
      keys.slice(0, 5),
      ["name", "on", "concurrency", "permissions", "jobs"],
      `${file} must use the canonical top-level key order`
    );

    const workflow = document.toJS();
    assert.equal(
      typeof workflow.concurrency["cancel-in-progress"],
      "boolean",
      `${file} must choose a concurrency cancellation policy`
    );
    assert.equal(typeof workflow.concurrency.group, "string", `${file} must have a group`);
    assert.notEqual(workflow.concurrency.group.trim(), "", `${file} must have a nonempty group`);
    assert.equal(
      typeof workflow.permissions,
      "object",
      `${file} must declare top-level permissions`
    );

    for (const [jobId, job] of Object.entries(workflow.jobs)) {
      assert.ok(
        Number.isFinite(job["timeout-minutes"]) && job["timeout-minutes"] > 0,
        `${file}:${jobId} must have a positive timeout-minutes`
      );

      for (const step of job.steps || []) {
        if (typeof step.uses === "string" && step.uses.startsWith("actions/checkout@")) {
          assert.equal(
            step.with?.["persist-credentials"],
            false,
            `${file}:${jobId}:${step.name || step.uses} must disable persisted credentials`
          );
        }
      }
    }
  }
});

test("wiki sync deploys every admitted push and fails closed", () => {
  const workflow = readWorkflowDocument("sync-wiki.yml").toJS();
  const filters = workflow.on.push.paths;
  const admitted = (files) =>
    files.some((file) => filters.some((filter) => path.matchesGlob(file, filter)));
  const admittedCases = {
    "docs-only": ["docs/guides/testing.md"],
    "transformer-only": ["scripts/wiki/transform-docs-to-wiki.js"],
    "sidebar-only": ["scripts/wiki/generate-wiki-sidebar.js"],
    "workflow-only": [".github/workflows/sync-wiki.yml"],
    rename: ["docs/old-name.md", "docs/new-name.md"]
  };

  assert.deepEqual(filters, ["docs/**", "scripts/wiki/**", ".github/workflows/sync-wiki.yml"]);
  for (const [name, files] of Object.entries(admittedCases)) {
    assert.equal(admitted(files), true, name);
  }
  assert.equal(admitted(["Runtime/Core/MessageBus.cs"]), false, "unrelated");

  const { validate, sync } = workflow.jobs;
  assert.ok(Object.hasOwn(workflow.on, "workflow_dispatch"), "manual dispatch must remain enabled");
  assert.equal(validate.outputs, undefined, "the divergent deployment detector must stay removed");
  assert.equal(sync.needs, "validate", "validation failures must block deployment");
  assert.equal(sync.if, undefined, "admitted pushes and manual dispatches must always deploy");
  assert.ok(
    validate.steps.every((step) => step.if === undefined),
    "every admitted run must execute validation"
  );
});

test("opt-in formatting never executes a fork head with repository write permissions", () => {
  const source = readWorkflow("format-on-demand.yml");
  const workflow = readWorkflowDocument("format-on-demand.yml").toJS();

  assert.equal(
    workflow.concurrency.group,
    "${{ github.workflow }}-${{ github.event.issue.number || inputs.pr_number }}"
  );
  assert.equal(workflow.concurrency["cancel-in-progress"], false);
  assert.deepEqual(workflow.permissions, {
    contents: "write",
    issues: "write",
    "pull-requests": "read"
  });
  assert.doesNotMatch(source, /repository:\s*\$\{\{\s*steps\.meta\.outputs\.head_repo/);

  for (const jobId of ["by_comment", "by_dispatch"]) {
    const job = workflow.jobs[jobId];
    const refusal = job.steps.find((step) => step.name === "Refuse fork pull requests");
    const checkouts = job.steps.filter(
      (step) => typeof step.uses === "string" && step.uses.startsWith("actions/checkout@")
    );
    assert.equal(checkouts.length, 1, `${jobId} must have one same-repository checkout`);
    const checkout = checkouts[0];
    assert.equal(refusal.if, "${{ steps.meta.outputs.same_repo != 'true' }}", jobId);
    assert.match(refusal.run, /exit 1/);
    assert.notEqual(refusal["continue-on-error"], true);
    assert.equal(checkout.if, "${{ steps.meta.outputs.same_repo == 'true' }}", jobId);
    assert.equal(checkout.with.repository, undefined, jobId);
  }

  const dispatchMeta = workflow.jobs.by_dispatch.steps.find(
    (step) => step.name === "Resolve PR metadata"
  );
  assert.equal(dispatchMeta.env.PR_NUMBER, "${{ inputs.pr_number }}");
  assert.match(dispatchMeta.with.script, /Number\(process\.env\.PR_NUMBER\)/);
});

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
    "scripts/validate-unity-pr-policy.py",
    ".github/analyzers/Roslynator.CSharp.Analyzers.dll",
    ".github/analyzers/LICENSE.txt",
    ".github/comparison-packages.json",
    ".github/actions/example/metadata.yaml",
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
test("dependabot never splits a build-lock bump from the copyable docs examples", () => {
  const updates = YAML.parse(
    fs.readFileSync(path.join(REPO_ROOT, ".github", "dependabot.yml"), "utf8")
  ).updates;
  const ignore = (updates.find((u) => u["package-ecosystem"] === "github-actions") || {}).ignore;
  assert.ok(Array.isArray(ignore), "dependabot.yml github-actions block must carry an ignore list");
  const patterns = ignore.map((entry) => entry["dependency-name"] || "");
  // prettier-ignore
  const actions = ["acquire-build-lock", "check-unity-runner-availability", "release-build-lock", "require-current-pr-head", "return-unity-license", "classify-unity-cleanup-evidence", "require-confirmed-unity-cleanup"];
  for (const name of actions) {
    const dependency = `${LOCK_ACTION_PREFIX}${name}`;
    const matched = patterns.some((pattern) =>
      new RegExp(`^${escapeRegExp(pattern).replaceAll("\\*", ".*")}$`).test(dependency)
    );
    assert.ok(
      matched,
      `dependabot.yml must ignore ${dependency}; its pin is copied into docs/ops and automation cannot edit both files in one commit`
    );
  }
});
// prettier-ignore
test("dependabot selects the uv updater for the uv-generated Python locks", () => { const updates = YAML.parse(fs.readFileSync(path.join(REPO_ROOT, ".github", "dependabot.yml"), "utf8")).updates; const rootUvUpdates = updates.filter((update) => update["package-ecosystem"] === "uv" && update.directory === "/"); const rootPipUpdates = updates.filter((update) => update["package-ecosystem"] === "pip" && update.directory === "/"); assert.equal(rootUvUpdates.length, 1, "dependabot.yml must define one root uv update entry"); assert.equal(rootPipUpdates.length, 0, "uv-generated locks must not use the pip updater, which recompiles with pip-compile"); assert.deepEqual(rootUvUpdates[0].groups?.["python-locks-all"]?.patterns, ["*"]); });
test("every Unity lock window releases with explicit cleanup proof", () => {
  const acquire = `uses: ${LOCK_ACTION_PREFIX}acquire-build-lock@${ACQUIRE_ACTION_SHA}${LOCK_ACTION_PIN.comment}`;
  const returnLicense = `uses: ${LOCK_ACTION_PREFIX}return-unity-license@${CLEANUP_POLICY_SHA}`;
  const classify = `uses: ${LOCK_ACTION_PREFIX}classify-unity-cleanup-evidence@${CLEANUP_POLICY_SHA}`;
  const release = `uses: ${LOCK_ACTION_PREFIX}release-build-lock@${ACQUIRE_ACTION_SHA}${LOCK_ACTION_PIN.comment}`;
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

  // The central editor gate exposes the validated executable through its
  // editor-path output. The invocation invariant: every Unity-consuming step
  // binds that output as UNITY_EDITOR_PATH step env, so licensed work runs the
  // exact editor the gate validated (the former "Bind validated Unity editor"
  // run step became this typed output binding).
  const editorPathBinding = "${{ steps.ensure_unity_editor.outputs.editor-path }}";

  for (const [file, jobId, licensedWorkName, emptyAware] of UNITY_LOCK_WINDOWS) {
    const label = `${file}:${jobId}`;
    const licensedCondition = `${file === "perf-numbers.yml" ? "success\\(\\) && " : ""}${file === "unity-tests.yml" ? "!cancelled\\(\\) && " : ""}${emptyAware ? "steps\\.compute\\.outputs\\.is-empty != 'true' && " : ""}steps\\.acquire_lock\\.outputs\\.acquired == 'true'`;
    const job = getJobBlock(readWorkflow(file), jobId, file);
    const install = getStepBlock(job, "Install artifact tooling dependencies");
    assert.match(
      install,
      /id: install_dependencies\n[\s\S]*shell: pwsh\n[\s\S]*\bnpm ci [^\n]*--ignore-scripts --no-audit --no-fund\n/
    );
    assert.doesNotMatch(install, /continue-on-error:|\n        if:/);
    assert.ok(
      job.indexOf("id: setup_node") < job.indexOf(install) &&
        job.indexOf(install) < job.indexOf(acquire),
      `${label}: install before acquiring a license`
    );
    for (const step of YAML.parse(job)[jobId].steps.filter(
      (step) => step.uses === "./.github/actions/redact-unity-artifacts"
    )) {
      assert.match(
        step.if,
        /steps\.install_dependencies\.outcome == 'success'/,
        `${label}: failed installs cannot authorize redaction or uploads`
      );
    }
    const expectedJobTimeout = file === "unity-tests.yml" ? 1050 : 900;
    // prettier-ignore
    assert.match(job, new RegExp(`\\n    timeout-minutes: ${expectedJobTimeout}\\n`), `${label}: lifecycle budget`);
    if (["perf-numbers.yml", "unity-benchmarks.yml", "unity-tests.yml"].includes(file)) {
      assert.match(job, /\n      fail-fast: false\n      max-parallel: 1\n/, `${label} fairness`);
    }
    for (const action of [acquire, returnLicense, classify, release, gate]) {
      assert.equal(job.split(action).length - 1, 1, `${label}: ${action}`);
    }

    // prettier-ignore
    const lifecycleNames = ["Require manually installed Unity editor", "Validate Unity license secrets", "Acquire organization Unity lock", "Require acquired Unity lock", licensedWorkName, "Return Unity license", "Classify Unity cleanup evidence", "Release organization Unity lock", "Require confirmed Unity cleanup"];
    const positions = lifecycleNames.map((name) => job.indexOf(`      - name: ${name}`));
    const sortedPositions = [...positions].sort((a, b) => a - b);
    assert.ok(
      positions.every((position) => position >= 0),
      `${label} lifecycle steps must all exist`
    );
    assert.deepEqual(positions, sortedPositions, `${label} lifecycle order`);

    // prettier-ignore
    const [validationStep, credentialStep, acquireStep, requireStep, workStep, returnStep, classifyStep, releaseStep, gateStep] = lifecycleNames.map((name) => getStepBlock(job, name));

    // The central immutable gate validates the runner-owned editor before any
    // credential reference or checkout, stays success-dependent and
    // failure-propagating, and refuses provisioning.
    // prettier-ignore
    const contracts = [
      [validationStep, /\n        id: ensure_unity_editor\n/],
      [validationStep, /\n        timeout-minutes: 10\n/],
      [validationStep, new RegExp(`uses: ${escapeRegExp(LOCK_ACTION_PREFIX)}ensure-unity-editor@${LOCK_ACTION_SHA}([\\s\\n]|#)`)],
      [validationStep, /install-root: \$\{\{ runner\.tool_cache \}\}\\u6-v3\n/, `${label}: the gate must pin the trusted editor root`],
      [validationStep, /diagnostics-path: unity-editor-check\.json\n/, `${label}: the gate must write its diagnostics contract path`],
      [validationStep, /ci-managed-only: true\n[\s\S]*require-healthy-existing: true\n/, `${label}: the gate must refuse fallback installs`],
      [validationStep, new RegExp(`provisioning-profile: ${escapeRegExp(UNITY_EDITOR_PROFILES[file])}\\n`), `${label}: the gate must use the reviewed provisioning profile`],
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
      ...(["perf-numbers.yml", "unity-benchmarks.yml", "unity-tests.yml"].includes(file) || jobId === "unity-checks" ? [[workStep, /-UnityInstallRoot \(Join-Path \$env:RUNNER_TOOL_CACHE 'u6-v3'\)/, `${label}: licensed work and central return must use the same trusted editor root`]] : [])
    ];
    for (const [actual, contract, message] of contracts) assert.match(actual, contract, message);

    assert.doesNotMatch(
      validationStep,
      /\n        if:/,
      `${label}: the editor gate must keep GitHub's implicit success chain`
    );
    assert.doesNotMatch(
      validationStep,
      /\n        continue-on-error:/,
      `${label}: the editor gate must propagate failure`
    );
    assert.doesNotMatch(returnStep, /continue-on-error:/);

    // Invocation invariant: the validated editor output reaches every
    // Unity-consuming step as UNITY_EDITOR_PATH, in gate-then-consume order.
    const parsedSteps = YAML.parse(job)[jobId].steps;
    const gateIndex = parsedSteps.findIndex((step) => step.id === "ensure_unity_editor");
    assert.ok(gateIndex >= 0, `${label}: editor gate step must exist`);
    const consumers = UNITY_EDITOR_CONSUMERS.find(
      ([candidateFile, candidateJobId]) => candidateFile === file && candidateJobId === jobId
    );
    assert.ok(consumers, `${label}: the contract must declare its Unity-consuming steps`);
    for (const consumerId of consumers[2]) {
      const consumerIndex = parsedSteps.findIndex((step) => step.id === consumerId);
      assert.ok(consumerIndex > gateIndex, `${label}:${consumerId} must follow the editor gate`);
      assert.equal(
        parsedSteps[consumerIndex].env.UNITY_EDITOR_PATH,
        editorPathBinding,
        `${label}:${consumerId} must run the editor the gate validated`
      );
    }

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

test("licensed PR workflows fail closed and skip only documented non-code paths", () => {
  // Documentation-only pull requests are skipped by the trigger filter itself.
  // The allowlist must stay closed and mechanical.
  const unityDocument = readWorkflowDocument("unity-tests.yml").toJS();
  assert.deepEqual(unityDocument.on.pull_request["paths-ignore"], DOCS_ONLY_PATH_IGNORES);

  // unity-tests.yml is absent for those pull requests, so the companion gate
  // keeps the required "Unity CI Success" context present. Mixed changes use
  // a distinct report name and leave that context to the licensed workflow.
  const gateSource = readWorkflow("unity-docs-gate.yml");
  const gateDocument = readWorkflowDocument("unity-docs-gate.yml").toJS();
  const { classify: classifyJob, report: reportJob } = gateDocument.jobs;
  // prettier-ignore
  assert.deepEqual(
    { paths: gateDocument.on.pull_request.paths, pathsIgnore: gateDocument.on.pull_request["paths-ignore"], classifySteps: classifyJob.steps.length, classifyOutput: classifyJob.outputs.documentation_only, reportName: reportJob.name, reportIf: reportJob.if, reportSteps: reportJob.steps.length, reportNeeds: reportJob.needs },
    { paths: [".github/workflows/**", ...DOCS_ONLY_PATH_IGNORES], pathsIgnore: undefined, classifySteps: 1, classifyOutput: "${{ steps.classify.outputs.documentation_only }}", reportName: "${{ (needs.classify.result != 'success' || needs.classify.outputs.documentation_only != 'false') && 'Unity CI Success' || 'Unity docs gate not applicable' }}", reportIf: "${{ always() }}", reportSteps: 1, reportNeeds: ["classify"] }
  );
  const pattern = /documentation_only_pattern='([^']+)'/.exec(gateSource);
  const allowed = new RegExp(pattern[1]);
  // prettier-ignore
  for (const file of ["docs/index.md", "docs/index.md.meta", ".docs-tests/DocsSnippetCompilationTests.cs", ".docs-tests/global.json", ".llm/context.md", "Samples~/Mini Combat/README.md", ".agents/skills/example/SKILL.md", ".claude/settings.json", "progress/2026-08-01-run.md", "GOAL.md", "llms.txt", "mkdocs.yml", "requirements-docs.in", "requirements-docs.txt", "requirements-brand.in", "requirements-brand.txt"])
    assert.match(file, allowed, `docs-only ${file}`);
  // The exact-name globs do not ignore .meta siblings: a pull request adding
  // one of those touches a path unity-tests.yml still covers.
  // prettier-ignore
  for (const file of [".github/workflows/unity-tests.yml", "Runtime/Core/MessageBus.cs", "Samples~/Mini Combat/Player.cs", "README.md", "AGENTS.md.meta", "requirements-docs.in.meta", "Samples~/Mini Combat/README.md.meta"])
    assert.doesNotMatch(file, allowed, `non-ignored ${file}`);

  // perf-numbers.yml keeps its in-band head freshness and relevance decisions.
  const perfSource = readWorkflow("perf-numbers.yml");
  const head = getJobBlock(perfSource, "head-check", "perf-numbers.yml");
  assert.match(head, /relevant: \$\{\{ steps\.head\.outputs\.relevant \}\}/);
  assert.match(head, /Changed-file lookup failed; running licensed/);
  assert.match(head, /echo "relevant=\$\{relevant\}" >> "\$\{GITHUB_OUTPUT\}"/);
  const perfAllowed = new RegExp(/documentation_only_pattern='([^']+)'/.exec(head)[1]);
  assert.match("docs/index.md", perfAllowed);
  assert.doesNotMatch("Runtime/Core/MessageBus.cs", perfAllowed);

  const unitySource = readWorkflow("unity-tests.yml");
  for (const jobId of ["runner-preflight", "unity-tests"]) {
    const job = getJobBlock(unitySource, jobId, "unity-tests.yml");
    assert.match(job, /github\.event\.pull_request\.user\.login != 'dependabot\[bot\]'/);
    assert.doesNotMatch(job, /github\.actor != 'dependabot\[bot\]'/);
  }
  // prettier-ignore
  assert.doesNotMatch(getJobBlock(unitySource, "unity-ci-success", "unity-tests.yml"), /github\.actor == 'dependabot\[bot\]'/);
  assert.match(
    getStepBlock(
      getJobBlock(unitySource, "unity-tests", "unity-tests.yml"),
      "Upload shipping-fidelity artifacts"
    ),
    /always\(\) &&[\s\S]*!cancelled\(\) &&[\s\S]*steps\.acquire_lock\.outputs\.acquired == 'true'[\s\S]*if-no-files-found: error/
  );
});
// prettier-ignore
test("Unity CI Success aggregates enforce the closed trusted-skip result shape", () => {
  const bash = process.platform === "win32" ? path.join(process.env.ProgramFiles, "Git", "bin", "bash.exe") : "bash";
  for (const [file, aggregateId, preflightJob, licensedJob] of [
    ["unity-tests.yml", "unity-ci-success", "runner-preflight", "unity-tests"],
    ["perf-numbers.yml", "perf-unity-success", "runner-preflight", "perf-benchmarks"]
  ]) {
    const job = readWorkflowDocument(file).toJS().jobs[aggregateId];
    assert.ok(
      String(job.if) === "always()" || String(job.if) === "${{ always() }}",
      `${file}: the aggregate must be exactly always()`
    );
    assert.equal(job.steps.length, 1, `${file}: the trusted-skip shape keeps exactly one gate step`);
    const gate = job.steps[0];
    assert.equal(gate.shell, "bash");
    assert.equal(gate["continue-on-error"], undefined);
    assert.deepEqual(Object.keys(gate.env), ["RUNNER_PREFLIGHT_RESULT", "UNITY_TESTS_RESULT", "FORK_PR", "DEPENDABOT_PR"]);
    assert.equal(gate.env.RUNNER_PREFLIGHT_RESULT, `\${{ needs.${preflightJob}.result }}`);
    assert.equal(gate.env.UNITY_TESTS_RESULT, `\${{ needs.${licensedJob}.result }}`);
    assert.equal(gate.env.FORK_PR, "${{ github.event_name == 'pull_request' && github.event.pull_request.head.repo.full_name != github.repository }}");
    assert.equal(gate.env.DEPENDABOT_PR, "${{ github.event_name == 'pull_request' && github.event.pull_request.user.login == 'dependabot[bot]' }}");
    // Execute the exact committed gate script against its truth table: the
    // two untrusted pull-request shapes skip green, every trusted run needs
    // both jobs green, and any other result shape fails.
    const run = (env) => spawnSync(bash, ["-c", gate.run], { env: { ...process.env, ...env } }).status;
    const truthTable = [
      ["fork pull requests skip green", { FORK_PR: "true", DEPENDABOT_PR: "false", RUNNER_PREFLIGHT_RESULT: "skipped", UNITY_TESTS_RESULT: "skipped" }, 0],
      ["dependabot pull requests skip green", { FORK_PR: "false", DEPENDABOT_PR: "true", RUNNER_PREFLIGHT_RESULT: "skipped", UNITY_TESTS_RESULT: "skipped" }, 0],
      ["trusted runs need both jobs green", { FORK_PR: "false", DEPENDABOT_PR: "false", RUNNER_PREFLIGHT_RESULT: "success", UNITY_TESTS_RESULT: "success" }, 0],
      ["a skipped licensed job on a trusted run is red", { FORK_PR: "false", DEPENDABOT_PR: "false", RUNNER_PREFLIGHT_RESULT: "skipped", UNITY_TESTS_RESULT: "skipped" }, 1],
      ["a partial trusted run is red", { FORK_PR: "false", DEPENDABOT_PR: "false", RUNNER_PREFLIGHT_RESULT: "success", UNITY_TESTS_RESULT: "skipped" }, 1],
      ["a failed licensed job is red", { FORK_PR: "false", DEPENDABOT_PR: "false", RUNNER_PREFLIGHT_RESULT: "success", UNITY_TESTS_RESULT: "failure" }, 1],
      ["a fork run that executed licensed work is red", { FORK_PR: "true", DEPENDABOT_PR: "false", RUNNER_PREFLIGHT_RESULT: "success", UNITY_TESTS_RESULT: "success" }, 1]
    ];
    for (const [name, env, expected] of truthTable) {
      assert.equal(run(env), expected, `${file}: ${name}`);
    }
  }
  const unityJob = readWorkflowDocument("unity-tests.yml").toJS().jobs["unity-ci-success"];
  assert.deepEqual(unityJob.permissions, { actions: "read" });
  assert.equal(readWorkflowDocument("unity-tests.yml").toJS().jobs["unity-tests"].name, "Unity ${{ matrix.unity-version }} all modes");
});
// prettier-ignore
test("active automation rejects direct Unity Helpers dependencies and pins external actions", () => {
  for (const [name, source, forbidden] of [["canonical owner", "uses: Ambiguous-Interactive/unity-helpers/action@ref", true], ["former owner case-insensitively", "uses: WALLSTOP/UNITY-HELPERS/action@ref", true], ["YAML comment", "# uses: Ambiguous-Interactive/unity-helpers/action@ref\nuses: ./local", false], ["different owner", "uses: notambiguous-interactive/unity-helpers/action@ref", false], ["different repository", "uses: wallstop/unity-helpers-fork/action@ref", false]]) assert.equal(FORBIDDEN_UNITY_HELPERS.test(JSON.stringify(YAML.parse(source))), forbidden, name);
  for (const filePath of [WORKFLOW_DIR, path.join(REPO_ROOT, ".github", "actions")].flatMap((root) => walkFiles(root, { match: (file) => /\.ya?ml$/.test(file) }))) {
    const source = fs.readFileSync(filePath, "utf8");
    assert.doesNotMatch(JSON.stringify(YAML.parse(source)), FORBIDDEN_UNITY_HELPERS, `${path.relative(REPO_ROOT, filePath)} must not directly depend on Unity Helpers`);
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
    if (file === "perf-numbers.yml") { assert.match(source, /\n  pull_request:/, file); assert.match(source, /\n  comment-perf-doc:/, file); }
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

for (const [mode, expected] of Object.entries(CORRECTNESS_CATEGORY_FILTERS)) {
  test(`${mode} correctness category filter retains lifecycle coverage and excludes heavy work`, () => {
    const steps = YAML.parse(readWorkflow("unity-tests.yml")).jobs["unity-tests"].steps;
    const step = steps.find((candidate) => candidate.id === `run_${mode}`);
    assert.ok(step, `${mode} correctness step must exist`);
    assert.match(step.run, new RegExp(`-TestMode ${mode}\\b`));
    assert.deepEqual(step.env.DXM_UNITY_TEST_CATEGORY.split(";").sort(), expected.toSorted());
  });
}
