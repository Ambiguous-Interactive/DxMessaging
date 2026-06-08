"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

const {
  BEGIN_MARKER,
  END_MARKER,
  SCOPE_ORDER,
  SCOPE_BACKEND,
  NEUTRAL_RUNNER_DESCRIPTION,
  deriveScope,
  parseComparisonScenario,
  selectRowsForVersion,
  alignTable,
  buildDispatchTable,
  buildBlock,
  formatBytesPerOp,
  scopeLabel,
  deriveBackendLabel,
  readMachineSpecs,
  formatMachineSpecs,
  blocksEquivalent,
  render
} = require("../unity/render-perf-doc.js");

const SCRIPT = path.resolve(__dirname, "..", "unity", "render-perf-doc.js");
const REPO_ROOT = path.resolve(__dirname, "..", "..");
const MANAGED_PRETTIER = path.resolve(REPO_ROOT, "scripts", "run-managed-prettier.js");
const COMMITTED_DOC = path.resolve(REPO_ROOT, "docs", "architecture", "performance.md");

const LATEST = "6000.3.16f1";
const OLDER = "2022.3.45f1";

// The platform cell now encodes the execution SCOPE as its leading token(s):
// "Editor EditMode ...", "Editor PlayMode ...", or "Standalone ..." (plus the
// scripting backend, arch, build config, and "(...; Unity <version>)"). Standalone
// runs the IL2CPP backend; PlayMode/EditMode run Mono -- the backend token is part
// of the real platform string and the renderer must not key its SCOPE off it.
function platform(version, scope = "PlayMode") {
  const target = scope === "Standalone" ? "Standalone" : `Editor ${scope}`;
  const backend = scope === "Standalone" ? "IL2CPP" : "Mono";
  return `${target} ${backend} x64 Release (LinuxEditor; Unity ${version})`;
}

// The thirteen dispatch scenarios (emits, alloc, ms). The registration floods (cold +
// warm-JIT) and the three cold first-dispatch scenarios are wall-clock (latency) rows:
// emits=0 and the time lives in wallClockMs, so they render as ms, not throughput.
const DISPATCH_SCENARIOS = [
  ["UntargetedFlood_OneHandler", 25000000.125, 0, 1000.0],
  ["UntargetedFlood_FourHandlers_OnePriority", 12000000.5, 0, 1000.0],
  ["UntargetedFlood_FourHandlers_FourPriorities", 8000000.25, 0, 1000.0],
  ["UntargetedFirstDispatch_Cold", 0, 128, 0.25],
  ["TargetedFlood_OneListener", 18000000.0, 0, 1000.0],
  ["TargetedFlood_SixteenListeners", 4000000.0, 0, 1000.0],
  ["TargetedFirstDispatch_Cold", 0, 96, 0.3],
  ["BroadcastFlood_OneHandler", 17000000.5, 0, 1000.0],
  ["BroadcastFirstDispatch_Cold", 0, 64, 0.2],
  ["InterceptorHeavy_FourInterceptors", 7000000.0, 0, 1000.0],
  ["PostProcessingHeavy_FourPostProcessors", 6000000.0, 0, 1000.0],
  ["RegistrationFlood_1000Types_FromColdBus", 0, 4096, 12.345],
  ["RegistrationFlood_1000Types_WarmJit", 0, 4096, 8.5]
];

function structuredLine(scenario, platformString, commit, emits, alloc, ms) {
  return (
    `[TestRunner] {scenario:"${scenario}", platform:"${platformString}", ` +
    `commit:"${commit}", runIndex:-1, emitsPerSec:${emits}, ` +
    `allocatedBytesDelta:${alloc}, wallClockMs:${ms}}`
  );
}

// A full set of dispatch rows for one Unity version + scope.
function unityLog(version, { commit = "abc1234", scope = "PlayMode" } = {}) {
  const platformString = platform(version, scope);
  return DISPATCH_SCENARIOS.map(([scenario, emits, alloc, ms]) =>
    structuredLine(scenario, platformString, commit, emits, alloc, ms)
  ).join("\n");
}

// Comparison rows for one scope. Each entry is [techKey, scenarioKey, emits, alloc].
// wallClockMs is fixed at 1000 so opCount == emits and bytes-per-op == alloc/emits
// is easy to reason about in assertions. Deliberately models real support gaps:
//   - only DxMessaging has PriorityOrdered / Filtered / PostProcess,
//   - UniRx does not support KeyedToOne,
//   - ZenjectSignalBus is entirely absent (its package did not resolve).
const COMPARISON_ROWS = [
  // DxMessaging: full coverage, zero alloc on the hot paths.
  ["DxMessaging", "GlobalToOne", 16980000.0, 0],
  ["DxMessaging", "GlobalToMany", 9000000.0, 0],
  ["DxMessaging", "KeyedToOne", 12000000.0, 0],
  ["DxMessaging", "PriorityOrdered", 8000000.0, 0],
  ["DxMessaging", "Filtered", 7000000.0, 0],
  ["DxMessaging", "PostProcess", 6000000.0, 0],
  ["DxMessaging", "SubUnsub", 5000000.0, 0],
  ["DxMessaging", "StructNoBox", 20000000.0, 0],
  // MessagePipe: subset, allocates on GlobalToOne (24 bytes/op at 1e6 ops).
  ["MessagePipe", "GlobalToOne", 10000000.0, 240000000],
  ["MessagePipe", "GlobalToMany", 6000000.0, 0],
  ["MessagePipe", "KeyedToOne", 8000000.0, 0],
  ["MessagePipe", "SubUnsub", 3000000.0, 0],
  ["MessagePipe", "StructNoBox", 9000000.0, 0],
  // UniRx: no KeyedToOne support -> that cell is N/A.
  ["UniRx", "GlobalToOne", 7000000.0, 0],
  ["UniRx", "GlobalToMany", 4000000.0, 0],
  ["UniRx", "SubUnsub", 2000000.0, 0],
  ["UniRx", "StructNoBox", 5000000.0, 0],
  // UnityAtoms / ScriptableObject / UnityEvent / CsEvent / UnitySendMessage:
  // minimal coverage so their rows appear.
  ["UnityAtoms", "GlobalToOne", 3000000.0, 0],
  ["ScriptableObject", "GlobalToOne", 2500000.0, 0],
  ["UnityEvent", "GlobalToOne", 4000000.0, 0],
  ["CsEvent", "GlobalToOne", 30000000.0, 0],
  ["UnitySendMessage", "GlobalToOne", 1500000.0, 480000000]
];

