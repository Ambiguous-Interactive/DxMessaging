"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const YAML = require("yaml");
const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOW_DIRECTORY = path.join(REPO_ROOT, ".github", "workflows");
const REDACTION_ACTION = "./.github/actions/redact-unity-artifacts";
const UPLOAD_ACTION = "actions/upload-artifact";
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
function normalizePath(value) {
  return String(value)
    .replace(/\$\{\{([^}]*)\}\}/g, (_match, expression) => `<${expression.trim()}>`)
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
function covers(redactionPaths, uploadedPath) {
  return redactionPaths.some((declared) => {
    const root = declared.replace(/\/+$/, "");
    return root.length > 0 && (uploadedPath === root || uploadedPath.startsWith(`${root}/`));
  });
}
function alwaysRuns(step) {
  if (step.if === undefined) {
    return false;
  }
  return String(step.if)
    .replace(/\$\{\{|\}\}/g, "")
    .trim()
    .split("&&")
    .some((clause) => clause.trim() === "always()");
}
function requiresRedactionSuccess(step, redactionId) {
  return String(step.if ?? "").includes(`steps.${redactionId}.outcome == 'success'`);
}
function jobSteps(job) {
  return Array.isArray(job?.steps) ? job.steps : [];
}
const NON_PRODUCING_STEP_IDS = new Set(["seal_shipping"]);
function isUpload(uses) {
  return uses.startsWith(UPLOAD_ACTION);
}
function isRedaction(uses) {
  return uses.startsWith(REDACTION_ACTION);
}
function unityUploads(workflows = readWorkflows()) {
  const uploads = [];
  for (const { name, document } of workflows) {
    for (const [jobId, job] of Object.entries(document?.jobs ?? {})) {
      let inForce = [];
      for (const step of jobSteps(job)) {
        const uses = typeof step?.uses === "string" ? step.uses : "";
        if (isRedaction(uses)) {
          if (alwaysRuns(step)) {
            inForce.push(
              ...toPathList(step?.with?.paths).map((coveredPath) => ({
                coveredPath,
                redactionId: step.id
              }))
            );
          }
          continue;
        }
        if (!isUpload(uses)) {
          if (!NON_PRODUCING_STEP_IDS.has(step?.id)) {
            inForce = [];
          }
          continue;
        }
        for (const uploadedPath of toPathList(step?.with?.path)) {
          if (!isUnityOutput(uploadedPath)) {
            continue;
          }
          const covering = inForce.findLast(({ coveredPath }) =>
            covers([coveredPath], uploadedPath)
          );
          uploads.push({
            workflow: name,
            jobId,
            stepName: step.name ?? uses,
            uploadedPath,
            inForce: inForce.map(({ coveredPath }) => coveredPath),
            redactionId: covering?.redactionId,
            gated: covering !== undefined && requiresRedactionSuccess(step, covering.redactionId)
          });
        }
      }
    }
  }
  return uploads;
}
test("every Unity artifact upload is preceded by sensitive-data redaction in the same job", () => {
  const uploads = unityUploads();
  assert.equal(
    uploads.length,
    15,
    "the number of Unity-log-bearing uploads changed; confirm each one is still redacted"
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
test("every Unity artifact upload refuses to run after a failed scrub", () => {
  const ungated = unityUploads().filter((upload) => !upload.gated);
  assert.deepEqual(
    ungated.map((upload) => `${upload.workflow} ${upload.jobId} "${upload.stepName}"`),
    [],
    "these steps upload Unity output under always(), so a failed redaction step still publishes " +
      "the tree; add steps.<redaction step id>.outcome == 'success' to the upload condition"
  );
});
test("an upload is gated by the redactor that covered its path", () => {
  const steps = [
    {
      id: "redact_a",
      uses: REDACTION_ACTION,
      if: "always()",
      with: { paths: ".artifacts/unity/a" }
    },
    {
      id: "redact_b",
      uses: REDACTION_ACTION,
      if: "always()",
      with: { paths: ".artifacts/unity/b" }
    },
    {
      uses: UPLOAD_ACTION,
      if: "always() && steps.redact_b.outcome == 'success'",
      with: { path: ".artifacts/unity/a" }
    }
  ];
  const uploads = unityUploads([
    { name: "synthetic.yml", document: { jobs: { test: { steps } } } }
  ]);
  assert.equal(uploads[0].redactionId, "redact_a");
  assert.equal(uploads[0].gated, false);
});
test("every redaction step requires this run's own node setup", () => {
  const offenders = [];
  for (const { name, document } of readWorkflows()) {
    for (const [jobId, job] of Object.entries(document?.jobs ?? {})) {
      const steps = jobSteps(job);
      const nodeIndex = steps.findIndex(
        (step) => typeof step.uses === "string" && step.uses.includes("actions/setup-node")
      );
      steps.forEach((step, index) => {
        if (!isRedaction(typeof step.uses === "string" ? step.uses : "")) {
          return;
        }
        const nodeId = nodeIndex >= 0 ? steps[nodeIndex].id : undefined;
        const gated =
          nodeId !== undefined &&
          nodeIndex < index &&
          String(step.if ?? "").includes(`steps.${nodeId}.outcome == 'success'`);
        if (!gated) {
          offenders.push(`${name} ${jobId} "${step.name ?? step.uses}"`);
        }
      });
    }
  }
  assert.deepEqual(
    offenders,
    [],
    "these redaction steps can fire on a run that never set up node; gate each one on the " +
      "outcome of a Setup Node.js step that precedes it in the same job"
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
    "sealing must follow redaction, or a bundle can be sealed around sensitive data"
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
  assert.equal(steps[uploadIndex].with?.["include-hidden-files"], true);
  assert.match(
    String(steps[uploadIndex].if),
    /steps\.seal_shipping\.outcome == 'success'/,
    "a failed seal or replay must block the shipping evidence upload"
  );
});
const COVERAGE_CASES = Object.freeze([
  [
    "different runtime roots",
    "<github.workspace>/.artifacts/unity",
    "<runner.temp>/.artifacts/unity/logs",
    false
  ],
  [
    "a string prefix in a different directory",
    ".artifacts/unity/a",
    ".artifacts/unity/ab/unity.log",
    false
  ],
  ["a sibling directory", ".artifacts/unity", ".artifacts/unity-secrets/unity.log", false],
  ["a genuine parent directory", ".artifacts/unity", ".artifacts/unity/6000-shipping", true],
  [
    "an exact match",
    "<runner.temp>/dx-unity-editor-validation",
    "<runner.temp>/dx-unity-editor-validation",
    true
  ],
  ["a trailing slash on the upload", ".artifacts/unity", ".artifacts/unity/release-dist/", true]
]);
for (const [label, declared, uploaded, expected] of COVERAGE_CASES) {
  test(`covers reports ${expected} for ${label}`, () => {
    assert.equal(
      covers([declared], uploaded),
      expected,
      `${label}: the guard is only as strong as this comparison, so a wrong answer here lets an ` +
        "left unredacted Unity upload satisfy it"
    );
  });
}
test("normalizePath keeps distinct workflow expressions distinct", () => {
  assert.equal(
    normalizePath("${{ runner.temp }}/x"),
    "<runner.temp>/x",
    "an expression collapses to a token built from its own text"
  );
  assert.equal(
    normalizePath("${{ github.workspace }}/x"),
    "<github.workspace>/x",
    "a different expression collapses to a different token"
  );
  assert.notEqual(
    normalizePath("${{ runner.temp }}/x"),
    normalizePath("${{ github.workspace }}/x"),
    "erasing expressions made two different runtime roots compare equal, so an upload from one " +
      "root read as covered by a redaction of the other"
  );
  assert.equal(
    normalizePath(" ${{ runner.temp }}\\logs\\unity.log "),
    "<runner.temp>/logs/unity.log",
    "backslashes and surrounding whitespace are normalized away"
  );
});
const ALWAYS_RUNS_CASES = Object.freeze([
  ["no condition at all", undefined, false],
  ["an explicit success()", "${{ success() }}", false],
  ["a bare always()", "always()", true],
  ["a wrapped always()", "${{ always() }}", true],
  ["always() combined with another clause", "${{ always() && !cancelled() }}", true],
  ["a hard false", "false", false]
]);
for (const [label, condition, expected] of ALWAYS_RUNS_CASES) {
  test(`alwaysRuns reports ${expected} for ${label}`, () => {
    const step = condition === undefined ? {} : { if: condition };
    assert.equal(
      alwaysRuns(step),
      expected,
      `${label}: a redaction step that skips must not be counted as coverage`
    );
  });
}
