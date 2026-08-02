const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const test = require("node:test");
const YAML = require("yaml");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOW_PATH = path.join(REPO_ROOT, ".github", "workflows", "perf-numbers.yml");
const KEY_COUNTS = [1, 4, 16, 256, 4096];
const OPERATIONS = ["Hit", "Miss", "Churn"];

function renderStepScript() {
  const workflow = YAML.parse(fs.readFileSync(WORKFLOW_PATH, "utf8"));
  const renderStep = workflow.jobs["comment-perf-doc"].steps.find(
    (step) => step.name === "Render current PR performance evidence"
  );
  assert.ok(renderStep?.run, "performance reporting must retain its evidence-rendering step");
  return renderStep.run;
}

function evidenceValidationScript() {
  const run = renderStepScript();
  const start = run.indexOf("if ! grep -q '^| Scenario");
  const end = run.indexOf("\nshort_sha=", start);
  assert.ok(start >= 0 && end > start, "could not isolate the workflow's evidence guards");
  return `set -euo pipefail\n${run.slice(start, end)}`;
}

function baselineClassificationScript() {
  const run = renderStepScript();
  const start = run.indexOf("baseline_rows=0");
  const end = run.indexOf('if [ "${baseline_rows}" -eq 0 ]; then', start);
  assert.ok(start >= 0 && end > start, "could not isolate the workflow's baseline classifier");
  return `set -euo pipefail\n${run.slice(start, end)}\ntest "\${baseline_rows}" -eq "\${EXPECTED_BASELINE_ROWS}"`;
}

function targetRows(metricSuffix = "operationsPerSecond=1000") {
  const rows = [];
  for (const keyCount of KEY_COUNTS) {
    for (const operation of OPERATIONS) {
      rows.push(
        `DXM_TARGET_MAP_BENCHMARK scenario=TargetMap_${keyCount}_${operation} keyCount=${keyCount} operation=${operation} ${metricSuffix}`
      );
    }
    rows.push(`DXM_TARGET_MAP_CONSTRUCTION keyCount=${keyCount} wallClockMs=1 ${metricSuffix}`);
  }
  return rows;
}

function createFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-perf-pr-evidence-"));
  const artifacts = path.join(root, ".artifacts");
  const playMode = path.join(artifacts, "perf-download", "perf-6000.3.16f1-playmode");
  const standalone = path.join(artifacts, "perf-download", "perf-6000.3.16f1-standalone");
  fs.mkdirSync(playMode, { recursive: true });
  fs.mkdirSync(standalone, { recursive: true });
  fs.writeFileSync(
    path.join(artifacts, "perf-playmode-current.md"),
    [
      "### Dispatch throughput - PlayMode (Mono)",
      "",
      "| Scenario | Throughput / Wall clock | GC allocs | GC bytes |",
      "| --- | --- | --- | --- |",
      "| Empty Bus Dispatch | 20 M emits/sec | 0 | 0 |",
      ""
    ].join("\n")
  );
  fs.writeFileSync(path.join(playMode, "unity.log"), `${targetRows().join("\n")}\n`);
  fs.writeFileSync(path.join(standalone, "unity.log"), "standalone editor noise\n");
  fs.writeFileSync(path.join(standalone, "player.log"), `${targetRows().join("\n")}\n`);
  return { root, artifacts, playMode, standalone };
}

function runFixture(fixture) {
  return spawnSync("bash", ["-c", evidenceValidationScript()], {
    cwd: fixture.root,
    encoding: "utf8"
  });
}