function comparisonLog(version, { commit = "abc1234", scope = "PlayMode" } = {}) {
  const platformString = platform(version, scope);
  return COMPARISON_ROWS.map(([techKey, scenarioKey, emits, alloc]) =>
    structuredLine(
      `Comparison_${techKey}_${scenarioKey}`,
      platformString,
      commit,
      emits,
      alloc,
      1000.0
    )
  ).join("\n");
}

// A combined log with dispatch + comparison rows for one or more scopes.
function fullLog(version, { commit = "abc1234", scopes = ["PlayMode"] } = {}) {
  return scopes
    .flatMap((scope) => [
      unityLog(version, { commit, scope }),
      comparisonLog(version, { commit, scope })
    ])
    .join("\n");
}

function extractRows(content) {
  return require("../unity/extract-perf-baseline.js").extractRows(content);
}

function blockFor(version, options = {}) {
  const rows = extractRows(fullLog(version, options));
  return buildBlock(selectRowsForVersion(rows, `Unity ${version}`), version);
}

// Run the repo's managed Prettier exactly as the markdown-json.yml gate does.
function managedPrettier(args) {
  return childProcess.spawnSync(process.execPath, [MANAGED_PRETTIER, ...args], {
    cwd: REPO_ROOT,
    encoding: "utf8",
    env: { ...process.env, DXMSG_HOOK_SKIP_INTEGRITY: "1" }
  });
}

// A seed doc that frames an AUTOGENERATED region with a Prettier-aligned
// placeholder table (mirrors docs/architecture/performance.md).
function seedDoc() {
  return [
    "# Performance Benchmarks",
    "",
    "Some intro prose.",
    "",
    BEGIN_MARKER,
    "",
    "The dispatch throughput table populates after the first default-branch CI benchmark run.",
    "",
    "| Scenario                       | Throughput / Wall clock | Allocated bytes |",
    "| ------------------------------ | ----------------------- | --------------- |",
    "| Pending first CI benchmark run | -                       | -               |",
    "",
    END_MARKER,
    "",
    "## Trailing Section",
    "",
    "Trailing prose stays intact.",
    ""
  ].join("\n");
}

describe("render-perf-doc deriveScope", () => {
  test("maps the platform string to a scope by substring, ignoring the backend token", () => {
    // Standalone carries an IL2CPP backend token; deriveScope must key off the
    // scope substring, not the backend word.
    expect(deriveScope(platform(LATEST, "Standalone"))).toBe("Standalone");
    expect(deriveScope(platform(LATEST, "PlayMode"))).toBe("PlayMode");
    expect(deriveScope(platform(LATEST, "EditMode"))).toBe("EditMode");
    expect(deriveScope("Something Unknown Mono x64")).toBeNull();
  });
});

describe("render-perf-doc scope order + per-backend labels", () => {
  test("SCOPE_ORDER is headline-first: PlayMode, then Standalone, then EditMode", () => {
    expect(SCOPE_ORDER).toEqual(["PlayMode", "Standalone", "EditMode"]);
  });

  test("SCOPE_BACKEND maps Standalone to IL2CPP and PlayMode/EditMode to Mono", () => {
    expect(SCOPE_BACKEND).toEqual({ Standalone: "IL2CPP", PlayMode: "Mono", EditMode: "Mono" });
  });

  test("scopeLabel applies the per-scope backend, with a (Mono) fallback", () => {
    expect(scopeLabel("PlayMode")).toBe("PlayMode (Mono)");
    expect(scopeLabel("Standalone")).toBe("Standalone (IL2CPP)");
    expect(scopeLabel("EditMode")).toBe("EditMode (Mono)");
    // Unknown scope falls back to (Mono) rather than throwing or printing undefined.
    expect(scopeLabel("MysteryScope")).toBe("MysteryScope (Mono)");
  });

  test("scopeLabel DERIVES the backend from the supplied platform token, not the scope name", () => {
    // The heading must follow the DATA: a Standalone row carrying a Mono backend
    // token reads "Standalone (Mono)" even though SCOPE_BACKEND maps it to IL2CPP,
    // so the heading never contradicts the Platform: line beneath it.
    expect(scopeLabel("Standalone", "Standalone IL2CPP x64 Release (Unity 6000.3.16f1)")).toBe(
      "Standalone (IL2CPP)"
    );
    expect(scopeLabel("Standalone", "Standalone Mono x64 Release (Unity 6000.3.16f1)")).toBe(
      "Standalone (Mono)"
    );
    // PlayMode carrying an IL2CPP token would follow the data too.
    expect(scopeLabel("PlayMode", "Editor PlayMode IL2CPP x64 Release")).toBe("PlayMode (IL2CPP)");
    // A platform with no backend token falls back to the SCOPE_BACKEND map.
    expect(scopeLabel("Standalone", "Standalone x64 Release")).toBe("Standalone (IL2CPP)");
    expect(scopeLabel("PlayMode", "Editor PlayMode x64 Release")).toBe("PlayMode (Mono)");
  });

  test("deriveBackendLabel reads the IL2CPP/Mono token, else null", () => {
    expect(deriveBackendLabel("Standalone IL2CPP x64")).toBe("IL2CPP");
    expect(deriveBackendLabel("Editor PlayMode Mono x64")).toBe("Mono");
    // Case-insensitive token match.
    expect(deriveBackendLabel("standalone il2cpp x64")).toBe("IL2CPP");
    // No backend token -> null so the caller can fall back to the SCOPE_BACKEND map.
    expect(deriveBackendLabel("Standalone x64 Release")).toBeNull();
    expect(deriveBackendLabel(undefined)).toBeNull();
  });
});

