#!/usr/bin/env node
"use strict";

/**
 * run-prepush-parallel.js
 *
 * The OPTIMIZED executor for the full pre-push parity set. It runs the SAME
 * validation coverage as `npm run preflight:pre-push` but far faster, and is
 * retained as an explicit CI/manual exhaustive accelerator. Native local
 * pre-push uses scripts/run-native-prepush.js instead.
 *
 * Cost model (measured on an 8-core Linux dev container; Windows ~2x):
 *   - `pre-commit` startup is ~10s PER INVOCATION (config load + env resolve +
 *     all-files enumeration). So the win is NOT "one lane per hook" (that pays
 *     ~10s x ~20 = a disaster, measured ~113s); it is running the FEWEST
 *     invocations and overlapping the big ones.
 *   - The full Jest suite (`script-tests`) is ~39s and spawns its own worker
 *     pool. The read-only non-Jest sweep (cspell + ~16 validators in ONE
 *     invocation) is ~32s. They run CONCURRENTLY (Jest's workers + the mostly
 *     single-threaded sweep), so wall-clock is ~max, not the sum.
 *
 * Lanes (each = one child process):
 *   1. sweep:jest -- ONE `pre-commit run script-tests --all-files` (the full
 *      Jest suite; covers the subset Jest suites, see DEDUP below).
 *   2. sweep:read-only-hooks -- ONE `pre-commit run --all-files` with `SKIP` set
 *      to the Jest suites + the mutating hooks, so it runs every read-only
 *      non-Jest pre-push hook in a SINGLE invocation.
 *   3. extra:* (read-only) -- the preflight-only `npm run` checks that are not
 *      pre-push hooks. Run via npm (the package.json source of truth) so an
 *      inlined glob can never drift.
 *   4. sweep:mutating-hooks (serial) -- ONE `pre-commit run --all-files` with
 *      `SKIP` = everything except the mutating hooks (csharpier restages,
 *      dotnet-tool-restore mutates tool state).
 *   5. extra:* (mutating, serial) -- the README markdown fixer + skills-index
 *      regen.
 *
 * Phases: mutating lanes (4-5) run serially FIRST (phase 0); then read-only
 * lanes (1-3) run together in a bounded pool (phase 1). Mutating-first preserves
 * the serial preflight's fix-before-check auto-heal: an auto-fixer/restager
 * (csharpier, repair:llm-policy) completes before the read-only freshness check
 * with the same target (e.g. the skills-index-check hook) observes the tree, so
 * a stale-but-recoverable input self-heals instead of hard-failing the push (the
 * zero-manual-touch invariant). Serial among writers => no two writers race the
 * git index; before all readers => no reader observes a partial write.
 *
 * DEDUP (coverage preserved, proven by run-prepush-parallel.test.js):
 *   - Jest: the `script-parser-tests` and `unity-contract-tests` subset suites
 *     are SKIPPED everywhere because the full `script-tests` suite (run-managed-
 *     jest with no --runTestsByPath) is a PROVEN superset (every file they name
 *     is discovered by the full run). Running them again re-runs the same tests
 *     and oversubscribes cores.
 *   - cspell: `check:cspell:all` / `:scripts` / `workflow-cspell` are dropped
 *     because the `cspell` pre-push hook (inside sweep:read-only-hooks) runs
 *     cspell over the same extension set on --all-files.
 *
 * `npm run preflight:pre-push` stays the byte-for-byte simple, serial parity
 * command for CI and on-demand use. Pure Node + scripts/lib/shell-command.js
 * (no shell, no bash); runs on native Linux, macOS, and Windows.
 */

const os = require("os");
const path = require("path");
const { mergeSanitizedEnv, spawnPlatformCommand } = require("./lib/shell-command");
const { hookIdsForStage } = require("./lib/precommit-stage-model");

const REPO_ROOT = path.resolve(__dirname, "..");

/**
 * Pre-push hooks that mutate the working tree / index or shared tool state:
 *   - csharpier: `run-and-restage` formatter (auto-formats + `git add`s .cs),
 *   - dotnet-tool-restore: restores .NET tools (mutates the local tool cache).
 * They run in the serial mutating phase (phase 0, before all readers). A test
 * asserts these are EXACTLY the pre-push hooks whose entry auto-fixes/restages
 * or restores tools, so a newly added mutating hook fails the build until it is
 * classified here. The detection assumes the repository contract (native-git-
 * hooks skill, rule 5) that EVERY tree-mutating hook stages through
 * `scripts/run-and-restage.js` / `scripts/run-and-stage.js`; a paired test also
 * rejects any pre-push hook entry that stages the git index directly, so the
 * marker heuristic stays exhaustive by construction.
 */
