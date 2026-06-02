/**
 * @fileoverview Static contract guard for the workflow + composite-action
 * side of the Unity host-prereq bootstrap surface:
 *   - .github/workflows/runner-bootstrap.yml (workflow_dispatch + transcript)
 *   - .github/actions/assert-unity-host-prereqs/action.yml (per-job preflight)
 *   - .github/actions/print-self-hosted-runner-diagnostics/action.yml
 *     (existing diagnostics action; now invokes the new composite).
 *
 * These three YAML files form the operator-facing surface that wraps
 * scripts/unity/bootstrap-windows-runner.ps1. A drift in any of them
 * (a missing runner-preflight `needs`, a label set that loses RAM-64GB, a
 * `shell: pwsh` slipping into the bootstrap workflow that's supposed to
 * INSTALL pwsh) would silently break the recovery story even if the script
 * itself is healthy. We pin the load-bearing tokens with pure text-grep
 * assertions (sub-millisecond, no YAML parser dependency, no process spawn).
 *
 * Read these in conjunction with:
 *   - unity-runner-host-prereq-contract.test.js (script-side static)
 *   - unity-runner-host-prereq-helper-mutation.test.js (script-side behavioral)
 *   - unity-ensure-editor-host-prereq-shortcircuit.test.js (ensure-editor wiring)
 */

"use strict";

const fs = require("fs");
const path = require("path");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const WORKFLOW_PATH = path.join(REPO_ROOT, ".github", "workflows", "runner-bootstrap.yml");
const ASSERT_COMPOSITE_PATH = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "assert-unity-host-prereqs",
  "action.yml"
);
const DIAGNOSTICS_COMPOSITE_PATH = path.join(
  REPO_ROOT,
  ".github",
  "actions",
  "print-self-hosted-runner-diagnostics",
  "action.yml"
);

function readUtf8(absPath) {
  return fs.readFileSync(absPath, "utf8");
}

/**
 * Strip YAML `#`-line comments + any trailing `... # comment` segment from
 * each line, preserving line breaks so positional indices stay stable
 * (a leading-`#` comment becomes a blank line; a trailing `# ...` becomes
 * the code prefix only). This lets us assert against the CODE-only view
 * without false-positive matches on prose in comments. `#` inside a YAML
 * scalar string (single or double quoted) is NOT a comment opener and
 * must be preserved -- we honor that with a tiny single-pass scanner that
 * tracks the active quote state. ReDoS-free (single linear pass per line).
 */
function stripYamlComments(yaml) {
  return yaml
    .split("\n")
    .map((line) => {
      let quote = null;
      for (let i = 0; i < line.length; i += 1) {
        const ch = line[i];
        if (quote) {
          if (ch === quote) {
            quote = null;
          }
          continue;
        }
        if (ch === '"' || ch === "'") {
          quote = ch;
          continue;
        }
        if (ch === "#") {
          // YAML comment: `#` at start of line OR preceded by whitespace.
          if (i === 0 || /\s/.test(line[i - 1])) {
            return line.slice(0, i).trimEnd();
          }
        }
      }
      return line;
    })
    .join("\n");
}

