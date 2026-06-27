"use strict";

const fs = require("fs");

const { extractRows } = require("./extract-perf-baseline.js");
const {
  SCENARIO_ORDER,
  DISPATCH_DISPLAY_NAMES,
  COMPARISON_SCENARIO_ORDER,
  COMPARISON_SCENARIO_LABELS,
  buildComparisonScenarioId,
  parseComparisonScenario
} = require("./perf-scenarios.js");
const { deriveScope, alignTable } = require("./render-perf-doc.js");

// Wall-clock scenarios carry emitsPerSecond=0, so the delta table compares
// wallClockMs and the regression gate skips them.
const REGISTRATION_SCENARIOS = new Set([
  "RegistrationFlood_1000Types_FromColdBus",
  "RegistrationFlood_1000Types_WarmJit",
  "DeregistrationFlood_1000Types_Cold",
  "DeregistrationFlood_1000Types_WarmJit",
  "UntargetedFirstDispatch_Cold",
  "TargetedFirstDispatch_Cold",
  "BroadcastFirstDispatch_Cold"
]);

const DXMESSAGING_TECH_KEY = "DxMessaging";

const DEFAULT_TOLERANCE = 0.02;
const DEFAULT_SCOPE = "Standalone";

const DEFAULT_REGRESSION_THRESHOLD = 0.33;

function usage() {
  return `Usage: node scripts/unity/render-perf-deltas.js --input <log-or-xml> [--input <path> ...] --baseline-csv <path> --unity-version <version> [--tolerance <fraction>] [--regression-threshold <fraction>] [--scope <name>] --output <markdown>

Compares current DxMessaging perf rows with the committed baseline CSV and prints
changed=true|false plus regressed=true|false. The script always exits 0; the
workflow decides whether the regressed= signal fails CI after posting diagnostics.

Defaults: --tolerance ${DEFAULT_TOLERANCE}, --regression-threshold ${DEFAULT_REGRESSION_THRESHOLD}, --scope ${DEFAULT_SCOPE}.
Missing/header-only baselines emit changed=false plus regressed=false.
`;
}

function parseArgs(argv) {
  const options = {
    inputs: [],
    baselineCsv: "",
    unityVersion: "",
    tolerance: DEFAULT_TOLERANCE,
    regressionThreshold: DEFAULT_REGRESSION_THRESHOLD,
    scope: DEFAULT_SCOPE,
    output: "",
    help: false
  };

  for (let index = 2; index < argv.length; index++) {
    const arg = argv[index];
    switch (arg) {
      case "--input":
        options.inputs.push(requireValue(argv, ++index, arg));
        break;
      case "--baseline-csv":
        options.baselineCsv = requireValue(argv, ++index, arg);
        break;
      case "--unity-version":
        options.unityVersion = requireValue(argv, ++index, arg);
        break;
      case "--tolerance":
        options.tolerance = parseNonNegative(requireValue(argv, ++index, arg), arg);
        break;
      case "--regression-threshold":
        options.regressionThreshold = parseNonNegative(requireValue(argv, ++index, arg), arg);
        break;
      case "--scope":
        options.scope = requireValue(argv, ++index, arg);
        break;
      case "--output":
        options.output = requireValue(argv, ++index, arg);
        break;
      case "--help":
      case "-h":
        options.help = true;
        break;
      default:
        throw new Error(`Unknown argument: ${arg}`);
    }
  }

  return options;
}

function requireValue(argv, index, flag) {
  const value = argv[index];
  if (value === undefined || value.startsWith("--")) {
    throw new Error(`${flag} requires a value.`);
  }
  return value;
}

function parseNonNegative(value, flag) {
  const parsed = Number.parseFloat(value);
  if (!Number.isFinite(parsed) || parsed < 0) {
    throw new Error(`${flag} must be a non-negative number, got: ${value}`);
  }
  return parsed;
}

function readInputs(inputs) {
  if (inputs.length === 0) {
    throw new Error("At least one --input path is required.");
  }
  return inputs
    .map((inputPath) => {
      if (!fs.existsSync(inputPath)) {
        throw new Error(`Input file not found: ${inputPath}`);
      }
      return fs.readFileSync(inputPath, "utf8");
    })
    .join("\n");
}

function isDxMessagingRow(scenario) {
  if (SCENARIO_ORDER.includes(scenario)) {
    return true;
  }
  const parsed = parseComparisonScenario(scenario);
  return parsed !== null && parsed.techKey === DXMESSAGING_TECH_KEY;
}

