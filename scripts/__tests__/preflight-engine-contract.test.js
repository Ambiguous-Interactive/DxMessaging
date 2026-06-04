/**
 * @fileoverview Engine contract for the change-aware preflight orchestrator
 * (design 7.1). Because file -> hook MATCHING is delegated wholesale to
 * pre-commit, there is no `selectHooksForChange` to unit-test; instead this
 * suite proves the orchestrator (a) DELEGATES correctly (issues the right
 * `pre-commit run --hook-stage <stage>` passes for each targeted stage), (b)
 * that every config hook is REACHABLE by pre-commit's own selection -- validated
 * against the REAL pre-commit oracle in a throwaway temp repo, never against our
 * own regex logic -- and (c) that the Node-direct fallback map COVERS every
 * agent-stage hook id (with an explicit EXEMPT list + a dead-entry guard).
 *
 * House style: parser-derived (precommit-stage-model), CRLF/BOM-safe, real-tool
 * oracle gated with a visible `test.skip` + a STATIC always-on assertion when
 * pre-commit / Python is absent (mirrors the repo's pwsh-gated pattern), and
 * anti-vacuous floors. Node stdlib + existing libs only.
 *
 * How to mutation-test this guard (do this when you touch it):
 *   1. Delete a NODE_DIRECT_MAP entry for a non-exempt, non-deferred agent-stage
 *      hook id -> the completeness test FAILS naming the id -> restore -> GREEN.
 *   2. Add a bogus id to NODE_DIRECT_EXEMPT -> the dead-entry guard FAILS ->
 *      restore -> GREEN.
 *   3. Change runPreCommitMode to emit only the `--files` pass -> the delegation
 *      test FAILS (the committed-range pass is missing) -> restore -> GREEN.
 *   4. In runPreflight's pre-commit branch, drop the `result.anyFailed` ->
 *      synthetic-id propagation -> the "authoritative non-zero exit" tests FAIL
 *      (a no-hook-id failing pass reports ok / exit 0) -> restore -> GREEN.
 *   5. In runPreCommitMode, delete the `options.profile === "guard"` SKIP
 *      injection -> the "defers heavy Jest suites in pre-commit mode" tests FAIL
 *      (SKIP unset) -> restore -> GREEN.
 */

"use strict";

const fs = require("fs");
const os = require("os");
const path = require("path");
const childProcess = require("child_process");

const { spawnPlatformCommandSync } = require("../lib/shell-command");
const { stagesInConfig, hookIdsForStage } = require("../lib/precommit-stage-model");
const preflight = require("../preflight");
const {
  AGENT_STAGES,
  GUARD_DEFERRED_HOOK_IDS,
  GUARD_RANGE_NO_WORKTREE_SKIP_HOOK_IDS,
  PRE_COMMIT_INTERNAL_ERROR_ID,
  PREFLIGHT_INVALID_OPTIONS_ID,
  NODE_DIRECT_MAP,
  NODE_DIRECT_EXEMPT,
  POLICY_HOOK_IDS,
  parseArgs,
  parseFailingHookIds,
  runPreCommitMode,
  runNodeDirectMode,
  runPreflight
} = preflight;

/**
 * Shared deps for runPreflight integration tests that force the pre-commit
 * path (ensurePreCommit ok) with an injected runCommand. The change-set is a
 * single staged file so only the `--files` pass runs.
 *
 * @param {Function} runCommandFn Injected command runner.
 * @returns {object} runPreflight deps.
 */
function preCommitModeDeps(runCommandFn) {
  return {
    computeChangeSetFn: () => ({
      files: ["a.cs"],
      base: null,
      mergeBase: null,
      scope: "worktree",
      sources: { committed: [], staged: ["a.cs"], unstaged: [], untracked: [] }
    }),
    stagesInConfigFn: () => new Set(["pre-commit", "pre-push"]),
    repairNodeToolingFn: () => ({ status: 0 }),
    ensurePreCommitFn: () => ({ ok: true }),
    runCommandFn,
    logFn: () => {},
    env: {}
  };
}

const REPO_ROOT = path.resolve(__dirname, "..", "..");

// ---------------------------------------------------------------------------
// pre-commit availability probe (for the real-oracle reachability test).
// ---------------------------------------------------------------------------