// ===========================================================================
// .github/workflows/runner-bootstrap.yml
// ===========================================================================
describe(".github/workflows/runner-bootstrap.yml contract", () => {
  let content;

  beforeAll(() => {
    content = readUtf8(WORKFLOW_PATH);
  });

  test("the workflow file exists", () => {
    expect(fs.existsSync(WORKFLOW_PATH)).toBe(true);
  });

  test("workflow is workflow_dispatch ONLY (no scheduled/push/pull triggers)", () => {
    // The bootstrap is an OPERATOR-DRIVEN recovery; it MUST NOT auto-run on
    // every push (it mutates the host) or on a schedule (it would race with
    // other jobs). The trigger surface is exactly `workflow_dispatch:`.
    expect(content).toMatch(/^on:\s*\n\s+workflow_dispatch:/m);
    // Belt-and-suspenders: no other documented triggers may appear at the
    // top level (a `push:` / `schedule:` / `pull_request:` block at YAML
    // depth-1 indent below `on:`).
    expect(content).not.toMatch(/^on:[\s\S]*?\n {2}push:/m);
    expect(content).not.toMatch(/^on:[\s\S]*?\n {2}schedule:/m);
    expect(content).not.toMatch(/^on:[\s\S]*?\n {2}pull_request:/m);
  });

  test("concurrency group is `runner-bootstrap-windows` (NOT parameterized by runner)", () => {
    // The group is intentionally NOT parameterized by inputs.runner-label so
    // two dispatches cannot race on installer locks / machine-wide UCRT state.
    // It is also distinct from `wallstop-organization-builds` so it does not
    // gate Unity jobs (and Unity jobs do not gate it). We scope the negative
    // grep to the concurrency BLOCK (the `group:` line itself); the bootstrap
    // job's `name:` IS parameterized by runner-label, which is correct and
    // unrelated.
    expect(content).toMatch(/concurrency:\s*\n\s+group:\s+runner-bootstrap-windows\b/);
    expect(content).not.toContain("group: wallstop-organization-builds");
    // Pin the group-line shape directly: it must be exactly the literal
    // `runner-bootstrap-windows`, NOT a Jinja-style expansion.
    const groupLineMatch = /\n\s+group:\s+(.+)\n/.exec(content);
    expect(groupLineMatch).not.toBeNull();
    const groupLine = groupLineMatch[1].trim();
    expect(groupLine).toBe("runner-bootstrap-windows");
  });

  test("declares a `runner-preflight` job on ubuntu-latest (validator required)", () => {
    // Every workflow that runs on self-hosted MUST have an ubuntu-latest
    // runner-access preflight gated `needs:` before the self-hosted job
    // (structural rule -- see docs/runbooks/unity-runners-after-transfer.md).
    expect(content).toMatch(/^\s+runner-preflight:/m);
    expect(content).toMatch(/runner-preflight:[\s\S]*?runs-on:\s+ubuntu-latest/);
  });

  test("`bootstrap` job needs runner-preflight (preflight BEFORE the self-hosted body)", () => {
    // The bootstrap job depends on runner-preflight via `needs:`. Without
    // this, a misconfigured / offline runner would silently consume a
    // self-hosted slot before failing.
    expect(content).toMatch(/^\s+bootstrap:/m);
    expect(content).toMatch(/bootstrap:[\s\S]*?needs:\s*\n\s+-\s+runner-preflight\b/);
  });

  test("`bootstrap` job runs on [self-hosted, Windows, RAM-64GB] (no other label combos)", () => {
    // The repo's Windows runners ALL carry these three labels; targeting
    // anything narrower would require operator action to add machine-name
    // labels (per the workflow's own header comment). The "wrong-target"
    // hard-fail step inside the job is the operator-facing protection.
    expect(content).toMatch(
      /bootstrap:[\s\S]*?runs-on:\s*\[\s*self-hosted\s*,\s*Windows\s*,\s*RAM-64GB\s*\]/
    );
  });

  test("`bootstrap` job uses `shell: powershell` (Windows PS 5.1) NOT `shell: pwsh`", () => {
    // CRITICAL chicken-and-egg constraint: this workflow's purpose
    // INCLUDES installing pwsh -- so it cannot REQUIRE pwsh. Every Windows
    // step inside the bootstrap job must use `shell: powershell` (Windows
    // PowerShell 5.1, always preinstalled on Windows runners). The
    // ubuntu-latest preflight step uses `shell: bash` (unrelated to the
    // chicken-and-egg constraint).
    const lines = content.split("\n");
    let inBootstrapJob = false;
    let bootstrapIndent = -1;
    let foundPwshUse = false;
    for (const line of lines) {
      const trimmed = line.trim();
      const indent = line.length - line.trimStart().length;
      if (/^bootstrap:\s*$/.test(trimmed)) {
        inBootstrapJob = true;
        bootstrapIndent = indent;
        continue;
      }
      if (inBootstrapJob && indent <= bootstrapIndent && trimmed.length > 0 && !trimmed.startsWith("#")) {
        if (/^[A-Za-z][\w-]*:\s*$/.test(trimmed)) {
          // Sibling top-level key -- left the bootstrap job.
          break;
        }
      }
      if (inBootstrapJob && /^shell:\s*pwsh\b/.test(trimmed)) {
        foundPwshUse = true;
      }
    }
    expect(foundPwshUse).toBe(false);
    // Positive form: at least one `shell: powershell` step must exist inside
    // the bootstrap job (the transcript+invoke step).
    expect(content).toMatch(/bootstrap:[\s\S]*?shell:\s*powershell/);
  });

  test("uses actions/checkout@v6 and actions/upload-artifact@v7 (repo-wide pin parity)", () => {
    // Pinned to match the rest of the repo (release.yml/unity-tests.yml/
    // unity-benchmarks.yml all use these versions).
    expect(content).toMatch(/uses:\s+actions\/checkout@v6\b/);
    expect(content).toMatch(/uses:\s+actions\/upload-artifact@v7\b/);
  });

  test("Confirm runner identity step HARD-FAILS (::error:: + exit 1) on mismatch, NOT ::warning::", () => {
    // The previous design's ::warning:: would silently bootstrap the wrong
    // (healthy) machine when the scheduler picked the runner the operator
    // did NOT intend. The fix is a HARD-FAIL.
    expect(content).toMatch(/name:\s+Confirm runner identity/);
    // Inside the Confirm step, we expect ::error:: + exit 1 (not
    // ::warning:: + continue). We strip comments first so a rationale line
    // like `# F2: HARD-FAIL on mismatch. The previous ::warning:: would ...`
    // does not false-positive the negative grep.
    const confirmStepMatch = /name:\s+Confirm runner identity[\s\S]*?(?=\n\s{6}-\s+name:|$)/.exec(
      content
    );
    expect(confirmStepMatch).not.toBeNull();
    const confirmStep = confirmStepMatch[0];
    const confirmStepCodeOnly = stripYamlComments(confirmStep);
    expect(confirmStepCodeOnly).toContain("::error::");
    expect(confirmStepCodeOnly).toMatch(/exit\s+1\b/);
    // The OLD ::warning:: posture MUST NOT reappear in the CODE part.
    expect(confirmStepCodeOnly).not.toMatch(/::warning::/);
  });

  test("upload-artifact uses if-no-files-found: error (HARD failure on missing transcript)", () => {
    // A missing transcript means the bootstrap step failed silently -- a
    // CRITICAL diagnostic loss. We hard-fail the artifact step on missing
    // files rather than silently uploading an empty artifact.
    expect(content).toMatch(/upload-artifact@v7[\s\S]*?if-no-files-found:\s+error/);
  });

  test("honors DXM_RUNNER_DISABLE_AUTO_BOOTSTRAP=1 operator override (forces DetectOnly)", () => {
    // The env-var override is the operator-facing escape hatch. The
    // workflow must respect it identically to the composite (same
    // precedence rule documented in both files' headers).
    expect(content).toMatch(/\$env:DXM_RUNNER_DISABLE_AUTO_BOOTSTRAP\s*-eq\s*'1'/);
  });
});

