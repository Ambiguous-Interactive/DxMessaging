/**
 * @fileoverview Tests for scripts/run-prepush-parallel.js -- the optimized
 * parallel executor of the full pre-push parity set used by the native
 * pre-push hook.
 *
 * Behavior tests use an injected async spawn (no real child processes, so
 * OS-independent). The DRIFT-GUARD tests are the load-bearing ones: they prove
 * the parallel plan preserves EXACTLY the coverage of `npm run
 * preflight:pre-push`:
 *   - the coarse sweeps PARTITION every pre-push hook (each runs once; none
 *     dropped) modulo the Jest subset suites that the full suite provably
 *     supersets,
 *   - the mutating set matches the only pre-push hooks that auto-restage /
 *     mutate tool state,
 *   - every `npm run` step in preflight:pre-commit is either an extra lane or a
 *     documented non-lane.
 */

"use strict";

const fs = require("fs");
const path = require("path");

const {
  MUTATING_PREPUSH_HOOKS,
  FULL_JEST_SUITE_HOOK,
  HOOK_COVERED_BY_FULL_JEST,
  PREFLIGHT_ONLY_EXTRAS,
  PREFLIGHT_ONLY_NON_LANE_REASONS,
  jestSuiteHookIds,
  buildLanes,
  runPool,
  defaultConcurrency,
  main
} = require("../run-prepush-parallel.js");
const { STEPS: PREPUSH_PREFLIGHT_STEPS } = require("../run-prepush-preflight.js");
const { hookIdsForStage } = require("../lib/precommit-stage-model.js");
const { findHookBlock } = require("../lib/precommit-yaml.js");
// PATH lookups must be case-insensitive: Windows exposes the variable as `Path`,
// POSIX as `PATH`. The merged spawn-env is a plain object, so a literal
// `env.PATH` read is case-SENSITIVE and returns undefined on windows-latest even
// though the child's PATH is intact -- which is exactly what failed this suite on
// Windows. getPathEnvValue resolves the value under either casing.
const { getPathEnvValue } = require("../lib/spawn-env-sandbox.js");

const PACKAGE_JSON = path.resolve(__dirname, "..", "..", "package.json");
const PRE_COMMIT_CONFIG = path.resolve(__dirname, "..", "..", ".pre-commit-config.yaml");

function prePushHookIds() {
  return hookIdsForStage("pre-push");
}

function laneById(id) {
  return buildLanes().find((lane) => lane.id === id);
}

function stepText(step) {
  const command = step.command === process.execPath ? "node" : step.command;
  return [command, ...step.args].join(" ");
}

/** SKIP set declared on a sweep lane's env (empty when none). */
function skipSetOf(laneId) {
  const lane = laneById(laneId);
  if (!lane || !lane.env || !lane.env.SKIP) {
    return new Set();
  }
  return new Set(lane.env.SKIP.split(",").filter(Boolean));
}

/** The pre-push hooks a sweep actually RUNS (all hooks minus its SKIP set). */
function hooksRunBy(laneId) {
  if (laneId === "sweep:jest") {
    return new Set([FULL_JEST_SUITE_HOOK]); // arg-scoped, not SKIP-scoped
  }
  const skip = skipSetOf(laneId);
  return new Set(prePushHookIds().filter((id) => !skip.has(id)));
}

/** Detect which pre-push hooks mutate the tree/index/tool state from the config. */
function detectMutatingPrePushHooks() {
  const lines = fs.readFileSync(PRE_COMMIT_CONFIG, "utf8").split("\n");
  const marker = /run-and-restage|run-and-stage|dotnet tool /;
  const mutating = [];
  for (const id of prePushHookIds()) {
    const block = findHookBlock(lines, id);
    const text = block ? block.lines.join("\n") : "";
    const entryMatch =
      /entry:\s*([\s\S]*?)(?:\n\s*(?:language|files|stages|name|pass_filenames|require_serial|description|types|exclude|args):|$)/.exec(
        text
      );
    if (entryMatch && marker.test(entryMatch[1])) {
      mutating.push(id);
    }
  }
  return mutating.sort();
}

