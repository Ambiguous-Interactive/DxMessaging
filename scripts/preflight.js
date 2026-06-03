#!/usr/bin/env node
"use strict";

/**
 * preflight.js
 *
 * Change-aware preflight orchestrator (`npm run preflight`). It inspects
 * exactly what the current branch changed -- the committed range vs the
 * integration base PLUS staged/unstaged/untracked work -- and runs the
 * relevant lint / spelling / doc / changelog / YAML / policy checks IN-LOOP,
 * so failures surface before the git pre-push hook (which remains the
 * exhaustive, tool-agnostic backstop).
 *
 * Design (see /tmp/dx-design/finalDesign.md sections 3, 4, 5.3):
 *
 *   1. Auto-recovery first (unless `--no-recover`): `repairNodeTooling()` heals
 *      node_modules, then `ensurePreCommit()` reports whether pre-commit is
 *      usable.
 *   2. Compute the change-set via `computeChangeSet({ scope })`.
 *   3. pre-commit-present path (the common case): for each TARGETED stage run
 *        node scripts/ensure-pre-commit.js run --hook-stage <stage> \
 *            --from-ref <mergeBase> --to-ref HEAD     (committed range)
 *      AND
 *        node scripts/ensure-pre-commit.js run --hook-stage <stage> \
 *            --files <staged u unstaged u untracked>  (working tree)
 *      pre-commit performs ALL file -> hook selection. To avoid double-running
 *      `always_run` / whole-repo hooks, the two passes run only when BOTH a
 *      committed range and uncommitted files exist; otherwise only the
 *      relevant single pass runs (design 3.1).
 *   4. Node-direct fallback (no pre-commit / no Python): run the already-
 *      maintained npm `check:*` / `validate:*` entrypoints (NEVER parse hook
 *      `entry` strings). yamllint cannot run without Python -> emit a LOUD
 *      top-level WARNING. Policy/security hooks always run (they need no npm)
 *      and NEVER fail open.
 *
 * `--json` prints a machine-readable status object (design 5.3) whose exact
 * shape the enforcement hooks and contract tests depend on:
 *
 *   {
 *     "status": {
 *       "kind": "ok" | "checks-failed" | "infra-unavailable",
 *       "failures": ["<hookId>", ...],        // lint/spell/test/etc. failures
 *       "policyFailures": ["<hookId>", ...],  // policy/security; force checks-failed
 *       "warnings": ["<message>", ...]        // e.g. the yamllint skip
 *     },
 *     "scope": "full" | "worktree",
 *     "profile": "guard" | "full",
 *     "mode": "pre-commit" | "node-direct",
 *     "base": "<ref>" | null,
 *     "changedFileCount": <number>
 *   }
 *
 * Exit code is non-zero IFF `status.kind === "checks-failed"`.
 *
 * Flags:
 *   --profile=guard|full  guard runs the fast subset and DEFERS the heavy Jest
 *                         suites (script-tests / script-parser-tests /
 *                         unity-contract-tests) to the native pre-push hook;
 *                         full runs everything change-scoped. Default: full
 *                         when run directly.
 *   --scope=worktree|full worktree skips base resolution + the committed range
 *                         (fast on long branches); full includes them.
 *                         Default: full.
 *   --stage=<name>        restrict to one stage (default: the agent-relevant
 *                         stages pre-commit + pre-push present in the config).
 *   --base=<ref>          explicit integration base (CI passes the PR base).
 *   --files=<a,b,...>     explicit working-tree file list (comma or repeat).
 *   --all                 exhaustive: `pre-commit run --hook-stage <s>
 *                         --all-files` per stage (parity with the native hook).
 *   --json                emit the status object instead of human output.
 *   --no-recover          skip the auto-recovery bootstrap.
 *   --help                print usage and exit 0.
 *
 * All child-process spawns route through `spawnPlatformCommandSync`
 * (scripts/lib/shell-command.js); no raw spawn, no shell.
 */

const childProcess = require("child_process");
const path = require("path");
const { spawnPlatformCommandSync } = require("./lib/shell-command");
const { toPosixPath } = require("./lib/path-classifier");
const { computeChangeSet } = require("./lib/changed-files");
const { stagesInConfig, hookIdsForStage } = require("./lib/precommit-stage-model");
const { repairNodeTooling } = require("./repair-node-tooling");
const { ensurePreCommit } = require("./ensure-pre-commit");
const { healRegenerableCaches } = require("./lib/regenerable-cache-registry");

const REPO_ROOT = path.resolve(__dirname, "..");

