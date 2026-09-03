"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { test } = require("node:test");

const {
  CREDENTIAL_PATTERNS,
  findCredentials,
  decodeText,
  encodeText,
  redactCredentials
} = require("../unity/credential-patterns.js");
const {
  formatSummary,
  parseArgs,
  redactDirectory,
  runCli,
  usage
} = require("../unity/redact-unity-artifacts.js");

/**
 * Every value below is synthetic and matches no credential this project has ever held. They exist
 * only to make each declared pattern fire, so nothing here is a live secret if the file leaks.
 */
const FAKE_SERIAL = "SC-FAKE-FAKE-FAKE-FAKE-FAKE";
const FAKE_LICENSE_ID = "FAKE-LICENSE-0000-0000";
const FAKE_GITHUB_TOKEN = `ghp_${"FAKEfake0123456789".repeat(2)}`;
const FAKE_AWS_KEY = "AKIA0000FAKE0000FAKE";
const FAKE_BEARER = "FAKEbearerFAKEbearerFAKEbearer";
const FAKE_PASSWORD = "fake-password-value-0000";
const FAKE_KEY_BODY = "FAKEkeybodyFAKEkeybodyFAKEkeybody";
const FAKE_PEM = `-----BEGIN RSA PRIVATE KEY-----\n${FAKE_KEY_BODY}\n-----END RSA PRIVATE KEY-----`;

/** `[patternId, sampleText, theSubstringThatMustDisappear]`, one row per declared pattern. */
const LEAK_CASES = Object.freeze([
  ["pem-private-key", `key follows\n${FAKE_PEM}\ndone\n`, FAKE_KEY_BODY],
  ["unity-license-id", `<License id="${FAKE_LICENSE_ID}" version="1.0">\n`, FAKE_LICENSE_ID],
  ["unity-serial", `Activated with serial ${FAKE_SERIAL} today\n`, FAKE_SERIAL],
  ["github-token", `remote token ${FAKE_GITHUB_TOKEN} rejected\n`, FAKE_GITHUB_TOKEN],
  ["aws-access-key-id", `uploader used ${FAKE_AWS_KEY} for the bucket\n`, FAKE_AWS_KEY],
  ["http-bearer-token", `Authorization: Bearer ${FAKE_BEARER}\n`, FAKE_BEARER],
  ["credential-assignment", `UNITY_PASSWORD=${FAKE_PASSWORD}\n`, FAKE_PASSWORD]
]);

function temporaryDirectory() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "redact-unity-artifacts-test-"));
}

/**
 * A miniature artifact tree: a clean log, two nested leaking logs, and a binary blob that carries
 * serial-shaped bytes next to a NUL so a skipped-binary regression cannot hide.
 */
const CLEAN_LOG = "[Licensing::Client] Successfully resolved entitlements in 0.284 seconds\n";
const BINARY_BLOB = Buffer.concat([
  Buffer.from([0x00, 0x01, 0x02, 0xff]),
  Buffer.from(FAKE_SERIAL, "utf8"),
  Buffer.from([0x00])
]);

function writeArtifactTree() {
  const root = temporaryDirectory();
  fs.mkdirSync(path.join(root, "logs", "deep"), { recursive: true });
  fs.writeFileSync(path.join(root, "clean.log"), CLEAN_LOG);
  fs.writeFileSync(
    path.join(root, "logs", "unity.log"),
    `serial ${FAKE_SERIAL} accepted\nreactivated with ${FAKE_SERIAL}\n`
  );
  fs.writeFileSync(
    path.join(root, "logs", "deep", "configure.log"),
    `Authorization: Bearer ${FAKE_BEARER}\nserial ${FAKE_SERIAL}\n`
  );
  fs.writeFileSync(path.join(root, "logs", "GameAssembly.bin"), BINARY_BLOB);
  return root;
}

/** A 16-character synthetic value, long enough to satisfy the assignment pattern's length rule. */
const FAKE_ASSIGNMENT_VALUE = "FAKEfake00000000";

/** A Unity log that picked up one stray NUL from native subprocess output, and still leaks. */
const STRAY_NUL_LOG = Buffer.concat([
  Buffer.from("Refreshing native plugins\n", "latin1"),
  Buffer.alloc(1),
  Buffer.from(`\nActivated with serial ${FAKE_SERIAL}\n`, "latin1")
]);

