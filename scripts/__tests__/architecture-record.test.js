const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Ajv = require("ajv");

const root = path.resolve(__dirname, "../..");
const schema = JSON.parse(
  fs.readFileSync(path.join(root, ".github/perf/architecture-record.v1.schema.json"), "utf8")
);
const record = JSON.parse(
  fs.readFileSync(path.join(root, ".github/perf/architecture-505-exact-sequential.v1.json"), "utf8")
);
const validate = new Ajv({ allErrors: true, strict: true }).compile(schema);
const copy = () => structuredClone(record);

test("exact sequential architecture declares every admission field", () => {
  assert.equal(validate(record), true, JSON.stringify(validate.errors));
});

test("architecture admission rejects missing ownership, topology, and lifecycle claims", () => {
  for (const [parent, field] of [
    ["ownership", "payloadLifetime"],
    ["topology", "threadAffinity"],
    ["ordering", "linearizationPoint"],
    ["capacity", "overflow"],
    ["lifecycle", "partialPublication"],
    ["decision", "stopRule"],
    ["evidence", "sourceSha256"],
    ["evidence", "testSourceSha256"]
  ]) {
    const candidate = copy();
    delete candidate[parent][field];
    assert.equal(validate(candidate), false, `${parent}.${field}`);
  }
});

test("exact sequential contract rejects amortized routes, changed order, and wait on full", () => {
  for (const change of [
    (candidate) => {
      candidate.ordering.routeAmortization = true;
    },
    (candidate) => {
      candidate.ordering.guarantee = "changed-cross-route";
    },
    (candidate) => {
      candidate.capacity.overflow = "blocking-lab-only";
    },
    (candidate) => {
      candidate.capacity.wait = "busy-spin-lab-only";
    },
    (candidate) => {
      candidate.dynamicDifferences = "mid-batch mutations are deferred";
    }
  ]) {
    const candidate = copy();
    change(candidate);
    assert.equal(validate(candidate), false);
  }
});

test("tested architecture must pin source and evidence hashes", () => {
  const candidate = copy();
  candidate.evidence.sourceSha256 = null;
  candidate.evidence.testSourceSha256 = null;
  candidate.evidence.evidenceSha256 = null;
  assert.equal(validate(candidate), false);
  candidate.evidence.sourceSha256 = "a".repeat(64);
  candidate.evidence.testSourceSha256 = "c".repeat(64);
  candidate.evidence.evidenceSha256 = "b".repeat(64);
  assert.equal(validate(candidate), true, JSON.stringify(validate.errors));
});
