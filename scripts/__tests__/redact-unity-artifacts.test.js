"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { test } = require("node:test");

const {
  CREDENTIAL_PATTERNS,
  findCredentials,
  looksBinary,
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
  assert.ok(
    looksBinary(BINARY_BLOB),
    "tree: the blob must actually look binary or the skip below proves nothing"
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