const MUTATING_PREPUSH_HOOKS = new Set(["csharpier", "dotnet-tool-restore"]);

/**
 * The full Jest suite hook: `script-tests` runs `run-managed-jest.js` with NO
 * --runTestsByPath filter, so it executes EVERY discovered test file.
 */
const FULL_JEST_SUITE_HOOK = "script-tests";

/**
 * Pre-push Jest "suite" hooks whose test set is a PROVABLE SUBSET of the full
 * `script-tests` suite (every file they name is among the files the unfiltered
 * full suite discovers -- verified via `run-managed-jest.js --listTests`). They
 * are SKIPPED in every sweep; `sweep:jest` is the single survivor.
 */
const HOOK_COVERED_BY_FULL_JEST = new Set(["script-parser-tests", "unity-contract-tests"]);

/**
 * `preflight:pre-commit` checks that are NOT pre-push hooks (so the sweeps do
 * not cover them). Each runs via `npm run <npm>` so there is zero risk of an
 * inlined glob drifting from the package.json script. `mutating: true` routes a
 * lane to the serial phase.
 */
const PREFLIGHT_ONLY_EXTRAS = Object.freeze([
  { id: "validate-node-tooling", npm: "validate:node-tooling" },
  { id: "validate-markdown-link-text", npm: "validate:markdown-link-text" },
  { id: "validate-changed-docs", npm: "validate:changed-docs" },
  { id: "validate-llm-markdown", npm: "validate:llm-markdown" },
  { id: "validate-workflows", npm: "validate:workflows" },
  { id: "validate-unity-versions", npm: "validate:unity-versions" },
  { id: "check-package-json-format", npm: "check:package-json-format" },
  { id: "check-prettier-hooks", npm: "check:prettier:hooks" },
  { id: "check-pwsh-output-assertions", npm: "check:pwsh-output-assertions" },
  { id: "check-yaml", npm: "check:yaml" },
  { id: "check-banner-sync", npm: "check:banner-sync" },
  { id: "validate-repo-identity", npm: "validate:repo-identity" },
  // Mutating extras (serial phase): the README markdown FIXER smoke-test and the
  // skills-index regen-and-stage. Kept to preserve exact preflight:pre-commit
  // behavior; both are no-ops on a clean tree.
  { id: "validate-hook-markdown", npm: "validate:hook-markdown", mutating: true },
  { id: "repair-llm-policy", npm: "repair:llm-policy", mutating: true }
]);

/**
 * preflight:pre-commit `npm run` steps that this orchestrator deliberately does
 * NOT run as an extra lane, with the reason. Used by the drift test to prove the
 * extras list is complete. Keyed by npm script name.
 *   - bootstrap: run by the native hook before this orchestrator.
 *   - cspell-superset: covered by the `cspell` pre-push hook (in the read-only
 *     sweep), which runs --all-files over the same extension set.
 *   - covered-by-hook: the same check runs as a pre-push hook (in a sweep).
 *   - decomposed: a wrapper whose parts are individually covered (hooks + an
 *     extra lane).
 */
const PREFLIGHT_ONLY_NON_LANE_REASONS = Object.freeze({
  "repair:node-tooling": "bootstrap",
  "repair:pre-commit": "bootstrap",
  "check:cspell:scripts": "cspell-superset",
  "check:workflow-cspell": "cspell-superset",
  "validate:pre-commit-tooling": "covered-by-hook",
  "validate:npm-meta": "covered-by-hook",
  "validate:changelog:coverage": "covered-by-hook",
  "validate:runtime-settings-docs": "covered-by-hook",
  "validate:no-plan-vocabulary": "covered-by-hook",
  "validate:untracked-policy": "covered-by-hook",
  // validate:llm-policy = validate:skills (hook) + generate-skills-index --check
  // (skills-index-check hook) + validate:llm-markdown (extra lane above).
  "validate:llm-policy": "decomposed"
});

/** Hook ids that run via the full Jest suite (so every sweep SKIPs them). */
function jestSuiteHookIds() {
  return [FULL_JEST_SUITE_HOOK, ...HOOK_COVERED_BY_FULL_JEST];
}

