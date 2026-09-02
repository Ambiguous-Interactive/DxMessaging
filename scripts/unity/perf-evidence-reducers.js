"use strict";

/**
 * Deterministic reducers for content-addressed performance-evidence bundles (issue #508).
 *
 * A reducer turns the raw files of one sealed bundle into the normalized, machine-readable result
 * that a decision cites. It must be a pure function of those bytes: same bytes in, byte-identical
 * JSON out, on every operating system. That is what lets `perf-evidence-bundle.js replay` prove a
 * published number was derived from the retained evidence and not typed in by hand.
 *
 * Rules every reducer here follows:
 *   - read only from the supplied content map, never from disk, the clock, or the environment;
 *   - copy measured values verbatim and derive only exact integer comparisons, so no floating point
 *     rounding can differ between the sealing runner and a reviewer's machine;
 *   - order every array by an ordinal key rather than by directory-walk order.
 */

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

function fail(message) {
  throw new Error(message);
}

function parseJsonObject(contents, relativePath) {
  const bytes = contents.get(relativePath);
  if (bytes === undefined) {
    fail(`${relativePath} is required by this reducer but is not in the bundle.`);
  }
  let parsed;
  try {
    parsed = JSON.parse(bytes.toString("utf8"));
  } catch (error) {
    fail(`${relativePath} is not readable JSON: ${error.message}`);
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    fail(`${relativePath} must contain a JSON object.`);
  }
  return parsed;
}

function requirePresent(source, field, relativePath) {
  const value = source[field];
  if (value === undefined || value === null) {
    fail(`${relativePath} is missing required field ${field}.`);
  }
  return value;
}

function requireStringArray(value, label) {
  if (!Array.isArray(value) || value.some((entry) => typeof entry !== "string")) {
    fail(`${label} must be an array of strings.`);
  }
  return [...value].sort();
}

/**
 * Cross-check one cell against the row the matrix wrapper already summarized. A tampered summary
 * that no longer matches the per-cell evidence it claims to describe must not replay cleanly.
 */
function assertMatrixRowAgrees(row, cell, cellId) {
  for (const field of CELL_FIELDS) {
    if (row[field] === undefined) {
      continue;
    }
    if (row[field] !== cell[field]) {
      fail(
        `${MATRIX_EVIDENCE_NAME} reports ${field}=${JSON.stringify(row[field])} for cell ` +
          `${cellId} but its own evidence says ${JSON.stringify(cell[field])}.`
      );
    }
  }
}

/**
 * Group the per-cell player sizes by stripping level. Stripping level is the factor the matrix
 * exists to characterize, and min/max over integers is order independent and exactly reproducible.
 */
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

/**
 * Reduce a sealed shipping-fidelity matrix bundle. Reads the matrix summary for the run-level
 * facts and every `<cellId>/shipping-cell-evidence.json` for the measured values, so the normalized
 * result is derived from the raw per-cell evidence rather than trusted from the summary.
 */
function reduceShippingFidelityMatrix(contents) {
  const matrix = parseJsonObject(contents, MATRIX_EVIDENCE_NAME);
  const rows = new Map();
  for (const row of Array.isArray(matrix.cells) ? matrix.cells : []) {
    if (row && typeof row.cellId === "string") {
      rows.set(row.cellId, row);
    }
  }
  const cellPaths = [...contents.keys()].filter((key) => key.endsWith(CELL_EVIDENCE_SUFFIX)).sort();
  if (cellPaths.length === 0) {
    fail(`The bundle declares no <cellId>${CELL_EVIDENCE_SUFFIX} evidence file.`);
  }
  const cells = [];
  for (const cellPath of cellPaths) {
    const cellId = cellPath.slice(0, -CELL_EVIDENCE_SUFFIX.length);
    const evidence = parseJsonObject(contents, cellPath);
    const timings = requirePresent(evidence, "timings", cellPath);
    const cell = { cellId };
    for (const field of CELL_FIELDS) {
      cell[field] = requirePresent(evidence, field, cellPath);
    }
    for (const field of TIMING_FIELDS) {
      cell[field] = requirePresent(timings, `${field}`, `${cellPath} timings`);
    }
    const row = rows.get(cellId);
    if (row === undefined) {
      fail(`${MATRIX_EVIDENCE_NAME} does not list completed cell ${cellId}.`);
    }
    assertMatrixRowAgrees(row, cell, cellId);
    cells.push(cell);
  }
  const failedCells = requireStringArray(matrix.failedCells ?? [], "failedCells");
  const unreadable = requireStringArray(
    matrix.unreadableEvidenceCells ?? [],
    "unreadableEvidenceCells"
  );
  return {
    schemaVersion: NORMALIZED_SCHEMA_VERSION,
    reducer: "shipping-fidelity-matrix-v1",
    measurementClass: "characterization",
    unityVersion: requirePresent(matrix, "unityVersion", MATRIX_EVIDENCE_NAME),
    declaredCellCount: requirePresent(matrix, "cellCount", MATRIX_EVIDENCE_NAME),
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
  reduceShippingFidelityMatrix,
  summarizeByStrippingLevel
};
