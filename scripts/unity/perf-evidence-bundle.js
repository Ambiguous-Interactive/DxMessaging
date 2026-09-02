"use strict";

/**
 * Seals, verifies, and replays one content-addressed performance-evidence bundle (issue #508).
 *
 * A bundle is a directory of raw evidence plus one manifest that names every file with its byte
 * length and SHA-256. The manifest also carries the normalized result a reducer derived from those
 * files, so `replay` can re-derive it and prove the published numbers came from the retained bytes.
 * One changed byte anywhere fails verification, which is the whole point: CI artifacts expire, and
 * a screenshot or a hand-copied winner cannot establish provenance.
 *
 * Hashes are lowercase hex. Paths inside a manifest are POSIX-relative so a bundle sealed on the
 * Windows perf runner verifies unchanged on a Linux or macOS reviewer machine.
 */

const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { findCredentials, looksBinary } = require("./credential-patterns.js");
const { reduceShippingFidelityMatrix } = require("./perf-evidence-reducers.js");

const SCHEMA_VERSION = 1;
const MANIFEST_NAME = "evidence-manifest.json";
const EXPERIMENT_ID_PATTERN = /^[a-z0-9][a-z0-9.-]*[a-z0-9]$/;
const SHA256_PATTERN = /^[0-9a-f]{64}$/;
const MAXIMUM_SCANNED_BYTES = 4 * 1024 * 1024;

/** Reducers are keyed by name so a manifest declares which one produced its normalized result. */
const REDUCERS = Object.freeze({
  "shipping-fidelity-matrix-v1": reduceShippingFidelityMatrix
});

function fail(message) {
  throw new Error(message);
}

function sha256(bytes) {
  return crypto.createHash("sha256").update(bytes).digest("hex");
}

/**
 * A bundle path must mean the same file on every operating system. Absolute paths, drive letters,
 * backslashes, `..` segments, and `.`/empty segments are all rejected rather than normalized,
 * because silently rewriting a path would change what the manifest claims to cover.
 */
function requirePortablePath(relativePath, label) {
  if (typeof relativePath !== "string" || relativePath.length === 0) {
    fail(`${label} must be a non-empty string.`);
  }
  if (relativePath.includes("\\")) {
    fail(`${label} "${relativePath}" must use forward slashes, not backslashes.`);
  }
  if (relativePath.startsWith("/") || /^[A-Za-z]:/.test(relativePath)) {
    fail(`${label} "${relativePath}" must be relative to the bundle root.`);
  }
  const segments = relativePath.split("/");
  if (segments.some((segment) => segment === "" || segment === "." || segment === "..")) {
    fail(`${label} "${relativePath}" must not contain empty, "." or ".." segments.`);
  }
  return relativePath;
}

function toPosixPath(value) {
  return value.split(path.sep).join("/");
}

/** Every file under `root`, POSIX-relative and ordinally sorted, excluding the manifest itself. */
function listBundleFiles(root, manifestName) {
  const found = [];
  const walk = (directory) => {
    const entries = fs.readdirSync(directory, { withFileTypes: true });
    for (const entry of entries.sort((left, right) => (left.name < right.name ? -1 : 1))) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(absolute);
      } else if (entry.isFile()) {
        found.push(toPosixPath(path.relative(root, absolute)));
      } else {
        fail(`${toPosixPath(path.relative(root, absolute))} is not a regular file.`);
      }
    }
  };
  walk(root);
  return found.filter((relativePath) => relativePath !== manifestName).sort();
}

/**
 * Refuse to seal a file that carries credential material. Binary files are skipped by a NUL-byte
 * probe, and only the first few megabytes of a text file are scanned so a large trace stays cheap.
 */
function assertNoSecrets(relativePath, bytes) {
  const window = bytes.subarray(0, MAXIMUM_SCANNED_BYTES);
  if (looksBinary(window)) {
    return;
  }
  for (const entry of findCredentials(window.toString("utf8"))) {
    fail(`${relativePath} looks like it contains ${entry.description}; scrub it before sealing.`);
  }
}

