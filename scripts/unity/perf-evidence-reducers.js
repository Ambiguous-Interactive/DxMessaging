"use strict";
const { isDeepStrictEqual } = require("node:util");
const { extractRows, buildCsv, deriveScope } = require("./extract-perf-baseline.js");
const { reducePairedBracket } = require("./reduce-paired-bracket.js");
// Reducers use only supplied bytes and ordinal ordering. Replay requires exact JSON equality.
const MATRIX_EVIDENCE_NAME = "shipping-matrix-evidence.json";
const CELL_EVIDENCE_SUFFIX = "/shipping-cell-evidence.json";
const NORMALIZED_SCHEMA_VERSION = 1;
/** Copied verbatim from each cell. These are the columns the matrix characterization publishes. */
const CELL_FIELDS = Object.freeze([
  "managedStrippingLevel",
  "topologyId",
  "messageTypeCount",
  "libraryState",
  "buildDurationMs",
  "editorBuildWallClockMs",
  "playerTotalBytes",
  "gameAssemblyBytes"
]);
const TIMING_FIELDS = Object.freeze([
  "engineStartToRunMs",
  "firstTypedDispatchUs",
  "dispatchLoopNsPerOp",
  "dispatchLoopShape"
]);
function requireBytes(contents, relativePath) {
  const bytes = contents.get(relativePath);
  if (bytes === undefined) {
    throw new Error(`${relativePath} is required by this reducer but is not in the bundle.`);
  }
  return bytes;
}
function parseJsonObject(contents, relativePath, characterization = true) {
  const bytes = requireBytes(contents, relativePath);
  let parsed;
  try {
    parsed = JSON.parse(bytes.toString("utf8"));
  } catch (error) {
    throw new Error(`${relativePath} is not readable JSON: ${error.message}`);
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new Error(`${relativePath} must contain a JSON object.`);
  }
  if (
    characterization &&
    (parsed.schemaVersion !== 1 || parsed.measurementClass !== "characterization")
  )
    throw new Error(`${relativePath} must use characterization schema version 1.`);
  return parsed;
}
// Preserve the existing exploratory screen, including every negative or invalid verdict.
function reducePairedThroughputScreen(contents, { sourceCommit } = {}) {
  const result = reducePairedBracket(
    requireBytes(contents, "bracket-manifest.json"),
    ["first.json", "center.json", "last.json"].map((file) => parseJsonObject(contents, file, false))
  );
  if (sourceCommit !== undefined && sourceCommit !== result.provenance[0].commit)
    throw new Error("Paired screen sourceCommit must match the first run's commit.");
  return result;
}
// Retain aggregate probe observations, not campaign intervals or an allocation verdict.
function reduceSubUnsubObservations(contents, { sourceCommit } = {}) {
  const csv = requireBytes(contents, "comparison-baseline.csv")
    .toString("utf8")
    .replaceAll("\r\n", "\n");
  const rows = extractRows(csv);
  // The extractor tolerates log noise, duplicates, legacy columns, and numeric prefixes.
  // Round-trip admission ensures none of those concessions silently discards CSV evidence.
  if (!rows.length || buildCsv(rows) !== csv)
    throw new Error("comparison-baseline.csv must be canonical non-empty eight-column CSV.");
  const raw = requireBytes(contents, "comparison-output.log").toString("utf8");
  if (buildCsv(extractRows(raw)) !== csv)
    throw new Error("comparison-baseline.csv disagrees with retained comparison-output.log rows.");
  const platform = rows[0].platform;
  const scope = deriveScope(platform);
  if (!scope || platform.match(/\b(?:Standalone|PlayMode|EditMode)\b/g).length !== 1)
    throw new Error("comparison-baseline.csv has an unknown or ambiguous execution scope.");
  const identities = new Set();
  for (const row of rows) {
    if (row.commit !== sourceCommit)
      throw new Error(`${row.scenario} commit must match sourceCommit.`);
    if (row.platform !== platform)
      throw new Error(`${row.scenario} platform must match every retained row.`);
    const identity = `${row.scenario}:${row.runIndex}`;
    if (identities.has(identity)) throw new Error(`${identity} duplicates a run identity.`);
    identities.add(identity);
    if (
      ["runIndex", "gcAllocations", "gcAllocatedBytes"].some(
        (field) => !Number.isSafeInteger(Number(row[field])) || Number(row[field]) < -1
      ) ||
      Number(row.emitsPerSecond) < 0 ||
      Number(row.wallClockMs) <= 0
    )
      throw new Error(`${identity} has an invalid measurement or sentinel.`);
  }
  const observations = rows.filter((row) => row.scenario.endsWith("_SubUnsub"));
  if (!observations.some((row) => row.scenario === "Comparison_DxMessaging_SubUnsub"))
    throw new Error("comparison-baseline.csv requires Comparison_DxMessaging_SubUnsub.");
  return {
    schemaVersion: 1,
    measurementClass: "observation",
    sourceCommit,
    platform,
    scope,
    rows: observations
  };
}
function requireValue(source, field, relativePath) {
  const value = source?.[field];
  const integer = /(?:Count|Bytes)$/.test(field);
  const numeric = integer || /(?:Ms|Us|NsPerOp)$/.test(field);
  if (
    numeric
      ? !Number.isFinite(value) || value < 0 || (integer && !Number.isSafeInteger(value))
      : typeof value !== "string" || value.trim() === ""
  )
    throw new Error(`${relativePath} has an invalid required field ${field}.`);
  return value;
}
function requireStringArray(value, label) {
  if (
    !Array.isArray(value) ||
    value.some((entry) => typeof entry !== "string" || !entry.trim()) ||
    new Set(value).size !== value.length
  )
    throw new Error(`${label} must be an array of unique non-empty strings.`);
  return [...value].sort();
}
// Summarize integer sizes by stripping level in ordinal order.
function summarizeByStrippingLevel(cells) {
  const levels = new Map();
  for (const cell of cells) {
    const level = cell.managedStrippingLevel;
    const summary = levels.get(level) ?? {
      managedStrippingLevel: level,
      cellCount: 0,
      minPlayerTotalBytes: cell.playerTotalBytes,
      maxPlayerTotalBytes: cell.playerTotalBytes,
      minGameAssemblyBytes: cell.gameAssemblyBytes,
      maxGameAssemblyBytes: cell.gameAssemblyBytes
    };
    summary.cellCount += 1;
    summary.minPlayerTotalBytes = Math.min(summary.minPlayerTotalBytes, cell.playerTotalBytes);
    summary.maxPlayerTotalBytes = Math.max(summary.maxPlayerTotalBytes, cell.playerTotalBytes);
    summary.minGameAssemblyBytes = Math.min(summary.minGameAssemblyBytes, cell.gameAssemblyBytes);
    summary.maxGameAssemblyBytes = Math.max(summary.maxGameAssemblyBytes, cell.gameAssemblyBytes);
    levels.set(level, summary);
  }
  return [...levels.values()].sort((left, right) =>
    left.managedStrippingLevel < right.managedStrippingLevel ? -1 : 1
  );
}
// Require one raw cell for every completed row, then derive values from those raw cells.
function reduceShippingFidelityMatrix(contents) {
  const matrix = parseJsonObject(contents, MATRIX_EVIDENCE_NAME);
  const unityVersion = requireValue(matrix, "unityVersion", MATRIX_EVIDENCE_NAME);
  const rows = new Map();
  if (!Array.isArray(matrix.cells))
    throw new Error(`${MATRIX_EVIDENCE_NAME} cells must be an array.`);
  for (const row of matrix.cells) {
    const cellId = requireValue(row, "cellId", MATRIX_EVIDENCE_NAME);
    if (rows.has(cellId)) throw new Error(`${MATRIX_EVIDENCE_NAME} duplicates cell ${cellId}.`);
    rows.set(cellId, row);
    if (!contents.has(`${cellId}${CELL_EVIDENCE_SUFFIX}`))
      throw new Error(`${cellId}${CELL_EVIDENCE_SUFFIX} is required by ${MATRIX_EVIDENCE_NAME}.`);
  }
  const failedCells = requireStringArray(matrix.failedCells, "failedCells");
  const unreadable = requireStringArray(matrix.unreadableEvidenceCells, "unreadableEvidenceCells");
  const allIds = [...rows.keys(), ...failedCells, ...unreadable];
  if (
    allIds.length === 0 ||
    new Set(allIds).size !== allIds.length ||
    requireValue(matrix, "completedCellCount", MATRIX_EVIDENCE_NAME) !== rows.size ||
    requireValue(matrix, "cellCount", MATRIX_EVIDENCE_NAME) !== allIds.length
  )
    throw new Error(
      `${MATRIX_EVIDENCE_NAME} cellCount, completedCellCount, or cell outcomes disagree.`
    );
  const cellPaths = [...contents.keys()].filter((key) => key.endsWith(CELL_EVIDENCE_SUFFIX)).sort();
  const cells = [];
  for (const cellPath of cellPaths) {
    const cellId = cellPath.slice(0, -CELL_EVIDENCE_SUFFIX.length);
    if (failedCells.includes(cellId) || unreadable.includes(cellId)) continue;
    const evidence = parseJsonObject(contents, cellPath);
    if (requireValue(evidence, "unityVersion", cellPath) !== unityVersion)
      throw new Error(`${cellPath} unityVersion differs from ${MATRIX_EVIDENCE_NAME}.`);
    const cell = { cellId };
    for (const field of [...CELL_FIELDS, ...TIMING_FIELDS]) {
      cell[field] = requireValue(
        TIMING_FIELDS.includes(field) ? evidence.timings : evidence,
        field,
        cellPath
      );
    }
    // Read-ShippingCellEvidence copies every raw field, overriding only cellId.
    if (!isDeepStrictEqual(rows.get(cellId), { ...evidence, cellId }))
      throw new Error(
        `${MATRIX_EVIDENCE_NAME} reports fields for cell ${cellId} that disagree with its raw cell.`
      );
    cells.push(cell);
  }
  return {
    schemaVersion: NORMALIZED_SCHEMA_VERSION,
    reducer: "shipping-fidelity-matrix-v1",
    measurementClass: "characterization",
    unityVersion,
    declaredCellCount: requireValue(matrix, "cellCount", MATRIX_EVIDENCE_NAME),
    completedCellCount: cells.length,
    failedCells,
    unreadableEvidenceCells: unreadable,
    strippingLevels: summarizeByStrippingLevel(cells),
    cells
  };
}
module.exports = {
  CELL_EVIDENCE_SUFFIX,
  MATRIX_EVIDENCE_NAME,
  reducePairedThroughputScreen,
  reduceSubUnsubObservations,
  reduceShippingFidelityMatrix,
  summarizeByStrippingLevel
};
