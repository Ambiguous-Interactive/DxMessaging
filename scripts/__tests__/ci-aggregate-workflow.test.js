"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOW_DIR = path.join(REPO_ROOT, ".github", "workflows");

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

function getJobBlock(source, jobId) {
  const header = new RegExp(`^  ${escapeRegExp(jobId)}:\n`, "m");
  const match = header.exec(source);
  assert.ok(match, `ci.yml must define a ${jobId} job`);

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
  // Pin the fail-closed aggregator action to a versioned tag, not @main/@master.
  assert.match(ciSuccess, /uses: re-actors\/alls-green@[\w./-]*v\d/);
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

test("release workflows pin App write scopes and denied-push diagnostics", () => {
  const prepare = fs.readFileSync(path.join(WORKFLOW_DIR, "release-prepare.yml"), "utf8");
  const tag = fs.readFileSync(path.join(WORKFLOW_DIR, "release-tag.yml"), "utf8");
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
