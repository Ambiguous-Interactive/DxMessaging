"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

const {
  BEGIN_MARKER,
  END_MARKER,
  deriveScope,
  parseComparisonScenario,
  selectRowsForVersion,
  alignTable,
  buildDispatchTable,
  buildBlock,
  formatBytesPerOp,
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
// scripting backend, arch, build config, and "(...; Unity <version>)").
function platform(version, scope = "PlayMode") {
  const target = scope === "Standalone" ? "Standalone" : `Editor ${scope}`;
  return `${target} Mono x64 Release (LinuxEditor; Unity ${version})`;
}

// The nine dispatch scenarios (emits, alloc, ms). Registration reports wall-clock.
const DISPATCH_SCENARIOS = [
  ["UntargetedFlood_OneHandler", 25000000.125, 0, 1000.0],
  ["UntargetedFlood_FourHandlers_OnePriority", 12000000.5, 0, 1000.0],
  ["UntargetedFlood_FourHandlers_FourPriorities", 8000000.25, 0, 1000.0],
  ["TargetedFlood_OneListener", 18000000.0, 0, 1000.0],
  ["TargetedFlood_SixteenListeners", 4000000.0, 0, 1000.0],
  ["BroadcastFlood_OneHandler", 17000000.5, 0, 1000.0],
  ["InterceptorHeavy_FourInterceptors", 7000000.0, 0, 1000.0],
  ["PostProcessingHeavy_FourPostProcessors", 6000000.0, 0, 1000.0],
  ["RegistrationFlood_1000Types_FromColdBus", 0, 4096, 12.345]
];

function structuredLine(scenario, platformString, commit, emits, alloc, ms) {
  return (
    `[TestRunner] {scenario:"${scenario}", platform:"${platformString}", ` +
    `commit:"${commit}", runIndex:-1, emitsPerSec:${emits}, ` +
    `allocatedBytesDelta:${alloc}, wallClockMs:${ms}}`
  );
}

// A full set of nine dispatch rows for one Unity version + scope.
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
  test("maps the leading platform token to a scope, player-fidelity first", () => {
    expect(deriveScope(platform(LATEST, "Standalone"))).toBe("Standalone");
    expect(deriveScope(platform(LATEST, "PlayMode"))).toBe("PlayMode");
    expect(deriveScope(platform(LATEST, "EditMode"))).toBe("EditMode");
    expect(deriveScope("Something Unknown Mono x64")).toBeNull();
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
    expect(playMode.size).toBe(9);
    for (const row of playMode.values()) {
      expect(row.platform).toContain(LATEST);
      expect(row.platform).not.toContain(OLDER);
    }
  });

  // Replaces the old "prefers the Editor platform" test: scopes are now kept
  // SEPARATE and ordered by player fidelity (Standalone, PlayMode, EditMode).
  test("keeps per-scope dispatch groups in player-fidelity order", () => {
    const rows = extractRows(
      [
        unityLog(LATEST, { scope: "EditMode" }),
        unityLog(LATEST, { scope: "Standalone" }),
        unityLog(LATEST, { scope: "PlayMode" })
      ].join("\n")
    );
    const selected = selectRowsForVersion(rows, `Unity ${LATEST}`);
    expect(selected.scopesPresent).toEqual(["Standalone", "PlayMode", "EditMode"]);
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

  test("renders one dispatch section per scope present, Standalone then PlayMode", () => {
    const block = blockFor(LATEST, { scopes: ["PlayMode", "Standalone"] });
    const standaloneLabel = "#### Dispatch throughput - Standalone (Mono)";
    const playModeLabel = "#### Dispatch throughput - PlayMode (Mono)";
    expect(block).toContain(standaloneLabel);
    expect(block).toContain(playModeLabel);
    // Player-fidelity order: Standalone section precedes PlayMode section.
    expect(block.indexOf(standaloneLabel)).toBeLessThan(block.indexOf(playModeLabel));
    // Each dispatch section carries its own Platform line.
    expect(block).toContain(`Platform: ${platform(LATEST, "Standalone")}.`);
    expect(block).toContain(`Platform: ${platform(LATEST, "PlayMode")}.`);
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

  test("comparison matrices use the most player-faithful scope (Standalone over PlayMode)", () => {
    const block = blockFor(LATEST, { scopes: ["PlayMode", "Standalone"] });
    expect(block).toContain("#### Library comparison - throughput (Standalone (Mono))");
    expect(block).not.toContain("#### Library comparison - throughput (PlayMode (Mono))");
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
    expect(result.content).toContain("#### Dispatch throughput - Standalone (Mono)");
    expect(result.content).toContain("#### Library comparison - throughput (Standalone (Mono))");
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
