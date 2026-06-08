"use strict";

const fs = require("fs");
const path = require("path");

const {
  SCENARIO_ORDER,
  DISPATCH_DISPLAY_NAMES,
  COMPARISON_SCENARIO_ORDER,
  COMPARISON_SCENARIO_LABELS,
  COMPARISON_TECH_ORDER,
  COMPARISON_TECH_LABELS
} = require("../unity/render-perf-doc.js");
const { SCENARIOS } = require("../unity/extract-perf-baseline.js");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const BENCHMARK_PROTOCOL_PATH = path.join(
  REPO_ROOT,
  "Tests",
  "Runtime",
  "Benchmarks",
  "BenchmarkProtocol.cs"
);
const DISPATCH_BENCHMARK_PATH = path.join(
  REPO_ROOT,
  "Tests",
  "Runtime",
  "Benchmarks",
  "DispatchThroughputBenchmarks.cs"
);
const PERF_REGRESSION_SMOKE_PATH = path.join(
  REPO_ROOT,
  "Tests",
  "Editor",
  "Benchmarks",
  "PerfRegressionSmokeTests.cs"
);
const COMPARISON_SCENARIO_PATH = path.join(
  REPO_ROOT,
  "Tests",
  "Runtime",
  "Comparisons",
  "ComparisonScenario.cs"
);
const COMPARISONS_DIR = path.join(REPO_ROOT, "Tests", "Runtime", "Comparisons");

function readFile(filePath) {
  expect(fs.existsSync(filePath)).toBe(true);
  return fs.readFileSync(filePath, "utf8");
}

// Extract the body of a `public static string <name>(...) { ... }` switch method by
// brace-matching from its opening brace. Tolerant of arbitrary whitespace/newlines.
function extractStringSwitchBody(source, methodName) {
  const signature = new RegExp(`public\\s+static\\s+string\\s+${methodName}\\s*\\([^)]*\\)\\s*\\{`);
  const match = signature.exec(source);
  if (!match) {
    throw new Error(`Could not find method '${methodName}' in source.`);
  }
  let index = match.index + match[0].length;
  let depth = 1;
  for (; index < source.length && depth > 0; index++) {
    const char = source[index];
    if (char === "{") {
      depth++;
    } else if (char === "}") {
      depth--;
    }
  }
  return source.slice(match.index + match[0].length, index - 1);
}

// Parse every `Enum.Member => "literal"` switch arm from a method body, tolerating
// newlines between the arrow and the literal (CSharpier wraps long arms).
function parseSwitchArms(body) {
  const arms = [];
  const armRegex = /([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)\s*=>\s*"([^"]*)"/g;
  let arm;
  while ((arm = armRegex.exec(body)) !== null) {
    arms.push({ member: arm[2], literal: arm[3] });
  }
  return arms;
}

function membersOf(arms) {
  return new Set(arms.map((arm) => arm.member));
}

function literalsOf(arms) {
  return arms.map((arm) => arm.literal);
}

// Build a Map<enumMember, literal> from parsed switch arms (last arm wins, but
// each enum member appears once in these source switches).
function armsByMember(arms) {
  const map = new Map();
  for (const arm of arms) {
    map.set(arm.member, arm.literal);
  }
  return map;
}

// Join a Key switch and a DisplayName switch (both keyed by enum member) into a
// Map<stableKey, displayName>. Every member present in keyArms must also have a
// DisplayName arm (a separate test already asserts the member sets match), so a
// missing display arm surfaces here as an undefined value mismatch.
function displayNamesByKey(keyArms, displayArms) {
  const displayByMember = armsByMember(displayArms);
  const byKey = new Map();
  for (const { member, literal: key } of keyArms) {
    byKey.set(key, displayByMember.get(member));
  }
  return byKey;
}

// Assert two key->value maps carry identical values for the same keys. Failure
// names the first mismatching key and both differing values so drift is obvious.
function expectValueMapsEqual(actualByKey, expectedByKey, label) {
  const keys = new Set([...actualByKey.keys(), ...Object.keys(expectedByKey)]);
  const mismatches = [];
  for (const key of keys) {
    const actual = actualByKey.get(key);
    const expected = expectedByKey[key];
    if (actual !== expected) {
      mismatches.push({ key, csharp: actual, js: expected });
    }
  }
  expect({ label, mismatches }).toEqual({ label, mismatches: [] });
}

