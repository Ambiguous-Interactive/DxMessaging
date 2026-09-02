"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { test } = require("node:test");

const {
  MANIFEST_NAME,
  bundleDigest,
  parseArgs,
  replayBundle,
  runCli,
  sealBundle,
  verifyBundle,
  writeBundleManifest
} = require("../unity/perf-evidence-bundle.js");
const { reduceShippingFidelityMatrix } = require("../unity/perf-evidence-reducers.js");

const SEAL_OPTIONS = Object.freeze({
  experimentId: "shipping-fidelity-matrix-6000.5.2f1",
  artifactClass: "shipping-fidelity-matrix",
  reducer: "shipping-fidelity-matrix-v1",
  sourceCommit: "98b47536a0eb1445fcd2a9700899aab0be24897f"
});

/** Synthetic throughout: this shape matches no serial this project has ever held. */
const FAKE_SERIAL = "SC-FAKE-FAKE-FAKE-FAKE-FAKE";

const STRIPPING_LEVELS = ["High", "Minimal"];
const TOPOLOGIES = [
  ["semantic-18", 18],
  ["cardinality-16", 16]
];

function temporaryDirectory() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "perf-evidence-bundle-test-"));
}

function cellEvidence(level, topologyId, messageTypeCount, index) {
  return {
    schemaVersion: 1,
    measurementClass: "characterization",
    profileId: `shipping-fidelity-il2cpp-${level.toLowerCase()}-player-v1`,
    managedStrippingLevel: level,
    topologyId: `${topologyId}-v1`,
    messageTypeCount,
    unityVersion: "6000.5.2f1",
    libraryState: "cold",
    buildDurationMs: 120000 + index,
    editorBuildWallClockMs: 130000 + index,
    playerTotalBytes: 40000000 + index * 1000,
    gameAssemblyBytes: 9000000 + index * 100,
    timings: {
      engineStartToRunMs: 300 + index,
      firstTypedDispatchUs: 40 + index,
      dispatchLoopNsPerOp: 21 + index,
      dispatchLoopShape: "class"
    }
  };
}

/** A miniature but structurally faithful two-level, two-topology matrix bundle. */
function writeMatrixBundle(root, { extraFiles = {} } = {}) {
  const cells = [];
  let index = 0;
  for (const level of STRIPPING_LEVELS) {
    for (const [topologyId, messageTypeCount] of TOPOLOGIES) {
      const cellId = `${level.toLowerCase()}-${topologyId}`;
      const evidence = cellEvidence(level, topologyId, messageTypeCount, index++);
      const cellDirectory = path.join(root, cellId);
      fs.mkdirSync(cellDirectory, { recursive: true });
      fs.writeFileSync(
        path.join(cellDirectory, "shipping-cell-evidence.json"),
        `${JSON.stringify(evidence, null, 2)}\n`
      );
      fs.writeFileSync(
        path.join(cellDirectory, "shipping-positive-player.log"),
        `cell ${cellId} completed\n`
      );
      cells.push({ cellId, ...evidence });
    }
  }
  fs.writeFileSync(
    path.join(root, "shipping-matrix-evidence.json"),
    `${JSON.stringify(
      {
        schemaVersion: 1,
        measurementClass: "characterization",
        unityVersion: "6000.5.2f1",
        cellCount: cells.length,
        completedCellCount: cells.length,
        failedCells: [],
        unreadableEvidenceCells: [],
        cells
      },
      null,
      2
    )}\n`
  );
  for (const [relativePath, content] of Object.entries(extraFiles)) {
    const absolute = path.join(root, ...relativePath.split("/"));
    fs.mkdirSync(path.dirname(absolute), { recursive: true });
    fs.writeFileSync(absolute, content);
  }
  return root;
}

function sealedBundle(options = {}) {
  const root = writeMatrixBundle(temporaryDirectory(), options);
  const manifest = sealBundle(root, { ...SEAL_OPTIONS, ...options });
  const manifestPath = writeBundleManifest(root, manifest);
  return { root, manifest, manifestPath };
}

function contentsOf(root) {
  const contents = new Map();
  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(absolute);
      } else {
        const relative = path.relative(root, absolute).split(path.sep).join("/");
        contents.set(relative, fs.readFileSync(absolute));
      }
    }
  };
  walk(root);
  return contents;
}

