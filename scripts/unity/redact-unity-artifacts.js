"use strict";

/**
 * Remove credential material from Unity build output before it is uploaded as a CI artifact.
 *
 * Unity writes its license identity into `unity.log` and `configure.log` during activation. GitHub
 * masks registered secrets in rendered job logs, but it does not touch the bytes of an uploaded
 * artifact, so on a public repository those logs publish the serial to anyone who can download the
 * artifact. This rewrites every credential value in place and reports what it removed by kind.
 *
 * Run it over the artifact root before every upload step. It is idempotent: no placeholder it
 * writes can match a credential pattern, so a second pass over redacted output changes nothing.
 */

const fs = require("fs");
const path = require("path");
const { decodeText, encodeText, redactCredentials } = require("./credential-patterns.js");

/** Larger than any Unity log this repository produces, and small enough to read into memory. */
const MAXIMUM_FILE_BYTES = 256 * 1024 * 1024;

function fail(message) {
  throw new Error(message);
}

function toPosixPath(value) {
  return value.split(path.sep).join("/");
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
      }
    }
  };
  walk(root);
  return found.sort();
}

/**
 * Redact every file under `root`. Skipped files are reported rather than silently ignored, because
 * a file this cannot read is a file whose contents were never checked.
 */
function redactDirectory(root) {
  if (!fs.existsSync(root) || !fs.statSync(root).isDirectory()) {
    fail(`${root} is not a directory.`);
  }
  const changed = [];
  const skipped = [];
  const totals = new Map();
  let binaryCount = 0;
  for (const absolute of listFiles(root)) {
    const relative = toPosixPath(path.relative(root, absolute));
    const size = fs.statSync(absolute).size;
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
      skipped.push({ path: relative, reason: `could not be read: ${error.message}` });
      continue;
    }
    const decoded = decodeText(bytes);
    if (decoded === undefined) {
      binaryCount += 1;
      continue;
    }
    const { redacted, counts } = redactCredentials(decoded.text);
    if (counts.size === 0) {
      continue;
    }
    try {
      fs.writeFileSync(absolute, encodeText(redacted, decoded.encoding));
    } catch (error) {
      fail(`${relative} contains credential material but could not be rewritten: ${error.message}`);
    }
    for (const [id, count] of counts) {
      totals.set(id, (totals.get(id) ?? 0) + count);
    }
    changed.push({ path: relative, counts: [...counts.keys()].sort() });
  }
  return { changed, skipped, totals, binaryCount };
}

function formatSummary(root, result) {
  const lines = [];
  if (result.changed.length === 0) {
    lines.push(`No credential material found under ${root}.`);
  } else {
    const byKind = [...result.totals.entries()]
      .sort((left, right) => (left[0] < right[0] ? -1 : 1))
      .map(([id, count]) => `${id} x${count}`)
      .join(", ");
    lines.push(`Redacted ${result.changed.length} file(s) under ${root}: ${byKind}.`);
    for (const file of result.changed) {
      lines.push(`  ${file.path}: ${file.counts.join(", ")}`);
    }
  }
  // Binary files are not scanned. Report the count so "nothing found" can never be confused with
  // "nothing looked at", without listing every DLL and PDB in a player directory.
  if (result.binaryCount > 0) {
    lines.push(`  ${result.binaryCount} binary file(s) were not scanned.`);
  }
  for (const file of result.skipped) {
    lines.push(`  WARNING: ${file.path} was not scanned because it ${file.reason}.`);
  }
  return `${lines.join("\n")}\n`;
}

function usage() {
  return `Usage: node scripts/unity/redact-unity-artifacts.js <directory> [<directory>...]

Rewrites credential values in every text file under each directory before the tree is uploaded as a
CI artifact. Missing directories are skipped so one call can cover every test mode of a run.
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

/**
 * A file this could not examine is a file whose contents were never checked, so the run cannot
 * claim the tree is clean. Returning zero after a skip would let the uploads, which now require
 * this step to have succeeded, publish a file nobody looked at. Binary files are different: those
 * were examined and judged not to be text, so they do not count as skipped.
 */
function runCli(argv, write = (text) => process.stdout.write(text)) {
  const { roots, help } = parseArgs(argv);
  if (help || roots.length === 0) {
    write(usage());
    return help ? 0 : 1;
  }
  const unexamined = [];
  for (const root of roots) {
    const resolved = path.resolve(root);
    if (!fs.existsSync(resolved)) {
      write(`Skipping ${root}; it does not exist.\n`);
      continue;
    }
    const result = redactDirectory(resolved);
    write(formatSummary(root, result));
    unexamined.push(...result.skipped.map((file) => `${root}/${file.path}`));
  }
  if (unexamined.length > 0) {
    write(
      `Refusing to report success: ${unexamined.length} file(s) could not be examined, so they ` +
        `cannot be shown to be free of credentials.\n  ${unexamined.join("\n  ")}\n`
    );
    return 2;
  }
  return 0;
}

if (require.main === module) {
  try {
    process.exitCode = runCli(process.argv);
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    process.exitCode = 1;
  }
}

module.exports = { formatSummary, parseArgs, redactDirectory, runCli, usage };