describe("lane shape", () => {
  test("every lane is a coarse sweep or an npm extra (NO per-hook lanes -- per-hook pays ~10s startup each)", () => {
    for (const lane of buildLanes()) {
      expect(lane.id.startsWith("sweep:") || lane.id.startsWith("extra:")).toBe(true);
    }
  });

  test("sweep:jest runs the full Jest suite hook via ensure-pre-commit --all-files", () => {
    const lane = laneById("sweep:jest");
    expect(lane).toBeDefined();
    expect(lane.command).toBe(process.execPath);
    expect(lane.args).toEqual([
      "scripts/ensure-pre-commit.js",
      "run",
      "--hook-stage",
      "pre-push",
      FULL_JEST_SUITE_HOOK,
      "--all-files"
    ]);
    expect(lane.mutating).toBe(false);
    // Heavy: Jest spawns its own worker pool, so the other read-only lanes run
    // at reduced concurrency alongside it (a full-width pool starves Jest).
    expect(lane.heavy).toBe(true);
  });

  test("the read-only and mutating sweeps are full-set invocations differentiated only by SKIP", () => {
    for (const id of ["sweep:read-only-hooks", "sweep:mutating-hooks"]) {
      const lane = laneById(id);
      expect(lane).toBeDefined();
      expect(lane.args).toEqual([
        "scripts/ensure-pre-commit.js",
        "run",
        "--hook-stage",
        "pre-push",
        "--all-files"
      ]);
      expect(typeof lane.env.SKIP).toBe("string");
    }
  });

  test("extra lanes invoke npm scripts (package.json is the source of truth, no inlined globs)", () => {
    for (const extra of PREFLIGHT_ONLY_EXTRAS) {
      const lane = laneById(`extra:${extra.id}`);
      expect(lane).toBeDefined();
      expect(lane.command).toBe("npm");
      expect(lane.args).toEqual(["run", extra.npm]);
      expect(lane.mutating).toBe(extra.mutating === true);
    }
  });
});

describe("coverage -- the sweeps partition every pre-push hook (no hook dropped)", () => {
  test("union of sweeps covers every pre-push hook (Jest subsets via the full suite)", () => {
    const union = new Set([
      ...hooksRunBy("sweep:jest"),
      ...hooksRunBy("sweep:read-only-hooks"),
      ...hooksRunBy("sweep:mutating-hooks")
    ]);
    for (const id of prePushHookIds()) {
      const covered = union.has(id) || HOOK_COVERED_BY_FULL_JEST.has(id);
      expect(covered).toBe(true);
    }
  });

  test("no hook is run by more than one sweep (disjoint partition, no wasted work)", () => {
    const ran = [
      ...hooksRunBy("sweep:jest"),
      ...hooksRunBy("sweep:read-only-hooks"),
      ...hooksRunBy("sweep:mutating-hooks")
    ];
    expect(ran.length).toBe(new Set(ran).size);
  });

  test("the read-only sweep SKIPs exactly the Jest suites and the mutating hooks", () => {
    const skip = skipSetOf("sweep:read-only-hooks");
    const expected = new Set(
      [...jestSuiteHookIds(), ...MUTATING_PREPUSH_HOOKS].filter((id) =>
        new Set(prePushHookIds()).has(id)
      )
    );
    expect([...skip].sort()).toEqual([...expected].sort());
  });

  test("the mutating sweep runs ONLY the mutating hooks (SKIPs everything else)", () => {
    expect([...hooksRunBy("sweep:mutating-hooks")].sort()).toEqual(
      [...MUTATING_PREPUSH_HOOKS].sort()
    );
  });

  test("the covered Jest subset suites are run by NO sweep (deduped, not duplicated)", () => {
    const union = new Set([
      ...hooksRunBy("sweep:jest"),
      ...hooksRunBy("sweep:read-only-hooks"),
      ...hooksRunBy("sweep:mutating-hooks")
    ]);
    for (const id of HOOK_COVERED_BY_FULL_JEST) {
      expect(union.has(id)).toBe(false);
    }
  });
});

