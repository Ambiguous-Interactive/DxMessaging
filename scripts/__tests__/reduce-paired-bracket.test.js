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
const DEFAULT_FACTORS = PERF_TEST_VECTORS.defaultFactors;
const COMMITS = PERF_TEST_VECTORS.commits;
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
    ...PERF_TEST_VECTORS.summaryDefaults,
    commit,
    sourceTree,
    candidateSourceSha256,
    bracketManifestSha256: manifestSha256(manifestBytes),
    executionProfile: structuredClone(PERF_TEST_VECTORS.executionProfile),
    ...PERF_TEST_VECTORS.protocol,
    rows: manifest.rows.map((row) => {
      const headline = ratios[row.scenario];
      const spread = spreads[row.scenario] ?? 1;
      const cycleRatios = makeCycleRatios(headline, spread);
      const cycleMeasurements = cycleRatios.map((ratio) => ({
        firstOperations: 40000,
        secondOperations: 40000,
        firstActiveSeconds: Math.max(1, 1 / ratio),
        secondActiveSeconds: Math.max(1, ratio),
        firstToSecondRatio: ratio
      }));
      return {
        scenario: row.scenario,
        firstToSecondRatio: headline,
        aggregateRateRatio:
          cycleMeasurements.reduce((sum, cycle) => sum + cycle.secondActiveSeconds, 0) /
          cycleMeasurements.reduce((sum, cycle) => sum + cycle.firstActiveSeconds, 0),
        cycleRatioSpreadPercent: spread,
        withinMaterialityBand: spread <= 3,
        cycleRatios,
        cycleMeasurements
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

for (const [status, options] of PERF_TEST_VECTORS.pairedDecisionCases) {
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
      ...PERF_TEST_VECTORS.pairedSealOptions,
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

// 2026-10-07: replay must reject raw work/time that the producer would refuse (#618).
for (const [
  index,
  [name, field, value, pattern, remove, sealPattern]
] of PERF_TEST_VECTORS.invalidRawCycles.entries()) {
  test(`paired reducer and sealing reject ${name}`, (t) => {
    const bracket = makeBracket();
    mutateField(
      bracket.summaries[index % 3].rows[0],
      field,
      remove ? undefined : value?.number ? Number(value.number) : value
    );
    assert.throws(
      () => reducePairedBracket(bracket.manifestBytes, bracket.summaries),
      new RegExp(pattern)
    );
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "paired-invalid-cycles-"));
    t.after(() => fs.rmSync(root, { recursive: true, force: true }));
    fs.writeFileSync(path.join(root, "bracket-manifest.json"), bracket.manifestBytes);
    for (const [position, summary] of bracket.summaries.entries())
      fs.writeFileSync(
        path.join(root, `${PERF_TEST_VECTORS.positions[position]}.json`),
        JSON.stringify(summary)
      );
    assert.throws(
      () =>
        sealBundle(root, {
          ...PERF_TEST_VECTORS.pairedSealOptions,
          sourceCommit: COMMITS[0]
        }),
      new RegExp(sealPattern ?? pattern)
    );
  });
}

for (const [name, row, status] of PERF_TEST_VECTORS.validRawRows) {
  test(`paired replay accepts valid raw evidence: ${name}`, () => {
    const bracket = makeBracket();
    bracket.summaries[0].rows[0] = structuredClone(row);
    assert.equal(reducePairedBracket(bracket.manifestBytes, bracket.summaries).status, status);
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

function bracketArguments({ manifestPath, summaryPaths }) {
  return [
    "--manifest",
    manifestPath,
    ...summaryPaths.flatMap((file, index) => [`--${PERF_TEST_VECTORS.positions[index]}`, file])
  ];
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
    const result = spawnReducer(bracketArguments({ manifestPath, summaryPaths }));
    assert.equal(result.status, 0, result.stderr);
    assert.equal(JSON.parse(result.stdout).status, "accepted");
  });

  await t.test("rejected output file before status 2", () => {
    const bracket = makeBracket({ factors: { [TARGET]: 1.01 } });
    const { manifestPath, summaryPaths } = writeBracket(directory, bracket);
    const outputPath = path.join(directory, "verdict.json");
    const result = spawnReducer([
      ...bracketArguments({ manifestPath, summaryPaths }),
      "--output",
      outputPath
    ]);
    assert.equal(result.status, 2, result.stderr);
    assert.equal(JSON.parse(fs.readFileSync(outputPath, "utf8")).status, "rejected");
  });

  await t.test("malformed evidence status 1", () => {
    const { manifestPath, summaryPaths } = writeBracket(directory, makeBracket());
    fs.writeFileSync(summaryPaths[1], "not-json\n");
    const result = spawnReducer(bracketArguments({ manifestPath, summaryPaths }));
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
  const bracket = makeBracket(PERF_TEST_VECTORS.commonModeRegression);
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
  for (const [name, manifest, pattern] of PERF_TEST_VECTORS.invalidManifestCases) {
    await t.test(name, () =>
      assert.throws(() => validateManifest(structuredClone(manifest)), new RegExp(pattern))
    );
  }
});

test("manifest preflight rejects wrong-case and untracked candidate paths", async (t) => {
  const cases = PERF_TEST_VECTORS.invalidCandidatePaths;
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
  for (const [
    name,
    operation,
    field,
    literal,
    from,
    pattern
  ] of PERF_TEST_VECTORS.summaryEvidenceMutations) {
    await t.test(name, () => {
      const summaries = clone(bracket.summaries);
      const resolve = (path) => path.split(".").reduce((value, key) => value[key], summaries);
      if (operation === "set") mutateField(summaries, field, from ? clone(resolve(from)) : literal);
      else if (operation === "push") resolve(field).push(structuredClone(literal));
      else resolve(field)[operation]();
      assert.throws(
        () => reducePairedBracket(bracket.manifestBytes, summaries),
        new RegExp(pattern)
      );
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
    for (const [index, value] of [1e200, 1e-200, 1e200].entries()) {
      const row = bracket.summaries[index].rows.find((candidate) => candidate.scenario === TARGET);
      row.firstToSecondRatio = value;
      row.cycleRatioSpreadPercent = 0;
      row.cycleRatios = [value, value, value, value];
      row.aggregateRateRatio = value;
      row.cycleMeasurements = row.cycleRatios.map((ratio) => ({
        firstOperations: 40000,
        secondOperations: 40000,
        firstActiveSeconds: Math.max(1, 1 / ratio),
        secondActiveSeconds: Math.max(1, ratio),
        firstToSecondRatio: ratio
      }));
    }
    assert.throws(
      () => reducePairedBracket(bracket.manifestBytes, bracket.summaries),
      /non-finite bracket reduction/
    );
  });
});

test("three consistently wrong summaries cannot define their own profile or protocol", async (t) => {
  const bracket = makeBracket();
  for (const [name, field, value, pattern, remove] of PERF_TEST_VECTORS.invalidSummaryProfiles) {
    await t.test(name, () => {
      const summaries = clone(bracket.summaries);
      summaries.forEach((summary) => mutateField(summary, field, remove ? undefined : value));
      assert.throws(
        () => reducePairedBracket(bracket.manifestBytes, summaries),
        new RegExp(pattern)
      );
    });
  }
});