// Match on scenario + derived scope, optionally limited to one Unity version.
// First row per scenario wins so output stays deterministic.
function indexDxMessagingRows(rows, scope, platformSubstring = "") {
  const byScenario = new Map();
  for (const row of rows) {
    if (platformSubstring && !row.platform.includes(platformSubstring)) {
      continue;
    }
    if (deriveScope(row.platform) !== scope) {
      continue;
    }
    if (!isDxMessagingRow(row.scenario)) {
      continue;
    }
    if (!byScenario.has(row.scenario)) {
      byScenario.set(row.scenario, row);
    }
  }
  return byScenario;
}

function deltaScenarioOrder() {
  return [
    ...SCENARIO_ORDER,
    ...COMPARISON_SCENARIO_ORDER.map((key) => buildComparisonScenarioId(DXMESSAGING_TECH_KEY, key))
  ];
}

function scenarioLabel(scenario) {
  if (DISPATCH_DISPLAY_NAMES[scenario]) {
    return DISPATCH_DISPLAY_NAMES[scenario];
  }
  const parsed = parseComparisonScenario(scenario);
  if (parsed && parsed.techKey === DXMESSAGING_TECH_KEY) {
    return COMPARISON_SCENARIO_LABELS[parsed.scenarioKey] || scenario;
  }
  return scenario;
}

function formatThroughput(emitsPerSecond) {
  const millions = Number.parseFloat(emitsPerSecond) / 1_000_000;
  return `${millions.toFixed(2)} M emits/sec`;
}

function formatWallClock(wallClockMs) {
  return `${Number.parseFloat(wallClockMs).toFixed(3)} ms`;
}

// Allocation is a COUNT of GC allocations over one batch (see AllocationProbe).
// -1 is the "Unmeasured" sentinel (no reliable probe on that backend): "n/a",
// never "0". A delta/regression is only computed when BOTH sides are real (>= 0).
function formatAllocations(gcAllocations) {
  const count = Number.parseInt(gcAllocations, 10);
  if (!Number.isFinite(count) || count < 0) {
    return "n/a";
  }
  return count.toLocaleString("en-US");
}

function allocationsComparable(currentAllocs, baselineAllocs) {
  return currentAllocs >= 0 && baselineAllocs >= 0;
}

// Allocated BYTES over one batch (informational companion to the count; fewer is
// better). -1 is the "Unmeasured" sentinel: "n/a", never "0". A byte delta is only
// rendered when BOTH sides are real (>= 0), the same gate as allocationsComparable.
function formatBytesAllocated(gcAllocatedBytes) {
  const bytes = Number.parseInt(gcAllocatedBytes, 10);
  if (!Number.isFinite(bytes) || bytes < 0) {
    return "n/a";
  }
  return bytes.toLocaleString("en-US");
}

function bytesComparable(currentBytes, baselineBytes) {
  return currentBytes >= 0 && baselineBytes >= 0;
}

// Renders a goodness-signed fraction: the value passed in is already normalized so
// POSITIVE means BETTER and NEGATIVE means WORSE (see goodnessRelativeChange), so a
// "+" here is always an improvement and a "-" is always a regression, for every metric.
function formatPct(fraction) {
  if (!Number.isFinite(fraction)) {
    return "n/a";
  }
  const rounded = (fraction * 100).toFixed(2);
  return Number.parseFloat(rounded) === 0 ? "0.00%" : `${fraction > 0 ? "+" : ""}${rounded}%`;
}

// Allocation delta in goodness terms: fewer allocations is better. The direction is
// spelled out ("fewer"/"more") rather than encoded as a bare +/- on the count -- a
// signed count next to a column the reader can see move reads ambiguously, while the
// primary-metric percentage carries the +/- goodness sign. So "fewer" is always the
// improvement and "more" is always the regression.
function formatAllocDelta(delta) {
  if (delta === 0) {
    return "0 allocs";
  }
  const magnitude = Math.abs(delta).toLocaleString("en-US");
  return delta < 0 ? `${magnitude} fewer allocs` : `${magnitude} more allocs`;
}

// Allocated-bytes delta in goodness terms (fewer bytes is better), worded the same
// way as the allocation-count delta so "fewer" is always the improvement and "more"
// is always the regression. Informational only -- bytes never gate (see isRegression).
function formatBytesDelta(delta) {
  if (delta === 0) {
    return "0 bytes";
  }
  const magnitude = Math.abs(delta).toLocaleString("en-US");
  return delta < 0 ? `${magnitude} fewer bytes` : `${magnitude} more bytes`;
}

function relativeChange(current, baseline) {
  return baseline === 0 ? (current === 0 ? 0 : Infinity) : (current - baseline) / baseline;
}

