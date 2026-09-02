"use strict";

/**
 * The one list of credential shapes this repository looks for in build output.
 *
 * Two consumers share it and must not drift apart:
 *   - `redact-unity-artifacts.js` rewrites every match before an artifact is uploaded;
 *   - `perf-evidence-bundle.js` refuses to seal a bundle that still contains one.
 *
 * The second is the backstop for the first. If redaction is skipped or a new log format appears,
 * sealing fails closed rather than publishing credential material to an immutable release asset.
 *
 * Each entry redacts to `<redacted:id>`. When `prefixGroup` is set, capture group 1 is the label to
 * keep so a log line still says which credential was removed, and only the value is destroyed. No
 * placeholder can match any pattern here, so redaction is idempotent.
 */
const CREDENTIAL_PATTERNS = Object.freeze([
  {
    id: "pem-private-key",
    description: "a PEM private key",
    // Prefer the whole block, but a header with no terminator still counts: the bytes after it
    // are key material, so redacting only the header would leave the key readable. A PEM header
    // in Unity build output is never legitimate, so consuming the remainder is the safe choice.
    pattern:
      /-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----[\s\S]*?-----END (?:[A-Z ]+ )?PRIVATE KEY-----|-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----[\s\S]*/
  },
  {
    id: "unity-license-id",
    description: "a Unity license identifier",
    // The value class excludes "<" so this cannot match its own placeholder. Without that, a
    // correctly scrubbed unity.log would keep reporting a hit and could never be sealed.
    pattern: /(<License\b[^>]*\bid=")[^"<]+/,
    prefixGroup: 1
  },
  {
    id: "unity-serial",
    description: "a Unity serial",
    pattern: /\bS[CBP]-[0-9A-Z]{4}(?:-[0-9A-Z]{4}){4}\b/
  },
  {
    id: "github-token",
    description: "a GitHub token",
    pattern: /\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})\b/
  },
  {
    id: "aws-access-key-id",
    description: "an AWS access key id",
    pattern: /\bAKIA[0-9A-Z]{16}\b/
  },
  {
    id: "http-bearer-token",
    description: "an HTTP bearer token",
    pattern: /(\bBearer\s+)[A-Za-z0-9._~+/=-]{20,}/,
    prefixGroup: 1
  },
  {
    id: "credential-assignment",
    description: "a credential assignment",
    // The value must look like real credential material. A masked `TOKEN=***` in a CI log is not a
    // leak, and failing on one would train operators to bypass this check.
    pattern:
      /(\b(?:UNITY_(?:SERIAL|EMAIL|PASSWORD)|[A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|API_KEY|ACCESS_KEY))["']?\s*[=:]\s*["']?)[A-Za-z0-9._~+/=@-]{12,}/,
    prefixGroup: 1
  }
]);

/**
 * How much of a file is examined before deciding it is binary, and how dense NUL bytes must be
 * within that sample to earn the label.
 *
 * "Contains a NUL" is the usual heuristic and it is wrong here. Unity logs pick up stray NULs from
 * native subprocess output, and one such byte would mark a whole log binary and silently publish
 * the serial. Real binaries are dense with NULs; a log with a handful is still a log. The threshold
 * separates the two and errs toward scanning, because a missed credential is worse than a wasted
 * scan. A file is only rewritten when a pattern matches, so scanning a misjudged binary is inert.
 */
const BINARY_PROBE_BYTES = 8192;
const BINARY_NUL_RATIO = 0.01;

/**
 * Decode a file for scanning, or return `undefined` when it is genuinely binary.
 *
 * UTF-16 is detected by its byte-order mark first, because a UTF-16 log is full of NUL bytes and
 * would otherwise be dismissed as binary. Windows PowerShell writes UTF-16LE by default, so this
 * is a real shape in CI output.
 *
 * Everything else is decoded as `latin1`, which maps bytes one to one and re-encodes exactly. That
 * matters more than understanding multi-byte text: a log may hold invalid UTF-8, and decoding it as
 * UTF-8 would replace those bytes with U+FFFD and corrupt the file on write. Every credential shape
 * here is ASCII, so a byte-exact mapping loses no matches.
 */
function decodeText(bytes) {
  if (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe) {
    return { text: bytes.toString("utf16le"), encoding: "utf16le" };
  }
  if (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff) {
    return { text: Buffer.from(bytes).swap16().toString("utf16le"), encoding: "utf16be" };
  }
  const probe = bytes.subarray(0, BINARY_PROBE_BYTES);
  let nulCount = 0;
  for (const byte of probe) {
    nulCount += byte === 0 ? 1 : 0;
  }
  // The allowance of one NUL on top of the ratio keeps a short log with a single stray byte on the
  // text side, where a pure ratio would misjudge it.
  if (nulCount > 1 + probe.length * BINARY_NUL_RATIO) {
    return undefined;
  }
  return { text: bytes.toString("latin1"), encoding: "latin1" };
}

/** Re-encode text produced by `decodeText` back to the byte layout it came from. */
function encodeText(text, encoding) {
  if (encoding === "utf16le") {
    return Buffer.from(text, "utf16le");
  }
  if (encoding === "utf16be") {
    return Buffer.from(text, "utf16le").swap16();
  }
  return Buffer.from(text, "latin1");
}

function globalRegExp(entry) {
  return new RegExp(entry.pattern.source, `${entry.pattern.flags}g`);
}

/** Every credential kind present in `text`, in declaration order. */
function findCredentials(text) {
  return CREDENTIAL_PATTERNS.filter((entry) => entry.pattern.test(text));
}

/**
 * Replace every credential value in `text`. Returns the rewritten text and a count per pattern id
 * so a caller can report what it removed without ever echoing what it removed.
 */
function redactCredentials(text) {
  const counts = new Map();
  let redacted = text;
  for (const entry of CREDENTIAL_PATTERNS) {
    let replaced = 0;
    redacted = redacted.replace(globalRegExp(entry), (...match) => {
      replaced += 1;
      const prefix = entry.prefixGroup ? match[entry.prefixGroup] : "";
      return `${prefix}<redacted:${entry.id}>`;
    });
    if (replaced > 0) {
      counts.set(entry.id, replaced);
    }
  }
  return { redacted, counts };
}

module.exports = {
  BINARY_NUL_RATIO,
  BINARY_PROBE_BYTES,
  CREDENTIAL_PATTERNS,
  decodeText,
  encodeText,
  findCredentials,
  redactCredentials
};