// Recursively collect every `.cs` file under a directory.
function collectCsFiles(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...collectCsFiles(fullPath));
    } else if (entry.name.endsWith(".cs")) {
      files.push(fullPath);
    }
  }
  return files;
}

function toRepoPath(filePath) {
  return path.relative(REPO_ROOT, filePath).replace(/\\/g, "/");
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function extractVoidMethodBody(source, methodName) {
  const signature = new RegExp(
    `\\b(?:public|private|protected|internal)\\s+void\\s+${methodName}\\s*\\(\\s*\\)\\s*\\{`
  );
  const match = signature.exec(source);
  if (!match) {
    return null;
  }

  let depth = 1;
  let index = match.index + match[0].length;
  for (; index < source.length && depth > 0; index++) {
    const char = source[index];
    if (char === "{") {
      depth++;
    } else if (char === "}") {
      depth--;
    }
  }

  return depth === 0 ? source.slice(match.index + match[0].length, index - 1) : null;
}

function findCachedChurnHandlerFields(source) {
  const fields = [];
  const fieldRegex =
    /\bprivate\s+(?!readonly\b)(?:[A-Za-z_][A-Za-z0-9_.]*(?:\s*<[^;\r\n]+>)?)\s+(_[A-Za-z0-9_]*[Cc]hurnHandler[A-Za-z0-9_]*)\s*;/g;
  let match;
  while ((match = fieldRegex.exec(source)) !== null) {
    fields.push(match[1]);
  }
  return fields;
}

function disposeClearsField(disposeBody, fieldName) {
  return new RegExp(`\\b${escapeRegex(fieldName)}\\s*=\\s*null\\s*;`).test(disposeBody);
}

function setsEqual(actual, expected) {
  const actualSet = new Set(actual);
  const expectedSet = new Set(expected);
  const missing = [...expectedSet].filter((value) => !actualSet.has(value));
  const extra = [...actualSet].filter((value) => !expectedSet.has(value));
  return { equal: missing.length === 0 && extra.length === 0, missing, extra };
}

function expectSetsEqual(actual, expected, label) {
  const { equal, missing, extra } = setsEqual(actual, expected);
  expect({
    label,
    missing,
    extra,
    equal
  }).toEqual({ label, missing: [], extra: [], equal: true });
}

function findParameterlessScenarioWrapperMethods(source, scenarioMembers) {
  const memberAlternation = scenarioMembers.map(escapeRegex).join("|");
  const methodRegex = new RegExp(`\\bpublic\\s+void\\s+(${memberAlternation})\\s*\\(\\s*\\)`, "g");
  const methods = [];
  let match;
  while ((match = methodRegex.exec(source)) !== null) {
    methods.push(match[1]);
  }
  return methods;
}

describe("dispatch + comparison methodology cross-language contract", () => {
  let benchmarkProtocol;
  let dispatchBenchmark;
  let perfRegressionSmoke;
  let comparisonScenario;
  let dispatchKeyArms;
  let dispatchDisplayArms;
  let comparisonKeyArms;
  let comparisonDisplayArms;
  let techKeys;
  let techNameByKey;

  beforeAll(() => {
    benchmarkProtocol = readFile(BENCHMARK_PROTOCOL_PATH);
    dispatchBenchmark = readFile(DISPATCH_BENCHMARK_PATH);
    perfRegressionSmoke = readFile(PERF_REGRESSION_SMOKE_PATH);
    comparisonScenario = readFile(COMPARISON_SCENARIO_PATH);

    dispatchKeyArms = parseSwitchArms(extractStringSwitchBody(benchmarkProtocol, "Key"));
    dispatchDisplayArms = parseSwitchArms(
      extractStringSwitchBody(benchmarkProtocol, "DisplayName")
    );
    comparisonKeyArms = parseSwitchArms(extractStringSwitchBody(comparisonScenario, "Key"));
    comparisonDisplayArms = parseSwitchArms(
      extractStringSwitchBody(comparisonScenario, "DisplayName")
    );

    // Each bridge declares its stable `TechKey` and human `TechName` as
    // expression-bodied properties in the SAME file. Pair them per file so the
    // value-parity test can compare each bridge's TechName against the published
    // JS label keyed by that bridge's TechKey. Whitespace-tolerant so it survives
    // CSharpier wrapping.
    techNameByKey = new Map();
    const techKeyRegex = /TechKey\s*=>\s*"([^"]*)"/;
    const techNameRegex = /TechName\s*=>\s*"([^"]*)"/;
    for (const file of collectCsFiles(COMPARISONS_DIR)) {
      const content = fs.readFileSync(file, "utf8");
      const keyMatch = techKeyRegex.exec(content);
      const nameMatch = techNameRegex.exec(content);
      if (keyMatch && nameMatch) {
        techNameByKey.set(keyMatch[1], nameMatch[1]);
      }
    }
    techKeys = [...techNameByKey.keys()];
  });

  // The warm/hot scenarios must never median-resample sub-windows; they measure one
  // continuous window via BenchmarkProtocol.Measure. The dispatch file does legitimately
  // use the median, but ONLY inside BenchmarkProtocol.MeasureColdLatency for the cold
  // single-shot reducer (median over distinct closed types) -- never for warm/hot
  // resampling. These assertions ban the old median-of-runs resampling helpers by
  // name and require the shared Measure entry point.
  test("DispatchThroughputBenchmarks uses the shared protocol with no warm/hot median resampling", () => {
    expect(dispatchBenchmark).not.toContain("MedianRuns");
    expect(dispatchBenchmark).not.toContain("MedianByPrimaryMetric");
    expect(dispatchBenchmark).not.toContain("AsMedian");
    expect(dispatchBenchmark).toContain("BenchmarkProtocol.Measure");
  });

  test("benchmark execution fixtures are data-driven over DispatchBenchmarkScenarios.All", () => {
    const scenarioMembers = dispatchKeyArms.map((arm) => arm.member);
    expect(scenarioMembers).toHaveLength(13);

    expect(dispatchBenchmark).toContain("[TestCaseSource(nameof(DispatchBenchmarkCases))]");
    expect(dispatchBenchmark).toContain(
      "public void DispatchBenchmark(DispatchBenchmarkScenario scenario)"
    );
    expect(dispatchBenchmark).toContain(
      "foreach (DispatchBenchmarkScenario scenario in DispatchBenchmarkScenarios.All)"
    );
    expect(dispatchBenchmark).toContain("SetName(scenario.ToString())");

    expect(perfRegressionSmoke).toContain("[TestCaseSource(nameof(PerfGateCases))]");
    expect(perfRegressionSmoke).toContain(
      "public void PerfRegressionGate(DispatchBenchmarkScenario scenario)"
    );
    expect(perfRegressionSmoke).toContain(
      "foreach (DispatchBenchmarkScenario scenario in DispatchBenchmarkScenarios.All)"
    );
    expect(perfRegressionSmoke).toContain("SetName(scenario.ToString())");

    expect({
      dispatchBenchmark: findParameterlessScenarioWrapperMethods(
        dispatchBenchmark,
        scenarioMembers
      ),
      perfRegressionSmoke: findParameterlessScenarioWrapperMethods(
        perfRegressionSmoke,
        scenarioMembers
      )
    }).toEqual({ dispatchBenchmark: [], perfRegressionSmoke: [] });
  });

  test("dispatch Key literals match SCENARIOS and SCENARIO_ORDER (as sets)", () => {
    const cSharpKeys = literalsOf(dispatchKeyArms);
    expect(cSharpKeys).toHaveLength(13);

    expectSetsEqual(cSharpKeys, [...SCENARIOS], "dispatch Key vs extract-perf-baseline SCENARIOS");
    expectSetsEqual(cSharpKeys, SCENARIO_ORDER, "dispatch Key vs render-perf-doc SCENARIO_ORDER");
    expect(SCENARIO_ORDER).toHaveLength(13);
  });

  test("dispatch DisplayName enum arms cover exactly the Key enum arms", () => {
    const keyMembers = membersOf(dispatchKeyArms);
    const displayMembers = membersOf(dispatchDisplayArms);
    expectSetsEqual(
      [...displayMembers],
      [...keyMembers],
      "dispatch DisplayName members vs Key members"
    );
  });

  test("DISPATCH_DISPLAY_NAMES keys match the C# dispatch Key set", () => {
    const cSharpKeys = literalsOf(dispatchKeyArms);
    expectSetsEqual(
      Object.keys(DISPATCH_DISPLAY_NAMES),
      cSharpKeys,
      "render-perf-doc DISPATCH_DISPLAY_NAMES keys vs C# dispatch Key set"
    );
  });

  test("comparison Key literals match COMPARISON_SCENARIO_ORDER and COMPARISON_SCENARIO_LABELS keys", () => {
    const cSharpKeys = literalsOf(comparisonKeyArms);
    expect(cSharpKeys).toHaveLength(8);

    expectSetsEqual(
      cSharpKeys,
      COMPARISON_SCENARIO_ORDER,
      "comparison Key vs render-perf-doc COMPARISON_SCENARIO_ORDER"
    );
    expectSetsEqual(
      cSharpKeys,
      Object.keys(COMPARISON_SCENARIO_LABELS),
      "comparison Key vs render-perf-doc COMPARISON_SCENARIO_LABELS keys"
    );
  });

  test("bridge TechKey literals match COMPARISON_TECH_ORDER and COMPARISON_TECH_LABELS keys", () => {
    expect(techKeys).toHaveLength(9);

    expectSetsEqual(
      techKeys,
      COMPARISON_TECH_ORDER,
      "bridge TechKey vs render-perf-doc COMPARISON_TECH_ORDER"
    );
    expectSetsEqual(
      techKeys,
      Object.keys(COMPARISON_TECH_LABELS),
      "bridge TechKey vs render-perf-doc COMPARISON_TECH_LABELS keys"
    );
  });

  // VALUE parity (not just key parity): the human-readable labels published in
  // the JS renderer must match the C# source strings exactly, so a future text
  // tweak on either side cannot silently diverge between docs/PR comments and the
  // C# logs/asserts that reference the same names.
  test("dispatch DisplayName values match DISPATCH_DISPLAY_NAMES (per key)", () => {
    expectValueMapsEqual(
      displayNamesByKey(dispatchKeyArms, dispatchDisplayArms),
      DISPATCH_DISPLAY_NAMES,
      "C# DispatchBenchmarkScenarios.DisplayName vs render-perf-doc DISPATCH_DISPLAY_NAMES"
    );
  });

  test("comparison DisplayName values match COMPARISON_SCENARIO_LABELS (per key)", () => {
    expectValueMapsEqual(
      displayNamesByKey(comparisonKeyArms, comparisonDisplayArms),
      COMPARISON_SCENARIO_LABELS,
      "C# ComparisonScenarios.DisplayName vs render-perf-doc COMPARISON_SCENARIO_LABELS"
    );
  });

  test("bridge TechName values match COMPARISON_TECH_LABELS (keyed by TechKey)", () => {
    expectValueMapsEqual(
      techNameByKey,
      COMPARISON_TECH_LABELS,
      "C# bridge TechName vs render-perf-doc COMPARISON_TECH_LABELS"
    );
  });

  test("cached churn handler fields are cleared by Dispose", () => {
    const checkedFields = [];
    const violations = [];

    for (const filePath of collectCsFiles(COMPARISONS_DIR)) {
      const source = fs.readFileSync(filePath, "utf8");
      const fields = findCachedChurnHandlerFields(source);
      if (fields.length === 0) {
        continue;
      }

      const repoPath = toRepoPath(filePath);
      const disposeBody = extractVoidMethodBody(source, "Dispose");
      for (const fieldName of fields) {
        checkedFields.push(`${repoPath}:${fieldName}`);
        if (!disposeBody) {
          violations.push(`${repoPath}: missing Dispose() for ${fieldName}`);
          continue;
        }
        if (!disposeClearsField(disposeBody, fieldName)) {
          violations.push(`${repoPath}: Dispose() does not clear ${fieldName}`);
        }
      }
    }

    expect(checkedFields).toEqual(
      expect.arrayContaining(["Tests/Runtime/Comparisons/DxMessagingBridge.cs:_churnHandler"])
    );
    expect(violations).toEqual([]);
  });
});
