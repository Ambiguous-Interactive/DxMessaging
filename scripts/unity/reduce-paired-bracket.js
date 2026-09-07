"use strict";

const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");
const supportedScenarios = require("./comparison-supported-scenarios.json");

const SCHEMA_VERSION = 1;
const MATERIALITY_BAND_PERCENT = 3;
const COMPARISON_TOLERANCE_PERCENT = 1e-9;
const EXECUTION_PROFILE_ID = "highest-efficiency-class-affinity-normal-v1";
const AFFINITY_MASK = "0xFFFF";
const PRIORITY_CLASS = "Normal";
const PROTOCOL = "interleaved-abba-baab-v1";
const CYCLES = 4;
const MINIMUM_CYCLE_ACTIVE_MILLISECONDS = 625;
const BATCH_OPERATIONS = 10000;
const ORIENTATIONS = new Set(["candidate-control-candidate", "control-candidate-control"]);
const ROW_ROLES = new Set(["target", "affected", "sentinel"]);
const CANONICAL_ONLY_SCENARIOS = new Set(["PriorityOrdered", "SubUnsub"]);
const PAIRED_SCENARIOS = supportedScenarios.MessagePipe.filter(
  (scenario) => !CANONICAL_ONLY_SCENARIOS.has(scenario)
);
const RELATIVE_TOLERANCE = 1e-12;
const SPREAD_TOLERANCE_PERCENT = 1e-9;
const REPO_ROOT = path.resolve(__dirname, "../..");
const EXPECTED_CPU_MODEL = "13th Gen Intel(R) Core(TM) i9-13900KF";
const EXPECTED_LOGICAL_PROCESSORS = Array.from({ length: 16 }, (_, index) => index);
let trackedRuntimePaths;

function usage() {
  return `Usage: node scripts/unity/reduce-paired-bracket.js --manifest <json> --first <summary.json> --center <summary.json> --last <summary.json> [--output <json>]

Reduces one predeclared three-run paired benchmark bracket. The manifest must be
present, byte-for-byte unchanged, in all three retained summary files.
Use --validate-manifest with --manifest to validate the declaration before run one.
`;
}

function parseArgs(argv) {
  const options = {
    manifest: "",
    first: "",
    center: "",
    last: "",
    output: "",
    validateManifest: false,
    help: false
  };
  for (let index = 2; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === "--help" || argument === "-h") {
      options.help = true;
      continue;
    }
    if (argument === "--validate-manifest") {
      options.validateManifest = true;
      continue;
    }
    const optionName = argument.startsWith("--") ? argument.slice(2) : "";
    if (!["manifest", "first", "center", "last", "output"].includes(optionName)) {
      throw new Error(`Unknown option: ${argument}`);
    }
    const value = argv[++index];
    if (!value || value.startsWith("--")) {
      throw new Error(`${argument} requires a value.`);
    }
    options[optionName] = value;
  }
  if (!options.help) {
    const required = options.validateManifest
      ? ["manifest"]
      : ["manifest", "first", "center", "last"];
    for (const name of required) {
      if (!options[name]) {
        throw new Error(`--${name} is required.`);
      }
    }
  }
  return options;
}

function isObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function requireFiniteNumber(value, field) {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new Error(`${field} must be a finite number.`);
  }
  return value;
}

function requireExactKeys(value, keys, field) {
  const actual = Object.keys(value).sort();
  const expected = [...keys].sort();
  if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) {
    throw new Error(`${field} must contain exactly: ${expected.join(", ")}.`);
  }
}

function getTrackedRuntimePaths() {
  if (trackedRuntimePaths === undefined) {
    const output = execFileSync(
      "git",
      ["-C", REPO_ROOT, "ls-tree", "-r", "--name-only", "HEAD", "--", "Runtime"],
      { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }
    );
    trackedRuntimePaths = output.split(/\r?\n/).filter(Boolean);
  }
  return trackedRuntimePaths;
}

