"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOW_DIR = path.join(REPO_ROOT, ".github", "workflows");
const CI_WORKFLOW = path.join(WORKFLOW_DIR, "ci.yml");

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
  return fs.readFileSync(CI_WORKFLOW, "utf8");
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
  assert.match(ciSuccess, /uses: re-actors\/alls-green@release\/v1/);
  assert.match(ciSuccess, /allowed-skips: ""/);
  assert.match(ciSuccess, /allowed-failures: ""/);

  for (const job of AGGREGATED_JOBS) {
    assert.match(ciSuccess, new RegExp(`\\n      - ${job}\\n`), `CI Success must need ${job}`);
  }
});

test("change detector considers current and previous paths", () => {
  const source = readCiWorkflow();
  assert.match(source, /--jq '\.\[\] \| \.filename, \(\.previous_filename \/\/ empty\)'/);
  assert.match(source, /git diff --name-status --find-renames --diff-filter=ACDMRT/);
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

  assert.match(markerScan, /git ls-files -z/);
  assert.match(markerScan, /'Runtime\/\*\*'/);
  assert.match(markerScan, /'Editor\/\*\*'/);
  assert.match(markerScan, /'SourceGenerators\/\*\*'/);
  assert.match(markerScan, /'Tests\/\*\*'/);
  assert.doesNotMatch(markerScan, /--no-ignore/);
  assert.doesNotMatch(markerScan, /\.github/);
  assert.doesNotMatch(markerScan, /\.llm/);
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