/**
 * Build the ordered lane set. A lane is
 * `{ id, command, args, mutating, env? }`.
 *
 * @param {object} [deps] Injectable deps for tests.
 * @param {Function} [deps.hookIds] Returns the pre-push hook id list.
 * @returns {Array<{id:string, command:string, args:string[], mutating:boolean,
 *   env?:object}>}
 */
function buildLanes(deps = {}) {
  const hookIds = (deps.hookIds || (() => hookIdsForStage("pre-push")))();
  const hookSet = new Set(hookIds);
  const present = (id) => hookSet.has(id);
  const ensureArgs = (...rest) => [
    "scripts/ensure-pre-commit.js",
    "run",
    "--hook-stage",
    "pre-push",
    ...rest,
    "--all-files"
  ];
  const lanes = [];

  // 1. The full Jest suite, ONE invocation (covers the subset Jest suites).
  if (present(FULL_JEST_SUITE_HOOK)) {
    lanes.push({
      id: "sweep:jest",
      command: process.execPath,
      args: ensureArgs(FULL_JEST_SUITE_HOOK),
      mutating: false,
      // Jest spawns its OWN worker pool (~every core). Marked heavy so the other
      // read-only lanes run at REDUCED concurrency alongside it instead of
      // starving it in a full-width pool (measured ~91s vs ~55s).
      heavy: true
    });
  }

  // 2. Every read-only non-Jest pre-push hook in ONE invocation, via SKIP of the
  //    Jest suites (covered by sweep:jest) and the mutating hooks (serial phase).
  const readOnlySkip = [...jestSuiteHookIds(), ...MUTATING_PREPUSH_HOOKS].filter(present);
  lanes.push({
    id: "sweep:read-only-hooks",
    command: process.execPath,
    args: ensureArgs(),
    env: { SKIP: readOnlySkip.join(",") },
    mutating: false
  });

  // 3. Read-only preflight-only extras (npm scripts that are not pre-push hooks).
  for (const extra of PREFLIGHT_ONLY_EXTRAS.filter((entry) => entry.mutating !== true)) {
    lanes.push({
      id: `extra:${extra.id}`,
      command: "npm",
      args: ["run", extra.npm],
      mutating: false
    });
  }

  // 4. The mutating pre-push hooks in ONE invocation, via SKIP of everything else.
  const mutatingHooks = [...MUTATING_PREPUSH_HOOKS].filter(present);
  if (mutatingHooks.length > 0) {
    const mutatingSkip = hookIds.filter((id) => !MUTATING_PREPUSH_HOOKS.has(id));
    lanes.push({
      id: "sweep:mutating-hooks",
      command: process.execPath,
      args: ensureArgs(),
      env: { SKIP: mutatingSkip.join(",") },
      mutating: true
    });
  }

  // 5. Mutating preflight-only extras (serial phase).
  for (const extra of PREFLIGHT_ONLY_EXTRAS.filter((entry) => entry.mutating === true)) {
    lanes.push({
      id: `extra:${extra.id}`,
      command: "npm",
      args: ["run", extra.npm],
      mutating: true
    });
  }

  return lanes;
}

/**
 * Run a list of lanes through a bounded concurrency pool. Each lane is spawned
 * via spawnPlatformCommand; output is captured per-lane and flushed atomically
 * (with an `[id]` prefix on failure) so concurrent lanes stay attributable. The
 * pool NEVER rejects and NEVER fail-fasts -- every lane runs so a single push
 * surfaces all failures at once.
 *
 * @param {Array} lanes Lanes to run.
 * @param {number} limit Max concurrent lanes.
 * @param {Function} spawnImpl Async spawn (injectable for tests).
 * @param {Function} writeLine Sink for streamed output (injectable for tests).
 * @returns {Promise<Array<{id:string, ok:boolean}>>} One result per lane.
 */
