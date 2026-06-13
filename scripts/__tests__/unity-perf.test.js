"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");

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
  render,
  readBaselineRows,
  scenarioLabel
} = require("../unity/render-perf-deltas.js");
const {
  buildComparisonSections,
  buildDispatchSections,
  buildBlock,
  blocksEquivalent,
  deriveScope,
  selectRowsForVersion
} = require("../unity/render-perf-doc.js");
const { buildComparisonScenarioId } = require("../unity/perf-scenarios.js");

const PLATFORM = "Unity 6000.3.16f1 Linux PlayMode Mono";
const STANDALONE_PLATFORM = "Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.3.16f1)";

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

function comparisonRow(emitsPerSecond, allocatedBytesDelta = "0") {
  return {
    platform: STANDALONE_PLATFORM,
    commit: "abc1234",
    runIndex: "-1",
    emitsPerSecond,
    allocatedBytesDelta,
    wallClockMs: "5000.000"
  };
}

function dispatchRow(scenario, emitsPerSecond) {
  return {
    scenario,
    platform: STANDALONE_PLATFORM,
    commit: "abc1234",
    runIndex: "-1",
    emitsPerSecond,
    allocatedBytesDelta: "0",
    wallClockMs: "5000.000"
  };
}

test("scenario filters keep only dispatch and DxMessaging comparison rows", () => {
  const cases = [
    [isKeptScenario, "UntargetedFlood_OneHandler", true],
    [isKeptScenario, "Comparison_DxMessaging_GlobalToOne", true],
    [isKeptScenario, "Comparison_MessagePipe_KeyedToOne", true],
    [isKeptScenario, "SomethingElse", false],
    [isKeptScenario, undefined, false],
    [isDxMessagingRow, "UntargetedFlood_OneHandler", true],
    [isDxMessagingRow, "Comparison_DxMessaging_GlobalToOne", true],
    [isDxMessagingRow, "Comparison_MessagePipe_GlobalToOne", false],
    [isDxMessagingRow, "Unknown", false]
  ];
  for (const [predicate, scenario, expected] of cases) {
    assert.equal(predicate(scenario), expected, `${scenario}`);
  }
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
  assert.deepEqual(
    rows.map(({ scenario, emitsPerSecond, allocatedBytesDelta }) => [
      scenario,
      emitsPerSecond,
      allocatedBytesDelta
    ]),
    [
      ["UntargetedFlood_OneHandler", "1000000.000", "0"],
      ["TargetedFlood_OneListener", "2500000.500", "64"]
    ]
  );
});

test("extract-perf-baseline CSV and args helpers cover round-trip and errors", () => {
  const rows = extractRows(`UntargetedFlood_OneHandler,${PLATFORM},abc1234,0,1000000,0,12.5`);
  assert.deepEqual(extractRows(buildCsv(rows)), rows);

  const options = parseArgs(["node", "x", "--input", "a.log", "--input", "b.log", "--append"]);
  assert.deepEqual(options.inputs, ["a.log", "b.log"]);
  assert.equal(options.append, true);
  assert.throws(() => parseArgs(["node", "x", "--append", "--replace"]), /cannot be used/);
  assert.throws(() => parseArgs(["node", "x", "--bogus"]), /Unknown argument/);
});

test("indexDxMessagingRows filters by scope, platform substring, and technology", () => {
  const rows = [
    row("UntargetedFlood_OneHandler", "1000000.000"),
    { ...row("TargetedFlood_OneListener", "2000000.000"), platform: "Unity 2021.3.45f1 EditMode" },
    row("Comparison_MessagePipe_GlobalToOne", "1.000")
  ];
  assert.deepEqual(
    [...indexDxMessagingRows(rows, "PlayMode", "Unity 6000.3.16f1").keys()],
    ["UntargetedFlood_OneHandler"]
  );
});

test("compareRow reports throughput deltas beyond tolerance", () => {
  const scenario = "UntargetedFlood_OneHandler";
  const baseline = row(scenario, "1000000.000");
  const improved = compareRow(scenario, row(scenario, "1100000.000"), baseline, 0.02);
  const steady = compareRow(scenario, row(scenario, "1010000.000"), baseline, 0.02);

  assert.equal(improved.moved, true);
  assert.equal(improved.cells[0], scenarioLabel(scenario));
  assert.ok(improved.cells[3].includes("+10.00%"));
  assert.equal(steady.moved, false);
});

test("isRegression trips on large throughput drops or allocation growth", () => {
  const baseline = row("x", "1000000.000");
  const cases = [
    [row("x", "500000.000"), baseline, true],
    [row("x", "900000.000"), baseline, false],
    [row("x", "1000000.000", "128"), baseline, true],
    [row("x", "0.000"), row("x", "0.000"), false]
  ];
  for (const [current, base, expected] of cases) {
    assert.equal(isRegression(current, base, 0.33), expected);
  }
});

test("computeRegressed and buildDeltaTable only use overlapping scenarios", () => {
  const scenario = "UntargetedFlood_OneHandler";
  const current = new Map([
    [scenario, row(scenario, "400000.000")],
    ["TargetedFlood_OneListener", row("TargetedFlood_OneListener", "1.000")]
  ]);
  const baseline = new Map([[scenario, row(scenario, "1000000.000")]]);
  const table = buildDeltaTable(current, baseline, 0.02, "PlayMode");
  const empty = buildDeltaTable(new Map(), baseline, 0.02, "PlayMode");

  assert.equal(computeRegressed(current, baseline, 0.33), true);
  assert.equal(computeRegressed(new Map(), baseline, 0.33), false);
  assert.equal(table.changed, true);
  assert.ok(table.markdown.includes("| Scenario"));
  assert.ok(table.markdown.includes(scenarioLabel(scenario)));
  assert.equal(empty.changed, false);
  assert.ok(empty.markdown.includes("No overlapping DxMessaging scenarios"));
});

