"use strict";

const fs = require("fs");
const path = require("path");

const {
  COMPARISON_SCENARIO_IDS,
  SCENARIOS,
  isComparisonScenario,
  deriveScope
} = require("./perf-scenarios.js");

const CSV_HEADER =
  "scenario,platform,commit,runIndex,emitsPerSecond,gcAllocations,wallClockMs,gcAllocatedBytes";

// -1 is the AllocationProbe "Unmeasured" sentinel; a legacy 7-column CSV row has
// no gcAllocatedBytes field, so re-extracting it defaults the bytes to this
// sentinel (rendered "n/a") rather than crashing.
const UNMEASURED_SENTINEL = "-1";

// Cross-library comparison rows share the exact CSV/log shape of the dispatch
// rows but carry a synthetic scenario id of the form
// "Comparison_<TechKey>_<ScenarioKey>" (e.g. "Comparison_DxMessaging_GlobalToOne").
// They are NOT in SCENARIOS, so accept a row whose scenario is either a known
// dispatch key OR a known comparison tech/scenario pair. The CSV schema (8 fields;
// a legacy 7-field row defaults gcAllocatedBytes to the Unmeasured sentinel) and the
// structured-log key names are unchanged; only the set of kept scenario ids grows.

function isKeptScenario(scenario) {
  return (
    typeof scenario === "string" && (SCENARIOS.has(scenario) || isComparisonScenario(scenario))
  );
}

function usage() {
  return `Usage: node scripts/unity/extract-perf-baseline.js --input <unity-log-or-results> [--input <path> ...] [--output <csv>] [--append|--replace] [--scope <name>]

Extracts DispatchThroughputBenchmarks CSV rows from Unity logs or NUnit XML output.
When --output is omitted, writes the normalized CSV to stdout. --scope keeps only
rows whose platform's derived execution scope (Standalone/PlayMode/EditMode)
matches the value, so a multi-leg input can be narrowed to one scope (the
committed master baseline stays Standalone-scoped).
`;
}

// deriveScope (platform -> Standalone/PlayMode/EditMode, else null) is imported
// from perf-scenarios.js -- the shared, dependency-free module -- and re-exported
// below so callers keep their existing import surface. ONE canonical copy now,
// with no circular require.

function parseArgs(argv) {
  const options = {
    inputs: [],
    output: "",
    append: false,
    replace: false,
    scope: ""
  };

  for (let index = 2; index < argv.length; index++) {
    const arg = argv[index];
    if (arg === "--input") {
      options.inputs.push(requireValue(argv, ++index, arg));
      continue;
    }

    if (arg === "--output") {
      options.output = requireValue(argv, ++index, arg);
      continue;
    }

    if (arg === "--scope") {
      options.scope = requireValue(argv, ++index, arg);
      continue;
    }

    if (arg === "--append") {
      options.append = true;
      continue;
    }

    if (arg === "--replace") {
      options.replace = true;
      continue;
    }

    if (arg === "--help" || arg === "-h") {
      options.help = true;
      continue;
    }

    throw new Error(`Unknown argument: ${arg}`);
  }

  if (options.append && options.replace) {
    throw new Error("--append and --replace cannot be used together.");
  }

  return options;
}

