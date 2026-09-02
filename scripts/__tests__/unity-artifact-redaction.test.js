"use strict";

/**
 * Structural guard for the credential leak fixed in this change.
 *
 * Unity writes its license serial into `unity.log` and `configure.log` during activation. GitHub
 * masks registered secrets in rendered job logs but not in the bytes of an uploaded artifact, so
 * uploading a Unity output directory on a public repository publishes the serial to anyone who can
 * download the artifact.
 *
 * Removing the six known leaks is not enough: the next workflow that uploads a Unity directory
 * would reintroduce the whole failure mode. This asserts the invariant instead. Every upload of a
 * Unity-log-bearing path must be preceded, in the same job, by a redaction step that covers it.
 */

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const YAML = require("yaml");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOW_DIRECTORY = path.join(REPO_ROOT, ".github", "workflows");
const REDACTION_ACTION = "./.github/actions/redact-unity-artifacts";
const UPLOAD_ACTION = "actions/upload-artifact";

/**
 * Path prefixes that can hold Unity editor output. `.artifacts/unity` is where every runner writes
 * its results, and `dx-unity-editor-validation` is the editor-validation scratch directory that the
 * licensing steps write into before any test runs.
 */
const UNITY_OUTPUT_PREFIXES = [".artifacts/unity", "dx-unity-editor-validation"];

function readWorkflows() {
  return fs
    .readdirSync(WORKFLOW_DIRECTORY)
    .filter((name) => name.endsWith(".yml") || name.endsWith(".yaml"))
    .sort()
    .map((name) => ({
      name,
      document: YAML.parse(fs.readFileSync(path.join(WORKFLOW_DIRECTORY, name), "utf8"))
    }));
}

/** Normalize a workflow expression to plain text so a prefix comparison is meaningful. */
function normalizePath(value) {
  return String(value)
    .replace(/\$\{\{[^}]*\}\}/g, "")
    .split("\\")
    .join("/")
    .trim();
}

function toPathList(value) {
  if (value === undefined || value === null) {
    return [];
  }
  return String(value)
    .split("\n")
    .map((entry) => normalizePath(entry))
    .filter((entry) => entry.length > 0);
}

function isUnityOutput(candidate) {
  return UNITY_OUTPUT_PREFIXES.some((prefix) => candidate.includes(prefix));
}

/** A redaction step covers an upload when one of its paths is a prefix of the uploaded path. */
function covers(redactionPaths, uploadedPath) {
  return redactionPaths.some(
    (declared) => declared.length > 0 && uploadedPath.startsWith(declared)
  );
}

function jobSteps(job) {
  return Array.isArray(job?.steps) ? job.steps : [];
}

/** Every Unity-log-bearing upload in the repository, with the redaction state that precedes it. */
function unityUploads() {
  const uploads = [];
  for (const { name, document } of readWorkflows()) {
    for (const [jobId, job] of Object.entries(document?.jobs ?? {})) {
      const redactedSoFar = [];
      for (const step of jobSteps(job)) {
        const uses = typeof step?.uses === "string" ? step.uses : "";
        if (uses.startsWith(REDACTION_ACTION)) {
          redactedSoFar.push(...toPathList(step?.with?.paths));
          continue;
        }
        if (!uses.startsWith(UPLOAD_ACTION)) {
          continue;
        }
        for (const uploadedPath of toPathList(step?.with?.path)) {
          if (!isUnityOutput(uploadedPath)) {
            continue;
          }
          uploads.push({
            workflow: name,
            jobId,
            stepName: step.name ?? uses,
            uploadedPath,
            redactedSoFar: [...redactedSoFar]
          });
        }
      }
    }
  }
  return uploads;
}

test("every Unity artifact upload is preceded by credential redaction in the same job", () => {
  const uploads = unityUploads();
  assert.ok(
    uploads.length >= 9,
    `expected the known Unity uploads to be discovered, found ${uploads.length}`
  );
  const unprotected = uploads.filter(
    (upload) => !covers(upload.redactedSoFar, upload.uploadedPath)
  );
  assert.deepEqual(
    unprotected.map((upload) => `${upload.workflow} ${upload.jobId} "${upload.stepName}"`),
    [],
    "these steps upload Unity output that still contains the license serial; add a " +
      `"${REDACTION_ACTION}" step earlier in the same job whose paths cover the uploaded path`
  );
});

test("the redaction action exists and runs the shared redactor", () => {
  const actionPath = path.join(
    REPO_ROOT,
    ".github",
    "actions",
    "redact-unity-artifacts",
    "action.yml"
  );
  const action = YAML.parse(fs.readFileSync(actionPath, "utf8"));
  assert.equal(action.runs.using, "composite", "the redaction step must be a composite action");
  assert.ok(action.inputs.paths.required, "paths must be required so a call cannot scrub nothing");
  const body = jobSteps(action.runs)
    .map((step) => step.run ?? "")
    .join("\n");
  assert.match(
    body,
    /node scripts\/unity\/redact-unity-artifacts\.js/,
    "the action must call the tested redactor rather than reimplementing the patterns inline"
  );
  assert.match(
    body,
    /throw/,
    "the action must fail the job when redaction fails; a silent pass recreates the leak"
  );
});

test("the shipping evidence bundle is sealed only after redaction has run", () => {
  const workflowPath = path.join(WORKFLOW_DIRECTORY, "unity-tests.yml");
  const document = YAML.parse(fs.readFileSync(workflowPath, "utf8"));
  const steps = jobSteps(document.jobs["unity-tests"]);
  const sealIndex = steps.findIndex((step) => step.id === "seal_shipping");
  assert.ok(sealIndex > 0, "unity-tests must seal the shipping evidence bundle");
  const redactionIndex = steps.findIndex(
    (step) =>
      typeof step.uses === "string" &&
      step.uses.startsWith(REDACTION_ACTION) &&
      covers(toPathList(step.with?.paths), ".artifacts/unity/x-shipping")
  );
  assert.ok(
    redactionIndex >= 0 && redactionIndex < sealIndex,
    "sealing must follow redaction, or a bundle can be sealed around credential material"
  );
  const uploadIndex = steps.findIndex(
    (step) =>
      typeof step.uses === "string" &&
      step.uses.startsWith(UPLOAD_ACTION) &&
      toPathList(step.with?.path).some((value) => value.endsWith("-shipping"))
  );
  assert.ok(
    uploadIndex > sealIndex,
    "the shipping upload must follow sealing so the manifest ships with the evidence"
  );
});
