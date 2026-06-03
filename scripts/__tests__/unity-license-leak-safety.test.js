/**
 * @fileoverview Static category guard for the "Unity license seat is leaked
 * because it is never deterministically returned" CI bug.
 *
 * THE CATEGORY: the repo uses classic SERIAL activation (UNITY_SERIAL +
 * UNITY_EMAIL + UNITY_PASSWORD). CI activates the paid Unity seat per run
 * (`Unity.exe -serial -username -password`) and MUST return it
 * (`Unity.exe -returnlicense`) on EVERY exit path -- clean exit, throw, or a
 * timed-out/killed editor that still unwinds. The serial has only a small
 * activation-seat pool (typically ~2 seats) shared ACROSS the whole organization
 * with no server-side reclaim, and the org build lock serializes Unity jobs to
 * one-at-a-time, so a leaked seat blocks other runs until it is reclaimed. The
 * proven defenses are LAYERED:
 *   (a) the runner script's `finally { ... Invoke-UnityLicenseReturn ... }`
 *       returns the seat by account even if the editor crashes; AND
 *   (b) a workflow `if: always()` step (./.github/actions/return-unity-license)
 *       that re-runs the return AFTER the Unity run as a second backstop for a
 *       HARD-killed process that never reaches its own finally; AND
 *   (c) the retired floating-license-server secret (UNITY_LICENSING_SERVER) must
 *       NOT linger in active workflows -- a stray reference silently points CI
 *       back at the removed licensing-server path.
 *
 * This guard is the static companion to the leak-regression smoke test in
 * unity-runner-strictmode-smoke.test.js (which proves the live run returns the
 * seat even when the editor fails). It is pure static analysis in JS (pwsh is
 * NOT required), with an always-on discovery-sanity test so the scan can never
 * silently validate nothing. It is modelled closely on
 * powershell-unity-process-wait-safety.test.js (same string/comment-blanking
 * helper, same in-memory FIRE/SILENT fixtures, same suppression-marker idiom).
 *
 * DETECTION NUANCE: the serial flags live INSIDE quoted PowerShell arrays (e.g.
 * `@('-serial', $Serial)` / `@('-returnlicense', ...)`), and stripCode() BLANKS
 * string interiors -- so the `-serial` / `-returnlicense` literals DO NOT survive
 * stripping. Detector (a) therefore keys on the BARE FUNCTION-NAME identifier
 * tokens `Invoke-UnityLicenseActivate` / `Invoke-UnityLicenseReturn`, which DO
 * survive stripCode (identifiers are code, not string data). run-ci-tests.ps1
 * factors activation into Invoke-UnityLicenseActivate and the return into
 * Invoke-UnityLicenseReturn, calling the return from its finally; that is the
 * correct, defensive shape, not a leak.
 *
 * Three detectors:
 *   (a) PowerShell finally-return: any tracked .ps1 that ACTIVATES a seat (calls
 *       `Invoke-UnityLicenseActivate`, the bare-identifier marker) MUST call
 *       `Invoke-UnityLicenseReturn` from INSIDE a brace-matched `finally { ... }`
 *       block -- EITHER as a direct call OR via a return-helper function whose own
 *       body calls Invoke-UnityLicenseReturn (the factored-out, defensive shape).
 *   (b) Workflow if:always() return step: any job that runs run-ci-tests.ps1 OR
 *       uses game-ci/unity-test-runner@v4 MUST have a LATER step with
 *       `if: always()` that `uses: ./.github/actions/return-unity-license`.
 *   (c) No retired secret in active workflows: an active workflow's text must not
 *       reference the retired floating-license-server secret
 *       secrets.UNITY_LICENSING_SERVER.
 *
 * Suppress a deliberate, reviewed exception with the inline marker
 * `# unity-license-leak-safety: ignore`.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");
const yaml = require("yaml");

const REPO_ROOT = path.resolve(__dirname, "..", "..");

// Suppression marker for a deliberate, reviewed exception.
const SUPPRESS_MARKER = "unity-license-leak-safety: ignore";

const RETURN_ACTION_USES = "./.github/actions/return-unity-license";
const RETURN_ACTION_REL = ".github/actions/return-unity-license/action.yml";

// The bare-identifier markers detector (a) keys on. These survive stripCode
// (they are code identifiers, not string data); the `-serial`/`-returnlicense`
// literals they wrap do NOT survive (they live inside blanked string interiors).
const ACTIVATE_MARKER = "Invoke-UnityLicenseActivate";
const RETURN_MARKER = "Invoke-UnityLicenseReturn";

// ---------------------------------------------------------------------------
// Comment/string-stripped code view (copied from
// powershell-unity-process-wait-safety.test.js). Blanks the INTERIOR of every
// quoted string and drops trailing `# comments`, so a literal like `-serial`
// living inside a string/comment can never be mistaken for a real invocation,
// and so a bare identifier mentioned only in a comment is dropped. Here-string
// bodies (`@'...'@` / `@"..."@`) are blanked.
// ---------------------------------------------------------------------------
function codeOf(line) {
  let out = "";
  let inSingle = false;
  let inDouble = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (ch === "'" && !inDouble) {
      out += ch;
      inSingle = !inSingle;
    } else if (ch === '"' && !inSingle) {
      out += ch;
      inDouble = !inDouble;
    } else if (ch === "#" && !inSingle && !inDouble) {
      break; // trailing comment: drop the remainder of the line
    } else if (inSingle || inDouble) {
      out += " "; // blank the string interior
    } else {
      out += ch;
    }
  }
  return out;
}

// Per-line "code" view of the whole file: here-string bodies are blanked
// entirely (their content is data); every other line is run through codeOf.
// Returns an array aligned 1:1 with the input lines, then joined with "\n" so
// the brace matcher can run over the whole stripped body.
function stripCode(text) {
  const lines = text.split(/\r?\n/);
  const codes = new Array(lines.length);
  let hereTerminator = null; // "'@" or '"@' while inside a here-string body
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (hereTerminator !== null) {
      const trimmed = line.replace(/^\s+/, "");
      codes[i] = "";
      if (trimmed.startsWith(hereTerminator)) {
        hereTerminator = null;
      }
      continue;
    }
    const opener = line.match(/@("|')\s*$/);
    if (opener) {
      const head = line.slice(0, opener.index);
      codes[i] = codeOf(head);
      hereTerminator = opener[1] + "@";
      continue;
    }
    codes[i] = codeOf(line);
  }
  return codes.join("\n");
}

// ---------------------------------------------------------------------------
// Detector (a): PowerShell finally-return brace matcher.
// ---------------------------------------------------------------------------

// Depth-count the `{ ... }` block whose opening brace is at-or-after `from`
// (only whitespace may precede the brace). Returns { body, end } where `end` is
// the index of the matching close brace, or null when no brace block is found.
function matchBraceBlock(stripped, from) {
  let i = from;
  while (i < stripped.length && stripped[i] !== "{") {
    if (!/\s/.test(stripped[i])) {
      return null; // something other than whitespace before the brace
    }
    i++;
  }
  if (stripped[i] !== "{") {
    return null;
  }
  let depth = 0;
  const start = i;
  for (; i < stripped.length; i++) {
    const ch = stripped[i];
    if (ch === "{") {
      depth++;
    } else if (ch === "}") {
      depth--;
      if (depth === 0) {
        return { body: stripped.slice(start + 1, i), end: i };
      }
    }
  }
  return null;
}

// Find every `finally` keyword (standalone token) in the stripped text and
// capture its `{ ... }` body by depth counting. Returns an array of body
// strings. A `finally` with no following `{` is skipped (malformed / not ours).
function extractFinallyBodies(stripped) {
  const bodies = [];
  const finallyRe = /\bfinally\b/g;
  let m;
  while ((m = finallyRe.exec(stripped)) !== null) {
    const block = matchBraceBlock(stripped, m.index + m[0].length);
    if (block) {
      bodies.push(block.body);
    }
  }
  return bodies;
}

// Names of PowerShell functions whose body calls Invoke-UnityLicenseReturn. A
// call to one of these from inside a finally returns the seat just as a direct
// Invoke-UnityLicenseReturn call would. This mirrors the original return-helper
// mechanism (a thin wrapper that delegates to the return). Returns a lowercased
// Set of function names. The defining function itself (Invoke-UnityLicenseReturn)
// is excluded so its own body (which contains the return args, not a self-call)
// is not mistaken for a wrapper. We match the bare RETURN_MARKER identifier,
// which survives stripCode.
function findReturnHelperFunctions(stripped) {
  const names = new Set();
  const fnRe = /\bfunction\s+([A-Za-z_][\w-]*)/g;
  let m;
  while ((m = fnRe.exec(stripped)) !== null) {
    const fnName = m[1];
    if (fnName.toLowerCase() === RETURN_MARKER.toLowerCase()) {
      continue; // the return function itself is not a wrapper-of-itself
    }
    const block = matchBraceBlock(stripped, m.index + m[0].length);
    if (block && block.body.includes(RETURN_MARKER)) {
      names.add(fnName.toLowerCase());
    }
  }
  return names;
}

// Scan one .ps1 body for the finally-return leak. Returns an array of finding
// strings (empty when clean / not applicable).
function scanPowerShellFinallyReturn(relPath, text) {
  const lines = text.split(/\r?\n/);
  // Suppression: a marker ANYWHERE in the file opts the whole script out (the
  // activate/return live in one tightly-coupled region of one script).
  if (lines.some((l) => l.includes(SUPPRESS_MARKER))) {
    return [];
  }

  const stripped = stripCode(text);
  if (!stripped.includes(ACTIVATE_MARKER)) {
    return []; // does not activate a seat -> nothing to bracket
  }

  const finallyBodies = extractFinallyBodies(stripped);
  const returnHelpers = findReturnHelperFunctions(stripped);
  const returnedInFinally = finallyBodies.some((body) => {
    // Direct Invoke-UnityLicenseReturn call inside the finally.
    if (body.includes(RETURN_MARKER)) {
      return true;
    }
    // Or a call to a return-helper function whose body calls
    // Invoke-UnityLicenseReturn (the factored-out, defensive shape).
    for (const name of returnHelpers) {
      if (new RegExp(`\\b${name}\\b`, "i").test(body)) {
        return true;
      }
    }
    return false;
  });
  if (returnedInFinally) {
    return [];
  }

  return [
    `${relPath}: activates a Unity license (${ACTIVATE_MARKER}) but does NOT ` +
      `return it (${RETURN_MARKER}, directly or via a return-helper function) from ` +
      `inside a finally { ... } block, so a throw/kill between activate and the ` +
      `explicit return LEAKS the single shared seat. Wrap the run in ` +
      `try { ... } finally { ... ${RETURN_MARKER} ... }. ` +
      `(Suppress with "# ${SUPPRESS_MARKER}" if intentional.)`
  ];
}

// ---------------------------------------------------------------------------
// Detector (b): workflow if:always() return step ordered after the Unity run.
// Operates on parsed YAML (the `yaml` package), like unity-workflow-shape.test.js.
// ---------------------------------------------------------------------------

// True when a step is a native Unity run: a pwsh `run:` invoking
// run-ci-tests.ps1, OR a `uses: game-ci/unity-test-runner@v4`.
function isUnityRunStep(step) {
  if (!step || typeof step !== "object") {
    return false;
  }
  if (typeof step.uses === "string" && step.uses.trim() === "game-ci/unity-test-runner@v4") {
    return true;
  }
  if (typeof step.run === "string" && /run-ci-tests\.ps1/.test(step.run)) {
    return true;
  }
  return false;
}

function isAlwaysReturnStep(step) {
  return (
    step &&
    typeof step === "object" &&
    typeof step.uses === "string" &&
    step.uses.trim() === RETURN_ACTION_USES &&
    typeof step.if === "string" &&
    /always\s*\(\s*\)/.test(step.if)
  );
}

// Scan one parsed workflow doc. `relPath` labels findings.
function scanWorkflowReturnStep(relPath, doc) {
  const findings = [];
  const jobs = (doc && doc.jobs) || {};
  for (const [jobId, job] of Object.entries(jobs)) {
    if (!job || !Array.isArray(job.steps)) {
      continue;
    }
    const steps = job.steps;
    // Index of the LAST native Unity run step (return must come after the run).
    let lastRunIndex = -1;
    for (let i = 0; i < steps.length; i++) {
      if (isUnityRunStep(steps[i])) {
        lastRunIndex = i;
      }
    }
    if (lastRunIndex === -1) {
      continue; // job does not run Unity natively -> not in scope
    }
    const returnIndex = steps.findIndex((s) => isAlwaysReturnStep(s));
    if (returnIndex === -1) {
      findings.push(
        `${relPath} :: ${jobId}: runs Unity natively but has no if: always() step ` +
          `that uses ${RETURN_ACTION_USES}. A hard-killed editor cannot return its ` +
          `own seat; add the if: always() return step after the Unity run.`
      );
      continue;
    }
    if (returnIndex < lastRunIndex) {
      findings.push(
        `${relPath} :: ${jobId}: the if: always() ${RETURN_ACTION_USES} step is ` +
          `ordered BEFORE the Unity run step (index ${returnIndex} < ${lastRunIndex}). ` +
          `Move it AFTER the run so it can return the seat the run activated.`
      );
    }
  }
  return findings;
}

// ---------------------------------------------------------------------------
// Discovery: tracked .ps1 scripts + active workflow .yml/.yaml files.
// ---------------------------------------------------------------------------
function listTrackedFiles(globArg) {
  const result = spawnSync("git", ["ls-files", globArg], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  if (result.status !== 0) {
    return [];
  }
  return result.stdout
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean);
}

function listTrackedFilesAny(...globArgs) {
  const seen = new Set();
  const out = [];
  for (const g of globArgs) {
    for (const rel of listTrackedFiles(g)) {
      if (!seen.has(rel)) {
        seen.add(rel);
        out.push(rel);
      }
    }
  }
  return out;
}

const PS1_FILES = listTrackedFiles("*.ps1");

// Active workflow files ONLY (NOT .github/workflows-disabled). The glob is
// anchored to .github/workflows/ so a disabled workflow under
// .github/workflows-disabled/ is never scanned.
const ACTIVE_WORKFLOW_FILES = listTrackedFilesAny(
  ".github/workflows/*.yml",
  ".github/workflows/*.yaml"
);

describe("Unity serial-activation license-leak static guard", () => {
  // ANTI-NO-OP: a silently-empty scan is the false-green class this guard
  // exists to prevent.
  test("discovers the scanned surface (scripts + active workflows + return action)", () => {
    expect(PS1_FILES).toContain("scripts/unity/run-ci-tests.ps1");
    expect(ACTIVE_WORKFLOW_FILES.length).toBeGreaterThanOrEqual(1);

    // The return-license composite action must exist with the right shape.
    const actionAbs = path.join(REPO_ROOT, RETURN_ACTION_REL);
    expect(fs.existsSync(actionAbs)).toBe(true);
    const actionText = fs.readFileSync(actionAbs, "utf8");
    const actionDoc = yaml.parse(actionText);
    expect(actionDoc.runs.using).toBe("composite");
    expect(actionText).toMatch(/shell:\s*pwsh/);
    expect(actionText).toContain("-returnlicense");

    // Sanity: run-ci-tests.ps1 ACTUALLY contains the activate marker so detector
    // (a) does real work on it (it is the only script that activates a seat).
    const runCi = fs.readFileSync(
      path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1"),
      "utf8"
    );
    expect(stripCode(runCi)).toContain(ACTIVATE_MARKER);
  });

  // ----- Detector (a) against the real .ps1 surface -----
  test.each(PS1_FILES)("%s returns its activated seat inside a finally block", (rel) => {
    const text = fs.readFileSync(path.join(REPO_ROOT, rel), "utf8");
    expect(scanPowerShellFinallyReturn(rel, text)).toEqual([]);
  });

  test("run-ci-tests.ps1 is actually exercised by detector (a) (it activates a seat)", () => {
    const text = fs.readFileSync(
      path.join(REPO_ROOT, "scripts", "unity", "run-ci-tests.ps1"),
      "utf8"
    );
    // Sanity: the script under guard genuinely contains the activate marker as a
    // BARE identifier (it survives stripCode), so detector (a) is doing real work
    // on it rather than short-circuiting on the "no activation" early return.
    expect(stripCode(text)).toContain(ACTIVATE_MARKER);
    // And the return marker also survives stripping (the `-returnlicense` literal
    // does NOT -- it lives inside a blanked string -- which is exactly why the
    // detector keys on the bare function-name identifiers, not the flags).
    const stripped = stripCode(text);
    expect(stripped).toContain(RETURN_MARKER);
    expect(stripped).not.toContain("-returnlicense");
    expect(scanPowerShellFinallyReturn("run-ci-tests.ps1", text)).toEqual([]);
  });

  // ----- Detector (b) against the real active-workflow surface -----
  test.each(ACTIVE_WORKFLOW_FILES)(
    "%s has an if: always() license return step after every Unity run",
    (rel) => {
      const doc = yaml.parse(fs.readFileSync(path.join(REPO_ROOT, rel), "utf8"));
      expect(scanWorkflowReturnStep(rel, doc)).toEqual([]);
    }
  );

  // ----- Detector (c) against the real active-workflow surface -----
  // Scoped to the `secrets.` prefix so it does NOT false-fire on the validate
  // action's remediation prose (which names the bare env var) or the local
  // scripts' fallback env vars.
  test.each(ACTIVE_WORKFLOW_FILES)(
    "%s does not reference the retired Unity licensing-server secret",
    (rel) => {
      const text = fs.readFileSync(path.join(REPO_ROOT, rel), "utf8");
      expect(text).not.toMatch(/secrets\.UNITY_LICENSING_SERVER\b/);
    }
  );

  // -----------------------------------------------------------------------
  // Detector self-tests: prove each detector FIRES on the bad shape and is
  // SILENT on the good shape. Tiny in-memory fixtures; no repo files touched.
  // -----------------------------------------------------------------------
  describe("detector (a) finally-return behavior on fixtures", () => {
    const FIRE_NO_FINALLY = [
      "Set-StrictMode -Version Latest",
      "Invoke-UnityLicenseActivate -EditorPath $editor -Serial $s -Email $e -Password $p -LogPath $log",
      "& $editor @args -logFile - 2>&1 | Tee-Object -FilePath $log",
      "Invoke-UnityLicenseReturn -EditorPath $editor -Email $e -Password $p -LogPath $log", // returned, but NOT inside a finally
      ""
    ].join("\n");

    const SILENT_RETURN_IN_FINALLY = [
      "Set-StrictMode -Version Latest",
      "Invoke-UnityLicenseActivate -EditorPath $editor -Serial $s -Email $e -Password $p -LogPath $log",
      "try {",
      "    & $editor @args -logFile - 2>&1 | Tee-Object -FilePath $log",
      "} finally {",
      "    Invoke-UnityLicenseReturn -EditorPath $editor -Email $e -Password $p -LogPath $log",
      "}",
      ""
    ].join("\n");

    const SILENT_NO_ACTIVATE = [
      "Set-StrictMode -Version Latest",
      "# this script runs the editor but never activates a paid seat",
      "& $editor @args -logFile - 2>&1 | Tee-Object -FilePath $log",
      ""
    ].join("\n");

    test("FIRES when Invoke-UnityLicenseReturn is present but NOT inside a finally", () => {
      const findings = scanPowerShellFinallyReturn("fixture.ps1", FIRE_NO_FINALLY);
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("finally");
    });

    test("FIRES when there is NO Invoke-UnityLicenseReturn at all", () => {
      const noReturn = [
        "Set-StrictMode -Version Latest",
        "Invoke-UnityLicenseActivate -EditorPath $editor -Serial $s -Email $e -Password $p -LogPath $log",
        "try { & $editor @args } finally { Write-Host 'done' }",
        ""
      ].join("\n");
      const findings = scanPowerShellFinallyReturn("fixture.ps1", noReturn);
      expect(findings).toHaveLength(1);
    });

    const SILENT_RETURN_VIA_HELPER = [
      "Set-StrictMode -Version Latest",
      "function Invoke-SeatReturn {",
      "    param($EditorPath, $Email, $Password, $LogPath)",
      "    Invoke-UnityLicenseReturn -EditorPath $EditorPath -Email $Email -Password $Password -LogPath $LogPath",
      "}",
      "Invoke-UnityLicenseActivate -EditorPath $editor -Serial $s -Email $e -Password $p -LogPath $log",
      "try {",
      "    & $editor @args -logFile - 2>&1 | Tee-Object -FilePath $log",
      "} finally {",
      "    Invoke-SeatReturn -EditorPath $editor -Email $e -Password $p -LogPath $log",
      "}",
      ""
    ].join("\n");

    test("SILENT when activate+return are bracketed by a finally", () => {
      expect(scanPowerShellFinallyReturn("fixture.ps1", SILENT_RETURN_IN_FINALLY)).toEqual([]);
    });

    test("SILENT when the finally calls a return-helper function (wrapper shape)", () => {
      expect(scanPowerShellFinallyReturn("fixture.ps1", SILENT_RETURN_VIA_HELPER)).toEqual([]);
    });

    test("SILENT for a .ps1 that runs the editor but never activates (local run-tests shape)", () => {
      expect(scanPowerShellFinallyReturn("fixture.ps1", SILENT_NO_ACTIVATE)).toEqual([]);
    });

    test("SILENT when the suppression marker is present even on a bad shape", () => {
      const suppressed = FIRE_NO_FINALLY + "\n# " + SUPPRESS_MARKER + "\n";
      expect(scanPowerShellFinallyReturn("fixture.ps1", suppressed)).toEqual([]);
    });

    test("does NOT false-fire on the activate marker mentioned inside a string/comment only", () => {
      const stringy = [
        "Set-StrictMode -Version Latest",
        "Write-Host 'we used to call Invoke-UnityLicenseActivate without a finally return'",
        "# Invoke-UnityLicenseActivate is mentioned here but never invoked",
        ""
      ].join("\n");
      expect(scanPowerShellFinallyReturn("fixture.ps1", stringy)).toEqual([]);
    });
  });

  describe("detector (b) workflow return-step behavior on fixtures", () => {
    const GOOD_RUN_CI = yaml.parse(
      [
        "jobs:",
        "  unity:",
        "    steps:",
        "      - name: Acquire lock",
        "        uses: org/lock@v1",
        "      - name: Run Unity Test Runner",
        "        shell: pwsh",
        "        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode",
        "      - name: Return Unity license",
        "        if: always()",
        "        uses: ./.github/actions/return-unity-license",
        "      - name: Release lock",
        "        if: always()",
        "        uses: org/lock-release@v1"
      ].join("\n")
    );

    const MISSING_RETURN = yaml.parse(
      [
        "jobs:",
        "  unity:",
        "    steps:",
        "      - name: Run Unity Test Runner",
        "        shell: pwsh",
        "        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode",
        "      - name: Release lock",
        "        if: always()",
        "        uses: org/lock-release@v1"
      ].join("\n")
    );

    const RETURN_BEFORE_RUN = yaml.parse(
      [
        "jobs:",
        "  unity:",
        "    steps:",
        "      - name: Return Unity license",
        "        if: always()",
        "        uses: ./.github/actions/return-unity-license",
        "      - name: Run Unity Test Runner",
        "        shell: pwsh",
        "        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode"
      ].join("\n")
    );

    const GOOD_GAMECI = yaml.parse(
      [
        "jobs:",
        "  experiment:",
        "    steps:",
        "      - name: Run GameCI",
        "        uses: game-ci/unity-test-runner@v4",
        "      - name: Return Unity license",
        "        if: always()",
        "        uses: ./.github/actions/return-unity-license"
      ].join("\n")
    );

    const RETURN_WITHOUT_ALWAYS = yaml.parse(
      [
        "jobs:",
        "  unity:",
        "    steps:",
        "      - name: Run Unity Test Runner",
        "        shell: pwsh",
        "        run: ./scripts/unity/run-ci-tests.ps1 -TestMode editmode",
        "      - name: Return Unity license",
        "        uses: ./.github/actions/return-unity-license"
      ].join("\n")
    );

    const NO_UNITY = yaml.parse(
      ["jobs:", "  lint:", "    steps:", "      - run: npm test"].join("\n")
    );

    test("SILENT on a clean run-ci-tests.ps1 job with an ordered if:always() return", () => {
      expect(scanWorkflowReturnStep("good.yml", GOOD_RUN_CI)).toEqual([]);
    });

    test("SILENT on a clean game-ci/unity-test-runner@v4 job with the return step", () => {
      expect(scanWorkflowReturnStep("good-gameci.yml", GOOD_GAMECI)).toEqual([]);
    });

    test("FIRES when the if:always() return step is missing", () => {
      const findings = scanWorkflowReturnStep("bad.yml", MISSING_RETURN);
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("no if: always() step");
    });

    test("FIRES when the return step is ordered BEFORE the Unity run", () => {
      const findings = scanWorkflowReturnStep("bad-order.yml", RETURN_BEFORE_RUN);
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("ordered BEFORE");
    });

    test("FIRES when the return step lacks if: always()", () => {
      const findings = scanWorkflowReturnStep("bad-if.yml", RETURN_WITHOUT_ALWAYS);
      expect(findings).toHaveLength(1);
      expect(findings[0]).toContain("no if: always() step");
    });

    test("SILENT on a job that does not run Unity at all", () => {
      expect(scanWorkflowReturnStep("lint.yml", NO_UNITY)).toEqual([]);
    });
  });

  describe("detector (c) retired-secret regex behavior", () => {
    const RETIRED_RE = /secrets\.UNITY_LICENSING_SERVER\b/;

    test("MATCHES the retired licensing-server secret reference", () => {
      expect("UNITY_LICENSING_SERVER: ${{ secrets.UNITY_LICENSING_SERVER }}").toMatch(RETIRED_RE);
      expect("${{ secrets.UNITY_LICENSING_SERVER }}").toMatch(RETIRED_RE);
    });

    test("does NOT match the active serial-activation secrets", () => {
      expect("${{ secrets.UNITY_SERIAL }}").not.toMatch(RETIRED_RE);
      expect("${{ secrets.UNITY_EMAIL }}").not.toMatch(RETIRED_RE);
      expect("${{ secrets.UNITY_PASSWORD }}").not.toMatch(RETIRED_RE);
    });

    test("does NOT match the bare env-var name in remediation prose (no secrets. prefix)", () => {
      expect(
        "Retired secret (UNITY_LICENSING_SERVER) is set; remove it after the migration."
      ).not.toMatch(RETIRED_RE);
    });
  });
});

module.exports = {
  scanPowerShellFinallyReturn,
  scanWorkflowReturnStep,
  extractFinallyBodies,
  findReturnHelperFunctions,
  matchBraceBlock,
  stripCode
};
