"use strict";
const fs = require("fs");
const path = require("path");
const { TextDecoder } = require("node:util");
const { isDirectDirectory } = require("../lib/path-classifier.js");
const {
  REVIEWED_TEXT_EXTENSIONS,
  decodeText,
  encodeText,
  findSensitiveData,
  hasBinaryMagic,
  isSerializedRedactionSafe,
  redactSensitiveData
} = require("./credential-patterns.js");
const MAXIMUM_FILE_BYTES = 256 * 1024 * 1024;
function fail(message) {
  throw new Error(message);
}
function toPosixPath(value) {
  return value.split(path.sep).join("/");
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
function listFiles(root) {
  const found = [];
  const walk = (directory) => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true }).sort()) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(absolute);
      } else if (entry.isFile()) {
        found.push(absolute);
      } else {
        fail("Artifact tree contains a symbolic link or non-regular entry.");
      }
    }
  };
  walk(root);
  return found.sort();
}
function decodeStrictText(bytes) {
  const encoding =
    bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe
      ? "utf-16le"
      : bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff
        ? "utf-16be"
        : "utf-8";
  try {
    return new TextDecoder(encoding, { fatal: true }).decode(bytes);
  } catch {
    return undefined;
  }
}
function redactDirectory(root) {
  if (!isDirectDirectory(root)) {
    fail("Artifact root is not a directory.");
  }
  const changed = [];
  const skipped = [];
  const totals = new Map();
  let binaryCount = 0;
  for (const absolute of listFiles(root)) {
    const relative = toPosixPath(path.relative(root, absolute));
    const pathFindings = findSensitiveData(relative);
    if (pathFindings.length > 0 || /\p{Cf}/u.test(relative)) {
      skipped.push({
        path: "[redacted:sensitive-file-name]",
        reason: "has a sensitive file name that cannot be rewritten"
      });
      continue;
    }
    const stats = fs.statSync(absolute);
    if (stats.nlink !== 1) {
      skipped.push({
        path: relative,
        reason: "has multiple hard links and cannot be rewritten safely"
      });
      continue;
    }
    const size = stats.size;
    if (size > MAXIMUM_FILE_BYTES) {
      skipped.push({
        path: relative,
        reason: `is ${size} bytes, over the ${MAXIMUM_FILE_BYTES} cap`
      });
      continue;
    }
    let bytes;
    try {
      bytes = fs.readFileSync(absolute);
    } catch (error) {
      skipped.push({
        path: relative,
        reason: `could not be read: ${safeDisplayPath(error.message)}`
      });
      continue;
    }
    const extension = path.extname(relative).toLowerCase();
    if (!REVIEWED_TEXT_EXTENSIONS.includes(extension)) {
      const strictText = decodeStrictText(bytes)?.replaceAll("\0", "");
      if (strictText !== undefined && findSensitiveData(strictText).length > 0) {
        skipped.push({
          path: relative,
          reason: "uses an unreviewed extension and contains sensitive data"
        });
      } else {
        binaryCount += 1;
      }
      continue;
    }
    const decoded = decodeText(bytes);
    if (decoded === undefined) {
      const strictText = decodeStrictText(bytes)?.replaceAll("\0", "");
      if (strictText !== undefined && findSensitiveData(strictText).length > 0) {
        skipped.push({ path: relative, reason: "is opaque but contains sensitive text" });
        continue;
      }
      binaryCount += 1;
      continue;
    }
    const nulCount = decoded.text.split("\0").length - 1;
    const normalized = nulCount === 0 ? decoded.text : decoded.text.replaceAll("\0", "");
    const lossyUtf8 = bytes.toString("utf8").replaceAll("\0", "");
    if (
      decoded.encoding === "latin1" &&
      findSensitiveData(lossyUtf8).length > 0 &&
      findSensitiveData(normalized).length === 0
    ) {
      skipped.push({ path: relative, reason: "is opaque but contains sensitive text" });
      continue;
    }
    if (hasBinaryMagic(encodeText(normalized, decoded.encoding))) {
      if (findSensitiveData(normalized).length > 0) {
        skipped.push({ path: relative, reason: "is opaque but contains sensitive text" });
      } else binaryCount += 1;
      continue;
    }
    const { redacted, counts } = redactSensitiveData(normalized);
    if (
      !isSerializedRedactionSafe(normalized, redacted) ||
      findSensitiveData(redacted).length > 0 ||
      /\p{Cf}/u.test(redacted.slice(decoded.encoding.startsWith("utf16") ? 1 : 0))
    ) {
      skipped.push({
        path: relative,
        reason: "contains encoded sensitive data or format controls that cannot be safely rewritten"
      });
      continue;
    }
    if (nulCount > 0) {
      counts.set("stray-nul-byte", nulCount);
    }
    if (counts.size === 0) {
      continue;
    }
    try {
      fs.writeFileSync(absolute, encodeText(redacted, decoded.encoding));
    } catch (error) {
      fail(
        `${relative} contains sensitive data but could not be rewritten: ` +
          safeDisplayPath(error.message)
      );
    }
    for (const [id, count] of counts) {
      totals.set(id, (totals.get(id) ?? 0) + count);
    }
    changed.push({ path: relative, counts: [...counts.keys()].sort() });
  }
  return { changed, skipped, totals, binaryCount };
}
function formatSummary(root, result) {
  const displayRoot = safeDisplayPath(root);
  const lines = [];
  if (result.changed.length === 0) {
    lines.push(
      result.skipped.length === 0 && result.binaryCount === 0
        ? `No credential or private identifier material found under ${displayRoot}.`
        : `No files were rewritten under ${displayRoot}.`
    );
  } else {
    const byKind = [...result.totals.entries()]
      .sort((left, right) => (left[0] < right[0] ? -1 : 1))
      .map(([id, count]) => `${id} x${count}`)
      .join(", ");
    lines.push(`Redacted ${result.changed.length} file(s) under ${displayRoot}: ${byKind}.`);
    for (const file of result.changed) {
      lines.push(`  ${safeDisplayPath(file.path)}: ${file.counts.join(", ")}`);
    }
  }
  // Report opaque files so "nothing found" cannot be mistaken for "nothing looked at."
  if (result.binaryCount > 0) {
    lines.push(`  ${result.binaryCount} opaque or binary file(s) were not scanned.`);
  }
  for (const file of result.skipped) {
    lines.push(
      `  WARNING: ${safeDisplayPath(file.path)} could not be safely prepared because it ` +
        `${safeDisplayPath(file.reason)}.`
    );
  }
  return `${lines.join("\n")}\n`;
}
function usage() {
  return `Usage: node scripts/unity/redact-unity-artifacts.js <directory> [<directory>...]
Rewrites credential and private identifier values in every text file under each directory before
the tree is uploaded as a CI artifact. Missing directories are skipped so one call can cover every
test mode of a run.
`;
}
function parseArgs(argv) {
  const roots = [];
  for (let index = 2; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === "--help" || argument === "-h") {
      return { roots, help: true };
    }
    if (argument.startsWith("-")) {
      fail(`Unknown option ${argument}.`);
    }
    roots.push(argument);
  }
  return { roots, help: false };
}
function runCli(argv, write = (text) => process.stdout.write(text)) {
  const { roots, help } = parseArgs(argv);
  if (help || roots.length === 0) {
    write(usage());
    return help ? 0 : 1;
  }
  const blocked = [];
  for (const root of roots) {
    const resolved = path.resolve(root);
    try {
      fs.lstatSync(resolved);
    } catch (error) {
      if (error.code === "ENOENT") {
        write(`Skipping ${safeDisplayPath(root)}; it does not exist.\n`);
        continue;
      }
      throw error;
    }
    const result = redactDirectory(resolved);
    write(formatSummary(root, result));
    blocked.push(...result.skipped.map((file) => safeDisplayPath(path.join(root, file.path))));
  }
  if (blocked.length > 0) {
    write(
      `Refusing to report success: ${blocked.length} file(s) could not be safely prepared for ` +
        `upload.\n  ${blocked.join("\n  ")}\n`
    );
    return 2;
  }
  return 0;
}
if (require.main === module) {
  try {
    process.exitCode = runCli(process.argv);
  } catch (error) {
    process.stderr.write(`${safeDisplayPath(error.message)}\n`);
    process.exitCode = 1;
  }
}
module.exports = { formatSummary, parseArgs, redactDirectory, runCli, safeDisplayPath, usage };