/**
 * Stages an agent must clear before declaring done / pushing. Post-* stages
 * are git-event hooks (checkout/merge), not push-gating, so they are excluded
 * by default.
 */
const AGENT_STAGES = Object.freeze(["pre-commit", "pre-push"]);

/**
 * Policy / security hook ids. These run as pure Node with no npm/python
 * dependency, so they always run even in degraded (Node-direct) mode and their
 * failures NEVER fail open -- they populate `policyFailures[]` and force
 * `checks-failed` regardless of infra state (design 5.3 / P1-7).
 */
const POLICY_HOOK_IDS = Object.freeze([
  "validate-vscode-settings",
  "validate-untracked-policy",
  "validate-no-plan-vocabulary",
  "validate-pre-commit-tooling"
]);

/**
 * Heavy Jest suites deferred to the native pre-push hook under
 * `--profile=guard`. They run on `scripts/**` edits and execute 100+ files, so
 * paying them in-loop on every guard pass would defeat the "earlier feedback"
 * goal (design 5.4).
 */
const GUARD_DEFERRED_HOOK_IDS = Object.freeze([
  "script-parser-tests",
  "script-tests",
  "unity-contract-tests"
]);

/**
 * Synthetic failure id used when a pre-commit pass exits non-zero but its
 * output contains no parseable `- hook id:` line (config/internal error, a hook
 * crash, an unattributed failure, or human-mode inherited stdio). The process
 * exit code is the authoritative pass/fail signal, so an unattributed non-zero
 * pass must still force `checks-failed` (design 3.3) rather than being dropped
 * and reported as ok. It is intentionally NOT a policy id so it lands in
 * `failures[]` (not `policyFailures[]`).
 */
const PRE_COMMIT_INTERNAL_ERROR_ID = "pre-commit-internal-error";

/**
 * Node-direct fallback mapping: hook id -> the npm script / node command(s)
 * that cover it when pre-commit/Python is unavailable. We NEVER parse hook
 * `entry` strings; we route to the already-maintained entrypoints (design 4).
 *
 * Each command is `{ command, args, label }`; `command` is a base name fed to
 * spawnPlatformCommandSync. `gate` (optional) is an extension test -- the
 * command only runs when at least one changed file matches.
 */
