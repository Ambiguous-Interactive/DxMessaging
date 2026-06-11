"use strict";

const COMPARISON_SCENARIO_PREFIX = "Comparison_";

// Stable scenario order for the rendered dispatch tables. The set mirrors
// DispatchBenchmarkScenario keys; the order groups cold first-dispatch rows next
// to their warm dispatch counterparts for readability.
const SCENARIO_ORDER = [
  "UntargetedFlood_OneHandler",
  "UntargetedFlood_FourHandlers_OnePriority",
  "UntargetedFlood_FourHandlers_FourPriorities",
  "UntargetedFirstDispatch_Cold",
  "TargetedFlood_OneListener",
  "TargetedFlood_SixteenListeners",
  "TargetedFirstDispatch_Cold",
  "BroadcastFlood_OneHandler",
  "BroadcastFirstDispatch_Cold",
  "InterceptorHeavy_FourInterceptors",
  "PostProcessingHeavy_FourPostProcessors",
  "RegistrationFlood_1000Types_FromColdBus",
  "RegistrationFlood_1000Types_WarmJit"
];

const SCENARIOS = new Set(SCENARIO_ORDER);

// Human-readable dispatch scenario labels shown in the Scenario column. These
// mirror DispatchBenchmarkScenarios.DisplayName in BenchmarkProtocol.cs.
const DISPATCH_DISPLAY_NAMES = {
  UntargetedFlood_OneHandler: "Untargeted Flood (One Handler)",
  UntargetedFlood_FourHandlers_OnePriority: "Untargeted Flood (Four Handlers, One Priority)",
  UntargetedFlood_FourHandlers_FourPriorities: "Untargeted Flood (Four Handlers, Four Priorities)",
  UntargetedFirstDispatch_Cold: "Untargeted First Dispatch (Cold, Distinct Types)",
  TargetedFlood_OneListener: "Targeted Flood (One Listener)",
  TargetedFlood_SixteenListeners: "Targeted Flood (Sixteen Listeners)",
  TargetedFirstDispatch_Cold: "Targeted First Dispatch (Cold, Distinct Types)",
  BroadcastFlood_OneHandler: "Broadcast Flood (One Handler)",
  BroadcastFirstDispatch_Cold: "Broadcast First Dispatch (Cold, Distinct Types)",
  InterceptorHeavy_FourInterceptors: "Interceptor Heavy (Four Interceptors)",
  PostProcessingHeavy_FourPostProcessors: "Post-Processing Heavy (Four Post-Processors)",
  RegistrationFlood_1000Types_FromColdBus: "Registration Flood (1000 Types, Cold Bus)",
  RegistrationFlood_1000Types_WarmJit: "Registration Flood (1000 Types, Warm JIT)"
};

// Fixed column order for the comparison matrices. Mirrors the ComparisonScenario
// enum order in Tests/Runtime/Comparisons/ComparisonScenario.cs.
const COMPARISON_SCENARIO_ORDER = [
  "GlobalToOne",
  "GlobalToMany",
  "KeyedToOne",
  "PriorityOrdered",
  "Filtered",
  "PostProcess",
  "SubUnsub",
  "StructNoBox"
];

const COMPARISON_SCENARIO_SET = new Set(COMPARISON_SCENARIO_ORDER);

// Human-readable comparison scenario labels for the matrix column headers. These
// mirror ComparisonScenarios.DisplayName in ComparisonScenario.cs.
const COMPARISON_SCENARIO_LABELS = {
  GlobalToOne: "Global -> 1 subscriber",
  GlobalToMany: "Global -> 16 subscribers",
  KeyedToOne: "Keyed/targeted -> 1 of many",
  PriorityOrdered: "Priority-ordered dispatch",
  Filtered: "Filtered/intercepted dispatch",
  PostProcess: "Post-processing dispatch",
  SubUnsub: "Subscribe/unsubscribe churn",
  StructNoBox: "Struct message (zero-copy)"
};

// Fixed row order for the comparison matrices, by bridge TechKey. Mirrors each
// bridge's TechKey property in Tests/Runtime/Comparisons/**.
const COMPARISON_TECH_ORDER = [
  "DxMessaging",
  "MessagePipe",
  "UniRx",
  "ZenjectSignalBus",
  "UnityAtoms",
  "ScriptableObject",
  "UnityEvent",
  "CsEvent",
  "UnitySendMessage"
];

const COMPARISON_TECH_SET = new Set(COMPARISON_TECH_ORDER);

// Human-readable technology labels for the first matrix column.
const COMPARISON_TECH_LABELS = {
  DxMessaging: "DxMessaging",
  CsEvent: "C# event",
  UnityEvent: "UnityEvent",
  ScriptableObject: "ScriptableObject channel",
  UnitySendMessage: "Unity SendMessage",
  MessagePipe: "MessagePipe",
  UniRx: "UniRx MessageBroker",
  ZenjectSignalBus: "Zenject SignalBus",
  UnityAtoms: "Unity Atoms"
};

function buildComparisonScenarioId(techKey, scenarioKey) {
  return `${COMPARISON_SCENARIO_PREFIX}${techKey}_${scenarioKey}`;
}

const COMPARISON_SCENARIO_IDS = COMPARISON_TECH_ORDER.flatMap((techKey) =>
  COMPARISON_SCENARIO_ORDER.map((scenarioKey) => buildComparisonScenarioId(techKey, scenarioKey))
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

module.exports = {
  SCENARIO_ORDER,
  SCENARIOS,
  DISPATCH_DISPLAY_NAMES,
  COMPARISON_SCENARIO_ORDER,
  COMPARISON_SCENARIO_LABELS,
  COMPARISON_TECH_ORDER,
  COMPARISON_TECH_LABELS,
  COMPARISON_SCENARIO_IDS,
  buildComparisonScenarioId,
  parseComparisonScenario,
  isComparisonScenario
};