test("a sealed bundle verifies and replays to the normalized result it published", () => {
  const { manifest, manifestPath } = sealedBundle();
  assert.equal(manifest.files.length, 9, "four cells contribute two files each plus the matrix");
  assert.equal(manifest.bundleDigest, bundleDigest(manifest), "the digest covers the manifest");
  const verified = verifyBundle(manifestPath);
  assert.equal(verified.manifest.bundleDigest, manifest.bundleDigest);
  const replayed = replayBundle(manifestPath);
  assert.deepEqual(replayed.normalized, manifest.normalized);
  assert.equal(replayed.normalized.completedCellCount, 4);
  assert.deepEqual(
    replayed.normalized.strippingLevels.map((level) => level.managedStrippingLevel),
    ["High", "Minimal"],
    "stripping-level summaries are ordinally sorted, not directory-walk ordered"
  );
});

test("sealing is deterministic and independent of directory-walk order", () => {
  const first = sealedBundle();
  const second = writeMatrixBundle(temporaryDirectory());
  const secondManifest = sealBundle(second, SEAL_OPTIONS);
  assert.equal(
    secondManifest.bundleDigest,
    first.manifest.bundleDigest,
    "identical bytes must seal to an identical digest"
  );
  assert.deepEqual(
    first.manifest.files.map((file) => file.path),
    [...first.manifest.files]
      .sort((left, right) => (left.path < right.path ? -1 : 1))
      .map((f) => f.path),
    "declared files are ordinally sorted"
  );
});

for (const [label, corrupt, expected] of [
  [
    "one changed raw byte",
    (root) => {
      const target = path.join(root, "high-semantic-18", "shipping-cell-evidence.json");
      fs.writeFileSync(target, fs.readFileSync(target, "utf8").replace("40000000", "40000001"));
    },
    /hashes to [0-9a-f]{64} but the manifest declares/
  ],
  [
    "a truncated raw file",
    (root) =>
      fs.writeFileSync(path.join(root, "high-semantic-18", "shipping-positive-player.log"), ""),
    /is 0 bytes but the manifest declares/
  ],
  [
    "a removed required artifact",
    (root) => fs.rmSync(path.join(root, "high-semantic-18", "shipping-cell-evidence.json")),
    /high-semantic-18\/shipping-cell-evidence\.json is declared by the manifest but could not be read/
  ],
  [
    "an inaccessible artifact directory",
    (root) => {
      fs.rmSync(path.join(root, "minimal-cardinality-16"), { recursive: true, force: true });
    },
    /minimal-cardinality-16\/.* could not be read/
  ],
  [
    "an undeclared file added after sealing",
    (root) => fs.writeFileSync(path.join(root, "smuggled.txt"), "late addition\n"),
    /Undeclared files are present in the bundle: smuggled\.txt/
  ],
  [
    "an edited manifest hash",
    (root) => {
      const manifestPath = path.join(root, MANIFEST_NAME);
      const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
      manifest.files[0].sha256 = "0".repeat(64);
      fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    },
    /does not match its own contents/
  ],
  [
    "an edited normalized result",
    (root) => {
      const manifestPath = path.join(root, MANIFEST_NAME);
      const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
      manifest.normalized.completedCellCount = 99;
      fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    },
    /does not match its own contents/
  ],
  [
    "reordered declared files",
    (root) => {
      const manifestPath = path.join(root, MANIFEST_NAME);
      const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
      manifest.files.reverse();
      fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    },
    /Declared files must be uniquely sorted/
  ]
]) {
  test(`verification rejects ${label}`, () => {
    const { root, manifestPath } = sealedBundle();
    corrupt(root);
    assert.throws(() => verifyBundle(manifestPath), expected, `${label} must fail verification`);
  });
}

test("replay rejects a bundle whose sealed bytes no longer produce the published result", () => {
  const { root, manifestPath } = sealedBundle();
  // Re-seal the tampered bytes so per-file hashes agree again. Only the reducer can catch this.
  const target = path.join(root, "high-semantic-18", "shipping-cell-evidence.json");
  const evidence = JSON.parse(fs.readFileSync(target, "utf8"));
  evidence.playerTotalBytes += 1;
  fs.writeFileSync(target, `${JSON.stringify(evidence, null, 2)}\n`);
  const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  const bytes = fs.readFileSync(target);
  const entry = manifest.files.find((file) =>
    file.path.startsWith("high-semantic-18/shipping-cell")
  );
  entry.length = bytes.length;
  entry.sha256 = require("node:crypto").createHash("sha256").update(bytes).digest("hex");
  manifest.bundleDigest = bundleDigest(manifest);
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  verifyBundle(manifestPath);
  assert.throws(
    () => replayBundle(manifestPath),
    /reports playerTotalBytes=\d+ for cell high-semantic-18 but its own evidence says/,
    "a summary that disagrees with its own per-cell evidence must not replay"
  );
});

