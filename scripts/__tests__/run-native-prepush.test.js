"use strict";

const nativePrePush = require("../run-native-prepush");
const { buildSpawnInvocation } = require("../lib/shell-command");

const ZERO = "0000000000000000000000000000000000000000";
const LOCAL = "1111111111111111111111111111111111111111";
const REMOTE = "2222222222222222222222222222222222222222";
const BASE = "3333333333333333333333333333333333333333";

function makeGit(rules = []) {
  const calls = [];
  const runGitFn = (args) => {
    calls.push(args.slice());
    for (const rule of rules) {
      if (rule.when(args)) {
        return rule.result;
      }
    }
    return { status: 1, stdout: "", stderr: `unexpected git ${args.join(" ")}` };
  };
  return { runGitFn, calls };
}

const revParse = (ref) => (args) =>
  args[0] === "rev-parse" && args.includes("--verify") && args.includes(ref);
const mergeBase = (args) => args[0] === "merge-base";
const catFile = (oid) => (args) => args[0] === "cat-file" && args[2] === `${oid}^{commit}`;

describe("native pre-push stdin parsing", () => {
  test("parses standard four-field ref update lines", () => {
    const updates = nativePrePush.parsePrePushInput(
      `refs/heads/feature ${LOCAL} refs/heads/feature ${REMOTE}\n`
    );

    expect(updates).toEqual([
      {
        localRef: "refs/heads/feature",
        localOid: LOCAL,
        remoteRef: "refs/heads/feature",
        remoteOid: REMOTE
      }
    ]);
  });

  test("rejects malformed pre-push input", () => {
    expect(() => nativePrePush.parsePrePushInput("refs/heads/main only-two-fields")).toThrow(
      /malformed/
    );
  });
});