const NODE_DIRECT_MAP = Object.freeze({
  // C# doc validators.
  "run-staged-validators": [
    {
      command: "node",
      args: ["scripts/run-staged-validators.js"],
      label: "run-staged-validators",
      gate: /\.cs$/i,
      passFiles: true
    }
  ],
  // Markdown pipeline. The pre-commit hook runs an in-place FIXER
  // (run-staged-md-pipeline.js writes files via prettier --write /
  // markdownlint --fix), but preflight (and the push-guard / Stop hook that
  // spawn it) MUST stay read-only and idempotent -- the guard's safety
  // rationale depends on that invariant, and a Node-direct run scopes to the
  // FULL change-set (committed range included), so a fixer would silently
  // rewrite committed markdown. We therefore route to the CHECK-ONLY
  // equivalents the fixer pipeline subsumes: the three doc validators (ASCII /
  // code-patterns / prose, all read-only by default) plus a prettier --check.
  // Markdownlint --fix has no read-only Node-direct entrypoint here; its
  // formatting parity is owned by the native pre-push hook + CI (which run the
  // real pipeline). None of these write to disk.
  "run-staged-md-pipeline": [
    {
      command: "node",
      args: ["scripts/validate-docs-ascii.js"],
      label: "validate-docs-ascii",
      gate: /\.(md|markdown)$/i,
      passFiles: true
    },
    {
      command: "node",
      args: ["scripts/validate-doc-code-patterns.js"],
      label: "validate-doc-code-patterns",
      gate: /\.(md|markdown)$/i,
      passFiles: true
    },
    {
      command: "node",
      args: ["scripts/validate-docs-prose.js"],
      label: "validate-docs-prose",
      gate: /\.(md|markdown)$/i,
      passFiles: true
    },
    {
      command: "node",
      args: ["scripts/run-managed-prettier.js", "--check"],
      label: "prettier (markdown)",
      gate: /\.(md|markdown)$/i,
      passFiles: true
    }
  ],
  // Spelling.
  cspell: [
    {
      command: "node",
      args: ["scripts/run-managed-cspell.js", "--no-progress", "--no-summary"],
      label: "cspell",
      gate: /\.(md|markdown|cs|json|ya?ml|ps1|js)$/i,
      passFiles: true
    }
  ],
  // Changelog.
  "validate-changelog-policy": [
    {
      command: "node",
      args: ["scripts/validate-changelog.js", "--check-coverage"],
      label: "validate-changelog-policy"
    }
  ],
  // npm meta.
  "validate-npm-meta": [
    {
      command: "node",
      args: ["scripts/validate-npm-meta.js", "--check"],
      label: "validate-npm-meta"
    }
  ],
  // Skills / llms.
  "validate-skills": [
    { command: "node", args: ["scripts/validate-skills.js"], label: "validate-skills" }
  ],
  "skills-index-check": [
    {
      command: "node",
      args: ["scripts/generate-skills-index.js", "--check"],
      label: "skills-index-check"
    }
  ],
  "skills-index-regen": [
    {
      command: "node",
      args: ["scripts/generate-skills-index.js", "--check"],
      label: "skills-index-regen"
    }
  ],
  "check-llms-txt-fresh": [
    {
      command: "node",
      args: ["scripts/update-llms-txt.js", "--check"],
      label: "check-llms-txt-fresh"
    }
  ],
  "update-llms-txt": [
    { command: "node", args: ["scripts/update-llms-txt.js", "--check"], label: "update-llms-txt" }
  ],
  // Policy / security (never skipped).
  "validate-vscode-settings": [
    {
      command: "node",
      args: ["scripts/validate-vscode-settings.js"],
      label: "validate-vscode-settings"
    }
  ],
  "validate-untracked-policy": [
    {
      command: "node",
      args: ["scripts/validate-untracked-policy.js"],
      label: "validate-untracked-policy"
    }
  ],
  "validate-no-plan-vocabulary": [
    {
      command: "node",
      args: ["scripts/validate-no-plan-vocabulary.js"],
      label: "validate-no-plan-vocabulary"
    }
  ],
  "validate-pre-commit-tooling": [
    {
      command: "node",
      args: ["scripts/validate-pre-commit-tooling.js"],
      label: "validate-pre-commit-tooling"
    }
  ],
  "validate-lychee-config": [
    {
      command: "node",
      args: ["scripts/validate-lychee-config.js"],
      label: "validate-lychee-config"
    }
  ],
  "validate-devcontainer-jsonc-usage": [
    {
      command: "node",
      args: ["scripts/validate-devcontainer-jsonc-usage.js"],
      label: "validate-devcontainer-jsonc-usage"
    }
  ],
  "validate-runtime-settings-docs": [
    {
      command: "node",
      args: ["scripts/validate-runtime-settings-docs.js"],
      label: "validate-runtime-settings-docs"
    }
  ],
  "validate-asmdef-references": [
    {
      command: "node",
      args: ["scripts/validate-asmdef-references.js"],
      label: "validate-asmdef-references"
    }
  ],
  // Workflows.
  actionlint: [
    { command: "node", args: ["scripts/run-actionlint-if-available.js"], label: "actionlint" }
  ],
  // Banner.
  "sync-banner-version": [
    { command: "node", args: ["scripts/validate-banner.js"], label: "validate-banner" }
  ],
  "validate-banner": [
    { command: "node", args: ["scripts/validate-banner.js"], label: "validate-banner" }
  ],
  // EOL / format / conflict markers.
  "eol-bom-check": [
    { command: "node", args: ["scripts/check-eol.js"], label: "eol-bom-check", passFiles: true }
  ],
  "conflict-markers": [
    {
      command: "node",
      args: ["scripts/check-conflict-markers.js"],
      label: "conflict-markers",
      passFiles: true
    }
  ],
  prettier: [
    {
      command: "node",
      args: ["scripts/run-managed-prettier.js", "--check"],
      label: "prettier",
      gate: /\.(json|asmdef|asmref|ya?ml)$/i,
      passFiles: true
    }
  ],
  "fix-eol": [
    { command: "node", args: ["scripts/check-eol.js"], label: "fix-eol", passFiles: true }
  ],
  "fix-pwsh-output-assertions": [
    {
      command: "node",
      args: ["scripts/fix-pwsh-output-assertions.js", "--check"],
      label: "fix-pwsh-output-assertions"
    }
  ],
  // YAML formatting (line-length / comments). yamllint itself cannot run.
  "fix-yaml-comments-line-length": [
    {
      command: "node",
      args: ["scripts/fix-yaml-comments-line-length.js", "--check", "--all-files"],
      label: "fix-yaml-comments-line-length",
      gate: /\.ya?ml$/i
    }
  ],
  "fix-yaml-block-scalar-line-length": [
    {
      command: "node",
      args: ["scripts/fix-yaml-block-scalar-line-length.js", "--check", "--all-files"],
      label: "fix-yaml-block-scalar-line-length",
      gate: /\.ya?ml$/i
    }
  ]
});