// ===========================================================================
// .github/actions/assert-unity-host-prereqs/action.yml
// ===========================================================================
describe(".github/actions/assert-unity-host-prereqs/action.yml contract", () => {
  let content;

  beforeAll(() => {
    content = readUtf8(ASSERT_COMPOSITE_PATH);
  });

  test("the composite action file exists", () => {
    expect(fs.existsSync(ASSERT_COMPOSITE_PATH)).toBe(true);
  });

  test("first step uses `shell: powershell` (PS 5.1 preflight for pwsh availability)", () => {
    // F14: the composite's first step verifies pwsh is available, using
    // Windows PowerShell 5.1 so it executes even when pwsh is absent.
    // Without this preflight a missing pwsh would fail the next step with
    // the cryptic "pwsh: command not found".
    const lines = content.split("\n");
    let stepsIdx = -1;
    for (let i = 0; i < lines.length; i += 1) {
      if (/^\s+steps:\s*$/.test(lines[i])) {
        stepsIdx = i;
        break;
      }
    }
    expect(stepsIdx).toBeGreaterThan(-1);
    // Walk forward to the first `- name:` entry; its body must include
    // `shell: powershell`.
    let firstStepStart = -1;
    for (let i = stepsIdx + 1; i < lines.length; i += 1) {
      if (/^\s+-\s+name:/.test(lines[i])) {
        firstStepStart = i;
        break;
      }
    }
    expect(firstStepStart).toBeGreaterThan(-1);
    // Capture the lines until the next `- name:` (sibling step) or eof.
    let firstStepEnd = lines.length;
    for (let i = firstStepStart + 1; i < lines.length; i += 1) {
      if (/^\s+-\s+name:/.test(lines[i])) {
        firstStepEnd = i;
        break;
      }
    }
    const firstStep = lines.slice(firstStepStart, firstStepEnd).join("\n");
    expect(firstStep).toMatch(/shell:\s+powershell\b/);
    // The first step also references pwsh somewhere in its body / error
    // text so a future contributor cannot remove the preflight by accident.
    expect(firstStep).toMatch(/pwsh/i);
  });

  test("resolves the bootstrap script via $env:GITHUB_WORKSPACE (NOT `../../../`)", () => {
    // F6: dropped the brittle GITHUB_ACTION_PATH `../../../` ascent in favor
    // of the documented stable anchor (the repo root after checkout). The
    // OLD `../../../` ascent MUST NOT reappear in the CODE (the comment
    // rationale legitimately references the old shape -- strip comments
    // before the negative grep).
    expect(content).toMatch(
      /Join-Path\s+\$env:GITHUB_WORKSPACE\s+'scripts\/unity\/bootstrap-windows-runner\.ps1'/
    );
    const codeOnly = stripYamlComments(content);
    expect(codeOnly).not.toMatch(/\$env:GITHUB_ACTION_PATH[\s\S]{0,200}\.\.\/\.\.\/\.\./);
    expect(codeOnly).not.toContain("../../../");
  });

  test("honors DXM_RUNNER_DISABLE_AUTO_BOOTSTRAP=1 to force DetectOnly", () => {
    expect(content).toContain("DXM_RUNNER_DISABLE_AUTO_BOOTSTRAP");
    expect(content).toMatch(/\$env:DXM_RUNNER_DISABLE_AUTO_BOOTSTRAP\s*-eq\s*'1'/);
  });

  test("non-Windows host emits a friendly skip notice and exits 0", () => {
    // Non-Windows callers (Linux/macOS) must be no-ops: the composite is
    // sometimes consumed from cross-OS workflows by accident, and the
    // bootstrap script's contract is "Windows-only". The composite must
    // surface a ::notice:: skip and exit 0 rather than ::error:: + 1.
    // The platform gate uses `DirectorySeparatorChar -ne '\'` (one literal
    // backslash inside a PowerShell single-quoted string).
    expect(content).toMatch(/DirectorySeparatorChar\s+-ne\s+'\\'/);
    expect(content).toMatch(/::notice::/);
    expect(content).toContain("skipping");
  });

  test("auto-install input defaults to 'true' and accepts the natural truthy set", () => {
    // F4: input normalization accepts true/True/1/yes/y (case-insensitive)
    // so an operator-typed YAML literal does not silently flip the gate to
    // DetectOnly.
    expect(content).toMatch(/auto-install:[\s\S]*?default:\s*"true"/);
    // The normalization step itself: ToLowerInvariant + the literal
    // accepted token set.
    expect(content).toMatch(/ToLowerInvariant/);
    expect(content).toMatch(/-eq 'true'/);
    expect(content).toMatch(/-eq '1'/);
    expect(content).toMatch(/-eq 'yes'/);
    expect(content).toMatch(/-eq 'y'/);
  });

  test("exports DXM_RUNNER_PREREQ_INSTALLED=1 to $GITHUB_ENV on success (R4-F5 producer)", () => {
    // The downstream consumer in scripts/unity/ensure-editor.ps1
    // (Write-UnityHostPrereqAnnotation, lines ~1184 + ~1218) branches on
    // $env:DXM_RUNNER_PREREQ_INSTALLED to phrase the 0xC0000135 annotation
    // correctly. Without the producer wired here, the consumer's
    // "preflight already ran successfully" branch is dead code and the
    // annotation always says "missing VC++ Redistributable" even when the
    // composite just installed VC++ successfully (operator-misleading).
    // This is the round-2 NR2 BLOCKER fix.
    expect(content).toContain("DXM_RUNNER_PREREQ_INSTALLED=1");
    expect(content).toMatch(/Add-Content[\s\S]{0,200}\$env:GITHUB_ENV/);
    // Producer must be gated on `$code -eq 0` so we never advertise a
    // healthy host when the bootstrap actually failed.
    expect(content).toMatch(/\$code\s*-eq\s*0[\s\S]{0,300}DXM_RUNNER_PREREQ_INSTALLED=1/);
  });
});

