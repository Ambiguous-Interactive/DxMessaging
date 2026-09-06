"use strict";

const Ajv = require("ajv");
const CONTRACT_SCHEMA = require("./comparison-contract-v1.schema.json");
const COMPARISON_SEMANTIC_LEDGER = require("./comparison-semantic-ledger-v1.json");
const COMPARISON_EVIDENCE_CATALOG = require("./comparison-evidence-catalog-v1.json");
const ajv = new Ajv({ allErrors: true, strict: true });
ajv.addSchema(CONTRACT_SCHEMA);
const validateComparisonLedger = ajv.compile({
  $ref: `${CONTRACT_SCHEMA.$id}#/definitions/ledger`
});
const validateComparisonCatalog = ajv.compile({
  $ref: `${CONTRACT_SCHEMA.$id}#/definitions/catalog`
});
for (const [validate, data] of [
  [validateComparisonLedger, COMPARISON_SEMANTIC_LEDGER],
  [validateComparisonCatalog, COMPARISON_EVIDENCE_CATALOG]
]) {
  if (!validate(data)) {
    throw new Error(`Invalid comparison contract: ${ajv.errorsText(validate.errors)}`);
  }
}

const COMPARISON_SCENARIO_PREFIX = "Comparison_";
const POST_ROUTE_SCENARIO_DEFINITIONS = require("./post-route-perf-scenarios.json");

// Stable labels/order mirror C# keys; attribution rows mirror both ScenarioKey methods
// in RegistrationLifecycleBenchmarks.cs. Post-route definitions remain single-sourced.
const {
  DISPATCH_BEFORE_POST_ROUTES,
  DISPATCH_AFTER_POST_ROUTES,
  COMPARISON_SCENARIO_ORDER,
  COMPARISON_SCENARIO_LABELS,
  COMPARISON_TECH_ORDER,
  COMPARISON_TECH_LABELS
} = require("./perf-scenario-definitions.json");
const SCENARIO_DEFINITIONS = [
  ...DISPATCH_BEFORE_POST_ROUTES,
  ...POST_ROUTE_SCENARIO_DEFINITIONS,
  ...DISPATCH_AFTER_POST_ROUTES
];

const SCENARIO_ORDER = SCENARIO_DEFINITIONS.map(([key]) => key);
const SCENARIOS = new Set(SCENARIO_ORDER);
const WALL_CLOCK_SCENARIOS = new Set(
  SCENARIO_DEFINITIONS.filter(([, , wallClock]) => wallClock).map(([key]) => key)
);
const DISPATCH_DISPLAY_NAMES = Object.fromEntries(
  SCENARIO_DEFINITIONS.map(([key, displayName]) => [key, displayName])
);

const COMPARISON_SCENARIO_SET = new Set(COMPARISON_SCENARIO_ORDER);

const COMPARISON_TECH_SET = new Set(COMPARISON_TECH_ORDER);

const COMPARISON_SUPPORTED_SCENARIOS = require("./comparison-supported-scenarios.json");

function buildComparisonScenarioId(techKey, scenarioKey) {
  return `${COMPARISON_SCENARIO_PREFIX}${techKey}_${scenarioKey}`;
}

const COMPARISON_SCENARIO_IDS = COMPARISON_TECH_ORDER.flatMap((techKey) =>
  COMPARISON_SCENARIO_ORDER.map((scenarioKey) => buildComparisonScenarioId(techKey, scenarioKey))
);

const COMPARISON_SUPPORTED_SCENARIO_IDS = COMPARISON_TECH_ORDER.flatMap((techKey) =>
  COMPARISON_SUPPORTED_SCENARIOS[techKey].map((scenarioKey) =>
    buildComparisonScenarioId(techKey, scenarioKey)
  )
);

// Parse a comparison row scenario id ("Comparison_<TechKey>_<ScenarioKey>") into
// known tech and scenario keys. TechKey values are single tokens, so the first
// underscore after the prefix splits tech from scenario.
function parseComparisonScenario(scenario) {
  if (typeof scenario !== "string" || !scenario.startsWith(COMPARISON_SCENARIO_PREFIX)) {
    return null;
  }

  const rest = scenario.slice(COMPARISON_SCENARIO_PREFIX.length);
  const splitAt = rest.indexOf("_");
  if (splitAt <= 0) {
    return null;
  }

  const techKey = rest.slice(0, splitAt);
  const scenarioKey = rest.slice(splitAt + 1);
  if (!COMPARISON_TECH_SET.has(techKey) || !COMPARISON_SCENARIO_SET.has(scenarioKey)) {
    return null;
  }

  return { techKey, scenarioKey };
}

function isComparisonScenario(scenario) {
  return parseComparisonScenario(scenario) !== null;
}

// Derive execution scope from the benchmark's leading platform tokens. Standalone
// wins if a future platform contains both tokens. Unknown shapes return null. This
// shared dependency-free copy avoids a renderer/extractor import cycle.
function deriveScope(platform) {
  if (typeof platform !== "string") {
    return null;
  }
  if (/\bStandalone\b/.test(platform)) {
    return "Standalone";
  }
  if (/\bPlayMode\b/.test(platform)) {
    return "PlayMode";
  }
  if (/\bEditMode\b/.test(platform)) {
    return "EditMode";
  }
  return null;
}

module.exports = {
  COMPARISON_SEMANTIC_LEDGER,
  COMPARISON_EVIDENCE_CATALOG,
  validateComparisonLedger,
  validateComparisonCatalog,
  SCENARIO_ORDER,
  SCENARIOS,
  WALL_CLOCK_SCENARIOS,
  DISPATCH_DISPLAY_NAMES,
  COMPARISON_SCENARIO_ORDER,
  COMPARISON_SCENARIO_LABELS,
  COMPARISON_TECH_ORDER,
  COMPARISON_TECH_LABELS,
  COMPARISON_SCENARIO_IDS,
  COMPARISON_SUPPORTED_SCENARIOS,
  COMPARISON_SUPPORTED_SCENARIO_IDS,
  buildComparisonScenarioId,
  parseComparisonScenario,
  isComparisonScenario,
  deriveScope
};