function validateManifest(manifest, { requireTrackedCandidatePaths = false } = {}) {
  if (!isObject(manifest)) {
    throw new Error("The bracket manifest must be an object.");
  }
  requireExactKeys(
    manifest,
    [
      "schemaVersion",
      "bracketId",
      "orientation",
      "materialityBandPercent",
      "candidatePaths",
      "rows"
    ],
    "The bracket manifest"
  );
  if (manifest.schemaVersion !== SCHEMA_VERSION) {
    throw new Error(`The bracket manifest schemaVersion must be ${SCHEMA_VERSION}.`);
  }
  if (typeof manifest.bracketId !== "string" || manifest.bracketId.trim() === "") {
    throw new Error("The bracket manifest bracketId must be a non-empty string.");
  }
  if (!ORIENTATIONS.has(manifest.orientation)) {
    throw new Error(`Unsupported bracket orientation: ${manifest.orientation}`);
  }
  if (manifest.materialityBandPercent !== MATERIALITY_BAND_PERCENT) {
    throw new Error(`The bracket materiality band must remain ${MATERIALITY_BAND_PERCENT}%.`);
  }
  if (!Array.isArray(manifest.candidatePaths) || manifest.candidatePaths.length === 0) {
    throw new Error("The bracket manifest candidatePaths must be a non-empty array.");
  }
  const candidatePaths = new Set();
  for (const [index, candidatePath] of manifest.candidatePaths.entries()) {
    if (
      typeof candidatePath !== "string" ||
      !candidatePath.startsWith("Runtime/") ||
      candidatePath.endsWith("/") ||
      candidatePath.includes("\\") ||
      candidatePath
        .split("/")
        .some((segment) => segment === "" || segment === "." || segment === "..")
    ) {
      throw new Error(
        `Manifest candidatePaths[${index}] must be a normalized path below Runtime/.`
      );
    }
    if (candidatePaths.has(candidatePath)) {
      throw new Error(`Manifest candidate path '${candidatePath}' is duplicated.`);
    }
    if (requireTrackedCandidatePaths) {
      const trackedPaths = getTrackedRuntimePaths();
      if (
        !trackedPaths.includes(candidatePath) &&
        !trackedPaths.some((trackedPath) => trackedPath.startsWith(`${candidatePath}/`))
      ) {
        throw new Error(
          `Manifest candidate path '${candidatePath}' is not tracked at HEAD with exact case.`
        );
      }
    }
    candidatePaths.add(candidatePath);
  }
  if (!Array.isArray(manifest.rows) || manifest.rows.length === 0) {
    throw new Error("The bracket manifest rows must be a non-empty array.");
  }
  if (
    manifest.rows.length !== PAIRED_SCENARIOS.length ||
    manifest.rows.some((row, index) => !isObject(row) || row.scenario !== PAIRED_SCENARIOS[index])
  ) {
    throw new Error(
      `The bracket manifest must classify every paired scenario in this order: ${PAIRED_SCENARIOS.join(
        ", "
      )}.`
    );
  }

  const scenarios = new Set();
  let targetCount = 0;
  let sentinelCount = 0;
  for (const [index, row] of manifest.rows.entries()) {
    if (!isObject(row)) {
      throw new Error(`Manifest row ${index} must be an object.`);
    }
    requireExactKeys(row, ["scenario", "role"], `Manifest row ${index}`);
    if (typeof row.scenario !== "string" || row.scenario.trim() === "") {
      throw new Error(`Manifest row ${index} scenario must be a non-empty string.`);
    }
    if (scenarios.has(row.scenario)) {
      throw new Error(`Manifest scenario '${row.scenario}' is duplicated.`);
    }
    scenarios.add(row.scenario);
    if (!ROW_ROLES.has(row.role)) {
      throw new Error(`Manifest scenario '${row.scenario}' has unsupported role '${row.role}'.`);
    }
    targetCount += row.role === "target" ? 1 : 0;
    sentinelCount += row.role === "sentinel" ? 1 : 0;
  }
  if (targetCount === 0) {
    throw new Error("The bracket manifest must declare at least one target row.");
  }
  if (sentinelCount < 2) {
    throw new Error("The bracket manifest must declare at least two sentinel rows.");
  }
  return manifest;
}

function manifestSha256(manifestBytes) {
  return crypto.createHash("sha256").update(manifestBytes).digest("hex");
}

function requireCycleEvidence(row, label, ratio, spread) {
  if (!Array.isArray(row.cycleRatios) || row.cycleRatios.length !== CYCLES) {
    throw new Error(`${label} cycleRatios must contain exactly ${CYCLES} values.`);
  }
  const cycleRatios = row.cycleRatios.map((value, index) => {
    const cycleRatio = requireFiniteNumber(value, `${label} cycleRatios[${index}]`);
    if (cycleRatio <= 0) {
      throw new Error(`${label} cycleRatios[${index}] must be positive.`);
    }
    return cycleRatio;
  });
  const logs = cycleRatios.map(Math.log);
  const headlineLog = logs.reduce((sum, value) => sum + value, 0) / CYCLES;
  const recomputedHeadline = Math.exp(headlineLog);
  if (
    !Number.isFinite(recomputedHeadline) ||
    recomputedHeadline <= 0 ||
    Math.abs(Math.log(ratio) - headlineLog) > Math.log1p(RELATIVE_TOLERANCE)
  ) {
    throw new Error(`${label} headline is not the geometric mean of its cycleRatios.`);
  }
  const logRange = Math.max(...logs) - Math.min(...logs);
  const recomputedSpread = Math.expm1(logRange) * 100;
  if (
    !Number.isFinite(recomputedSpread) ||
    Math.abs(spread - recomputedSpread) > SPREAD_TOLERANCE_PERCENT
  ) {
    throw new Error(`${label} spread does not match its cycleRatios.`);
  }
}