describe("render-perf-doc machine specs (--machine-specs provenance)", () => {
  let tempDir;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-render-specs-"));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  const SAMPLE_SPECS = {
    cpu: "AMD Ryzen 9 5950X",
    physicalCores: 16,
    logicalCores: 32,
    clockMhz: 3400,
    ramGb: 64,
    ramSpeedMhz: 3600,
    ramType: "DDR4",
    gpu: "NVIDIA RTX 3080",
    os: "Microsoft Windows 11 Pro (10.0.22631)"
  };
  const SAMPLE_SUMMARY =
    "AMD Ryzen 9 5950X, 16C/32T @ 3400MHz; 64GB DDR4@3600; NVIDIA RTX 3080; " +
    "Microsoft Windows 11 Pro (10.0.22631)";

  test("formatMachineSpecs renders the one-line Runner summary in order", () => {
    expect(formatMachineSpecs(SAMPLE_SPECS)).toBe(SAMPLE_SUMMARY);
  });

  test("formatMachineSpecs renders missing/blank fields as 'unknown', never 'undefined'", () => {
    const partial = { cpu: "Some CPU", physicalCores: 8 };
    const summary = formatMachineSpecs(partial);
    expect(summary).toContain("Some CPU");
    expect(summary).toContain("8C/unknownT");
    expect(summary).toContain("unknownGB unknown@unknown");
    expect(summary).not.toContain("undefined");
    // A blank-string field also degrades to unknown.
    expect(formatMachineSpecs({ cpu: "   " })).toContain("unknown, unknownC");
  });

  test("readMachineSpecs returns the neutral literal when no path is given", () => {
    expect(readMachineSpecs("")).toBe(NEUTRAL_RUNNER_DESCRIPTION);
    expect(NEUTRAL_RUNNER_DESCRIPTION).toBe("self-hosted Windows runner");
  });

  test("readMachineSpecs falls back to the neutral literal for a missing file (never throws)", () => {
    const missing = path.join(tempDir, "nope.json");
    expect(readMachineSpecs(missing)).toBe(NEUTRAL_RUNNER_DESCRIPTION);
  });

  test("readMachineSpecs falls back to the neutral literal for unparseable JSON", () => {
    const badPath = path.join(tempDir, "bad.json");
    fs.writeFileSync(badPath, "{ this is not json", "utf8");
    expect(readMachineSpecs(badPath)).toBe(NEUTRAL_RUNNER_DESCRIPTION);
  });

  test("readMachineSpecs parses a valid specs file into the one-line summary", () => {
    const goodPath = path.join(tempDir, "specs.json");
    fs.writeFileSync(goodPath, JSON.stringify(SAMPLE_SPECS), "utf8");
    expect(readMachineSpecs(goodPath)).toBe(SAMPLE_SUMMARY);
  });

  test("render embeds the Runner provenance line as a NON-table line (ignored by idempotence)", () => {
    const docPath = path.join(tempDir, "performance.md");
    const inputPath = path.join(tempDir, "unity.log");
    const specsPath = path.join(tempDir, "specs.json");
    fs.writeFileSync(docPath, seedDoc(), "utf8");
    fs.writeFileSync(inputPath, fullLog(LATEST), "utf8");
    fs.writeFileSync(specsPath, JSON.stringify(SAMPLE_SPECS), "utf8");

    const result = render({
      inputs: [inputPath],
      doc: docPath,
      unityVersion: LATEST,
      platformSubstring: "",
      tolerance: 0.02,
      machineSpecs: specsPath
    });
    expect(result.changed).toBe(true);
    expect(result.content).toContain(`Runner: ${SAMPLE_SUMMARY}`);
    fs.writeFileSync(docPath, result.content, "utf8");

    // A second render with DIFFERENT machine specs but identical numbers must be a
    // no-op: the Runner line is not a table row, so it cannot trigger churn.
    fs.writeFileSync(
      specsPath,
      JSON.stringify({ ...SAMPLE_SPECS, cpu: "Intel Core i9-13900K", ramType: "DDR5" }),
      "utf8"
    );
    const second = render({
      inputs: [inputPath],
      doc: docPath,
      unityVersion: LATEST,
      platformSubstring: "",
      tolerance: 0.02,
      machineSpecs: specsPath
    });
    expect(second.changed).toBe(false);
    expect(second.content).toBe(result.content);
    // The first machine's Runner line is still present (block unchanged).
    expect(second.content).toContain(`Runner: ${SAMPLE_SUMMARY}`);
  });

  test("render falls back to the neutral literal when --machine-specs is missing, never 'ELI-MACHINE'", () => {
    const docPath = path.join(tempDir, "performance.md");
    const inputPath = path.join(tempDir, "unity.log");
    fs.writeFileSync(docPath, seedDoc(), "utf8");
    fs.writeFileSync(inputPath, fullLog(LATEST), "utf8");

    const result = render({
      inputs: [inputPath],
      doc: docPath,
      unityVersion: LATEST,
      platformSubstring: "",
      tolerance: 0.02,
      machineSpecs: path.join(tempDir, "does-not-exist.json")
    });
    expect(result.changed).toBe(true);
    expect(result.content).toContain("Runner: self-hosted Windows runner");
    expect(result.content).not.toContain("ELI-MACHINE");
  });

  test("render uses the neutral Runner literal when --machine-specs is not provided", () => {
    const docPath = path.join(tempDir, "performance.md");
    const inputPath = path.join(tempDir, "unity.log");
    fs.writeFileSync(docPath, seedDoc(), "utf8");
    fs.writeFileSync(inputPath, fullLog(LATEST), "utf8");

    // The flag being ABSENT (options.machineSpecs undefined) still yields the
    // neutral literal -- never a host name, never ELI-MACHINE.
    const result = render({
      inputs: [inputPath],
      doc: docPath,
      unityVersion: LATEST,
      platformSubstring: "",
      tolerance: 0.02
    });
    expect(result.changed).toBe(true);
    expect(result.content).toContain("Runner: self-hosted Windows runner");
    expect(result.content).not.toContain("ELI-MACHINE");
  });
});

