/**
 * @fileoverview Contract test for the change-aware preflight Claude Code hooks
 * (design 7.3). It pins:
 *
 *   1. `.claude/settings.json` SHAPE: a `PreToolUse` Bash-matcher entry pointing
 *      at the push-guard, a `Stop` entry pointing at the advisory stop hook, the
 *      EXISTING `PostToolUse` yaml-line-length-guard entry preserved, every hook
 *      command wired through `$CLAUDE_PROJECT_DIR`, and NO `permissions` key (the
 *      permissions contract lives only in the gitignored settings.local.json --
 *      a committed auto-approval must fail this test; enforces context.md:26).
 *   2. Both hook script files exist on disk AND are git-tracked.
 *   3. The guard's `buildDecision(status)` JSON shape against the documented
 *      PreToolUse schema (`hookSpecificOutput.hookEventName === "PreToolUse"`,
 *      `permissionDecision` enum): ok->allow, changed-file cspell->deny,
 *      checks-failed->deny naming hooks, infra-unavailable->allow+warning,
 *      infra-unavailable WITH policyFailures ->deny (policy/security never fail
 *      open).
 *   4. The stop hook's `buildAdvisory(status)`: advisory-only (a STRING on
 *      checks-failed / policyFailures, null otherwise) -- and a source scan
 *      proving the stop hook NEVER emits `decision: "block"` (owner override:
 *      Stop is advisory only).
 *   5. `commandLooksLikeGitPush(cmd)` / `commandLooksLikeGitCommit(cmd)`
 *      positives / conservative-true / negatives,
 *      asserted-as-a-heuristic (not a tokenizer).
 *   6. `shouldSkip(env)` re-entrancy on the `DXMSG_PREFLIGHT_ACTIVE` sentinel,
 *      for BOTH hooks.
 *
 * The settings file is parsed JSONC-tolerantly, mirroring
 * `claude-permissions-contract.test.js`'s `parseJsonc` (the Claude CLI accepts
 * JSONC). Node stdlib only; no shell-outs.
 */

"use strict";

// cspell:ignore wurd zzzxqword

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");
const { stripJsCommentsAndStrings } = require("../lib/source-stripping");
const { isPathOutsideDirectory } = require("../lib/path-classifier");

const guard = require("../hooks/preflight-before-push-guard");
const stop = require("../hooks/preflight-on-stop");
const preflight = require("../preflight");
const {
  ISOLATED_JEST_CACHE_ROOT,
  REPO_ROOT: RUN_MANAGED_JEST_REPO_ROOT
} = require("../run-managed-jest");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const SETTINGS_PATH = path.join(REPO_ROOT, ".claude", "settings.json");
const GUARD_REL = "scripts/hooks/preflight-before-push-guard.js";
const STOP_REL = "scripts/hooks/preflight-on-stop.js";

/**
 * Parse a possibly-JSONC file (strip `//` and block comments outside string
 * literals, then JSON.parse). Mirrors claude-permissions-contract.test.js.
 *
 * @param {string} text
 * @returns {unknown}
 */
function parseJsonc(text) {
  let out = "";
  let i = 0;
  let inString = false;
  let stringChar = "";
  while (i < text.length) {
    const c = text[i];
    const next = text[i + 1];
    if (inString) {
      if (c === "\\" && i + 1 < text.length) {
        out += c + next;
        i += 2;
        continue;
      }
      if (c === stringChar) {
        inString = false;
      }
      out += c;
      i += 1;
      continue;
    }
    if (c === '"' || c === "'") {
      inString = true;
      stringChar = c;
      out += c;
      i += 1;
      continue;
    }
    if (c === "/" && next === "/") {
      while (i < text.length && text[i] !== "\n") {
        i += 1;
      }
      continue;
    }
    if (c === "/" && next === "*") {
      i += 2;
      while (i < text.length && !(text[i] === "*" && text[i + 1] === "/")) {
        i += 1;
      }
      i += 2;
      continue;
    }
    out += c;
    i += 1;
  }
  return JSON.parse(out);
}

function runGit(args) {
  return childProcess.spawnSync("git", args, {
    cwd: REPO_ROOT,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });
}

/**
 * True when a repo-relative path is destined for git tracking: either already
 * in the index (`git ls-files` lists it) or present-and-not-ignored (`git
 * check-ignore` exits non-zero). New files that have not yet been `git add`-ed
 * are still "to be tracked" -- they are not gitignored -- so this accepts both
 * states. (The CI commit lands them in the index; this guard only proves they
 * will not silently slip through `.gitignore`.)
 *
 * @param {string} rel Repo-relative POSIX path.
 * @returns {boolean}
 */
function isTrackedOrWillBeTracked(rel) {
  const listed = runGit(["ls-files", "--", rel]);
  if (listed.status === 0 && listed.stdout.trim() === rel) {
    return true;
  }
  // check-ignore exits 0 when the path IS ignored; non-zero when it is not.
  const ignored = runGit(["check-ignore", "--", rel]);
  return ignored.status !== 0;
}