describe("mutating classification -- drift guard", () => {
  test("MUTATING_PREPUSH_HOOKS matches exactly the auto-restaging / tool-mutating pre-push hooks", () => {
    expect([...MUTATING_PREPUSH_HOOKS].sort()).toEqual(detectMutatingPrePushHooks());
  });

  test("every declared mutating hook is a real pre-push hook", () => {
    const ids = new Set(prePushHookIds());
    for (const id of MUTATING_PREPUSH_HOOKS) {
      expect(ids.has(id)).toBe(true);
    }
  });

  test("no pre-push hook entry stages the git index directly -- mutation only via the run-and-restage/run-and-stage wrappers", () => {
    // The marker heuristic in detectMutatingPrePushHooks (and MUTATING_PREPUSH_HOOKS)
    // recognizes mutation only through the run-and-restage / run-and-stage wrappers
    // (native-git-hooks skill, rule 5). This guard keeps that exhaustive: a hook
    // that stages the index with a bare `git add`/`git commit`/... in its entry --
    // bypassing the wrappers -- would be misclassified read-only and race the
    // index in the parallel pool. Reject it here.
    const lines = fs.readFileSync(PRE_COMMIT_CONFIG, "utf8").split("\n");
    for (const id of prePushHookIds()) {
      const block = findHookBlock(lines, id);
      const text = block ? block.lines.join("\n") : "";
      const entryMatch =
        /entry:\s*([\s\S]*?)(?:\n\s*(?:language|files|stages|name|pass_filenames|require_serial|description|types|exclude|args):|$)/.exec(
          text
        );
      const entry = entryMatch ? entryMatch[1] : "";
      const usesWrapper = /run-and-restage|run-and-stage/.test(entry);
      const directGitMutation = /\bgit\s+(?:add|commit|stash|rm|mv|reset|checkout)\b/.test(entry);
      expect(directGitMutation && !usesWrapper).toBe(false);
    }
  });
});

describe("Jest suite dedup -- coverage proof", () => {
  const lines = fs.readFileSync(PRE_COMMIT_CONFIG, "utf8").split("\n");

  // pre-commit passes --runTestsByPath via either the folded entry:
  // (script-parser-tests) or the args: list (unity-contract-tests); scan the
  // whole hook block.
  function hookBlockText(id) {
    const block = findHookBlock(lines, id);
    return block ? block.lines.join("\n") : "";
  }

  test("the survivor script-tests is the UNFILTERED full suite (run-managed-jest, no --runTestsByPath)", () => {
    const block = hookBlockText(FULL_JEST_SUITE_HOOK);
    expect(block).toContain("run-managed-jest.js");
    expect(block).not.toContain("--runTestsByPath");
  });

  test("every covered subset suite is a FILTERED subset, so the full suite supersets it", () => {
    // Manually verified airtight via `run-managed-jest.js --listTests`: all 74
    // files named by the subset suites are among the 117 the full suite
    // discovers. This proxy keeps the invariant honest at CI time.
    for (const id of HOOK_COVERED_BY_FULL_JEST) {
      expect(hookBlockText(id)).toContain("--runTestsByPath");
    }
  });

  test("covered subset suites are real pre-push hooks (subsumed, not typos)", () => {
    const ids = new Set(prePushHookIds());
    for (const id of HOOK_COVERED_BY_FULL_JEST) {
      expect(ids.has(id)).toBe(true);
    }
  });
});