/** Roughly four kilobytes of log text carrying eight scattered NUL bytes. */
const SPARSE_NUL_LOG = Buffer.concat(
  Array.from({ length: 8 }, () =>
    Buffer.concat([Buffer.from("a".repeat(512), "latin1"), Buffer.alloc(1)])
  )
);

/** The opening of a managed DLL: a short header, a long NUL run, then dense payload bytes. */
const DLL_LIKE_BLOB = Buffer.concat([
  Buffer.from("MZ", "latin1"),
  Buffer.alloc(4000),
  Buffer.from("x".repeat(4000), "latin1")
]);

/** UTF-16 text with no byte-order mark, which is genuinely unreadable without one. */
const BOMLESS_UTF16_LOG = Buffer.from("Unity build log line\n".repeat(8), "utf16le");

/** What Windows PowerShell writes by default: UTF-16LE behind a byte-order mark. */
const UTF16LE_BOM_LOG = Buffer.concat([
  Buffer.from([0xff, 0xfe]),
  Buffer.from(`Activated with serial ${FAKE_SERIAL}\n`, "utf16le")
]);

/** Bytes that are not valid UTF-8, as a Unity log holding raw native output can be. */
const INVALID_UTF8_BYTES = Buffer.from([0x80, 0xfe, 0x41, 0x0a]);

/**
 * `[label, bytes, expectedEncoding]`, where `undefined` means the bytes are genuinely binary and
 * are left unscanned.
 *
 * "Contains a NUL" is the usual binary test and it is the wrong rule here. A Unity log picks up
 * stray NUL bytes from native subprocess output, and treating one such byte as proof of a binary
 * silently disabled redaction for the whole file and published the serial in the uploaded
 * artifact. A real binary is dense with NULs; a log with a handful is still a log and must stay on
 * the text side where the redactor can reach it.
 */
const DECODE_CASES = Object.freeze([
  ["a short log with one stray NUL", STRAY_NUL_LOG, "latin1"],
  ["a four-kilobyte log with eight stray NULs", SPARSE_NUL_LOG, "latin1"],
  ["a DLL-like blob", DLL_LIKE_BLOB, undefined],
  ["UTF-16 with no byte-order mark", BOMLESS_UTF16_LOG, undefined],
  ["a UTF-16LE log behind a byte-order mark", UTF16LE_BOM_LOG, "utf16le"],
  ["a plain ASCII log", Buffer.from(CLEAN_LOG, "latin1"), "latin1"]
]);

/**
 * Assignment keywords written with no vendor prefix. The pattern's prefix class accepts an empty
 * prefix, so a bare `TOKEN=` is as much a credential assignment as `UNITY_SERIAL=`, and CI shells
 * and Unity build scripts write both shapes.
 */
const BARE_KEYWORDS = Object.freeze([
  "TOKEN",
  "SECRET",
  "PASSWORD",
  "API_KEY",
  "AWS_SECRET_ACCESS_KEY"
]);

for (const [id, text, secret] of LEAK_CASES) {
  test(`${id} is found and its value is destroyed`, () => {
    assert.deepEqual(
      findCredentials(text).map((entry) => entry.id),
      [id],
      `${id}: findCredentials must report exactly this kind and nothing else`
    );
    const { redacted, counts } = redactCredentials(text);
    assert.ok(!redacted.includes(secret), `${id}: the sensitive substring must be gone`);
    assert.ok(redacted.includes(`<redacted:${id}>`), `${id}: the placeholder must name the kind`);
    assert.deepEqual([...counts], [[id, 1]], `${id}: exactly one value must be counted`);
  });
}

test("the leak table exercises every declared credential pattern", () => {
  assert.deepEqual(
    LEAK_CASES.map(([id]) => id).sort(),
    CREDENTIAL_PATTERNS.map((entry) => entry.id).sort(),
    "a new credential pattern must arrive with a LEAK_CASES row that proves it fires"
  );
});

for (const [id, text, expected] of [
  [
    "http-bearer-token",
    `Authorization: Bearer ${FAKE_BEARER}\n`,
    "Authorization: Bearer <redacted:http-bearer-token>\n"
  ],
  [
    "credential-assignment",
    `UNITY_PASSWORD=${FAKE_PASSWORD}\n`,
    "UNITY_PASSWORD=<redacted:credential-assignment>\n"
  ],
  [
    "unity-license-id",
    `<License id="${FAKE_LICENSE_ID}" version="1.0">\n`,
    '<License id="<redacted:unity-license-id>" version="1.0">\n'
  ]
]) {
  test(`${id} keeps the label that says which credential was removed`, () => {
    assert.equal(
      redactCredentials(text).redacted,
      expected,
      `${id}: only the value may be destroyed, the surrounding label must survive`
    );
  });
}

