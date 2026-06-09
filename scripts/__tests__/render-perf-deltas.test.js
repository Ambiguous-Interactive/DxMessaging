"use strict";

const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");

const {
  DEFAULT_SCOPE,
  DEFAULT_TOLERANCE,
  DEFAULT_REGRESSION_THRESHOLD,
  isDxMessagingRow,
  indexDxMessagingRows,
  deltaScenarioOrder,
  scenarioLabel,
  buildDeltaTable,
  isRegression,
  computeRegressed,
  render
} = require("../unity/render-perf-deltas.js");
const { extractRows } = require("../unity/extract-perf-baseline.js");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");

const SCRIPT = path.resolve(__dirname, "..", "unity", "render-perf-deltas.js");
const LATEST = "6000.3.16f1";

// Platform string with the execution scope as its leading token plus the real
// scripting backend. Standalone runs IL2CPP; PlayMode/EditMode run Mono. Matching
// must key off the SCOPE substring, NOT the backend word.
function platform(scope = "PlayMode", version = LATEST) {
  const target = scope === "Standalone" ? "Standalone" : `Editor ${scope}`;
  const backend = scope === "Standalone" ? "IL2CPP" : "Mono";
  return `${target} ${backend} x64 Release (LinuxEditor; Unity ${version})`;
}

function structuredLine(scenario, platformString, commit, emits, alloc, ms) {
  return (
    `[TestRunner] {scenario:"${scenario}", platform:"${platformString}", ` +
    `commit:"${commit}", runIndex:-1, emitsPerSec:${emits}, ` +
    `allocatedBytesDelta:${alloc}, wallClockMs:${ms}}`
  );
}

function csvLine(scenario, platformString, commit, emits, alloc, ms) {
  return [scenario, platformString, commit, "-1", emits, alloc, ms].join(",");
}

const CSV_HEADER =
  "scenario,platform,commit,runIndex,emitsPerSecond,allocatedBytesDelta,wallClockMs";

// A baseline value table. [scenario, emits, alloc, ms]. Includes the 13 dispatch
// scenarios (the registration floods and the three cold first-dispatch scenarios carry
// 0 emits + wall clock), DxMessaging comparison rows, AND a couple of NON-DxMessaging
// comparison rows that must be ignored. 13 dispatch + 2 DxMessaging comparison = 15
// DxMessaging rows that survive indexDxMessagingRows.
const BASELINE = [
  ["UntargetedFlood_OneHandler", 25000000, 0, 1000],
  ["UntargetedFlood_FourHandlers_OnePriority", 12000000, 0, 1000],
  ["UntargetedFlood_FourHandlers_FourPriorities", 8000000, 0, 1000],
  ["UntargetedFirstDispatch_Cold", 0, 128, 0.25],
  ["TargetedFlood_OneListener", 18000000, 0, 1000],
  ["TargetedFlood_SixteenListeners", 4000000, 0, 1000],
  ["TargetedFirstDispatch_Cold", 0, 96, 0.3],
  ["BroadcastFlood_OneHandler", 17000000, 0, 1000],
  ["BroadcastFirstDispatch_Cold", 0, 64, 0.2],
  ["InterceptorHeavy_FourInterceptors", 7000000, 0, 1000],
  ["PostProcessingHeavy_FourPostProcessors", 6000000, 0, 1000],
  ["RegistrationFlood_1000Types_FromColdBus", 0, 4096, 12.345],
  ["RegistrationFlood_1000Types_WarmJit", 0, 4096, 8.5],
  ["Comparison_DxMessaging_GlobalToOne", 16980000, 0, 1000],
  ["Comparison_DxMessaging_StructNoBox", 20000000, 0, 1000],
  // Non-DxMessaging comparison rows -- must be excluded from the delta table.
  ["Comparison_MessagePipe_GlobalToOne", 10000000, 240000000, 1000],
  ["Comparison_UniRx_GlobalToOne", 7000000, 0, 1000]
];

function baselineCsv({ scope = "PlayMode", mutate = {} } = {}) {
  const platformString = platform(scope);
  return [
    CSV_HEADER,
    ...BASELINE.map(([scenario, emits, alloc, ms]) => {
      const override = mutate[scenario] || {};
      const finalEmits = override.emits !== undefined ? override.emits : emits;
      const finalAlloc = override.alloc !== undefined ? override.alloc : alloc;
      const finalMs = override.ms !== undefined ? override.ms : ms;
      return csvLine(
        scenario,
        platformString,
        "base123",
        finalEmits.toFixed(3),
        String(finalAlloc),
        finalMs.toFixed(3)
      );
    })
  ].join("\n");
}