test("comparison rows stay diagnostic and do not trip the hard gate", () => {
  const dispatchScenario = "UntargetedFlood_OneHandler";
  const scenario = buildComparisonScenarioId("DxMessaging", "GlobalToMany");
  const dir = fs.mkdtempSync("dxm-perf-");
  try {
    const currentPath = `${dir}/current.log`;
    const baselinePath = `${dir}/baseline.csv`;
    fs.writeFileSync(
      currentPath,
      buildCsv([row(dispatchScenario, "1000000.000"), row(scenario, "1000000.000")])
    );
    fs.writeFileSync(
      baselinePath,
      buildCsv([row(dispatchScenario, "1000000.000"), row(scenario, "2000000.000")])
    );

    const result = render({
      inputs: [currentPath],
      baselineCsv: baselinePath,
      unityVersion: "6000.3.16f1",
      tolerance: 0.02,
      regressionThreshold: 0.33,
      scope: "PlayMode"
    });
    assert.equal(result.changed, true);
    assert.equal(result.regressed, false);
    assert.ok(result.markdown.includes("Global -> 16 subscribers"));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("readBaselineRows degrades gracefully when the baseline is absent", () => {
  assert.equal(readBaselineRows(""), null);
  assert.equal(readBaselineRows("/nonexistent/baseline.csv"), null);
});

test("buildComparisonSections bolds per-scenario throughput winners only", () => {
  const sections = buildComparisonSections(
    new Map([
      [
        "Standalone",
        new Map([
          ["DxMessaging|GlobalToOne", comparisonRow("30000000.000")],
          ["MessagePipe|GlobalToOne", comparisonRow("90000000.000")],
          ["CsEvent|GlobalToOne", comparisonRow("90000000.000")],
          ["DxMessaging|KeyedToOne", comparisonRow("12000000.000", "64")],
          ["MessagePipe|KeyedToOne", comparisonRow("9000000.000")]
        ])
      ]
    ])
  );
  const throughput = sections[0].join("\n");
  const allocations = sections[1].join("\n");

  for (const expected of ["**90.00 M emits/sec**", "**12.00 M emits/sec**", "30.00 M emits/sec"]) {
    assert.ok(throughput.includes(expected));
  }
  for (const forbidden of ["**30.00 M emits/sec**", "**9.00 M emits/sec**", "**N/A**"]) {
    assert.ok(!throughput.includes(forbidden));
  }
  assert.ok(!allocations.includes("**"));
});

test("buildComparisonSections bolds a sole present tech and display-precision ties", () => {
  const sole = buildComparisonSections(
    new Map([["Standalone", new Map([["UnityAtoms|Filtered", comparisonRow("5000000.000")]])]])
  )[0].join("\n");
  assert.ok(sole.includes("**5.00 M emits/sec**"));

  const tied = buildComparisonSections(
    new Map([
      [
        "Standalone",
        new Map([
          ["DxMessaging|GlobalToOne", comparisonRow("5001000.000")],
          ["MessagePipe|GlobalToOne", comparisonRow("5004000.000")]
        ])
      ]
    ])
  )[0].join("\n");
  assert.equal((tied.match(/\*\*5\.00 M emits\/sec\*\*/g) || []).length, 2);
});

test("render-perf-doc deriveScope reads scope tokens from platform strings", () => {
  assert.equal(deriveScope(STANDALONE_PLATFORM), "Standalone");
  assert.equal(
    deriveScope("Editor PlayMode Mono x64 Release (WindowsEditor; Unity 6000.3.16f1)"),
    "PlayMode"
  );
});

test("a winner flip within tolerance does not defeat table idempotence", () => {
  const existing = [
    "| Technology  | Global broadcast      |",
    "| ----------- | --------------------- |",
    "| DxMessaging | **30.10 M emits/sec** |",
    "| MessagePipe | 30.00 M emits/sec     |"
  ].join("\n");
  const candidate = [
    "| Technology  | Global broadcast      |",
    "| ----------- | --------------------- |",
    "| DxMessaging | 30.05 M emits/sec     |",
    "| MessagePipe | **30.20 M emits/sec** |"
  ].join("\n");

  assert.equal(blocksEquivalent(existing, candidate, 0.02), true);
  assert.equal(
    blocksEquivalent(
      existing,
      candidate.replace("**30.20 M emits/sec**", "**45.00 M emits/sec**"),
      0.02
    ),
    false
  );
});

test("rendered perf-doc region uses MD001-safe h3 headings", () => {
  const dispatchByScope = new Map([
    [
      "Standalone",
      new Map([
        ["UntargetedFlood_OneHandler", dispatchRow("UntargetedFlood_OneHandler", "37690000.000")]
      ])
    ]
  ]);
  const heading = buildDispatchSections(dispatchByScope, ["Standalone"])[0][0];
  assert.ok(heading.startsWith("### "), `expected h3, got: ${heading}`);
  assert.ok(!heading.startsWith("#### "), "dispatch heading must not be h4");

  const rows = [
    dispatchRow("UntargetedFlood_OneHandler", "37690000.000"),
    dispatchRow(buildComparisonScenarioId("DxMessaging", "GlobalToOne"), "28710000.000"),
    dispatchRow(buildComparisonScenarioId("MessagePipe", "GlobalToOne"), "69680000.000")
  ];
  const block = buildBlock(selectRowsForVersion(rows, "Unity 6000.3.16f1"), "6000.3.16f1");
  assert.ok(block.includes("### Dispatch throughput - "));
  assert.ok(block.includes("### Library comparison - throughput "));
  assert.ok(!block.includes("#### "));
});