describe("render-perf-doc parseComparisonScenario", () => {
  test("splits Comparison_<TechKey>_<ScenarioKey> on the first underscore", () => {
    expect(parseComparisonScenario("Comparison_DxMessaging_GlobalToOne")).toEqual({
      techKey: "DxMessaging",
      scenarioKey: "GlobalToOne"
    });
    expect(parseComparisonScenario("Comparison_MessagePipe_Filtered")).toEqual({
      techKey: "MessagePipe",
      scenarioKey: "Filtered"
    });
    // Unknown scenario key is rejected.
    expect(parseComparisonScenario("Comparison_DxMessaging_NotAScenario")).toBeNull();
    // Dispatch rows are not comparison rows.
    expect(parseComparisonScenario("UntargetedFlood_OneHandler")).toBeNull();
  });
});

describe("render-perf-doc selectRowsForVersion", () => {
  test("filters to the requested Unity version and groups dispatch rows by scope", () => {
    const rows = extractRows([unityLog(LATEST), unityLog(OLDER)].join("\n"));
    const selected = selectRowsForVersion(rows, `Unity ${LATEST}`);
    expect(selected.scopesPresent).toEqual(["PlayMode"]);
    const playMode = selected.dispatchByScope.get("PlayMode");
    expect(playMode.size).toBe(13);
    for (const row of playMode.values()) {
      expect(row.platform).toContain(LATEST);
      expect(row.platform).not.toContain(OLDER);
    }
  });

  // Replaces the old "prefers the Editor platform" test: scopes are now kept
  // SEPARATE and ordered headline-first (PlayMode Mono is the shipped runtime, so
  // it leads; then Standalone IL2CPP for AOT coverage; then EditMode).
  test("keeps per-scope dispatch groups in headline order (PlayMode first)", () => {
    const rows = extractRows(
      [
        unityLog(LATEST, { scope: "EditMode" }),
        unityLog(LATEST, { scope: "Standalone" }),
        unityLog(LATEST, { scope: "PlayMode" })
      ].join("\n")
    );
    const selected = selectRowsForVersion(rows, `Unity ${LATEST}`);
    expect(selected.scopesPresent).toEqual(["PlayMode", "Standalone", "EditMode"]);
  });
});

describe("render-perf-doc alignTable (Prettier-aligned output)", () => {
  test("pads every column to max(3, widest cell) like Prettier GFM tables", () => {
    const table = alignTable([
      ["A", "Long header here", "C"],
      ["x", "y", "zzzzzzzzzz"]
    ]);
    expect(table).toBe(
      [
        "| A   | Long header here | C          |",
        "| --- | ---------------- | ---------- |",
        "| x   | y                | zzzzzzzzzz |"
      ].join("\n")
    );
  });

  test("buildDispatchTable emits aligned pipes, not compact `| --- |`", () => {
    const rows = extractRows(unityLog(LATEST));
    const selected = selectRowsForVersion(rows, `Unity ${LATEST}`);
    const table = buildDispatchTable(selected.dispatchByScope.get("PlayMode"));
    const lines = table.split("\n");
    // Separator row is filled with dashes to the column width (> 3), never the
    // compact 3-dash form that the required Prettier gate would reflow.
    expect(lines[1]).toMatch(/^\| -{4,} \| -+ \| -+ \|$/);
    expect(lines[1]).not.toBe("| --- | --- | --- |");
    // Header cells are space-padded so the pipes line up.
    expect(lines[0]).toContain("| Scenario");
  });
});

describe("render-perf-doc formatBytesPerOp", () => {
  test("computes allocatedBytesDelta / opCount with thousands separators", () => {
    // opCount = emitsPerSecond * wallClockMs / 1000 = 1e6 * 1000 / 1000 = 1e6.
    // 240,000,000 bytes / 1e6 ops = 240 bytes/op.
    expect(
      formatBytesPerOp({
        emitsPerSecond: "1000000",
        wallClockMs: "1000",
        allocatedBytesDelta: "240000000"
      })
    ).toBe("240");
    // Genuinely-zero allocation renders "0".
    expect(
      formatBytesPerOp({ emitsPerSecond: "1000000", wallClockMs: "1000", allocatedBytesDelta: "0" })
    ).toBe("0");
    // Thousands separators on large per-op values.
    expect(
      formatBytesPerOp({
        emitsPerSecond: "1000",
        wallClockMs: "1000",
        allocatedBytesDelta: "2000000"
      })
    ).toBe("2,000");
    // Div-by-zero guard: zero opCount -> "0".
    expect(
      formatBytesPerOp({ emitsPerSecond: "0", wallClockMs: "1000", allocatedBytesDelta: "5000" })
    ).toBe("0");
  });
});

