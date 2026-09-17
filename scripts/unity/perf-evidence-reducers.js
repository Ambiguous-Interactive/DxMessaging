"use strict";
const crypto = require("crypto");
const path = require("path");
const { spawnSync } = require("child_process");
const { isDeepStrictEqual } = require("node:util");
const DIFFERENTIAL_CONTRACT = require("./differential-replay-contract.json");
const { extractRows, buildCsv, deriveScope } = require("./extract-perf-baseline.js");
const { reducePairedBracket } = require("./reduce-paired-bracket.js");
// Reducers use only supplied bytes and ordinal ordering. Replay requires exact JSON equality.
const MATRIX_EVIDENCE_NAME = "shipping-matrix-evidence.json";
const CELL_EVIDENCE_SUFFIX = "/shipping-cell-evidence.json";
const NORMALIZED_SCHEMA_VERSION = 1;
const DIFFERENTIAL_ENVIRONMENT_NAME = "differential-replay-environment.json";
const DIFFERENTIAL_PROFILE_NAME = "differential-replay-profile.json";
const DIFFERENTIAL_KINDS = DIFFERENTIAL_CONTRACT.profile.messageKinds;
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
function requireExactKeys(value, keys, label) {
  if (!value || typeof value !== "object" || Array.isArray(value))
    throw new Error(`${label} must contain an object.`);
  if (!isDeepStrictEqual(Object.keys(value).sort(), [...keys].sort()))
    throw new Error(`${label} has missing or unexpected fields.`);
  return value;
}
function requireReplayString(value, label) {
  if (typeof value !== "string" || !value.trim()) throw new Error(`${label} must be non-empty.`);
  return value;
}
function requireReplayInteger(value, label, minimum = 0) {
  if (!Number.isSafeInteger(value) || value < minimum)
    throw new Error(`${label} must be a safe integer at least ${minimum}.`);
  return value;
}
function contentSha256(contents, relativePath) {
  return crypto.createHash("sha256").update(requireBytes(contents, relativePath)).digest("hex");
}
function validateObservation(value, label) {
  const observation = requireExactKeys(value, DIFFERENTIAL_CONTRACT.observationFields, label);
  const arrays = ["callbacks", "finalEmissions", "unmatchedDiagnostics"];
  const nullableStrings = ["exception", "trimResult"];
  if (
    !arrays.every(
      (field) =>
        Array.isArray(observation[field]) &&
        observation[field].every((entry) => typeof entry === "string")
    ) ||
    typeof observation.state !== "string" ||
    !nullableStrings.every(
      (field) => observation[field] === null || typeof observation[field] === "string"
    ) ||
    !["occupiedTypeSlots", "occupiedTargetSlots"].every(
      (field) => Number.isSafeInteger(observation[field]) && observation[field] >= 0
    )
  )
    throw new Error(`${label} contains an invalid observation value.`);
  return observation;
}
// SYNC: DifferentialBusTrace.Compare in Tests/Runtime/TestUtilities/DifferentialBusTrace.cs.
function replayMismatchCategory(control, candidate) {
  if (!isDeepStrictEqual(control.callbacks, candidate.callbacks)) return "callbacks";
  if (control.exception !== candidate.exception) return "exception";
  if (control.trimResult !== candidate.trimResult) return "trim";
  if (control.state !== candidate.state) return "state";
  if (
    control.occupiedTypeSlots !== candidate.occupiedTypeSlots ||
    control.occupiedTargetSlots !== candidate.occupiedTargetSlots
  )
    return "storage";
  if (!isDeepStrictEqual(control.finalEmissions, candidate.finalEmissions)) return "final-emission";
  if (!isDeepStrictEqual(control.unmatchedDiagnostics, candidate.unmatchedDiagnostics))
    return "unmatched-diagnostic";
  return null;
}
function validateReplay(value, label) {
  const replay = requireExactKeys(value, DIFFERENTIAL_CONTRACT.replayFields, label);
  if (!Array.isArray(replay.operations) || replay.operations.length === 0)
    throw new Error(`${label}.operations must be non-empty.`);
  const integerFields = DIFFERENTIAL_CONTRACT.operationIntegerFields;
  for (const [index, operation] of replay.operations.entries()) {
    const operationLabel = `${label}.operations[${index}]`;
    requireExactKeys(operation, ["kind", ...integerFields, "handlerActive"], operationLabel);
    if (
      typeof operation.kind !== "string" ||
      !operation.kind ||
      typeof operation.handlerActive !== "boolean" ||
      !integerFields.every((field) => Number.isSafeInteger(operation[field]))
    )
      throw new Error(`${operationLabel} contains an invalid operation value.`);
  }
  for (const field of ["controlTrace", "candidateTrace"]) {
    if (!Array.isArray(replay[field]) || replay[field].length !== replay.operations.length)
      throw new Error(`${label}.${field} must match the operation count.`);
    replay[field].forEach((entry, index) =>
      validateObservation(entry, `${label}.${field}[${index}]`)
    );
  }
  let mismatchIndex = -1;
  let category = null;
  for (let index = 0; index < replay.operations.length && mismatchIndex < 0; index++) {
    category = replayMismatchCategory(replay.controlTrace[index], replay.candidateTrace[index]);
    if (category !== null) mismatchIndex = index;
  }
  if (replay.mismatchIndex !== mismatchIndex || replay.category !== category || mismatchIndex < 0)
    throw new Error(`${label} does not declare its first observable mismatch exactly.`);
  return replay;
}
function isOperationSubsequence(original, minimized) {
  let cursor = 0;
  for (const operation of original) {
    if (isDeepStrictEqual(operation, minimized[cursor])) cursor += 1;
  }
  return cursor === minimized.length;
}
function reduceDifferentialReplayFailure(contents, { sourceCommit } = {}) {
  const environment = requireExactKeys(
    parseJsonObject(contents, DIFFERENTIAL_ENVIRONMENT_NAME, false),
    DIFFERENTIAL_CONTRACT.environmentFields,
    DIFFERENTIAL_ENVIRONMENT_NAME
  );
  const profile = requireExactKeys(
    parseJsonObject(contents, DIFFERENTIAL_PROFILE_NAME, false),
    DIFFERENTIAL_CONTRACT.profileFields,
    DIFFERENTIAL_PROFILE_NAME
  );
  if (
    environment.schemaVersion !== 1 ||
    environment.sourceCommit !== sourceCommit ||
    !/^[0-9a-f]{40}$/.test(environment.sourceTree)
  )
    throw new Error(`${DIFFERENTIAL_ENVIRONMENT_NAME} has invalid source identity.`);
  for (const field of ["unityVersion", "testMode", "scriptingBackend", "testAssembly", "testName"])
    requireReplayString(environment[field], `${DIFFERENTIAL_ENVIRONMENT_NAME}.${field}`);
  if (!isDeepStrictEqual(profile, DIFFERENTIAL_CONTRACT.profile))
    throw new Error(
      `${DIFFERENTIAL_PROFILE_NAME} does not match the reviewed native lifecycle profile.`
    );
  const cases = DIFFERENTIAL_KINDS.map((messageKind) => {
    const relativePath = `replays/${messageKind.toLowerCase()}.json`;
    const value = requireExactKeys(
      parseJsonObject(contents, relativePath, false),
      DIFFERENTIAL_CONTRACT.caseFields,
      relativePath
    );
    if (
      value.schemaVersion !== 1 ||
      value.observationSchemaVersion !== profile.observationSchemaVersion ||
      value.generatorVersion !== profile.generatorVersion ||
      value.seed !== profile.seed ||
      value.messageKind !== messageKind ||
      value.fault !== profile.fault
    )
      throw new Error(`${relativePath} disagrees with the replay profile.`);
    const original = validateReplay(value.original, `${relativePath}.original`);
    const minimized = validateReplay(value.minimized, `${relativePath}.minimized`);
    if (
      !isDeepStrictEqual(original.operations, DIFFERENTIAL_CONTRACT.operations) ||
      !isDeepStrictEqual(minimized.operations, [DIFFERENTIAL_CONTRACT.operations[3]]) ||
      original.mismatchIndex !== 3 ||
      minimized.mismatchIndex !== 0 ||
      original.category !== profile.classification ||
      minimized.category !== profile.classification ||
      !isOperationSubsequence(original.operations, minimized.operations)
    )
      throw new Error(`${relativePath} is not the reviewed deletion-minimal lifecycle failure.`);
    return {
      messageKind,
      originalOperationCount: original.operations.length,
      originalMismatchIndex: original.mismatchIndex,
      minimizedOperationCount: minimized.operations.length,
      minimizedMismatchIndex: minimized.mismatchIndex
    };
  });
  const candidate = requireBytes(contents, "candidate-adapter.txt");
  requireReplayString(candidate.toString("utf8"), "candidate-adapter.txt");
  const replayCommand = requireBytes(contents, "replay-command.txt").toString("utf8").trim();
  requireReplayString(replayCommand, "replay-command.txt");
  return {
    schemaVersion: 1,
    evidenceClass: "differential-replay-failure",
    sourceCommit,
    sourceTree: environment.sourceTree,
    environmentSha256: contentSha256(contents, DIFFERENTIAL_ENVIRONMENT_NAME),
    profileSha256: contentSha256(contents, DIFFERENTIAL_PROFILE_NAME),
    candidateSha256: crypto.createHash("sha256").update(candidate).digest("hex"),
    replayCommand,
    unityVersion: environment.unityVersion,
    testMode: environment.testMode,
    scriptingBackend: environment.scriptingBackend,
    testAssembly: environment.testAssembly,
    testName: environment.testName,
    generatorVersion: profile.generatorVersion,
    observationSchemaVersion: profile.observationSchemaVersion,
    seed: profile.seed,
    fault: profile.fault,
    classification: profile.classification,
    cases
  };
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
function reduceOpenLoopEditorCapture(contents, { sourceCommit } = {}) {
  const encoded = Object.fromEntries([...contents].map(([name, bytes]) => [name, bytes.toString("base64")]));
  const input = JSON.stringify({ sourceCommit, contents: encoded });
  const script = path.join(__dirname, "audit_open_loop_trace.py");
  const run = spawnSync(process.platform === "win32" ? "python" : "python3", [script, "--bundle-stdin"], { input, encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
  if (run.error || run.status !== 0)
    throw new Error(`Open-loop capture replay failed: ${run.error?.message ?? run.stderr?.trim()}`);
  return JSON.parse(run.stdout);
}
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
  DIFFERENTIAL_ENVIRONMENT_NAME,
  DIFFERENTIAL_PROFILE_NAME,
  MATRIX_EVIDENCE_NAME,
  reduceDifferentialReplayFailure,
  reducePairedThroughputScreen,
  reduceOpenLoopEditorCapture,
  reduceSubUnsubObservations,
  reduceShippingFidelityMatrix,
  summarizeByStrippingLevel
};