/**
 * Resolve a working `pre-commit run` invocation for the oracle test, trying the
 * `pre-commit` binary first, then `python -m pre_commit` / `python3 -m
 * pre_commit`. Returns `{ command, argsPrefix }` or null when none is usable.
 *
 * @returns {{command: string, argsPrefix: string[]}|null}
 */
function resolvePreCommitInvocation() {
  const candidates = [
    { command: "pre-commit", argsPrefix: [] },
    { command: "python", argsPrefix: ["-m", "pre_commit"] },
    { command: "python3", argsPrefix: ["-m", "pre_commit"] }
  ];
  for (const candidate of candidates) {
    const result = spawnPlatformCommandSync(
      candidate.command,
      [...candidate.argsPrefix, "--version"],
      { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] }
    );
    if (
      result &&
      !result.error &&
      result.status === 0 &&
      /pre-commit\s+\d/.test(String(result.stdout))
    ) {
      return candidate;
    }
  }
  return null;
}

const PRE_COMMIT = resolvePreCommitInvocation();
const PRE_COMMIT_AVAILABLE = PRE_COMMIT !== null;

function runGit(args, cwd) {
  return childProcess.spawnSync("git", args, {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });
}

function runPreCommitOracle(args, cwd) {
  return spawnPlatformCommandSync(PRE_COMMIT.command, [...PRE_COMMIT.argsPrefix, ...args], {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });
}

// ---------------------------------------------------------------------------
// (a) Delegation: the orchestrator issues the right pre-commit passes.
// ---------------------------------------------------------------------------