/**
 * Hook ids intentionally NOT covered by Node-direct mode, with the reason.
 *   - yamllint: requires pre-commit/Python; surfaced as a LOUD warning.
 *   - csharpier / dotnet-tool-restore: require the dotnet SDK; formatters whose
 *     parity is owned by the native hook.
 */
const NODE_DIRECT_EXEMPT = Object.freeze({
  yamllint: "requires pre-commit/Python; enforced by CI and the native pre-push hook",
  csharpier: "requires the dotnet SDK; formatter, native hook backstops",
  "dotnet-tool-restore": "requires the dotnet SDK; native hook backstops",
  "fix-csharp-underscore-methods": "auto-fixer owned by the commit-time hook; native hook backstops"
});

/**
 * Parse argv into an options object. Supports `--flag`, `--key=value`, and
 * `--key value` for the value-bearing flags.
 *
 * @param {string[]} argv Arguments (already sliced past node + script).
 * @returns {object} Parsed options.
 */
function parseArgs(argv) {
  const options = {
    profile: "full",
    scope: "full",
    stage: null,
    base: null,
    files: null,
    all: false,
    json: false,
    recover: true,
    help: false
  };

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const takeValue = (inlineValue) => {
      if (inlineValue !== undefined) {
        return inlineValue;
      }
      const next = argv[i + 1];
      i += 1;
      return next;
    };
    const eq = arg.indexOf("=");
    const key = eq === -1 ? arg : arg.slice(0, eq);
    const inline = eq === -1 ? undefined : arg.slice(eq + 1);

    switch (key) {
      case "--profile":
        options.profile = takeValue(inline) === "guard" ? "guard" : "full";
        break;
      case "--scope":
        options.scope = takeValue(inline) === "worktree" ? "worktree" : "full";
        break;
      case "--stage":
        options.stage = takeValue(inline) || null;
        break;
      case "--base":
        options.base = takeValue(inline) || null;
        break;
      case "--files": {
        const value = takeValue(inline);
        const parsed = String(value || "")
          .split(",")
          .map((file) => file.trim())
          .filter(Boolean);
        options.files = options.files ? options.files.concat(parsed) : parsed;
        break;
      }
      case "--all":
        options.all = true;
        break;
      case "--json":
        options.json = true;
        break;
      case "--no-recover":
        options.recover = false;
        break;
      case "--help":
      case "-h":
        options.help = true;
        break;
      default:
        // Unknown flags are ignored (forward-compatible with future flags).
        break;
    }
  }

  return options;
}

const USAGE = `Usage: node scripts/preflight.js [options]

Change-aware preflight: runs the lint/spelling/doc/changelog/YAML/policy
checks relevant to what this branch changed, delegating file->hook selection
to pre-commit when available (Node-direct fallback otherwise).

Options:
  --profile=guard|full   guard = fast subset (defers heavy Jest suites to the
                         native pre-push hook); full = everything (default).
  --scope=worktree|full  worktree = staged+unstaged+untracked only (skips the
                         committed range; fast on long branches);
                         full = committed range + working tree (default).
  --stage=<name>         restrict to one stage (default: pre-commit + pre-push).
  --base=<ref>           explicit integration base (CI passes the PR base).
  --files=<a,b,...>      explicit working-tree file list.
  --all                  exhaustive --all-files parity per stage.
  --json                 emit a machine-readable status object.
  --no-recover           skip the node_modules / pre-commit auto-recovery.
  --help                 print this help.

Exit code is non-zero only when checks fail (status.kind === "checks-failed").`;

/**
 * Spawn a child command through the cross-platform shape, returning the raw
 * spawn result. Pipes stdio by default so the orchestrator can parse output;
 * callers that want inherited stdio pass `stdio: "inherit"`.
 *
 * @param {string} command Base command name.
 * @param {string[]} args Arguments.
 * @param {object} [options] spawnSync option overrides.
 * @param {Function} [spawnSyncImpl] Injected spawnSync for tests.
 * @returns {object} spawnSync result.
 */
function runCommand(command, args, options = {}, spawnSyncImpl = childProcess.spawnSync) {
  return spawnPlatformCommandSync(
    command,
    args,
    {
      cwd: REPO_ROOT,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      ...options
    },
    spawnSyncImpl
  );
}

