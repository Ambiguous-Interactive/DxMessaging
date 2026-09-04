"use strict";
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { test } = require("node:test");
const VECTORS = require("./fixtures/unity-redaction-vectors.json");
const {
  MANIFEST_NAME,
  bundleDigest,
  listBundleFiles,
  parseArgs,
  replayBundle,
  runCli,
  safeDisplayPath,
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
function writeJson(file, value) {
  fs.writeFileSync(file, `${JSON.stringify(value, null, 2)}\n`);
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
      writeJson(path.join(cellDirectory, "shipping-cell-evidence.json"), evidence);
      fs.writeFileSync(
        path.join(cellDirectory, "shipping-positive-player.log"),
        `cell ${cellId} completed\n`
      );
      cells.push({ cellId, ...evidence });
    }
  }
  writeJson(path.join(root, "shipping-matrix-evidence.json"), {
    schemaVersion: 1,
    measurementClass: "characterization",
    unityVersion: "6000.5.2f1",
    cellCount: cells.length,
    completedCellCount: cells.length,
    failedCells: [],
    unreadableEvidenceCells: [],
    cells
  });
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
function bundleWithFile(fileName, content) {
  return writeMatrixBundle(temporaryDirectory(), { extraFiles: { [fileName]: content } });
}
function contentsOf(root) {
  return new Map(
    listBundleFiles(root).map((file) => [file, fs.readFileSync(path.join(root, file))])
  );
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
    "privacy-unsafe declared bytes with matching hashes",
    (root) => {
      const manifestPath = path.join(root, MANIFEST_NAME);
      const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
      const entry = manifest.files.find((file) => file.path.endsWith("player.log"));
      const bytes = Buffer.from("Machine ID: FAKEmachineID000000000000=\n");
      fs.writeFileSync(path.join(root, ...entry.path.split("/")), bytes);
      Object.assign(entry, {
        length: bytes.length,
        sha256: crypto.createHash("sha256").update(bytes).digest("hex")
      });
      manifest.bundleDigest = bundleDigest(manifest);
      fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    },
    /scrub it before sealing/
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
for (const target of ["manifest", "file entry"]) {
  test(`verification rejects an unknown sensitive ${target} field`, () => {
    const { manifestPath } = sealedBundle();
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    const holder = target === "manifest" ? manifest : manifest.files[0];
    holder.runnerHost = "C:\\Users\\Private Runner";
    fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    assert.throws(() => verifyBundle(manifestPath), /contains unsupported fields/);
  });
}
test("verification scans raw manifest bytes before trusting parsed duplicate keys", () => {
  const { manifestPath } = sealedBundle();
  const raw = fs
    .readFileSync(manifestPath, "utf8")
    .replace(
      `"artifactClass": "${SEAL_OPTIONS.artifactClass}"`,
      `"artifactClass": "C:\\\\Users\\\\Private Runner", "artifactClass": "${SEAL_OPTIONS.artifactClass}"`
    );
  fs.writeFileSync(manifestPath, raw);
  assert.throws(() => verifyBundle(manifestPath), /scrub it before sealing/);
});
test("replay rejects a bundle whose sealed bytes no longer produce the published result", () => {
  const { root, manifestPath } = sealedBundle();
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
for (const [label, content] of VECTORS.sealingSensitive) {
  test(`sealing refuses ${label}`, () => {
    const root = bundleWithFile("private.log", content);
    assert.throws(
      () => sealBundle(root, SEAL_OPTIONS),
      /^Error: private\.log looks like it contains /,
      `${label} must block immutable publication`
    );
  });
}
for (const [label, file, text, encoding] of VECTORS.sealingAccepted) {
  test(`sealing accepts ${label}`, () => {
    const manifest = sealBundle(bundleWithFile(file, Buffer.from(text, encoding)), SEAL_OPTIONS);
    assert.ok(
      manifest.files.some((entry) => entry.path === file),
      `${label} must remain sealable`
    );
  });
}
test("sealing refuses an unreviewed binary artifact", () => {
  const root = bundleWithFile(
    "GameAssembly.pdb",
    Buffer.concat([Buffer.alloc(256, 0), Buffer.from("C:\\Users\\fake-runner\\project", "latin1")])
  );
  assert.throws(
    () => sealBundle(root, SEAL_OPTIONS),
    /GameAssembly\.pdb does not use a reviewed text evidence extension/,
    "a binary cannot enter public evidence until a reviewed inspection path exists"
  );
  fs.symlinkSync(root, `${root}-link`, "dir");
  assert.throws(() => sealBundle(`${root}-link`, SEAL_OPTIONS), /Bundle root is not a directory/);
});
test("sealing and manifest writes refuse a symbolic-link ancestor", () => {
  const root = writeMatrixBundle(temporaryDirectory());
  const parentLink = `${root}-parent-link`;
  fs.symlinkSync(path.dirname(root), parentLink, "dir");
  const aliasedRoot = path.join(parentLink, path.basename(root));
  assert.throws(() => sealBundle(aliasedRoot, SEAL_OPTIONS), /symbolic link/);
  const manifest = sealBundle(root, SEAL_OPTIONS);
  assert.throws(() => writeBundleManifest(aliasedRoot, manifest), /symbolic link/);
});
for (const [label, bytes] of [
  ["PNG magic", Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x41])],
  ["ZIP magic", Buffer.from([0x50, 0x4b, 0x03, 0x04, 0x41, 0x42, 0x43])],
  ["PDF magic", Buffer.from("%PDF-1.7\nprintable payload")],
  [
    "UTF-8 BOM PDF magic",
    Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from("%PDF-1.7\npayload")])
  ],
  ["whitespace-prefixed PDF magic", Buffer.from(" \r\n\t%PDF-1.7\npayload")],
  ["structured preamble PDF", Buffer.from("junk\n%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF")],
  ["PE magic", Buffer.from("MZprintable payload")],
  ["ar magic", Buffer.from("!<arch>\nprintable payload")],
  ["invalid high bytes", Buffer.from([0x41, 0xff, 0x42])],
  ["a UTF-8 C1 control", Buffer.from([0x41, 0xc2, 0x80, 0x42])],
  [
    "a binary tail after a text prefix",
    Buffer.concat([Buffer.alloc(8192, 0x41), Buffer.alloc(64)])
  ],
  ["a NUL-split PDF signature", Buffer.from("%P\0DF-1.7\npayload")]
]) {
  test(`sealing rejects ${label} disguised with a text extension`, () => {
    const root = bundleWithFile("disguised.log", bytes);
    assert.throws(
      () => sealBundle(root, SEAL_OPTIONS),
      /disguised\.log (?:is binary|is not valid UTF-8|contains (?:a NUL-split binary signature|non-text control or format characters|too many NUL bytes))/,
      `${label}: a friendly extension must not bypass whole-file text validation`
    );
  });
}
for (const fileName of ["runner-192.168.42.17.log", "runner-192&#46;168&#46;42&#46;17.log"]) {
  test(`sealing refuses a private identifier in a file name: ${fileName}`, () => {
    const root = writeMatrixBundle(temporaryDirectory(), { extraFiles: { [fileName]: "clean\n" } });
    assert.throws(
      () => sealBundle(root, SEAL_OPTIONS),
      /Bundle file path looks like it contains an IPv4 address/
    );
  });
}
for (const [field, value] of VECTORS.sensitiveMetadata) {
  test(`sealing refuses a private identifier in ${field}`, () => {
    const root = writeMatrixBundle(temporaryDirectory());
    assert.throws(
      () => sealBundle(root, { ...SEAL_OPTIONS, [field]: value }),
      (error) => {
        assert.match(error.message, new RegExp(`^${field} must not contain credential or private`));
        assert.ok(!error.message.includes(value), "the rejection must not echo the private value");
        return true;
      }
    );
  });
}
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
for (const [label, declaredPath, expected] of VECTORS.invalidBundlePaths) {
  test(`verification rejects ${label} in a manifest`, () => {
    const { root, manifestPath } = sealedBundle();
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    manifest.files[0].path = declaredPath;
    manifest.bundleDigest = bundleDigest(manifest);
    fs.writeFileSync(path.join(root, MANIFEST_NAME), `${JSON.stringify(manifest, null, 2)}\n`);
    assert.throws(
      () => verifyBundle(manifestPath),
      new RegExp(expected),
      `${label} is not portable`
    );
  });
}
for (const [label, left, right] of [
  ["case", "same.log", "SAME.log"],
  ["Unicode normalization", "\u00e9.log", "e\u0301.log"],
  ["Unicode long-s case", "s.log", "ſ.log"]
]) {
  test(`verification rejects a ${label} path collision`, () => {
    const { manifest, manifestPath } = sealedBundle();
    manifest.files[0].path = left;
    manifest.files[1].path = right;
    manifest.bundleDigest = bundleDigest(manifest);
    fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    assert.throws(() => verifyBundle(manifestPath), /case-insensitive/);
  });
}
test("the manifest output name is reserved case-insensitively", () => {
  const root = bundleWithFile("Evidence-Manifest.json", "clean\n");
  assert.throws(() => sealBundle(root, SEAL_OPTIONS), /case-insensitive/);
  const { manifest, manifestPath } = sealedBundle();
  manifest.files[0].path = "Evidence-Manifest.json";
  manifest.bundleDigest = bundleDigest(manifest);
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
  assert.throws(() => verifyBundle(manifestPath), /case-insensitive/);
});
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
for (const [field, value, expected] of VECTORS.invalidSealMetadata) {
  test(`sealing rejects ${field}=${value}`, () => {
    const root = writeMatrixBundle(temporaryDirectory());
    assert.throws(
      () => sealBundle(root, { ...SEAL_OPTIONS, [field]: value }),
      new RegExp(expected)
    );
  });
}
test("sealing rejects an empty evidence directory", () => {
  assert.throws(
    () => sealBundle(temporaryDirectory(), SEAL_OPTIONS),
    /contains no evidence files to seal/
  );
});
for (const artifactClass of VECTORS.unsupportedArtifactClasses) {
  test(`shipping evidence cannot impersonate ${artifactClass}`, () => {
    const { root, manifest, manifestPath } = sealedBundle();
    const expected = /does not support artifactClass/;
    assert.throws(() => sealBundle(root, { ...SEAL_OPTIONS, artifactClass }), expected);
    manifest.artifactClass = artifactClass;
    manifest.bundleDigest = bundleDigest(manifest);
    assert.throws(() => writeBundleManifest(root, manifest), expected);
    fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);
    for (const operation of [verifyBundle, replayBundle]) {
      assert.throws(() => operation(manifestPath), expected, operation.name);
    }
  });
}
test("parseArgs reads the seal contract and rejects unknown options", () => {
  assert.deepEqual(parseArgs(VECTORS.sealCli.argv), VECTORS.sealCli.expected);
  assert.throws(() => parseArgs(["node", "cli", "--nope"]), /Unknown option --nope/);
  assert.throws(() => parseArgs(["node", "cli", "seal", "a", "--reducer"]), /requires a value/);
});
test("sealer diagnostics redact account-bearing root paths", () => {
  const privateRoot = "C:\\Users\\Private Runner\\bundle-192.168.42.17";
  const display = safeDisplayPath(privateRoot);
  assert.doesNotMatch(display, /Private Runner|192\.168\.42\.17/);
  assert.match(display, /\[redacted:(?:account-home-path|ipv4-address|encoded-sensitive-data)\]/);
  assert.doesNotMatch(
    safeDisplayPath(`EACCES: open '${privateRoot}\\evidence-manifest.json'`),
    /Private Runner|192\.168\.42\.17/
  );
  assert.equal(safeDisplayPath("::error::forged"), "[redacted:unsafe-path]");
});
test("the CLI seals, verifies, and replays a bundle end to end", (t) => {
  const root = writeMatrixBundle(temporaryDirectory());
  const written = [];
  t.mock.method(process.stdout, "write", (chunk) => written.push(chunk));
  t.mock.method(process.stderr, "write", (chunk) => written.push(chunk));
  const argv = (...rest) => ["node", "perf-evidence-bundle.js", ...rest];
  assert.equal(runCli(argv("seal", root, ...VECTORS.sealCli.validFlags)), 0);
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
for (const [label, injectedPath] of VECTORS.digestInjectionPaths) {
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
for (const [field, value] of VECTORS.digestInjectionFields) {
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
test("writing a manifest cannot modify a hard-linked file outside the bundle", () => {
  const root = writeMatrixBundle(temporaryDirectory());
  const outside = path.join(temporaryDirectory(), "outside.json");
  fs.writeFileSync(outside, "outside bytes\n");
  fs.linkSync(outside, path.join(root, MANIFEST_NAME));
  assert.throws(() => writeBundleManifest(root, sealBundle(root, SEAL_OPTIONS)), /private regular/);
  assert.equal(fs.readFileSync(outside, "utf8"), "outside bytes\n");
});
test("sealing refuses a hard-linked evidence file", () => {
  const root = writeMatrixBundle(temporaryDirectory());
  const outside = path.join(temporaryDirectory(), "outside.log");
  fs.writeFileSync(outside, "public evidence\n");
  fs.linkSync(outside, path.join(root, "linked.log"));
  assert.throws(() => sealBundle(root, SEAL_OPTIONS), /linked\.log is not a private regular file/);
});
test("writing a manifest validates its name and sealed contents before touching disk", () => {
  const holder = temporaryDirectory();
  const root = writeMatrixBundle(path.join(holder, "bundle"));
  const outside = path.join(holder, "outside.json");
  const manifest = sealBundle(root, SEAL_OPTIONS);
  assert.throws(
    () => writeBundleManifest(root, manifest, "../outside.json"),
    /must not contain empty/
  );
  assert.equal(fs.existsSync(outside), false);
  assert.throws(() => writeBundleManifest(root, { secret: "fake-value" }), /unsupported fields/);
  manifest.bundleDigest = "0".repeat(64);
  assert.throws(() => writeBundleManifest(root, manifest), /does not match the current bundle/);
  assert.equal(fs.existsSync(path.join(root, MANIFEST_NAME)), false);
});
test("custom manifest names must use a reviewed text extension", () => {
  const root = writeMatrixBundle(temporaryDirectory());
  assert.throws(
    () => sealBundle(root, { ...SEAL_OPTIONS, manifestName: "manifest.bin" }),
    /manifest\.bin does not use a reviewed text evidence extension/
  );
});
test("verification rejects non-regular entries before reading them", () => {
  const { root, manifest, manifestPath } = sealedBundle();
  const manifestDirectory = path.join(temporaryDirectory(), "manifest.json");
  fs.mkdirSync(manifestDirectory);
  assert.throws(() => verifyBundle(manifestDirectory), /not a private regular file/);
  const declaredPath = path.join(root, ...manifest.files[0].path.split("/"));
  fs.unlinkSync(declaredPath);
  fs.mkdirSync(declaredPath);
  assert.throws(() => verifyBundle(manifestPath), /is not a private regular file/);
});
test("adding a file the reducer never reads still trips the append-only check", () => {
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
test("sealing refuses an encoded UNC authority that straddles the old scan window", () => {
  const root = bundleWithFile(
    "player.log",
    `${"x".repeat(4 * 1024 * 1024 - 64 * 1024 - 101)}\npath=\\\\&#${"0".repeat(66000)}114;${"runner-private".slice(1)}&#92;share\n`
  );
  assert.throws(
    () => sealBundle(root, SEAL_OPTIONS),
    /^Error: player\.log looks like it contains a UNC host name; scrub it before sealing\.$/,
    "the encoded authority crosses the old overlap boundary but must still block publication"
  );
});
test("sealing refuses a credential in a log carrying a stray NUL", () => {
  const root = bundleWithFile(
    "unity.log",
    Buffer.concat([
      Buffer.from("boot\n", "latin1"),
      Buffer.alloc(1),
      Buffer.from(`\nserial ${FAKE_SERIAL}\n`, "latin1")
    ])
  );
  assert.throws(
    () => sealBundle(root, SEAL_OPTIONS),
    /^Error: unity\.log looks like it contains a Unity serial; scrub it before sealing\.$/,
    "a stray NUL must not disable the sealing backstop for a whole log"
  );
});
test("sealing accepts a large clean log with ordinary serialization escapes", () => {
  const content = `${"x".repeat(4 * 1024 * 1024 + 1)}\nC:\\\\runner said \\\"hello\\\" & done\n`;
  assert.doesNotThrow(() => sealBundle(bundleWithFile("unity.log", content), SEAL_OPTIONS));
});
test("sealing refuses a large encoded command whose private value is on the next line", (t) => {
  const content = `${"ordinary log entry\n".repeat(240000)}\\u002dpassword\nfake-private-value\n`;
  const root = bundleWithFile("unity.log", content);
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.throws(() => sealBundle(root, SEAL_OPTIONS), /Unity password assignment/);
});
for (const [label, text] of [
  ["GitHub token", `ghp_${"a".repeat(18)}\0${"a".repeat(18)}`],
  ["IPv4 address", "192.168\0.42.17"]
]) {
  test(`sealing rejects a NUL-split ${label}`, () => {
    const root = writeMatrixBundle(temporaryDirectory(), { extraFiles: { "unity.log": text } });
    assert.throws(
      () => sealBundle(root, SEAL_OPTIONS),
      /unity\.log looks like it contains/,
      `${label}: a consumer that ignores NUL must not reconstruct sensitive data`
    );
  });
}
test("sealing rejects more than eight stray NULs", () => {
  const root = bundleWithFile("unity.log", Buffer.from(`short${"\0".repeat(9)}log`, "utf8"));
  assert.throws(() => sealBundle(root, SEAL_OPTIONS), /contains too many NUL bytes/);
});
test("sealing refuses malformed byte-order-marked UTF-16", () => {
  const root = bundleWithFile("malformed.log", Buffer.from([0xff, 0xfe, 0x41, 0x00, 0xff]));
  assert.throws(
    () => sealBundle(root, SEAL_OPTIONS),
    /malformed\.log is not valid UTF-8 or byte-order-marked UTF-16 text/,
    "every byte must be decoded before the evidence can be classified as reviewed text"
  );
});