/**
 * Flatten the `command` strings of every hook entry under a hooks[event] array,
 * regardless of matcher shape.
 *
 * @param {Array} eventEntries hooks[event] array.
 * @returns {Array<{matcher: string|undefined, command: string}>}
 */
function flattenHookCommands(eventEntries) {
  const out = [];
  if (!Array.isArray(eventEntries)) {
    return out;
  }
  for (const entry of eventEntries) {
    const matcher = entry && entry.matcher;
    const hooks = entry && Array.isArray(entry.hooks) ? entry.hooks : [];
    for (const hook of hooks) {
      if (hook && typeof hook.command === "string") {
        out.push({ matcher, command: hook.command });
      }
    }
  }
  return out;
}

describe("change-aware preflight Claude hooks contract", () => {
  let raw;
  let parsed;

  beforeAll(() => {
    raw = fs.readFileSync(SETTINGS_PATH, "utf8");
    parsed = parseJsonc(raw);
  });

  // ---- .claude/settings.json shape -------------------------------------

  test(".claude/settings.json is tracked and parses to an object", () => {
    expect(isTrackedOrWillBeTracked(".claude/settings.json")).toBe(true);
    expect(parsed).toEqual(expect.any(Object));
    expect(parsed.hooks).toEqual(expect.any(Object));
  });

  test("PreToolUse has a Bash matcher entry that invokes the push-guard via $CLAUDE_PROJECT_DIR", () => {
    const commands = flattenHookCommands(parsed.hooks.PreToolUse);
    const guardEntry = commands.find((c) => c.command.includes(GUARD_REL));
    expect(guardEntry).toBeDefined();
    expect(guardEntry.matcher).toBe("Bash");
    expect(guardEntry.command).toContain("$CLAUDE_PROJECT_DIR");
    expect(guardEntry.command.startsWith("node ")).toBe(true);
  });

  test("Stop has an entry that invokes the advisory stop hook via $CLAUDE_PROJECT_DIR", () => {
    const commands = flattenHookCommands(parsed.hooks.Stop);
    const stopEntry = commands.find((c) => c.command.includes(STOP_REL));
    expect(stopEntry).toBeDefined();
    expect(stopEntry.command).toContain("$CLAUDE_PROJECT_DIR");
    expect(stopEntry.command.startsWith("node ")).toBe(true);
  });

  test("the existing PostToolUse yaml-line-length-guard entry is preserved", () => {
    const commands = flattenHookCommands(parsed.hooks.PostToolUse);
    const yamlEntry = commands.find((c) =>
      c.command.includes("scripts/hooks/yaml-line-length-guard.js")
    );
    expect(yamlEntry).toBeDefined();
    expect(yamlEntry.matcher).toBe("Edit|Write|MultiEdit");
    expect(yamlEntry.command).toContain("$CLAUDE_PROJECT_DIR");
  });

  test("PostToolUse has a generalized post-edit-validate guard entry (Edit|Write|MultiEdit)", () => {
    const commands = flattenHookCommands(parsed.hooks.PostToolUse);
    const guardEntry = commands.find((c) =>
      c.command.includes("scripts/hooks/post-edit-validate-guard.js")
    );
    expect(guardEntry).toBeDefined();
    expect(guardEntry.matcher).toBe("Edit|Write|MultiEdit");
    expect(guardEntry.command).toContain("$CLAUDE_PROJECT_DIR");
    expect(guardEntry.command.startsWith("node ")).toBe(true);
  });

  test("settings.json has NO permissions key (auto-approval stays out of tracked settings)", () => {
    // context.md:26 -- never commit settings that auto-approve chat-invoked
    // terminal commands. A committed `permissions` block must fail here. The
    // permissions contract lives only in the gitignored settings.local.json.
    expect(parsed.permissions).toBeUndefined();
    expect(Object.prototype.hasOwnProperty.call(parsed, "permissions")).toBe(false);
  });

  test("both hook scripts exist on disk and are tracked (or not gitignored, pre-commit)", () => {
    for (const rel of [GUARD_REL, STOP_REL]) {
      expect(fs.existsSync(path.join(REPO_ROOT, rel))).toBe(true);
      // Accepts the "added but not yet committed" working-tree state: the file
      // must not be gitignored, so it will be tracked once committed.
      expect(isTrackedOrWillBeTracked(rel)).toBe(true);
    }
  });

  // ---- buildDecision (PreToolUse JSON shape) ---------------------------

  test("buildDecision: ok -> permissionDecision allow with PreToolUse event name", () => {
    const out = guard.buildDecision({ kind: "ok", failures: [], policyFailures: [], warnings: [] });
    expect(out.hookSpecificOutput.hookEventName).toBe("PreToolUse");
    expect(out.hookSpecificOutput.permissionDecision).toBe("allow");
  });

  test("buildDecision: checks-failed -> deny with a reason naming the failing hook ids", () => {
    const out = guard.buildDecision({
      kind: "checks-failed",
      failures: ["cspell", "prettier"],
      policyFailures: [],
      warnings: []
    });
    expect(out.hookSpecificOutput.hookEventName).toBe("PreToolUse");
    expect(out.hookSpecificOutput.permissionDecision).toBe("deny");
    expect(out.hookSpecificOutput.permissionDecisionReason).toContain("cspell");
    expect(out.hookSpecificOutput.permissionDecisionReason).toContain("prettier");
    expect(out.hookSpecificOutput.permissionDecisionReason).toContain("npm run preflight");
  });

  test("buildDecision: infra-unavailable (no policyFailures) -> allow with a warning reason", () => {
    const out = guard.buildDecision({
      kind: "infra-unavailable",
      failures: [],
      policyFailures: [],
      warnings: ["yamllint skipped"]
    });
    expect(out.hookSpecificOutput.permissionDecision).toBe("allow");
    expect(out.hookSpecificOutput.permissionDecisionReason).toMatch(/infrastructure/i);
    expect(out.hookSpecificOutput.permissionDecisionReason).toContain("yamllint skipped");
  });

  test("buildDecision: infra-unavailable WITH policyFailures -> deny (policy never fails open)", () => {
    const out = guard.buildDecision({
      kind: "infra-unavailable",
      failures: [],
      policyFailures: ["validate-untracked-policy"],
      warnings: []
    });
    expect(out.hookSpecificOutput.permissionDecision).toBe("deny");
    expect(out.hookSpecificOutput.permissionDecisionReason).toContain("validate-untracked-policy");
  });

  test("buildDecision tolerates a malformed status by allowing (fail-open on infra/garbage)", () => {
    expect(guard.buildDecision(undefined).hookSpecificOutput.permissionDecision).toBe("allow");
    expect(guard.buildDecision({}).hookSpecificOutput.permissionDecision).toBe("allow");
  });

  test("filterCspellFiles mirrors the native cspell-covered extension set", () => {
    expect(
      guard.filterCspellFiles([
        "README.md",
        "docs/guide.markdown",
        "Runtime/Foo.cs",
        "package.json",
        ".github/workflows/ci.yml",
        ".yamllint.yaml",
        "scripts/hook.ps1",
        "scripts/tool.js",
        "Editor/Analyzers/x.dll"
      ])
    ).toEqual([
      "README.md",
      "docs/guide.markdown",
      "Runtime/Foo.cs",
      "package.json",
      ".github/workflows/ci.yml",
      ".yamllint.yaml",
      "scripts/hook.ps1",
      "scripts/tool.js"
    ]);
  });

  test("runChangedCspellGuard blocks on any changed-file cspell failure before full preflight", () => {
    let capturedFileList = "";
    const spawnFn = jest.fn((_command, args) => {
      capturedFileList = fs.readFileSync(args[args.indexOf("--file-list") + 1], "utf8");
      return {
        status: 1,
        stdout: "scripts/x.js:1:2 - Unknown word (wurd)",
        stderr: ""
      };
    });
    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      env: {},
      computeChangeSetFn: () => ({ files: ["scripts/x.js", "Editor/Analyzers/x.dll"] }),
      statSyncFn: () => ({ isFile: () => true }),
      spawnFn
    });

    expect(result.kind).toBe("checks-failed");
    expect(result.files).toEqual(["scripts/x.js"]);
    expect(result.detail).toContain("Unknown word");
    const [, args, options] = spawnFn.mock.calls[0];
    expect(args).toEqual([
      "scripts/run-managed-cspell.js",
      "--no-progress",
      "--no-summary",
      "--no-must-find-files",
      "--file-list",
      expect.any(String)
    ]);
    expect(args).not.toContain("scripts/x.js");
    expect(capturedFileList).toContain(path.join(REPO_ROOT, "scripts", "x.js"));
    expect(options.env.DXMSG_PREFLIGHT_ACTIVE).toBe("1");
    expect(options.timeout).toBeGreaterThan(0);
  });

  test("runChangedCspellGuard checks committed HEAD content when the worktree copy is missing", () => {
    const spawnFn = jest.fn((_command, args, options) => {
      expect(args).toContain("stdin://scripts/missing.js");
      expect(options.input).toBe("const zzzxqword = 1;\n");
      expect(options.env.DXMSG_HOOK_SKIP_INTEGRITY).toBeUndefined();
      return {
        status: 1,
        stdout: "scripts/missing.js:1:7 - Unknown word (zzzxqword)",
        stderr: ""
      };
    });
    const gitSpawnFn = jest.fn((_command, args) => {
      expect(args).toEqual(["show", "HEAD:scripts/missing.js"]);
      return { status: 0, stdout: "const zzzxqword = 1;\n", stderr: "" };
    });

    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      env: {},
      computeChangeSetFn: () => ({
        files: ["scripts/missing.js"],
        mergeBase: "base",
        sources: {
          committed: ["scripts/missing.js"],
          staged: [],
          unstaged: [],
          untracked: []
        }
      }),
      statSyncFn: () => {
        throw new Error("ENOENT");
      },
      gitSpawnFn,
      spawnFn
    });

    expect(result.kind).toBe("checks-failed");
    expect(result.files).toEqual(["scripts/missing.js"]);
    expect(result.detail).toContain("Unknown word");
    expect(spawnFn).toHaveBeenCalledTimes(1);
  });

  test("runChangedCspellGuard blocks when required committed HEAD content cannot be read", () => {
    const spawnFn = jest.fn();
    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      computeChangeSetFn: () => ({
        files: ["scripts/missing.js"],
        mergeBase: "base",
        sources: {
          committed: ["scripts/missing.js"],
          staged: [],
          unstaged: [],
          untracked: []
        }
      }),
      statSyncFn: () => {
        throw new Error("ENOENT");
      },
      gitSpawnFn: () => ({ status: 128, stdout: "", stderr: "fatal: not found" }),
      spawnFn
    });

    expect(result.kind).toBe("checks-failed");
    expect(result.files).toEqual(["scripts/missing.js"]);
    expect(result.detail).toContain("could not read committed HEAD content");
    expect(spawnFn).not.toHaveBeenCalled();
  });

  test("runChangedCspellGuard checks both worktree and HEAD when local edits can mask pushed content", () => {
    let capturedFileList = "";
    const spawnFn = jest.fn((_command, args, options) => {
      if (args.includes("--file-list")) {
        capturedFileList = fs.readFileSync(args[args.indexOf("--file-list") + 1], "utf8");
        return { status: 0, stdout: "", stderr: "" };
      }

      expect(args).toContain("stdin://scripts/x.js");
      expect(options.input).toBe("const pushed = 'zzzxqword';\n");
      expect(options.env.DXMSG_HOOK_SKIP_INTEGRITY).toBe("1");
      return {
        status: 1,
        stdout: "scripts/x.js:1:17 - Unknown word (zzzxqword)",
        stderr: ""
      };
    });

    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      env: {},
      computeChangeSetFn: () => ({
        files: ["scripts/x.js"],
        mergeBase: "base",
        sources: {
          committed: ["scripts/x.js"],
          staged: [],
          unstaged: ["scripts/x.js"],
          untracked: []
        }
      }),
      statSyncFn: () => ({ isFile: () => true }),
      gitSpawnFn: () => ({ status: 0, stdout: "const pushed = 'zzzxqword';\n", stderr: "" }),
      spawnFn
    });

    expect(result.kind).toBe("checks-failed");
    expect(result.files).toEqual(["scripts/x.js"]);
    expect(capturedFileList).toContain(path.join(REPO_ROOT, "scripts", "x.js"));
    expect(spawnFn).toHaveBeenCalledTimes(2);
  });

  test("runChangedCspellGuard preserves cspell ignore semantics for virtual HEAD content", () => {
    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      computeChangeSetFn: () => ({
        files: ["Samples~/Example/readme.md"],
        mergeBase: "base",
        sources: {
          committed: ["Samples~/Example/readme.md"],
          staged: [],
          unstaged: [],
          untracked: []
        }
      }),
      statSyncFn: () => {
        throw new Error("ENOENT");
      },
      gitSpawnFn: () => ({ status: 0, stdout: "zzzxqword sample\n", stderr: "" })
    });

    expect(result).toEqual({
      kind: "ok",
      files: ["Samples~/Example/readme.md"],
      detail: ""
    });
  });

  test("runChangedCspellGuard falls back to tracked cspell files when no merge-base resolves", () => {
    let capturedFileList = "";
    const spawnFn = jest.fn((_command, args) => {
      capturedFileList = fs.readFileSync(args[args.indexOf("--file-list") + 1], "utf8");
      return { status: 0, stdout: "", stderr: "" };
    });
    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      env: {},
      computeChangeSetFn: () => ({ files: [], mergeBase: null }),
      collectTrackedFilesFn: () => ["README.md", "Editor/Analyzers/x.dll", "scripts/committed.js"],
      statSyncFn: () => ({ isFile: () => true }),
      spawnFn
    });

    expect(result.kind).toBe("ok");
    expect(result.files).toEqual(["README.md", "scripts/committed.js"]);
    const [, args] = spawnFn.mock.calls[0];
    expect(args).toContain("--file-list");
    expect(capturedFileList).toContain(path.join(REPO_ROOT, "README.md"));
    expect(capturedFileList).toContain(path.join(REPO_ROOT, "scripts", "committed.js"));
  });

  test("runChangedCspellGuard drops missing tracked fallback files before cspell", () => {
    let capturedFileList = "";
    const spawnFn = jest.fn((_command, args) => {
      capturedFileList = fs.readFileSync(args[args.indexOf("--file-list") + 1], "utf8");
      return { status: 0, stdout: "", stderr: "" };
    });
    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      computeChangeSetFn: () => ({ files: [], mergeBase: null }),
      collectTrackedFilesFn: () => ["README.md", "scripts/missing.js"],
      statSyncFn: (abs) => {
        if (abs.endsWith(path.join("scripts", "missing.js"))) {
          throw new Error("ENOENT");
        }
        return { isFile: () => true };
      },
      gitSpawnFn: () => ({ status: 128, stdout: "", stderr: "fatal: path does not exist" }),
      spawnFn
    });

    expect(result.kind).toBe("ok");
    expect(result.files).toEqual(["README.md"]);
    expect(capturedFileList).toContain(path.join(REPO_ROOT, "README.md"));
    expect(capturedFileList).not.toContain("missing.js");
  });

  test("runChangedCspellGuard cleanup failures do not mask a successful cspell run", () => {
    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      computeChangeSetFn: () => ({ files: ["scripts/x.js"], mergeBase: "base" }),
      statSyncFn: () => ({ isFile: () => true }),
      spawnFn: () => ({ status: 0, stdout: "", stderr: "" }),
      mkdtempSyncFn: () => path.join(REPO_ROOT, "dxm-prepush-cleanup-fixture"),
      writeFileSyncFn: () => {},
      rmSyncFn: () => {
        throw new Error("EPERM");
      }
    });
    expect(result.kind).toBe("ok");
  });

  test("runChangedCspellGuard reports file-list creation failures with structured output", () => {
    let removed = false;
    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      computeChangeSetFn: () => ({ files: ["scripts/x.js"], mergeBase: "base" }),
      statSyncFn: () => ({ isFile: () => true }),
      spawnFn: jest.fn(),
      mkdtempSyncFn: () => path.join(REPO_ROOT, "dxm-cspell-write-failure"),
      writeFileSyncFn: () => {
        throw new Error("disk full");
      },
      rmSyncFn: () => {
        removed = true;
      }
    });
    expect(result.kind).toBe("checks-failed");
    expect(result.detail).toContain("could not create file list");
    expect(removed).toBe(true);
  });

  test("runChangedCspellGuard real --file-list path succeeds for an existing clean file", () => {
    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      computeChangeSetFn: () => ({
        files: ["scripts/hooks/post-edit-validate-guard.js"],
        mergeBase: "base"
      })
    });
    expect(result).toEqual({
      kind: "ok",
      files: ["scripts/hooks/post-edit-validate-guard.js"],
      detail: ""
    });
  });

  test("runChangedCspellGuard skips the managed runner when no changed cspell files exist", () => {
    const spawnFn = jest.fn();
    const result = guard.runChangedCspellGuard(REPO_ROOT, {
      computeChangeSetFn: () => ({ files: ["Editor/Analyzers/x.dll"] }),
      spawnFn
    });
    expect(result).toEqual({ kind: "ok", files: [], detail: "" });
    expect(spawnFn).not.toHaveBeenCalled();
  });

  test("buildCspellDecision denies and includes changed file attribution", () => {
    const out = guard.buildCspellDecision({
      files: ["scripts/x.js"],
      detail: "scripts/x.js:1:2 - Unknown word (wurd)"
    });
    expect(out.hookSpecificOutput.hookEventName).toBe("PreToolUse");
    expect(out.hookSpecificOutput.permissionDecision).toBe("deny");
    expect(out.hookSpecificOutput.permissionDecisionReason).toContain("scripts/x.js");
    expect(out.hookSpecificOutput.permissionDecisionReason).toContain("Unknown word");
  });

  // ---- buildAdvisory (Stop, advisory only) -----------------------------

  test("buildAdvisory: checks-failed -> a STRING naming the hooks + remediation", () => {
    const msg = stop.buildAdvisory({
      kind: "checks-failed",
      failures: ["cspell"],
      policyFailures: []
    });
    expect(typeof msg).toBe("string");
    expect(msg).toContain("cspell");
    expect(msg).toContain("npm run preflight");
  });

  test("buildAdvisory: policyFailures under infra-unavailable still advises", () => {
    const msg = stop.buildAdvisory({
      kind: "infra-unavailable",
      failures: [],
      policyFailures: ["validate-untracked-policy"]
    });
    expect(typeof msg).toBe("string");
    expect(msg).toContain("validate-untracked-policy");
  });

  test("buildAdvisory: ok and plain infra-unavailable -> null (silent)", () => {
    expect(stop.buildAdvisory({ kind: "ok" })).toBeNull();
    expect(stop.buildAdvisory({ kind: "infra-unavailable", policyFailures: [] })).toBeNull();
  });

  test("the Stop hook source NEVER emits decision:block (advisory only, owner override)", () => {
    // Structural guarantee: the advisory Stop hook must not emit a blocking
    // decision. Scan the CODE only (comments/strings stripped) so the docstring
    // that NAMES the forbidden shape does not self-trip. A future edit that adds
    // a real `decision: "block"` in code must fail here.
    const src = fs.readFileSync(path.join(REPO_ROOT, STOP_REL), "utf8");
    const code = stripJsCommentsAndStrings(src);
    expect(code).not.toMatch(/decision\s*:/);
  });

  // ---- git operation heuristics ----------------------------------------

  test.each([
    ["git push"],
    ["git -C /some/dir push"],
    ["cd repo && git push origin HEAD"],
    ["(cd repo && git push origin HEAD)"],
    ["env FOO=1 git push origin HEAD"],
    ["time git push origin HEAD"],
    ["FOO=1 git push"],
    ["git push --force-with-lease"],
    ["git   push    --tags"]
  ])("commandLooksLikeGitPush is true for a probable push: %s", (cmd) => {
    expect(guard.commandLooksLikeGitPush(cmd)).toBe(true);
  });

  test.each([
    ["git status"],
    ["git commit -m wip"],
    ['git commit -m "fix push guard"'],
    ["git commit -m fix push guard"],
    ["git commit -m x && echo git push"],
    ["echo git push"],
    ["echo done"],
    ["npm run push:docs"],
    ["git pushd-not-a-thing"],
    [""],
    [undefined]
  ])("commandLooksLikeGitPush is false for a non-push: %s", (cmd) => {
    expect(guard.commandLooksLikeGitPush(cmd)).toBe(false);
  });

  test.each([
    ["git commit -m wip"],
    ['git commit -m "fix push guard"'],
    ["git commit -m fix push guard"],
    ["git commit -m x && echo git push"],
    ["git -C /some/dir commit --amend"],
    ["cd repo && git commit --allow-empty"],
    ["(cd repo && git commit --allow-empty)"],
    ["env FOO=1 git commit --allow-empty"],
    ["time git commit --allow-empty"],
    ["FOO=1 git commit"]
  ])("commandLooksLikeGitCommit is true for a probable commit: %s", (cmd) => {
    expect(guard.commandLooksLikeGitCommit(cmd)).toBe(true);
    expect(guard.resolveGuardOperation(cmd)).toBe("commit");
  });

  test.each([
    ["git status"],
    ["echo done"],
    ["npm run commitlint"],
    ["git commitment-not-a-thing"],
    [""],
    [undefined]
  ])("commandLooksLikeGitCommit is false for a non-commit: %s", (cmd) => {
    expect(guard.commandLooksLikeGitCommit(cmd)).toBe(false);
  });

  test("resolveGuardOperation prefers push when both git boundaries appear", () => {
    expect(guard.resolveGuardOperation("git commit -m x && git push")).toBe("push");
    expect(guard.resolveGuardOperation("git commit -m x && echo git push")).toBe("commit");
    expect(guard.resolveGuardOperation("git status")).toBeNull();
  });

  test("git operation detection is documented as heuristic, not a tokenizer", () => {
    // The over-trigger-is-safe property is load-bearing (preflight is read-only
    // and idempotent). Pin the docstring intent so a future "tighten it into a
    // real parser" refactor is a conscious choice, not an accident.
    const src = fs.readFileSync(path.join(REPO_ROOT, GUARD_REL), "utf8");
    expect(src).toMatch(/HEURISTICS, NOT tokenizers/);
  });

  // ---- shouldSkip re-entrancy ------------------------------------------

  test("shouldSkip honors the DXMSG_PREFLIGHT_ACTIVE sentinel for both hooks", () => {
    expect(guard.shouldSkip({ DXMSG_PREFLIGHT_ACTIVE: "1" })).toBe(true);
    expect(guard.shouldSkip({ DXMSG_PREFLIGHT_ACTIVE: "0" })).toBe(false);
    expect(guard.shouldSkip({})).toBe(false);
    expect(stop.shouldSkip({ DXMSG_PREFLIGHT_ACTIVE: "1" })).toBe(true);
    expect(stop.shouldSkip({})).toBe(false);
  });

  test("the guard run() exits 0 and emits nothing for a non-Bash tool", () => {
    const writes = [];
    const original = process.stdout.write;
    process.stdout.write = (chunk) => {
      writes.push(String(chunk));
      return true;
    };
    try {
      const code = guard.run(JSON.stringify({ tool_name: "Edit", tool_input: {} }), {
        env: {},
        repoRoot: REPO_ROOT
      });
      expect(code).toBe(0);
      expect(writes.join("")).toBe("");
    } finally {
      process.stdout.write = original;
    }
  });

  test("the guard run() skips (no preflight spawn) under the re-entrancy sentinel", () => {
    const spawnFn = jest.fn();
    const code = guard.run(
      JSON.stringify({ tool_name: "Bash", tool_input: { command: "git push" } }),
      {
        env: { DXMSG_PREFLIGHT_ACTIVE: "1" },
        repoRoot: REPO_ROOT,
        spawnFn
      }
    );
    expect(code).toBe(0);
    expect(spawnFn).not.toHaveBeenCalled();
  });

  test("the guard run() denies a push when injected preflight reports checks-failed", () => {
    const writes = [];
    const original = process.stdout.write;
    process.stdout.write = (chunk) => {
      writes.push(String(chunk));
      return true;
    };
    const spawnFn = jest.fn(() => ({
      status: 1,
      stdout: JSON.stringify({
        status: { kind: "checks-failed", failures: ["cspell"], policyFailures: [], warnings: [] }
      }),
      stderr: ""
    }));
    try {
      const code = guard.run(
        JSON.stringify({ tool_name: "Bash", tool_input: { command: "git push origin HEAD" } }),
        { env: {}, repoRoot: REPO_ROOT, spawnFn, computeChangeSetFn: () => ({ files: [] }) }
      );
      expect(code).toBe(0);
      // The guard passes --scope=full --profile=guard --no-recover and sets the
      // sentinel in the child env.
      const [, args, options] = spawnFn.mock.calls[0];
      expect(args).toContain("--scope=full");
      expect(args).toContain("--profile=guard");
      expect(args).toContain("--no-recover");
      expect(options.env.DXMSG_PREFLIGHT_ACTIVE).toBe("1");

      const emitted = JSON.parse(writes.join(""));
      expect(emitted.hookSpecificOutput.permissionDecision).toBe("deny");
      expect(emitted.hookSpecificOutput.permissionDecisionReason).toContain("cspell");
    } finally {
      process.stdout.write = original;
    }
  });

  test("the guard run() scopes probable git commits to the pre-commit stage", () => {
    const writes = [];
    const original = process.stdout.write;
    process.stdout.write = (chunk) => {
      writes.push(String(chunk));
      return true;
    };
    const spawnFn = jest.fn(() => ({
      status: 0,
      stdout: JSON.stringify({
        status: { kind: "ok", failures: [], policyFailures: [], warnings: [] }
      }),
      stderr: ""
    }));
    const writeHookValidationStampFn = jest.fn();
    try {
      const code = guard.run(
        JSON.stringify({ tool_name: "Bash", tool_input: { command: "git commit -m test" } }),
        {
          env: {},
          repoRoot: REPO_ROOT,
          spawnFn,
          computeChangeSetFn: () => ({ files: [] }),
          writeHookValidationStampFn
        }
      );
      expect(code).toBe(0);
      const [, args, options] = spawnFn.mock.calls[0];
      expect(args).toContain("--scope=full");
      expect(args).toContain("--profile=guard");
      expect(args).toContain("--stage=pre-commit");
      expect(options.env.DXMSG_PREFLIGHT_ACTIVE).toBe("1");
      const emitted = JSON.parse(writes.join(""));
      expect(emitted.hookSpecificOutput.permissionDecision).toBe("allow");
      expect(writeHookValidationStampFn).toHaveBeenCalledWith(REPO_ROOT, "pre-commit");
    } finally {
      process.stdout.write = original;
    }
  });

  test("the guard run() denies a push on changed-file cspell before spawning full preflight", () => {
    const writes = [];
    const original = process.stdout.write;
    process.stdout.write = (chunk) => {
      writes.push(String(chunk));
      return true;
    };
    const spawnFn = jest.fn(() => ({
      status: 1,
      stdout: "scripts/x.js:1:2 - Unknown word (wurd)",
      stderr: ""
    }));
    try {
      const code = guard.run(
        JSON.stringify({ tool_name: "Bash", tool_input: { command: "git push origin HEAD" } }),
        {
          env: {},
          repoRoot: REPO_ROOT,
          computeChangeSetFn: () => ({ files: ["scripts/x.js"] }),
          statSyncFn: () => ({ isFile: () => true }),
          spawnFn
        }
      );
      expect(code).toBe(0);
      expect(spawnFn).toHaveBeenCalledTimes(1);
      const emitted = JSON.parse(writes.join(""));
      expect(emitted.hookSpecificOutput.permissionDecision).toBe("deny");
      expect(emitted.hookSpecificOutput.permissionDecisionReason).toContain("changed-file cspell");
      expect(emitted.hookSpecificOutput.permissionDecisionReason).toContain("Unknown word");
    } finally {
      process.stdout.write = original;
    }
  });

  test("the guard passes a self-imposed timeout and fails OPEN (allow) when preflight times out", () => {
    const writes = [];
    const original = process.stdout.write;
    process.stdout.write = (chunk) => {
      writes.push(String(chunk));
      return true;
    };
    // A timed-out spawnSync kills the child: ETIMEDOUT error + SIGTERM signal +
    // empty stdout. Degradation must be deterministic (not reliant on the
    // Claude Code framework's version-specific kill behavior).
    const spawnFn = jest.fn(() => ({
      error: Object.assign(new Error("spawnSync node ETIMEDOUT"), { code: "ETIMEDOUT" }),
      signal: "SIGTERM",
      status: null,
      stdout: "",
      stderr: ""
    }));
    try {
      const code = guard.run(
        JSON.stringify({ tool_name: "Bash", tool_input: { command: "git push" } }),
        { env: {}, repoRoot: REPO_ROOT, spawnFn, computeChangeSetFn: () => ({ files: [] }) }
      );
      expect(code).toBe(0);
      const [, , options] = spawnFn.mock.calls[0];
      expect(options.timeout).toBeGreaterThan(0);
      const emitted = JSON.parse(writes.join(""));
      // A timeout is an infra condition, not a check failure -> allow; the
      // native pre-push hook remains the real local gate, while exhaustive
      // parity stays in CI/manual preflight.
      expect(emitted.hookSpecificOutput.permissionDecision).toBe("allow");
    } finally {
      process.stdout.write = original;
    }
  });

  test("the stop hook passes a self-imposed timeout and stays SILENT (exit 0) when preflight times out", () => {
    const writes = [];
    const original = process.stdout.write;
    process.stdout.write = (chunk) => {
      writes.push(String(chunk));
      return true;
    };
    const spawnFn = jest.fn(() => ({
      error: Object.assign(new Error("spawnSync node ETIMEDOUT"), { code: "ETIMEDOUT" }),
      signal: "SIGTERM",
      status: null,
      stdout: "",
      stderr: ""
    }));
    try {
      const code = stop.run(JSON.stringify({ hook_event_name: "Stop" }), {
        env: {},
        repoRoot: REPO_ROOT,
        spawnFn
      });
      expect(code).toBe(0);
      const [, , options] = spawnFn.mock.calls[0];
      expect(options.timeout).toBeGreaterThan(0);
      // Advisory hook: an infra timeout emits no systemMessage and never blocks.
      expect(writes.join("")).toBe("");
    } finally {
      process.stdout.write = original;
    }
  });
});