test("a PEM key is redacted as one block, not just its header", () => {
  const { redacted } = redactCredentials(`prelude\n${FAKE_PEM}\nepilogue\n`);
  assert.equal(
    redacted,
    "prelude\n<redacted:pem-private-key>\nepilogue\n",
    "PEM: the body bytes and the END line must go with the header"
  );
  assert.ok(!redacted.includes("PRIVATE KEY"), "PEM: no part of the armour may survive");
});

test("redacting already-redacted text is a no-op", () => {
  // The CI step may run over the same tree more than once, so a second pass must find nothing.
  for (const [id, text] of LEAK_CASES) {
    const once = redactCredentials(text);
    const twice = redactCredentials(once.redacted);
    assert.equal(twice.redacted, once.redacted, `${id}: a second pass must not change the text`);
    assert.equal(twice.counts.size, 0, `${id}: a second pass must report nothing removed`);
    assert.deepEqual(
      findCredentials(once.redacted).map((entry) => entry.id),
      [],
      `${id}: no placeholder may look like a credential to the sealing backstop`
    );
  }
});

test("a redacted Unity license id is not mistaken for a live one", () => {
  const once = redactCredentials(`<License id="${FAKE_LICENSE_ID}" version="1.0">\n`);
  assert.equal(
    redactCredentials(once.redacted).counts.size,
    0,
    "license: a second pass is a no-op"
  );
  assert.deepEqual(
    findCredentials(once.redacted).map((entry) => entry.id),
    [],
    "license: a scrubbed log must still be sealable"
  );
});

for (const [label, text] of [
  ["a masked GitHub token", "GITHUB_TOKEN=***\n"],
  ["a masked Unity serial", "UNITY_SERIAL=***\n"],
  // Paired with the bare-keyword tests below. `API_KEY=` now matches the keyword class, so
  // this row proves the twelve-character minimum rejects the value rather than passing
  // because the keyword itself went unrecognized.
  ["a value too short to be a key", "API_KEY=short\n"],
  ["a bearer header with a short value", "Authorization: Bearer short\n"],
  ["ordinary prose", "The build uploaded a token to the store without a password.\n"],
  ["a Unity licensing log line", CLEAN_LOG],
  ["a Unity engine banner", "Initialize engine version: 6000.5.2f1 (b9e1b8d9d3a2)\n"]
]) {
  test(`${label} is left byte-identical`, () => {
    const { redacted, counts } = redactCredentials(text);
    assert.equal(redacted, text, `${label}: a false positive would train operators to skip this`);
    assert.equal(counts.size, 0, `${label}: nothing may be counted as removed`);
    assert.deepEqual(findCredentials(text), [], `${label}: nothing may be reported as found`);
  });
}

test("redactDirectory rewrites only the leaking text files under the tree", () => {
  const root = writeArtifactTree();
  const result = redactDirectory(root);
  assert.deepEqual(
    result.changed.map((file) => file.path),
    ["logs/deep/configure.log", "logs/unity.log"],
    "tree: only the two leaking logs may be rewritten"
  );
  assert.deepEqual(
    result.changed.map((file) => file.counts),
    [["http-bearer-token", "unity-serial"], ["unity-serial"]],
    "tree: each rewritten file reports the kinds it carried"
  );
  assert.deepEqual(
    [...result.totals].sort(),
    [
      ["http-bearer-token", 1],
      ["unity-serial", 3]
    ],
    "tree: counts are aggregated per pattern id across the whole walk"
  );
  assert.deepEqual(result.skipped, [], "tree: nothing in a readable tree may be skipped");
  assert.equal(
    fs.readFileSync(path.join(root, "clean.log"), "utf8"),
    CLEAN_LOG,
    "tree: a clean file must not be touched"
  );
  assert.equal(
    decodeText(BINARY_BLOB),
    undefined,
    "tree: the blob must actually decode as binary or the skip below proves nothing"
  );
  assert.deepEqual(
    fs.readFileSync(path.join(root, "logs", "GameAssembly.bin")),
    BINARY_BLOB,
    "tree: a binary file is skipped, so its serial-shaped bytes survive unchanged"
  );
  const scrubbed = fs.readFileSync(path.join(root, "logs", "unity.log"), "utf8");
  assert.ok(!scrubbed.includes(FAKE_SERIAL), "tree: the rewritten log must not keep the serial");
  assert.equal(
    scrubbed,
    "serial <redacted:unity-serial> accepted\nreactivated with <redacted:unity-serial>\n",
    "tree: every occurrence in a file is replaced, not just the first"
  );
  const second = redactDirectory(root);
  assert.deepEqual(second.changed, [], "tree: a second pass must rewrite nothing");
  assert.deepEqual([...second.totals], [], "tree: a second pass must report nothing removed");
});