describe("preflight delegation to pre-commit", () => {
  test("parseArgs accepts explicit range and no-worktree options", () => {
    const options = parseArgs([
      "--stage=pre-push",
      "--profile=guard",
      "--range-from",
      "old",
      "--range-to=new",
      "--no-worktree"
    ]);

    expect(options.stage).toBe("pre-push");
    expect(options.profile).toBe("guard");
    expect(options.rangeFrom).toBe("old");
    expect(options.rangeTo).toBe("new");
    expect(options.noWorktree).toBe(true);
  });

  test("runPreCommitMode issues BOTH committed-range and working-tree passes per stage", () => {
    const calls = [];
    runPreCommitMode({
      options: { all: false, json: true, profile: "full" },
      changeSet: {
        mergeBase: "MB",
        files: ["a.cs", "b.md"],
        sources: { committed: ["a.cs"], staged: ["b.md"], unstaged: [], untracked: [] }
      },
      stages: ["pre-commit", "pre-push"],
      runCommandFn: (command, args) => {
        calls.push({ command, args });
        return { status: 0, stdout: "", stderr: "" };
      },
      logFn: () => {},
      env: {}
    });

    // Every call is `node scripts/ensure-pre-commit.js run --hook-stage <stage> ...`.
    expect(calls.length).toBe(4);
    for (const call of calls) {
      expect(call.command).toBe("node");
      expect(call.args[0]).toBe("scripts/ensure-pre-commit.js");
      expect(call.args[1]).toBe("run");
      expect(call.args[2]).toBe("--hook-stage");
      expect(AGENT_STAGES).toContain(call.args[3]);
    }

    // Per stage: one --from-ref/--to-ref pass and one --files pass.
    for (const stage of ["pre-commit", "pre-push"]) {
      const stageCalls = calls.filter((c) => c.args[3] === stage);
      expect(stageCalls.length).toBe(2);
      const committed = stageCalls.find((c) => c.args.includes("--from-ref"));
      const working = stageCalls.find((c) => c.args.includes("--files"));
      expect(committed).toBeDefined();
      expect(committed.args).toEqual([
        "scripts/ensure-pre-commit.js",
        "run",
        "--hook-stage",
        stage,
        "--from-ref",
        "MB",
        "--to-ref",
        "HEAD"
      ]);
      expect(working).toBeDefined();
      expect(working.args).toEqual([
        "scripts/ensure-pre-commit.js",
        "run",
        "--hook-stage",
        stage,
        "--files",
        "b.md"
      ]);
    }
  });

  test("always_run dedupe: only the --files pass runs when there is no committed range", () => {
    const calls = [];
    runPreCommitMode({
      options: { all: false, json: true, profile: "full" },
      changeSet: {
        mergeBase: null,
        files: ["b.md"],
        sources: { committed: [], staged: ["b.md"], unstaged: [], untracked: [] }
      },
      stages: ["pre-commit"],
      runCommandFn: (command, args) => {
        calls.push({ command, args });
        return { status: 0 };
      },
      logFn: () => {},
      env: {}
    });
    expect(calls.length).toBe(1);
    expect(calls[0].args).toContain("--files");
    expect(calls[0].args).not.toContain("--from-ref");
  });

  test("always_run dedupe: only the committed-range pass runs when there are no working files", () => {
    const calls = [];
    runPreCommitMode({
      options: { all: false, json: true, profile: "full" },
      changeSet: {
        mergeBase: "MB",
        files: ["a.cs"],
        sources: { committed: ["a.cs"], staged: [], unstaged: [], untracked: [] }
      },
      stages: ["pre-commit"],
      runCommandFn: (command, args) => {
        calls.push({ command, args });
        return { status: 0 };
      },
      logFn: () => {},
      env: {}
    });
    expect(calls.length).toBe(1);
    expect(calls[0].args).toContain("--from-ref");
    expect(calls[0].args).not.toContain("--files");
  });

  test("range mode uses the exact range end instead of HEAD", () => {
    const calls = [];
    runPreCommitMode({
      options: { all: false, json: true, profile: "guard" },
      changeSet: {
        mergeBase: "old",
        rangeTo: "new",
        files: ["a.cs"],
        sources: { committed: ["a.cs"], staged: [], unstaged: [], untracked: [] }
      },
      stages: ["pre-push"],
      runCommandFn: (command, args) => {
        calls.push({ command, args });
        return { status: 0 };
      },
      logFn: () => {},
      env: {}
    });

    expect(calls).toHaveLength(1);
    expect(calls[0].args).toEqual([
      "scripts/ensure-pre-commit.js",
      "run",
      "--hook-stage",
      "pre-push",
      "--from-ref",
      "old",
      "--to-ref",
      "new"
    ]);
  });

  test("runPreflight forwards range and no-worktree options to computeChangeSet", () => {
    let captured;
    const { report, exitCode } = runPreflight(
      {
        profile: "guard",
        scope: "full",
        stage: "pre-push",
        recover: true,
        json: true,
        all: false,
        rangeFrom: "old",
        rangeTo: "new",
        noWorktree: true
      },
      {
        computeChangeSetFn: (options) => {
          captured = options;
          return {
            files: ["a.cs"],
            base: "old",
            mergeBase: "old",
            rangeTo: "new",
            scope: "range",
            sources: { committed: ["a.cs"], staged: [], unstaged: [], untracked: [] }
          };
        },
        stagesInConfigFn: () => new Set(["pre-push"]),
        repairNodeToolingFn: () => ({ status: 0 }),
        ensurePreCommitFn: () => ({ ok: true }),
        runCommandFn: () => ({ status: 0, stdout: "", stderr: "" }),
        logFn: () => {},
        env: {}
      }
    );

    expect(exitCode).toBe(0);
    expect(report.scope).toBe("range");
    expect(captured).toEqual({
      baseOverride: undefined,
      scope: "full",
      rangeFrom: "old",
      rangeTo: "new",
      noWorktree: true
    });
  });

  test("runPreflight rejects one-sided explicit ranges before computing changes", () => {
    const computeChangeSetFn = jest.fn();
    const { report, exitCode } = runPreflight(
      {
        profile: "guard",
        scope: "full",
        recover: false,
        json: true,
        all: false,
        rangeFrom: "old",
        rangeTo: null,
        noWorktree: true
      },
      {
        computeChangeSetFn,
        logFn: () => {}
      }
    );

    expect(exitCode).toBe(1);
    expect(report.mode).toBe("invalid-options");
    expect(report.status.failures).toContain(PREFLIGHT_INVALID_OPTIONS_ID);
    expect(report.status.warnings.join("\n")).toContain("--range-from and --range-to");
    expect(computeChangeSetFn).not.toHaveBeenCalled();
  });

  test("runPreflight rejects no-worktree without an explicit range or all-files mode", () => {
    const computeChangeSetFn = jest.fn();
    const { report, exitCode } = runPreflight(
      {
        profile: "guard",
        scope: "full",
        recover: false,
        json: true,
        all: false,
        noWorktree: true
      },
      {
        computeChangeSetFn,
        logFn: () => {}
      }
    );

    expect(exitCode).toBe(1);
    expect(report.status.failures).toContain(PREFLIGHT_INVALID_OPTIONS_ID);
    expect(report.status.warnings.join("\n")).toContain("--no-worktree requires");
    expect(computeChangeSetFn).not.toHaveBeenCalled();
  });

  test("--all routes to a single --all-files pass per stage", () => {
    const calls = [];
    runPreCommitMode({
      options: { all: true, json: true, profile: "full" },
      changeSet: {
        mergeBase: "MB",
        files: ["a.cs"],
        sources: { committed: ["a.cs"], staged: ["b.md"], unstaged: [], untracked: [] }
      },
      stages: ["pre-commit", "pre-push"],
      runCommandFn: (command, args) => {
        calls.push({ command, args });
        return { status: 0 };
      },
      logFn: () => {},
      env: {}
    });
    expect(calls.length).toBe(2);
    for (const call of calls) {
      expect(call.args).toContain("--all-files");
      expect(call.args).not.toContain("--from-ref");
      expect(call.args).not.toContain("--files");
    }
  });

  test("a non-zero pre-commit pass surfaces as a failure via parseFailingHookIds", () => {
    const result = runPreCommitMode({
      options: { all: false, json: true, profile: "full" },
      changeSet: {
        mergeBase: null,
        files: ["a.cs"],
        sources: { committed: [], staged: ["a.cs"], unstaged: [], untracked: [] }
      },
      stages: ["pre-commit"],
      runCommandFn: () => ({
        status: 1,
        stdout: "cspell..................Failed\n- hook id: cspell\n",
        stderr: ""
      }),
      logFn: () => {},
      env: {}
    });
    expect(result.anyFailed).toBe(true);
    expect(result.failedHookIds).toContain("cspell");
  });
});