function requireValue(argv, index, flag) {
  const value = argv[index];
  if (!value || value.startsWith("--")) {
    throw new Error(`${flag} requires a value.`);
  }

  return value;
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

function extractRows(content) {
  const rows = [];
  const seen = new Set();
  for (const line of content.split(/\r?\n/)) {
    const row = parseStructuredLogFromLine(line) || parseCsvRowFromLine(line);
    if (!row) {
      continue;
    }

    const csv = toCsvRow(row);
    if (!seen.has(csv)) {
      rows.push(row);
      seen.add(csv);
    }
  }

  return rows;
}

function parseCsvRowFromLine(line) {
  const trimmed = stripUnityPrefix(line.trim());
  if (!trimmed || trimmed.startsWith("scenario,")) {
    return null;
  }

  const fields = parseCsvFields(trimmed);
  // Accept both the legacy 7-column rows and the current 8-column rows. A legacy
  // row has no gcAllocatedBytes field (fields[7] undefined), so default it to the
  // Unmeasured sentinel rather than crashing.
  if ((fields.length !== 7 && fields.length !== 8) || !isKeptScenario(fields[0])) {
    return null;
  }

  return normalizeRow({
    scenario: fields[0],
    platform: fields[1],
    commit: fields[2],
    runIndex: fields[3],
    emitsPerSecond: fields[4],
    gcAllocations: fields[5],
    wallClockMs: fields[6],
    gcAllocatedBytes: fields[7] === undefined ? UNMEASURED_SENTINEL : fields[7]
  });
}

function parseStructuredLogFromLine(line) {
  const trimmed = stripUnityPrefix(line.trim());
  if (!trimmed.startsWith("{") || !trimmed.endsWith("}")) {
    return null;
  }

  const row = {
    scenario: matchStructuredString(trimmed, "scenario"),
    platform: matchStructuredString(trimmed, "platform"),
    commit: matchStructuredString(trimmed, "commit"),
    runIndex: matchStructuredNumber(trimmed, "runIndex"),
    emitsPerSecond: matchStructuredNumber(trimmed, "emitsPerSec"),
    gcAllocations: matchStructuredNumber(trimmed, "gcAllocations"),
    wallClockMs: matchStructuredNumber(trimmed, "wallClockMs"),
    gcAllocatedBytes: matchStructuredNumber(trimmed, "gcAllocatedBytes")
  };

  if (!isKeptScenario(row.scenario)) {
    return null;
  }

  return normalizeRow(row);
}

function stripUnityPrefix(line) {
  const structuredStart = line.indexOf("{scenario:");
  if (structuredStart >= 0) {
    return line.slice(structuredStart);
  }

  const csvStart = findScenarioIndex(line);
  if (csvStart >= 0) {
    return line.slice(csvStart);
  }

  return line;
}

function findScenarioIndex(line) {
  let bestIndex = -1;
  // Dispatch keys plus strict comparison-row scenario ids; the earliest CSV
  // scenario field wins so a leading Unity log prefix (e.g. timestamp) is
  // stripped off a CSV row without treating arbitrary log text as a row start.
  for (const scenario of SCENARIOS) {
    let searchFrom = 0;
    while (searchFrom < line.length) {
      const index = line.indexOf(scenario, searchFrom);
      if (index < 0) {
        break;
      }

      if (line[index + scenario.length] === "," && (bestIndex < 0 || index < bestIndex)) {
        bestIndex = index;
        if (bestIndex === 0) {
          return bestIndex;
        }
      }

      searchFrom = index + 1;
    }
  }

  for (const scenario of COMPARISON_SCENARIO_IDS) {
    let searchFrom = 0;
    while (searchFrom < line.length) {
      const index = line.indexOf(scenario, searchFrom);
      if (index < 0) {
        break;
      }

      if (line[index + scenario.length] === "," && (bestIndex < 0 || index < bestIndex)) {
        bestIndex = index;
        if (bestIndex === 0) {
          return bestIndex;
        }
      }

      searchFrom = index + 1;
    }
  }

  return bestIndex;
}

function parseCsvFields(line) {
  const fields = [];
  let current = "";
  let inQuotes = false;

  for (let index = 0; index < line.length; index++) {
    const value = line[index];
    if (value === '"') {
      if (inQuotes && line[index + 1] === '"') {
        current += '"';
        index++;
        continue;
      }

      inQuotes = !inQuotes;
      continue;
    }

    if (value === "," && !inQuotes) {
      fields.push(current);
      current = "";
      continue;
    }

    current += value;
  }

  fields.push(current);
  return fields;
}

function matchStructuredString(line, name) {
  const match = new RegExp(`${name}:"([^"]*)"`).exec(line);
  return match ? match[1] : "";
}

function matchStructuredNumber(line, name) {
  const match = new RegExp(`${name}:([-+]?\\d+(?:\\.\\d+)?)`).exec(line);
  return match ? match[1] : "";
}

function normalizeRow(row) {
  return {
    scenario: requireText(row.scenario, "scenario"),
    platform: requireText(row.platform, "platform"),
    commit: requireText(row.commit, "commit"),
    runIndex: normalizeInteger(row.runIndex, "runIndex"),
    emitsPerSecond: normalizeDecimal(row.emitsPerSecond, "emitsPerSecond"),
    gcAllocations: normalizeInteger(row.gcAllocations, "gcAllocations"),
    wallClockMs: normalizeDecimal(row.wallClockMs, "wallClockMs"),
    // A legacy CSV row or a legacy structured-log line carries no gcAllocatedBytes,
    // so default a missing/empty value to the Unmeasured sentinel (rendered "n/a").
    gcAllocatedBytes: normalizeInteger(
      row.gcAllocatedBytes === undefined || row.gcAllocatedBytes === ""
        ? UNMEASURED_SENTINEL
        : row.gcAllocatedBytes,
      "gcAllocatedBytes"
    )
  };
}

function requireText(value, name) {
  if (typeof value !== "string" || value.length === 0) {
    throw new Error(`Invalid ${name}: expected a non-empty string.`);
  }

  return value;
}

function normalizeInteger(value, name) {
  if (!/^-?\d+$/.test(String(value))) {
    throw new Error(`Invalid ${name}: ${value}`);
  }

  return String(Number.parseInt(value, 10));
}

function normalizeDecimal(value, name) {
  const parsed = Number.parseFloat(String(value));
  if (!Number.isFinite(parsed)) {
    throw new Error(`Invalid ${name}: ${value}`);
  }

  return parsed.toFixed(3);
}

function buildCsv(rows) {
  return [CSV_HEADER, ...rows.map(toCsvRow)].join("\n") + "\n";
}

function toCsvRow(row) {
  return [
    escapeCsv(row.scenario),
    escapeCsv(row.platform),
    escapeCsv(row.commit),
    row.runIndex,
    row.emitsPerSecond,
    row.gcAllocations,
    row.wallClockMs,
    row.gcAllocatedBytes
  ].join(",");
}

function escapeCsv(value) {
  if (!/[",\r\n]/.test(value)) {
    return value;
  }

  return `"${value.replace(/"/g, '""')}"`;
}

function writeRows(outputPath, rows, options) {
  if (!outputPath) {
    process.stdout.write(buildCsv(rows));
    return;
  }

  const outputExists = fs.existsSync(outputPath);
  if (outputExists && !options.append && !options.replace) {
    throw new Error(`Output already exists: ${outputPath}. Use --append or --replace.`);
  }

  fs.mkdirSync(path.dirname(outputPath), { recursive: true });

  if (options.append && outputExists) {
    const existing = fs.readFileSync(outputPath, "utf8");
    const prefix = existing.endsWith("\n") || existing.length === 0 ? "" : "\n";
    const body = rows.map(toCsvRow).join("\n");
    fs.appendFileSync(outputPath, `${prefix}${body}\n`, "utf8");
    return;
  }

  fs.writeFileSync(outputPath, buildCsv(rows), "utf8");
}

function main(argv = process.argv) {
  const options = parseArgs(argv);
  if (options.help) {
    process.stdout.write(usage());
    return 0;
  }

  let rows = extractRows(readInputs(options.inputs));
  // Filter to one execution scope BEFORE the empty-check so `--scope Standalone`
  // against editor-only inputs fails loudly with "No ... rows found" rather than
  // writing an empty baseline. Absent --scope keeps every row (existing behavior).
  if (options.scope) {
    rows = rows.filter((row) => deriveScope(row.platform) === options.scope);
  }
  if (rows.length === 0) {
    throw new Error("No DispatchThroughputBenchmarks rows found.");
  }

  writeRows(options.output, rows, options);
  process.stderr.write(`Extracted ${rows.length} performance baseline row(s).\n`);
  return 0;
}

if (require.main === module) {
  try {
    process.exitCode = main();
  } catch (error) {
    process.stderr.write(`${error.message}\n\n${usage()}`);
    process.exitCode = 1;
  }
}

module.exports = {
  CSV_HEADER,
  isKeptScenario,
  extractRows,
  buildCsv,
  parseArgs,
  deriveScope
};