// A current-run log. By default it mirrors the baseline values exactly (a no-op
// run). `mutate` lets a test override individual [scenario] -> {emits, alloc, ms}.
function currentLog({ scope = "PlayMode", commit = "cur999", mutate = {} } = {}) {
  const platformString = platform(scope);
  return BASELINE.map(([scenario, emits, alloc, ms]) => {
    const override = mutate[scenario] || {};
    const finalEmits = override.emits !== undefined ? override.emits : emits;
    const finalAlloc = override.alloc !== undefined ? override.alloc : alloc;
    const finalMs = override.ms !== undefined ? override.ms : ms;
    return structuredLine(scenario, platformString, commit, finalEmits, finalAlloc, finalMs);
  }).join("\n");
}

// Parse the markdown table the script writes into trimmed-cell rows, skipping the
// separator. Returns [] when the content has no table.
function tableRows(markdown) {
  const rows = [];
  for (const line of markdown.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed.startsWith("|")) {
      continue;
    }
    const cells = trimmed
      .replace(/^\|/, "")
      .replace(/\|$/, "")
      .split("|")
      .map((cell) => cell.trim());
    if (cells.every((cell) => /^:?-+:?$/.test(cell))) {
      continue;
    }
    rows.push(cells);
  }
  return rows;
}

function rowFor(markdown, scenarioLabelText) {
  return tableRows(markdown).find((cells) => cells[0] === scenarioLabelText);
}

describe("render-perf-deltas isDxMessagingRow (DxMessaging-only filter)", () => {
  test("keeps the dispatch scenarios", () => {
    expect(isDxMessagingRow("UntargetedFlood_OneHandler")).toBe(true);
    expect(isDxMessagingRow("RegistrationFlood_1000Types_FromColdBus")).toBe(true);
  });

  test("keeps Comparison_DxMessaging_* rows only", () => {
    expect(isDxMessagingRow("Comparison_DxMessaging_GlobalToOne")).toBe(true);
    expect(isDxMessagingRow("Comparison_DxMessaging_StructNoBox")).toBe(true);
  });

  test("drops every other Comparison_<tech>_* row", () => {
    expect(isDxMessagingRow("Comparison_MessagePipe_GlobalToOne")).toBe(false);
    expect(isDxMessagingRow("Comparison_UniRx_GlobalToOne")).toBe(false);
    expect(isDxMessagingRow("Comparison_ZenjectSignalBus_PriorityOrdered")).toBe(false);
  });

  test("drops unknown / malformed scenarios", () => {
    expect(isDxMessagingRow("NotAScenario")).toBe(false);
    expect(isDxMessagingRow("Comparison_DxMessaging_NotAScenario")).toBe(false);
    expect(isDxMessagingRow(undefined)).toBe(false);
  });
});

describe("render-perf-deltas indexDxMessagingRows (scope matching)", () => {
  test("indexes only rows whose derived scope matches, ignoring the backend word", () => {
    const rows = extractRows(
      [currentLog({ scope: "PlayMode" }), currentLog({ scope: "Standalone" })].join("\n")
    );
    const playMode = indexDxMessagingRows(rows, "PlayMode");
    // 13 dispatch + 2 DxMessaging comparison rows; the 2 non-DxMessaging rows are dropped.
    expect(playMode.size).toBe(15);
    expect(playMode.has("Comparison_MessagePipe_GlobalToOne")).toBe(false);
    for (const row of playMode.values()) {
      // The platform carries "Mono" (PlayMode) -- scope still matched on PlayMode.
      expect(row.platform).toContain("PlayMode");
    }

    const standalone = indexDxMessagingRows(rows, "Standalone");
    expect(standalone.size).toBe(15);
    for (const row of standalone.values()) {
      // Standalone rows carry the IL2CPP backend token; matching is by scope only.
      expect(row.platform).toContain("Standalone IL2CPP");
    }
  });

  test("a Mono/IL2CPP backend difference does NOT break current-vs-baseline matching", () => {
    // Baseline platform says "Mono"; an imagined future run says a different
    // backend word but the SAME PlayMode scope -- both must index under the scope.
    const baseRows = extractRows(baselineCsv({ scope: "PlayMode" }));
    const oddBackend = currentLog({ scope: "PlayMode" }).replace(
      /PlayMode Mono/g,
      "PlayMode CoreCLR"
    );
    const curRows = extractRows(oddBackend);

    const baseIndex = indexDxMessagingRows(baseRows, "PlayMode");
    const curIndex = indexDxMessagingRows(curRows, "PlayMode");
    expect(baseIndex.has("UntargetedFlood_OneHandler")).toBe(true);
    expect(curIndex.has("UntargetedFlood_OneHandler")).toBe(true);
  });
});

