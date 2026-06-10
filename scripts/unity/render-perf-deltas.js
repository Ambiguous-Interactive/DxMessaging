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

// Wall-clock (latency) scenarios report wall-clock milliseconds (lower is better)
// and a zero throughput, so their delta is measured on wall clock, not emits/sec.
// This covers both registration floods (cold + warm-JIT) and the three cold
// first-dispatch scenarios. MIRRORS REGISTRATION_SCENARIOS in render-perf-doc.js
// (kept local because that set is not exported). isRegression needs no change: a
// baseline emitsPerSecond<=0 already auto-excludes every one of these from the gate.
const REGISTRATION_SCENARIOS = new Set([
  "RegistrationFlood_1000Types_FromColdBus",
  "RegistrationFlood_1000Types_WarmJit",
  "UntargetedFirstDispatch_Cold",
  "TargetedFirstDispatch_Cold",
  "BroadcastFirstDispatch_Cold"
]);

// The DxMessaging comparison tech key. The delta comment is DxMessaging-only:
// dispatch rows are all DxMessaging, but among comparison rows we keep ONLY
// Comparison_DxMessaging_* and drop every other Comparison_<tech>_* row.
const DXMESSAGING_TECH_KEY = "DxMessaging";

const DEFAULT_TOLERANCE = 0.02;
const DEFAULT_SCOPE = "PlayMode";

// The redesigned CI regression gate fires when a throughput scenario drops by more
// than this fraction (relative) OR allocates more bytes than its baseline. It is
// deliberately looser than --tolerance (which only decides whether the delta COMMENT
// says "changed"): the comment flags small movements for humans, while the gate only
// fails the job on a meaningful regression. The workflow reads the `regressed=` line.
const DEFAULT_REGRESSION_THRESHOLD = 0.33;