describe("render-perf-doc buildBlock dispatch tables", () => {
  test("renders throughput rows and a wall-clock registration row in stable order", () => {
    const block = blockFor(LATEST);

    expect(block.startsWith(BEGIN_MARKER)).toBe(true);
    expect(block.endsWith(END_MARKER)).toBe(true);
    expect(block).toContain(`Unity ${LATEST}`);
    expect(block).toContain("`abc1234`");
    expect(block).toContain("25.00 M emits/sec");
    expect(block).toContain("12.345 ms");
    expect(block).toContain("4,096 B");

    // DisplayNames are shown, NOT the raw scenario keys.
    expect(block).toContain("Untargeted Flood (One Handler)");
    expect(block).toContain("Registration Flood (1000 Types, Cold Bus)");
    expect(block).not.toMatch(/\|\s*UntargetedFlood_OneHandler\s*\|/);

    const firstIndex = block.indexOf("Untargeted Flood (One Handler)");
    const lastIndex = block.indexOf("Registration Flood (1000 Types, Cold Bus)");
    expect(firstIndex).toBeGreaterThan(0);
    expect(lastIndex).toBeGreaterThan(firstIndex);
  });

  test("renders the four cold/warm-JIT latency rows as wall-clock ms, not throughput", () => {
    const block = blockFor(LATEST);

    const escapeRegex = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

    // The display names appear, with their wall-clock ms values (never an emits/sec value).
    const coldRows = [
      ["Untargeted First Dispatch (Cold, Distinct Types)", "0.250 ms"],
      ["Targeted First Dispatch (Cold, Distinct Types)", "0.300 ms"],
      ["Broadcast First Dispatch (Cold, Distinct Types)", "0.200 ms"],
      ["Registration Flood (1000 Types, Warm JIT)", "8.500 ms"]
    ];
    for (const [label, ms] of coldRows) {
      // Match the rendered table row by its trimmed display-name cell and assert the
      // primary cell is the wall-clock ms value (alignment padding is incidental).
      const rowPattern = new RegExp(
        `\\|\\s*${escapeRegex(label)}\\s*\\|\\s*${escapeRegex(ms)}\\s*\\|`
      );
      expect(block).toMatch(rowPattern);
      // No throughput unit is attached to these latency rows.
      const throughputLeak = new RegExp(
        `\\|\\s*${escapeRegex(label)}\\s*\\|\\s*[\\d.]+ M emits/sec`
      );
      expect(block).not.toMatch(throughputLeak);
    }
  });

  test("renders one dispatch section per scope present, PlayMode (Mono) then Standalone (IL2CPP)", () => {
    const block = blockFor(LATEST, { scopes: ["PlayMode", "Standalone"] });
    const playModeLabel = "#### Dispatch throughput - PlayMode (Mono)";
    const standaloneLabel = "#### Dispatch throughput - Standalone (IL2CPP)";
    expect(block).toContain(playModeLabel);
    expect(block).toContain(standaloneLabel);
    // Headline order: PlayMode (shipped Mono runtime) precedes Standalone (IL2CPP).
    expect(block.indexOf(playModeLabel)).toBeLessThan(block.indexOf(standaloneLabel));
    // Each dispatch section carries its own Platform line (with its real backend).
    expect(block).toContain(`Platform: ${platform(LATEST, "PlayMode")}.`);
    expect(block).toContain(`Platform: ${platform(LATEST, "Standalone")}.`);
    // The Standalone platform string carries the IL2CPP backend token.
    expect(block).toContain("Standalone IL2CPP x64 Release");
  });

  // Fix-3 GUARD: the per-scope heading backend follows the platform token in the
  // scope's rows, NOT the scope name. A Standalone row carrying IL2CPP renders
  // "Standalone (IL2CPP)"; the SAME scope carrying a Mono token renders
  // "Standalone (Mono)" so the heading can never contradict the Platform: line.
  test("a Standalone row carrying IL2CPP renders 'Standalone (IL2CPP)'", () => {
    const block = blockFor(LATEST, { scopes: ["Standalone"] });
    expect(block).toContain("#### Dispatch throughput - Standalone (IL2CPP)");
    expect(block).toContain("Platform: Standalone IL2CPP x64 Release");
    expect(block).not.toContain("#### Dispatch throughput - Standalone (Mono)");
  });

  test("a Standalone row carrying Mono renders 'Standalone (Mono)' (heading follows the data)", () => {
    // Flip the backend token on the Standalone rows; the heading must follow it.
    const monoStandalone = fullLog(LATEST, { scopes: ["Standalone"] }).replace(
      /Standalone IL2CPP/g,
      "Standalone Mono"
    );
    const block = buildBlock(
      selectRowsForVersion(extractRows(monoStandalone), `Unity ${LATEST}`),
      LATEST
    );
    expect(block).toContain("#### Dispatch throughput - Standalone (Mono)");
    expect(block).toContain("Platform: Standalone Mono x64 Release");
    expect(block).not.toContain("#### Dispatch throughput - Standalone (IL2CPP)");
    // The comparison matrices for this (single) scope follow the data too.
    expect(block).toContain("#### Library comparison - throughput (Standalone (Mono))");
    expect(block).not.toContain("#### Library comparison - throughput (Standalone (IL2CPP))");
  });

  // ATTRIBUTION GUARD: the in-marker provenance comment must point at the
  // workflow that regenerates and commits the doc -- perf-numbers.yml.
  test("emits a provenance comment that references perf-numbers.yml, not unity-benchmarks.yml", () => {
    const block = blockFor(LATEST);
    expect(block).toContain("See perf-numbers.yml.");
    expect(block).not.toContain("unity-benchmarks.yml");
  });
});