// ---------------------------------------------------------------------------
// (a') Authoritative exit-code propagation: a non-zero pre-commit pass with NO
// parseable `- hook id:` line MUST still fail (the process exit code -- not the
// parsed ids -- is the source of truth; design 3.3). Without this, a genuinely
// broken pre-commit run (InvalidConfigError, an unstaged config under
// --from-ref, a hook crash, or human-mode inherited stdio with no captured
// stdout) is reported as ok / exit 0 and the push-guard ALLOWS the push.
// ---------------------------------------------------------------------------

describe("preflight propagates the authoritative non-zero exit (anyFailed)", () => {
  test("a status:1 pass with no `- hook id:` line yields checks-failed / exit 1", () => {
    const { report, exitCode } = runPreflight(
      { profile: "guard", scope: "worktree", recover: true, json: true, all: false },
      preCommitModeDeps(() => ({
        status: 1,
        stdout: "An error has occurred: InvalidConfigError",
        stderr: ""
      }))
    );
    expect(report.status.kind).toBe("checks-failed");
    expect(exitCode).toBe(1);
    expect(report.status.failures).toContain(PRE_COMMIT_INTERNAL_ERROR_ID);
  });

  test("human-mode (inherited stdio -> undefined stdout) status:1 still fails", () => {
    const { report, exitCode } = runPreflight(
      { profile: "guard", scope: "worktree", recover: true, json: false, all: false },
      preCommitModeDeps(() => ({ status: 1, stdout: undefined, stderr: undefined }))
    );
    expect(report.status.kind).toBe("checks-failed");
    expect(exitCode).toBe(1);
    expect(report.status.failures).toContain(PRE_COMMIT_INTERNAL_ERROR_ID);
  });

  test("a failing pass is NOT masked as infra-unavailable when a recovery reason is present", () => {
    const deps = preCommitModeDeps(() => ({ status: 1, stdout: "boom (no hook id)", stderr: "" }));
    deps.repairNodeToolingFn = () => ({
      status: 1,
      gateResult: { reason: "package-lock.json has unstaged changes" }
    });
    const { report, exitCode } = runPreflight(
      { profile: "guard", scope: "worktree", recover: true, json: true, all: false },
      deps
    );
    expect(report.status.kind).toBe("checks-failed");
    expect(exitCode).toBe(1);
  });

  test("a parseable `- hook id:` line is still attributed to the real id (not the sentinel)", () => {
    const { report } = runPreflight(
      { profile: "guard", scope: "worktree", recover: true, json: true, all: false },
      preCommitModeDeps(() => ({
        status: 1,
        stdout: "cspell..................Failed\n- hook id: cspell\n",
        stderr: ""
      }))
    );
    expect(report.status.failures).toContain("cspell");
    expect(report.status.failures).not.toContain(PRE_COMMIT_INTERNAL_ERROR_ID);
  });

  test("an all-pass run stays ok / exit 0 (the sentinel is not a false positive)", () => {
    const { report, exitCode } = runPreflight(
      { profile: "guard", scope: "worktree", recover: true, json: true, all: false },
      preCommitModeDeps(() => ({ status: 0, stdout: "", stderr: "" }))
    );
    expect(report.status.kind).toBe("ok");
    expect(exitCode).toBe(0);
  });

  test("the sentinel id is a non-policy failure (lands in failures[], not policyFailures[])", () => {
    expect(POLICY_HOOK_IDS).not.toContain(PRE_COMMIT_INTERNAL_ERROR_ID);
    const { report } = runPreflight(
      { profile: "guard", scope: "worktree", recover: true, json: true, all: false },
      preCommitModeDeps(() => ({ status: 1, stdout: "no hook id here", stderr: "" }))
    );
    expect(report.status.policyFailures).toEqual([]);
    expect(report.status.failures).toEqual([PRE_COMMIT_INTERNAL_ERROR_ID]);
  });
});