describe("native pre-push range planning", () => {
  test("remote base candidates keep remote refs fully qualified", () => {
    const refs = nativePrePush.remoteBaseCandidates("upstream").map((candidate) => candidate.ref);

    expect(refs).toContain("refs/remotes/upstream/HEAD");
    expect(refs).toContain("refs/remotes/upstream/master");
    expect(refs).toContain("refs/remotes/upstream/main");
    expect(refs).toContain("refs/remotes/origin/HEAD");
    expect(refs).toContain("refs/heads/master");
    expect(refs).toContain("refs/heads/main");
    expect(refs).not.toContain("upstream/HEAD");
    expect(refs).not.toContain("origin/HEAD");
  });

  test("remote base candidates allow slash-delimited remote names", () => {
    const refs = nativePrePush
      .remoteBaseCandidates("team/upstream")
      .map((candidate) => candidate.ref);

    expect(refs.slice(0, 3)).toEqual([
      "refs/remotes/team/upstream/HEAD",
      "refs/remotes/team/upstream/master",
      "refs/remotes/team/upstream/main"
    ]);
    expect(refs).toContain("refs/remotes/origin/HEAD");
  });

  test("skips deleted refs and no-op ref updates before validation", () => {
    const jobs = nativePrePush.buildValidationJobs([
      {
        localRef: "(delete)",
        localOid: ZERO,
        remoteRef: "refs/heads/deleted",
        remoteOid: REMOTE
      },
      {
        localRef: "refs/heads/noop",
        localOid: LOCAL,
        remoteRef: "refs/heads/noop",
        remoteOid: LOCAL
      }
    ]);

    expect(jobs).toEqual([]);
  });

  test("existing remote refs validate the exact remote_oid..local_oid range", () => {
    const { runGitFn } = makeGit([
      { when: catFile(REMOTE), result: { status: 0, stdout: "" } },
      { when: catFile(LOCAL), result: { status: 0, stdout: "" } }
    ]);

    const jobs = nativePrePush.buildValidationJobs(
      [
        {
          localRef: "refs/heads/feature",
          localOid: LOCAL,
          remoteRef: "refs/heads/feature",
          remoteOid: REMOTE
        }
      ],
      { runGitFn }
    );

    expect(jobs).toEqual([
      {
        type: "range",
        rangeFrom: REMOTE,
        rangeTo: LOCAL,
        label: `refs/heads/feature: ${REMOTE}..${LOCAL}`
      }
    ]);
  });

  test("existing remote refs fall back to exhaustive preflight when an endpoint is not local", () => {
    const { runGitFn } = makeGit([
      { when: catFile(REMOTE), result: { status: 1, stdout: "" } },
      { when: catFile(LOCAL), result: { status: 0, stdout: "" } }
    ]);

    const jobs = nativePrePush.buildValidationJobs(
      [
        {
          localRef: "refs/heads/feature",
          localOid: LOCAL,
          remoteRef: "refs/heads/feature",
          remoteOid: REMOTE
        }
      ],
      { runGitFn }
    );

    expect(jobs).toEqual([
      { type: "full", label: "ref update endpoint is not available in the local object database" }
    ]);
  });

  test("new refs validate from merge-base with the default branch when available", () => {
    const { runGitFn, calls } = makeGit([
      { when: revParse("refs/remotes/origin/HEAD"), result: { status: 0, stdout: "" } },
      { when: mergeBase, result: { status: 0, stdout: `${BASE}\n` } }
    ]);

    const jobs = nativePrePush.buildValidationJobs(
      [
        {
          localRef: "refs/heads/new",
          localOid: LOCAL,
          remoteRef: "refs/heads/new",
          remoteOid: ZERO
        }
      ],
      { runGitFn }
    );

    expect(jobs).toEqual([
      {
        type: "range",
        rangeFrom: BASE,
        rangeTo: LOCAL,
        label: `refs/heads/new: ${BASE}..${LOCAL} (new ref via refs/remotes/origin/HEAD)`
      }
    ]);
    expect(calls).toContainEqual(["merge-base", "refs/remotes/origin/HEAD", LOCAL]);
  });

  test("new refs prefer the pushed remote HEAD before origin candidates", () => {
    const { runGitFn, calls } = makeGit([
      { when: revParse("refs/remotes/upstream/HEAD"), result: { status: 0, stdout: "" } },
      { when: mergeBase, result: { status: 0, stdout: `${BASE}\n` } }
    ]);

    const jobs = nativePrePush.buildValidationJobs(
      [
        {
          localRef: "refs/heads/new",
          localOid: LOCAL,
          remoteRef: "refs/heads/new",
          remoteOid: ZERO
        }
      ],
      { runGitFn, remoteName: "upstream" }
    );

    expect(jobs).toEqual([
      {
        type: "range",
        rangeFrom: BASE,
        rangeTo: LOCAL,
        label: `refs/heads/new: ${BASE}..${LOCAL} (new ref via refs/remotes/upstream/HEAD)`
      }
    ]);
    expect(calls).toContainEqual(["merge-base", "refs/remotes/upstream/HEAD", LOCAL]);
    expect(calls).not.toContainEqual(["rev-parse", "--verify", "--quiet", "refs/remotes/origin/HEAD"]);
    expect(calls).not.toContainEqual(["merge-base", "refs/remotes/origin/HEAD", LOCAL]);
    expect(calls).not.toContainEqual(["merge-base", "upstream/HEAD", LOCAL]);
  });

  test("unsafe remote names are not interpolated into candidate refs", () => {
    const { runGitFn, calls } = makeGit([
      { when: revParse("refs/remotes/origin/HEAD"), result: { status: 0, stdout: "" } },
      { when: mergeBase, result: { status: 0, stdout: `${BASE}\n` } }
    ]);

    const jobs = nativePrePush.buildValidationJobs(
      [
        {
          localRef: "refs/heads/new",
          localOid: LOCAL,
          remoteRef: "refs/heads/new",
          remoteOid: ZERO
        }
      ],
      { runGitFn, remoteName: "../upstream" }
    );

    expect(jobs[0].label).toContain("(new ref via refs/remotes/origin/HEAD)");
    expect(calls.flat().join("\n")).not.toContain("../upstream");
  });

  test("remote base candidates reject invalid slash components", () => {
    for (const remoteName of [
      "../upstream",
      "team//upstream",
      "team/.upstream",
      "team/upstream.lock",
      "team/up stream"
    ]) {
      const refs = nativePrePush.remoteBaseCandidates(remoteName).map((candidate) => candidate.ref);
      expect(refs).not.toContain(`refs/remotes/${remoteName}/HEAD`);
      expect(refs[0]).toBe("refs/remotes/origin/HEAD");
    }
  });


  test("new refs fall back to exhaustive preflight when no default branch base resolves", () => {
    const { runGitFn } = makeGit([
      { when: (args) => args[0] === "rev-parse", result: { status: 1, stdout: "" } }
    ]);

    const jobs = nativePrePush.buildValidationJobs(
      [
        {
          localRef: "refs/heads/new",
          localOid: LOCAL,
          remoteRef: "refs/heads/new",
          remoteOid: ZERO
        }
      ],
      { runGitFn }
    );

    expect(jobs).toEqual([
      { type: "full", label: "new ref without a resolvable default-branch merge base" }
    ]);
  });

  test("dedupes multiple refs that point at the same validation range", () => {
    const { runGitFn } = makeGit([
      { when: catFile(REMOTE), result: { status: 0, stdout: "" } },
      { when: catFile(LOCAL), result: { status: 0, stdout: "" } }
    ]);

    const jobs = nativePrePush.buildValidationJobs(
      [
        {
          localRef: "refs/heads/a",
          localOid: LOCAL,
          remoteRef: "refs/heads/a",
          remoteOid: REMOTE
        },
        {
          localRef: "refs/heads/b",
          localOid: LOCAL,
          remoteRef: "refs/heads/b",
          remoteOid: REMOTE
        }
      ],
      { runGitFn }
    );

    expect(jobs).toHaveLength(1);
    expect(jobs[0].rangeFrom).toBe(REMOTE);
    expect(jobs[0].rangeTo).toBe(LOCAL);
  });
});

