"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const test = require("node:test");
const PERF_TEST_VECTORS = require("./perf-test-vectors.json");
const {
  sealBundle,
  writeBundleManifest,
  replayBundle
} = require("../unity/perf-evidence-bundle.js");
const { reducePairedThroughputScreen } = require("../unity/perf-evidence-reducers.js");

const {
  manifestSha256,
  parseArgs,
  reducePairedBracket,
  validateManifest
} = require("../unity/reduce-paired-bracket.js");

test("manifest-only validation has an explicit CLI shape", () => {
  const options = parseArgs(["node", "script", "--validate-manifest", "--manifest", "gate.json"]);
  assert.equal(options.validateManifest, true);
  assert.equal(options.manifest, "gate.json");
  assert.throws(
    () => parseArgs(["node", "script", "--validateManifest", "true"]),
    /Unknown option/
  );
});

const TARGET = "Filtered";
const AFFECTED = "FilteredPostProcess";
const DEFAULT_ROWS = PERF_TEST_VECTORS.defaultRows;
const DEFAULT_FACTORS = Object.fromEntries(DEFAULT_ROWS.map((row) => [row.scenario, 1]));
DEFAULT_FACTORS[TARGET] = 1.06;
DEFAULT_FACTORS[AFFECTED] = 0.99;
const COMMITS = [
  "98b47536a0eb1445fcd2a9700899aab0be24897f",
  "261b1867e052723517db8a77048c209a20108204",
  "da5439ac21e60d5e04e4f453e3766e03daff0486"
];
const OUTER_TREE = "a".repeat(40);
const CENTER_TREE = "b".repeat(40);
const OUTER_CANDIDATE_SOURCE = "c".repeat(64);
const CENTER_CANDIDATE_SOURCE = "d".repeat(64);

function makeManifest(orientation = "candidate-control-candidate", rows = DEFAULT_ROWS) {
  return {
    ...structuredClone(PERF_TEST_VECTORS.manifestDefaults),
    orientation,
    rows
  };
}