function validateSummary(summary, label, manifest, expectedDigest) {
  if (!isObject(summary)) {
    throw new Error(`${label} summary must be an object.`);
  }
  if (summary.schemaVersion !== 2) {
    throw new Error(`${label} summary schemaVersion must be 2.`);
  }
  if (typeof summary.commit !== "string" || !/^[0-9a-f]{40}$/.test(summary.commit)) {
    throw new Error(`${label} summary commit must be a full lowercase Git SHA-1.`);
  }
  if (typeof summary.sourceTree !== "string" || !/^[0-9a-f]{40}$/.test(summary.sourceTree)) {
    throw new Error(`${label} summary sourceTree must be a full lowercase Git tree SHA-1.`);
  }
  if (
    typeof summary.candidateSourceSha256 !== "string" ||
    !/^[0-9a-f]{64}$/.test(summary.candidateSourceSha256)
  ) {
    throw new Error(`${label} summary candidateSourceSha256 must be a lowercase SHA-256.`);
  }
  if (
    typeof summary.platform !== "string" ||
    !/^Standalone IL2CPP x64 Release \(WindowsPlayer; Unity \d+\.\d+\.\d+f\d+\)$/.test(
      summary.platform
    )
  ) {
    throw new Error(`${label} summary platform is not the pinned Standalone IL2CPP Release shape.`);
  }
  if (
    !isObject(summary.executionProfile) ||
    summary.executionProfile.id !== EXECUTION_PROFILE_ID ||
    summary.executionProfile.affinityMask !== AFFINITY_MASK ||
    summary.executionProfile.priorityClass !== PRIORITY_CLASS
  ) {
    throw new Error(`${label} summary execution profile does not match the pinned profile.`);
  }
  requireExactKeys(
    summary.executionProfile,
    [
      "id",
      "cpuModel",
      "source",
      "selectionPolicy",
      "selectedEfficiencyClass",
      "selectedLogicalProcessorIndices",
      "affinityMask",
      "priorityClass"
    ],
    `${label} summary execution profile`
  );
  if (
    summary.executionProfile.cpuModel !== EXPECTED_CPU_MODEL ||
    summary.executionProfile.source !== "GetSystemCpuSetInformation" ||
    summary.executionProfile.selectionPolicy !== "maximum EfficiencyClass" ||
    !Number.isInteger(summary.executionProfile.selectedEfficiencyClass) ||
    summary.executionProfile.selectedEfficiencyClass < 0 ||
    !Array.isArray(summary.executionProfile.selectedLogicalProcessorIndices) ||
    summary.executionProfile.selectedLogicalProcessorIndices.length !==
      EXPECTED_LOGICAL_PROCESSORS.length ||
    summary.executionProfile.selectedLogicalProcessorIndices.some(
      (logicalProcessor, index) => logicalProcessor !== EXPECTED_LOGICAL_PROCESSORS[index]
    )
  ) {
    throw new Error(`${label} summary execution profile topology does not match the pinned host.`);
  }
  if (
    summary.protocol !== PROTOCOL ||
    summary.cycles !== CYCLES ||
    summary.minimumCycleActiveMilliseconds !== MINIMUM_CYCLE_ACTIVE_MILLISECONDS ||
    summary.batchOperations !== BATCH_OPERATIONS
  ) {
    throw new Error(`${label} summary protocol constants do not match the pinned protocol.`);
  }
  if (summary.bracketManifestSha256 !== expectedDigest) {
    throw new Error(`${label} summary does not match the predeclared bracket manifest digest.`);
  }
  if (!Array.isArray(summary.rows)) {
    throw new Error(`${label} summary rows must be an array.`);
  }
  if (summary.materialityBandPercent !== manifest.materialityBandPercent) {
    throw new Error(`${label} summary materiality band does not match the manifest.`);
  }
  if (summary.rows.length !== manifest.rows.length) {
    throw new Error(`${label} summary row count does not match the manifest.`);
  }

  const rows = new Map();
  for (let index = 0; index < manifest.rows.length; index++) {
    const expected = manifest.rows[index].scenario;
    const row = summary.rows[index];
    if (!isObject(row) || row.scenario !== expected) {
      throw new Error(`${label} summary row ${index} must be '${expected}'.`);
    }
    if (rows.has(row.scenario)) {
      throw new Error(`${label} summary scenario '${row.scenario}' is duplicated.`);
    }
    const ratio = requireFiniteNumber(
      row.firstToSecondRatio,
      `${label} summary '${row.scenario}' firstToSecondRatio`
    );
    if (ratio <= 0) {
      throw new Error(`${label} summary '${row.scenario}' ratio must be positive.`);
    }
    const spread = requireFiniteNumber(
      row.cycleRatioSpreadPercent,
      `${label} summary '${row.scenario}' cycleRatioSpreadPercent`
    );
    if (spread < 0) {
      throw new Error(`${label} summary '${row.scenario}' spread must not be negative.`);
    }
    requireCycleEvidence(row, `${label} summary '${row.scenario}'`, ratio, spread);
    rows.set(row.scenario, { ratio, spread });
  }
  return rows;
}

