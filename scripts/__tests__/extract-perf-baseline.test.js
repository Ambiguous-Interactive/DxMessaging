"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

const {
  CSV_HEADER,
  buildCsv,
  extractRows,
  isComparisonScenario,
  isKeptScenario
} = require("../unity/extract-perf-baseline.js");

const SCRIPT = path.resolve(__dirname, "..", "unity", "extract-perf-baseline.js");

describe("extract-perf-baseline", () => {
  test("extracts CSV rows from Unity output and preserves quoted platform fields", () => {
    const content = [
      "Noise before results",
      'UntargetedFlood_OneHandler,"Editor Mono x64 Development (LinuxEditor; Unity 2022.3.45f1)",25a4dcc,-1,25000000.125,0,1000.000',
      "Noise after results"
    ].join("\n");

    const rows = extractRows(content);

    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      scenario: "UntargetedFlood_OneHandler",
      platform: "Editor Mono x64 Development (LinuxEditor; Unity 2022.3.45f1)",
      commit: "25a4dcc",
      runIndex: "-1",
      emitsPerSecond: "25000000.125",
      allocatedBytesDelta: "0",
      wallClockMs: "1000.000"
    });
  });

  test("extracts Unity-prefixed CSV rows using the earliest scenario candidate", () => {
    const content = [
      '[TestRunner] 12:00:00 UntargetedFlood_OneHandler,"Editor PlayMode Mono x64 Release Comparison_DxMessaging_GlobalToOne BroadcastFlood_OneHandler",abc1234,-1,25000000.125,0,1000.000',
      '[TestRunner] 12:00:01 Comparison_DxMessaging_GlobalToOne,"Standalone IL2CPP x64 Release UntargetedFlood_OneHandler",abc1234,-1,16980000.000,0,1000.000'
    ].join("\n");

    const rows = extractRows(content);

    expect(rows.map((row) => row.scenario)).toEqual([
      "UntargetedFlood_OneHandler",
      "Comparison_DxMessaging_GlobalToOne"
    ]);
    expect(rows[0].platform).toContain("Comparison_DxMessaging_GlobalToOne");
    expect(rows[0].platform).toContain("BroadcastFlood_OneHandler");
    expect(rows[1].platform).toContain("UntargetedFlood_OneHandler");
  });

  test("extracts structured Debug.Log rows from prefixed Unity log lines", () => {
    const content =
      '[TestRunner] {scenario:"BroadcastFlood_OneHandler", platform:"Editor Mono x64 Development (LinuxEditor; Unity 2022.3.45f1)", commit:"HEAD", runIndex:-1, emitsPerSec:17000000.5, allocatedBytesDelta:0, wallClockMs:1000.25}';

    expect(buildCsv(extractRows(content))).toBe(
      [
        CSV_HEADER,
        "BroadcastFlood_OneHandler,Editor Mono x64 Development (LinuxEditor; Unity 2022.3.45f1),HEAD,-1,17000000.500,0,1000.250",
        ""
      ].join("\n")
    );
  });

  test("finds scenario starts without allocating a combined candidate array", () => {
    const source = fs.readFileSync(SCRIPT, "utf8");
    const start = source.indexOf("function findScenarioIndex(line)");
    const end = source.indexOf("function parseCsvFields(line)");

    expect(start).toBeGreaterThanOrEqual(0);
    expect(end).toBeGreaterThan(start);

    const body = source.slice(start, end);
    expect(body).toContain("for (const scenario of SCENARIOS)");
    expect(body).toContain("for (const scenario of COMPARISON_SCENARIO_IDS)");
    expect(body).not.toContain("line.indexOf(COMPARISON_SCENARIO_PREFIX)");
    expect(body).not.toMatch(/\[\s*\.\.\.\s*SCENARIOS\b/);
    expect(body).not.toMatch(/Array\.from\(\s*SCENARIOS\b/);
  });

  test("keeps only known Comparison_<TechKey>_<ScenarioKey> rows", () => {
    expect(isComparisonScenario("Comparison_DxMessaging_GlobalToOne")).toBe(true);
    expect(isComparisonScenario("Comparison_MessagePipe_Filtered")).toBe(true);
    expect(isComparisonScenario("Comparison_Garbage_GlobalToOne")).toBe(false);
    expect(isComparisonScenario("Comparison_DxMessaging_NotAScenario")).toBe(false);
    expect(isComparisonScenario("Comparison_DxMessaging_GlobalToOne_extra")).toBe(false);
    expect(isComparisonScenario("Comparison_DxMessaging-GlobalToOne")).toBe(false);

    expect(isKeptScenario("Comparison_DxMessaging_GlobalToOne")).toBe(true);
    expect(isKeptScenario("Comparison_Garbage_GlobalToOne")).toBe(false);
    expect(isKeptScenario("Comparison_DxMessaging_NotAScenario")).toBe(false);
  });

  test("keeps cross-library Comparison_* rows alongside dispatch rows", () => {
    const content = [
      "Noise before results",
      // A dispatch row (must still survive, byte-identical behavior).
      'UntargetedFlood_OneHandler,"Editor PlayMode Mono x64 Release (LinuxEditor; Unity 6000.3.16f1)",abc1234,-1,25000000.125,0,1000.000',
      // A comparison row (NEW: must now be kept).
      'Comparison_DxMessaging_GlobalToOne,"Editor PlayMode Mono x64 Release (LinuxEditor; Unity 6000.3.16f1)",abc1234,-1,16980000.000,0,1000.000',
      // A structured-log comparison row (also kept).
      '[TestRunner] {scenario:"Comparison_MessagePipe_Filtered", platform:"Standalone Mono x64 Release (LinuxEditor; Unity 6000.3.16f1)", commit:"abc1234", runIndex:-1, emitsPerSec:7000000.0, allocatedBytesDelta:0, wallClockMs:1000.0}',
      // A non-benchmark row that must still be ignored.
      "TotallyUnrelated_Row,whatever,deadbee,-1,1.0,0,1.0",
      "Noise after results"
    ].join("\n");

    const rows = extractRows(content);

    const scenarios = rows.map((row) => row.scenario);
    expect(scenarios).toContain("UntargetedFlood_OneHandler");
    expect(scenarios).toContain("Comparison_DxMessaging_GlobalToOne");
    expect(scenarios).toContain("Comparison_MessagePipe_Filtered");
    expect(scenarios).not.toContain("TotallyUnrelated_Row");
    expect(rows).toHaveLength(3);

    const comparison = rows.find((row) => row.scenario === "Comparison_DxMessaging_GlobalToOne");
    expect(comparison).toMatchObject({
      platform: "Editor PlayMode Mono x64 Release (LinuxEditor; Unity 6000.3.16f1)",
      commit: "abc1234",
      emitsPerSecond: "16980000.000",
      allocatedBytesDelta: "0",
      wallClockMs: "1000.000"
    });
  });

  test("ignores bogus Comparison_* rows and does not strip a valid row at a bogus prefix", () => {
    const content = [
      "Noise before results",
      "Comparison_Garbage_GlobalToOne,Editor PlayMode Mono x64 Release (LinuxEditor; Unity 6000.3.16f1),abc1234,-1,1.000,0,1.000",
      "Comparison_DxMessaging_NotAScenario,Editor PlayMode Mono x64 Release (LinuxEditor; Unity 6000.3.16f1),abc1234,-1,1.000,0,1.000",
      '[TestRunner] Comparison_Garbage_GlobalToOne prefix UntargetedFlood_OneHandler,"Editor PlayMode Mono x64 Release (LinuxEditor; Unity 6000.3.16f1)",abc1234,-1,25000000.125,0,1000.000',
      '[TestRunner] {scenario:"Comparison_Garbage_GlobalToOne", platform:"Standalone Mono x64 Release (LinuxEditor; Unity 6000.3.16f1)", commit:"abc1234", runIndex:-1, emitsPerSec:1.0, allocatedBytesDelta:0, wallClockMs:1.0}',
      "Noise after results"
    ].join("\n");

    const rows = extractRows(content);

    expect(rows).toHaveLength(1);
    expect(rows[0]).toMatchObject({
      scenario: "UntargetedFlood_OneHandler",
      platform: "Editor PlayMode Mono x64 Release (LinuxEditor; Unity 6000.3.16f1)",
      commit: "abc1234",
      emitsPerSecond: "25000000.125",
      allocatedBytesDelta: "0",
      wallClockMs: "1000.000"
    });
  });

  test("keeps the cold first-dispatch and warm-JIT flood rows in the keep-set", () => {
    const content = [
      "Noise before results",
      // A cold first-dispatch row (latency: emits=0, time in wallClockMs).
      'UntargetedFirstDispatch_Cold,"Editor PlayMode Mono x64 Release (LinuxEditor; Unity 6000.3.16f1)",abc1234,-1,0.000,128,0.250',
      // The warm-JIT registration flood row (latency).
      'RegistrationFlood_1000Types_WarmJit,"Editor PlayMode Mono x64 Release (LinuxEditor; Unity 6000.3.16f1)",abc1234,-1,0.000,4096,8.500',
      // A structured-log cold targeted row (also kept).
      '[TestRunner] {scenario:"TargetedFirstDispatch_Cold", platform:"Standalone IL2CPP x64 Release (LinuxEditor; Unity 6000.3.16f1)", commit:"abc1234", runIndex:-1, emitsPerSec:0.0, allocatedBytesDelta:64, wallClockMs:0.01}',
      "Noise after results"
    ].join("\n");

    const rows = extractRows(content);
    const scenarios = rows.map((row) => row.scenario);
    expect(scenarios).toContain("UntargetedFirstDispatch_Cold");
    expect(scenarios).toContain("RegistrationFlood_1000Types_WarmJit");
    expect(scenarios).toContain("TargetedFirstDispatch_Cold");
    expect(rows).toHaveLength(3);

    const cold = rows.find((row) => row.scenario === "UntargetedFirstDispatch_Cold");
    expect(cold).toMatchObject({
      emitsPerSecond: "0.000",
      allocatedBytesDelta: "128",
      wallClockMs: "0.250"
    });
  });

  test("appends rows to an existing baseline without duplicating the header", () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-perf-"));
    const inputPath = path.join(tempDir, "unity.log");
    const outputPath = path.join(tempDir, "perf-baseline.csv");
    fs.writeFileSync(`${outputPath}`, `${CSV_HEADER}\n`, "utf8");
    fs.writeFileSync(
      inputPath,
      'TargetedFlood_OneListener,"Editor Mono x64 Development (LinuxEditor; Unity 2022.3.45f1)",29a5338,-1,18000000.000,0,1000.000\n',
      "utf8"
    );

    const result = childProcess.spawnSync(
      process.execPath,
      [SCRIPT, "--input", inputPath, "--output", outputPath, "--append"],
      { encoding: "utf8" }
    );

    expect(result.status).toBe(0);
    expect(fs.readFileSync(outputPath, "utf8")).toBe(
      [
        CSV_HEADER,
        "TargetedFlood_OneListener,Editor Mono x64 Development (LinuxEditor; Unity 2022.3.45f1),29a5338,-1,18000000.000,0,1000.000",
        ""
      ].join("\n")
    );
  });
});