/**
 * The bundle digest covers identity, the reducer name, the complete file inventory, and the
 * normalized result. It is computed over a canonical string rather than over `JSON.stringify` of
 * the manifest so that adding a descriptive field later cannot silently change an existing digest.
 */
function bundleDigest(manifest) {
  const lines = [
    `schemaVersion:${manifest.schemaVersion}`,
    `experimentId:${manifest.experimentId}`,
    `revision:${manifest.revision}`,
    `artifactClass:${manifest.artifactClass}`,
    `reducer:${manifest.reducer}`,
    `sourceCommit:${manifest.sourceCommit}`,
    ...manifest.files.map((file) => `file:${file.path}:${file.length}:${file.sha256}`),
    `normalized:${JSON.stringify(manifest.normalized)}`
  ];
  return sha256(Buffer.from(`${lines.join("\n")}\n`, "utf8"));
}

function requireInteger(value, label, minimum) {
  if (!Number.isSafeInteger(value) || value < minimum) {
    fail(`${label} must be an integer of at least ${minimum}.`);
  }
  return value;
}

function requireString(value, label) {
  if (typeof value !== "string" || value.length === 0) {
    fail(`${label} must be a non-empty string.`);
  }
  return value;
}

function requireReducer(name) {
  const reducer = REDUCERS[name];
  if (!reducer) {
    fail(`Unknown reducer "${name}". Supported reducers: ${Object.keys(REDUCERS).join(", ")}.`);
  }
  return reducer;
}

/** Read every declared file once, so verify and replay share one set of bytes. */
function readDeclaredFiles(root, files) {
  const contents = new Map();
  for (const file of files) {
    const absolute = path.join(root, ...file.path.split("/"));
    let bytes;
    try {
      bytes = fs.readFileSync(absolute);
    } catch (error) {
      fail(`${file.path} is declared by the manifest but could not be read: ${error.message}`);
    }
    contents.set(file.path, bytes);
  }
  return contents;
}

function sealBundle(root, options) {
  const experimentId = requireString(options.experimentId, "experimentId");
  if (!EXPERIMENT_ID_PATTERN.test(experimentId)) {
    fail(`experimentId "${experimentId}" must be lowercase alphanumeric with dots or dashes.`);
  }
  const reducerName = requireString(options.reducer, "reducer");
  const reducer = requireReducer(reducerName);
  const revision = requireInteger(options.revision ?? 1, "revision", 1);
  const manifestName = options.manifestName ?? MANIFEST_NAME;
  const relativePaths = listBundleFiles(root, manifestName);
  if (relativePaths.length === 0) {
    fail(`${root} contains no evidence files to seal.`);
  }
  const files = [];
  const contents = new Map();
  for (const relativePath of relativePaths) {
    requirePortablePath(relativePath, "Bundle file path");
    const bytes = fs.readFileSync(path.join(root, ...relativePath.split("/")));
    assertNoSecrets(relativePath, bytes);
    files.push({ path: relativePath, length: bytes.length, sha256: sha256(bytes) });
    contents.set(relativePath, bytes);
  }
  const manifest = {
    schemaVersion: SCHEMA_VERSION,
    experimentId,
    revision,
    artifactClass: requireString(options.artifactClass, "artifactClass"),
    reducer: reducerName,
    sourceCommit: requireString(options.sourceCommit, "sourceCommit"),
    files,
    normalized: reducer(contents),
    bundleDigest: ""
  };
  manifest.bundleDigest = bundleDigest(manifest);
  return manifest;
}

/**
 * Evidence is append-only. Re-sealing the same experiment ID and revision over different bytes is
 * the failure this refuses: a correction must publish a new revision so the old digest still
 * resolves to the exact bytes that produced the old conclusion.
 */