test("PR evidence guards accept complete measured PlayMode and symmetric TargetMap rows", () => {
  const fixture = createFixture();
  try {
    const result = runFixture(fixture);
    assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
    const rows = fs
      .readFileSync(path.join(fixture.artifacts, "perf-target-map-current.txt"), "utf8")
      .trim()
      .split("\n");
    assert.equal(rows.length, 40, "both scopes should contribute all 20 trusted identities");
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("PR evidence guards reject a missing Standalone TargetMap identity", () => {
  const fixture = createFixture();
  try {
    fs.writeFileSync(
      path.join(fixture.standalone, "player.log"),
      `${targetRows().slice(1).join("\n")}\n`
    );
    const result = runFixture(fixture);
    assert.notEqual(result.status, 0, "a missing Standalone identity must fail reporting");
    assert.match(
      result.stdout + result.stderr,
      /identities are missing or differ|does not exactly match/
    );
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("PR evidence guards reject duplicate identities with conflicting measurements", () => {
  const fixture = createFixture();
  try {
    fs.appendFileSync(
      path.join(fixture.playMode, "unity.log"),
      `${targetRows("operationsPerSecond=2000")[0]}\n`
    );
    const result = runFixture(fixture);
    assert.notEqual(result.status, 0, "conflicting measurements for one identity must fail");
    assert.match(result.stdout + result.stderr, /repeated scenario identities/);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("PR evidence guards reject throughput-only PlayMode output", () => {
  const fixture = createFixture();
  try {
    fs.writeFileSync(
      path.join(fixture.artifacts, "perf-playmode-current.md"),
      "| Scenario | Throughput / Wall clock |\n| --- | --- |\n| Empty Bus Dispatch | 20 M emits/sec |\n"
    );
    const result = runFixture(fixture);
    assert.notEqual(result.status, 0, "missing allocation count and byte cells must fail");
    assert.match(result.stdout + result.stderr, /allocation table was not rendered/);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("PR evidence guards reject unmeasured PlayMode allocation cells", () => {
  const fixture = createFixture();
  try {
    fs.writeFileSync(
      path.join(fixture.artifacts, "perf-playmode-current.md"),
      [
        "| Scenario | Throughput / Wall clock | GC allocs | GC bytes |",
        "| --- | --- | --- | --- |",
        "| Empty Bus Dispatch | 20 M emits/sec | n/a | n/a |",
        ""
      ].join("\n")
    );
    const result = runFixture(fixture);
    assert.notEqual(result.status, 0, "unmeasured allocation cells must fail");
    assert.match(result.stdout + result.stderr, /allocation table was not rendered/);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("PR evidence guards reject an unexpected identity present in both scopes", () => {
  const fixture = createFixture();
  const extra =
    "DXM_TARGET_MAP_BENCHMARK scenario=TargetMap_32_Hit keyCount=32 operation=Hit operationsPerSecond=1000\n";
  try {
    fs.appendFileSync(path.join(fixture.playMode, "unity.log"), extra);
    fs.appendFileSync(path.join(fixture.standalone, "player.log"), extra);
    const result = runFixture(fixture);
    assert.notEqual(result.status, 0, "a symmetric but untrusted identity must fail");
    assert.match(result.stdout + result.stderr, /does not exactly match/);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("PR evidence baseline classifier handles absent, seed, and populated baselines", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-perf-pr-baseline-"));
  const baseline = path.join(root, "perf-baseline.csv");
  const header =
    "scenario,platform,commit,runIndex,emitsPerSecond,gcAllocations,wallClockMs,gcAllocatedBytes\n";
  try {
    for (const [contents, expectedRows] of [
      [null, "0"],
      [header, "0"],
      [`${header}EmptyBus_Dispatch,Standalone IL2CPP x64 Release,abc123,0,1000,0,1.0,-1\n`, "1"]
    ]) {
      if (contents === null) {
        fs.rmSync(baseline, { force: true });
      } else {
        fs.writeFileSync(baseline, contents);
      }
      const result = spawnSync("bash", ["-c", baselineClassificationScript()], {
        cwd: root,
        encoding: "utf8",
        env: {
          ...process.env,
          EXPECTED_BASELINE_ROWS: expectedRows,
          PERF_BASELINE: baseline
        }
      });
      assert.equal(result.status, 0, `${result.stdout}\n${result.stderr}`);
    }
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