describe("render-perf-deltas deltaScenarioOrder + scenarioLabel", () => {
  test("orders the 13 dispatch scenarios first, then DxMessaging comparison scenarios", () => {
    const order = deltaScenarioOrder();
    // The 13 dispatch scenarios in SCENARIO_ORDER: each cold first-dispatch key sits
    // beside its warm sibling kind-group, and the warm-JIT flood beside the cold flood.
    expect(order.slice(0, 13)).toEqual([
      "UntargetedFlood_OneHandler",
      "UntargetedFlood_FourHandlers_OnePriority",
      "UntargetedFlood_FourHandlers_FourPriorities",
      "UntargetedFirstDispatch_Cold",
      "TargetedFlood_OneListener",
      "TargetedFlood_SixteenListeners",
      "TargetedFirstDispatch_Cold",
      "BroadcastFlood_OneHandler",
      "BroadcastFirstDispatch_Cold",
      "InterceptorHeavy_FourInterceptors",
      "PostProcessingHeavy_FourPostProcessors",
      "RegistrationFlood_1000Types_FromColdBus",
      "RegistrationFlood_1000Types_WarmJit"
    ]);
    expect(order).toContain("Comparison_DxMessaging_GlobalToOne");
    expect(order.every((key) => !key.startsWith("Comparison_MessagePipe"))).toBe(true);
  });

  test("scenarioLabel maps keys to the same human labels the doc uses", () => {
    expect(scenarioLabel("UntargetedFlood_OneHandler")).toBe("Untargeted Flood (One Handler)");
    expect(scenarioLabel("Comparison_DxMessaging_GlobalToOne")).toBe("Global -> 1 subscriber");
  });
});

describe("render-perf-deltas buildDeltaTable (table + changed signal)", () => {
  function indexes(mutate = {}, scope = "PlayMode") {
    const current = indexDxMessagingRows(extractRows(currentLog({ scope, mutate })), scope);
    const baseline = indexDxMessagingRows(extractRows(baselineCsv({ scope })), scope);
    return { current, baseline };
  }

  test("a within-tolerance run is changed=false and renders every overlapping row", () => {
    const { current, baseline } = indexes({
      UntargetedFlood_OneHandler: { emits: 25100000 } // +0.4%, within 2%
    });
    const result = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    expect(result.changed).toBe(false);
    const rows = tableRows(result.markdown);
    // Header + 15 DxMessaging rows.
    expect(rows[0]).toEqual(["Scenario", "Baseline", "Current", "Delta"]);
    expect(rows).toHaveLength(16);
    // Non-DxMessaging rows never appear.
    expect(result.markdown).not.toContain("MessagePipe");
    expect(result.markdown).not.toContain("UniRx");
  });

  test("a throughput regression beyond tolerance flips changed=true", () => {
    const { current, baseline } = indexes({
      UntargetedFlood_OneHandler: { emits: 12000000 } // -52%
    });
    const result = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    expect(result.changed).toBe(true);
    const row = rowFor(result.markdown, "Untargeted Flood (One Handler)");
    expect(row[1]).toContain("25.00 M emits/sec");
    expect(row[2]).toContain("12.00 M emits/sec");
    expect(row[3]).toContain("-52.00%");
  });

  test("an allocation regression (0 -> non-zero) flips changed=true and shows the byte delta", () => {
    const { current, baseline } = indexes({
      Comparison_DxMessaging_GlobalToOne: { alloc: 48 }
    });
    const result = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    expect(result.changed).toBe(true);
    const row = rowFor(result.markdown, "Global -> 1 subscriber");
    expect(row[3]).toContain("+48 B");
  });

  test("the registration scenario compares on wall-clock percent, not throughput", () => {
    const { current, baseline } = indexes({
      RegistrationFlood_1000Types_FromColdBus: { ms: 20.0 } // 12.345 -> 20.0 ms, big slowdown
    });
    const result = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    expect(result.changed).toBe(true);
    const row = rowFor(result.markdown, "Registration Flood (1000 Types, Cold Bus)");
    expect(row[1]).toContain("12.345 ms");
    expect(row[2]).toContain("20.000 ms");
    // +62% wall-clock move, sign present.
    expect(row[3]).toMatch(/^\+\d/);
  });

  test("a within-tolerance wall-clock jitter on registration stays changed=false", () => {
    const { current, baseline } = indexes({
      RegistrationFlood_1000Types_FromColdBus: { ms: 12.4 } // ~+0.45%
    });
    const result = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    expect(result.changed).toBe(false);
  });

  test("no overlapping scenarios yields a note and changed=false", () => {
    const baseline = indexDxMessagingRows(extractRows(baselineCsv()), "PlayMode");
    const empty = new Map();
    const result = buildDeltaTable(empty, baseline, DEFAULT_TOLERANCE, "Standalone");
    expect(result.changed).toBe(false);
    expect(result.markdown).toContain("No overlapping DxMessaging scenarios");
    expect(result.markdown).toContain("Standalone");
  });
});