function assertAppendOnly(existingManifest, manifest) {
  if (existingManifest.experimentId !== manifest.experimentId) {
    return;
  }
  if (existingManifest.revision !== manifest.revision) {
    return;
  }
  if (existingManifest.bundleDigest !== manifest.bundleDigest) {
    fail(
      `${manifest.experimentId} revision ${manifest.revision} is already sealed as ` +
        `${existingManifest.bundleDigest} but these bytes seal as ${manifest.bundleDigest}. ` +
        "Publish a new revision instead of replacing sealed evidence."
    );
  }
}

function writeBundleManifest(root, manifest, manifestName = MANIFEST_NAME) {
  const manifestPath = path.join(root, manifestName);
  if (fs.existsSync(manifestPath)) {
    assertAppendOnly(readManifest(manifestPath), manifest);
  }
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  return manifestPath;
}

function readManifest(manifestPath) {
  let parsed;
  try {
    parsed = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  } catch (error) {
    fail(`${manifestPath} is not readable JSON: ${error.message}`);
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    fail(`${manifestPath} must contain a JSON object.`);
  }
  return parsed;
}

function validateManifestShape(manifest) {
  if (manifest.schemaVersion !== SCHEMA_VERSION) {
    fail(
      `Unsupported manifest schemaVersion ${manifest.schemaVersion}; expected ${SCHEMA_VERSION}.`
    );
  }
  requireString(manifest.experimentId, "experimentId");
  requireInteger(manifest.revision, "revision", 1);
  requireString(manifest.artifactClass, "artifactClass");
  requireString(manifest.sourceCommit, "sourceCommit");
  requireReducer(requireString(manifest.reducer, "reducer"));
  if (!Array.isArray(manifest.files) || manifest.files.length === 0) {
    fail("The manifest must declare at least one file.");
  }
  let previousPath = "";
  for (const file of manifest.files) {
    requirePortablePath(file?.path, "Declared file path");
    requireInteger(file.length, `${file.path} length`, 0);
    if (!SHA256_PATTERN.test(file.sha256 ?? "")) {
      fail(`${file.path} sha256 must be 64 lowercase hex characters.`);
    }
    if (file.path <= previousPath) {
      fail(`Declared files must be uniquely sorted; "${file.path}" follows "${previousPath}".`);
    }
    previousPath = file.path;
  }
  if (manifest.normalized === undefined) {
    fail("The manifest must carry the normalized result its reducer produced.");
  }
  return manifest;
}

/**
 * Verify a sealed bundle. Returns the manifest and the verified bytes so `replay` never re-reads a
 * file that verification already proved, which is what makes replay operate on the sealed bytes
 * rather than on whatever currently sits on disk.
 */
function verifyBundle(manifestPath, root = path.dirname(manifestPath)) {
  const manifest = validateManifestShape(readManifest(manifestPath));
  const expectedDigest = bundleDigest(manifest);
  if (manifest.bundleDigest !== expectedDigest) {
    fail(
      `Manifest bundleDigest ${manifest.bundleDigest} does not match its own contents ` +
        `(${expectedDigest}); the manifest was edited after sealing.`
    );
  }
  const contents = readDeclaredFiles(root, manifest.files);
  for (const file of manifest.files) {
    const bytes = contents.get(file.path);
    if (bytes.length !== file.length) {
      fail(`${file.path} is ${bytes.length} bytes but the manifest declares ${file.length}.`);
    }
    const digest = sha256(bytes);
    if (digest !== file.sha256) {
      fail(`${file.path} hashes to ${digest} but the manifest declares ${file.sha256}.`);
    }
  }
  const declared = new Set(manifest.files.map((file) => file.path));
  const undeclared = listBundleFiles(root, path.basename(manifestPath)).filter(
    (relativePath) => !declared.has(relativePath)
  );
  if (undeclared.length > 0) {
    fail(`Undeclared files are present in the bundle: ${undeclared.join(", ")}.`);
  }
  return { manifest, contents };
}

/**
 * Re-derive the normalized result from the verified raw bytes and require it to match what the
 * manifest published. This is the reproduction step: a conclusion that cannot be re-derived from
 * the retained bundle is not campaign evidence.
 */
