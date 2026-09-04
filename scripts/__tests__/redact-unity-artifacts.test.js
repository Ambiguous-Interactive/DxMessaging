"use strict";
// cspell:ignore Brien bfnrt nner earer
const assert = require("node:assert/strict");
const { spawnSync } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { test } = require("node:test");
const VECTORS = require("./fixtures/unity-redaction-vectors.json");
const {
  CREDENTIAL_PATTERNS,
  IDENTIFIER_PATTERNS,
  findCredentials,
  findIdentifiers,
  findSensitiveData,
  decodeText,
  encodeText,
  redactCredentials,
  redactSensitiveData
} = require("../unity/credential-patterns.js");
const {
  formatSummary,
  parseArgs,
  redactDirectory,
  runCli,
  safeDisplayPath,
  usage
} = require("../unity/redact-unity-artifacts.js");
const FAKE_SERIAL = "SC-FAKE-FAKE-FAKE-FAKE-FAKE";
const FAKE_LICENSE_ID = "FAKE-LICENSE-0000-0000";
const FAKE_GITHUB_TOKEN = `ghp_${"FAKEfake0123456789".repeat(2)}`;
const FAKE_AWS_KEY = "AKIA0000FAKE0000FAKE";
const FAKE_BEARER = "FAKEbearerFAKEbearerFAKEbearer";
const FAKE_PASSWORD = "fake-password-value-0000";
const FAKE_KEY_BODY = "FAKEkeybodyFAKEkeybodyFAKEkeybody";
const FAKE_PEM = `-----BEGIN RSA PRIVATE KEY-----\n${FAKE_KEY_BODY}\n-----END RSA PRIVATE KEY-----`;
const FAKE_PRIVATE_IP = "192.168.42.17";
const FAKE_MAC = "02:00:5E:10:00:00";
const FAKE_MACHINE_ID = "FAKEmachineID000000000000=";
const FAKE_HOST = "fake-runner-host";
const FAKE_ACCOUNT = "Fake Runner Account";
const CREDENTIAL_PATTERNS_PATH = path.resolve(__dirname, "../unity/credential-patterns.js");
function invokeCli(root, written = []) {
  return runCli(["node", "cli", root], (text) => written.push(text));
}
const LEAK_CASES = Object.freeze([
  ["pem-private-key", `key follows\n${FAKE_PEM}\ndone\n`, FAKE_KEY_BODY],
  ["unity-license-id", `<License id="${FAKE_LICENSE_ID}" version="1.0">\n`, FAKE_LICENSE_ID],
  ["unity-serial", `Activated with serial ${FAKE_SERIAL} today\n`, FAKE_SERIAL],
  ["github-token", `remote token ${FAKE_GITHUB_TOKEN} rejected\n`, FAKE_GITHUB_TOKEN],
  ["aws-access-key-id", `uploader used ${FAKE_AWS_KEY} for the bucket\n`, FAKE_AWS_KEY],
  ["http-bearer-token", `authorization: bearer ${FAKE_BEARER}\n`, FAKE_BEARER],
  ["unity-password-assignment", `UNITY_PASSWORD=${FAKE_PASSWORD}\n`, FAKE_PASSWORD],
  ["unity-email-assignment", "UNITY_EMAIL=o'connor@example.com\n", "o'connor@example.com"],
  ["password-assignment", "PASSWORD=hunter2\n", "hunter2"],
  ["credential-assignment", `TOKEN=${FAKE_PASSWORD}\n`, FAKE_PASSWORD]
]);
const IDENTIFIER_CASES = VECTORS.identifierCases;
function temporaryDirectory() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "redact-unity-artifacts-test-"));
}
function artifactFile(contents, fileName = "artifact.log") {
  const root = temporaryDirectory();
  const target = path.join(root, fileName);
  fs.writeFileSync(target, contents);
  return { root, target };
}
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
const FAKE_ASSIGNMENT_VALUE = "FAKEfake00000000";
const STRAY_NUL_LOG = Buffer.concat([
  Buffer.from("Refreshing native plugins\n", "latin1"),
  Buffer.alloc(1),
  Buffer.from(`\nActivated with serial ${FAKE_SERIAL}\n`, "latin1")
]);
const SPARSE_NUL_LOG = Buffer.concat(
  Array.from({ length: 8 }, () =>
    Buffer.concat([Buffer.from("a".repeat(512), "latin1"), Buffer.alloc(1)])
  )
);
const DLL_LIKE_BLOB = Buffer.concat([
  Buffer.from("MZ", "latin1"),
  Buffer.alloc(4000),
  Buffer.from("x".repeat(4000), "latin1")
]);
const BOMLESS_UTF16_LOG = Buffer.from("Unity build log line\n".repeat(8), "utf16le");
/** What Windows PowerShell writes by default: UTF-16LE behind a byte-order mark. */
const UTF16LE_BOM_LOG = Buffer.concat([
  Buffer.from([0xff, 0xfe]),
  Buffer.from(`Activated with serial ${FAKE_SERIAL}\n`, "utf16le")
]);
const INVALID_UTF8_BYTES = Buffer.from([0x80, 0xfe, 0x41, 0x0a]);
const UTF8_BOM_PDF = Buffer.concat([
  Buffer.from([0xef, 0xbb, 0xbf]),
  Buffer.from(`%PDF-1.7\n${FAKE_SERIAL}`)
]);
const UTF8_BOM_LOG = Buffer.concat([
  Buffer.from([0xef, 0xbb, 0xbf]),
  Buffer.from(`URL https://${FAKE_HOST}/path`, "utf8")
]);
const PREFIXED_PDF = Buffer.from(` \r\n\t%PDF-1.7\n${FAKE_SERIAL}`);
const DECODE_CASES = Object.freeze([
  ["a short log with one stray NUL", STRAY_NUL_LOG, "utf8"],
  ["a four-kilobyte log with eight stray NULs", SPARSE_NUL_LOG, "utf8"],
  ["a DLL-like blob", DLL_LIKE_BLOB, undefined],
  ["a short BOMless UTF-16 value", Buffer.from("abc", "utf16le"), undefined],
  ["UTF-16 with no byte-order mark", BOMLESS_UTF16_LOG, undefined],
  ["a UTF-16LE log behind a byte-order mark", UTF16LE_BOM_LOG, "utf16le"],
  ["a UTF-8 BOM-prefixed PDF", UTF8_BOM_PDF, undefined],
  ["a UTF-8 BOM-prefixed log", UTF8_BOM_LOG, "utf8bom"],
  ["a text-prefixed PDF", PREFIXED_PDF, undefined],
  ["a plain ASCII log", Buffer.from(CLEAN_LOG, "latin1"), "utf8"],
  ["a Unicode UTF-8 log", Buffer.from("https://bâtisseur/path", "utf8"), "utf8"]
]);
const BARE_KEYWORDS = VECTORS.bareKeywords;
for (const [id, text, secret] of LEAK_CASES) {
  test(`${id} is found and its value is destroyed`, () => {
    assert.deepEqual(
      findCredentials(text).map((entry) => entry.id),
      [id],
      `${id}: findCredentials must report exactly this kind and nothing else`
    );
    const { redacted, counts } = redactCredentials(text);
    assert.ok(!redacted.includes(secret), `${id}: the sensitive substring must be gone`);
    assert.ok(redacted.includes(`[redacted:${id}]`), `${id}: the placeholder must name the kind`);
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
for (const [id, text, identifier] of IDENTIFIER_CASES) {
  test(`${id} is found and its private value is destroyed`, () => {
    assert.deepEqual(
      findIdentifiers(text).map((entry) => entry.id),
      [id],
      `${id}: wrong kind`
    );
    const { redacted, counts } = redactSensitiveData(text);
    assert.ok(!redacted.includes(identifier), `${id}: the private substring must be gone`);
    assert.ok(redacted.includes(`[redacted:${id}]`), `${id}: the placeholder must name the kind`);
    assert.deepEqual([...counts], [[id, 1]], `${id}: wrong count`);
  });
}
test("the identifier table exercises every declared identifier pattern", () => {
  const caseIds = IDENTIFIER_CASES.map(([id]) => id).sort();
  assert.deepEqual(caseIds, [...new Set(IDENTIFIER_PATTERNS.map((entry) => entry.id))].sort());
  const variants = IDENTIFIER_PATTERNS.map((entry) => entry.id);
  assert.equal(variants.filter((id) => id === "account-home-path").length, 8);
  assert.equal(variants.filter((id) => id === "file-uri-hostname").length, 5);
});
test("quoted and path-bearing variants contribute to one identifier count", () => {
  const text = 'file://runner/share "file://other"\n/home/alice/work "/Users/bob"';
  const { counts } = redactSensitiveData(text);
  assert.equal(counts.get("file-uri-hostname"), 2);
  assert.equal(counts.get("account-home-path"), 2);
});
test("redacting already-redacted identifiers is a no-op", () => {
  for (const [id, text] of IDENTIFIER_CASES) {
    const once = redactSensitiveData(text);
    const twice = redactSensitiveData(once.redacted);
    assert.equal(twice.redacted, once.redacted, `${id}: a second pass must not change the text`);
    assert.equal(twice.counts.size, 0, `${id}: a second pass must report nothing removed`);
    assert.deepEqual(findIdentifiers(once.redacted), [], `${id}: result must be sealable`);
  }
});
test("identifier placeholders preserve JSON strings and XML attributes", () => {
  const json = `{"path":"C:\\\\Users\\\\${FAKE_ACCOUNT}\\\\project"}`;
  const redactedJson = redactSensitiveData(json).redacted;
  assert.doesNotThrow(() => JSON.parse(redactedJson), "a replacement inside JSON must stay JSON");
  assert.equal(JSON.parse(redactedJson).path, "C:\\Users\\[redacted:account-home-path]\\project");
  const xml = `<test-case host="${FAKE_PRIVATE_IP}" />`;
  const redactedXml = redactSensitiveData(xml).redacted;
  assert.equal(redactedXml, '<test-case host="[redacted:ipv4-address]" />');
  assert.doesNotMatch(redactedXml, /host="[^"\r\n]*[<>][^"\r\n]*"/);
});
for (const [label, text] of VECTORS.safeEvidence) {
  test(`${label} remains available as non-private evidence`, () => {
    assert.deepEqual(findIdentifiers(text), []);
    assert.equal(redactSensitiveData(text).redacted, text);
  });
}
for (const [text, expected] of VECTORS.accountBoundaries)
  test(`account boundary is preserved: ${text}`, () =>
    assert.equal(redactSensitiveData(text).redacted, expected));
for (const [text, expected] of VECTORS.identifierDelimiters) {
  test(`identifier delimiter is preserved: ${text}`, () => {
    const once = redactSensitiveData(text).redacted;
    assert.equal(once, expected);
    assert.deepEqual(findIdentifiers(once), []);
    assert.equal(redactSensitiveData(once).redacted, once);
  });
}
test("escaped quotes do not end quoted sensitive values early", () => {
  const credential = '{"PASSWORD":"abcdefghijkl\\\"PRIVATE_SUFFIX"}\n';
  const identity = '{"machineName":"runner\\\"private"}\n';
  const scrubbedCredential = redactSensitiveData(credential).redacted;
  const scrubbedIdentity = redactSensitiveData(identity).redacted;
  assert.equal(JSON.parse(scrubbedCredential).PASSWORD, "[redacted:password-assignment]");
  assert.equal(JSON.parse(scrubbedIdentity).machineName, "[redacted:named-account-or-host]");
  assert.doesNotMatch(`${scrubbedCredential}${scrubbedIdentity}`, /PRIVATE_SUFFIX|private/);
});
for (const [label, value] of VECTORS.addresses)
  test(`${label} is removed`, () =>
    assert.ok(!redactSensitiveData(value).redacted.includes(value)));
for (const [label, text] of VECTORS.pathValues) {
  test(`${label} removes the account and is idempotent`, () => {
    const once = redactSensitiveData(text).redacted;
    assert.match(once, /\[redacted:account-home-path\]/);
    assert.equal(redactSensitiveData(once).redacted, once);
  });
}
for (const [input, expected] of VECTORS.rootPaths)
  test(`root home keeps its delimiter: ${input}`, () =>
    assert.equal(redactSensitiveData(input).redacted, expected));
for (const [label, value] of VECTORS.macs)
  test(`${label} is removed`, () =>
    assert.equal(redactSensitiveData(value).redacted, "[redacted:mac-address]"));
for (const [id, text, expected] of [
  [
    "http-bearer-token",
    `Authorization: Bearer ${FAKE_BEARER}\n`,
    "Authorization: Bearer [redacted:http-bearer-token]\n"
  ],
  [
    "unity-password-assignment",
    `UNITY_PASSWORD=${FAKE_PASSWORD}\n`,
    "UNITY_PASSWORD=[redacted:unity-password-assignment]\n"
  ],
  [
    "unity-license-id",
    `<License id="${FAKE_LICENSE_ID}" version="1.0">\n`,
    '<License id="[redacted:unity-license-id]" version="1.0">\n'
  ]
]) {
  test(`${id} keeps the label that says which credential was removed`, () => {
    assert.equal(redactCredentials(text).redacted, expected, id);
  });
}
test("a PEM key is redacted as one block, not just its header", () => {
  const { redacted } = redactCredentials(`prelude\n${FAKE_PEM}\nepilogue\n`);
  assert.equal(redacted, "prelude\n[redacted:pem-private-key]\nepilogue\n");
  assert.ok(!redacted.includes("PRIVATE KEY"), "PEM: no part of the armour may survive");
});
test("redacting already-redacted text is a no-op", () => {
  for (const [id, text] of LEAK_CASES) {
    const once = redactCredentials(text);
    const twice = redactCredentials(once.redacted);
    assert.equal(twice.redacted, once.redacted, `${id}: a second pass must not change the text`);
    assert.equal(twice.counts.size, 0, `${id}: a second pass must report nothing removed`);
    assert.deepEqual(
      findCredentials(once.redacted).map((entry) => entry.id),
      [],
      id
    );
  }
});
for (const [text, expected] of VECTORS.credentials)
  test(`credential value is removed: ${text.trim()}`, () => {
    assert.equal(redactCredentials(text).redacted, expected);
    assert.deepEqual(findCredentials(expected), []);
  });
for (const [text, expected] of VECTORS.licenses)
  test(`Unity license variation is removed: ${text}`, () =>
    assert.equal(redactCredentials(text).redacted, expected));
for (const text of VECTORS.truncatedLicenses) {
  test(`a truncated Unity license value is removed: ${text}`, () => {
    const actual = redactCredentials(text).redacted;
    assert.doesNotMatch(actual, /abcdefghijklmnop/);
    if (text.includes("next")) assert.match(actual, /next <tag>/);
  });
}
for (const [label, text] of [
  ...VECTORS.safeCredentials,
  ["a Unity licensing log line", CLEAN_LOG]
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
  assert.equal(result.binaryCount, 1, "tree: the unreviewed .bin must be reported as opaque");
  assert.deepEqual(
    fs.readFileSync(path.join(root, "logs", "GameAssembly.bin")),
    BINARY_BLOB,
    "tree: a binary file is skipped, so its serial-shaped bytes survive unchanged"
  );
  const scrubbed = fs.readFileSync(path.join(root, "logs", "unity.log"), "utf8");
  assert.ok(!scrubbed.includes(FAKE_SERIAL), "tree: the rewritten log must not keep the serial");
  assert.equal(
    scrubbed,
    "serial [redacted:unity-serial] accepted\nreactivated with [redacted:unity-serial]\n",
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
    /Artifact root is not a directory\./,
    "a file target must fail rather than be walked"
  );
  assert.throws(
    () => redactDirectory(path.join(root, "absent")),
    /Artifact root is not a directory\./,
    "a missing target must fail rather than report a clean tree"
  );
});
test("redactDirectory reports a file it cannot read instead of ignoring it", (t) => {
  if (!process.getuid || process.getuid() === 0) {
    t.skip("root can read any file, so an unreadable file cannot be staged");
    return;
  }
  const { root, target: locked } = artifactFile(`serial ${FAKE_SERIAL}\n`, "locked.log");
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
  assert.equal(invokeCli(root, written), 0, "cli: a tree exits 0");
  assert.match(
    written.join(""),
    /^Redacted 2 file\(s\) under .*: http-bearer-token x1, unity-serial x3\./,
    "cli: the summary reports kinds and counts without echoing any value"
  );
  assert.ok(!written.join("").includes(FAKE_SERIAL), "cli: output must never echo a credential");
});
test("the CLI never reports success when a file could not be scrubbed", (t) => {
  if (process.getuid && process.getuid() === 0) {
    t.skip("root can open any file, so neither refusal can be staged");
    return;
  }
  const { root } = artifactFile("nothing here\n", "clean.log");
  fs.writeFileSync(path.join(root, "GameAssembly.bin"), BINARY_BLOB);
  const resistant = path.join(root, "locked.log");
  fs.writeFileSync(resistant, `serial ${FAKE_SERIAL}\n`);
  fs.chmodSync(resistant, 0o000);
  t.after(() => fs.chmodSync(resistant, 0o644));
  const written = [];
  if (process.platform === "win32") {
    assert.throws(
      () => invokeCli(root, written),
      /locked\.log contains sensitive data but could not be rewritten/,
      "cli: a file that cannot be rewritten must stop the run"
    );
    return;
  }
  assert.equal(
    invokeCli(root, written),
    2,
    "cli: a file that was not examined must not exit 0, or the gated upload publishes it"
  );
  const output = written.join("");
  assert.match(output, /Refusing to report success: 1 file\(s\) could not be safely prepared/);
  assert.match(output, /locked\.log/, "cli: the refusal must name the file to scrub");
  assert.ok(!output.includes(FAKE_SERIAL), "cli: the refusal must not echo the credential");
});
test("a readable file that cannot be rewritten stops the run on every platform", (t) => {
  if (process.getuid && process.getuid() === 0) {
    t.skip("root can write any file, so the unwritable case cannot be staged");
    return;
  }
  const { root, target: unwritable } = artifactFile(`serial ${FAKE_SERIAL}\n`, "audit.log");
  fs.chmodSync(unwritable, 0o444);
  t.after(() => fs.chmodSync(unwritable, 0o644));
  assert.throws(
    () => invokeCli(root),
    /audit\.log contains sensitive data but could not be rewritten/,
    "a credential that cannot be removed must fail the step, never be reported as clean"
  );
  assert.match(
    fs.readFileSync(unwritable, "utf8"),
    new RegExp(FAKE_SERIAL),
    "the file is left untouched; the run fails rather than half-scrubbing it"
  );
});
test("the CLI still exits 0 when the only unscanned files are binary", () => {
  const { root } = artifactFile("nothing here\n", "clean.log");
  fs.writeFileSync(path.join(root, "GameAssembly.bin"), BINARY_BLOB);
  const written = [];
  assert.equal(
    invokeCli(root, written),
    0,
    "cli: a binary file was examined and judged not text, so it is not an unexamined file"
  );
  assert.match(written.join(""), /1 opaque or binary file\(s\) were not scanned\./);
});
for (const [fileName, contents] of [
  ["leak.pem", FAKE_PEM],
  ["leak.env", `UNITY_PASSWORD=${FAKE_PASSWORD}\n`]
]) {
  test(`the CLI fails closed on sensitive text in unreviewed ${path.extname(fileName)}`, () => {
    const { root, target } = artifactFile(contents, fileName);
    const written = [];
    assert.equal(invokeCli(root, written), 2);
    assert.equal(
      fs.readFileSync(target, "utf8"),
      contents,
      "the unreviewed format stays unchanged"
    );
    assert.match(written.join(""), /uses an unreviewed extension and contains sensitive data/);
  });
}
for (const contents of VECTORS.blockedSensitive) {
  test(`the CLI blocks sensitive data it cannot map safely: ${contents}`, () => {
    const { root, target } = artifactFile(contents, "encoded.json");
    const written = [];
    assert.equal(invokeCli(root, written), 2);
    assert.match(written.join(""), /contains encoded sensitive data/);
    assert.equal(fs.readFileSync(target, "utf8"), contents);
  });
}
for (const [contents, privatePart] of VECTORS.entityIdentifiers) {
  test(`the CLI safely rewrites an entity-bearing identifier: ${contents}`, () => {
    const { root, target } = artifactFile(contents, "encoded.xml");
    assert.equal(invokeCli(root), 0);
    const actual = fs.readFileSync(target, "utf8");
    assert.ok(!actual.includes(privatePart));
    assert.deepEqual(findIdentifiers(actual), []);
  });
}
test("entity-heavy account input cannot exhaust the scrubber", () => {
  const script = `require(${JSON.stringify(CREDENTIAL_PATTERNS_PATH)}).findIdentifiers('/home/'+'&amp;'.repeat(256)+'!')`;
  const result = spawnSync(process.execPath, ["-e", script], { timeout: 1000 });
  assert.equal(result.error, undefined);
  assert.equal(result.status, 0);
});
for (const unique of [false, true]) {
  test(`large escaped logs stay within a bounded heap (unique records: ${unique})`, () => {
    const row = String.raw`{"message":"C:\\runner said \"hello\" &amp; done"}` + "\n";
    const prefix = unique ? Array.from({ length: 4097 }, (_, i) => `row ${i}\n`).join("") : "";
    const suffix = unique ? "Bearer\\n\n\\u0061" + "a".repeat(24) + "\n" : "";
    const script = `const f=require(${JSON.stringify(CREDENTIAL_PATTERNS_PATH)}).findSensitiveData,r=${JSON.stringify(row)},n=16*1024*1024,t=${JSON.stringify(prefix)}+r.repeat(Math.ceil(n/r.length)).slice(0,n)+${JSON.stringify(suffix)},v=f(t);if(${unique}?!v.some(x=>x.id==='http-bearer-token'):v.length)process.exit(2)`;
    const result = spawnSync(process.execPath, ["--max-old-space-size=192", "-e", script], {
      timeout: 30000
    });
    assert.equal(result.status, 0, result.error?.message ?? result.stderr.toString());
  });
}
for (const [label, prefix, separator, value, kind] of [
  ["password", "\\u002dpassword", "\n", FAKE_PASSWORD, "unity-password-assignment"],
  ["account", "&#45;username", "\r\n", FAKE_ACCOUNT, "unity-email-assignment"],
  ["endpoint", "\\u002dcacheServerEndpoint", "\n", FAKE_HOST, "unity-cache-server-endpoint"],
  ["assignment", "\\u0050ASSWORD=", "\r\n \t\r\n", FAKE_PASSWORD, "password-assignment"],
  ["split assignment", "\\u0050ASSWORD", "\n=\n", FAKE_PASSWORD, "password-assignment"],
  ["encoded value", "Bearer", "\n", "\\u0061" + "a".repeat(24), "http-bearer-token"],
  ["control escape", "Bearer\\n", "\n", "\\u0061" + "a".repeat(24), "http-bearer-token"],
  ["bearer", "\\u0042earer", "\r \t\r", FAKE_BEARER, "http-bearer-token"]
]) {
  test(`large encoded ${label} retains its following value`, (t) => {
    // The same encoded prefix first appears with a masked value; caching by prefix alone
    // must not suppress the later unmasked occurrence.
    const contents =
      `${"ordinary log entry\n".repeat(240000)}${prefix}${separator}[redacted:example]\n` +
      `${prefix}${separator}${value}\n`;
    assert.ok(
      findSensitiveData(contents).some((entry) => entry.id === kind),
      label
    );
    const { root, target } = artifactFile(contents);
    t.after(() => fs.rmSync(root, { recursive: true, force: true }));
    assert.equal(invokeCli(root), 2, label);
    assert.equal(fs.readFileSync(target, "utf8"), contents, "unsafe bytes stay unchanged");
  });
}
test("large encoded assignments preserve arbitrarily long unencoded whitespace", () => {
  const content = `\\u0050ASSWORD=\n${" ".repeat(4 * 1024 * 1024)}\n${FAKE_PASSWORD}`;
  assert.ok(findSensitiveData(content).some((entry) => entry.id === "password-assignment"));
});
for (const contents of VECTORS.mappableSensitive) {
  test(`the CLI maps direct sensitive data despite an unrelated escape: ${contents}`, () => {
    const { root, target } = artifactFile(contents, "mappable.xml");
    assert.equal(invokeCli(root), 0);
    assert.match(fs.readFileSync(target, "utf8"), /\[redacted:/);
  });
}
for (const fileName of [
  `runner-${FAKE_PRIVATE_IP}.log`,
  "runner-192&#46;168&#46;42&#46;17.log",
  "runner-﻿-private.log"
]) {
  test(`the CLI refuses a sensitive file name without printing it: ${fileName}`, () => {
    const { root } = artifactFile("clean\n", fileName);
    const written = [];
    assert.equal(invokeCli(root, written), 2);
    assert.ok(!written.join("").includes(fileName));
    assert.match(written.join(""), /\[redacted:sensitive-file-name\]/);
  });
}
test("displayed roots never echo an account home or network address", () => {
  const privateRoot = `C:\\Users\\${FAKE_ACCOUNT}\\runner-${FAKE_PRIVATE_IP}`;
  const written = [];
  assert.equal(invokeCli(privateRoot, written), 0);
  const nativeError = `EACCES: permission denied, open '${privateRoot}\\unity.log'`;
  const summary = formatSummary(privateRoot, {
    changed: [],
    skipped: [{ path: "unity.log", reason: nativeError }],
    totals: new Map()
  });
  for (const output of [
    safeDisplayPath(privateRoot),
    written.join(""),
    safeDisplayPath(nativeError),
    summary
  ]) {
    assert.doesNotMatch(output, new RegExp(FAKE_ACCOUNT));
    assert.doesNotMatch(output, new RegExp(FAKE_PRIVATE_IP.replaceAll(".", "\\.")));
  }
  for (const encoded of [
    "runner-192&#46;168&#46;42&#46;17",
    "runner-192\\u002e168\\u002e42\\u002e17"
  ]) {
    assert.equal(safeDisplayPath(encoded), "[redacted:encoded-sensitive-data]");
  }
  assert.doesNotMatch(
    safeDisplayPath("/tmp/-cacheServerEndpoint private&#45;runner-host/bundle"),
    /private|runner-host/
  );
  assert.equal(safeDisplayPath("clean\n::error::forged"), "[redacted:unsafe-path]");
});
test("formatSummary renders a clean tree, a redacted tree, and a skipped file", () => {
  assert.equal(
    formatSummary("artifacts", { changed: [], skipped: [], totals: new Map() }),
    "No files were rewritten under artifacts.\n",
    "summary: a result without scan totals makes no clean claim"
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
    "No files were rewritten under artifacts.\n" +
      "  WARNING: locked.log could not be safely prepared because it could not be read: EACCES.\n",
    "summary: a file that was not prepared is a warning, not a false clean result"
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
test("a log carrying a stray NUL is normalized and redacted", () => {
  const { root, target } = artifactFile(STRAY_NUL_LOG, "unity.log");
  const result = redactDirectory(root);
  assert.deepEqual(
    result.changed.map((file) => file.path),
    ["unity.log"],
    "stray NUL: one NUL byte must not take a whole Unity log out of the scan"
  );
  const rewritten = fs.readFileSync(target);
  assert.ok(!rewritten.includes(FAKE_SERIAL), "stray NUL: the serial must be gone");
  assert.ok(
    rewritten.includes("[redacted:unity-serial]"),
    "stray NUL: the placeholder must name the kind that was removed"
  );
  assert.ok(!rewritten.includes(0), "stray NUL: normalization must remove the separator byte");
  assert.equal(result.totals.get("stray-nul-byte"), 1);
});
test("redaction removes a NUL inserted inside a sensitive token before matching", () => {
  const splitSerial = FAKE_SERIAL.replace("FAKE-FAKE", "FAKE\0-\0FAKE");
  const { root, target } = artifactFile(splitSerial, "unity.log");
  const result = redactDirectory(root);
  const rewritten = fs.readFileSync(target, "utf8");
  assert.equal(rewritten, "[redacted:unity-serial]");
  assert.equal(result.totals.get("stray-nul-byte"), 2);
  assert.equal(result.totals.get("unity-serial"), 1);
});
test("a UTF-16LE log behind a byte-order mark is redacted and re-encoded as UTF-16LE", () => {
  const { root, target } = artifactFile(UTF16LE_BOM_LOG, "configure.log");
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
    text.includes("[redacted:unity-serial]"),
    "utf16le: the rewrite must be re-encoded in the encoding it was read from"
  );
});
test("a rewritten UTF-8 log preserves its byte-order mark", () => {
  const { root, target } = artifactFile(UTF8_BOM_LOG);
  redactDirectory(root);
  const rewritten = fs.readFileSync(target);
  assert.deepEqual(rewritten.subarray(0, 3), Buffer.from([0xef, 0xbb, 0xbf]));
  assert.ok(!rewritten.includes(Buffer.from(FAKE_HOST)));
});
for (const [label, bytes] of [
  ["UTF-8 web host", Buffer.from("URL https://bâtisseur/path", "utf8")],
  ["UTF-8 UNC host", Buffer.from("path=\\\\构建机\\share\\file", "utf8")],
  [
    "UTF-16LE normalized web host",
    Buffer.concat([
      Buffer.from([0xff, 0xfe]),
      Buffer.from("URL https://runner。local/path", "utf16le")
    ])
  ]
]) {
  test(`redactDirectory removes a ${label}`, () => {
    const { root, target } = artifactFile(bytes);
    const first = redactDirectory(root);
    const text = decodeText(fs.readFileSync(target)).text;
    assert.deepEqual(
      first.changed.map((file) => file.path),
      ["artifact.log"]
    );
    assert.deepEqual(findSensitiveData(text), []);
    assert.match(text, /\[redacted:(?:web-hostname|unc-hostname)\]/);
    assert.deepEqual(redactDirectory(root).changed, []);
  });
}
for (const [label, bom, encoded] of [
  ["UTF-16LE", [0xff, 0xfe], Buffer.from(`\u0001${FAKE_SERIAL}\u0002`, "utf16le")],
  ["UTF-16BE", [0xfe, 0xff], Buffer.from(`\u0001${FAKE_SERIAL}\u0002`, "utf16le").swap16()]
]) {
  test(`${label} control bytes make a reviewed extension opaque`, () => {
    const original = Buffer.concat([Buffer.from(bom), encoded]);
    const { root, target } = artifactFile(original, "disguised.log");
    const result = redactDirectory(root);
    assert.deepEqual(fs.readFileSync(target), original);
    assert.equal(result.skipped.length, 1);
    assert.deepEqual(result.changed, []);
  });
}
test("bytes that are not valid UTF-8 round-trip byte for byte", () => {
  const decoded = decodeText(INVALID_UTF8_BYTES);
  assert.equal(decoded.encoding, "latin1", "invalid UTF-8: the decode maps bytes one to one");
  assert.deepEqual(
    encodeText(decoded.text, decoded.encoding),
    INVALID_UTF8_BYTES,
    "invalid UTF-8: a UTF-8 decode would substitute U+FFFD and corrupt the file on write"
  );
  const { root, target } = artifactFile(INVALID_UTF8_BYTES, "player.log");
  const result = redactDirectory(root);
  assert.deepEqual(result.changed, [], "invalid UTF-8: a file with no credential is not rewritten");
  assert.deepEqual(
    fs.readFileSync(target),
    INVALID_UTF8_BYTES,
    "invalid UTF-8: every byte of an untouched file must survive the walk"
  );
});
test("malformed UTF-8 containing a Unicode sensitive host is still scrubbed", () => {
  const original = Buffer.concat([
    Buffer.from([0x80]),
    Buffer.from(" URL https://bâtisseur/path", "utf8")
  ]);
  const { root, target } = artifactFile(original);
  const result = redactDirectory(root);
  const rewritten = fs.readFileSync(target);
  assert.deepEqual(
    result.changed.map((file) => file.path),
    ["artifact.log"]
  );
  assert.ok(!rewritten.includes(Buffer.from("bâtisseur", "utf8")));
  assert.deepEqual(redactDirectory(root).changed, []);
});
for (const keyword of BARE_KEYWORDS) {
  test(`${keyword}= is a credential assignment even with no vendor prefix`, () => {
    const text = `${keyword}=${FAKE_ASSIGNMENT_VALUE}\n`;
    const id = keyword === "PASSWORD" ? "password-assignment" : "credential-assignment";
    assert.deepEqual(
      findCredentials(text).map((entry) => entry.id),
      [id],
      `${keyword}: a bare keyword assignment must be reported as a leak`
    );
    assert.equal(
      redactCredentials(text).redacted,
      `${keyword}=[redacted:${id}]\n`,
      `${keyword}: only the value may be destroyed, the keyword must survive`
    );
  });
}
test("a short assignment value is rejected by the length rule, not by an unmatched keyword", () => {
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
  const root = writeArtifactTree();
  const result = redactDirectory(root);
  assert.equal(result.binaryCount, 1, "binary: the skipped blob must be counted, not forgotten");
  assert.match(
    formatSummary("artifacts", result),
    /^ {2}1 opaque or binary file\(s\) were not scanned\.$/m,
    "binary: the summary must say how many files were never scanned"
  );
  assert.match(
    formatSummary("artifacts", { changed: [], skipped: [], totals: new Map(), binaryCount: 4 }),
    /^No files were rewritten under artifacts\.\n {2}4 opaque or binary file\(s\) were not scanned\.\n$/,
    "binary: a tree with no findings still reports what it could not read"
  );
});
for (const [fileName, suffix] of [
  ["evidence.png", FAKE_PRIVATE_IP],
  ["disguised.log", FAKE_SERIAL]
]) {
  test(`redactDirectory leaves PNG bytes byte-identical as ${fileName}`, () => {
    const source = path.resolve(__dirname, "../../docs/images/dxmessaging-store-icon-320.png");
    const original = Buffer.concat([fs.readFileSync(source), Buffer.from(suffix)]);
    const { root, target } = artifactFile(original, fileName);
    const result = redactDirectory(root);
    assert.deepEqual(
      fs.readFileSync(target),
      original,
      "PNG bytes must never be rewritten as text"
    );
    assert.equal(result.binaryCount, 1);
    assert.deepEqual(result.changed, []);
  });
}
for (const [fileName, original] of [
  ["bom-pdf.log", UTF8_BOM_PDF],
  ["prefixed-pdf.log", PREFIXED_PDF]
]) {
  test(`redactDirectory preserves disguised binary bytes: ${fileName}`, () => {
    const { root, target } = artifactFile(original, fileName);
    const result = redactDirectory(root);
    assert.deepEqual(fs.readFileSync(target), original);
    assert.equal(result.skipped.length, 1);
  });
}
test("a PDF marker in an ordinary log cannot hide a credential", () => {
  const { root, target } = artifactFile(
    `note: parser saw %PDF-1.7 marker\nUNITY_EMAIL=a@b.co\n`,
    "marker.log"
  );
  const result = redactDirectory(root);
  assert.equal(result.binaryCount, 0);
  assert.match(fs.readFileSync(target, "utf8"), /\[redacted:unity-email-assignment\]/);
});
for (const [label, original] of [
  ["NUL-split PDF", Buffer.from(`%P\0DF-1.7\nUNITY_EMAIL=a@b.co\n`, "latin1")],
  [
    "NUL-split UTF-8 BOM PDF",
    Buffer.concat([
      Buffer.from([0xef, 0, 0xbb, 0xbf]),
      Buffer.from(`%PDF-1.7\nUNITY_EMAIL=a@b.co\n`)
    ])
  ]
])
  test(`${label} cannot be normalized and corrupted`, () => {
    const { root, target } = artifactFile(original, "opaque.log");
    const result = redactDirectory(root);
    assert.deepEqual(fs.readFileSync(target), original);
    assert.equal(result.skipped.length, 1);
  });
test("a hard-linked artifact cannot rewrite bytes outside the tree", () => {
  const holder = temporaryDirectory();
  const root = path.join(holder, "artifact");
  const outside = path.join(holder, "outside.log");
  fs.mkdirSync(root);
  fs.writeFileSync(outside, `UNITY_PASSWORD=${FAKE_PASSWORD}\n`);
  fs.linkSync(outside, path.join(root, "linked.log"));
  assert.equal(invokeCli(root), 2);
  assert.equal(fs.readFileSync(outside, "utf8"), `UNITY_PASSWORD=${FAKE_PASSWORD}\n`);
});
test("the CLI rejects a cross-window authority without echoing a hostile root", () => {
  const root = path.join(temporaryDirectory(), "artifact-\u202e-private");
  const contents = `${"x".repeat(4 * 1024 * 1024 - 64 * 1024 - 101)}\npath=\\\\&#${"0".repeat(66000)}114;${"runner-private".slice(1)}&#92;share\n`;
  const target = path.join(root, "encoded.log");
  fs.mkdirSync(root);
  fs.writeFileSync(target, contents);
  const written = [];
  assert.equal(invokeCli(root, written), 2);
  assert.doesNotMatch(written.join(""), /\u202e-private/);
  assert.match(written.join(""), /\[redacted:unsafe-path\]/);
  assert.equal(fs.readFileSync(target, "utf8"), contents);
});
test("formatSummary safely renders a hostile filename on every platform", () => {
  const summary = formatSummary("artifacts", {
    changed: [],
    skipped: [{ path: "bad\n::error::forged.env", reason: "could not be read" }]
  });
  assert.doesNotMatch(summary, /\n::error::forged/);
  assert.match(summary, /\[redacted:unsafe-path\]/);
});
test("redactDirectory refuses symbolic links instead of silently skipping them", () => {
  const holder = temporaryDirectory();
  const root = path.join(holder, "artifact");
  const target = path.join(holder, "private.log");
  fs.mkdirSync(root);
  fs.writeFileSync(target, `serial ${FAKE_SERIAL}\n`);
  fs.symlinkSync(target, path.join(root, "linked.log"));
  assert.throws(
    () => redactDirectory(root),
    /Artifact tree contains a symbolic link or non-regular entry/
  );
  assert.match(fs.readFileSync(target, "utf8"), new RegExp(FAKE_SERIAL));
  fs.symlinkSync(root, path.join(holder, "root-link"), "dir");
  assert.throws(() => redactDirectory(path.join(holder, "root-link")), /root is not a directory/);
  fs.symlinkSync(holder, path.join(holder, "parent-link"), "dir");
  assert.throws(
    () => redactDirectory(path.join(holder, "parent-link", "artifact")),
    /root is not a directory/
  );
  const dangling = path.join(holder, "dangling-root");
  fs.symlinkSync(path.join(holder, "absent"), dangling, "dir");
  assert.throws(() => runCli(["node", "cli", dangling]), /root is not a directory/);
});
for (const [label, bom, encoded] of [
  ["UTF-16LE", [0xff, 0xfe], Buffer.from("Machine Id: private-machine-id", "utf16le")],
  ["UTF-16BE", [0xfe, 0xff], Buffer.from("Machine Id: private-machine-id", "utf16le").swap16()]
]) {
  test(`redactDirectory leaves malformed ${label} byte-identical`, () => {
    const original = Buffer.concat([Buffer.from(bom), encoded, Buffer.from([0xff])]);
    const { root, target } = artifactFile(original, "opaque.log");
    const result = redactDirectory(root);
    assert.deepEqual(fs.readFileSync(target), original, "malformed UTF-16 must not be rewritten");
    assert.equal(result.changed.length, 0);
    assert.equal(result.binaryCount, 1, "the malformed file must be reported as opaque");
  });
}
