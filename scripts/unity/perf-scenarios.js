"use strict";

const COMPARISON_SCENARIO_PREFIX = "Comparison_";

// Stable rendered order, labels, and wall-clock classification; mirrors C# keys.
const SCENARIO_DEFINITIONS = [
  ["EmptyBus_Dispatch", "Empty Bus Dispatch"],
  ["UntargetedFlood_OneHandler", "Untargeted Flood (One Handler)"],
  ["UntargetedFlood_TwoHandlers_OnePriority", "Untargeted Flood (Two Handlers, One Priority)"],
  ["UntargetedFlood_ThreeHandlers_OnePriority", "Untargeted Flood (Three Handlers, One Priority)"],
  ["UntargetedFlood_FourHandlers_OnePriority", "Untargeted Flood (Four Handlers, One Priority)"],
  [
    "UntargetedFlood_FourHandlers_FourPriorities",
    "Untargeted Flood (Four Handlers, Four Priorities)"
  ],
  [
    "UntargetedFlood_SixteenHandlers_OnePriority",
    "Untargeted Flood (Sixteen Handlers, One Priority)"
  ],
  ["UntargetedFlood_OneInactiveHandler", "Untargeted Flood (One Inactive Handler)"],
  ["UntargetedFirstDispatch_Cold", "Untargeted First Dispatch (Cold, Distinct Types)", true],
  ["TargetedFlood_NoMatchingTarget", "Targeted Flood (No Matching Target)"],
  ["TargetedFlood_OneListener", "Targeted Flood (One Listener)"],
  ["TargetedFlood_SixteenListeners", "Targeted Flood (Sixteen Listeners)"],
  ["TargetedFirstDispatch_Cold", "Targeted First Dispatch (Cold, Distinct Types)", true],
  ["BroadcastFlood_OneHandler", "Broadcast Flood (One Handler)"],
  ["BroadcastFirstDispatch_Cold", "Broadcast First Dispatch (Cold, Distinct Types)", true],
  ["InterceptorHeavy_FourInterceptors", "Interceptor Heavy (Four Interceptors)"],
  ["PostProcessingHeavy_FourPostProcessors", "Post-Processing Heavy (Four Post-Processors)"],
  ["MessageBusConstruction_1000", "Message Bus Construction (1000)", true],
  [
    "MessageRegistrationTokenConstruction_1000_PrebuiltHandlerAndBus",
    "Registration Token Construction (1000, Prebuilt Handler + Bus)",
    true
  ],
  ["RegistrationFlood_1000Types_FromColdBus", "Registration Flood (1000 Types, Cold Bus)", true],
  ["RegistrationFlood_1000Types_WarmJit", "Registration Flood (1000 Types, Warm JIT)", true],
  ["UntargetedRegistration_Marginal", "Untargeted Registration (Marginal, 1000 Same-Type)", true],
  ["TargetedRegistration_Marginal", "Targeted Registration (Marginal, 1000 Same-Type)", true],
  ["BroadcastRegistration_Marginal", "Broadcast Registration (Marginal, 1000 Same-Type)", true],
  ["DeregistrationFlood_1000Types_Cold", "Deregistration Flood (1000 Types, Cold)", true],
  ["DeregistrationFlood_1000Types_WarmJit", "Deregistration Flood (1000 Types, Warm JIT)", true],
  [
    "DeregistrationAttribution_DirectBus_131072",
    "Deregistration Attribution (Direct Bus, 131072)",
    true
  ],
  [
    "DeregistrationAttribution_DirectHandler_131072",
    "Deregistration Attribution (Direct Handler, 131072)",
    true
  ],
  [
    "DeregistrationAttribution_TokenRemove_131072",
    "Deregistration Attribution (Token Remove, 131072)",
    true
  ],
  [
    "DeregistrationAttribution_TokenDisable_131072",
    "Deregistration Attribution (Token Disable, 131072)",
    true
  ]
];

const SCENARIO_ORDER = SCENARIO_DEFINITIONS.map(([key]) => key);
const SCENARIOS = new Set(SCENARIO_ORDER);
const WALL_CLOCK_SCENARIOS = new Set(
  SCENARIO_DEFINITIONS.filter(([, , wallClock]) => wallClock).map(([key]) => key)
);
const DISPATCH_DISPLAY_NAMES = Object.fromEntries(
  SCENARIO_DEFINITIONS.map(([key, displayName]) => [key, displayName])
);

// Fixed comparison-matrix columns; mirrors the ComparisonScenario enum order.
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

// Matrix column labels mirror ComparisonScenarios.DisplayName.
const COMPARISON_SCENARIO_LABELS = {
  GlobalToOne: "Global -> 1 subscriber",
  GlobalToMany: "Global -> 16 subscribers",
  KeyedToOne: "Keyed/targeted -> 1 of many",
  PriorityOrdered: "Priority-ordered dispatch",
  Filtered: "Filtered/intercepted dispatch",
  PostProcess: "Post-processing dispatch",
  SubUnsub: "Subscribe/unsubscribe churn",
  StructNoBox: "Struct message (no boxing)"
};

// Fixed matrix row order, mirroring each comparison bridge's TechKey.
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

const COMPARISON_SUPPORTED_SCENARIOS = require("./comparison-supported-scenarios.json");

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