// Goodness-signed relative change: POSITIVE always means BETTER, NEGATIVE always means
// WORSE, so the rendered "+"/"-" is read identically for every metric. Throughput is
// higher-is-better (the raw change); wall clock / latency is lower-is-better (the raw
// change negated), so a faster (lower-ms) run renders as a "+" gain rather than the
// "-" a naive numeric delta would show.
function goodnessRelativeChange(current, baseline, higherIsBetter) {
  const raw = relativeChange(current, baseline);
  return higherIsBetter ? raw : -raw;
}

function compareRow(scenario, current, baseline, tolerance) {
  const label = scenarioLabel(scenario);
  const currentAllocs = Number.parseInt(current.gcAllocations, 10);
  const baselineAllocs = Number.parseInt(baseline.gcAllocations, 10);
  // Only meaningful when both sides were measured; otherwise cells render "n/a".
  const allocComparable = allocationsComparable(currentAllocs, baselineAllocs);
  const allocDelta = allocComparable ? currentAllocs - baselineAllocs : 0;
  const allocMoved = allocComparable
    ? baselineAllocs === 0
      ? allocDelta !== 0
      : Math.abs(allocDelta / baselineAllocs) > tolerance
    : false;

  // Bytes mirror the count exactly but are informational only -- they never gate
  // (see isRegression) and never mark the row moved; only the byte delta renders.
  const currentBytes = Number.parseInt(current.gcAllocatedBytes, 10);
  const baselineBytes = Number.parseInt(baseline.gcAllocatedBytes, 10);
  const bytesAreComparable = bytesComparable(currentBytes, baselineBytes);
  const bytesDelta = bytesAreComparable ? currentBytes - baselineBytes : 0;

  // Each cell is "primary[, allocs][, bytes]". An unmeasured metric (the sentinel on
  // BOTH sides -- e.g. the profiler-stripped Standalone IL2CPP leg) is OMITTED, not
  // padded with a useless ", n/a"; allocComparable/bytesAreComparable gate it.
  const baselineParts = [];
  const currentParts = [];

  let primaryPct;
  let primaryMoved;

  if (REGISTRATION_SCENARIOS.has(scenario)) {
    const baselineMs = Number.parseFloat(baseline.wallClockMs);
    const currentMs = Number.parseFloat(current.wallClockMs);
    // Wall clock / latency: lower is better, so a faster run renders as a "+" gain.
    primaryPct = goodnessRelativeChange(currentMs, baselineMs, false);
    primaryMoved = Math.abs(primaryPct) > tolerance;
    baselineParts.push(formatWallClock(baseline.wallClockMs));
    currentParts.push(formatWallClock(current.wallClockMs));
  } else {
    const baselineEmits = Number.parseFloat(baseline.emitsPerSecond);
    const currentEmits = Number.parseFloat(current.emitsPerSecond);
    // Throughput: higher is better, so more emits/sec renders as a "+" gain.
    primaryPct = goodnessRelativeChange(currentEmits, baselineEmits, true);
    primaryMoved = Math.abs(primaryPct) > tolerance;
    baselineParts.push(formatThroughput(baseline.emitsPerSecond));
    currentParts.push(formatThroughput(current.emitsPerSecond));
  }

  if (allocComparable) {
    baselineParts.push(formatAllocations(baseline.gcAllocations));
    currentParts.push(formatAllocations(current.gcAllocations));
  }
  if (bytesAreComparable) {
    baselineParts.push(formatBytesAllocated(baseline.gcAllocatedBytes));
    currentParts.push(formatBytesAllocated(current.gcAllocatedBytes));
  }
  const baselineCell = baselineParts.join(", ");
  const currentCell = currentParts.join(", ");

  const deltaParts = [formatPct(primaryPct)];
  if (allocComparable && allocDelta !== 0) {
    deltaParts.push(formatAllocDelta(allocDelta));
  }
  if (bytesAreComparable && bytesDelta !== 0) {
    deltaParts.push(formatBytesDelta(bytesDelta));
  }
  const deltaCell = deltaParts.join(", ");

  return {
    cells: [label, baselineCell, currentCell, deltaCell],
    moved: primaryMoved || allocMoved
  };
}