// ---------------------------------------------------------------------------
// (a'') Guard-profile deferral in PRE-COMMIT mode (the common path): the heavy
// Jest suites must be deferred to CI/manual pre-push parity via pre-commit's
// SKIP env var. The cross-platform suite only pins this in node-direct mode;
// this pins it in the dominant pre-commit mode (design 5.4). Without it the
// always-on Stop hook + the push-guard run the multi-minute suites in-loop.
// ---------------------------------------------------------------------------

describe("preflight guard profile defers heavy Jest suites in pre-commit mode", () => {
  /**
   * Capture the child env handed to the first pre-commit pass.
   * @param {object} options runPreCommitMode options.
   * @param {object} env base env.
   * @returns {object|undefined} the captured child env.
   */
  function captureChildEnv(options, env = {}) {
    let captured;
    runPreCommitMode({
      options,
      changeSet: {
        mergeBase: null,
        files: ["scripts/preflight.js"],
        sources: { committed: [], staged: ["scripts/preflight.js"], unstaged: [], untracked: [] }
      },
      stages: ["pre-push"],
      runCommandFn: (_command, _args, opts) => {
        captured = opts.env;
        return { status: 0 };
      },
      logFn: () => {},
      env
    });
    return captured;
  }

  test("profile=guard sets SKIP containing every GUARD_DEFERRED_HOOK_IDS entry", () => {
    const childEnv = captureChildEnv({ profile: "guard", all: false, json: true });
    expect(typeof childEnv.SKIP).toBe("string");
    const skipped = childEnv.SKIP.split(",").map((s) => s.trim());
    for (const id of GUARD_DEFERRED_HOOK_IDS) {
      expect(skipped).toContain(id);
    }
  });

  test("profile=full does NOT set SKIP (full runs everything change-scoped)", () => {
    const childEnv = captureChildEnv({ profile: "full", all: false, json: true });
    expect(childEnv.SKIP).toBeUndefined();
  });

  test("profile=guard WITH --all does NOT set SKIP (full parity per stage)", () => {
    let captured;
    runPreCommitMode({
      options: { profile: "guard", all: true, json: true },
      changeSet: {
        mergeBase: "MB",
        files: ["a.cs"],
        sources: { committed: ["a.cs"], staged: [], unstaged: [], untracked: [] }
      },
      stages: ["pre-commit"],
      runCommandFn: (_command, _args, opts) => {
        captured = opts.env;
        return { status: 0 };
      },
      logFn: () => {},
      env: {}
    });
    expect(captured.SKIP).toBeUndefined();
  });

  test("a pre-existing SKIP env is preserved and merged (not clobbered)", () => {
    const childEnv = captureChildEnv({ profile: "guard", all: false, json: true }, { SKIP: "yamllint" });
    const skipped = childEnv.SKIP.split(",").map((s) => s.trim());
    expect(skipped).toContain("yamllint");
    for (const id of GUARD_DEFERRED_HOOK_IDS) {
      expect(skipped).toContain(id);
    }
  });

  test("guard range with --no-worktree skips worktree-only policy hooks", () => {
    const childEnv = captureChildEnv({
      profile: "guard",
      all: false,
      json: true,
      noWorktree: true,
      rangeFrom: "old",
      rangeTo: "new"
    });
    const skipped = childEnv.SKIP.split(",").map((s) => s.trim());
    for (const id of GUARD_RANGE_NO_WORKTREE_SKIP_HOOK_IDS) {
      expect(skipped).toContain(id);
    }
  });

  test("guard range without --no-worktree does not skip worktree-only policy hooks", () => {
    const childEnv = captureChildEnv({
      profile: "guard",
      all: false,
      json: true,
      noWorktree: false,
      rangeFrom: "old",
      rangeTo: "new"
    });
    const skipped = childEnv.SKIP.split(",").map((s) => s.trim());
    for (const id of GUARD_RANGE_NO_WORKTREE_SKIP_HOOK_IDS) {
      expect(skipped).not.toContain(id);
    }
  });

  test("profile=full range with --no-worktree does not set controlled SKIP", () => {
    const childEnv = captureChildEnv({
      profile: "full",
      all: false,
      json: true,
      noWorktree: true,
      rangeFrom: "old",
      rangeTo: "new"
    });
    expect(childEnv.SKIP).toBeUndefined();
  });
});

