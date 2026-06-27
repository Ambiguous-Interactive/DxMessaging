"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");
const { spawnSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  CSV_HEADER,
  isKeptScenario,
  extractRows,
  buildCsv,
  parseArgs,
  deriveScope: extractorDeriveScope
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
  scenarioLabel,
  goodnessRelativeChange,
  formatAllocDelta,
  formatBytesDelta
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
const EDITOR_PLAYMODE_PLATFORM =
  "Editor PlayMode Mono x64 Release (WindowsEditor; Unity 6000.3.16f1)";
const REPO_ROOT = path.resolve(__dirname, "..", "..");

function row(
  scenario,
  emitsPerSecond,
  gcAllocations = "0",
  wallClockMs = "10.000",
  gcAllocatedBytes = "0"
) {
  return {
    scenario,
    platform: PLATFORM,
    commit: "abc1234",
    runIndex: "0",
    emitsPerSecond,
    gcAllocations,
    wallClockMs,
    gcAllocatedBytes
  };
}

function comparisonRow(emitsPerSecond, gcAllocations = "0", gcAllocatedBytes = "0") {
  return {
    platform: STANDALONE_PLATFORM,
    commit: "abc1234",
    runIndex: "-1",
    emitsPerSecond,
    gcAllocations,
    wallClockMs: "5000.000",
    gcAllocatedBytes
  };
}

function dispatchRow(scenario, emitsPerSecond, gcAllocations = "0", gcAllocatedBytes = "0") {
  return {
    scenario,
    platform: STANDALONE_PLATFORM,
    commit: "abc1234",
    runIndex: "-1",
    emitsPerSecond,
    gcAllocations,
    wallClockMs: "5000.000",
    gcAllocatedBytes
  };
}

function playModeRow(
  scenario,
  emitsPerSecond,
  gcAllocations,
  wallClockMs = "5000.000",
  gcAllocatedBytes = "0"
) {
  return {
    scenario,
    platform: EDITOR_PLAYMODE_PLATFORM,
    commit: "abc1234",
    runIndex: "-1",
    emitsPerSecond,
    gcAllocations,
    wallClockMs,
    gcAllocatedBytes
  };
}

function standaloneRows(entries) {
  return new Map([["Standalone", new Map(entries)]]);
}

function comparisonSectionsFor(rows) {
  const { comparisonByScope } = selectRowsForVersion(rows, "Unity 6000.3.16f1");
  return buildComparisonSections(comparisonByScope).map((section) => section.join("\n"));
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
    `2026-01-01T00:00:00 UntargetedFlood_OneHandler,${PLATFORM},abc1234,0,1000000,0,12.5,2048`,
    `UntargetedFlood_OneHandler,${PLATFORM},abc1234,0,1000000,0,12.5,2048`,
    `{scenario:"TargetedFlood_OneListener",platform:"${PLATFORM}",commit:"abc1234",runIndex:1,emitsPerSec:2500000.5,gcAllocations:64,wallClockMs:8.25,gcAllocatedBytes:4096}`,
    `UnknownScenario,${PLATFORM},abc1234,0,1,0,1,0`
  ].join("\n");

  const rows = extractRows(content);
  assert.deepEqual(
    rows.map(({ scenario, emitsPerSecond, gcAllocations, gcAllocatedBytes }) => [
      scenario,
      emitsPerSecond,
      gcAllocations,
      gcAllocatedBytes
    ]),
    [
      ["UntargetedFlood_OneHandler", "1000000.000", "0", "2048"],
      ["TargetedFlood_OneListener", "2500000.500", "64", "4096"]
    ]
  );
});