describe("preflight.runRecovery regenerable-cache heal (push-guard --no-recover gap)", () => {
  test("invokes the regenerable-cache heal EVEN when options.recover === false (guard path)", () => {
    // The PreToolUse push-guard spawns preflight with --no-recover, which skips
    // the expensive node_modules npm-ci recovery. The regenerable-cache heal
    // MUST still run (it is placed OUTSIDE the recover gate) or the guard would
    // never auto-clear the corrupt isolated cache before the native hook fires.
    const healSpy = jest.fn(() => ({ healed: false, perEntry: [] }));
    const repairSpy = jest.fn(() => ({ status: 0 }));
    const ensureSpy = jest.fn(() => ({ ok: true }));

    const result = preflight.runRecovery(
      { recover: false },
      {
        repairNodeToolingFn: repairSpy,
        ensurePreCommitFn: ensureSpy,
        healRegenerableCachesFn: healSpy,
        logFn: () => {},
        env: {}
      }
    );

    // The heal ran; the expensive node_modules repair did NOT (recover:false).
    expect(healSpy).toHaveBeenCalledTimes(1);
    expect(repairSpy).not.toHaveBeenCalled();
    // A heal call must not add an infraReason or change the recovery shape.
    expect(result.infraReasons).toEqual([]);
    expect(result.integrity).toBeNull();
  });

  test("also invokes the heal on the recover path (native hook / Stop hook)", () => {
    const healSpy = jest.fn(() => ({ healed: false, perEntry: [] }));
    const repairSpy = jest.fn(() => ({ status: 0 }));
    const ensureSpy = jest.fn(() => ({ ok: true }));

    preflight.runRecovery(
      { recover: true },
      {
        repairNodeToolingFn: repairSpy,
        ensurePreCommitFn: ensureSpy,
        healRegenerableCachesFn: healSpy,
        logFn: () => {},
        env: {}
      }
    );

    expect(healSpy).toHaveBeenCalledTimes(1);
    expect(repairSpy).toHaveBeenCalledTimes(1);
  });

  test("a THROWING heal does NOT fail-close runRecovery: the call is wrapped best-effort", () => {
    // runRecovery wraps the healRegenerableCachesFn call in try/catch, so the
    // "never adds an infraReason / never changes preflight status" guarantee
    // holds EVEN IF a future caller passes a raw (non-orchestrator) healer that
    // throws -- not just the production orchestrator that catches per-entry. We
    // inject a THROWING fake and assert runRecovery neither propagates the throw
    // nor records an infraReason.
    const throwingHeal = jest.fn(() => {
      throw new Error("heal orchestrator blew up");
    });
    const warnings = [];
    let result;
    expect(() => {
      result = preflight.runRecovery(
        { recover: false },
        {
          repairNodeToolingFn: jest.fn(),
          ensurePreCommitFn: () => ({ ok: true }),
          healRegenerableCachesFn: throwingHeal,
          logFn: (m) => warnings.push(String(m)),
          env: {}
        }
      );
    }).not.toThrow();
    expect(throwingHeal).toHaveBeenCalledTimes(1);
    expect(result.infraReasons).toEqual([]);
    // The swallow is surfaced as a best-effort warning, not a status change.
    expect(warnings.some((m) => m.includes("heal orchestrator threw"))).toBe(true);
  });

  test("the regenerable cache root is OUTSIDE the repo (guard/Stop read-only-to-committed-files invariant)", () => {
    // Purging the cache mutates no tracked/committed file: the cache root lives
    // under os.tmpdir(), which is outside the repo tree.
    //
    // We route through the shared, cross-drive-safe `isPathOutsideDirectory`
    // helper rather than the bare `path.relative(repo, cache).startsWith("..")`
    // shortcut. On Windows where os.tmpdir() is on C:\ and the repo is on D:\,
    // `path.relative` returns an ABSOLUTE `C:\...` target (it cannot express a
    // cross-drive traversal), which does NOT start with ".." -- so the bare
    // shortcut would wrongly report the cache as INSIDE the repo and fail this
    // assertion even though the cache is on another drive entirely. The helper
    // covers Linux/macOS, Windows same-drive, and Windows cross-drive uniformly.
    expect(isPathOutsideDirectory(ISOLATED_JEST_CACHE_ROOT, RUN_MANAGED_JEST_REPO_ROOT)).toBe(true);
  });
});
