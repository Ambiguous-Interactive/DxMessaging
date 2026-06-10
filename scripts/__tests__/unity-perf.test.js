"use strict";

// Smoke tests for the exported pure logic of scripts/unity/extract-perf-baseline.js
// and scripts/unity/render-perf-deltas.js. Paths that need real Unity logs/NUnit
// artifacts (readInputs, main, the CLI) are intentionally not covered.

const { test } = require("node:test");
const assert = require("node:assert/strict");

const {
  CSV_HEADER,
  isKeptScenario,
  extractRows,
  buildCsv,
  parseArgs
} = require("../unity/extract-perf-baseline.js");
const {
  isDxMessagingRow,
  indexDxMessagingRows,
  compareRow,
  isRegression,
  computeRegressed,
  buildDeltaTable,
  readBaselineRows,
  scenarioLabel
} = require("../unity/render-perf-deltas.js");

const PLATFORM = "Unity 6000.3.16f1 Linux PlayMode Mono";

function row(scenario, emitsPerSecond, allocatedBytesDelta = "0", wallClockMs = "10.000") {
  return {
    scenario,
    platform: PLATFORM,
    commit: "abc1234",
    runIndex: "0",
    emitsPerSecond,
    allocatedBytesDelta,
    wallClockMs
  };
}

test("isKeptScenario accepts dispatch and comparison scenarios only", () => {
  assert.equal(isKeptScenario("UntargetedFlood_OneHandler"), true);
  assert.equal(isKeptScenario("Comparison_DxMessaging_GlobalToOne"), true);
  assert.equal(isKeptScenario("Comparison_MessagePipe_KeyedToOne"), true);
  assert.equal(isKeptScenario("SomethingElse"), false);
  assert.equal(isKeptScenario(undefined), false);
});

test("extractRows parses CSV and structured log lines, dedupes, skips noise", () => {
  const content = [
    "random unity log line",
    CSV_HEADER,
    `2026-01-01T00:00:00 UntargetedFlood_OneHandler,${PLATFORM},abc1234,0,1000000,0,12.5`,
    `UntargetedFlood_OneHandler,${PLATFORM},abc1234,0,1000000,0,12.5`,
    `{scenario:"TargetedFlood_OneListener",platform:"${PLATFORM}",commit:"abc1234",runIndex:1,emitsPerSec:2500000.5,allocatedBytesDelta:64,wallClockMs:8.25}`,
    `UnknownScenario,${PLATFORM},abc1234,0,1,0,1`
  ].join("\n");

  const rows = extractRows(content);
  assert.equal(rows.length, 2);
  assert.equal(rows[0].scenario, "UntargetedFlood_OneHandler");
  assert.equal(rows[0].emitsPerSecond, "1000000.000");
  assert.equal(rows[1].scenario, "TargetedFlood_OneListener");
  assert.equal(rows[1].emitsPerSecond, "2500000.500");
  assert.equal(rows[1].allocatedBytesDelta, "64");
});

test("buildCsv round-trips through extractRows", () => {
  const rows = extractRows(`UntargetedFlood_OneHandler,${PLATFORM},abc1234,0,1000000,0,12.5`);
  const csv = buildCsv(rows);
  assert.ok(csv.startsWith(`${CSV_HEADER}\n`));
  assert.deepEqual(extractRows(csv), rows);
});

test("extract-perf-baseline parseArgs handles flags and rejects conflicts", () => {
  const options = parseArgs(["node", "x", "--input", "a.log", "--input", "b.log", "--append"]);
  assert.deepEqual(options.inputs, ["a.log", "b.log"]);
  assert.equal(options.append, true);
  assert.throws(() => parseArgs(["node", "x", "--append", "--replace"]), /cannot be used/);
  assert.throws(() => parseArgs(["node", "x", "--bogus"]), /Unknown argument/);
});

test("isDxMessagingRow keeps dispatch and DxMessaging comparison rows only", () => {
  assert.equal(isDxMessagingRow("UntargetedFlood_OneHandler"), true);
  assert.equal(isDxMessagingRow("Comparison_DxMessaging_GlobalToOne"), true);
  assert.equal(isDxMessagingRow("Comparison_MessagePipe_GlobalToOne"), false);
  assert.equal(isDxMessagingRow("Unknown"), false);
});

test("indexDxMessagingRows filters by scope and platform substring", () => {
  const rows = [
    row("UntargetedFlood_OneHandler", "1000000.000"),
    { ...row("TargetedFlood_OneListener", "2000000.000"), platform: "Unity 2021.3.45f1 EditMode" },
    row("Comparison_MessagePipe_GlobalToOne", "1.000")
  ];
  const indexed = indexDxMessagingRows(rows, "PlayMode", "Unity 6000.3.16f1");
  assert.deepEqual([...indexed.keys()], ["UntargetedFlood_OneHandler"]);
});

test("compareRow reports throughput deltas beyond tolerance", () => {
  const baseline = row("UntargetedFlood_OneHandler", "1000000.000");
  const improved = compareRow(
    "UntargetedFlood_OneHandler",
    row("UntargetedFlood_OneHandler", "1100000.000"),
    baseline,
    0.02
  );
  assert.equal(improved.moved, true);
  assert.equal(improved.cells[0], scenarioLabel("UntargetedFlood_OneHandler"));
  assert.ok(improved.cells[3].includes("+10.00%"));

  const steady = compareRow(
    "UntargetedFlood_OneHandler",
    row("UntargetedFlood_OneHandler", "1010000.000"),
    baseline,
    0.02
  );
  assert.equal(steady.moved, false);
});

test("isRegression trips on large throughput drops or allocation growth", () => {
  const baseline = row("UntargetedFlood_OneHandler", "1000000.000");
  assert.equal(isRegression(row("x", "500000.000"), baseline, 0.33), true);
  assert.equal(isRegression(row("x", "900000.000"), baseline, 0.33), false);
  assert.equal(isRegression(row("x", "1000000.000", "128"), baseline, 0.33), true);
  // Zero-throughput baselines (wall-clock scenarios) never gate.
  assert.equal(isRegression(row("x", "0.000"), row("x", "0.000"), 0.33), false);
});

test("computeRegressed and buildDeltaTable only use overlapping scenarios", () => {
  const scenario = "UntargetedFlood_OneHandler";
  const current = new Map([
    [scenario, row(scenario, "400000.000")],
    ["TargetedFlood_OneListener", row("TargetedFlood_OneListener", "1.000")]
  ]);
  const baseline = new Map([[scenario, row(scenario, "1000000.000")]]);

  assert.equal(computeRegressed(current, baseline, 0.33), true);
  assert.equal(computeRegressed(new Map(), baseline, 0.33), false);

  const table = buildDeltaTable(current, baseline, 0.02, "PlayMode");
  assert.equal(table.changed, true);
  assert.ok(table.markdown.includes("| Scenario"));
  assert.ok(table.markdown.includes(scenarioLabel(scenario)));

  const empty = buildDeltaTable(new Map(), baseline, 0.02, "PlayMode");
  assert.equal(empty.changed, false);
  assert.ok(empty.markdown.includes("No overlapping DxMessaging scenarios"));
});

test("readBaselineRows degrades gracefully when the baseline is absent", () => {
  assert.equal(readBaselineRows(""), null);
  assert.equal(readBaselineRows("/nonexistent/baseline.csv"), null);
});