describe("extras completeness -- no preflight-only check dropped", () => {
  const preflightPreCommit = JSON.parse(fs.readFileSync(PACKAGE_JSON, "utf8")).scripts[
    "preflight:pre-commit"
  ];
  const npmTokens = (preflightPreCommit.match(/npm run ([a-z0-9:_-]+)/g) || []).map((s) =>
    s.replace("npm run ", "")
  );
  const extraNpm = new Set(PREFLIGHT_ONLY_EXTRAS.map((e) => e.npm));
  const nonLane = PREFLIGHT_ONLY_NON_LANE_REASONS;

  test("preflight:pre-commit has npm steps to account for", () => {
    expect(npmTokens.length).toBeGreaterThan(10);
  });

  test("EVERY preflight:pre-commit npm step is either an extra lane or a documented non-lane", () => {
    const unaccounted = npmTokens.filter(
      (token) => !extraNpm.has(token) && !Object.prototype.hasOwnProperty.call(nonLane, token)
    );
    expect(unaccounted).toEqual([]);
  });

  test("EVERY safe serial preflight:pre-push step is covered by the parallel plan", () => {
    // run-prepush-parallel.js claims parity with preflight:pre-push, which is
    // `preflight:pre-commit && check:cspell:all && <inline --hook-stage pre-push
    // --all-files>`, now encoded in scripts/run-prepush-preflight.js so the
    // success stamp is impossible to run without the preceding checks. The
    // pre-commit accounting above covers the first; this guards a NEW step
    // added DIRECTLY to that runner from silently drifting past the parallel
    // orchestrator.
    const steps = PREPUSH_PREFLIGHT_STEPS.map(stepText);
    for (const step of steps) {
      const covered =
        /\bnpm run preflight:pre-commit\b/.test(step) || // accounted for by the pre-commit completeness test
        /\bnpm run check:cspell:all\b/.test(step) || // cspell-superset: covered by the cspell hook lane
        /--hook-stage\s+pre-push\s+--all-files\b/.test(step); // covered by the sweep:* lanes
      expect(covered).toBe(true);
    }
  });

  test("an npm step is never both an extra lane and a non-lane", () => {
    for (const token of Object.keys(nonLane)) {
      expect(extraNpm.has(token)).toBe(false);
    }
  });

  test("every extra lane npm script exists in package.json scripts", () => {
    const scripts = JSON.parse(fs.readFileSync(PACKAGE_JSON, "utf8")).scripts;
    for (const extra of PREFLIGHT_ONLY_EXTRAS) {
      expect(typeof scripts[extra.npm]).toBe("string");
    }
  });

  test("'covered-by-hook' non-lane steps map to a real pre-push hook", () => {
    const coveredByHook = {
      "validate:pre-commit-tooling": "validate-pre-commit-tooling",
      "validate:npm-meta": "validate-npm-meta",
      "validate:changelog:coverage": "validate-changelog-policy",
      "validate:runtime-settings-docs": "validate-runtime-settings-docs",
      "validate:no-plan-vocabulary": "validate-no-plan-vocabulary",
      "validate:untracked-policy": "validate-untracked-policy"
    };
    const ids = new Set(prePushHookIds());
    for (const [npm, hookId] of Object.entries(coveredByHook)) {
      expect(nonLane[npm]).toBe("covered-by-hook");
      expect(ids.has(hookId)).toBe(true);
    }
  });

  test("the deduped cspell steps are documented as covered by the cspell hook", () => {
    expect(nonLane["check:cspell:scripts"]).toBe("cspell-superset");
    expect(nonLane["check:workflow-cspell"]).toBe("cspell-superset");
    expect(new Set(prePushHookIds()).has("cspell")).toBe(true);
  });
});

// ---- Behavior (injected async spawn; OS-independent) ---------------------

/**
 * Injected async spawn that maps (command,args,SKIP) back to a lane id via
 * buildLanes(), records invocation order + max concurrency, and fails lanes
 * whose id contains any string in failSubstrings.
 */