describe("render-perf-doc buildBlock comparison matrices", () => {
  test("renders throughput + bytes-per-op matrices with correct tech rows and scenario columns", () => {
    const block = blockFor(LATEST);

    expect(block).toContain("#### Library comparison - throughput (PlayMode (Mono))");
    expect(block).toContain(
      "#### Library comparison - allocations, bytes per op (PlayMode (Mono))"
    );

    // Column headers use the human comparison-scenario labels.
    expect(block).toContain("Global -> 1 subscriber");
    expect(block).toContain("Priority-ordered dispatch");
    expect(block).toContain("Struct message (zero-copy)");

    // Tech rows use the human tech labels; first column header is "Technology".
    expect(block).toContain("| Technology");
    expect(block).toContain("DxMessaging");
    expect(block).toContain("MessagePipe");
    expect(block).toContain("UniRx MessageBroker");
    expect(block).toContain("C# event");
    expect(block).toContain("Unity SendMessage");

    // DxMessaging GlobalToOne throughput cell.
    expect(block).toContain("16.98 M emits/sec");
  });

  test("shows N/A for unsupported (tech, scenario) cells and omits fully-absent techs", () => {
    const block = blockFor(LATEST);

    // ZenjectSignalBus has no rows at all -> its row is omitted entirely.
    expect(block).not.toContain("Zenject SignalBus");

    // Only DxMessaging has PriorityOrdered / Filtered / PostProcess. Find the
    // throughput matrix and confirm non-DxMessaging rows show N/A in those cells.
    const throughputRows = matrixRows(block, "throughput (PlayMode (Mono))");
    const header = throughputRows[0];
    const priorityCol = header.indexOf("Priority-ordered dispatch");
    const keyedCol = header.indexOf("Keyed/targeted -> 1 of many");
    expect(priorityCol).toBeGreaterThan(0);
    expect(keyedCol).toBeGreaterThan(0);

    const messagePipeRow = throughputRows.find((cells) => cells[0] === "MessagePipe");
    expect(messagePipeRow[priorityCol]).toBe("N/A");
    const uniRxRow = throughputRows.find((cells) => cells[0] === "UniRx MessageBroker");
    // UniRx does not support KeyedToOne.
    expect(uniRxRow[keyedCol]).toBe("N/A");
    // DxMessaging does support PriorityOrdered.
    const dxRow = throughputRows.find((cells) => cells[0] === "DxMessaging");
    expect(dxRow[priorityCol]).not.toBe("N/A");
  });

  test("computes bytes-per-op cells correctly, including a genuine zero", () => {
    const block = blockFor(LATEST);
    const allocRows = matrixRows(block, "bytes per op (PlayMode (Mono))");
    const header = allocRows[0];
    const globalToOneCol = header.indexOf("Global -> 1 subscriber");

    // MessagePipe GlobalToOne: 240,000,000 bytes / (1e7 * 1000 / 1000 = 1e7) = 24.
    const messagePipeRow = allocRows.find((cells) => cells[0] === "MessagePipe");
    expect(messagePipeRow[globalToOneCol]).toBe("24");

    // UnitySendMessage GlobalToOne: 480,000,000 / 1.5e6 = 320.
    const sendMessageRow = allocRows.find((cells) => cells[0] === "Unity SendMessage");
    expect(sendMessageRow[globalToOneCol]).toBe("320");

    // DxMessaging GlobalToOne is genuinely zero-alloc.
    const dxRow = allocRows.find((cells) => cells[0] === "DxMessaging");
    expect(dxRow[globalToOneCol]).toBe("0");
  });

  test("comparison matrices use the headline scope (PlayMode over Standalone)", () => {
    const block = blockFor(LATEST, { scopes: ["PlayMode", "Standalone"] });
    expect(block).toContain("#### Library comparison - throughput (PlayMode (Mono))");
    expect(block).not.toContain("#### Library comparison - throughput (Standalone (IL2CPP))");
  });

  test("omits the comparison matrices entirely when no comparison rows exist", () => {
    const rows = extractRows(unityLog(LATEST));
    const block = buildBlock(selectRowsForVersion(rows, `Unity ${LATEST}`), LATEST);
    expect(block).not.toContain("#### Library comparison");
  });
});

// CRITICAL-1 REGRESSION GUARD: the prior renderer emitted compact `| --- |`
// pipes that the repo's REQUIRED markdown prettier gate reflows. These tests
// fail if the rendered output is not already Prettier-clean.
describe("render-perf-doc Prettier parity (CRITICAL-1)", () => {
  let tempDir;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-render-prettier-"));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  test("a freshly rendered doc (multi-scope + matrices) passes managed Prettier --check unchanged", () => {
    const docPath = path.join(tempDir, "performance.md");
    const inputPath = path.join(tempDir, "unity.log");
    fs.writeFileSync(docPath, seedDoc(), "utf8");
    fs.writeFileSync(inputPath, fullLog(LATEST, { scopes: ["PlayMode", "Standalone"] }), "utf8");

    const result = render({
      inputs: [inputPath],
      doc: docPath,
      unityVersion: LATEST,
      platformSubstring: "",
      tolerance: 0.02
    });
    expect(result.changed).toBe(true);
    fs.writeFileSync(docPath, result.content, "utf8");

    const before = fs.readFileSync(docPath, "utf8");
    const write = managedPrettier(["--write", docPath]);
    expect(write.status).toBe(0);
    // Prettier left the rendered tables byte-for-byte unchanged.
    expect(fs.readFileSync(docPath, "utf8")).toBe(before);

    const check = managedPrettier(["--check", docPath]);
    expect(check.status).toBe(0);
  });

  test("the committed performance.md passes managed Prettier --check", () => {
    // The committed doc must ship Prettier-clean AND contain exactly one marker
    // pair so the renderer can operate on it.
    const content = fs.readFileSync(COMMITTED_DOC, "utf8");
    expect(content.split(BEGIN_MARKER)).toHaveLength(2);
    expect(content.split(END_MARKER)).toHaveLength(2);

    const check = managedPrettier(["--check", COMMITTED_DOC]);
    expect(check.status).toBe(0);
  });
});

