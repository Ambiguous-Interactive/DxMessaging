"use strict";
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { TextDecoder } = require("node:util");
const {
  REVIEWED_TEXT_EXTENSIONS,
  findSensitiveData,
  hasBinaryMagic,
  hasTooManyNuls,
  isSerializedRedactionSafe,
  redactSensitiveData
} = require("./credential-patterns.js");
const { reduceShippingFidelityMatrix } = require("./perf-evidence-reducers.js");
const { isDirectDirectory } = require("../lib/path-classifier.js");
const SCHEMA_VERSION = 1;
const MANIFEST_NAME = "evidence-manifest.json";
const EXPERIMENT_ID_PATTERN = /^[a-z0-9][a-z0-9.-]*[a-z0-9]$/;
const COMMIT_PATTERN = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/;
const SHA256_PATTERN = /^[0-9a-f]{64}$/;
const MAXIMUM_SCANNED_BYTES = 256 * 1024 * 1024;
const REDUCERS = Object.freeze({
  "shipping-fidelity-matrix-v1": reduceShippingFidelityMatrix
});
function fail(message) {
  throw new Error(message);
}
function sha256(bytes) {
  return crypto.createHash("sha256").update(bytes).digest("hex");
}
function safeDisplayPath(value) {
  const source = String(value);
  if (/[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(source) || source.startsWith("::"))
    return "[redacted:unsafe-path]";
  const redacted = redactSensitiveData(source).redacted;
  return !isSerializedRedactionSafe(source, redacted) || findSensitiveData(redacted).length > 0
    ? "[redacted:encoded-sensitive-data]"
    : redacted;
}
function requireDirectDirectory(root) {
  if (!isDirectDirectory(root)) fail("Bundle root is not a directory or contains a symbolic link.");
}
function requirePrivateRegularFile(filePath, label, readContext = label) {
  let stats;
  try {
    stats = fs.lstatSync(filePath);
  } catch (error) {
    fail(`${readContext} could not be read: ${error.message}`);
  }
  if (!stats.isFile() || stats.nlink !== 1) fail(`${label} is not a private regular file.`);
}
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
  if (
    segments.some((segment) =>
      /[ .]$|^(?:con|prn|aux|nul|(?:com|lpt)(?:[1-9¹²³]))(?:\.|$)/i.test(segment)
    )
  )
    fail(`${label} "${relativePath}" is not portable to Windows.`);
  if (/[<>"|?*]/.test(relativePath))
    fail(`${label} "${relativePath}" contains a character forbidden on Windows.`);
  // eslint-disable-next-line no-control-regex -- control characters are exactly what is rejected.
  if (/[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}:]/u.test(relativePath)) {
    fail(`${label} "${relativePath}" must not contain control characters or a colon.`);
  }
  return relativePath;
}
function requirePortablePaths(paths, label) {
  const folded = new Set();
  for (const value of paths) {
    requirePortablePath(value, label);
    const key = value.normalize("NFC").toUpperCase();
    if (folded.has(key)) fail(`${label}s must not collide on a case-insensitive file system.`);
    folded.add(key);
  }
}
function toPosixPath(value) {
  return value.split(path.sep).join("/");
}
function listBundleFiles(root, manifestName) {
  requireDirectDirectory(root);
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
function assertNoSensitiveData(relativePath, bytes) {
  for (const entry of findSensitiveData(relativePath)) {
    fail(`Bundle file path looks like it contains ${entry.description}; rename it before sealing.`);
  }
  if (bytes.length > MAXIMUM_SCANNED_BYTES) {
    fail(`${relativePath} is ${bytes.length} bytes, too large to prove free of sensitive data.`);
  }
  const extension = path.posix.extname(relativePath).toLowerCase();
  if (!REVIEWED_TEXT_EXTENSIONS.includes(extension)) {
    fail(
      `${relativePath} does not use a reviewed text evidence extension; exclude it or add a ` +
        "reviewed inspection path before sealing."
    );
  }
  if (hasBinaryMagic(bytes)) {
    fail(
      `${relativePath} is binary despite its text extension; exclude it or add a reviewed binary ` +
        "inspection path before sealing."
    );
  }
  let decoded;
  try {
    const encoding =
      bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe
        ? "utf-16le"
        : bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff
          ? "utf-16be"
          : "utf-8";
    decoded = { text: new TextDecoder(encoding, { fatal: true }).decode(bytes), encoding };
  } catch {
    fail(
      `${relativePath} is not valid UTF-8 or byte-order-marked UTF-16 text; exclude it or add ` +
        "a reviewed binary inspection path before sealing."
    );
  }
  const nulCount = decoded.text.split("\0").length - 1;
  if (hasTooManyNuls(decoded.text, nulCount)) {
    fail(
      `${relativePath} contains too many NUL bytes to classify as reviewed text; exclude it or ` +
        "add a reviewed binary inspection path before sealing."
    );
  }
  const sensitiveText = decoded.text.replaceAll("\0", "");
  if (hasBinaryMagic(Buffer.from(sensitiveText, "utf8"))) {
    fail(`${relativePath} contains a NUL-split binary signature; exclude it before sealing.`);
  }
  for (const entry of findSensitiveData(sensitiveText)) {
    fail(`${relativePath} looks like it contains ${entry.description}; scrub it before sealing.`);
  }
  // eslint-disable-next-line no-control-regex -- control and format characters are rejected.
  if (/[\u0001-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]|\p{Cf}/u.test(decoded.text)) {
    fail(
      `${relativePath} contains non-text control or format characters; exclude it or add a ` +
        "reviewed inspection path before sealing."
    );
  }
}
function bundleDigest(manifest) {
  const field = (name, value) => `${name}:${JSON.stringify(value)}`;
  const lines = [
    field("schemaVersion", manifest.schemaVersion),
    field("experimentId", manifest.experimentId),
    field("revision", manifest.revision),
    field("artifactClass", manifest.artifactClass),
    field("reducer", manifest.reducer),
    field("sourceCommit", manifest.sourceCommit),
    ...manifest.files.map((file) => field("file", [file.path, file.length, file.sha256])),
    field("normalized", manifest.normalized)
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
function requireExactKeys(value, keys, label) {
  if (!value || typeof value !== "object" || Array.isArray(value))
    fail(`${label} must be an object.`);
  if (Object.keys(value).some((key) => !keys.includes(key)))
    fail(`${label} contains unsupported fields.`);
}
function requireIdentityText(value, label) {
  requireString(value, label);
  // eslint-disable-next-line no-control-regex -- control characters are exactly what is rejected.
  if (/[\p{Cc}\p{Cf}\p{Cs}\p{Zl}\p{Zp}]/u.test(value)) {
    fail(`${label} must not contain control characters.`);
  }
  return value;
}
function requirePrivacySafeIdentity(value, label) {
  requireIdentityText(value, label);
  if (findSensitiveData(value).length > 0) {
    fail(`${label} must not contain credential or private identifier material.`);
  }
  return value;
}
function requireSourceCommit(value) {
  requirePrivacySafeIdentity(value, "sourceCommit");
  if (!COMMIT_PATTERN.test(value)) fail("sourceCommit must be a 40- or 64-character commit ID.");
  return value;
}
function requireExperimentId(value) {
  requirePrivacySafeIdentity(value, "experimentId");
  if (!EXPERIMENT_ID_PATTERN.test(value)) {
    fail(`experimentId "${value}" must be lowercase alphanumeric with dots or dashes.`);
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
function readDeclaredFiles(root, files) {
  const contents = new Map();
  for (const file of files) {
    const absolute = path.join(root, ...file.path.split("/"));
    requirePrivateRegularFile(absolute, file.path, `${file.path} is declared by the manifest but`);
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
  const experimentId = requireExperimentId(options.experimentId);
  const reducerName = requireString(options.reducer, "reducer");
  const reducer = requireReducer(reducerName);
  const revision = requireInteger(options.revision ?? 1, "revision", 1);
  const manifestName = options.manifestName ?? MANIFEST_NAME;
  const relativePaths = listBundleFiles(root, manifestName);
  if (relativePaths.length === 0) {
    fail(`${safeDisplayPath(root)} contains no evidence files to seal.`);
  }
  requirePortablePaths([...relativePaths, manifestName], "Bundle file path");
  assertNoSensitiveData(manifestName, Buffer.alloc(0));
  const files = [];
  const contents = new Map();
  for (const relativePath of relativePaths) {
    const absolute = path.join(root, ...relativePath.split("/"));
    requirePrivateRegularFile(absolute, relativePath);
    const bytes = fs.readFileSync(absolute);
    assertNoSensitiveData(relativePath, bytes);
    files.push({ path: relativePath, length: bytes.length, sha256: sha256(bytes) });
    contents.set(relativePath, bytes);
  }
  const manifest = {
    schemaVersion: SCHEMA_VERSION,
    experimentId,
    revision,
    artifactClass: requirePrivacySafeIdentity(options.artifactClass, "artifactClass"),
    reducer: reducerName,
    sourceCommit: requireSourceCommit(options.sourceCommit),
    files,
    normalized: reducer(contents),
    bundleDigest: ""
  };
  manifest.bundleDigest = bundleDigest(manifest);
  return manifest;
}
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
  requireDirectDirectory(root);
  requirePortablePath(manifestName, "Manifest name");
  if (manifestName.includes("/")) fail("Manifest name must be one file name.");
  validateManifestShape(manifest, manifestName);
  const expected = sealBundle(root, {
    experimentId: manifest.experimentId,
    revision: manifest.revision,
    artifactClass: manifest.artifactClass,
    reducer: manifest.reducer,
    sourceCommit: manifest.sourceCommit,
    manifestName
  });
  if (manifest.bundleDigest !== expected.bundleDigest)
    fail("Manifest does not match the current bundle bytes and normalized result.");
  const manifestPath = path.join(root, manifestName);
  let existing;
  try {
    existing = fs.lstatSync(manifestPath);
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
  }
  if (existing !== undefined) {
    if (!existing.isFile() || existing.nlink !== 1)
      fail("Existing bundle manifest is not a private regular file.");
    // Shape validation keeps undefined identity fields from bypassing the append-only comparison.
    assertAppendOnly(validateManifestShape(readManifest(manifestPath), manifestName), manifest);
  }
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  return manifestPath;
}
function readManifest(manifestPath) {
  requirePrivateRegularFile(manifestPath, safeDisplayPath(manifestPath));
  let parsed;
  try {
    parsed = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  } catch (error) {
    fail(`${safeDisplayPath(manifestPath)} is not readable JSON: ${error.message}`);
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    fail(`${safeDisplayPath(manifestPath)} must contain a JSON object.`);
  }
  return parsed;
}
function validateManifestShape(manifest, manifestName = MANIFEST_NAME) {
  requireExactKeys(
    manifest,
    [
      "schemaVersion",
      "experimentId",
      "revision",
      "artifactClass",
      "reducer",
      "sourceCommit",
      "files",
      "normalized",
      "bundleDigest"
    ],
    "The manifest"
  );
  if (manifest.schemaVersion !== SCHEMA_VERSION) {
    fail(
      `Unsupported manifest schemaVersion ${manifest.schemaVersion}; expected ${SCHEMA_VERSION}.`
    );
  }
  requireExperimentId(manifest.experimentId);
  requireInteger(manifest.revision, "revision", 1);
  requirePrivacySafeIdentity(manifest.artifactClass, "artifactClass");
  requireSourceCommit(manifest.sourceCommit);
  requireReducer(requireString(manifest.reducer, "reducer"));
  if (!Array.isArray(manifest.files) || manifest.files.length === 0) {
    fail("The manifest must declare at least one file.");
  }
  let previousPath = "";
  requirePortablePaths(
    [...manifest.files.map((file) => file?.path), manifestName],
    "Declared file path"
  );
  assertNoSensitiveData(manifestName, Buffer.alloc(0));
  for (const file of manifest.files) {
    requireExactKeys(file, ["path", "length", "sha256"], "A manifest file entry");
    assertNoSensitiveData(file.path, Buffer.alloc(0));
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
  if (!SHA256_PATTERN.test(manifest.bundleDigest ?? ""))
    fail("The manifest bundleDigest must be 64 lowercase hex characters.");
  return manifest;
}
function verifyBundle(manifestPath, root = path.dirname(manifestPath)) {
  requireDirectDirectory(root);
  const manifest = validateManifestShape(readManifest(manifestPath), path.basename(manifestPath));
  assertNoSensitiveData(path.basename(manifestPath), fs.readFileSync(manifestPath));
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
    assertNoSensitiveData(file.path, bytes);
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
        `${manifest.revision} (${manifest.bundleDigest}) into ${safeDisplayPath(manifestPath)}\n`
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
    process.stderr.write(`${safeDisplayPath(error.message)}\n`);
    process.exitCode = 1;
  }
}
module.exports = {
  MANIFEST_NAME,
  REVIEWED_TEXT_EXTENSIONS,
  SCHEMA_VERSION,
  bundleDigest,
  listBundleFiles,
  parseArgs,
  replayBundle,
  runCli,
  safeDisplayPath,
  sealBundle,
  usage,
  verifyBundle,
  writeBundleManifest
};
