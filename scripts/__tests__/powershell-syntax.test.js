/**
 * @fileoverview PowerShell parse-time syntax guard for every pwsh surface in
 * the repo: standalone *.ps1 scripts AND inline `shell: pwsh` `run:` blocks in
 * GitHub Actions workflows and composite actions.
 *
 * WHY THIS EXISTS: a `$label:` token (parsed as a scope-qualified variable like
 * `$env:`) inside a composite action's `Write-Host` slipped past three review
 * rounds, `npm run test:scripts`, `validate:workflows`, AND `actionlint` --
 * because every one of those checks is string/grep/schema based and NONE of
 * them actually parse PowerShell. A pwsh parse error fails the step at load
 * time (before any statement runs), so a typo turns an entire self-hosted
 * Windows job red on every run. This test closes that class by running the
 * PowerShell language parser over every pwsh snippet we ship.
 *
 * GitHub-Actions `${{ ... }}` expressions are templated out BEFORE PowerShell
 * sees them, so we substitute them with a placeholder bareword before parsing
 * (otherwise `${{` is itself a pwsh parse error and every block would "fail").
 *
 * pwsh is preinstalled on GitHub's ubuntu-latest and windows-latest runners
 * (the script-tests.yml matrix), so this runs in CI. When pwsh is absent
 * locally the parse assertions are skipped, but the collection-sanity test
 * still runs so a zero-snippet regression (the test silently validating
 * nothing) cannot hide.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");
const yaml = require("yaml");
const { assertSpawnStatus } = require("../lib/pwsh-output");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

function pwshAvailable() {
  const probe = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], {
    encoding: "utf8"
  });
  return probe.status === 0;
}

const PWSH_PRESENT = pwshAvailable();

// GitHub Actions templating is resolved before PowerShell runs; replace each
// ${{ ... }} with a bareword so the residual is parseable PowerShell.
function stripGithubExpressions(text) {
  return text.replace(/\$\{\{[\s\S]*?\}\}/g, "GHAEXPR");
}

function listTrackedFiles(globArg) {
  const result = spawnSync("git", ["ls-files", globArg], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  if (result.status !== 0) {
    return [];
  }
  return result.stdout
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
}

// Collect a pwsh `run:` block from a step if it declares `shell: pwsh`.
function collectPwshSteps(steps, sourceLabel, sink) {
  if (!Array.isArray(steps)) {
    return;
  }
  steps.forEach((step, index) => {
    if (
      step &&
      typeof step.run === "string" &&
      typeof step.shell === "string" &&
      step.shell.toLowerCase() === "pwsh"
    ) {
      const name = step.name
        ? `${sourceLabel} :: ${step.name}`
        : `${sourceLabel} :: step[${index}]`;
      sink.push({ name, src: stripGithubExpressions(step.run) });
    }
  });
}

function buildSnippets() {
  const snippets = [];

  // 1) Standalone *.ps1 scripts (no GitHub templating).
  for (const rel of listTrackedFiles("*.ps1")) {
    const abs = path.join(REPO_ROOT, rel);
    snippets.push({ name: rel, src: fs.readFileSync(abs, "utf8") });
  }

  // 2) Composite-action pwsh steps.
  for (const rel of listTrackedFiles(".github/actions/**/action.yml")) {
    const abs = path.join(REPO_ROOT, rel);
    const doc = yaml.parse(fs.readFileSync(abs, "utf8"));
    if (doc && doc.runs && Array.isArray(doc.runs.steps)) {
      collectPwshSteps(doc.runs.steps, rel, snippets);
    }
  }

  // 3) Workflow pwsh steps across every job.
  for (const rel of listTrackedFiles(".github/workflows/*.yml")) {
    const abs = path.join(REPO_ROOT, rel);
    const doc = yaml.parse(fs.readFileSync(abs, "utf8"));
    const jobs = (doc && doc.jobs) || {};
    for (const [jobId, job] of Object.entries(jobs)) {
      if (job && Array.isArray(job.steps)) {
        collectPwshSteps(job.steps, `${rel} :: ${jobId}`, snippets);
      }
    }
  }

  return snippets;
}

// Parse every snippet in ONE pwsh invocation (snippets in, JSON errors out).
function parseAll(snippets) {
  const probe = [
    "$ErrorActionPreference = 'Stop'",
    "$items = [Console]::In.ReadToEnd() | ConvertFrom-Json",
    "$results = foreach ($it in $items) {",
    "  $errs = $null",
    "  [System.Management.Automation.Language.Parser]::ParseInput($it.src, [ref]$null, [ref]$errs) | Out-Null",
    "  $messages = @()",
    "  if ($errs) { $messages = @($errs | ForEach-Object { $_.Message }) }",
    "  [pscustomobject]@{ name = $it.name; errors = $messages }",
    "}",
    "@($results) | ConvertTo-Json -Depth 6 -AsArray"
  ].join("\n");

  const run = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", probe], {
    input: JSON.stringify(snippets),
    encoding: "utf8",
    maxBuffer: 32 * 1024 * 1024
  });

  assertSpawnStatus(run, 0, expect.getState().currentTestName || "pwsh harness");

  const parsed = JSON.parse(run.stdout);
  const byName = new Map();
  for (const entry of parsed) {
    byName.set(entry.name, Array.isArray(entry.errors) ? entry.errors : []);
  }
  return byName;
}

const snippets = buildSnippets();

describe("PowerShell parse-time syntax guard", () => {
  // Always runs (even without pwsh): a zero-snippet result would mean the
  // discovery silently validates nothing -- the exact false-green class this
  // whole change set exists to prevent.
  test("discovers the pwsh surface (scripts + action/workflow blocks)", () => {
    const ps1Count = snippets.filter((s) => s.name.endsWith(".ps1")).length;
    const blockCount = snippets.filter((s) => s.name.includes(" :: ")).length;
    expect(ps1Count).toBeGreaterThanOrEqual(5);
    // The two Unity composite actions each contribute at least one pwsh block.
    expect(blockCount).toBeGreaterThanOrEqual(2);
  });

  if (!PWSH_PRESENT) {
    // eslint-disable-next-line no-console
    console.warn(
      "[powershell-syntax] pwsh not found on PATH; skipping parse assertions (CI runners have pwsh)."
    );
    test.skip.each(snippets.map((s) => s.name))("%s parses without PowerShell errors", () => {});
    return;
  }

  let errorsByName;
  beforeAll(() => {
    errorsByName = parseAll(snippets);
  });

  test.each(snippets.map((s) => s.name))("%s parses without PowerShell errors", (name) => {
    const errors = errorsByName.get(name) || [];
    expect(errors).toEqual([]);
  });
});