describe("render-perf-doc blocksEquivalent (idempotence)", () => {
  test("treats jitter within tolerance as unchanged across tables and matrices", () => {
    const base = blockFor(LATEST);
    // 25000000.125 -> 25.00 M; +1% jitter keeps the rounded display identical
    // and stays within the 2% tolerance. Jitter a comparison cell too.
    const jitter = fullLog(LATEST)
      .replace("25000000.125", "25250000.0")
      .replace("16980000.0", "17100000.0");
    const jittered = buildBlock(
      selectRowsForVersion(extractRows(jitter), `Unity ${LATEST}`),
      LATEST
    );
    expect(blocksEquivalent(base, jittered, 0.02)).toBe(true);
  });

  test("treats a real regression beyond tolerance as changed", () => {
    const base = blockFor(LATEST);
    const regressed = fullLog(LATEST).replace("25000000.125", "12000000.0");
    const regressedBlock = buildBlock(
      selectRowsForVersion(extractRows(regressed), `Unity ${LATEST}`),
      LATEST
    );
    expect(blocksEquivalent(base, regressedBlock, 0.02)).toBe(false);
  });

  test("a regression in a comparison cell beyond tolerance is a change", () => {
    const base = blockFor(LATEST);
    // Halve a DxMessaging comparison throughput -> well beyond 2%.
    const regressed = fullLog(LATEST).replace(
      'Comparison_DxMessaging_GlobalToOne", platform:"' +
        platform(LATEST, "PlayMode") +
        '", commit:"abc1234", runIndex:-1, emitsPerSec:16980000',
      'Comparison_DxMessaging_GlobalToOne", platform:"' +
        platform(LATEST, "PlayMode") +
        '", commit:"abc1234", runIndex:-1, emitsPerSec:8000000'
    );
    const regressedBlock = buildBlock(
      selectRowsForVersion(extractRows(regressed), `Unity ${LATEST}`),
      LATEST
    );
    expect(blocksEquivalent(base, regressedBlock, 0.02)).toBe(false);
  });

  // CRITICAL-2a: a changed commit SHA with in-tolerance numbers is NOT a change
  // (the provenance line is not a table row).
  test("a changed commit hash with in-tolerance numbers is NOT a change", () => {
    const base = blockFor(LATEST, { commit: "0000aaa" });
    const sameNumbersNewCommit = blockFor(LATEST, { commit: "ffff999" });
    expect(sameNumbersNewCommit).not.toBe(base); // provenance line differs
    expect(blocksEquivalent(base, sameNumbersNewCommit, 0.02)).toBe(true);
  });

  // CRITICAL-2b: equivalence must be insensitive to Prettier's column alignment.
  test("compact-pipe and Prettier-aligned blocks are equivalent", () => {
    const aligned = blockFor(LATEST);
    const compact = aligned
      .split("\n")
      .map((line) =>
        line.startsWith("|")
          ? "| " +
            line
              .replace(/^\|/, "")
              .replace(/\|$/, "")
              .split("|")
              .map((cell) => {
                const trimmed = cell.trim();
                return /^-+$/.test(trimmed) ? "---" : trimmed;
              })
              .join(" | ") +
            " |"
          : line
      )
      .join("\n");
    expect(compact).not.toBe(aligned);
    expect(blocksEquivalent(aligned, compact, 0.02)).toBe(true);
  });
});