for (const [label, relativePath, content] of [
  ["a GitHub token", "leak.log", `token: ghp_${"a".repeat(36)}\n`],
  ["a Unity serial", "leak.log", `UNITY_SERIAL resolved to ${FAKE_SERIAL}\n`],
  ["a PEM private key", "leak.pem", "-----BEGIN RSA PRIVATE KEY-----\nFAKEKEYBODY\n"],
  ["a bearer header", "leak.log", `Authorization: Bearer ${"x".repeat(40)}\n`],
  ["a credential assignment", "leak.env", "UNITY_PASSWORD=correct-horse-battery\n"]
]) {
  test(`sealing refuses ${label}`, () => {
    const root = writeMatrixBundle(temporaryDirectory(), {
      extraFiles: { [relativePath]: content }
    });
    assert.throws(
      () => sealBundle(root, SEAL_OPTIONS),
      new RegExp(`^Error: ${relativePath.replace(".", "\\.")} looks like it contains `),
      `${label} must block publication`
    );
  });
}

test("sealing tolerates masked credentials and binary artifacts", () => {
  const root = writeMatrixBundle(temporaryDirectory(), {
    extraFiles: {
      "clean.log": "GITHUB_TOKEN=***\nUNITY_SERIAL=***\n",
      "GameAssembly.pdb": Buffer.from([0, 1, 2, 3, 0, 255])
    }
  });
  const manifest = sealBundle(root, SEAL_OPTIONS);
  assert.ok(
    manifest.files.some((file) => file.path === "GameAssembly.pdb"),
    "a binary artifact is sealed without being scanned as text"
  );
});

test("re-sealing an experiment revision over different bytes is refused", () => {
  const { root, manifest } = sealedBundle();
  fs.writeFileSync(
    path.join(root, "high-semantic-18", "shipping-positive-player.log"),
    "changed\n"
  );
  const reSealed = sealBundle(root, SEAL_OPTIONS);
  assert.notEqual(reSealed.bundleDigest, manifest.bundleDigest, "different bytes seal differently");
  assert.throws(
    () => writeBundleManifest(root, reSealed),
    /is already sealed as [0-9a-f]{64} but these bytes seal as [0-9a-f]{64}/,
    "an overwrite of sealed evidence must fail closed"
  );
  const nextRevision = sealBundle(root, { ...SEAL_OPTIONS, revision: 2 });
  assert.doesNotThrow(
    () => writeBundleManifest(root, nextRevision),
    "a correction publishes a new revision instead"
  );
});

test("re-sealing identical bytes at the same revision is idempotent", () => {
  const { root, manifest } = sealedBundle();
  const again = sealBundle(root, SEAL_OPTIONS);
  assert.equal(again.bundleDigest, manifest.bundleDigest);
  assert.doesNotThrow(() => writeBundleManifest(root, again));
});

for (const [label, mutate, expected] of [
  [
    "a Windows drive-letter path",
    (files) => (files[0].path = "C:/cell/evidence.json"),
    /must be relative/
  ],
  [
    "a backslash path",
    (files) => (files[0].path = "cell\\evidence.json"),
    /must use forward slashes/
  ],
  ["a parent traversal", (files) => (files[0].path = "../outside.json"), /must not contain empty/],
  ["an absolute POSIX path", (files) => (files[0].path = "/etc/passwd"), /must be relative/]
]) {
  test(`verification rejects ${label} in a manifest`, () => {
    const { root, manifestPath } = sealedBundle();
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    mutate(manifest.files);
    manifest.bundleDigest = bundleDigest(manifest);
    fs.writeFileSync(path.join(root, MANIFEST_NAME), `${JSON.stringify(manifest, null, 2)}\n`);
    assert.throws(() => verifyBundle(manifestPath), expected, `${label} is not portable`);
  });
}

test("the reducer names the artifact a bundle is missing", () => {
  const contents = contentsOf(writeMatrixBundle(temporaryDirectory()));
  contents.delete("shipping-matrix-evidence.json");
  assert.throws(
    () => reduceShippingFidelityMatrix(contents),
    /shipping-matrix-evidence\.json is required by this reducer but is not in the bundle/
  );
});

