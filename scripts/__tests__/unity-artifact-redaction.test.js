"use strict";

/**
 * Structural guard for the credential leak fixed in this change.
 *
 * Unity writes its license serial into `unity.log` and `configure.log` during activation. GitHub
 * masks registered secrets in rendered job logs but not in the bytes of an uploaded artifact, so
 * uploading a Unity output directory on a public repository publishes the serial to anyone who can
 * download the artifact.
 *
 * Removing the known leaks is not enough: the next workflow that uploads a Unity directory would
 * reintroduce the whole failure mode. This asserts the invariant instead.
 *
 * "Preceded by a redaction step" is too weak a rule, and a real run proved it. A redaction step
 * placed early in a job scrubs a tree Unity has not written yet; Unity then runs and writes fresh
 * logs, and the upload publishes them unscrubbed. Perf artifacts leaked exactly that way while the
 * first version of this guard passed. So coverage is invalidated by any step that could write into
 * the tree: only uploads, other redaction steps, and one explicitly named post-processing step may
 * sit between a redaction step and the upload it protects.
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

/**
 * Steps allowed between a redaction step and the upload it protects. Everything else is assumed to
 * be able to write Unity output, which invalidates the scrub that came before it. Adding to this
 * list is a deliberate claim that the step writes nothing into an uploaded Unity path.
 */
const NON_PRODUCING_STEP_IDS = new Set(["seal_shipping"]);

function isUpload(uses) {
  return uses.startsWith(UPLOAD_ACTION);
}

function isRedaction(uses) {
  return uses.startsWith(REDACTION_ACTION);
}

/** Every Unity-log-bearing upload in the repository, with the redaction still in force at it. */
function unityUploads() {
  const uploads = [];
  for (const { name, document } of readWorkflows()) {
    for (const [jobId, job] of Object.entries(document?.jobs ?? {})) {
      let inForce = [];
      for (const step of jobSteps(job)) {
        const uses = typeof step?.uses === "string" ? step.uses : "";
        if (isRedaction(uses)) {
          inForce = [...inForce, ...toPathList(step?.with?.paths)];
          continue;
        }
        if (!isUpload(uses)) {
          if (!NON_PRODUCING_STEP_IDS.has(step?.id)) {
            // This step may have written new Unity output, so anything scrubbed before it is stale.
            inForce = [];
          }
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
            inForce: [...inForce]
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
  const unprotected = uploads.filter((upload) => !covers(upload.inForce, upload.uploadedPath));
  assert.deepEqual(
    unprotected.map((upload) => `${upload.workflow} ${upload.jobId} "${upload.stepName}"`),
    [],
    "these steps upload Unity output that still contains the license serial; add a " +
      `"${REDACTION_ACTION}" step covering the uploaded path immediately before the upload, ` +
      "with no Unity-producing step in between"
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