async function runPool(lanes, limit, spawnImpl, writeLine) {
  const results = new Array(lanes.length);
  let cursor = 0;

  const worker = async () => {
    while (cursor < lanes.length) {
      const index = cursor;
      cursor += 1;
      const lane = lanes[index];
      const outcome = await spawnImpl(lane.command, lane.args, {
        cwd: REPO_ROOT,
        encoding: "utf8",
        env: mergeSanitizedEnv(process.env, lane.env || {}, { removeKeys: ["SKIP"] }),
        stdio: ["ignore", "pipe", "pipe"]
      });

      const status = typeof outcome.status === "number" ? outcome.status : 1;
      const ok = status === 0 && !outcome.error;
      results[index] = { id: lane.id, ok };

      if (!ok) {
        const detail = `${outcome.stdout || ""}${outcome.stderr || ""}`.trim();
        const errSuffix = outcome.error ? ` (spawn error: ${outcome.error.message})` : "";
        writeLine(`[FAIL ${lane.id}] exit ${status}${errSuffix}`);
        if (detail) {
          writeLine(
            detail
              .split("\n")
              .map((line) => `  [${lane.id}] ${line}`)
              .join("\n")
          );
        }
      } else {
        writeLine(`[ok ${lane.id}]`);
      }
    }
  };

  const workerCount = Math.max(1, Math.min(limit, lanes.length));
  await Promise.all(Array.from({ length: workerCount }, () => worker()));
  return results;
}

/**
 * Default concurrency: leave one core free for the OS / the parent hook.
 *
 * @returns {number} Pool size (>= 1).
 */
function defaultConcurrency() {
  const cores =
    typeof os.availableParallelism === "function" ? os.availableParallelism() : os.cpus().length;
  return Math.max(1, cores - 1);
}

/**
 * Orchestrate the parallel pre-push parity run: mutating lanes serially first
 * (phase 0, so auto-fixers heal the tree before read-only checks observe it),
 * then read-only lanes through the pool (phase 1).
 *
 * @param {object} [deps] Injectable deps for tests.
 * @returns {Promise<number>} 0 when every lane passed, 1 otherwise.
 */
async function main(deps = {}) {
  const spawnImpl = deps.spawnImpl || spawnPlatformCommand;
  const writeLine = deps.writeLine || ((line) => process.stdout.write(`${line}\n`));
  const concurrency =
    typeof deps.concurrency === "number" ? deps.concurrency : defaultConcurrency();
  const lanes = buildLanes(deps);

  const mutating = lanes.filter((lane) => lane.mutating);
  const readOnly = lanes.filter((lane) => !lane.mutating);
  const heavy = readOnly.filter((lane) => lane.heavy);
  const lightReadOnly = readOnly.filter((lane) => !lane.heavy);
  // While a heavy lane (the full Jest suite) runs it uses ~every core for its
  // own worker pool, so the other read-only lanes run at a REDUCED concurrency
  // alongside it; a full-width pool would starve Jest into 2x its solo time.
  const lightConcurrency =
    heavy.length > 0 ? Math.max(2, Math.floor(concurrency / 3)) : concurrency;

  writeLine(
    `run-prepush-parallel: ${lanes.length} lanes ` +
      `(${mutating.length} mutating serial first, then ${heavy.length} heavy + ` +
      `${lightReadOnly.length} read-only @ concurrency ${lightConcurrency})`
  );

  const results = [];
  // Phase 0: mutating auto-fixers / formatters run FIRST, serially. This
  // preserves the serial preflight's fix-before-check ordering: a freshness
  // check whose repair counterpart is also a lane (e.g. the skills-index-check
  // hook vs the repair:llm-policy regen-and-stage extra) must see the
  // already-healed tree, so a stale-but-auto-recoverable input self-heals
  // instead of hard-failing the push (the zero-manual-touch invariant). Serial
  // among themselves => no two writers race the git index; before all readers
  // => no reader observes a partial write.
  results.push(...(await runPool(mutating, 1, spawnImpl, writeLine)));
  // Phase 1: read-only lanes -- the heavy Jest suite (each ~all cores) runs
  // CONCURRENTLY with a reduced-concurrency pool of the light read-only lanes,
  // so the cores are filled without starving the heavy lane. Runs after the
  // mutating phase, so every reader sees the healed/formatted tree.
  const phase1 = await Promise.all([
    runPool(heavy, 1, spawnImpl, writeLine),
    runPool(lightReadOnly, lightConcurrency, spawnImpl, writeLine)
  ]);
  results.push(...phase1.flat());

  const failed = results.filter((result) => !result.ok);
  if (failed.length > 0) {
    writeLine(`run-prepush-parallel: FAILED (${failed.map((entry) => entry.id).join(", ")})`);
    return 1;
  }

  writeLine(`run-prepush-parallel: all ${results.length} lanes passed`);
  return 0;
}

module.exports = {
  REPO_ROOT,
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
};

if (require.main === module) {
  main().then((code) => {
    process.exit(code);
  });
}