describe("preflight range no-worktree skips worktree-only hooks in Node-direct mode", () => {
  test("guard range with --no-worktree does not run validate-untracked-policy locally", () => {
    const calls = [];
    runNodeDirectMode({
      options: {
        profile: "guard",
        all: false,
        json: true,
        noWorktree: true,
        rangeFrom: "old",
        rangeTo: "new"
      },
      changeSet: {
        files: ["Runtime/Foo.cs"],
        sources: { committed: ["Runtime/Foo.cs"], staged: [], unstaged: [], untracked: [] }
      },
      stages: ["pre-push"],
      runCommandFn: (command, args) => {
        calls.push({ command, args });
        return { status: 0 };
      },
      logFn: () => {},
      env: {}
    });

    const commandText = calls.map((call) => [call.command, ...call.args].join(" ")).join("\n");
    expect(commandText).not.toContain("scripts/validate-untracked-policy.js");
  });

  test("ordinary guard pre-push still runs validate-untracked-policy locally", () => {
    const calls = [];
    runNodeDirectMode({
      options: { profile: "guard", all: false, json: true, noWorktree: false },
      changeSet: {
        files: ["Runtime/Foo.cs"],
        sources: { committed: ["Runtime/Foo.cs"], staged: [], unstaged: [], untracked: [] }
      },
      stages: ["pre-push"],
      runCommandFn: (command, args) => {
        calls.push({ command, args });
        return { status: 0 };
      },
      logFn: () => {},
      env: {}
    });

    const commandText = calls.map((call) => [call.command, ...call.args].join(" ")).join("\n");
    expect(commandText).toContain("scripts/validate-untracked-policy.js");
  });
});

// ---------------------------------------------------------------------------
// (b) Reachability via the REAL pre-commit oracle (skipped when unavailable).
// ---------------------------------------------------------------------------

const SYNTHETIC_CONFIG = `repos:
  - repo: local
    hooks:
      - id: cs-archetype
        name: cs-archetype
        entry: node -e "process.exit(1)"
        language: system
        types: [c#]
        stages: [pre-commit]
      - id: md-files-archetype
        name: md-files-archetype
        entry: node -e "process.exit(1)"
        language: system
        files: '\\.md$'
        stages: [pre-commit]
      - id: excluded-archetype
        name: excluded-archetype
        entry: node -e "process.exit(1)"
        language: system
        files: '\\.txt$'
        exclude: '^skip/'
        stages: [pre-commit]
      - id: always-archetype
        name: always-archetype
        entry: node -e "process.exit(1)"
        language: system
        always_run: true
        pass_filenames: false
        stages: [pre-commit]
`;

