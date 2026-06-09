/**
 * @fileoverview Native Git hook bootstrap contract.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const childProcess = require("child_process");
const { makeTempDir, cleanupDir } = require("../lib/jest-fixtures");
const { installGitHooks, REQUIRED_NATIVE_HOOKS } = require("../install-git-hooks");
const {
  parseGitVersion,
  versionAtLeast,
  configureLocalGitPerformance
} = require("../configure-local-git-performance");
const { repairNodeTooling } = require("../repair-node-tooling");
const { ensurePreCommit, runPreCommit, PACKAGE_SPEC } = require("../ensure-pre-commit");
const {
  fingerprintGitState,
  hasValidHookValidationStamp,
  writeHookValidationStamp
} = require("../lib/hook-validation-stamp");

const REPO_ROOT = path.resolve(__dirname, "..", "..");
const PRE_COMMIT_HOOK = path.join(REPO_ROOT, "scripts", "hooks", "pre-commit");
const PRE_PUSH_HOOK = path.join(REPO_ROOT, "scripts", "hooks", "pre-push");
const POSTINSTALL = path.join(REPO_ROOT, "scripts", "postinstall.js");
const PACKAGE_JSON = path.join(REPO_ROOT, "package.json");

function stampSpawnFor(options) {
  const {
    stampPath,
    head = "abc123",
    indexTree = "tree-a",
    indexPath = path.join(path.dirname(stampPath), "index"),
    changelogDiff = "",
    changelogUntracked = "",
    trackedWorktreeRawDiff = "",
    untracked = ""
  } = options;
  return jest.fn((command, args) => {
    expect(command).toBe("git");
    const joined = args.join(" ");
    if (joined === "rev-parse --git-path dxmsg-pre-commit-stamp.json") {
      return { status: 0, stdout: `${stampPath}\n`, stderr: "" };
    }
    if (joined === "rev-parse --git-path dxmsg-pre-push-stamp.json") {
      return { status: 0, stdout: `${stampPath}\n`, stderr: "" };
    }
    if (joined === "rev-parse --verify HEAD") {
      return { status: 0, stdout: `${head}\n`, stderr: "" };
    }
    if (joined === "rev-parse --git-path index") {
      return { status: 0, stdout: `${indexPath}\n`, stderr: "" };
    }
    if (joined === "write-tree") {
      return { status: 0, stdout: `${indexTree}\n`, stderr: "" };
    }
    if (
      joined ===
      "ls-files --others --exclude-standard -z -- CHANGELOG.md package.json Runtime Editor SourceGenerators Samples~"
    ) {
      return { status: 0, stdout: changelogUntracked, stderr: "" };
    }
    if (
      joined ===
      "diff --binary --no-ext-diff -- CHANGELOG.md package.json Runtime Editor SourceGenerators Samples~"
    ) {
      return { status: 0, stdout: changelogDiff, stderr: "" };
    }
    if (joined === "ls-files --others --exclude-standard -z") {
      return { status: 0, stdout: untracked, stderr: "" };
    }
    if (joined === "diff-files --raw --abbrev=40 -z --") {
      return { status: 0, stdout: trackedWorktreeRawDiff, stderr: "" };
    }
    if (joined === "merge-base --is-ancestor base remote") {
      return { status: 0, stdout: "", stderr: "" };
    }
    if (joined === "merge-base --is-ancestor base head") {
      return { status: 0, stdout: "", stderr: "" };
    }
    if (joined === "merge-base --is-ancestor remote head") {
      return { status: 0, stdout: "", stderr: "" };
    }
    if (joined === "merge-base --is-ancestor base diverged") {
      return { status: 0, stdout: "", stderr: "" };
    }
    if (joined === "merge-base --is-ancestor diverged head") {
      return { status: 1, stdout: "", stderr: "" };
    }
    if (joined === "merge-base --is-ancestor base unrelated") {
      return { status: 1, stdout: "", stderr: "" };
    }
    return { status: 1, stdout: "", stderr: `unexpected git args: ${joined}` };
  });
}

function runGit(args, cwd) {
  return childProcess.spawnSync("git", args, {
    cwd,
    encoding: "utf8",
    stdio: ["ignore", "pipe", "pipe"]
  });
}

describe("native Git hooks", () => {
  test("pre-commit hook is a Node wrapper for the pre-commit framework stage", () => {
    expect(fs.existsSync(PRE_COMMIT_HOOK)).toBe(true);

    const content = fs.readFileSync(PRE_COMMIT_HOOK, "utf8");
    expect(content.startsWith("#!/usr/bin/env node\n")).toBe(true);
    expect(content).toContain("repair-node-tooling.js");
    expect(content).toContain("ensure-pre-commit.js");
    expect(content).toContain('"pre-commit"');
    expect(content).toContain('"--hook-stage"');
    expect(content).toContain("retrying once after auto-fixes");
    expect(content).toContain("failed without detected file changes; not retrying");
    expect(content).toContain("spawnPlatformCommandSync");
    expect(content).not.toMatch(/\b(?:bash|sh|pwsh|powershell)\b/);
    expect(content).not.toContain("shell: true");

    const repairIndex = content.indexOf("repair-node-tooling.js");
    const ensureIndex = content.indexOf("ensure-pre-commit.js");
    const frameworkIndex = content.indexOf('"--hook-stage"');
    expect(repairIndex).toBeGreaterThanOrEqual(0);
    expect(ensureIndex).toBeGreaterThan(repairIndex);
    expect(frameworkIndex).toBeGreaterThan(ensureIndex);
  });

  test("pre-push hook is a Node wrapper for the native range-aware runner", () => {
    expect(fs.existsSync(PRE_PUSH_HOOK)).toBe(true);

    const content = fs.readFileSync(PRE_PUSH_HOOK, "utf8");
    expect(content.startsWith("#!/usr/bin/env node\n")).toBe(true);
    expect(content).toContain("../run-native-prepush");
    expect(content).not.toContain("hasValidHookValidationStamp");
    expect(content).not.toContain("writeHookValidationStamp");
    expect(content).not.toContain("repair-node-tooling.js");
    expect(content).not.toContain("ensure-pre-commit.js");
    expect(content).not.toContain('"doctor"');
    expect(content).not.toContain("run-prepush-parallel.js");
    expect(content).not.toContain("preflight:pre-push");
    expect(content).not.toContain("--all-files");
    expect(content).not.toMatch(/\b(?:bash|sh|pwsh|powershell)\b/);
    expect(content).not.toContain("shell: true");
  });

  test("native range runner is local-fast and keeps exhaustive parity explicit", () => {
    const content = fs.readFileSync(
      path.join(REPO_ROOT, "scripts", "run-native-prepush.js"),
      "utf8"
    );

    expect(content).toContain("parsePrePushInput");
    expect(content).toContain("--stage=pre-push");
    expect(content).toContain("--profile=guard");
    expect(content).toContain("--range-from");
    expect(content).toContain("--range-to");
    expect(content).toContain("--no-worktree");
    expect(content).toContain('removeKeys: ["SKIP"]');
    expect(content).toContain("preflight:pre-push");
    expect(content).toContain("hasValidHookValidationStamp");
    expect(content).not.toContain("run-prepush-parallel.js");
    expect(content).not.toContain("writeHookValidationStamp");
    expect(content).not.toContain("--all-files");
  });

  test("native pre-push hook contract forbids old exhaustive local behavior", () => {
    const content = fs.readFileSync(PRE_PUSH_HOOK, "utf8");

    for (const forbidden of [
      "run-prepush-parallel.js",
      "npm run doctor",
      "--all-files",
      "writeHookValidationStamp"
    ]) {
      expect(content).not.toContain(forbidden);
    }
  });

  test("native hook executability is tracked in Git metadata", () => {
    for (const hookPath of REQUIRED_NATIVE_HOOKS.map((hook) => `scripts/hooks/${hook}`)) {
      const result = runGit(["ls-files", "--stage", "--", hookPath], REPO_ROOT);
      expect(result.status).toBe(0);
      expect(result.stdout.trim()).toMatch(/^100755\s/);
    }
  });

  test("native git-event hooks stay out of core.hooksPath to avoid checkout-time mutators", () => {
    for (const hook of ["post-checkout", "post-merge", "post-rewrite"]) {
      expect(REQUIRED_NATIVE_HOOKS).not.toContain(hook);
      expect(fs.existsSync(path.join(REPO_ROOT, "scripts", "hooks", hook))).toBe(false);
    }
  });

  test("pre-commit stamp fingerprint covers staged content and changelog-relevant local changes", () => {
    const temp = makeTempDir("hook-stamp");
    try {
      const stampFile = path.join(temp, "stamp.json");
      fs.mkdirSync(path.join(temp, "Runtime"), { recursive: true });
      fs.writeFileSync(path.join(temp, "Runtime", "scratch.txt"), "untracked-before", "utf8");
      writeHookValidationStamp(temp, "pre-commit", {
        spawnFn: stampSpawnFor({
          stampPath: stampFile,
          indexTree: "tree-a",
          changelogDiff: "diff-before",
          changelogUntracked: "Runtime/scratch.txt\0"
        })
      });

      expect(
        hasValidHookValidationStamp(temp, "pre-commit", {
          spawnFn: stampSpawnFor({
            stampPath: stampFile,
            indexTree: "tree-a",
            changelogDiff: "diff-before",
            changelogUntracked: "Runtime/scratch.txt\0"
          })
        }).valid
      ).toBe(true);
      expect(
        hasValidHookValidationStamp(temp, "pre-commit", {
          spawnFn: stampSpawnFor({
            stampPath: stampFile,
            indexTree: "tree-b",
            changelogDiff: "diff-before",
            changelogUntracked: "Runtime/scratch.txt\0"
          })
        }).valid
      ).toBe(false);
      expect(
        hasValidHookValidationStamp(temp, "pre-commit", {
          spawnFn: stampSpawnFor({
            stampPath: stampFile,
            indexTree: "tree-a",
            changelogDiff: "diff-after",
            changelogUntracked: "Runtime/scratch.txt\0"
          })
        }).valid
      ).toBe(false);
      expect(
        hasValidHookValidationStamp(temp, "pre-commit", {
          spawnFn: stampSpawnFor({
            stampPath: stampFile,
            indexTree: "tree-a",
            changelogDiff: "diff -- package.json changed",
            changelogUntracked: "Runtime/scratch.txt\0"
          })
        }).valid
      ).toBe(false);
      fs.writeFileSync(path.join(temp, "Runtime", "scratch.txt"), "untracked-after", "utf8");
      expect(
        hasValidHookValidationStamp(temp, "pre-commit", {
          spawnFn: stampSpawnFor({
            stampPath: stampFile,
            indexTree: "tree-a",
            changelogDiff: "diff-before",
            changelogUntracked: "Runtime/scratch.txt\0"
          })
        }).valid
      ).toBe(false);
    } finally {
      cleanupDir(temp);
    }
  });

  test("hook fingerprint changes when staged content changes but porcelain status is stable", () => {
    const temp = makeTempDir("hook-fingerprint");
    try {
      const stampFile = path.join(temp, "stamp.json");
      const first = fingerprintGitState(temp, {
        spawnFn: stampSpawnFor({ stampPath: stampFile, indexTree: "tree-a" })
      });
      const second = fingerprintGitState(temp, {
        spawnFn: stampSpawnFor({ stampPath: stampFile, indexTree: "tree-b" })
      });

      expect(first.indexTree).not.toBe(second.indexTree);
      expect(first).not.toEqual(second);
    } finally {
      cleanupDir(temp);
    }
  });

  test("pre-push stamp validates only the exact pushed range", () => {
    const temp = makeTempDir("hook-prepush-stamp");
    try {
      const stampFile = path.join(temp, "stamp.json");
      writeHookValidationStamp(temp, "pre-push", {
        validatedFrom: "base",
        validatedTo: "head",
        spawnFn: stampSpawnFor({ stampPath: stampFile })
      });

      expect(
        hasValidHookValidationStamp(temp, "pre-push", {
          rangeFrom: "base",
          rangeTo: "head",
          spawnFn: stampSpawnFor({ stampPath: stampFile })
        }).valid
      ).toBe(true);
      expect(
        hasValidHookValidationStamp(temp, "pre-push", {
          rangeFrom: "remote",
          rangeTo: "head",
          spawnFn: stampSpawnFor({ stampPath: stampFile })
        }).valid
      ).toBe(false);
      expect(
        hasValidHookValidationStamp(temp, "pre-push", {
          rangeFrom: "diverged",
          rangeTo: "head",
          spawnFn: stampSpawnFor({ stampPath: stampFile })
        }).valid
      ).toBe(false);
      expect(
        hasValidHookValidationStamp(temp, "pre-push", {
          rangeFrom: "unrelated",
          rangeTo: "head",
          spawnFn: stampSpawnFor({ stampPath: stampFile })
        }).valid
      ).toBe(false);
      expect(
        hasValidHookValidationStamp(temp, "pre-push", {
          rangeFrom: "base",
          rangeTo: "other-head",
          spawnFn: stampSpawnFor({ stampPath: stampFile })
        }).valid
      ).toBe(false);
    } finally {
      cleanupDir(temp);
    }
  });

  test("hook validation stamps reject unsupported hook names", () => {
    expect(
      hasValidHookValidationStamp(REPO_ROOT, "post-checkout", {
        spawnFn: jest.fn()
      })
    ).toEqual({ valid: false, reason: "unsupported-hook", filePath: undefined });
    expect(() =>
      writeHookValidationStamp(REPO_ROOT, "post-checkout", {
        spawnFn: jest.fn()
      })
    ).toThrow(/Unsupported hook validation stamp/);
  });

  test("preflight repairs node tooling before read-only validation", () => {
    const pkg = JSON.parse(fs.readFileSync(PACKAGE_JSON, "utf8"));
    const preflight = pkg.scripts["preflight:pre-commit"];

    expect(pkg.scripts["repair:node-tooling"]).toBe("node scripts/repair-node-tooling.js");
    expect(preflight).toContain("npm run repair:node-tooling");
    expect(preflight.indexOf("npm run repair:node-tooling")).toBeLessThan(
      preflight.indexOf("npm run validate:node-tooling")
    );
  });

  test("repair-node-tooling invokes the shared integrity gate with recovery enabled", () => {
    const runIntegrityGateWithRecoveryFn = jest.fn(() => ({ ok: true, didRecover: true }));
    const result = repairNodeTooling({
      env: {},
      repoRoot: REPO_ROOT,
      runIntegrityGateWithRecoveryFn,
      probeIntegrityFn: jest.fn(),
      probeIntegrityInSubprocessFn: jest.fn(),
      probeResolverHealthFn: jest.fn(),
      attemptNpmCiRecoveryFn: jest.fn(),
      getNpmMajorVersionFn: jest.fn(() => 11),
      printActionableRepairBannerFn: jest.fn(),
      warnFn: jest.fn()
    });

    expect(result.status).toBe(0);
    expect(runIntegrityGateWithRecoveryFn).toHaveBeenCalledWith(
      expect.objectContaining({
        repoRoot: REPO_ROOT,
        bypassCache: true,
        attemptNpmCiRecoveryFn: expect.any(Function),
        isAutoRepairAllowedFn: expect.any(Function)
      })
    );
  });

  test("repair-node-tooling status is INDEPENDENT of a throwing heal orchestrator (best-effort)", () => {
    // The heal is best-effort: a throwing healRegenerableCachesFn must NEVER
    // abort the bootstrap (the first native-pre-push step). It is wrapped in
    // try/catch so repairNodeTooling still returns the gate-derived status
    // (0 when the integrity gate is ok), matching the documented contract.
    const warnFn = jest.fn();
    const throwingHeal = jest.fn(() => {
      throw new Error("heal orchestrator blew up");
    });

    let result;
    expect(() => {
      result = repairNodeTooling({
        env: {},
        repoRoot: REPO_ROOT,
        runIntegrityGateWithRecoveryFn: jest.fn(() => ({ ok: true, didRecover: false })),
        probeIntegrityFn: jest.fn(),
        probeIntegrityInSubprocessFn: jest.fn(),
        probeResolverHealthFn: jest.fn(),
        attemptNpmCiRecoveryFn: jest.fn(),
        getNpmMajorVersionFn: jest.fn(() => 11),
        printActionableRepairBannerFn: jest.fn(),
        healRegenerableCachesFn: throwingHeal,
        warnFn
      });
    }).not.toThrow();

    expect(throwingHeal).toHaveBeenCalledTimes(1);
    expect(result.status).toBe(0); // gate ok -> 0, despite the heal throw
    expect(warnFn.mock.calls.some((c) => String(c[0]).includes("heal orchestrator threw"))).toBe(
      true
    );
  });

  test("DXMSG_HOOK_SKIP_INTEGRITY=1 STILL invokes the regenerable-cache heal (orthogonal opt-outs)", () => {
    // The integrity-gate bypass (DXMSG_HOOK_SKIP_INTEGRITY) and the
    // regenerable-cache heal opt-out (DXMSG_HOOK_NO_REGENERABLE_HEAL) are
    // ORTHOGONAL: skipping the expensive node_modules npm-ci probe must NOT
    // silently disable the cheap, safe tmpdir-cache heal. The heal runs BEFORE
    // the skip-integrity early return, so it fires even in skip mode. The
    // integrity gate itself must NOT run (it was skipped).
    const healFn = jest.fn(() => ({ healed: false, perEntry: [] }));
    const gateFn = jest.fn(() => ({ ok: true, didRecover: false }));
    const result = repairNodeTooling({
      env: { DXMSG_HOOK_SKIP_INTEGRITY: "1" },
      repoRoot: REPO_ROOT,
      runIntegrityGateWithRecoveryFn: gateFn,
      probeIntegrityFn: jest.fn(),
      probeIntegrityInSubprocessFn: jest.fn(),
      probeResolverHealthFn: jest.fn(),
      attemptNpmCiRecoveryFn: jest.fn(),
      getNpmMajorVersionFn: jest.fn(() => 11),
      printActionableRepairBannerFn: jest.fn(),
      healRegenerableCachesFn: healFn,
      warnFn: jest.fn()
    });

    expect(result.skipped).toBe(true); // integrity bootstrap was skipped
    expect(gateFn).not.toHaveBeenCalled(); // the expensive gate did NOT run
    expect(healFn).toHaveBeenCalledTimes(1); // but the heal STILL ran
    // The heal is gated only by its OWN opt-out: the call carries the env so
    // healRegenerableCaches can honor DXMSG_HOOK_NO_REGENERABLE_HEAL itself.
    expect(healFn).toHaveBeenCalledWith(
      expect.objectContaining({ env: { DXMSG_HOOK_SKIP_INTEGRITY: "1" } })
    );
  });

  test("ensure-pre-commit uses existing executable when it matches the pinned version", () => {
    const runCommandFn = jest.fn((command, args) => {
      if (command === "pre-commit" && args[0] === "--version") {
        return { status: 0, stdout: "pre-commit 4.6.0\n" };
      }
      return { status: 1, stdout: "", stderr: "" };
    });

    const result = ensurePreCommit({
      runCommandFn,
      logFn: jest.fn(),
      warnFn: jest.fn()
    });

    expect(result).toEqual({
      ok: true,
      invocation: {
        command: "pre-commit",
        argsPrefix: [],
        version: "pre-commit 4.6.0"
      },
      installed: false
    });
    expect(runCommandFn).toHaveBeenCalledWith("pre-commit", ["--version"]);
  });

  test("ensure-pre-commit ignores an existing executable with the wrong version", () => {
    const calls = [];
    const runCommandFn = jest.fn((command, args) => {
      calls.push([command, args]);
      if (command === "pre-commit" && args[0] === "--version") {
        return { status: 0, stdout: "pre-commit 3.5.0\n" };
      }
      if (command === "python" && args.join(" ") === "--version") {
        return { status: 0, stdout: "Python 3.12.0\n" };
      }
      if (command === "python" && args.join(" ") === "-m pre_commit --version") {
        const pipInstallAlreadyRan = calls.some((call) => call[1].includes(PACKAGE_SPEC));
        return pipInstallAlreadyRan
          ? { status: 0, stdout: "pre-commit 4.6.0\n" }
          : { status: 1, stdout: "pre-commit 3.5.0\n" };
      }
      if (command === "python" && args.includes("pip") && args.includes(PACKAGE_SPEC)) {
        return { status: 0 };
      }
      return { status: 1, stdout: "", stderr: "" };
    });

    const result = ensurePreCommit({
      runCommandFn,
      candidates: [{ command: "python", args: [] }],
      logFn: jest.fn(),
      warnFn: jest.fn()
    });

    expect(result.ok).toBe(true);
    expect(result.installed).toBe(true);
    expect(calls).toContainEqual([
      "python",
      ["-m", "pip", "install", "--disable-pip-version-check", "--user", PACKAGE_SPEC]
    ]);
  });

  test("ensure-pre-commit auto-installs pinned pre-commit when Python is available", () => {
    const calls = [];
    const runCommandFn = jest.fn((command, args) => {
      calls.push([command, args]);
      if (command === "pre-commit") {
        return { error: Object.assign(new Error("missing"), { code: "ENOENT" }) };
      }
      if (command === "python" && args.join(" ") === "--version") {
        return { status: 0, stdout: "Python 3.12.0\n" };
      }
      if (command === "python" && args.join(" ") === "-m pre_commit --version") {
        const pipInstallAlreadyRan = calls.some((call) => call[1].includes(PACKAGE_SPEC));
        return pipInstallAlreadyRan
          ? { status: 0, stdout: "pre-commit 4.6.0\n" }
          : { status: 1, stdout: "", stderr: "No module named pre_commit" };
      }
      if (command === "python" && args.includes("pip") && args.includes(PACKAGE_SPEC)) {
        return { status: 0 };
      }
      return { status: 1, stdout: "", stderr: "" };
    });

    const result = ensurePreCommit({
      runCommandFn,
      candidates: [{ command: "python", args: [] }],
      logFn: jest.fn(),
      warnFn: jest.fn()
    });

    expect(result).toEqual({
      ok: true,
      invocation: {
        command: "python",
        argsPrefix: ["-m", "pre_commit"],
        version: "pre-commit 4.6.0"
      },
      installed: true
    });
    expect(calls).toContainEqual([
      "python",
      ["-m", "pip", "install", "--disable-pip-version-check", "--user", PACKAGE_SPEC]
    ]);
  });

  test("runPreCommit invokes the resolved Python module when no executable is on PATH", () => {
    const runCommandFn = jest.fn(() => ({ status: 0 }));
    const status = runPreCommit(["run", "--hook-stage", "pre-commit"], {
      ensurePreCommitFn: () => ({
        ok: true,
        invocation: {
          command: "python",
          argsPrefix: ["-m", "pre_commit"],
          version: "pre-commit 4.6.0"
        }
      }),
      runCommandFn
    });

    expect(status).toBe(0);
    expect(runCommandFn).toHaveBeenCalledWith(
      "python",
      ["-m", "pre_commit", "run", "--hook-stage", "pre-commit"],
      { stdio: "inherit", encoding: undefined }
    );
  });

  test("postinstall attempts native hook installation without making npm install fatal", () => {
    const content = fs.readFileSync(POSTINSTALL, "utf8");

    expect(content).toContain("install-git-hooks.js");
    expect(content).toContain("configure-local-git-performance.js");
    expect(content).toContain("runNonFatal");
    expect(content).toContain("process.exit(0)");
  });

  test("local Git performance bootstrap enables supported local config", () => {
    const calls = [];
    const runGitFn = jest.fn((args) => {
      calls.push(args.slice());
      const joined = args.join(" ");
      if (joined === "rev-parse --show-toplevel") {
        return { status: 0, stdout: "/repo\n" };
      }
      if (joined === "update-index --test-untracked-cache") {
        return { status: 0, stdout: "" };
      }
      if (joined === "--version") {
        return { status: 0, stdout: "git version 2.44.0\n" };
      }
      if (joined === "config --local core.untrackedCache true") {
        return { status: 0, stdout: "" };
      }
      if (joined === "config --local core.fsmonitor true") {
        return { status: 0, stdout: "" };
      }
      return { status: 1, stdout: "", stderr: joined };
    });

    const result = configureLocalGitPerformance({
      cwd: "/repo",
      platform: "darwin",
      runGitFn,
      log: jest.fn(),
      warn: jest.fn()
    });

    expect(result.ok).toBe(true);
    expect(result.changed).toEqual(["core.untrackedCache", "core.fsmonitor"]);
    expect(calls).toContainEqual(["config", "--local", "core.untrackedCache", "true"]);
    expect(calls).toContainEqual(["config", "--local", "core.fsmonitor", "true"]);
  });

  test("local Git performance bootstrap skips unsupported fsmonitor platforms", () => {
    const runGitFn = jest.fn((args) => {
      const joined = args.join(" ");
      if (joined === "rev-parse --show-toplevel") {
        return { status: 0, stdout: "/repo\n" };
      }
      if (joined === "update-index --test-untracked-cache") {
        return { status: 1, stdout: "" };
      }
      return { status: 1, stdout: "", stderr: joined };
    });

    const result = configureLocalGitPerformance({
      cwd: "/repo",
      platform: "linux",
      runGitFn,
      log: jest.fn(),
      warn: jest.fn()
    });

    expect(result.ok).toBe(true);
    expect(result.changed).toEqual([]);
    expect(runGitFn.mock.calls.some((call) => call[0][0] === "--version")).toBe(false);
  });

  test("local Git performance bootstrap preserves explicit local config values", () => {
    const calls = [];
    const runGitFn = jest.fn((args) => {
      calls.push(args.slice());
      const joined = args.join(" ");
      if (joined === "rev-parse --show-toplevel") {
        return { status: 0, stdout: "/repo\n" };
      }
      if (joined === "update-index --test-untracked-cache") {
        return { status: 0, stdout: "" };
      }
      if (joined === "--version") {
        return { status: 0, stdout: "git version 2.44.0\n" };
      }
      if (joined === "config --local --get core.untrackedCache") {
        return { status: 0, stdout: "false\n" };
      }
      if (joined === "config --local --get core.fsmonitor") {
        return { status: 0, stdout: ".git/hooks/fsmonitor-watchman\n" };
      }
      return { status: 1, stdout: "", stderr: joined };
    });

    const result = configureLocalGitPerformance({
      cwd: "/repo",
      platform: "win32",
      runGitFn,
      log: jest.fn(),
      warn: jest.fn()
    });

    expect(result.ok).toBe(true);
    expect(result.changed).toEqual([]);
    expect(calls).not.toContainEqual(["config", "--local", "core.untrackedCache", "true"]);
    expect(calls).not.toContainEqual(["config", "--local", "core.fsmonitor", "true"]);
  });

  test("git version parser gates built-in fsmonitor support", () => {
    expect(parseGitVersion("git version 2.44.1.windows.1")).toEqual({
      major: 2,
      minor: 44,
      patch: 1
    });
    expect(versionAtLeast(parseGitVersion("git version 2.36.9"), 2, 37, 0)).toBe(false);
    expect(versionAtLeast(parseGitVersion("git version 2.37.0"), 2, 37, 0)).toBe(true);
  });

  test("installer configures core.hooksPath in a Git worktree", () => {
    const temp = makeTempDir("native-hooks");
    try {
      expect(runGit(["init"], temp).status).toBe(0);

      const hooksDir = path.join(temp, "scripts", "hooks");
      fs.mkdirSync(hooksDir, { recursive: true });
      for (const hook of REQUIRED_NATIVE_HOOKS) {
        fs.writeFileSync(path.join(hooksDir, hook), "#!/usr/bin/env node\n", "utf8");
      }

      const result = installGitHooks({
        cwd: temp,
        log: () => {},
        warn: () => {}
      });

      expect(result).toEqual({ ok: true, changed: true, skipped: false });
      const configured = runGit(["config", "--local", "--get", "core.hooksPath"], temp);
      expect(configured.status).toBe(0);
      expect(configured.stdout.trim()).toBe("scripts/hooks");
    } finally {
      cleanupDir(temp);
    }
  });

  test("installer refuses to configure core.hooksPath when a required native hook is missing", () => {
    const temp = makeTempDir("native-hooks-missing");
    try {
      expect(runGit(["init"], temp).status).toBe(0);

      const hooksDir = path.join(temp, "scripts", "hooks");
      fs.mkdirSync(hooksDir, { recursive: true });
      for (const hook of REQUIRED_NATIVE_HOOKS.filter((hook) => hook !== "pre-commit")) {
        fs.writeFileSync(path.join(hooksDir, hook), "#!/usr/bin/env node\n", "utf8");
      }

      const warnings = [];
      const result = installGitHooks({
        cwd: temp,
        log: () => {},
        warn: (message) => warnings.push(message)
      });

      expect(result).toEqual({
        ok: false,
        changed: false,
        skipped: false,
        missingHooks: ["pre-commit"]
      });
      expect(warnings.join("\n")).toContain("scripts/hooks/pre-commit");
      const configured = runGit(["config", "--local", "--get", "core.hooksPath"], temp);
      expect(configured.status).not.toBe(0);
    } finally {
      cleanupDir(temp);
    }
  });

  test("installer no-ops outside a Git worktree", () => {
    const temp = makeTempDir("native-hooks-outside");
    try {
      const result = installGitHooks({
        cwd: temp,
        log: () => {},
        warn: () => {}
      });

      expect(result).toEqual({ ok: true, changed: false, skipped: true });
    } finally {
      cleanupDir(temp);
    }
  });
});