/**
 * Parse failing hook ids out of a `pre-commit run` stdout/stderr payload.
 * pre-commit prints one block per hook; a failing hook block contains a
 * `- hook id: <id>` line. We collect every hook id whose block also shows a
 * `Failed`/`exit code` outcome. Because the per-hook outcome marker
 * ("Passed"/"Failed") appears on the SAME header line as the hook name (not
 * the `- hook id:` line), we conservatively collect all `- hook id:` values
 * when the overall run failed -- the ids are advisory detail for the human /
 * JSON summary, and the authoritative pass/fail signal is the process exit
 * code.
 *
 * @param {string} output combined stdout+stderr.
 * @returns {string[]} hook ids mentioned in the output.
 */
function parseFailingHookIds(output) {
  const text = String(output || "");
  const ids = [];
  const seen = new Set();
  const re = /-\s*hook id:\s*([^\s]+)/g;
  let match;
  while ((match = re.exec(text)) !== null) {
    const id = match[1].trim();
    if (id && !seen.has(id)) {
      seen.add(id);
      ids.push(id);
    }
  }
  return ids;
}

/**
 * Combine a spawn result's stdout + stderr into one string.
 *
 * @param {object} result spawnSync result.
 * @returns {string}
 */
function combinedOutput(result) {
  if (!result) {
    return "";
  }
  return `${result.stdout ? String(result.stdout) : ""}\n${result.stderr ? String(result.stderr) : ""}`;
}

/**
 * Determine the stages to target.
 *
 * @param {object} options Parsed CLI options.
 * @param {Function} [stagesInConfigFn] Injected stage model.
 * @returns {string[]} ordered target stages.
 */
function resolveTargetStages(options, stagesInConfigFn = stagesInConfig) {
  const present = stagesInConfigFn();
  if (options.stage) {
    return present.has(options.stage) ? [options.stage] : [];
  }
  return AGENT_STAGES.filter((stage) => present.has(stage));
}

/**
 * Run the auto-recovery bootstrap (node_modules repair + pre-commit probe).
 * Returns the integrity result and the ensure-pre-commit result. Skipped when
 * `options.recover` is false (the guard passes --no-recover so recovery is not
 * paid twice in a session).
 *
 * @param {object} options Parsed CLI options.
 * @param {object} deps Injected dependencies.
 * @returns {{ integrity: object|null, preCommit: object, infraReasons: string[] }}
 */
function runRecovery(options, deps) {
  const {
    repairNodeToolingFn = repairNodeTooling,
    ensurePreCommitFn = ensurePreCommit,
    healRegenerableCachesFn = healRegenerableCaches,
    logFn = console.error,
    env = process.env
  } = deps;

  const infraReasons = [];
  let integrity = null;

  if (options.recover) {
    integrity = repairNodeToolingFn({ env });
    if (integrity && integrity.status !== 0) {
      const reason =
        integrity.gateResult && integrity.gateResult.reason
          ? integrity.gateResult.reason
          : "node_modules integrity gate failed";
      infraReasons.push(reason);
    }
  }

  // Heal regenerable caches UNCONDITIONALLY -- outside the `options.recover`
  // gate. The PreToolUse push-guard spawns preflight with --no-recover (so the
  // expensive npm-ci recovery above is skipped), but a corrupt isolated
  // managed-Jest cache must STILL be auto-cleared before the native pre-push
  // hook fires, or the guard would never heal this class. Justified: it is a
  // ~5ms tmpdir purge (no readdir/lock when the cache root is absent) entirely
  // unrelated to the npm-ci recovery that --no-recover deliberately skips.
  // Best-effort: a heal failure NEVER adds an infraReason or changes preflight
  // status (a regenerable artifact must not fail closed). The cache lives at a
  // '..'-prefixed path relative to repoRoot (outside the worktree), so purging
  // it mutates no tracked/committed file -- the guard/Stop read-only-to-the-repo
  // invariant holds. The default healRegenerableCaches catches per-healer throws
  // internally, but we wrap the call so the "never changes preflight status"
  // contract holds even if the orchestrator ITSELF throws (a future bug, or a
  // raw non-orchestrator healer injected by a future caller): a heal throw must
  // never fail-close the guard's read-only push check.
  try {
    healRegenerableCachesFn({ env, warnFn: logFn });
  } catch (error) {
    const detail = error && error.message ? error.message : String(error);
    logFn(`WARNING: Regenerable-cache heal orchestrator threw (best-effort, ignored): ${detail}`);
  }

  const preCommit = ensurePreCommitFn({ logFn, warnFn: logFn });
  if (!preCommit.ok && preCommit.reason) {
    infraReasons.push(`pre-commit unavailable: ${preCommit.reason}`);
  }

  return { integrity, preCommit, infraReasons };
}