describe("render-perf-deltas regression gate (isRegression / computeRegressed)", () => {
  // Build current + baseline indexes (PlayMode) from the shared BASELINE table; the
  // baseline can be mutated independently of the current run so a test can move only
  // one side. Both sides go through extractRows so they share the real parser.
  function indexes({ current = {}, baseline = {} } = {}, scope = "PlayMode") {
    const currentIndex = indexDxMessagingRows(
      extractRows(currentLog({ scope, mutate: current })),
      scope
    );
    const baselineIndex = indexDxMessagingRows(
      extractRows(currentLog({ scope, commit: "base123", mutate: baseline })),
      scope
    );
    return { current: currentIndex, baseline: baselineIndex };
  }

  test("DEFAULT_REGRESSION_THRESHOLD is 0.33", () => {
    expect(DEFAULT_REGRESSION_THRESHOLD).toBe(0.33);
  });

  // (a) Boundary uses a STRICT `>`: a relative throughput drop just ABOVE the
  // threshold regresses; just BELOW (and exactly AT) the threshold does not.
  test("a throughput drop just ABOVE the threshold flips regressed=true", () => {
    // 25,000,000 -> 16,740,000 is a 33.04% drop (> 33%).
    const { current, baseline } = indexes({
      current: { UntargetedFlood_OneHandler: { emits: 16740000 } }
    });
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(true);
  });

  test("a throughput drop just BELOW the threshold stays regressed=false", () => {
    // 25,000,000 -> 16,760,000 is a 32.96% drop (< 33%).
    const { current, baseline } = indexes({
      current: { UntargetedFlood_OneHandler: { emits: 16760000 } }
    });
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(false);
  });

  test("a throughput drop EXACTLY at the threshold is NOT a regression (strict >)", () => {
    // 25,000,000 -> 16,750,000 is exactly a 33% drop; strict `>` means not regressed.
    const baselineRow = { emitsPerSecond: "25000000.000", allocatedBytesDelta: "0" };
    const currentRow = { emitsPerSecond: "16750000.000", allocatedBytesDelta: "0" };
    expect(isRegression(currentRow, baselineRow, 0.33)).toBe(false);
    // A hair beyond the boundary regresses.
    expect(
      isRegression({ emitsPerSecond: "16749999.000", allocatedBytesDelta: "0" }, baselineRow, 0.33)
    ).toBe(true);
  });

  test("a throughput IMPROVEMENT is never a regression", () => {
    const { current, baseline } = indexes({
      current: { UntargetedFlood_OneHandler: { emits: 50000000 } } // 2x faster
    });
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(false);
  });

  // (b) A current allocation STRICTLY greater than baseline regresses, independent of
  // the throughput threshold.
  test("a current allocation greater than baseline flips regressed=true", () => {
    const { current, baseline } = indexes({
      current: { Comparison_DxMessaging_GlobalToOne: { alloc: 48 } } // baseline 0 -> 48
    });
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(true);
  });

  test("a tiny allocation increase can be changed=false but still regressed=true", () => {
    const { current, baseline } = indexes({
      baseline: { Comparison_DxMessaging_GlobalToOne: { alloc: 1000 } },
      current: { Comparison_DxMessaging_GlobalToOne: { alloc: 1001 } }
    });

    const table = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    expect(table.changed).toBe(false);
    expect(rowFor(table.markdown, "Global -> 1 subscriber")[3]).toContain("+1 B");
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(true);
  });

  test("an equal or lower current allocation is NOT a regression", () => {
    const equal = indexes({
      current: { Comparison_DxMessaging_GlobalToOne: { alloc: 0 } }
    });
    expect(computeRegressed(equal.current, equal.baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(
      false
    );
    // A DROP in allocation (baseline 240,000,000 on a DxMessaging scenario -> 0) is
    // an improvement, not a regression.
    const lower = indexes({
      baseline: { Comparison_DxMessaging_StructNoBox: { alloc: 1000 } },
      current: { Comparison_DxMessaging_StructNoBox: { alloc: 0 } }
    });
    expect(computeRegressed(lower.current, lower.baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(
      false
    );
  });

  // (c) The registration flood (zero throughput, wall-clock + allocation only) and
  // any zero-throughput scenario are EXCLUDED from the gate -- even a huge wall-clock
  // slowdown or an allocation increase there must not trip regressed.
  test("the registration flood is EXCLUDED from the regression gate", () => {
    const { current, baseline } = indexes({
      current: { RegistrationFlood_1000Types_FromColdBus: { ms: 100.0, alloc: 8192 } }
    });
    // changed flips (the comment still reports it) but the GATE ignores it.
    const table = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    expect(table.changed).toBe(true);
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(false);
  });

  // The cold first-dispatch scenarios and the warm-JIT flood are wall-clock (latency)
  // rows with zero baseline throughput, so they are auto-excluded from the gate exactly
  // like the cold registration flood -- even a large wall-clock slowdown or an allocation
  // jump must not trip regressed. Mirrors the flood-exclusion test above.
  test("the cold first-dispatch and warm-JIT latency rows are EXCLUDED from the regression gate", () => {
    const { current, baseline } = indexes({
      current: {
        UntargetedFirstDispatch_Cold: { ms: 50.0, alloc: 999999 },
        TargetedFirstDispatch_Cold: { ms: 50.0, alloc: 999999 },
        BroadcastFirstDispatch_Cold: { ms: 50.0, alloc: 999999 },
        RegistrationFlood_1000Types_WarmJit: { ms: 100.0, alloc: 8192 }
      }
    });
    // The delta comment still reports the movement (changed=true)...
    const table = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    expect(table.changed).toBe(true);
    // ...but the GATE ignores every one of these zero-throughput latency rows.
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(false);
  });

  test("a cold first-dispatch row renders as wall-clock percent in the delta table", () => {
    const { current, baseline } = indexes({
      current: { UntargetedFirstDispatch_Cold: { ms: 0.5 } } // 0.25 -> 0.5 ms, +100%
    });
    const table = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    const row = rowFor(table.markdown, "Untargeted First Dispatch (Cold, Distinct Types)");
    expect(row).toBeDefined();
    expect(row[1]).toContain("0.250 ms");
    expect(row[2]).toContain("0.500 ms");
    // Wall-clock percent move, sign present (never a throughput unit on a latency row).
    expect(row[3]).toMatch(/^\+\d/);
    expect(row[1]).not.toContain("M emits/sec");
  });

  test("a zero-throughput overlapping scenario never regresses, even on an allocation jump", () => {
    // Force a normally-throughput scenario to zero emits on BOTH sides: with no
    // baseline throughput it is excluded, so an allocation increase cannot regress.
    const { current, baseline } = indexes({
      baseline: { Comparison_DxMessaging_StructNoBox: { emits: 0, alloc: 0 } },
      current: { Comparison_DxMessaging_StructNoBox: { emits: 0, alloc: 999999 } }
    });
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(false);
  });

  // (e) Partial overlap: a current-only extra scenario is skipped by both the delta
  // table and the gate, while the overlapping rows still render and evaluate.
  test("a current-only extra scenario is skipped; overlapping rows still render and gate", () => {
    const current = indexDxMessagingRows(extractRows(currentLog()), "PlayMode");
    // Baseline is missing one dispatch scenario entirely (drop its row).
    const baselineSource = currentLog({ commit: "base123" })
      .split("\n")
      .filter((line) => !line.includes('scenario:"TargetedFlood_OneListener"'))
      .join("\n");
    const baseline = indexDxMessagingRows(extractRows(baselineSource), "PlayMode");
    expect(baseline.has("TargetedFlood_OneListener")).toBe(false);

    const table = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    // The non-overlapping scenario does not produce a row...
    expect(rowFor(table.markdown, "Targeted Flood (One Listener)")).toBeUndefined();
    // ...but an overlapping one still does.
    expect(rowFor(table.markdown, "Untargeted Flood (One Handler)")).toBeDefined();
    // A quiet run over the overlapping rows is not a regression.
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(false);
  });

  // (f) An overlapping scenario whose BASELINE throughput is 0 must NOT divide-by-zero:
  // it is excluded from the gate, and the delta cell renders "n/a".
  test("baseline emitsPerSecond=0 yields delta 'n/a' and never crashes the gate", () => {
    const { current, baseline } = indexes({
      baseline: { Comparison_DxMessaging_GlobalToOne: { emits: 0 } }
    });
    // No throw, and the zero-baseline scenario is excluded from the gate.
    expect(computeRegressed(current, baseline, DEFAULT_REGRESSION_THRESHOLD)).toBe(false);
    const table = buildDeltaTable(current, baseline, DEFAULT_TOLERANCE, "PlayMode");
    const row = rowFor(table.markdown, "Global -> 1 subscriber");
    expect(row).toBeDefined();
    // Divide-by-zero guard: the delta cell shows "n/a", not "Infinity%" / "NaN%".
    expect(row[3]).toContain("n/a");
    expect(row[3]).not.toContain("Infinity");
    expect(row[3]).not.toContain("NaN");
  });

  test("computeRegressed over an empty baseline map is false (no overlap)", () => {
    const current = indexDxMessagingRows(extractRows(currentLog()), "PlayMode");
    expect(computeRegressed(current, new Map(), DEFAULT_REGRESSION_THRESHOLD)).toBe(false);
  });
});

describe("render-perf-deltas render (graceful missing baseline)", () => {
  let tempDir;

  beforeEach(() => {
    tempDir = makeTempDir("render-deltas");
  });

  afterEach(() => {
    cleanupDir(tempDir);
  });

  test("writes a 'no baseline on master yet' note and changed=false when the baseline CSV is absent", () => {
    const inputPath = path.join(tempDir, "current.log");
    fs.writeFileSync(inputPath, currentLog(), "utf8");

    const result = render({
      inputs: [inputPath],
      baselineCsv: path.join(tempDir, "does-not-exist.csv"),
      unityVersion: LATEST,
      tolerance: DEFAULT_TOLERANCE,
      scope: DEFAULT_SCOPE,
      output: ""
    });
    expect(result.changed).toBe(false);
    expect(result.regressed).toBe(false);
    expect(result.markdown).toContain("No baseline on master yet");
  });

  test("does not even read --input when the baseline is missing (no throw on absent inputs)", () => {
    // baseline missing short-circuits before readInputs, so an absent/empty input
    // list must not crash the graceful path.
    const result = render({
      inputs: [],
      baselineCsv: path.join(tempDir, "missing.csv"),
      unityVersion: LATEST,
      tolerance: DEFAULT_TOLERANCE,
      scope: DEFAULT_SCOPE,
      output: ""
    });
    expect(result.changed).toBe(false);
    expect(result.regressed).toBe(false);
    expect(result.markdown).toContain("No baseline on master yet");
  });

  // (d) A header-only baseline CSV (file EXISTS but has no data rows) is treated
  // exactly like a missing baseline: graceful note, changed=false, regressed=false.
  test("a header-only baseline CSV degrades like a missing baseline (note + changed/regressed false)", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    fs.writeFileSync(inputPath, currentLog(), "utf8");
    // CSV_HEADER only -- a real first-rollout baseline file with zero data rows.
    fs.writeFileSync(baselinePath, `${CSV_HEADER}\n`, "utf8");

    const result = render({
      inputs: [inputPath],
      baselineCsv: baselinePath,
      unityVersion: LATEST,
      tolerance: DEFAULT_TOLERANCE,
      scope: DEFAULT_SCOPE,
      output: ""
    });
    expect(result.changed).toBe(false);
    expect(result.regressed).toBe(false);
    expect(result.markdown).toContain("No baseline on master yet");
  });

  // A real regression flows all the way through render(): regressed=true while the
  // table still renders and changed=true.
  test("render reports regressed=true on a throughput regression beyond the gate threshold", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    fs.writeFileSync(
      inputPath,
      currentLog({ mutate: { UntargetedFlood_OneHandler: { emits: 12000000 } } }), // -52%
      "utf8"
    );
    fs.writeFileSync(baselinePath, baselineCsv(), "utf8");

    const result = render({
      inputs: [inputPath],
      baselineCsv: baselinePath,
      unityVersion: LATEST,
      tolerance: DEFAULT_TOLERANCE,
      regressionThreshold: DEFAULT_REGRESSION_THRESHOLD,
      scope: DEFAULT_SCOPE,
      output: ""
    });
    expect(result.changed).toBe(true);
    expect(result.regressed).toBe(true);
  });

  // A within-gate (but reported) movement: changed=true, regressed=false. Proves the
  // gate threshold is independent of (looser than) the comment tolerance.
  test("a movement past --tolerance but within the gate threshold is changed=true, regressed=false", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    // 25,000,000 -> 20,000,000 is a 20% drop: past the 2% comment tolerance but
    // within the 33% gate threshold.
    fs.writeFileSync(
      inputPath,
      currentLog({ mutate: { UntargetedFlood_OneHandler: { emits: 20000000 } } }),
      "utf8"
    );
    fs.writeFileSync(baselinePath, baselineCsv(), "utf8");

    const result = render({
      inputs: [inputPath],
      baselineCsv: baselinePath,
      unityVersion: LATEST,
      tolerance: DEFAULT_TOLERANCE,
      regressionThreshold: DEFAULT_REGRESSION_THRESHOLD,
      scope: DEFAULT_SCOPE,
      output: ""
    });
    expect(result.changed).toBe(true);
    expect(result.regressed).toBe(false);
  });

  test("--regression-threshold can be tightened so a smaller drop trips the gate", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    // A 20% drop trips a 0.10 gate but not the default 0.33 gate.
    fs.writeFileSync(
      inputPath,
      currentLog({ mutate: { UntargetedFlood_OneHandler: { emits: 20000000 } } }),
      "utf8"
    );
    fs.writeFileSync(baselinePath, baselineCsv(), "utf8");

    const tight = render({
      inputs: [inputPath],
      baselineCsv: baselinePath,
      unityVersion: LATEST,
      tolerance: DEFAULT_TOLERANCE,
      regressionThreshold: 0.1,
      scope: DEFAULT_SCOPE,
      output: ""
    });
    expect(tight.regressed).toBe(true);
  });

  test("--unity-version filters out rows from a different Unity version (no false overlap)", () => {
    const OTHER = "2022.3.45f1";
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    // The current run only has OTHER-version rows; the baseline has LATEST rows.
    // Filtering to LATEST leaves the current side empty -> no overlap, no change.
    fs.writeFileSync(
      inputPath,
      currentLog({ mutate: { UntargetedFlood_OneHandler: { emits: 1 } } }).replace(
        new RegExp(`Unity ${LATEST}`, "g"),
        `Unity ${OTHER}`
      ),
      "utf8"
    );
    fs.writeFileSync(baselinePath, baselineCsv(), "utf8");

    const result = render({
      inputs: [inputPath],
      baselineCsv: baselinePath,
      unityVersion: LATEST,
      tolerance: DEFAULT_TOLERANCE,
      scope: DEFAULT_SCOPE,
      output: ""
    });
    expect(result.changed).toBe(false);
    expect(result.markdown).toContain("No overlapping DxMessaging scenarios");
  });
});