test("redactDirectory refuses a path that is not a directory", () => {
  const root = writeArtifactTree();
  assert.throws(
    () => redactDirectory(path.join(root, "clean.log")),
    /clean\.log is not a directory\./,
    "a file target must fail rather than be walked"
  );
  assert.throws(
    () => redactDirectory(path.join(root, "absent")),
    /absent is not a directory\./,
    "a missing target must fail rather than report a clean tree"
  );
});

test("redactDirectory reports a file it cannot read instead of ignoring it", (t) => {
  if (!process.getuid || process.getuid() === 0) {
    t.skip("root can read any file, so an unreadable file cannot be staged");
    return;
  }
  const root = temporaryDirectory();
  const locked = path.join(root, "locked.log");
  fs.writeFileSync(locked, `serial ${FAKE_SERIAL}\n`);
  fs.chmodSync(locked, 0o000);
  t.after(() => fs.chmodSync(locked, 0o600));
  const result = redactDirectory(root);
  assert.deepEqual(result.changed, [], "unreadable: nothing can be rewritten");
  assert.equal(result.skipped.length, 1, "unreadable: the file must be reported exactly once");
  assert.equal(result.skipped[0].path, "locked.log", "unreadable: the report names the file");
  assert.match(
    result.skipped[0].reason,
    /^could not be read: /,
    "unreadable: an unchecked file must never be silently treated as clean"
  );
});

test("the CLI skips a missing directory, demands a target, and explains itself", () => {
  const written = [];
  const write = (text) => written.push(text);
  const argv = (...rest) => ["node", "redact-unity-artifacts.js", ...rest];
  const missing = path.join(temporaryDirectory(), "absent");
  assert.equal(runCli(argv(missing), write), 0, "cli: a missing directory is not a failure");
  assert.match(
    written.join(""),
    /^Skipping .*absent; it does not exist\.\n$/,
    "cli: a missing directory is named in the output"
  );
  written.length = 0;
  assert.equal(runCli(argv(), write), 1, "cli: no target is a usage error");
  assert.equal(written.join(""), usage(), "cli: a usage error prints the usage text");
  written.length = 0;
  assert.equal(runCli(argv("--help"), write), 0, "cli: --help is not an error");
  assert.equal(written.join(""), usage(), "cli: --help prints the usage text");
  assert.throws(
    () => runCli(argv("--nope"), write),
    /Unknown option --nope\./,
    "cli: an unknown option must fail loudly rather than be read as a directory"
  );
  assert.deepEqual(
    parseArgs(argv("first", "second")),
    { roots: ["first", "second"], help: false },
    "cli: every positional argument becomes a root so one call covers a whole run"
  );
});

test("the CLI redacts a real tree and summarizes what it removed", () => {
  const root = writeArtifactTree();
  const written = [];
  assert.equal(
    runCli(["node", "cli", root], (text) => written.push(text)),
    0,
    "cli: a tree exits 0"
  );
  assert.match(
    written.join(""),
    /^Redacted 2 file\(s\) under .*: http-bearer-token x1, unity-serial x3\./,
    "cli: the summary reports kinds and counts without echoing any value"
  );
  assert.ok(!written.join("").includes(FAKE_SERIAL), "cli: output must never echo a credential");
});