test("the reducer rejects a summary that omits a completed cell", () => {
  const root = writeMatrixBundle(temporaryDirectory());
  const summaryPath = path.join(root, "shipping-matrix-evidence.json");
  const summary = JSON.parse(fs.readFileSync(summaryPath, "utf8"));
  summary.cells = summary.cells.filter((cell) => cell.cellId !== "high-semantic-18");
  fs.writeFileSync(summaryPath, `${JSON.stringify(summary, null, 2)}\n`);
  assert.throws(
    () => reduceShippingFidelityMatrix(contentsOf(root)),
    /does not list completed cell high-semantic-18/
  );
});

test("sealing rejects an unknown reducer and an unusable experiment id", () => {
  const root = writeMatrixBundle(temporaryDirectory());
  assert.throws(
    () => sealBundle(root, { ...SEAL_OPTIONS, reducer: "not-a-reducer" }),
    /Unknown reducer "not-a-reducer"/
  );
  assert.throws(
    () => sealBundle(root, { ...SEAL_OPTIONS, experimentId: "Shipping_Matrix" }),
    /must be lowercase alphanumeric with dots or dashes/
  );
  assert.throws(
    () => sealBundle(temporaryDirectory(), SEAL_OPTIONS),
    /contains no evidence files to seal/
  );
});

test("parseArgs reads the seal contract and rejects unknown options", () => {
  const options = parseArgs([
    "node",
    "perf-evidence-bundle.js",
    "seal",
    "/tmp/bundle",
    "--experiment-id",
    "shipping-matrix",
    "--artifact-class",
    "shipping-fidelity-matrix",
    "--reducer",
    "shipping-fidelity-matrix-v1",
    "--source-commit",
    "abc123",
    "--revision",
    "3"
  ]);
  assert.deepEqual(options, {
    command: "seal",
    target: "/tmp/bundle",
    revision: 3,
    experimentId: "shipping-matrix",
    artifactClass: "shipping-fidelity-matrix",
    reducer: "shipping-fidelity-matrix-v1",
    sourceCommit: "abc123"
  });
  assert.throws(() => parseArgs(["node", "cli", "--nope"]), /Unknown option --nope/);
  assert.throws(() => parseArgs(["node", "cli", "seal", "a", "--reducer"]), /requires a value/);
});

test("the CLI seals, verifies, and replays a bundle end to end", (t) => {
  const root = writeMatrixBundle(temporaryDirectory());
  const written = [];
  t.mock.method(process.stdout, "write", (chunk) => written.push(chunk));
  t.mock.method(process.stderr, "write", (chunk) => written.push(chunk));
  const argv = (...rest) => ["node", "perf-evidence-bundle.js", ...rest];
  assert.equal(
    runCli(
      argv(
        "seal",
        root,
        "--experiment-id",
        SEAL_OPTIONS.experimentId,
        "--artifact-class",
        SEAL_OPTIONS.artifactClass,
        "--reducer",
        SEAL_OPTIONS.reducer,
        "--source-commit",
        SEAL_OPTIONS.sourceCommit
      )
    ),
    0
  );
  const manifestPath = path.join(root, MANIFEST_NAME);
  assert.ok(fs.existsSync(manifestPath), "seal writes the manifest into the bundle root");
  assert.equal(runCli(argv("verify", manifestPath)), 0);
  assert.equal(runCli(argv("replay", manifestPath)), 0);
  assert.equal(runCli(argv("--help")), 0);
  assert.match(
    written.join(""),
    /Sealed 9 files as shipping-fidelity-matrix-6000\.5\.2f1 revision 1/
  );
  assert.match(written.join(""), /the normalized result matches the sealed manifest/);
  assert.throws(() => runCli(argv("nonsense", "x")), /Unknown command nonsense/);
});

/**
 * `[label, injectedPath]`. The bundle digest joins one field per line as `name:value`, so a
 * declared path carrying the field separator or the line separator could stand in for a whole extra
 * entry and let two different evidence sets share one digest.
 */