function buildDeltaTable(currentByScenario, baselineByScenario, tolerance, scope) {
  const header = ["Scenario", "Baseline", "Current", "Delta"];
  const dataRows = [];
  let changed = false;

  for (const scenario of deltaScenarioOrder()) {
    const current = currentByScenario.get(scenario);
    const baseline = baselineByScenario.get(scenario);
    if (!current || !baseline) {
      continue;
    }
    const comparison = compareRow(scenario, current, baseline, tolerance);
    dataRows.push(comparison.cells);
    changed ||= comparison.moved;
  }

  if (dataRows.length === 0) {
    return {
      markdown: `No overlapping DxMessaging scenarios for scope **${scope}** in both the current run and the baseline.`,
      changed: false
    };
  }

  return { markdown: alignTable([header, ...dataRows]), changed };
}

function isRegression(current, baseline, regressionThreshold) {
  const baselineEmits = Number.parseFloat(baseline.emitsPerSecond);
  const currentEmits = Number.parseFloat(current.emitsPerSecond);
  if (!Number.isFinite(baselineEmits) || baselineEmits <= 0) {
    return false;
  }

  const relativeDrop = (baselineEmits - currentEmits) / baselineEmits;
  if (relativeDrop > regressionThreshold) {
    return true;
  }

  // Allocation regression: more allocations than baseline. Only gate when both
  // sides are real counts; an Unmeasured sentinel (-1) is never comparable.
  const currentAllocs = Number.parseInt(current.gcAllocations, 10);
  const baselineAllocs = Number.parseInt(baseline.gcAllocations, 10);
  return allocationsComparable(currentAllocs, baselineAllocs) && currentAllocs > baselineAllocs;
}

function computeRegressed(currentByScenario, baselineByScenario, regressionThreshold) {
  // Comparison rows stay in the delta table, but single comparison samples are
  // report-only; the required gate tracks the canonical dispatch scenarios.
  return SCENARIO_ORDER.some((scenario) => {
    const current = currentByScenario.get(scenario);
    const baseline = baselineByScenario.get(scenario);
    return current && baseline && isRegression(current, baseline, regressionThreshold);
  });
}

function writeOutput(outputPath, markdown) {
  if (!outputPath) {
    process.stdout.write(`${markdown}\n`);
    return;
  }
  fs.writeFileSync(outputPath, `${markdown}\n`, "utf8");
}

function readBaselineRows(baselineCsv) {
  if (!baselineCsv || !fs.existsSync(baselineCsv)) {
    return null;
  }
  let content;
  try {
    content = fs.readFileSync(baselineCsv, "utf8");
  } catch {
    return null;
  }
  return extractRows(content);
}

function render(options) {
  const baselineRows = readBaselineRows(options.baselineCsv);
  if (baselineRows === null || baselineRows.length === 0) {
    return {
      changed: false,
      regressed: false,
      markdown:
        "_No baseline on master yet; skipping the DxMessaging delta comparison " +
        "(it will populate once the baseline CSV is committed to the default branch)._"
    };
  }

  const currentRows = extractRows(readInputs(options.inputs));

  // Match render-perf-doc's Unity-version filter.
  const platformSubstring = options.unityVersion ? `Unity ${options.unityVersion}` : "";

  const currentByScenario = indexDxMessagingRows(currentRows, options.scope, platformSubstring);
  const baselineByScenario = indexDxMessagingRows(baselineRows, options.scope, platformSubstring);

  const table = buildDeltaTable(
    currentByScenario,
    baselineByScenario,
    options.tolerance,
    options.scope
  );
  const regressionThreshold = Number.isFinite(options.regressionThreshold)
    ? options.regressionThreshold
    : DEFAULT_REGRESSION_THRESHOLD;
  const regressed = computeRegressed(currentByScenario, baselineByScenario, regressionThreshold);

  return { ...table, regressed };
}

function main(argv = process.argv) {
  const options = parseArgs(argv);
  if (options.help) {
    process.stdout.write(usage());
    return 0;
  }

  const result = render(options);
  writeOutput(options.output, result.markdown);
  process.stdout.write(`changed=${result.changed ? "true" : "false"}\n`);
  process.stdout.write(`regressed=${result.regressed ? "true" : "false"}\n`);
  return 0;
}

if (require.main === module) {
  try {
    process.exitCode = main();
  } catch (error) {
    // Delta rendering is diagnostic. Keep stdout stable on crashes, but emit
    // false signals so CI does not gate without a computed regression.
    process.stderr.write(`${error.message}\n\n${usage()}`);
    process.stdout.write("changed=false\n");
    process.stdout.write("regressed=false\n");
    process.exitCode = 0;
  }
}

module.exports = {
  isDxMessagingRow,
  indexDxMessagingRows,
  scenarioLabel,
  goodnessRelativeChange,
  formatAllocDelta,
  formatBytesDelta,
  compareRow,
  buildDeltaTable,
  isRegression,
  computeRegressed,
  render,
  readBaselineRows
};