function makeTrackingSpawn({ failSubstrings = [] } = {}) {
  const lanes = buildLanes();
  const key = (command, args, skip) => `${command}|${args.join(" ")}|${skip || ""}`;
  const idByKey = new Map(lanes.map((l) => [key(l.command, l.args, l.env && l.env.SKIP), l.id]));
  const order = [];
  let live = 0;
  let maxLive = 0;
  const liveSet = new Set();
  const coLive = new Map(); // lane id -> Set of peer ids ever concurrently live
  const impl = async (command, args, options) => {
    const skip = options && options.env ? options.env.SKIP : undefined;
    const id = idByKey.get(key(command, args, skip)) || `${command} ${args.join(" ")}`;
    if (!coLive.has(id)) {
      coLive.set(id, new Set());
    }
    for (const peer of liveSet) {
      coLive.get(id).add(peer);
      if (!coLive.has(peer)) {
        coLive.set(peer, new Set());
      }
      coLive.get(peer).add(id);
    }
    liveSet.add(id);
    order.push(id);
    live += 1;
    maxLive = Math.max(maxLive, live);
    await new Promise((resolve) => setImmediate(resolve));
    live -= 1;
    liveSet.delete(id);
    const fail = failSubstrings.some((sub) => id.includes(sub));
    return {
      status: fail ? 1 : 0,
      signal: null,
      stdout: fail ? "boom output" : "",
      stderr: "",
      error: null
    };
  };
  impl.order = order;
  impl.getMaxLive = () => maxLive;
  impl.coLiveOf = (id) => coLive.get(id) || new Set();
  return impl;
}

describe("runPool", () => {
  const lanes = [
    { id: "a", command: "node", args: ["a"], mutating: false },
    { id: "b", command: "node", args: ["b"], mutating: false },
    { id: "c", command: "node", args: ["c"], mutating: false }
  ];

  test("runs every lane even when one fails (no fail-fast) and reports per-lane ok", async () => {
    const spawn = async (command, args) => ({
      status: args[0] === "b" ? 2 : 0,
      stdout: "",
      stderr: "",
      error: null
    });
    const results = await runPool(lanes, 3, spawn, () => {});
    expect(results).toHaveLength(3);
    expect(results.find((r) => r.id === "b").ok).toBe(false);
    expect(
      results
        .filter((r) => r.ok)
        .map((r) => r.id)
        .sort()
    ).toEqual(["a", "c"]);
  });

  test("respects the concurrency limit", async () => {
    let live = 0;
    let maxLive = 0;
    const spawn = async () => {
      live += 1;
      maxLive = Math.max(maxLive, live);
      await new Promise((resolve) => setImmediate(resolve));
      live -= 1;
      return { status: 0, stdout: "", stderr: "", error: null };
    };
    const many = Array.from({ length: 8 }, (_, i) => ({
      id: `l${i}`,
      command: "node",
      args: [`${i}`]
    }));
    await runPool(many, 2, spawn, () => {});
    expect(maxLive).toBeLessThanOrEqual(2);
  });

  test("strips caller SKIP, preserves controlled lane SKIP, and keeps PATH", async () => {
    const originalSkip = process.env.SKIP;
    const originalLowerSkip = process.env.skip;
    const originalMixedSkip = process.env.SkIp;
    process.env.SKIP = "script-tests,cspell";
    process.env.skip = "validate-untracked-policy";
    process.env.SkIp = "yamllint";
    const capturedOptions = [];
    const spawn = async (command, args, options) => {
      capturedOptions.push(options);
      return { status: 0, stdout: "", stderr: "", error: null };
    };
    try {
      await runPool(
        [
          { id: "controlled", command: "node", args: ["a"], env: { SKIP: "foo,bar" } },
          { id: "plain", command: "node", args: ["b"] }
        ],
        1,
        spawn,
        () => {}
      );

      expect(capturedOptions[0].env.SKIP).toBe("foo,bar");
      expect(capturedOptions[0].env.skip).toBeUndefined();
      expect(capturedOptions[0].env.SkIp).toBeUndefined();
      expect(capturedOptions[1].env.SKIP).toBeUndefined();
      expect(capturedOptions[1].env.skip).toBeUndefined();
      expect(capturedOptions[1].env.SkIp).toBeUndefined();
      // Case-insensitive: the child must inherit PATH (Windows `Path` / POSIX `PATH`).
      expect(getPathEnvValue(capturedOptions[0].env)).toBe(getPathEnvValue(process.env));
      expect(getPathEnvValue(capturedOptions[1].env)).toBe(getPathEnvValue(process.env));
    } finally {
      if (originalSkip === undefined) {
        delete process.env.SKIP;
      } else {
        process.env.SKIP = originalSkip;
      }
      if (originalLowerSkip === undefined) {
        delete process.env.skip;
      } else {
        process.env.skip = originalLowerSkip;
      }
      if (originalMixedSkip === undefined) {
        delete process.env.SkIp;
      } else {
        process.env.SkIp = originalMixedSkip;
      }
    }
  });

  test("a spawn error marks the lane failed and is streamed", async () => {
    const spawn = async () => ({
      status: null,
      stdout: "",
      stderr: "",
      error: new Error("ENOENT-ish")
    });
    const log = [];
    const results = await runPool([lanes[0]], 1, spawn, (line) => log.push(line));
    expect(results[0].ok).toBe(false);
    expect(log.join("\n")).toContain("[FAIL a]");
    expect(log.join("\n")).toContain("spawn error");
  });
});