describe("render-perf-deltas CLI", () => {
  let tempDir;

  beforeEach(() => {
    tempDir = makeTempDir("render-deltas");
  });

  afterEach(() => {
    cleanupDir(tempDir);
  });

  function run(args) {
    return childProcess.spawnSync(process.execPath, [SCRIPT, ...args], { encoding: "utf8" });
  }

  test("writes the table to --output and prints changed=false for a quiet run, exit 0", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    const outputPath = path.join(tempDir, "delta.md");
    fs.writeFileSync(inputPath, currentLog(), "utf8");
    fs.writeFileSync(baselinePath, baselineCsv(), "utf8");

    const result = run([
      "--input",
      inputPath,
      "--baseline-csv",
      baselinePath,
      "--unity-version",
      LATEST,
      "--output",
      outputPath
    ]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("changed=false");
    // A quiet run also prints the gate signal regressed=false.
    expect(result.stdout).toContain("regressed=false");
    const markdown = fs.readFileSync(outputPath, "utf8");
    expect(markdown).toContain("| Scenario");
    expect(markdown).toContain("Untargeted Flood (One Handler)");
    expect(markdown).not.toContain("MessagePipe");
  });

  test("prints changed=true on a regression and writes the regressed row, exit 0", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    const outputPath = path.join(tempDir, "delta.md");
    fs.writeFileSync(
      inputPath,
      currentLog({ mutate: { UntargetedFlood_OneHandler: { emits: 12000000 } } }),
      "utf8"
    );
    fs.writeFileSync(baselinePath, baselineCsv(), "utf8");

    const result = run([
      "--input",
      inputPath,
      "--baseline-csv",
      baselinePath,
      "--unity-version",
      LATEST,
      "--output",
      outputPath
    ]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("changed=true");
    // A -52% drop trips the gate too: the workflow fails the job off this line.
    expect(result.stdout).toContain("regressed=true");
    expect(fs.readFileSync(outputPath, "utf8")).toContain("-52.00%");
  });

  test("prints regressed=false (but changed=true) for a movement within the gate threshold", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    const outputPath = path.join(tempDir, "delta.md");
    // 25,000,000 -> 20,000,000 is -20%: past --tolerance but within the 33% gate.
    fs.writeFileSync(
      inputPath,
      currentLog({ mutate: { UntargetedFlood_OneHandler: { emits: 20000000 } } }),
      "utf8"
    );
    fs.writeFileSync(baselinePath, baselineCsv(), "utf8");

    const result = run([
      "--input",
      inputPath,
      "--baseline-csv",
      baselinePath,
      "--unity-version",
      LATEST,
      "--output",
      outputPath
    ]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("changed=true");
    expect(result.stdout).toContain("regressed=false");
  });

  test("--regression-threshold tightens the gate so a within-default drop regresses, exit 0", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    const outputPath = path.join(tempDir, "delta.md");
    // A 20% drop trips a 0.10 gate (but not the 0.33 default).
    fs.writeFileSync(
      inputPath,
      currentLog({ mutate: { UntargetedFlood_OneHandler: { emits: 20000000 } } }),
      "utf8"
    );
    fs.writeFileSync(baselinePath, baselineCsv(), "utf8");

    const result = run([
      "--input",
      inputPath,
      "--baseline-csv",
      baselinePath,
      "--unity-version",
      LATEST,
      "--regression-threshold",
      "0.10",
      "--output",
      outputPath
    ]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("regressed=true");
  });

  test("prints changed=false + regressed=true for a tiny allocation increase, exit 0", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    const outputPath = path.join(tempDir, "delta.md");
    fs.writeFileSync(
      inputPath,
      currentLog({ mutate: { Comparison_DxMessaging_GlobalToOne: { alloc: 1001 } } }),
      "utf8"
    );
    fs.writeFileSync(
      baselinePath,
      baselineCsv({ mutate: { Comparison_DxMessaging_GlobalToOne: { alloc: 1000 } } }),
      "utf8"
    );

    const result = run([
      "--input",
      inputPath,
      "--baseline-csv",
      baselinePath,
      "--unity-version",
      LATEST,
      "--output",
      outputPath
    ]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("changed=false");
    expect(result.stdout).toContain("regressed=true");
    expect(fs.readFileSync(outputPath, "utf8")).toContain("+1 B");
  });

  test("missing baseline CSV writes the note, prints changed=false + regressed=false, exits 0", () => {
    const inputPath = path.join(tempDir, "current.log");
    const outputPath = path.join(tempDir, "delta.md");
    fs.writeFileSync(inputPath, currentLog(), "utf8");

    const result = run([
      "--input",
      inputPath,
      "--baseline-csv",
      path.join(tempDir, "nope.csv"),
      "--unity-version",
      LATEST,
      "--output",
      outputPath
    ]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("changed=false");
    expect(result.stdout).toContain("regressed=false");
    expect(fs.readFileSync(outputPath, "utf8")).toContain("No baseline on master yet");
  });

  test("a header-only baseline CSV degrades gracefully at the CLI (note + both flags false)", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    const outputPath = path.join(tempDir, "delta.md");
    fs.writeFileSync(inputPath, currentLog(), "utf8");
    fs.writeFileSync(baselinePath, `${CSV_HEADER}\n`, "utf8");

    const result = run([
      "--input",
      inputPath,
      "--baseline-csv",
      baselinePath,
      "--unity-version",
      LATEST,
      "--output",
      outputPath
    ]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("changed=false");
    expect(result.stdout).toContain("regressed=false");
    expect(fs.readFileSync(outputPath, "utf8")).toContain("No baseline on master yet");
  });

  test("--scope selects the execution scope used for matching", () => {
    const inputPath = path.join(tempDir, "current.log");
    const baselinePath = path.join(tempDir, "baseline.csv");
    const outputPath = path.join(tempDir, "delta.md");
    // Current + baseline have Standalone rows; ask for Standalone explicitly.
    fs.writeFileSync(inputPath, currentLog({ scope: "Standalone" }), "utf8");
    fs.writeFileSync(baselinePath, baselineCsv({ scope: "Standalone" }), "utf8");

    const result = run([
      "--input",
      inputPath,
      "--baseline-csv",
      baselinePath,
      "--unity-version",
      LATEST,
      "--scope",
      "Standalone",
      "--output",
      outputPath
    ]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("changed=false");
    expect(fs.readFileSync(outputPath, "utf8")).toContain("Untargeted Flood (One Handler)");
  });

  test("--help prints usage and exits 0", () => {
    const result = run(["--help"]);
    expect(result.status).toBe(0);
    expect(result.stdout).toContain("Usage: node scripts/unity/render-perf-deltas.js");
    expect(result.stdout).toContain("changed=true");
    // The usage documents the new gate flag + signal.
    expect(result.stdout).toContain("--regression-threshold");
    expect(result.stdout).toContain("regressed=true|false");
  });
});