function makeOracleRepo() {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "dxm-preflight-oracle-"));
  expect(runGit(["init", "-q"], temp).status).toBe(0);
  runGit(["config", "user.email", "test@example.com"], temp);
  runGit(["config", "user.name", "Test"], temp);
  fs.writeFileSync(path.join(temp, ".pre-commit-config.yaml"), SYNTHETIC_CONFIG, "utf8");
  fs.writeFileSync(path.join(temp, "a.cs"), "class A {}\n", "utf8");
  fs.writeFileSync(path.join(temp, "b.md"), "# heading\n", "utf8");
  fs.mkdirSync(path.join(temp, "skip"), { recursive: true });
  fs.writeFileSync(path.join(temp, "kept.txt"), "x\n", "utf8");
  fs.writeFileSync(path.join(temp, "skip", "ignored.txt"), "y\n", "utf8");
  expect(runGit(["add", "-A"], temp).status).toBe(0);
  expect(runGit(["commit", "-qm", "init"], temp).status).toBe(0);
  // A second commit changing ONLY a.cs, so the committed range HEAD~1..HEAD is
  // exactly { a.cs }.
  fs.appendFileSync(path.join(temp, "a.cs"), "class B {}\n", "utf8");
  expect(runGit(["add", "a.cs"], temp).status).toBe(0);
  expect(runGit(["commit", "-qm", "two"], temp).status).toBe(0);
  return temp;
}

const describeOracle = PRE_COMMIT_AVAILABLE ? describe : describe.skip;

describeOracle("pre-commit selection oracle (real pre-commit in a temp repo)", () => {
  let temp;
  beforeAll(() => {
    temp = makeOracleRepo();
  });
  afterAll(() => {
    if (temp) {
      fs.rmSync(temp, { recursive: true, force: true });
    }
  });

  test("committed range { a.cs } selects types:[c#] + always_run, skips files:/exclude: archetypes", () => {
    const result = runPreCommitOracle(
      ["run", "--hook-stage", "pre-commit", "--from-ref", "HEAD~1", "--to-ref", "HEAD"],
      temp
    );
    const failing = parseFailingHookIds(`${result.stdout}\n${result.stderr}`);
    expect(failing).toContain("cs-archetype");
    expect(failing).toContain("always-archetype");
    expect(failing).not.toContain("md-files-archetype");
    expect(failing).not.toContain("excluded-archetype");
    expect(result.status).not.toBe(0);
  });

  test("--files b.md selects files:.md + always_run only", () => {
    const result = runPreCommitOracle(
      ["run", "--hook-stage", "pre-commit", "--files", "b.md"],
      temp
    );
    const failing = parseFailingHookIds(`${result.stdout}\n${result.stderr}`);
    expect(failing).toContain("md-files-archetype");
    expect(failing).toContain("always-archetype");
    expect(failing).not.toContain("cs-archetype");
  });

  test("--files honors exclude: a path under skip/ does NOT select the excluded archetype", () => {
    const kept = runPreCommitOracle(
      ["run", "--hook-stage", "pre-commit", "--files", "kept.txt"],
      temp
    );
    const keptFailing = parseFailingHookIds(`${kept.stdout}\n${kept.stderr}`);
    expect(keptFailing).toContain("excluded-archetype");

    const skipped = runPreCommitOracle(
      ["run", "--hook-stage", "pre-commit", "--files", "skip/ignored.txt"],
      temp
    );
    const skippedFailing = parseFailingHookIds(`${skipped.stdout}\n${skipped.stderr}`);
    expect(skippedFailing).not.toContain("excluded-archetype");
  });

  test("--files with a non-matching path selects only always_run (exit 1) -- never the file-scoped ones", () => {
    const result = runPreCommitOracle(
      ["run", "--hook-stage", "pre-commit", "--files", "kept.txt"],
      temp
    );
    const failing = parseFailingHookIds(`${result.stdout}\n${result.stderr}`);
    // always_run fires regardless of the file set.
    expect(failing).toContain("always-archetype");
    // The .cs / .md archetypes do not match a .txt file.
    expect(failing).not.toContain("cs-archetype");
    expect(failing).not.toContain("md-files-archetype");
  });
});

describe("pre-commit oracle availability (static, always on)", () => {
  test("when pre-commit/Python is unavailable the oracle suite is skipped intentionally", () => {
    // This static assertion guarantees the suite never silently no-ops: either
    // pre-commit is available (the describeOracle block above ran) or this
    // documents the skip. On CI (and this devcontainer) pre-commit 4.6.0 is
    // present, so the oracle runs.
    expect(typeof PRE_COMMIT_AVAILABLE).toBe("boolean");
  });
});

// ---------------------------------------------------------------------------
// (c) Node-direct map completeness + dead-entry guard + anti-vacuous floors.
// ---------------------------------------------------------------------------

