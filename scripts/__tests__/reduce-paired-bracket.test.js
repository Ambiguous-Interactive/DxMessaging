"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { spawnSync } = require("node:child_process");
const test = require("node:test");

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
const DEFAULT_ROWS = [
  { scenario: "GlobalToOne", role: "sentinel" },
  { scenario: "GlobalToMany", role: "sentinel" },
  { scenario: "KeyedToOne", role: "sentinel" },
  { scenario: TARGET, role: "target" },
  { scenario: "PostProcess", role: "sentinel" },
  { scenario: AFFECTED, role: "affected" },
  { scenario: "StructNoBox", role: "sentinel" }
];
const DEFAULT_FACTORS = Object.fromEntries(DEFAULT_ROWS.map((row) => [row.scenario, 1]));
DEFAULT_FACTORS[TARGET] = 1.06;
DEFAULT_FACTORS[AFFECTED] = 0.99;
const COMMITS = ["1".repeat(40), "2".repeat(40), "3".repeat(40)];
const OUTER_TREE = "a".repeat(40);
const CENTER_TREE = "b".repeat(40);
const OUTER_CANDIDATE_SOURCE = "c".repeat(64);
const CENTER_CANDIDATE_SOURCE = "d".repeat(64);

function makeManifest(orientation = "candidate-control-candidate", rows = DEFAULT_ROWS) {
  return {
    schemaVersion: 1,
    bracketId: "test-bracket",
    orientation,
    materialityBandPercent: 3,
    candidatePaths: ["Runtime/Core/MessageBus/MessageBus.cs"],
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
    executionProfile: {
      id: "highest-efficiency-class-affinity-normal-v1",
      cpuModel: "13th Gen Intel(R) Core(TM) i9-13900KF",
      source: "GetSystemCpuSetInformation",
      selectionPolicy: "maximum EfficiencyClass",
      selectedEfficiencyClass: 1,
      selectedLogicalProcessorIndices: Array.from({ length: 16 }, (_, index) => index),
      affinityMask: "0xFFFF",
      priorityClass: "Normal"
    },
    protocol: "interleaved-abba-baab-v1",
    cycles: 4,
    minimumCycleActiveMilliseconds: 625,
    batchOperations: 10000,
    materialityBandPercent: 3,
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
  const rows = [
    { scenario: "GlobalToOne", role: "sentinel" },
    { scenario: "GlobalToMany", role: "sentinel" },
    { scenario: "KeyedToOne", role: "sentinel" },
    { scenario: "Filtered", role: "target" },
    { scenario: "PostProcess", role: "sentinel" },
    { scenario: "FilteredPostProcess", role: "affected" },
    { scenario: "StructNoBox", role: "sentinel" }
  ];
  const manifest = makeManifest("candidate-control-candidate", rows);
  manifest.bracketId = "session-240-inline-interceptor-flat-access";
  const manifestBytes = encodeManifest(manifest);
  const c1 = [
    0.3664825458329125, 0.9638895979446739, 1.1749603478334951, 0.4139565263871168,
    0.30839240772835985, 0.35066743667156336, 0.41332080747580546
  ];
  const control = [
    0.3397850194262837, 0.967803075233871, 1.1798167374564192, 0.3863364779458949,
    0.31514396784308657, 0.3446972097633311, 0.38286598713193026
  ];
  const c2 = [
    0.3626055198992972, 0.9684320021425954, 1.198908896941916, 0.41538562631333986,
    0.3070944651881458, 0.3511185780464608, 0.4086341036570222
  ];
  const spreads = [
    [
      0.8204612835834624, 1.9333897889375118, 2.6835356075866956, 0.9103789875130497,
      0.5659055890008702, 0.8146324113570858, 0.9451177125524346
    ],
    [
      1.1028308453360225, 2.516331076690048, 1.831094466025407, 2.6269405956543146,
      1.2355279753484494, 0.45885913299001935, 0.7936012788094526
    ],
    [
      0.9706598505605957, 0.9008923477044295, 2.021145035917682, 0.8933299443459664,
      0.6974634572843197, 1.335523904224556, 0.6007602409331403
    ]
  ];
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
  const cases = [
    [
      "missing cycles",
      (row) => {
        delete row.cycleRatios;
      },
      /exactly 4/
    ],
    [
      "short cycles",
      (row) => {
        row.cycleRatios.pop();
      },
      /exactly 4/
    ],
    [
      "non-positive cycle",
      (row) => {
        row.cycleRatios[0] = 0;
      },
      /must be positive/
    ],
    [
      "non-finite cycle",
      (row) => {
        row.cycleRatios[0] = Number.NaN;
      },
      /finite number/
    ],
    [
      "fabricated headline",
      (row) => {
        row.firstToSecondRatio *= 1.01;
      },
      /geometric mean/
    ],
    [
      "fabricated spread",
      (row) => {
        row.cycleRatioSpreadPercent = 0;
      },
      /spread does not match/
    ]
  ];
  for (const [name, mutate, pattern] of cases) {
    await t.test(name, () => {
      const summaries = clone(bracket.summaries);
      mutate(summaries[0].rows[0]);
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
    [
      "missing commit",
      (summary) => {
        delete summary.commit;
      },
      /summary commit/
    ],
    [
      "missing source tree",
      (summary) => {
        delete summary.sourceTree;
      },
      /sourceTree/
    ],
    [
      "missing candidate source",
      (summary) => {
        delete summary.candidateSourceSha256;
      },
      /candidateSourceSha256/
    ],
    [
      "Mono platform",
      (summary) => {
        summary.platform = "PlayMode Mono";
      },
      /platform/
    ],
    [
      "missing profile",
      (summary) => {
        delete summary.executionProfile;
      },
      /execution profile/
    ],
    [
      "CPU model",
      (summary) => {
        summary.executionProfile.cpuModel = "other";
      },
      /profile topology/
    ],
    [
      "missing CPU model",
      (summary) => {
        delete summary.executionProfile.cpuModel;
      },
      /must contain exactly/
    ],
    [
      "profile source",
      (summary) => {
        summary.executionProfile.source = "other";
      },
      /profile topology/
    ],
    [
      "selection policy",
      (summary) => {
        summary.executionProfile.selectionPolicy = "other";
      },
      /profile topology/
    ],
    [
      "efficiency class",
      (summary) => {
        summary.executionProfile.selectedEfficiencyClass = -1;
      },
      /profile topology/
    ],
    [
      "logical processors",
      (summary) => {
        summary.executionProfile.selectedLogicalProcessorIndices = [1, 2];
      },
      /profile topology/
    ],
    [
      "profile",
      (summary) => {
        summary.executionProfile.id = "other";
      },
      /execution profile/
    ],
    [
      "affinity",
      (summary) => {
        summary.executionProfile.affinityMask = "0xFFFFFFFF";
      },
      /execution profile/
    ],
    [
      "priority",
      (summary) => {
        summary.executionProfile.priorityClass = "High";
      },
      /execution profile/
    ],
    [
      "missing protocol",
      (summary) => {
        delete summary.protocol;
      },
      /protocol constants/
    ],
    [
      "protocol",
      (summary) => {
        summary.protocol = "other";
      },
      /protocol constants/
    ],
    [
      "cycles",
      (summary) => {
        summary.cycles = 1;
      },
      /protocol constants/
    ],
    [
      "active time",
      (summary) => {
        summary.minimumCycleActiveMilliseconds = 1;
      },
      /protocol constants/
    ],
    [
      "batch",
      (summary) => {
        summary.batchOperations = 1;
      },
      /protocol constants/
    ]
  ];
  for (const [name, mutate, pattern] of cases) {
    await t.test(name, () => {
      const summaries = clone(bracket.summaries);
      summaries.forEach(mutate);
      assert.throws(() => reducePairedBracket(bracket.manifestBytes, summaries), pattern);
    });
  }
});