for (const [label, injectedPath] of [
  ["a colon", "aa:4:deadbeef"],
  ["a newline", "aa\nfile:bb"]
]) {
  test(`verification rejects a declared file path containing ${label}`, () => {
    const { manifest, manifestPath } = sealedBundle();
    manifest.files[0].path = injectedPath;
    manifest.bundleDigest = bundleDigest(manifest);
    fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    assert.throws(
      () => verifyBundle(manifestPath),
      /must not contain control characters or a colon/,
      `${label}: a path that carries digest structure lets one entry impersonate several`
    );
  });
}

/**
 * `[field, value]`. Identity fields feed the same digest lines, so the same impersonation works
 * through them. Seal time is not enough: a manifest is re-read by reviewers long after sealing, and
 * whoever hands over the file is not necessarily whoever sealed it.
 */
for (const [field, value] of [
  ["experimentId", "shipping\nmatrix"],
  ["artifactClass", "c\nreducer:shipping-fidelity-matrix-v1"],
  ["sourceCommit", "98b47536\nfile:smuggled.json"]
]) {
  test(`verification rejects a control character in ${field}`, () => {
    const { manifest, manifestPath } = sealedBundle();
    manifest[field] = value;
    manifest.bundleDigest = bundleDigest(manifest);
    fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    assert.throws(
      () => verifyBundle(manifestPath),
      new RegExp(`^Error: ${field} must not contain control characters\\.$`),
      `${field}: a sealed manifest must be re-checked at verify time, not only at seal time`
    );
  });
}

test("replacing a sealed manifest with an empty object does not bypass the append-only check", () => {
  const { root } = sealedBundle();
  fs.writeFileSync(path.join(root, MANIFEST_NAME), "{}\n");
  fs.writeFileSync(path.join(root, "high-semantic-18", "player.log"), "different bytes\n");
  const reSealed = sealBundle(root, SEAL_OPTIONS);
  assert.throws(
    () => writeBundleManifest(root, reSealed),
    /^Error: Unsupported manifest schemaVersion undefined; expected 1\.$/,
    "an unusable manifest must fail closed; reading it as a plain object waves the write through"
  );
});

test("adding a file the reducer never reads still trips the append-only check", () => {
  // `player.log` is invisible to the reducer, so the normalized result is unchanged and the
  // reducer's own cross-checks stay silent. Only the append-only comparison can refuse this.
  const { root, manifest } = sealedBundle();
  fs.writeFileSync(path.join(root, "high-semantic-18", "player.log"), "different bytes\n");
  const reSealed = sealBundle(root, SEAL_OPTIONS);
  assert.deepEqual(
    reSealed.normalized,
    manifest.normalized,
    "reducer: the added file must not change the normalized result, or this proves nothing"
  );
  assert.notEqual(
    reSealed.bundleDigest,
    manifest.bundleDigest,
    "digest: the file inventory changed, so the digest must change"
  );
  assert.throws(
    () => writeBundleManifest(root, reSealed),
    /is already sealed as [0-9a-f]{64} but these bytes seal as [0-9a-f]{64}/,
    "an overwrite of sealed evidence must fail closed even when the reducer sees no difference"
  );
});

test("sealing refuses a credential past the first four mebibytes of a file", () => {
  // An earlier version scanned only the first 4 MiB of each file, so a serial written after a long
  // build log sealed cleanly. A credential past an arbitrary window is still a credential.
  const root = writeMatrixBundle(temporaryDirectory(), {
    extraFiles: { "player.log": `${"x".repeat(4 * 1024 * 1024 + 64)}\nserial ${FAKE_SERIAL}\n` }
  });
  assert.throws(
    () => sealBundle(root, SEAL_OPTIONS),
    /^Error: player\.log looks like it contains a Unity serial; scrub it before sealing\.$/,
    "a credential beyond the old scan window must still block publication"
  );
});

test("sealing refuses a credential in a log carrying a stray NUL", () => {
  // One stray NUL from native subprocess output used to make the whole log read as binary, which
  // skipped the scan entirely and sealed the serial into an immutable release asset.
  const root = writeMatrixBundle(temporaryDirectory(), {
    extraFiles: {
      "unity.log": Buffer.concat([
        Buffer.from("boot\n", "latin1"),
        Buffer.alloc(1),
        Buffer.from(`\nserial ${FAKE_SERIAL}\n`, "latin1")
      ])
    }
  });
  assert.throws(
    () => sealBundle(root, SEAL_OPTIONS),
    /^Error: unity\.log looks like it contains a Unity serial; scrub it before sealing\.$/,
    "a stray NUL must not disable the sealing backstop for a whole log"
  );
});