function replayBundle(manifestPath, root = path.dirname(manifestPath)) {
  const { manifest, contents } = verifyBundle(manifestPath, root);
  const replayed = REDUCERS[manifest.reducer](contents);
  const replayedJson = JSON.stringify(replayed);
  const publishedJson = JSON.stringify(manifest.normalized);
  if (replayedJson !== publishedJson) {
    fail(
      `Replaying ${manifest.reducer} over the sealed bytes produced a different normalized ` +
        `result.\n  published: ${publishedJson}\n  replayed:  ${replayedJson}`
    );
  }
  return { manifest, normalized: replayed };
}

function usage() {
  return `Usage:
  node scripts/unity/perf-evidence-bundle.js seal <root> --experiment-id <id> \\
      --artifact-class <class> --reducer <name> --source-commit <sha> [--revision <n>]
  node scripts/unity/perf-evidence-bundle.js verify <manifest.json>
  node scripts/unity/perf-evidence-bundle.js replay <manifest.json>

Seals a directory of performance evidence into a content-addressed bundle, verifies a sealed
bundle byte for byte, or replays its reducer to reproduce the published normalized result.
Reducers: ${Object.keys(REDUCERS).join(", ")}.
`;
}

const CLI_FLAGS = Object.freeze({
  "--experiment-id": "experimentId",
  "--artifact-class": "artifactClass",
  "--reducer": "reducer",
  "--source-commit": "sourceCommit",
  "--revision": "revision"
});

function parseArgs(argv) {
  const options = { command: "", target: "", revision: 1 };
  for (let index = 2; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === "--help" || argument === "-h") {
      return { ...options, help: true };
    }
    const key = CLI_FLAGS[argument];
    if (key) {
      const value = argv[++index];
      if (value === undefined) {
        fail(`${argument} requires a value.`);
      }
      options[key] = key === "revision" ? Number.parseInt(value, 10) : value;
      continue;
    }
    if (argument.startsWith("-")) {
      fail(`Unknown option ${argument}.`);
    }
    if (!options.command) {
      options.command = argument;
    } else if (!options.target) {
      options.target = argument;
    } else {
      fail(`Unexpected argument ${argument}.`);
    }
  }
  return options;
}

function runCli(argv) {
  const options = parseArgs(argv);
  if (options.help || !options.command) {
    process.stdout.write(usage());
    return options.help ? 0 : 1;
  }
  if (!options.target) {
    fail(`${options.command} requires a path argument.`);
  }
  if (options.command === "seal") {
    const root = path.resolve(options.target);
    const manifest = sealBundle(root, options);
    const manifestPath = writeBundleManifest(root, manifest);
    process.stdout.write(
      `Sealed ${manifest.files.length} files as ${manifest.experimentId} revision ` +
        `${manifest.revision} (${manifest.bundleDigest}) into ${manifestPath}\n`
    );
    return 0;
  }
  if (options.command === "verify") {
    const { manifest } = verifyBundle(path.resolve(options.target));
    process.stdout.write(
      `Verified ${manifest.files.length} files for ${manifest.experimentId} revision ` +
        `${manifest.revision} (${manifest.bundleDigest}).\n`
    );
    return 0;
  }
  if (options.command === "replay") {
    const { manifest, normalized } = replayBundle(path.resolve(options.target));
    process.stdout.write(`${JSON.stringify(normalized, null, 2)}\n`);
    process.stderr.write(
      `Replayed ${manifest.reducer} for ${manifest.experimentId} revision ${manifest.revision}; ` +
        "the normalized result matches the sealed manifest.\n"
    );
    return 0;
  }
  fail(`Unknown command ${options.command}.`);
}

if (require.main === module) {
  try {
    process.exitCode = runCli(process.argv);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = {
  MANIFEST_NAME,
  SCHEMA_VERSION,
  bundleDigest,
  listBundleFiles,
  parseArgs,
  replayBundle,
  runCli,
  sealBundle,
  usage,
  verifyBundle,
  writeBundleManifest
};