function stableIdentity(summary) {
  return JSON.stringify({
    platform: summary.platform,
    executionProfile: summary.executionProfile,
    protocol: summary.protocol,
    cycles: summary.cycles,
    minimumCycleActiveMilliseconds: summary.minimumCycleActiveMilliseconds,
    batchOperations: summary.batchOperations,
    materialityBandPercent: summary.materialityBandPercent
  });
}

function reducePairedBracket(manifestBytes, summaries) {
  if (!Buffer.isBuffer(manifestBytes) && typeof manifestBytes !== "string") {
    throw new Error("Manifest bytes must be a Buffer or string.");
  }
  if (!Array.isArray(summaries) || summaries.length !== 3) {
    throw new Error("Exactly three retained summaries are required.");
  }
  const manifestText = manifestBytes.toString();
  const manifest = validateManifest(JSON.parse(manifestText));
  const digest = manifestSha256(manifestBytes);
  const labels = ["first", "center", "last"];
  const rowsByRun = summaries.map((summary, index) =>
    validateSummary(summary, labels[index], manifest, digest)
  );
  const identity = stableIdentity(summaries[0]);
  for (let index = 1; index < summaries.length; index++) {
    if (stableIdentity(summaries[index]) !== identity) {
      throw new Error(`${labels[index]} summary protocol or execution profile does not match.`);
    }
  }
  if (new Set(summaries.map((summary) => summary.commit)).size !== 3) {
    throw new Error("The three summaries must come from distinct commits.");
  }
  if (summaries[0].sourceTree !== summaries[2].sourceTree) {
    throw new Error("The two outer summaries must contain the same source tree.");
  }
  if (summaries[0].sourceTree === summaries[1].sourceTree) {
    throw new Error("The outer and center summaries must contain different source trees.");
  }
  if (summaries[0].candidateSourceSha256 !== summaries[2].candidateSourceSha256) {
    throw new Error("The two outer summaries must contain the same candidate-source digest.");
  }
  if (summaries[0].candidateSourceSha256 === summaries[1].candidateSourceSha256) {
    throw new Error(
      "The outer and center summaries must contain different candidate-source digests."
    );
  }

  const band = manifest.materialityBandPercent;
  const rowResults = [];
  const sentinelFactors = [];
  for (const declaration of manifest.rows) {
    const first = rowsByRun[0].get(declaration.scenario);
    const center = rowsByRun[1].get(declaration.scenario);
    const last = rowsByRun[2].get(declaration.scenario);
    const outerLogMean = (Math.log(first.ratio) + Math.log(last.ratio)) / 2;
    const candidateLogFactor =
      manifest.orientation === "candidate-control-candidate"
        ? outerLogMean - Math.log(center.ratio)
        : Math.log(center.ratio) - outerLogMean;
    const candidateFactor = Math.exp(candidateLogFactor);
    const candidateEffectPercent = Math.expm1(candidateLogFactor) * 100;
    const outerSpreadPercent =
      Math.expm1(Math.abs(Math.log(first.ratio) - Math.log(last.ratio))) * 100;
    if (
      !Number.isFinite(candidateFactor) ||
      candidateFactor <= 0 ||
      !Number.isFinite(candidateEffectPercent) ||
      !Number.isFinite(outerSpreadPercent)
    ) {
      throw new Error(`${declaration.scenario} produced a non-finite bracket reduction.`);
    }
    const maximumRawSpreadPercent = Math.max(first.spread, center.spread, last.spread);
    if (declaration.role === "sentinel") {
      sentinelFactors.push(candidateFactor);
    }
    rowResults.push({
      scenario: declaration.scenario,
      role: declaration.role,
      candidateLogFactor,
      candidateEffectPercent,
      outerSpreadPercent,
      maximumRawSpreadPercent
    });
  }

  const sentinelLogReference =
    sentinelFactors.reduce((sum, factor) => sum + Math.log(factor), 0) / sentinelFactors.length;
  const sentinelReference = Math.exp(sentinelLogReference);
  if (!Number.isFinite(sentinelReference) || sentinelReference <= 0) {
    throw new Error("The sentinel reference is not finite and positive.");
  }
  for (const row of rowResults) {
    row.sentinelNormalizedEffectPercent =
      row.role === "sentinel"
        ? null
        : Math.expm1(row.candidateLogFactor - sentinelLogReference) * 100;
    if (
      row.sentinelNormalizedEffectPercent !== null &&
      !Number.isFinite(row.sentinelNormalizedEffectPercent)
    ) {
      throw new Error(`${row.scenario} produced a non-finite sentinel-normalized effect.`);
    }
  }

  const unstableReasons = [];
  const rejectionReasons = [];
  for (const row of rowResults) {
    if (row.maximumRawSpreadPercent > band + COMPARISON_TOLERANCE_PERCENT) {
      unstableReasons.push(`${row.scenario} raw-cycle spread exceeds ${band}%.`);
    }
    if (row.outerSpreadPercent > band + COMPARISON_TOLERANCE_PERCENT) {
      unstableReasons.push(`${row.scenario} outer spread exceeds ${band}%.`);
    }
    if (
      row.role === "sentinel" &&
      Math.abs(row.candidateEffectPercent) > band + COMPARISON_TOLERANCE_PERCENT
    ) {
      unstableReasons.push(`${row.scenario} sentinel effect exceeds +/-${band}%.`);
    }
    if (
      row.role === "target" &&
      row.sentinelNormalizedEffectPercent <= band + COMPARISON_TOLERANCE_PERCENT
    ) {
      rejectionReasons.push(
        `${row.scenario} sentinel-normalized target effect does not exceed ${band}%.`
      );
    }
    if (
      row.role === "affected" &&
      row.sentinelNormalizedEffectPercent < -band - COMPARISON_TOLERANCE_PERCENT
    ) {
      rejectionReasons.push(
        `${row.scenario} sentinel-normalized affected-row regression exceeds ${band}%.`
      );
    }
  }

  const status =
    unstableReasons.length > 0
      ? "uninterpretable"
      : rejectionReasons.length > 0
        ? "rejected"
        : "accepted";
  return {
    schemaVersion: SCHEMA_VERSION,
    bracketId: manifest.bracketId,
    orientation: manifest.orientation,
    bracketManifestSha256: digest,
    materialityBandPercent: band,
    provenance: summaries.map((summary, index) => ({
      position: labels[index],
      commit: summary.commit,
      sourceTree: summary.sourceTree,
      candidateSourceSha256: summary.candidateSourceSha256
    })),
    sentinelReference,
    status,
    reasons: status === "uninterpretable" ? unstableReasons : rejectionReasons,
    rows: rowResults.map(({ candidateLogFactor, ...row }) => row)
  };
}

function runCli(argv) {
  const options = parseArgs(argv);
  if (options.help) {
    process.stdout.write(usage());
    return 0;
  }
  const manifestBytes = fs.readFileSync(options.manifest);
  if (options.validateManifest) {
    validateManifest(JSON.parse(manifestBytes.toString()), { requireTrackedCandidatePaths: true });
    process.stdout.write(`${manifestSha256(manifestBytes)}\n`);
    return 0;
  }
  const summaries = [options.first, options.center, options.last].map((file) =>
    JSON.parse(fs.readFileSync(file, "utf8"))
  );
  const result = reducePairedBracket(manifestBytes, summaries);
  const output = `${JSON.stringify(result, null, 2)}\n`;
  if (options.output) {
    fs.writeFileSync(options.output, output);
  } else {
    process.stdout.write(output);
  }
  return result.status === "accepted" ? 0 : 2;
}

if (require.main === module) {
  try {
    process.exitCode = runCli(process.argv);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = {
  MATERIALITY_BAND_PERCENT,
  manifestSha256,
  parseArgs,
  reducePairedBracket,
  runCli,
  validateManifest
};