/**
 * Run the pre-commit-present path: per-stage two-pass invocation with the
 * always_run/whole-repo dedupe. Returns aggregated failing hook ids and a
 * boolean for whether any pass failed.
 *
 * @param {object} ctx Execution context.
 * @returns {{ failedHookIds: string[], anyFailed: boolean }}
 */
function runPreCommitMode(ctx) {
  const { options, changeSet, stages, runCommandFn, logFn, env } = ctx;

  const childEnv = { ...env };
  // Honor the guard profile in pre-commit mode (the common path): defer the
  // heavy Jest suites to the native pre-push hook by passing them through
  // pre-commit's SKIP env var. Without this, `--profile=guard` is a no-op when
  // pre-commit is installed and the guard / advisory Stop hook would run the
  // multi-minute suites in-loop (design 5.4). `--all` (full parity) and
  // `--profile=full` keep running everything.
  if (options.profile === "guard" && !options.all) {
    const existingSkip = String(childEnv.SKIP || "")
      .split(",")
      .map((entry) => entry.trim())
      .filter(Boolean);
    childEnv.SKIP = [...new Set([...existingSkip, ...GUARD_DEFERRED_HOOK_IDS])].join(",");
  }
  const failedHookIds = [];
  let anyFailed = false;

  const recordFailure = (result) => {
    anyFailed = true;
    for (const id of parseFailingHookIds(combinedOutput(result))) {
      if (!failedHookIds.includes(id)) {
        failedHookIds.push(id);
      }
    }
  };

  const runPass = (stage, passArgs, label) => {
    const argv = ["run", "--hook-stage", stage, ...passArgs];
    logFn(`preflight: pre-commit ${stage} (${label})`);
    const result = runCommandFn("node", ["scripts/ensure-pre-commit.js", ...argv], {
      stdio: options.json ? ["ignore", "pipe", "pipe"] : "inherit",
      env: childEnv
    });
    // ensure-pre-commit returns 1 when pre-commit itself is unavailable; the
    // caller has already classified that as infra, so a non-zero here in the
    // pre-commit path means a hook failed.
    const status = result && typeof result.status === "number" ? result.status : 1;
    if (status !== 0) {
      recordFailure(result);
    }
  };

  const workingFiles = [
    ...new Set([
      ...changeSet.sources.staged,
      ...changeSet.sources.unstaged,
      ...changeSet.sources.untracked
    ])
  ].sort();
  const hasCommitted = changeSet.mergeBase && changeSet.sources.committed.length > 0;
  const hasWorking = workingFiles.length > 0;

  for (const stage of stages) {
    if (options.all) {
      runPass(stage, ["--all-files"], "all-files");
      continue;
    }

    // Two passes only when BOTH sides exist; otherwise the relevant single
    // pass (design 3.1 always_run dedupe).
    if (hasCommitted) {
      runPass(stage, ["--from-ref", changeSet.mergeBase, "--to-ref", "HEAD"], "committed-range");
    }
    if (hasWorking) {
      runPass(stage, ["--files", ...workingFiles], "working-tree");
    }
    if (!hasCommitted && !hasWorking) {
      logFn(`preflight: ${stage} has no changed files to check.`);
    }
  }

  return { failedHookIds, anyFailed };
}

/**
 * Decide whether a Node-direct command should run given the change-set. A
 * command with a `gate` regex runs only when at least one changed file
 * matches; otherwise it always runs.
 *
 * @param {object} command Node-direct command descriptor.
 * @param {string[]} files Changed files.
 * @returns {boolean}
 */
function nodeDirectCommandApplies(command, files) {
  if (!command.gate) {
    return true;
  }
  return files.some((file) => command.gate.test(file));
}

/**
 * Run the Node-direct fallback: route each targeted hook id to its npm /
 * node entrypoint, gate by the change-set, surface the loud yamllint skip, and
 * keep policy hooks always-on.
 *
 * @param {object} ctx Execution context.
 * @returns {{ failedHookIds: string[], policyFailedHookIds: string[], warnings: string[] }}
 */