// ===========================================================================
// .github/actions/print-self-hosted-runner-diagnostics/action.yml
// ===========================================================================
describe(".github/actions/print-self-hosted-runner-diagnostics/action.yml contract", () => {
  let content;

  beforeAll(() => {
    content = readUtf8(DIAGNOSTICS_COMPOSITE_PATH);
  });

  test("the composite action file exists", () => {
    expect(fs.existsSync(DIAGNOSTICS_COMPOSITE_PATH)).toBe(true);
  });

  test("invokes the new assert-unity-host-prereqs composite", () => {
    // The diagnostics composite was extended to invoke the new
    // host-prereq assertion as its final step, so every Unity job that
    // already prints diagnostics now ALSO asserts the prereqs.
    expect(content).toMatch(
      /uses:\s+\.\/\.github\/actions\/assert-unity-host-prereqs\b/
    );
  });

  test("still has its own PS 5.1 preflight for pwsh availability (belt-and-suspenders)", () => {
    // The diagnostics composite predates the new assert-unity-host-prereqs
    // composite; both happen to have their own PS 5.1 preflight (the new
    // composite's preflight is the "F14" rule, the diagnostics composite's
    // preflight existed before that). Keep both -- removing either silently
    // regresses a load-bearing failure-mode (pwsh missing on the runner).
    expect(content).toMatch(/shell:\s+powershell\b/);
    expect(content).toMatch(/pwsh/i);
  });
});

// ===========================================================================
// Cross-file sanity checks
// ===========================================================================
describe("workflow / composite cross-file consistency", () => {
  test("the workflow + composite + diagnostics composite all exist", () => {
    expect(fs.existsSync(WORKFLOW_PATH)).toBe(true);
    expect(fs.existsSync(ASSERT_COMPOSITE_PATH)).toBe(true);
    expect(fs.existsSync(DIAGNOSTICS_COMPOSITE_PATH)).toBe(true);
  });

  test("workflow wraps runner maintenance and the composite still owns host bootstrap", () => {
    // Manual operator dispatch runs the full maintenance entrypoint (host
    // bootstrap plus Unity editor/module provisioning). The reusable prereq
    // composite stays scoped to host bootstrap for detect-only Unity jobs.
    const workflow = readUtf8(WORKFLOW_PATH);
    const composite = readUtf8(ASSERT_COMPOSITE_PATH);
    expect(workflow).toContain("scripts\\unity\\maintain-windows-runner.ps1");
    expect(composite).toContain("scripts/unity/bootstrap-windows-runner.ps1");
  });
});
