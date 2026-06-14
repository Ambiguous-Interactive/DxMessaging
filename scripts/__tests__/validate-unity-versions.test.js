"use strict";

const { test } = require("node:test");
const assert = require("node:assert/strict");

const {
  validateCanonicalSchema,
  extractVersionLiterals,
  checkConsumer,
  resolveWorkflowPolicy,
  parseVersionTriple
} = require("../validate-unity-versions.js");

const VALID = { all: ["2021.3.45f1", "2022.3.45f1", "6000.3.16f1"], release: "2022.3.45f1" };

test("parseVersionTriple parses the leading numeric triple", () => {
  assert.deepEqual(parseVersionTriple("6000.3.16f1"), [6000, 3, 16]);
  assert.deepEqual(parseVersionTriple("2022.1.0b3"), [2022, 1, 0]);
  assert.equal(parseVersionTriple("not-a-version"), null);
});

test("validateCanonicalSchema accepts the canonical shape", () => {
  assert.deepEqual(validateCanonicalSchema(VALID), []);
});

test("validateCanonicalSchema rejects bad roots and bad `all`", () => {
  assert.equal(validateCanonicalSchema(null).length, 1);
  assert.equal(validateCanonicalSchema([]).length, 1);
  assert.ok(validateCanonicalSchema({ all: [], release: "x" })[0].includes("non-empty array"));
  assert.ok(
    validateCanonicalSchema({ all: ["2021.3.45f1", 7], release: "2021.3.45f1" })[0].includes(
      "must be a string"
    )
  );
});

test("validateCanonicalSchema rejects malformed, duplicate, and unordered entries", () => {
  const malformed = validateCanonicalSchema({ all: ["2021.3.45"], release: "2021.3.45" });
  assert.ok(malformed.some((message) => message.includes("not a valid Unity version")));

  const duplicate = validateCanonicalSchema({
    all: ["2021.3.45f1", "2021.3.45f1"],
    release: "2021.3.45f1"
  });
  assert.ok(duplicate.some((message) => message.includes("duplicate")));

  const unordered = validateCanonicalSchema({
    all: ["2022.3.45f1", "2021.3.45f1"],
    release: "2021.3.45f1"
  });
  assert.ok(unordered.some((message) => message.includes("strictly ascending")));
});

test("validateCanonicalSchema requires release to be a member of all", () => {
  const missing = validateCanonicalSchema({ all: ["2021.3.45f1"] });
  assert.ok(missing.some((message) => message.includes("`release` must be a string")));

  const notMember = validateCanonicalSchema({ all: ["2021.3.45f1"], release: "6000.3.16f1" });
  assert.ok(notMember.some((message) => message.includes("must be a member of `all`")));
});

test("extractVersionLiterals finds literals with line numbers, ignoring comments", () => {
  const content = [
    "image: unity:2021.3.45f1",
    "# comment 6000.3.16f1",
    "version: 2022.3.45f1 # trailing 2021.3.45f1",
    "no version here"
  ].join("\n");

  assert.deepEqual(extractVersionLiterals(content, ".yml"), [
    { version: "2021.3.45f1", line: 1 },
    { version: "2022.3.45f1", line: 3 }
  ]);
});

test("checkConsumer enforces the no-literals policy", () => {
  const literals = [{ version: "2021.3.45f1", line: 4 }];
  const violations = checkConsumer({
    relativePath: ".github/workflows/foo.yml",
    policy: "no-literals",
    literals,
    all: VALID.all,
    release: VALID.release
  });
  assert.equal(violations.length, 1);
  assert.ok(violations[0].includes("foo.yml:4"));
  assert.deepEqual(
    checkConsumer({ relativePath: "x", policy: "no-literals", literals: [], ...VALID }),
    []
  );
});

test("checkConsumer enforces the mirror-release policy", () => {
  const base = { relativePath: ".github/workflows/release.yml", policy: "mirror-release", ...VALID };
  assert.equal(checkConsumer({ ...base, literals: [] }).length, 1);
  assert.deepEqual(checkConsumer({ ...base, literals: [{ version: "2022.3.45f1", line: 2 }] }), []);
  const drift = checkConsumer({ ...base, literals: [{ version: "6000.3.16f1", line: 2 }] });
  assert.equal(drift.length, 1);
  assert.ok(drift[0].includes("does not match canonical"));
});

test("checkConsumer enforces the mirror-all policy", () => {
  const base = { relativePath: "runner.ps1", policy: "mirror-all", ...VALID };
  const exact = VALID.all.map((version, index) => ({ version, line: index + 1 }));
  assert.deepEqual(checkConsumer({ ...base, literals: exact }), []);

  const missing = checkConsumer({ ...base, literals: exact.slice(0, 2) });
  assert.equal(missing.length, 1);
  assert.ok(missing[0].includes("'6000.3.16f1' is missing"));

  const unknown = checkConsumer({
    ...base,
    literals: [...exact, { version: "2019.4.0f1", line: 9 }]
  });
  assert.ok(unknown.some((message) => message.includes("not in canonical")));
});

test("checkConsumer throws on unknown policies", () => {
  assert.throws(
    () => checkConsumer({ relativePath: "x", policy: "bogus", literals: [], ...VALID }),
    /Unknown consumer policy/
  );
});

test("resolveWorkflowPolicy honors explicit table and workflow default", () => {
  assert.equal(resolveWorkflowPolicy(".github/workflows/release.yml"), "mirror-release");
  assert.equal(
    resolveWorkflowPolicy("scripts/unity/maintain-windows-runner.ps1"),
    "mirror-all"
  );
  assert.equal(resolveWorkflowPolicy(".github/workflows/brand-new.yml"), "no-literals");
  assert.equal(resolveWorkflowPolicy("docs/page.md"), null);
  assert.equal(resolveWorkflowPolicy(".github/workflows-disabled/old.yml"), null);
});