function encodeManifest(manifest) {
  return Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`);
}

function makeCycleRatios(headline, spread) {
  const halfRange = Math.sqrt(1 + spread / 100);
  return [headline / halfRange, headline / halfRange, headline * halfRange, headline * halfRange];
}

function makeSummary(
  manifestBytes,
  manifest,
  ratios,
  spreads = {},
  {
    commit = COMMITS[0],
    sourceTree = OUTER_TREE,
    candidateSourceSha256 = OUTER_CANDIDATE_SOURCE
  } = {}
) {
  return {
    schemaVersion: 2,
    platform: "Standalone IL2CPP x64 Release (WindowsPlayer; Unity 6000.5.2f1)",
    commit,
    sourceTree,
    candidateSourceSha256,
    bracketManifestSha256: manifestSha256(manifestBytes),
    executionProfile: structuredClone(PERF_TEST_VECTORS.executionProfile),
    ...PERF_TEST_VECTORS.protocol,
    rows: manifest.rows.map((row) => {
      const headline = ratios[row.scenario];
      const spread = spreads[row.scenario] ?? 1;
      return {
        scenario: row.scenario,
        firstToSecondRatio: headline,
        aggregateRateRatio: headline,
        cycleRatioSpreadPercent: spread,
        withinMaterialityBand: spread <= 3,
        cycleRatios: makeCycleRatios(headline, spread)
      };
    })
  };
}

function makeBracket({
  orientation = "candidate-control-candidate",
  factors = {},
  spreads = {},
  outerScales = {}
} = {}) {
  const manifest = makeManifest(orientation);
  const manifestBytes = encodeManifest(manifest);
  const resolvedFactors = { ...DEFAULT_FACTORS, ...factors };
  const firstRatios = {};
  const centerRatios = {};
  const lastRatios = {};
  for (const row of manifest.rows) {
    const factor = resolvedFactors[row.scenario];
    const scale = outerScales[row.scenario] ?? 1;
    if (orientation === "candidate-control-candidate") {
      firstRatios[row.scenario] = factor / scale;
      centerRatios[row.scenario] = 1;
      lastRatios[row.scenario] = factor * scale;
    } else {
      firstRatios[row.scenario] = 1 / scale;
      centerRatios[row.scenario] = factor;
      lastRatios[row.scenario] = scale;
    }
  }
  return {
    manifest,
    manifestBytes,
    summaries: [
      makeSummary(manifestBytes, manifest, firstRatios, spreads.first, {
        commit: COMMITS[0],
        sourceTree: OUTER_TREE
      }),
      makeSummary(manifestBytes, manifest, centerRatios, spreads.center, {
        commit: COMMITS[1],
        sourceTree: CENTER_TREE,
        candidateSourceSha256: CENTER_CANDIDATE_SOURCE
      }),
      makeSummary(manifestBytes, manifest, lastRatios, spreads.last, {
        commit: COMMITS[2],
        sourceTree: OUTER_TREE
      })
    ]
  };
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function mutateField(target, field, value) {
  const parts = field.split(".");
  const key = parts.pop();
  const owner = parts.reduce((current, part) => current[part], target);
  if (value === undefined) {
    delete owner[key];
  } else {
    owner[key] = Array.isArray(value) ? [...value] : value;
  }
}

test("paired bundle reducer requires all positions and validates retained raw cycles", () => {
  const bracket = makeBracket();
  const contents = new Map([
    ["bracket-manifest.json", bracket.manifestBytes],
    ...["first.json", "center.json", "last.json"].map((file, index) => [
      file,
      Buffer.from(JSON.stringify(bracket.summaries[index]))
    ])
  ]);
  assert.deepEqual(
    reducePairedThroughputScreen(new Map([...contents].reverse())),
    reducePairedBracket(bracket.manifestBytes, bracket.summaries)
  );
  for (const file of contents.keys()) {
    const missing = new Map(contents);
    missing.delete(file);
    assert.throws(() => reducePairedThroughputScreen(missing), /is required by this reducer/);
  }
  for (const bytes of ["{", "null", "[]"]) {
    assert.throws(
      () => reducePairedThroughputScreen(new Map(contents).set("first.json", Buffer.from(bytes))),
      /first.json/
    );
  }
  const changed = clone(bracket.summaries[0]);
  changed.rows[0].cycleRatios[0] *= 1.1;
  assert.throws(
    () =>
      reducePairedThroughputScreen(
        new Map(contents).set("first.json", Buffer.from(JSON.stringify(changed)))
      ),
    /raw|cycle|ratio/i
  );
  const swapped = new Map(contents);
  swapped.set("first.json", contents.get("center.json"));
  swapped.set("center.json", contents.get("first.json"));
  assert.throws(() => reducePairedThroughputScreen(swapped), /outer|tree|source/i);
});

for (const [status, options] of [
  ["accepted", {}],
  ["rejected", { factors: { [TARGET]: 1 } }],
  ["uninterpretable", { spreads: { first: { [TARGET]: 5 } } }]
]) {
  test(`sealed paired screens replay ${status} decisions from retained cycles`, (t) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "paired-evidence-"));
    t.after(() => fs.rmSync(root, { recursive: true, force: true }));
    const bracket = makeBracket(options);
    fs.writeFileSync(path.join(root, "bracket-manifest.json"), bracket.manifestBytes);
    for (const [index, position] of ["first", "center", "last"].entries()) {
      fs.writeFileSync(
        path.join(root, `${position}.json`),
        JSON.stringify(bracket.summaries[index])
      );
    }
    const manifest = sealBundle(root, {
      experimentId: "paired-screen-test",
      artifactClass: "paired-throughput-screen",
      reducer: "paired-throughput-screen-v1",
      sourceCommit: COMMITS[0]
    });
    assert.equal(manifest.normalized.status, status);
    assert.throws(
      () => sealBundle(root, { ...manifest, sourceCommit: COMMITS[1] }),
      /sourceCommit must match the first run/
    );
    assert.deepEqual(
      manifest.normalized,
      reducePairedBracket(bracket.manifestBytes, bracket.summaries)
    );
    const manifestPath = writeBundleManifest(root, manifest);
    assert.deepEqual(replayBundle(manifestPath).normalized, manifest.normalized);
    const firstPath = path.join(root, "first.json");
    bracket.summaries[0].rows[0].cycleRatios[0] *= 1.1;
    fs.writeFileSync(firstPath, JSON.stringify(bracket.summaries[0]));
    assert.throws(() => replayBundle(manifestPath), /first.json/);
  });
}

function writeBracket(directory, bracket) {
  const manifestPath = path.join(directory, "manifest.json");
  fs.writeFileSync(manifestPath, bracket.manifestBytes);
  const summaryPaths = bracket.summaries.map((summary, index) => {
    const summaryPath = path.join(directory, `summary-${index}.json`);
    fs.writeFileSync(summaryPath, `${JSON.stringify(summary)}\n`);
    return summaryPath;
  });
  return { manifestPath, summaryPaths };
}

function spawnReducer(arguments_) {
  return spawnSync(
    process.execPath,
    [path.resolve(__dirname, "../unity/reduce-paired-bracket.js"), ...arguments_],
    { encoding: "utf8" }
  );
}

test("CLI validates manifests and preserves the accepted, rejected, and invalid exit contract", async (t) => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-paired-reducer-"));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));

  await t.test("manifest preflight", () => {
    const { manifestPath } = writeBracket(directory, makeBracket());
    const result = spawnReducer(["--validate-manifest", "--manifest", manifestPath]);
    assert.equal(result.status, 0, result.stderr);
    assert.match(result.stdout, /^[0-9a-f]{64}\n$/);
  });

  await t.test("accepted stdout", () => {
    const { manifestPath, summaryPaths } = writeBracket(directory, makeBracket());
    const result = spawnReducer([
      "--manifest",
      manifestPath,
      "--first",
      summaryPaths[0],
      "--center",
      summaryPaths[1],
      "--last",
      summaryPaths[2]
    ]);
    assert.equal(result.status, 0, result.stderr);
    assert.equal(JSON.parse(result.stdout).status, "accepted");
  });

  await t.test("rejected output file before status 2", () => {
    const bracket = makeBracket({ factors: { [TARGET]: 1.01 } });
    const { manifestPath, summaryPaths } = writeBracket(directory, bracket);
    const outputPath = path.join(directory, "verdict.json");
    const result = spawnReducer([
      "--manifest",
      manifestPath,
      "--first",
      summaryPaths[0],
      "--center",
      summaryPaths[1],
      "--last",
      summaryPaths[2],
      "--output",
      outputPath
    ]);
    assert.equal(result.status, 2, result.stderr);
    assert.equal(JSON.parse(fs.readFileSync(outputPath, "utf8")).status, "rejected");
  });

  await t.test("malformed evidence status 1", () => {
    const { manifestPath, summaryPaths } = writeBracket(directory, makeBracket());
    fs.writeFileSync(summaryPaths[1], "not-json\n");
    const result = spawnReducer([
      "--manifest",
      manifestPath,
      "--first",
      summaryPaths[0],
      "--center",
      summaryPaths[1],
      "--last",
      summaryPaths[2]
    ]);
    assert.equal(result.status, 1);
    assert.notEqual(result.stderr.trim(), "");
  });
});

test("candidate-control-candidate accepts a stable target-specific improvement", () => {
  const bracket = makeBracket();
  const result = reducePairedBracket(bracket.manifestBytes, bracket.summaries);
  assert.equal(result.status, "accepted");
  assert.deepEqual(result.reasons, []);
  assert.deepEqual(
    result.provenance.map((entry) => entry.commit),
    COMMITS
  );
  assert.ok(
    result.rows.find((row) => row.scenario === TARGET).sentinelNormalizedEffectPercent > 3,
    "The target should exceed the fixed target-specific threshold."
  );
});

test("control-candidate-control uses the mirrored positional reduction", () => {
  const bracket = makeBracket({ orientation: "control-candidate-control" });
  const result = reducePairedBracket(bracket.manifestBytes, bracket.summaries);
  assert.equal(result.status, "accepted");
  assert.ok(
    Math.abs(result.rows.find((row) => row.scenario === TARGET).candidateEffectPercent - 6) < 1e-10,
    "The center candidate should retain the declared positive effect."
  );
});

test("a stable bracket rejects a target at or below three percent", () => {
  const bracket = makeBracket({
    factors: { [TARGET]: 1.03, [AFFECTED]: 1 }
  });
  const result = reducePairedBracket(bracket.manifestBytes, bracket.summaries);
  assert.equal(result.status, "rejected");
  assert.match(result.reasons.join("\n"), /Filtered sentinel-normalized target effect/);
});

test("a stable bracket rejects an affected-row regression beyond three percent", () => {
  const bracket = makeBracket({
    factors: { [TARGET]: 1.06, [AFFECTED]: 0.96 }
  });
  const result = reducePairedBracket(bracket.manifestBytes, bracket.summaries);
  assert.equal(result.status, "rejected");
  assert.match(result.reasons.join("\n"), /FilteredPostProcess sentinel-normalized affected-row/);
});

test("affected regressions are normalized against the same common-mode sentinel shift", () => {
  const bracket = makeBracket({
    factors: {
      [TARGET]: 1.08,
      [AFFECTED]: 0.998,
      GlobalToOne: 1.029,
      GlobalToMany: 1.029,
      KeyedToOne: 1.029,
      PostProcess: 1.029,
      StructNoBox: 1.029
    },
    spreads: { first: {}, center: {}, last: {} }
  });
  const result = reducePairedBracket(bracket.manifestBytes, bracket.summaries);
  const affected = result.rows.find((row) => row.scenario === AFFECTED);
  assert.equal(result.status, "rejected");
  assert.ok(affected.sentinelNormalizedEffectPercent < -3);
  assert.match(result.reasons.join("\n"), /sentinel-normalized affected-row/);
});

test("raw-cycle spread above three percent makes the bracket uninterpretable", () => {
  const bracket = makeBracket({ spreads: { center: { [TARGET]: 3.01 } } });
  const result = reducePairedBracket(bracket.manifestBytes, bracket.summaries);
  assert.equal(result.status, "uninterpretable");
  assert.match(result.reasons.join("\n"), /Filtered raw-cycle spread/);
});

test("outer same-code spread above three percent makes the bracket uninterpretable", () => {
  const bracket = makeBracket({ outerScales: { [TARGET]: 1.02 } });
  const result = reducePairedBracket(bracket.manifestBytes, bracket.summaries);
  assert.equal(result.status, "uninterpretable");
  assert.match(result.reasons.join("\n"), /Filtered outer spread/);
});

test("a sentinel effect outside three percent makes the bracket uninterpretable", () => {
  const bracket = makeBracket({
    factors: { GlobalToOne: 1.031 }
  });
  const result = reducePairedBracket(bracket.manifestBytes, bracket.summaries);
  assert.equal(result.status, "uninterpretable");
  assert.match(result.reasons.join("\n"), /GlobalToOne sentinel effect/);
});

test("the session 240 artifact-shaped bracket fails its sentinel gate", () => {
  const rows = PERF_TEST_VECTORS.session240Rows;
  const manifest = makeManifest("candidate-control-candidate", rows);
  manifest.bracketId = "session-240-inline-interceptor-flat-access";
  const manifestBytes = encodeManifest(manifest);
  const c1 = PERF_TEST_VECTORS.session240c1;
  const control = PERF_TEST_VECTORS.session240control;
  const c2 = PERF_TEST_VECTORS.session240c2;
  const spreads = PERF_TEST_VECTORS.session240spreads;
  const toMap = (values) =>
    Object.fromEntries(rows.map((row, index) => [row.scenario, values[index]]));
  const summaries = [c1, control, c2].map((values, index) =>
    makeSummary(manifestBytes, manifest, toMap(values), toMap(spreads[index]), {
      commit: COMMITS[index],
      sourceTree: index === 1 ? CENTER_TREE : OUTER_TREE,
      candidateSourceSha256: index === 1 ? CENTER_CANDIDATE_SOURCE : OUTER_CANDIDATE_SOURCE
    })
  );

  const result = reducePairedBracket(manifestBytes, summaries);
  const filtered = result.rows.find((row) => row.scenario === "Filtered");
  assert.equal(result.status, "uninterpretable");
  assert.match(result.reasons.join("\n"), /GlobalToOne sentinel effect/);
  assert.match(result.reasons.join("\n"), /StructNoBox sentinel effect/);
  assert.ok(
    Math.abs(filtered.sentinelNormalizedEffectPercent - 4.753994) < 0.00001,
    "The diagnostic must normalize against all five sentinels without cherry-picking."
  );
});

test("manifest validation rejects incomplete, reordered, unknown, and unsupported classifications", async (t) => {
  const cases = [
    [
      "empty candidate source scope",
      { ...makeManifest(), candidatePaths: [] },
      /candidatePaths must be a non-empty array/
    ],
    [
      "candidate path outside Runtime",
      { ...makeManifest(), candidatePaths: ["docs/architecture/performance.md"] },
      /normalized path below Runtime/
    ],
    [
      "fewer than two sentinels",
      makeManifest(
        undefined,
        DEFAULT_ROWS.map((row, index) =>
          row.role === "sentinel" && index > 0 ? { ...row, role: "affected" } : row
        )
      ),
      /at least two sentinel/
    ],
    [
      "no target",
      makeManifest(
        undefined,
        DEFAULT_ROWS.map((row) => ({ ...row, role: "sentinel" }))
      ),
      /at least one target/
    ],
    [
      "omitted scenario",
      makeManifest(undefined, DEFAULT_ROWS.slice(0, -1)),
      /classify every paired scenario/
    ],
    [
      "reordered scenario",
      makeManifest(undefined, [DEFAULT_ROWS[1], DEFAULT_ROWS[0], ...DEFAULT_ROWS.slice(2)]),
      /classify every paired scenario/
    ],
    [
      "unknown scenario",
      makeManifest(
        undefined,
        DEFAULT_ROWS.map((row, index) =>
          index === DEFAULT_ROWS.length - 1 ? { ...row, scenario: "Unknown" } : row
        )
      ),
      /classify every paired scenario/
    ],
    [
      "canonical-only role",
      makeManifest(
        undefined,
        DEFAULT_ROWS.map((row, index) => (index === 1 ? { ...row, role: "canonical-only" } : row))
      ),
      /unsupported role/
    ]
  ];
  for (const [name, manifest, pattern] of cases) {
    await t.test(name, () => assert.throws(() => validateManifest(manifest), pattern));
  }
});

test("manifest preflight rejects wrong-case and untracked candidate paths", async (t) => {
  const cases = [
    ["wrong case", "Runtime/Core/MessageBus/messageBus.cs"],
    ["untracked", "Runtime/UntrackedCandidate.cs"]
  ];
  for (const [name, candidatePath] of cases) {
    await t.test(name, () => {
      const manifest = { ...makeManifest(), candidatePaths: [candidatePath] };
      assert.throws(
        () => validateManifest(manifest, { requireTrackedCandidatePaths: true }),
        /not tracked at HEAD with exact case/
      );
    });
  }
});

test("retained artifact reduction does not depend on the current HEAD path inventory", () => {
  const bracket = makeBracket();
  const historicalManifest = {
    ...bracket.manifest,
    candidatePaths: ["Runtime/RenamedAfterThisBracket.cs"]
  };
  const historicalManifestBytes = encodeManifest(historicalManifest);
  const summaries = clone(bracket.summaries);
  const digest = manifestSha256(historicalManifestBytes);
  summaries.forEach((summary) => {
    summary.bracketManifestSha256 = digest;
  });
  assert.equal(reducePairedBracket(historicalManifestBytes, summaries).status, "accepted");
});

test("summary validation rejects omitted, extra, reordered, and mismatched-manifest evidence", async (t) => {
  const bracket = makeBracket();
  const cases = [
    ["omitted row", (summaries) => summaries[0].rows.pop(), /row count/],
    [
      "extra row",
      (summaries) =>
        summaries[0].rows.push({
          scenario: "Extra",
          firstToSecondRatio: 1,
          cycleRatioSpreadPercent: 1,
          cycleRatios: [1, 1, 1, 1]
        }),
      /row count/
    ],
    ["reordered row", (summaries) => summaries[0].rows.reverse(), /summary row 0/],
    [
      "duplicated row",
      (summaries) => {
        summaries[0].rows[1] = clone(summaries[0].rows[0]);
      },
      /summary row 1/
    ],
    [
      "manifest digest mismatch",
      (summaries) => {
        summaries[1].bracketManifestSha256 = "0".repeat(64);
      },
      /manifest digest/
    ],
    [
      "execution profile mismatch",
      (summaries) => {
        summaries[2].executionProfile.affinityMask = "0xFFFFFFFF";
      },
      /execution profile/
    ],
    [
      "duplicate commit provenance",
      (summaries) => {
        summaries[2].commit = summaries[0].commit;
      },
      /distinct commits/
    ],
    [
      "different outer source trees",
      (summaries) => {
        summaries[2].sourceTree = "c".repeat(40);
      },
      /outer summaries.*same source tree/
    ],
    [
      "identical center source tree",
      (summaries) => {
        summaries[1].sourceTree = summaries[0].sourceTree;
      },
      /outer and center.*different source trees/
    ],
    [
      "different outer candidate source",
      (summaries) => {
        summaries[2].candidateSourceSha256 = "e".repeat(64);
      },
      /outer summaries.*same candidate-source digest/
    ],
    [
      "unchanged candidate source in center",
      (summaries) => {
        summaries[1].candidateSourceSha256 = summaries[0].candidateSourceSha256;
      },
      /outer and center.*different candidate-source digests/
    ]
  ];
  for (const [name, mutate, pattern] of cases) {
    await t.test(name, () => {
      const summaries = clone(bracket.summaries);
      mutate(summaries);
      assert.throws(() => reducePairedBracket(bracket.manifestBytes, summaries), pattern);
    });
  }
});

test("summary validation recomputes headline and spread from four retained cycle ratios", async (t) => {
  const bracket = makeBracket();
  const row = bracket.summaries[0].rows[0];
  const cases = [
    ["missing cycles", "cycleRatios", undefined, /exactly 4/],
    ["short cycles", "cycleRatios", row.cycleRatios.slice(0, -1), /exactly 4/],
    ["non-positive cycle", "cycleRatios.0", 0, /must be positive/],
    ["non-finite cycle", "cycleRatios.0", Number.NaN, /finite number/],
    ["fabricated headline", "firstToSecondRatio", row.firstToSecondRatio * 1.01, /geometric mean/],
    ["fabricated spread", "cycleRatioSpreadPercent", 0, /spread does not match/]
  ];
  for (const [name, field, value, pattern] of cases) {
    await t.test(name, () => {
      const summaries = clone(bracket.summaries);
      mutateField(summaries[0].rows[0], field, value);
      assert.throws(() => reducePairedBracket(bracket.manifestBytes, summaries), pattern);
    });
  }
});

test("extreme finite inputs cannot overflow or underflow into an accepted verdict", async (t) => {
  await t.test("raw cycle spread overflow", () => {
    const bracket = makeBracket();
    const row = bracket.summaries[0].rows[0];
    row.cycleRatios = [Number.MAX_VALUE, Number.MAX_VALUE, Number.MIN_VALUE, Number.MIN_VALUE];
    row.firstToSecondRatio = Math.exp(
      row.cycleRatios.reduce((sum, value) => sum + Math.log(value), 0) / row.cycleRatios.length
    );
    row.cycleRatioSpreadPercent = 0;
    assert.throws(
      () => reducePairedBracket(bracket.manifestBytes, bracket.summaries),
      /spread does not match/
    );
  });

  await t.test("bracket factor overflow", () => {
    const bracket = makeBracket();
    for (const [index, value] of [Number.MAX_VALUE, Number.MIN_VALUE, Number.MAX_VALUE].entries()) {
      const row = bracket.summaries[index].rows.find((candidate) => candidate.scenario === TARGET);
      row.firstToSecondRatio = value;
      row.cycleRatioSpreadPercent = 0;
      row.cycleRatios = [value, value, value, value];
    }
    assert.throws(
      () => reducePairedBracket(bracket.manifestBytes, bracket.summaries),
      /non-finite bracket reduction/
    );
  });
});

test("three consistently wrong summaries cannot define their own profile or protocol", async (t) => {
  const bracket = makeBracket();
  const cases = [
    ["missing commit", "commit", undefined, /summary commit/],
    ["missing source tree", "sourceTree", undefined, /sourceTree/],
    ["missing candidate source", "candidateSourceSha256", undefined, /candidateSourceSha256/],
    ["Mono platform", "platform", "PlayMode Mono", /platform/],
    ["missing profile", "executionProfile", undefined, /execution profile/],
    ["CPU model", "executionProfile.cpuModel", "other", /profile topology/],
    ["missing CPU model", "executionProfile.cpuModel", undefined, /must contain exactly/],
    ["profile source", "executionProfile.source", "other", /profile topology/],
    ["selection policy", "executionProfile.selectionPolicy", "other", /profile topology/],
    ["efficiency class", "executionProfile.selectedEfficiencyClass", -1, /profile topology/],
    [
      "logical processors",
      "executionProfile.selectedLogicalProcessorIndices",
      [1, 2],
      /profile topology/
    ],
    ["profile", "executionProfile.id", "other", /execution profile/],
    ["affinity", "executionProfile.affinityMask", "0xFFFFFFFF", /execution profile/],
    ["priority", "executionProfile.priorityClass", "High", /execution profile/],
    ["missing protocol", "protocol", undefined, /protocol constants/],
    ["protocol", "protocol", "other", /protocol constants/],
    ["cycles", "cycles", 1, /protocol constants/],
    ["active time", "minimumCycleActiveMilliseconds", 1, /protocol constants/],
    ["batch", "batchOperations", 1, /protocol constants/]
  ];
  for (const [name, field, value, pattern] of cases) {
    await t.test(name, () => {
      const summaries = clone(bracket.summaries);
      summaries.forEach((summary) => mutateField(summary, field, value));
      assert.throws(() => reducePairedBracket(bracket.manifestBytes, summaries), pattern);
    });
  }
});