test("the CLI refuses to report success when a file could not be examined", (t) => {
  // The uploads now require this step to have succeeded, so exiting 0 after a skip would publish a
  // file nobody looked at. A binary file is not a skip: it was examined and judged not to be text.
  if (process.getuid && process.getuid() === 0) {
    t.skip("root can read any file, so the unreadable case cannot be staged");
    return;
  }
  const root = temporaryDirectory();
  fs.writeFileSync(path.join(root, "clean.log"), "nothing here\n");
  fs.writeFileSync(path.join(root, "GameAssembly.bin"), BINARY_BLOB);
  const unreadable = path.join(root, "locked.log");
  fs.writeFileSync(unreadable, `serial ${FAKE_SERIAL}\n`);
  fs.chmodSync(unreadable, 0o000);
  t.after(() => fs.chmodSync(unreadable, 0o644));

  const written = [];
  assert.equal(
    runCli(["node", "cli", root], (text) => written.push(text)),
    2,
    "cli: a file that was not examined must not exit 0, or the gated upload publishes it"
  );
  const output = written.join("");
  assert.match(output, /Refusing to report success: 1 file\(s\) could not be examined/);
  assert.match(output, /locked\.log/, "cli: the refusal must name the file to scrub");
  assert.ok(!output.includes(FAKE_SERIAL), "cli: the refusal must not echo the credential");
});

test("the CLI still exits 0 when the only unscanned files are binary", () => {
  const root = temporaryDirectory();
  fs.writeFileSync(path.join(root, "clean.log"), "nothing here\n");
  fs.writeFileSync(path.join(root, "GameAssembly.bin"), BINARY_BLOB);
  const written = [];
  assert.equal(
    runCli(["node", "cli", root], (text) => written.push(text)),
    0,
    "cli: a binary file was examined and judged not text, so it is not an unexamined file"
  );
  assert.match(written.join(""), /1 binary file\(s\) were not scanned\./);
});

test("formatSummary renders a clean tree, a redacted tree, and a skipped file", () => {
  assert.equal(
    formatSummary("artifacts", { changed: [], skipped: [], totals: new Map() }),
    "No credential material found under artifacts.\n",
    "summary: a clean tree says so plainly"
  );
  assert.equal(
    formatSummary("artifacts", {
      changed: [
        { path: "logs/deep/configure.log", counts: ["http-bearer-token", "unity-serial"] },
        { path: "logs/unity.log", counts: ["unity-serial"] }
      ],
      skipped: [],
      totals: new Map([
        ["unity-serial", 3],
        ["http-bearer-token", 1]
      ])
    }),
    "Redacted 2 file(s) under artifacts: http-bearer-token x1, unity-serial x3.\n" +
      "  logs/deep/configure.log: http-bearer-token, unity-serial\n" +
      "  logs/unity.log: unity-serial\n",
    "summary: per-kind totals are sorted by id and each file lists its kinds"
  );
  assert.equal(
    formatSummary("artifacts", {
      changed: [],
      skipped: [{ path: "locked.log", reason: "could not be read: EACCES" }],
      totals: new Map()
    }),
    "No credential material found under artifacts.\n" +
      "  WARNING: locked.log was not scanned because it could not be read: EACCES.\n",
    "summary: a file that was not scanned is a warning, not a silent omission"
  );
});

for (const [label, bytes, expectedEncoding] of DECODE_CASES) {
  test(`decodeText classifies ${label}`, () => {
    const decoded = decodeText(bytes);
    if (expectedEncoding === undefined) {
      assert.equal(decoded, undefined, `${label}: dense NUL bytes must be judged binary`);
      return;
    }
    assert.notEqual(decoded, undefined, `${label}: text must stay scannable, not be skipped`);
    assert.equal(
      decoded.encoding,
      expectedEncoding,
      `${label}: the decoder must report the encoding it used`
    );
    assert.deepEqual(
      encodeText(decoded.text, decoded.encoding),
      bytes,
      `${label}: decoding then re-encoding must be byte-exact`
    );
  });
}

test("a log carrying a stray NUL is redacted and keeps that byte", () => {
  const root = temporaryDirectory();
  const target = path.join(root, "unity.log");
  fs.writeFileSync(target, STRAY_NUL_LOG);
  const result = redactDirectory(root);
  assert.deepEqual(
    result.changed.map((file) => file.path),
    ["unity.log"],
    "stray NUL: one NUL byte must not take a whole Unity log out of the scan"
  );
  const rewritten = fs.readFileSync(target);
  assert.ok(!rewritten.includes(FAKE_SERIAL), "stray NUL: the serial must be gone");
  assert.ok(
    rewritten.includes("<redacted:unity-serial>"),
    "stray NUL: the placeholder must name the kind that was removed"
  );
  assert.ok(rewritten.includes(0), "stray NUL: the byte itself must survive the rewrite");
});