describe("main", () => {
  test("mutating lanes all run BEFORE any read-only lane (fix-before-check auto-heal ordering); returns 0 when all pass", async () => {
    const spawn = makeTrackingSpawn();
    const code = await main({ spawnImpl: spawn, writeLine: () => {}, concurrency: 4 });
    expect(code).toBe(0);

    const lanes = buildLanes();
    const order = spawn.order;
    const idx = (id) => order.indexOf(id);
    const readOnlyIds = lanes.filter((l) => !l.mutating).map((l) => l.id);
    const mutatingIds = lanes.filter((l) => l.mutating).map((l) => l.id);

    expect(mutatingIds.length).toBeGreaterThan(0);
    const lastMutating = Math.max(...mutatingIds.map(idx));
    const firstReadOnly = Math.min(...readOnlyIds.map(idx));
    expect(lastMutating).toBeLessThan(firstReadOnly);
  });

  test("the heavy Jest lane runs concurrently with >= 1 light lane AND throttles the pool (no full-width starvation)", async () => {
    const spawn = makeTrackingSpawn();
    // concurrency 9 -> lightConcurrency = max(2, floor(9/3)) = 3; phase-1 peak
    // is heavy(1) + light(3) = 4. The phase-0 mutating pool is serial (1).
    await main({ spawnImpl: spawn, writeLine: () => {}, concurrency: 9 });

    const lanes = buildLanes();
    const heavyId = lanes.find((l) => l.heavy).id;
    const lightIds = new Set(lanes.filter((l) => !l.mutating && !l.heavy).map((l) => l.id));
    const heavyLightPeers = [...spawn.coLiveOf(heavyId)].filter((id) => lightIds.has(id));

    // REAL overlap (per-lane co-liveness, not aggregate maxLive which the light
    // pool alone could satisfy): the heavy lane was concurrently live with >= 1
    // light lane. Serializing heavy after the light pool would make this empty.
    expect(heavyLightPeers.length).toBeGreaterThanOrEqual(1);
    // Throttled, not full-width: peak concurrency bounded by heavy(1) + light(3).
    expect(spawn.getMaxLive()).toBeLessThanOrEqual(1 + 3);
  });

  test("returns 1 when the mutating sweep fails (failure in the serial phase still gates)", async () => {
    const spawn = makeTrackingSpawn({ failSubstrings: ["sweep:mutating-hooks"] });
    const code = await main({ spawnImpl: spawn, writeLine: () => {}, concurrency: 4 });
    expect(code).toBe(1);
  });

  test("returns 1 when a read-only sweep fails", async () => {
    const spawn = makeTrackingSpawn({ failSubstrings: ["sweep:read-only-hooks"] });
    const code = await main({ spawnImpl: spawn, writeLine: () => {}, concurrency: 4 });
    expect(code).toBe(1);
  });

  test("emits a final summary line", async () => {
    const log = [];
    await main({
      spawnImpl: makeTrackingSpawn(),
      writeLine: (line) => log.push(line),
      concurrency: 4
    });
    expect(log.join("\n")).toMatch(/all \d+ lanes passed/);
  });
});

describe("defaultConcurrency", () => {
  test("is at least 1", () => {
    expect(defaultConcurrency()).toBeGreaterThanOrEqual(1);
  });
});