test("legacy 7-column CSV rows default gcAllocatedBytes to the -1 sentinel", () => {
  // An old baseline row (no gcAllocatedBytes column) must still parse: the missing
  // field defaults to the Unmeasured sentinel "-1" so re-extracting a legacy baseline
  // round-trips honestly instead of crashing.
  const legacy = `UntargetedFlood_OneHandler,${PLATFORM},abc1234,0,1000000,0,12.5`;
  const [parsed] = extractRows(legacy);
  assert.equal(parsed.gcAllocatedBytes, "-1");

  // Re-serializing yields the 8-column shape with the sentinel preserved.
  const reparsed = extractRows(buildCsv([parsed]));
  assert.deepEqual(reparsed, [parsed]);

  // A legacy structured-log line (no gcAllocatedBytes key) defaults the same way.
  const legacyLog = `{scenario:"UntargetedFlood_OneHandler",platform:"${PLATFORM}",commit:"abc1234",runIndex:0,emitsPerSec:1000000,gcAllocations:0,wallClockMs:12.5}`;
  assert.equal(extractRows(legacyLog)[0].gcAllocatedBytes, "-1");
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

test("delta sign is normalized so + is always better and - is always worse", () => {
  // goodnessRelativeChange: higher-is-better keeps the raw sign; lower-is-better
  // negates it, so an improvement is always positive regardless of metric direction.
  assert.ok(goodnessRelativeChange(110, 100, true) > 0, "more throughput is better (+)");
  assert.ok(goodnessRelativeChange(90, 100, true) < 0, "less throughput is worse (-)");
  assert.ok(goodnessRelativeChange(90, 100, false) > 0, "lower latency is better (+)");
  assert.ok(goodnessRelativeChange(110, 100, false) < 0, "higher latency is worse (-)");

  // formatAllocDelta words the direction (fewer/more) instead of a bare +/-.
  assert.equal(formatAllocDelta(-5), "5 fewer allocs");
  assert.equal(formatAllocDelta(5), "5 more allocs");

  // formatBytesDelta mirrors the count wording: fewer bytes is better.
  assert.equal(formatBytesDelta(-2048), "2,048 fewer bytes");
  assert.equal(formatBytesDelta(2048), "2,048 more bytes");
  assert.equal(formatBytesDelta(0), "0 bytes");

  // Throughput scenario: a faster run (more emits/sec) reads "+", a regression "-".
  const t = "UntargetedFlood_OneHandler";
  const up = compareRow(t, row(t, "1100000.000"), row(t, "1000000.000"), 0.02);
  const down = compareRow(t, row(t, "900000.000"), row(t, "1000000.000"), 0.02);
  assert.ok(up.cells[3].startsWith("+10.00%"), up.cells[3]);
  assert.ok(down.cells[3].startsWith("-10.00%"), down.cells[3]);

  // Registration (wall-clock) scenario: a FASTER run (lower ms) must read "+", even
  // though the raw number went DOWN -- this is the bug the normalization fixes. Fewer
  // allocations read "fewer"; more read "more".
  const reg = "RegistrationFlood_1000Types_FromColdBus";
  const baselineReg = row(reg, "0.000", "80", "12.500");
  const fasterFewer = compareRow(reg, row(reg, "0.000", "75", "11.000"), baselineReg, 0.02);
  const slowerMore = compareRow(reg, row(reg, "0.000", "85", "13.750"), baselineReg, 0.02);
  assert.ok(
    fasterFewer.cells[3].startsWith("+12.00%") && fasterFewer.cells[3].includes("5 fewer allocs"),
    fasterFewer.cells[3]
  );
  assert.ok(
    slowerMore.cells[3].startsWith("-10.00%") && slowerMore.cells[3].includes("5 more allocs"),
    slowerMore.cells[3]
  );
});

test("isRegression trips on large throughput drops or allocation growth", () => {
  const baseline = row("x", "1000000.000");
  const cases = [
    [row("x", "500000.000"), baseline, true],
    [row("x", "900000.000"), baseline, false],
    [row("x", "1000000.000", "128"), baseline, true],
    [row("x", "0.000"), row("x", "0.000"), false],
    [row("x", "1000000.000", "-1"), row("x", "1000000.000", "0"), false],
    [row("x", "1000000.000", "5"), row("x", "1000000.000", "-1"), false]
  ];
  for (const [current, base, expected] of cases) {
    assert.equal(isRegression(current, base, 0.33), expected);
  }
});

test("allocation reporting is honest: real counts render, sentinel renders n/a", () => {
  const s = "UntargetedFlood_OneHandler";
  const grew = compareRow(s, row(s, "1000000.000", "10"), row(s, "1000000.000", "0"), 0.02);
  assert.ok(grew.cells[3].includes("10 more allocs") && grew.moved, grew.cells[3]);

  const na = compareRow(s, row(s, "1000000.000", "-1"), row(s, "1000000.000", "-1"), 0.02);
  assert.ok(na.cells[1].includes("n/a") && na.cells[2].includes("n/a"), na.cells[1]);
  assert.ok(!na.cells[3].includes("alloc") && na.moved === false, na.cells[3]);

  const allocations = buildComparisonSections(
    standaloneRows([
      ["UnitySendMessage|GlobalToOne", comparisonRow("3000000.000", "110806")],
      ["MessagePipe|GlobalToOne", comparisonRow("9000000.000", "-1")]
    ])
  )[1].join("\n");
  assert.ok(allocations.includes("110,806") && allocations.includes("n/a"), allocations);
});

test("byte reporting mirrors allocs: real byte delta is goodness-signed, sentinel is n/a", () => {
  const s = "UntargetedFlood_OneHandler";
  const moreBytes = compareRow(
    s,
    row(s, "1000000.000", "0", "10.000", "4096"),
    row(s, "1000000.000", "0", "10.000", "2048"),
    0.02
  );
  assert.ok(moreBytes.cells[3].includes("2,048 more bytes"), moreBytes.cells[3]);
  assert.equal(moreBytes.moved, false, "bytes do not gate / do not mark moved");

  const fewerBytes = compareRow(
    s,
    row(s, "1000000.000", "0", "10.000", "1024"),
    row(s, "1000000.000", "0", "10.000", "2048"),
    0.02
  );
  assert.ok(fewerBytes.cells[3].includes("1,024 fewer bytes"), fewerBytes.cells[3]);

  assert.ok(moreBytes.cells[1].includes("2,048") && moreBytes.cells[2].includes("4,096"));
  const naBytes = compareRow(
    s,
    row(s, "1000000.000", "0", "10.000", "-1"),
    row(s, "1000000.000", "0", "10.000", "-1"),
    0.02
  );
  assert.ok(naBytes.cells[1].includes("n/a") && naBytes.cells[2].includes("n/a"), naBytes.cells[1]);
  assert.ok(!naBytes.cells[3].includes("bytes"), naBytes.cells[3]);
  assert.ok(!/\b0 bytes\b/.test(naBytes.cells[3]));
});

test("dispatch table renders a GC bytes column with real values and the n/a sentinel", () => {
  const scenario = "UntargetedFlood_OneHandler";
  const broadcast = "BroadcastFlood_OneHandler";
  const rows = [
    playModeRow(scenario, "20000000.000", "0", "5000.000", "0"),
    playModeRow(broadcast, "9000000.000", "1234", "5000.000", "98304")
  ];
  const block = buildBlock(selectRowsForVersion(rows, "Unity 6000.3.16f1"), "6000.3.16f1");
  assert.ok(block.includes("GC bytes"), block);
  assert.ok(block.includes("98,304"), block);

  const sentinelRows = [{ ...playModeRow(scenario, "20000000.000", "42"), gcAllocatedBytes: "-1" }];
  const sentinelBlock = buildBlock(
    selectRowsForVersion(sentinelRows, "Unity 6000.3.16f1"),
    "6000.3.16f1"
  );
  assert.ok(sentinelBlock.includes("42"), sentinelBlock);
  assert.ok(/\bn\/a\b/.test(sentinelBlock), sentinelBlock);
  assert.ok(!/\b42\b[^|]*n\/a[^|]*n\/a/.test(sentinelBlock), sentinelBlock);
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
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-perf-"));
  try {
    const currentPath = path.join(dir, "current.log");
    const baselinePath = path.join(dir, "baseline.csv");
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

test("render-perf-deltas CLI failures preserve non-gating diagnostic output", () => {
  const script = path.join(REPO_ROOT, "scripts", "unity", "render-perf-deltas.js");
  const result = spawnSync(process.execPath, [script, "--bogus"], {
    cwd: REPO_ROOT,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });

  assert.ifError(result.error);
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, "changed=false\nregressed=false\n");
  assert.match(result.stderr, /Unknown argument: --bogus/);
  assert.match(result.stderr, /workflow decides whether the regressed= signal fails CI/);
});

test("comparison-enabled Unity workflows run comparison contracts", () => {
  const workflowDir = path.join(REPO_ROOT, ".github", "workflows");
  const offenders = fs.readdirSync(workflowDir).flatMap((name) => {
    if (!name.endsWith(".yml") && !name.endsWith(".yaml")) {
      return [];
    }
    const text = fs.readFileSync(path.join(workflowDir, name), "utf8");
    if (!/include-comparisons:\s*["']?true["']?/i.test(text)) {
      return [];
    }
    const filters = [...text.matchAll(/DXM_UNITY_TEST_CATEGORY:\s*["']?([^"'\r\n#]+)/g)].map(
      (match) => match[1].trim()
    );
    return filters.length > 0 && !filters.some((filter) => filter.split(";").includes("Comparison"))
      ? [`${name}: include-comparisons=true but DXM_UNITY_TEST_CATEGORY=[${filters.join(", ")}]`]
      : [];
  });
  assert.deepEqual(offenders, []);
});

test("buildComparisonSections bolds per-scenario throughput winners only", () => {
  const sections = buildComparisonSections(
    standaloneRows([
      ["DxMessaging|GlobalToOne", comparisonRow("30000000.000")],
      ["MessagePipe|GlobalToOne", comparisonRow("90000000.000")],
      ["CsEvent|GlobalToOne", comparisonRow("90000000.000")],
      ["DxMessaging|KeyedToOne", comparisonRow("12000000.000", "64")],
      ["MessagePipe|KeyedToOne", comparisonRow("9000000.000")]
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
    standaloneRows([["UnityAtoms|Filtered", comparisonRow("5000000.000")]])
  )[0].join("\n");
  assert.ok(sole.includes("**5.00 M emits/sec**"));

  const tied = buildComparisonSections(
    standaloneRows([
      ["DxMessaging|GlobalToOne", comparisonRow("5001000.000")],
      ["MessagePipe|GlobalToOne", comparisonRow("5004000.000")]
    ])
  )[0].join("\n");
  assert.equal((tied.match(/\*\*5\.00 M emits\/sec\*\*/g) || []).length, 2);
});

test("deriveScope reads the execution scope from platform strings", () => {
  // ONE canonical deriveScope (perf-scenarios.js, re-exported by render-perf-doc
  // and extract-perf-baseline), so cover it directly over the full token table:
  // Standalone -> PlayMode -> EditMode precedence, null for no-token/non-string.
  const cases = [
    [STANDALONE_PLATFORM, "Standalone"],
    [EDITOR_PLAYMODE_PLATFORM, "PlayMode"],
    ["Unity 6000.3.16f1 EditMode Mono", "EditMode"],
    ["Unity 6000.3.16f1 Linux", null],
    [undefined, null],
    // Both tokens present: Standalone wins (most player-faithful scope).
    ["Standalone PlayMode mix", "Standalone"]
  ];
  for (const [platform, expected] of cases) {
    assert.equal(deriveScope(platform), expected, `${platform}`);
  }
  // The re-export consumed by extract-perf-baseline.js is the SAME function.
  assert.equal(extractorDeriveScope, deriveScope);
});

test("two scopes render separate dispatch tables; only the Mono leg shows real allocs", () => {
  const scenario = "UntargetedFlood_OneHandler";
  const broadcast = "BroadcastFlood_OneHandler";
  const rows = [
    // Standalone (IL2CPP) leg: allocations are the Unmeasured sentinel (-1 -> n/a).
    { ...dispatchRow(scenario, "37500000.000"), gcAllocations: "-1" },
    { ...dispatchRow(broadcast, "18000000.000"), gcAllocations: "-1" },
    // In-editor PlayMode (Mono) leg: REAL counts for the SAME version.
    playModeRow(scenario, "20000000.000", "0"),
    playModeRow(broadcast, "9000000.000", "1234")
  ];
  const block = buildBlock(selectRowsForVersion(rows, "Unity 6000.3.16f1"), "6000.3.16f1");

  // Two dispatch sections, Standalone before PlayMode (headline order).
  const standaloneHeading = "### Dispatch throughput - Standalone (IL2CPP)";
  const playModeHeading = "### Dispatch throughput - PlayMode (Mono)";
  assert.ok(block.includes(standaloneHeading), block);
  assert.ok(block.includes(playModeHeading), block);
  assert.ok(
    block.indexOf(standaloneHeading) < block.indexOf(playModeHeading),
    "Standalone dispatch table must precede the PlayMode one"
  );

  const standaloneSection = block.slice(
    block.indexOf(standaloneHeading),
    block.indexOf(playModeHeading)
  );
  const playModeSection = block.slice(block.indexOf(playModeHeading));

  assert.ok(/\bn\/a\b/.test(standaloneSection), standaloneSection);
  assert.ok(!playModeSection.includes("n/a"), playModeSection);
  assert.ok(playModeSection.includes("| 0 "), playModeSection);
  assert.ok(playModeSection.includes("1,234"), playModeSection);
});

test("comparison throughput stays on Standalone while GC count+bytes come from the Mono leg", () => {
  const rows = [
    dispatchRow("Comparison_DxMessaging_GlobalToOne", "28000000.000", "-1", "-1"),
    dispatchRow("Comparison_MessagePipe_GlobalToOne", "68000000.000", "-1", "-1"),
    playModeRow("Comparison_DxMessaging_GlobalToOne", "20000000.000", "0", "5000.000", "0"),
    playModeRow("Comparison_MessagePipe_GlobalToOne", "40000000.000", "110806", "5000.000", "20000")
  ];
  const sections = comparisonSectionsFor(rows);
  assert.equal(sections.length, 3);
  const [throughput, allocations, bytes] = sections;

  assert.ok(throughput.includes("### Library comparison - throughput (Standalone (IL2CPP))"));
  assert.ok(throughput.includes("68.00 M emits/sec"), throughput);
  assert.ok(
    allocations.includes("### Library comparison - GC allocations per 10k ops (PlayMode (Mono))"),
    allocations
  );
  assert.ok(allocations.includes("110,806") && allocations.includes("| 0 "), allocations);
  assert.ok(
    bytes.includes("### Library comparison - GC allocated bytes per 10k ops (PlayMode (Mono))"),
    bytes
  );
  assert.ok(bytes.includes("20,000") && bytes.includes("| 0 "), bytes);
});

test("comparison bytes choose their own measured scope", () => {
  const cases = [
    [
      [
        dispatchRow("Comparison_DxMessaging_GlobalToOne", "28000000.000", "-1", "4096"),
        dispatchRow("Comparison_MessagePipe_GlobalToOne", "68000000.000", "-1", "8192"),
        playModeRow("Comparison_DxMessaging_GlobalToOne", "20000000.000", "0", "5000.000", "-1"),
        playModeRow(
          "Comparison_MessagePipe_GlobalToOne",
          "40000000.000",
          "110806",
          "5000.000",
          "-1"
        )
      ],
      "GC allocations per 10k ops (PlayMode (Mono))",
      "110,806",
      "GC allocated bytes per 10k ops (Standalone (IL2CPP))"
    ],
    [
      [
        dispatchRow("Comparison_DxMessaging_GlobalToOne", "28000000.000", "-1", "-1"),
        dispatchRow("Comparison_MessagePipe_GlobalToOne", "68000000.000", "-1", "-1"),
        playModeRow("Comparison_DxMessaging_GlobalToOne", "20000000.000", "-1", "5000.000", "4096"),
        playModeRow("Comparison_MessagePipe_GlobalToOne", "40000000.000", "-1", "5000.000", "8192")
      ],
      "GC allocations per 10k ops (Standalone (IL2CPP))",
      "n/a",
      "GC allocated bytes per 10k ops (PlayMode (Mono))"
    ]
  ];
  for (const [rows, allocHeading, allocValue, bytesHeading] of cases) {
    const [, allocations, bytes] = comparisonSectionsFor(rows);
    assert.ok(allocations.includes(allocHeading) && allocations.includes(allocValue), allocations);
    assert.ok(bytes.includes(bytesHeading), bytes);
    assert.ok(bytes.includes("4,096") && bytes.includes("8,192"), bytes);
  }
});

test("comparison count+bytes matrices stay Standalone n/a when no editor leg ran", () => {
  const sections = buildComparisonSections(
    standaloneRows([
      ["DxMessaging|GlobalToOne", comparisonRow("28000000.000", "-1", "-1")],
      ["MessagePipe|GlobalToOne", comparisonRow("68000000.000", "-1", "-1")]
    ])
  );
  assert.equal(sections.length, 3);
  const allocations = sections[1].join("\n");
  const bytes = sections[2].join("\n");
  assert.ok(
    allocations.includes(
      "### Library comparison - GC allocations per 10k ops (Standalone (IL2CPP))"
    ) && allocations.includes("n/a"),
    allocations
  );
  assert.ok(
    bytes.includes(
      "### Library comparison - GC allocated bytes per 10k ops (Standalone (IL2CPP))"
    ) && bytes.includes("n/a"),
    bytes
  );
});

test("extract-perf-baseline --scope filters rows to one execution scope", () => {
  // --scope Standalone drops the PlayMode/EditMode rows and keeps Standalone rows.
  const mixed = [
    `UntargetedFlood_OneHandler,${STANDALONE_PLATFORM},abc1234,-1,37500000,-1,5000`,
    `UntargetedFlood_OneHandler,${EDITOR_PLAYMODE_PLATFORM},abc1234,-1,20000000,0,5000`,
    `TargetedFlood_OneListener,Unity 6000.3.16f1 EditMode Mono,abc1234,-1,9000000,0,5000`
  ].join("\n");
  const all = extractRows(mixed);
  const standaloneOnly = all.filter((r) => extractorDeriveScope(r.platform) === "Standalone");
  assert.equal(all.length, 3);
  assert.deepEqual(
    standaloneOnly.map((r) => r.platform),
    [STANDALONE_PLATFORM]
  );

  // End-to-end via the CLI: --scope Standalone against a mixed input writes ONLY
  // the Standalone row; an all-editor input with --scope Standalone fails loudly.
  const script = path.join(REPO_ROOT, "scripts", "unity", "extract-perf-baseline.js");
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-scope-"));
  try {
    const mixedPath = path.join(dir, "mixed.log");
    const editorOnlyPath = path.join(dir, "editor.log");
    fs.writeFileSync(mixedPath, mixed);
    fs.writeFileSync(
      editorOnlyPath,
      `UntargetedFlood_OneHandler,${EDITOR_PLAYMODE_PLATFORM},abc1234,-1,20000000,0,5000`
    );

    const kept = spawnSync(
      process.execPath,
      [script, "--input", mixedPath, "--scope", "Standalone"],
      { cwd: REPO_ROOT, encoding: "utf8" }
    );
    assert.equal(kept.status, 0, kept.stderr);
    const keptRows = extractRows(kept.stdout);
    assert.deepEqual(
      keptRows.map((r) => r.platform),
      [STANDALONE_PLATFORM]
    );

    const empty = spawnSync(
      process.execPath,
      [script, "--input", editorOnlyPath, "--scope", "Standalone"],
      { cwd: REPO_ROOT, encoding: "utf8" }
    );
    assert.notEqual(empty.status, 0);
    assert.match(empty.stderr, /No DispatchThroughputBenchmarks rows found/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
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
  const dispatchByScope = standaloneRows([
    ["UntargetedFlood_OneHandler", dispatchRow("UntargetedFlood_OneHandler", "37690000.000")]
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