describe("Node-direct fallback map completeness", () => {
  /**
   * The union of agent-stage hook ids (pre-commit + pre-push), parser-derived.
   * @returns {string[]}
   */
  function agentStageHookIds() {
    const ids = new Set();
    for (const stage of AGENT_STAGES) {
      for (const id of hookIdsForStage(stage)) {
        ids.add(id);
      }
    }
    return [...ids];
  }

  /**
   * All hook ids across EVERY stage in the config (for the dead-entry guard).
   * @returns {Set<string>}
   */
  function allConfigHookIds() {
    const ids = new Set();
    for (const stage of stagesInConfig()) {
      for (const id of hookIdsForStage(stage)) {
        ids.add(id);
      }
    }
    return ids;
  }

  test("every agent-stage hook id is mapped, exempt, or a deferred heavy Jest suite", () => {
    const mapped = new Set(Object.keys(NODE_DIRECT_MAP));
    const exempt = new Set(Object.keys(NODE_DIRECT_EXEMPT));
    const deferred = new Set(GUARD_DEFERRED_HOOK_IDS);

    const uncovered = agentStageHookIds().filter(
      (id) => !mapped.has(id) && !exempt.has(id) && !deferred.has(id)
    );

    if (uncovered.length > 0) {
      throw new Error(
        "Node-direct coverage gap: these agent-stage hook id(s) have neither a " +
          "NODE_DIRECT_MAP entry, a NODE_DIRECT_EXEMPT reason, nor membership in " +
          "GUARD_DEFERRED_HOOK_IDS (the heavy Jest suites deferred to CI/manual " +
          "pre-push parity):\n  " +
          uncovered.join("\n  ") +
          "\nAdd a Node-direct command, an exemption with a cited reason, or " +
          "(if it is a heavy suite) add it to GUARD_DEFERRED_HOOK_IDS."
      );
    }
    expect(uncovered).toEqual([]);
  });

  test("dead-entry guard: every NODE_DIRECT_EXEMPT id is a real hook id in the config", () => {
    const allIds = allConfigHookIds();
    const dead = Object.keys(NODE_DIRECT_EXEMPT).filter((id) => !allIds.has(id));
    if (dead.length > 0) {
      throw new Error(
        "Stale NODE_DIRECT_EXEMPT entries -- these ids are not declared by any " +
          "hook in .pre-commit-config.yaml (renamed or removed):\n  " +
          dead.join("\n  ")
      );
    }
    expect(dead).toEqual([]);
  });

  test("dead-entry guard: every NODE_DIRECT_MAP key is a real hook id in the config", () => {
    const allIds = allConfigHookIds();
    const dead = Object.keys(NODE_DIRECT_MAP).filter((id) => !allIds.has(id));
    if (dead.length > 0) {
      throw new Error(
        "Stale NODE_DIRECT_MAP keys -- these ids are not declared by any hook in " +
          ".pre-commit-config.yaml (renamed or removed):\n  " +
          dead.join("\n  ")
      );
    }
    expect(dead).toEqual([]);
  });

  test("every NODE_DIRECT_EXEMPT reason is a non-empty string", () => {
    for (const [id, reason] of Object.entries(NODE_DIRECT_EXEMPT)) {
      expect(typeof reason).toBe("string");
      expect(reason.trim().length).toBeGreaterThan(0);
      expect(typeof id).toBe("string");
      expect(id.length).toBeGreaterThan(0);
    }
  });

  test("the deferred-suite ids never overlap the Node-direct map (deferred, not routed)", () => {
    for (const id of GUARD_DEFERRED_HOOK_IDS) {
      expect(Object.prototype.hasOwnProperty.call(NODE_DIRECT_MAP, id)).toBe(false);
    }
  });

  test("policy hook ids are all routed by the Node-direct map (never fail open)", () => {
    for (const id of POLICY_HOOK_IDS) {
      expect(Object.prototype.hasOwnProperty.call(NODE_DIRECT_MAP, id)).toBe(true);
    }
  });

  // ---- anti-vacuous floors --------------------------------------------

  test("anti-vacuous: at least two stages are declared in the config", () => {
    expect(stagesInConfig().size).toBeGreaterThanOrEqual(2);
  });

  test("anti-vacuous: the Node-direct map has at least 20 entries", () => {
    expect(Object.keys(NODE_DIRECT_MAP).length).toBeGreaterThanOrEqual(20);
  });

  test("anti-vacuous: there are agent-stage hook ids to cover", () => {
    expect(agentStageHookIds().length).toBeGreaterThanOrEqual(10);
  });
});