test("a UTF-16LE log behind a byte-order mark is redacted and re-encoded as UTF-16LE", () => {
  const root = temporaryDirectory();
  const target = path.join(root, "configure.log");
  fs.writeFileSync(target, UTF16LE_BOM_LOG);
  const result = redactDirectory(root);
  assert.deepEqual(
    result.changed.map((file) => file.path),
    ["configure.log"],
    "utf16le: a PowerShell-written log is text and must be scanned"
  );
  const rewritten = fs.readFileSync(target);
  assert.deepEqual(
    rewritten.subarray(0, 2),
    Buffer.from([0xff, 0xfe]),
    "utf16le: the byte-order mark must survive so the file still reads as UTF-16LE"
  );
  const text = rewritten.toString("utf16le");
  assert.ok(!text.includes(FAKE_SERIAL), "utf16le: the serial must be gone");
  assert.ok(
    text.includes("<redacted:unity-serial>"),
    "utf16le: the rewrite must be re-encoded in the encoding it was read from"
  );
});

test("bytes that are not valid UTF-8 round-trip byte for byte", () => {
  const decoded = decodeText(INVALID_UTF8_BYTES);
  assert.equal(decoded.encoding, "latin1", "invalid UTF-8: the decode maps bytes one to one");
  assert.deepEqual(
    encodeText(decoded.text, decoded.encoding),
    INVALID_UTF8_BYTES,
    "invalid UTF-8: a UTF-8 decode would substitute U+FFFD and corrupt the file on write"
  );
  const root = temporaryDirectory();
  const target = path.join(root, "player.log");
  fs.writeFileSync(target, INVALID_UTF8_BYTES);
  const result = redactDirectory(root);
  assert.deepEqual(result.changed, [], "invalid UTF-8: a file with no credential is not rewritten");
  assert.deepEqual(
    fs.readFileSync(target),
    INVALID_UTF8_BYTES,
    "invalid UTF-8: every byte of an untouched file must survive the walk"
  );
});

for (const keyword of BARE_KEYWORDS) {
  test(`${keyword}= is a credential assignment even with no vendor prefix`, () => {
    const text = `${keyword}=${FAKE_ASSIGNMENT_VALUE}\n`;
    assert.deepEqual(
      findCredentials(text).map((entry) => entry.id),
      ["credential-assignment"],
      `${keyword}: a bare keyword assignment must be reported as a leak`
    );
    assert.equal(
      redactCredentials(text).redacted,
      `${keyword}=<redacted:credential-assignment>\n`,
      `${keyword}: only the value may be destroyed, the keyword must survive`
    );
  });
}

test("a short assignment value is rejected by the length rule, not by an unmatched keyword", () => {
  // The review found the `API_KEY=short` case passing for the wrong reason: the old pattern never
  // matched a bare `API_KEY=` at all, so the twelve-character minimum it claimed to prove was never
  // reached. The long value below is what makes the short one mean anything.
  assert.deepEqual(
    findCredentials(`API_KEY=${FAKE_ASSIGNMENT_VALUE}\n`).map((entry) => entry.id),
    ["credential-assignment"],
    "short value: the keyword must match, or the length rule is never exercised"
  );
  assert.deepEqual(
    findCredentials("API_KEY=short\n"),
    [],
    "short value: a value too short to be a key is not a leak"
  );
  assert.deepEqual(
    findCredentials("GITHUB_TOKEN=***\n"),
    [],
    "masked value: a masked assignment is not a leak"
  );
});

test("redactDirectory counts the binary files it never scanned", () => {
  // "Nothing found" must never be confusable with "nothing looked at". A binary file is a file
  // whose bytes were never checked, so the count is reported rather than dropped on the floor.
  const root = writeArtifactTree();
  const result = redactDirectory(root);
  assert.equal(result.binaryCount, 1, "binary: the skipped blob must be counted, not forgotten");
  assert.match(
    formatSummary("artifacts", result),
    /^ {2}1 binary file\(s\) were not scanned\.$/m,
    "binary: the summary must say how many files were never scanned"
  );
  assert.match(
    formatSummary("artifacts", { changed: [], skipped: [], totals: new Map(), binaryCount: 4 }),
    /^No credential material found under artifacts\.\n {2}4 binary file\(s\) were not scanned\.\n$/,
    "binary: a tree with no findings still reports what it could not read"
  );
});