function runNodeDirectMode(ctx) {
  const { options, changeSet, stages, runCommandFn, logFn, env } = ctx;
  const files = changeSet.files;
  const childEnv = { ...env };

  const failedHookIds = [];
  const policyFailedHookIds = [];
  const warnings = [];

  // Union of targeted hook ids across stages (config order preserved per stage).
  const targetedIds = [];
  const seenIds = new Set();
  for (const stage of stages) {
    for (const id of hookIdsForStage(stage)) {
      if (!seenIds.has(id)) {
        seenIds.add(id);
        targetedIds.push(id);
      }
    }
  }

  // Loud yamllint skip whenever a YAML file changed (or --all) and yamllint
  // was targeted but cannot run without Python.
  if (seenIds.has("yamllint")) {
    const yamlChanged = options.all || files.some((file) => /\.ya?ml$/i.test(file));
    if (yamlChanged) {
      const warning =
        "WARNING: yamllint requires pre-commit/Python and was SKIPPED; YAML lint is enforced by CI and the native pre-push hook on a Python-equipped machine. Changed YAML is still format+line-length checked here.";
      warnings.push(warning);
      logFn(`preflight: ${warning}`);
    }
  }

  for (const id of targetedIds) {
    if (GUARD_DEFERRED_HOOK_IDS.includes(id) && options.profile === "guard") {
      continue;
    }
    if (NODE_DIRECT_EXEMPT[id]) {
      continue;
    }
    const commands = NODE_DIRECT_MAP[id];
    if (!commands) {
      // No Node-direct coverage and not explicitly exempt: record a warning so
      // the gap is visible rather than silently passing.
      const warning = `WARNING: no Node-direct coverage for hook "${id}"; it is enforced by the native pre-push hook.`;
      warnings.push(warning);
      logFn(`preflight: ${warning}`);
      continue;
    }

    const isPolicy = POLICY_HOOK_IDS.includes(id);
    for (const command of commands) {
      // Policy hooks always run; non-policy commands gate on the change-set.
      if (!isPolicy && !options.all && !nodeDirectCommandApplies(command, files)) {
        continue;
      }
      // When a command both forwards files AND declares a `gate`, forward ONLY
      // the files matching that gate. Feeding a tool files outside its remit
      // (e.g. a `.cs` path to prettier --check) makes it error spuriously
      // ("No parser could be inferred"); a gated command must only ever see the
      // files it is meant to inspect.
      const passableFiles = command.gate ? files.filter((file) => command.gate.test(file)) : files;
      const args =
        command.passFiles && passableFiles.length > 0
          ? [...command.args, ...passableFiles]
          : command.args;
      logFn(`preflight: node-direct ${command.label}`);
      const result = runCommandFn(command.command, args, {
        stdio: options.json ? ["ignore", "pipe", "pipe"] : "inherit",
        env: childEnv
      });
      const status = result && typeof result.status === "number" ? result.status : 1;
      if (status !== 0) {
        if (isPolicy) {
          if (!policyFailedHookIds.includes(id)) {
            policyFailedHookIds.push(id);
          }
        } else if (!failedHookIds.includes(id)) {
          failedHookIds.push(id);
        }
      }
    }
  }

  return { failedHookIds, policyFailedHookIds, warnings };
}

/**
 * Build the final `status` object (design 5.3) from the per-mode results.
 *
 * `kind` precedence: checks-failed (any failures OR policyFailures) wins;
 * else infra-unavailable when an infra reason surfaced; else ok.
 *
 * @param {object} args
 * @returns {{ kind: string, failures: string[], policyFailures: string[], warnings: string[] }}
 */
function buildStatus({ failures = [], policyFailures = [], warnings = [], infraReasons = [] }) {
  const uniqueFailures = [...new Set(failures)];
  const uniquePolicy = [...new Set(policyFailures)];
  const uniqueWarnings = [...new Set(warnings)];

  let kind;
  if (uniqueFailures.length > 0 || uniquePolicy.length > 0) {
    kind = "checks-failed";
  } else if (infraReasons.length > 0) {
    kind = "infra-unavailable";
  } else {
    kind = "ok";
  }

  return {
    kind,
    failures: uniqueFailures,
    policyFailures: uniquePolicy,
    warnings: uniqueWarnings
  };
}

/**
 * Orchestrate a full preflight run. Returns the JSON-serializable report and an
 * exit code. Pure modulo the injected `deps`, so tests drive it with fakes.
 *
 * @param {object} [options] Parsed CLI options (see {@link parseArgs}).
 * @param {object} [deps] Injected dependencies.
 * @returns {{ report: object, exitCode: number }}
 */