function usage() {
  return `Usage: node scripts/unity/render-perf-deltas.js --input <log-or-xml> [--input <path> ...] --baseline-csv <path> --unity-version <version> [--tolerance <fraction>] [--regression-threshold <fraction>] [--scope <name>] --output <markdown>

Renders a DxMessaging-only delta table comparing the current benchmark run
against the committed master baseline CSV, for a single execution scope. Prints
'changed=true' if any DxMessaging metric moved beyond --tolerance, else
'changed=false', AND 'regressed=true|false' (the CI regression gate signal).
Always exits 0 -- the workflow decides whether to fail the job from the
'regressed=' line, so the delta comment can post first.

  --input                Current-run Unity log or NUnit results file (repeatable).
  --baseline-csv         Committed master baseline CSV (docs/architecture/perf-baseline.csv).
  --unity-version        Unity version whose rows drive the comparison (e.g. 6000.3.16f1).
  --tolerance            Relative move that counts as "changed" (default ${DEFAULT_TOLERANCE}).
  --regression-threshold Relative throughput drop that counts as a regression (default ${DEFAULT_REGRESSION_THRESHOLD}).
  --scope                Execution scope to compare (default ${DEFAULT_SCOPE}).
  --output               Markdown file to write the delta table to.

If --baseline-csv is missing, unreadable, or contains only a header (no data rows
yet -- first rollout) a short note is written to --output, 'changed=false' and
'regressed=false' are printed, and the process exits 0.
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
    if (arg === "--input") {
      options.inputs.push(requireValue(argv, ++index, arg));
      continue;
    }
    if (arg === "--baseline-csv") {
      options.baselineCsv = requireValue(argv, ++index, arg);
      continue;
    }
    if (arg === "--unity-version") {
      options.unityVersion = requireValue(argv, ++index, arg);
      continue;
    }
    if (arg === "--tolerance") {
      options.tolerance = parseTolerance(requireValue(argv, ++index, arg));
      continue;
    }
    if (arg === "--regression-threshold") {
      options.regressionThreshold = parseThreshold(requireValue(argv, ++index, arg));
      continue;
    }
    if (arg === "--scope") {
      options.scope = requireValue(argv, ++index, arg);
      continue;
    }
    if (arg === "--output") {
      options.output = requireValue(argv, ++index, arg);
      continue;
    }
    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }
    throw new Error(`Unknown argument: ${arg}`);
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

function parseTolerance(value) {
  const parsed = Number.parseFloat(value);
  if (!Number.isFinite(parsed) || parsed < 0) {
    throw new Error(`--tolerance must be a non-negative number, got: ${value}`);
  }
  return parsed;
}

function parseThreshold(value) {
  const parsed = Number.parseFloat(value);
  if (!Number.isFinite(parsed) || parsed < 0) {
    throw new Error(`--regression-threshold must be a non-negative number, got: ${value}`);
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

// Is this row a DxMessaging row we should compare? A row is kept when its
// scenario is one of the dispatch keys OR a Comparison_DxMessaging_* row.
// Every other Comparison_<tech>_* row (MessagePipe, UniRx, ...) is dropped so the
// PR delta comment is strictly about DxMessaging's own numbers.
function isDxMessagingRow(scenario) {
  if (SCENARIO_ORDER.includes(scenario)) {
    return true;
  }
  const parsed = parseComparisonScenario(scenario);
  return parsed !== null && parsed.techKey === DXMESSAGING_TECH_KEY;
}

// Build a Map keyed by scenario id (the raw key) -> row, for rows whose derived
// scope matches the requested scope AND that are DxMessaging rows. Matching is on
// (scenario, scope), NOT the full platform string, so a Mono/IL2CPP backend word
// in the platform never breaks current-vs-baseline matching. First row per
// scenario within the scope wins (deterministic). When `platformSubstring` is
// non-empty, rows are first restricted to that substring (e.g. "Unity <version>")
// so a multi-version log/CSV only contributes the requested Unity version's rows.
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

// Stable scenario order for the delta table: the dispatch scenarios first (in
// SCENARIO_ORDER), then the DxMessaging comparison scenarios (in
// COMPARISON_SCENARIO_ORDER), each as its synthetic Comparison_DxMessaging_<key>.
function deltaScenarioOrder() {
  return [
    ...SCENARIO_ORDER,
    ...COMPARISON_SCENARIO_ORDER.map((key) => buildComparisonScenarioId(DXMESSAGING_TECH_KEY, key))
  ];
}

// Human label for a delta-table scenario row. Dispatch keys use
// DISPATCH_DISPLAY_NAMES; DxMessaging comparison rows use the comparison-scenario
// label so the same human names appear as in the doc.
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

function formatBytes(bytes) {
  const value = Number.parseInt(bytes, 10);
  return value === 0 ? "0 B" : `${value.toLocaleString("en-US")} B`;
}

// Signed percentage string, e.g. "+3.21%" / "-1.04%" / "0.00%". A genuinely-zero
// move renders "0.00%" without a sign.
function formatPct(fraction) {
  if (!Number.isFinite(fraction)) {
    return "n/a";
  }
  const pct = fraction * 100;
  const rounded = pct.toFixed(2);
  if (Number.parseFloat(rounded) === 0) {
    return "0.00%";
  }
  const sign = pct > 0 ? "+" : "";
  return `${sign}${rounded}%`;
}

// Signed byte delta string, e.g. "+1,024 B" / "-512 B" / "0 B".
function formatByteDelta(delta) {
  if (delta === 0) {
    return "0 B";
  }
  const sign = delta > 0 ? "+" : "-";
  return `${sign}${Math.abs(delta).toLocaleString("en-US")} B`;
}

function relativeChange(current, baseline) {
  if (baseline === 0) {
    return current === 0 ? 0 : Infinity;
  }
  return (current - baseline) / baseline;
}

// Compute the per-scenario comparison for one (current, baseline) row pair.
// Returns { cells, moved } where cells is the [Scenario, Baseline, Current,
// Delta] table row and moved is true when ANY tracked metric moved beyond
// tolerance.
//   - Registration scenarios (0 throughput): wall-clock pct + allocation bytes.
//   - All other scenarios: throughput pct + allocation bytes.
// The Delta cell shows the primary-metric pct and, when allocation changed, the
// signed byte delta too.
function compareRow(scenario, current, baseline, tolerance) {
  const label = scenarioLabel(scenario);
  const currentBytes = Number.parseInt(current.allocatedBytesDelta, 10);
  const baselineBytes = Number.parseInt(baseline.allocatedBytesDelta, 10);
  const byteDelta = currentBytes - baselineBytes;
  const byteMoved =
    baselineBytes === 0 ? byteDelta !== 0 : Math.abs(byteDelta / baselineBytes) > tolerance;

  let baselineCell;
  let currentCell;
  let primaryPct;
  let primaryMoved;

  if (REGISTRATION_SCENARIOS.has(scenario)) {
    const baselineMs = Number.parseFloat(baseline.wallClockMs);
    const currentMs = Number.parseFloat(current.wallClockMs);
    primaryPct = relativeChange(currentMs, baselineMs);
    primaryMoved = Math.abs(primaryPct) > tolerance;
    baselineCell = `${formatWallClock(baseline.wallClockMs)}, ${formatBytes(baseline.allocatedBytesDelta)}`;
    currentCell = `${formatWallClock(current.wallClockMs)}, ${formatBytes(current.allocatedBytesDelta)}`;
  } else {
    const baselineEmits = Number.parseFloat(baseline.emitsPerSecond);
    const currentEmits = Number.parseFloat(current.emitsPerSecond);
    primaryPct = relativeChange(currentEmits, baselineEmits);
    primaryMoved = Math.abs(primaryPct) > tolerance;
    baselineCell = `${formatThroughput(baseline.emitsPerSecond)}, ${formatBytes(baseline.allocatedBytesDelta)}`;
    currentCell = `${formatThroughput(current.emitsPerSecond)}, ${formatBytes(current.allocatedBytesDelta)}`;
  }

  const deltaParts = [formatPct(primaryPct)];
  if (byteDelta !== 0) {
    deltaParts.push(formatByteDelta(byteDelta));
  }
  const deltaCell = deltaParts.join(", ");

  return {
    cells: [label, baselineCell, currentCell, deltaCell],
    moved: primaryMoved || byteMoved
  };
}

// Build the markdown delta table and the changed flag from the current + baseline
// row indexes. Only scenarios present in BOTH current and baseline (for the
// requested scope) produce a row; a scenario missing from either side is skipped
// (it cannot be a delta). Returns { markdown, changed }.
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
    if (comparison.moved) {
      changed = true;
    }
  }

  if (dataRows.length === 0) {
    return {
      markdown: `No overlapping DxMessaging scenarios for scope **${scope}** in both the current run and the baseline.`,
      changed: false
    };
  }

  return { markdown: alignTable([header, ...dataRows]), changed };
}

// Does this single (current, baseline) pair count as a regression? Only THROUGHPUT
// scenarios participate: the baseline must carry emitsPerSecond > 0, which excludes
// the registration flood and any wall-clock-only / zero-throughput scenario (their
// baseline emits is 0). Gating on the baseline side also avoids a divide-by-zero in
// the relative-drop computation. A pair regresses when EITHER:
//   - throughput dropped (current < baseline) by MORE than the threshold (strict >),
//     i.e. (baseline - current) / baseline > threshold; OR
//   - the current allocation is STRICTLY greater than the baseline allocation.
// Returns false for any non-throughput scenario so the gate never trips on the
// registration flood's wall-clock/allocation movement.
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

  const baselineBytes = Number.parseInt(baseline.allocatedBytesDelta, 10);
  const currentBytes = Number.parseInt(current.allocatedBytesDelta, 10);
  if (currentBytes > baselineBytes) {
    return true;
  }

  return false;
}

// The CI regression gate signal computed from the SAME indexed current/baseline
// maps the delta table uses. true when ANY overlapping throughput scenario regresses
// (see isRegression). Only scenarios present in BOTH sides participate; a scenario
// missing from either side cannot be a regression. A caller that passes an empty
// baseline map (missing / header-only baseline) gets false (no overlap), which keeps
// the gate graceful on first rollout.
function computeRegressed(currentByScenario, baselineByScenario, regressionThreshold) {
  for (const scenario of deltaScenarioOrder()) {
    const current = currentByScenario.get(scenario);
    const baseline = baselineByScenario.get(scenario);
    if (!current || !baseline) {
      continue;
    }
    if (isRegression(current, baseline, regressionThreshold)) {
      return true;
    }
  }
  return false;
}

function writeOutput(outputPath, markdown) {
  if (!outputPath) {
    process.stdout.write(`${markdown}\n`);
    return;
  }
  fs.writeFileSync(outputPath, `${markdown}\n`, "utf8");
}

// Read the baseline CSV through the SAME extractRows() the current run uses, so
// both sides share one parser. Returns null when the file is absent/unreadable so
// the caller can degrade gracefully on first rollout.
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
  // A missing/unreadable baseline (null) AND a header-only baseline (file exists but
  // extractRows found zero data rows) are both treated as "no baseline yet": emit the
  // graceful note, changed=false, regressed=false. Reading --input is skipped so the
  // first-rollout path never crashes on absent inputs.
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

  // Restrict both sides to the requested Unity version (by platform substring,
  // exactly like render-perf-doc) so a multi-version log/CSV only contributes the
  // version under comparison. An empty unity-version disables the filter.
  const platformSubstring = options.unityVersion ? `Unity ${options.unityVersion}` : "";

  const currentByScenario = indexDxMessagingRows(currentRows, options.scope, platformSubstring);
  const baselineByScenario = indexDxMessagingRows(baselineRows, options.scope, platformSubstring);

  const table = buildDeltaTable(
    currentByScenario,
    baselineByScenario,
    options.tolerance,
    options.scope
  );
  // Default the threshold when a programmatic caller omits it (the CLI always sets
  // it). A non-finite override also degrades to the default rather than disabling
  // the gate.
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
    // A delta comment must never fail the workflow: report the error, emit safe
    // changed=false / regressed=false signals, and exit 0 so the (non-blocking) PR
    // comment step degrades gracefully and the gate never trips on a crash.
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
  compareRow,
  buildDeltaTable,
  isRegression,
  computeRegressed,
  readBaselineRows
};