describe("native pre-push validation execution", () => {
  test("range jobs call preflight with pre-push guard profile and no worktree scan", () => {
    const calls = [];
    const status = nativePrePush.runJobs(
      [{ type: "range", rangeFrom: REMOTE, rangeTo: LOCAL, label: "range" }],
      {
        spawnFn: (command, args, options) => {
          calls.push({ command, args, options });
          return { status: 0 };
        },
        env: { SKIP: "cspell", skip: "yamllint", PATH: "test-path" },
        logFn: () => {}
      }
    );

    expect(status).toBe(0);
    expect(calls).toHaveLength(1);
    expect(calls[0].command).toBe(process.execPath);
    expect(calls[0].args).toEqual([
      "scripts/preflight.js",
      "--stage=pre-push",
      "--profile=guard",
      "--range-from",
      REMOTE,
      "--range-to",
      LOCAL,
      "--no-worktree"
    ]);
    expect(calls[0].options.env.SKIP).toBeUndefined();
    expect(calls[0].options.env.skip).toBeUndefined();
  });

  test("full fallback delegates to npm run preflight:pre-push with caller SKIP stripped", () => {
    const calls = [];
    const status = nativePrePush.runJobs(
      [{ type: "full", label: "fallback" }],
      {
        spawnFn: (command, args, options) => {
          calls.push({ command, args, options });
          return { status: 0 };
        },
        env: { SKIP: "script-tests", PATH: "test-path" },
        logFn: () => {}
      }
    );

    expect(status).toBe(0);
    expect(calls).toHaveLength(1);
    expect(calls[0].command).toBe("npm");
    expect(calls[0].args).toEqual(["run", "preflight:pre-push"]);
    expect(calls[0].options.env.SKIP).toBeUndefined();
  });

  test("full fallback uses the platform-aware npm shim shape on Windows", () => {
    const spawnSyncImpl = jest.fn(() => ({ status: 0 }));
    const status = nativePrePush.runFullFallback({
      spawnSyncImpl,
      platform: "win32",
      env: { SKIP: "script-tests", PATH: "test-path", ComSpec: "cmd.exe" }
    });

    expect(status).toBe(0);
    const expected = buildSpawnInvocation(
      "npm",
      ["run", "preflight:pre-push"],
      {
        cwd: nativePrePush.REPO_ROOT,
        env: {},
        stdio: "inherit"
      },
      "win32"
    );
    expect(spawnSyncImpl).toHaveBeenCalledWith(
      expected.command,
      expected.args,
      expect.objectContaining({
        cwd: nativePrePush.REPO_ROOT,
        env: expect.objectContaining({ PATH: "test-path" }),
        stdio: "inherit",
        shell: false,
        windowsHide: true
      })
    );
    const env = spawnSyncImpl.mock.calls[0][2].env;
    expect(env.SKIP).toBeUndefined();
  });

  test("main skips validation for no-op input without spawning tooling", () => {
    const spawnFn = jest.fn();
    const status = nativePrePush.main([], {
      stdin: `refs/heads/main ${LOCAL} refs/heads/main ${LOCAL}\n`,
      spawnFn,
      logFn: () => {}
    });

    expect(status).toBe(0);
    expect(spawnFn).not.toHaveBeenCalled();
  });

  test("main uses Git pre-push argv remote name for new-ref base resolution", () => {
    const { runGitFn } = makeGit([
      { when: revParse("refs/remotes/upstream/HEAD"), result: { status: 0, stdout: "" } },
      { when: mergeBase, result: { status: 0, stdout: `${BASE}\n` } }
    ]);
    const logFn = jest.fn();

    const status = nativePrePush.main(["upstream", "git@example.com:repo.git"], {
      stdin: `refs/heads/new ${LOCAL} refs/heads/new ${ZERO}\n`,
      runGitFn,
      spawnFn: () => ({ status: 0 }),
      logFn
    });

    expect(status).toBe(0);
    expect(logFn).toHaveBeenCalledWith(
      `native pre-push: validating refs/heads/new: ${BASE}..${LOCAL} (new ref via refs/remotes/upstream/HEAD).`
    );
  });
});