function runPreflight(options = {}, deps = {}) {
  const {
    computeChangeSetFn = computeChangeSet,
    stagesInConfigFn = stagesInConfig,
    runCommandFn = runCommand,
    logFn = console.error,
    env = process.env
  } = deps;

  const recovery = runRecovery(options, deps);
  const stages = resolveTargetStages(options, stagesInConfigFn);

  const changeSet = computeChangeSetFn({ baseOverride: options.base, scope: options.scope });

  // Explicit --files overrides the working-tree sources (CI / guard reuse).
  if (Array.isArray(options.files)) {
    const explicit = [...new Set(options.files)].sort();
    changeSet.sources.staged = explicit;
    changeSet.sources.unstaged = [];
    changeSet.sources.untracked = [];
    changeSet.files = [...new Set([...changeSet.sources.committed, ...explicit])].sort();
  }

  let mode;
  let failures = [];
  let policyFailures = [];
  let warnings = [];

  const ctx = { options, changeSet, stages, runCommandFn, logFn, env };

  if (recovery.preCommit.ok) {
    mode = "pre-commit";
    const result = runPreCommitMode(ctx);
    failures = result.failedHookIds;
    // The AUTHORITATIVE pass/fail signal is the process exit code, not the
    // parsed hook ids. pre-commit can exit non-zero WITHOUT emitting a
    // `- hook id:` line (InvalidConfigError on a malformed config, an unstaged
    // `.pre-commit-config.yaml` under --from-ref, a hook crash, a future
    // failure-summary format, or human-mode stdio:"inherit" where stdout is
    // not captured). In all of those, `failedHookIds` is empty but
    // `anyFailed` is true. Synthesize a sentinel id so buildStatus yields
    // checks-failed / exit 1 -- otherwise a genuinely broken pre-commit run
    // would be reported as "ok" and the push-guard would ALLOW the push
    // (design 3.3: any pre-commit pass exiting non-zero -> preflight non-zero).
    if (result.anyFailed && failures.length === 0) {
      failures = [PRE_COMMIT_INTERNAL_ERROR_ID];
    }
    // In pre-commit mode, policy hooks run inside pre-commit; a policy failure
    // surfaces as a normal hook-id failure. Promote known policy ids so the
    // guard's never-fail-open logic still sees them.
    policyFailures = failures.filter((id) => POLICY_HOOK_IDS.includes(id));
    failures = failures.filter((id) => !POLICY_HOOK_IDS.includes(id));
  } else {
    mode = "node-direct";
    const result = runNodeDirectMode(ctx);
    failures = result.failedHookIds;
    policyFailures = result.policyFailedHookIds;
    warnings = result.warnings;
  }

  const status = buildStatus({
    failures,
    policyFailures,
    warnings,
    infraReasons: recovery.infraReasons
  });

  const report = {
    status,
    scope: changeSet.scope,
    profile: options.profile,
    mode,
    base: changeSet.base,
    changedFileCount: changeSet.files.length
  };

  const exitCode = status.kind === "checks-failed" ? 1 : 0;
  return { report, exitCode };
}

/**
 * Render a human-readable summary line for non-JSON runs.
 *
 * @param {object} report The preflight report.
 * @returns {string}
 */
function formatHumanSummary(report) {
  const { status } = report;
  const parts = [
    `preflight: ${status.kind} (mode=${report.mode}, scope=${report.scope}, profile=${report.profile}, files=${report.changedFileCount})`
  ];
  if (status.failures.length > 0) {
    parts.push(`  failures: ${status.failures.join(", ")}`);
  }
  if (status.policyFailures.length > 0) {
    parts.push(`  policy failures: ${status.policyFailures.join(", ")}`);
  }
  for (const warning of status.warnings) {
    parts.push(`  ${warning}`);
  }
  return parts.join("\n");
}

/**
 * CLI entrypoint.
 *
 * @param {string[]} [argv] Raw args (default process.argv slice).
 * @returns {number} exit code.
 */
function main(argv = process.argv.slice(2)) {
  const options = parseArgs(argv);

  if (options.help) {
    process.stdout.write(`${USAGE}\n`);
    return 0;
  }

  const { report, exitCode } = runPreflight(options);

  if (options.json) {
    process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
  } else {
    process.stderr.write(`${formatHumanSummary(report)}\n`);
  }

  return exitCode;
}

module.exports = {
  REPO_ROOT,
  AGENT_STAGES,
  POLICY_HOOK_IDS,
  GUARD_DEFERRED_HOOK_IDS,
  PRE_COMMIT_INTERNAL_ERROR_ID,
  NODE_DIRECT_MAP,
  NODE_DIRECT_EXEMPT,
  parseArgs,
  USAGE,
  runCommand,
  parseFailingHookIds,
  combinedOutput,
  resolveTargetStages,
  runRecovery,
  runPreCommitMode,
  nodeDirectCommandApplies,
  runNodeDirectMode,
  buildStatus,
  runPreflight,
  formatHumanSummary,
  toPosixPath,
  main
};

if (require.main === module) {
  process.exit(main());
}