describe("render-perf-doc render (doc rewrite)", () => {
  let tempDir;
  let docPath;
  let inputPath;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-render-perf-"));
    docPath = path.join(tempDir, "performance.md");
    inputPath = path.join(tempDir, "unity.log");
    fs.writeFileSync(docPath, seedDoc(), "utf8");
    fs.writeFileSync(inputPath, fullLog(LATEST, { scopes: ["PlayMode", "Standalone"] }), "utf8");
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  function renderDoc(overrides = {}) {
    return render({
      inputs: [inputPath],
      doc: docPath,
      unityVersion: LATEST,
      platformSubstring: "",
      tolerance: 0.02,
      ...overrides
    });
  }

  test("replaces the placeholder block and preserves surrounding sections", () => {
    const result = renderDoc();
    expect(result.changed).toBe(true);
    expect(result.content).toContain("# Performance Benchmarks");
    expect(result.content).toContain("## Trailing Section");
    expect(result.content).toContain("Trailing prose stays intact.");
    expect(result.content).toContain("25.00 M emits/sec");
    expect(result.content).toContain("#### Dispatch throughput - PlayMode (Mono)");
    expect(result.content).toContain("#### Dispatch throughput - Standalone (IL2CPP)");
    expect(result.content).toContain("#### Library comparison - throughput (PlayMode (Mono))");
    expect(result.content).not.toContain("Pending first CI benchmark run");
    expect(result.content.split(BEGIN_MARKER)).toHaveLength(2);
    expect(result.content.split(END_MARKER)).toHaveLength(2);
  });

  test("is idempotent: a second render of the same numbers is a byte-identical no-op", () => {
    const first = renderDoc();
    fs.writeFileSync(docPath, first.content, "utf8");

    const second = renderDoc();
    expect(second.changed).toBe(false);
    expect(second.content).toBe(first.content);
  });

  // CRITICAL-2a end-to-end: a later run with a DIFFERENT commit but in-tolerance
  // numbers must NOT rewrite the doc (no churn PR).
  test("does not rewrite when only the commit changed and numbers are in tolerance", () => {
    const first = renderDoc();
    fs.writeFileSync(docPath, first.content, "utf8");

    fs.writeFileSync(
      inputPath,
      fullLog(LATEST, { commit: "deadbee", scopes: ["PlayMode", "Standalone"] }).replace(
        "25000000.125",
        "25200000.0"
      ),
      "utf8"
    );
    const second = renderDoc();
    expect(second.changed).toBe(false);
    expect(second.content).toBe(first.content);
  });

  test("rewrites (and refreshes the commit) on a real regression", () => {
    const first = renderDoc();
    fs.writeFileSync(docPath, first.content, "utf8");

    fs.writeFileSync(
      inputPath,
      fullLog(LATEST, { commit: "cafef00", scopes: ["PlayMode", "Standalone"] }).replace(
        /25000000\.125/g,
        "12000000.0"
      ),
      "utf8"
    );
    const second = renderDoc();
    expect(second.changed).toBe(true);
    expect(second.content).toContain("`cafef00`");
    // The regressed first-scenario row now reads 12.00 M emits/sec (alignment
    // padding is incidental, so match the row by its trimmed display-name cell).
    expect(second.content).toMatch(
      /\|\s*Untargeted Flood \(One Handler\)\s*\|\s*12\.00 M emits\/sec\s*\|\s*0 B\s*\|/
    );
  });

  // LOW-1 REGRESSION GUARD: duplicate markers previously caused a silent partial
  // rewrite. Now the renderer refuses.
  test("throws when the doc contains more than one marker pair", () => {
    const doubled = seedDoc() + "\n" + [BEGIN_MARKER, "stray", END_MARKER].join("\n") + "\n";
    fs.writeFileSync(docPath, doubled, "utf8");
    expect(() => renderDoc()).toThrow(/exactly one AUTOGENERATED dispatch-throughput marker pair/);
  });

  test("throws when the doc is missing the markers entirely", () => {
    fs.writeFileSync(docPath, "# No markers here\n\nbody\n", "utf8");
    expect(() => renderDoc()).toThrow(/Could not find AUTOGENERATED dispatch-throughput markers/);
  });
});

describe("render-perf-doc CLI", () => {
  let tempDir;
  let docPath;
  let inputPath;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-render-perf-cli-"));
    docPath = path.join(tempDir, "performance.md");
    inputPath = path.join(tempDir, "unity.log");
    fs.writeFileSync(docPath, seedDoc(), "utf8");
    fs.writeFileSync(inputPath, fullLog(LATEST, { scopes: ["PlayMode", "Standalone"] }), "utf8");
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  test("writes the doc and exits 0", () => {
    const result = childProcess.spawnSync(
      process.execPath,
      [SCRIPT, "--input", inputPath, "--doc", docPath, "--unity-version", LATEST],
      { encoding: "utf8" }
    );
    expect(result.status).toBe(0);
    expect(fs.readFileSync(docPath, "utf8")).toContain("25.00 M emits/sec");
  });

  test("a CLI-written doc passes managed Prettier --check (no compact-pipe drift)", () => {
    const write = childProcess.spawnSync(
      process.execPath,
      [SCRIPT, "--input", inputPath, "--doc", docPath, "--unity-version", LATEST],
      { encoding: "utf8" }
    );
    expect(write.status).toBe(0);
    const check = managedPrettier(["--check", docPath]);
    expect(check.status).toBe(0);
  });

  test("--check exits 3 when the doc would change and 0 when current", () => {
    const drift = childProcess.spawnSync(
      process.execPath,
      [SCRIPT, "--input", inputPath, "--doc", docPath, "--unity-version", LATEST, "--check"],
      { encoding: "utf8" }
    );
    expect(drift.status).toBe(3);

    childProcess.spawnSync(
      process.execPath,
      [SCRIPT, "--input", inputPath, "--doc", docPath, "--unity-version", LATEST],
      { encoding: "utf8" }
    );
    const current = childProcess.spawnSync(
      process.execPath,
      [SCRIPT, "--input", inputPath, "--doc", docPath, "--unity-version", LATEST, "--check"],
      { encoding: "utf8" }
    );
    expect(current.status).toBe(0);
  });

  test("fails when no rows match the requested Unity version", () => {
    const result = childProcess.spawnSync(
      process.execPath,
      [SCRIPT, "--input", inputPath, "--doc", docPath, "--unity-version", "9999.9.9f9"],
      { encoding: "utf8" }
    );
    expect(result.status).toBe(1);
    expect(result.stderr).toContain("No DispatchThroughputBenchmarks rows matched");
  });
});

// Parse the table rows of a named matrix (the table immediately following the
// "#### Library comparison - <fragment>" heading) into trimmed-cell arrays,
// skipping the separator row.
function matrixRows(block, headingFragment) {
  const lines = block.split("\n");
  const headingIndex = lines.findIndex(
    (line) => line.startsWith("#### Library comparison") && line.includes(headingFragment)
  );
  if (headingIndex < 0) {
    throw new Error(`Matrix heading not found for fragment: ${headingFragment}`);
  }
  const rows = [];
  for (let index = headingIndex + 1; index < lines.length; index++) {
    const line = lines[index].trim();
    if (!line.startsWith("|")) {
      if (rows.length > 0) {
        break;
      }
      continue;
    }
    const cells = line
      .replace(/^\|/, "")
      .replace(/\|$/, "")
      .split("|")
      .map((cell) => cell.trim());
    if (cells.every((cell) => /^:?-+:?$/.test(cell))) {
      continue;
    }
    rows.push(cells);
  }
  return rows;
}
