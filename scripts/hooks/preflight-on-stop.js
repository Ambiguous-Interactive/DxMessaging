#!/usr/bin/env node
"use strict";

/**
 * preflight-on-stop.js
 *
 * Claude Code Stop hook. When an agent ends a turn ("declaring done"), this
 * hook runs the change-aware preflight against the WORKING TREE ONLY
 * (`--scope=worktree`: staged + unstaged + untracked; it NEVER resolves an
 * integration base or scans the committed range, so it stays fast even on a
 * many-commit branch) and, if checks fail, surfaces an ADVISORY warning so the
 * agent learns -- in-loop -- that there is something to fix before truly
 * finishing. It covers the "edited a file, said done, never pushed" path that
 * the push-only PreToolUse guard cannot see.
 *
 * OWNER DECISION ("Block push + advisory Stop"): this hook is ADVISORY ONLY. It
 * MUST NEVER emit `decision: "block"`. It emits a `systemMessage` /
 * `additionalContext` warning and ALWAYS exits 0, so an end-of-turn check
 * failure (or an infra problem) never traps the agent. The blocking enforcement
 * lives on the PreToolUse push-guard (full scope) and the native pre-push hook.
 *
 * It reuses ONLY the I/O harness of yaml-line-length-guard.js (stdin JSON read,
 * `$CLAUDE_PROJECT_DIR` root resolution, pure Node, no deps).
 *
 * Contract:
 *   1. Read the Stop event JSON from stdin (the payload is not otherwise used).
 *   2. Re-entrancy: if `DXMSG_PREFLIGHT_ACTIVE === "1"` -> exit 0 silently.
 *   3. Spawn `node scripts/preflight.js --json --profile=guard --scope=worktree`
 *      with `DXMSG_PREFLIGHT_ACTIVE=1` in the child env.
 *   4. {@link buildAdvisory} maps the preflight `status` to an advisory message
 *      (or null when there is nothing to say). On `checks-failed` / any
 *      `policyFailures`, emit a `systemMessage` naming the failing hooks + the
 *      `npm run preflight` remediation. On `ok` / `infra-unavailable`, stay
 *      silent.
 *   5. ALWAYS exit 0; NEVER emit `decision: "block"`.
 *
 * All child-process spawns route through `spawnPlatformCommandSync`
 * (scripts/lib/shell-command.js); no raw spawn, no shell.
 */

const path = require("path");
const { spawnPlatformCommandSync } = require("../lib/shell-command");

/**
 * Advisory remediation pointer (the Stop hook never blocks, so this is guidance
 * the agent can act on before declaring the task done).
 */
const REMEDIATION =
  "Run `npm run preflight` to reproduce and auto-fix these before declaring the " +
  "task done; the change is not push-clean yet.";

/**
 * Self-imposed ceiling (ms) on the preflight child. The advisory Stop hook runs
 * a fast, worktree-scoped preflight at every turn end; capping its runtime keeps
 * the agent loop responsive and makes degradation DETERMINISTIC and
 * version-independent (independent of the Claude Code framework's own per-hook
 * timeout). On timeout the child is killed, no parseable JSON is produced, and
 * the hook stays SILENT (it is advisory and never blocks). Override with
 * DXMSG_PREFLIGHT_HOOK_TIMEOUT_MS.
 */
const PREFLIGHT_TIMEOUT_MS = (() => {
  const raw = Number.parseInt(process.env.DXMSG_PREFLIGHT_HOOK_TIMEOUT_MS || "", 10);
  return Number.isFinite(raw) && raw > 0 ? raw : 45000;
})();

/**
 * Read stdin to completion as a UTF-8 string.
 *
 * @returns {Promise<string>} The full stdin payload.
 */
function readStdin() {
  return new Promise((resolve) => {
    let data = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => {
      data += chunk;
    });
    process.stdin.on("end", () => resolve(data));
    process.stdin.on("error", () => resolve(data));
  });
}

/**
 * Resolve the repository root. Prefers the Claude Code project-dir env var;
 * falls back to the repo root relative to this script (scripts/hooks/..).
 *
 * @returns {string} Absolute repository root.
 */
function resolveRepoRoot() {
  if (process.env.CLAUDE_PROJECT_DIR && process.env.CLAUDE_PROJECT_DIR.trim().length > 0) {
    return process.env.CLAUDE_PROJECT_DIR;
  }
  return path.resolve(__dirname, "..", "..");
}

/**
 * Re-entrancy check: true when preflight is already running in this process
 * tree (the sentinel env var is set).
 *
 * @param {object} [env] Environment to inspect (default `process.env`).
 * @returns {boolean} True when the hook should skip.
 */
function shouldSkip(env = process.env) {
  return !!env && env.DXMSG_PREFLIGHT_ACTIVE === "1";
}

/**
 * Build the advisory message from a preflight `status`, or null when there is
 * nothing to advise. ADVISORY ONLY -- this never produces a blocking decision.
 *
 * On `checks-failed` / any `policyFailures`, returns a message naming the
 * failing hooks plus the remediation. On `ok` and `infra-unavailable`, returns
 * null (infra problems must not nag the agent at end-of-turn).
 *
 * @param {{kind: string, failures?: string[], policyFailures?: string[]}} status
 *   Preflight status object.
 * @returns {string|null} The advisory message, or null when silent.
 */
function buildAdvisory(status) {
  const safe = status && typeof status === "object" ? status : { kind: "ok" };
  const failures = Array.isArray(safe.failures) ? safe.failures : [];
  const policyFailures = Array.isArray(safe.policyFailures) ? safe.policyFailures : [];

  const blocks = safe.kind === "checks-failed" || policyFailures.length > 0;
  if (!blocks) {
    return null;
  }

  const allFailing = [...policyFailures, ...failures];
  const named = allFailing.length > 0 ? allFailing.join(", ") : "(see preflight output)";
  return (
    `Advisory: change-aware preflight (working tree) reports failing hook(s): ` +
    `${named}. ${REMEDIATION}`
  );
}

/**
 * Spawn the change-aware preflight in guard profile / worktree scope and parse
 * its `--json` report. Returns the parsed report, or null when preflight could
 * not be spawned or produced no parseable JSON.
 *
 * @param {string} repoRoot Absolute repo root (child cwd).
 * @param {object} [deps] Injected dependencies (spawn fn + base env) for tests.
 * @returns {object|null} The parsed preflight report, or null.
 */
function runStopPreflight(repoRoot, deps = {}) {
  const { spawnFn = spawnPlatformCommandSync, env = process.env } = deps;

  const result = spawnFn(
    "node",
    ["scripts/preflight.js", "--json", "--profile=guard", "--scope=worktree"],
    {
      cwd: repoRoot,
      encoding: "utf8",
      stdio: ["ignore", "pipe", "pipe"],
      env: { ...env, DXMSG_PREFLIGHT_ACTIVE: "1" },
      timeout: PREFLIGHT_TIMEOUT_MS,
      killSignal: "SIGTERM",
      maxBuffer: 16 * 1024 * 1024
    }
  );

  // Prefer the report whenever preflight emitted parseable JSON. On a timeout
  // (SIGTERM / ETIMEDOUT), spawn failure, or no parseable report, fall through
  // to null so the hook stays silent -- advisory only, never blocks.
  if (!result) {
    return null;
  }

  const stdout = result.stdout ? String(result.stdout) : "";
  const start = stdout.indexOf("{");
  if (start === -1) {
    return null;
  }
  try {
    return JSON.parse(stdout.slice(start));
  } catch (_error) {
    return null;
  }
}

/**
 * Main hook entry. Reads the Stop payload, runs the worktree-scoped preflight,
 * and emits an advisory `systemMessage` on failure. ALWAYS exits 0 and NEVER
 * blocks.
 *
 * @param {string} stdinPayload Raw Stop JSON from stdin.
 * @param {object} [deps] Injected dependencies for tests.
 * @returns {number} Process exit code (always 0).
 */
function run(stdinPayload, deps = {}) {
  const { env = process.env, repoRoot = resolveRepoRoot() } = deps;

  // The Stop payload is read for harness symmetry / robustness; an unparseable
  // payload simply means we stay silent.
  try {
    JSON.parse(stdinPayload);
  } catch (_error) {
    return 0;
  }

  if (shouldSkip(env)) {
    return 0;
  }

  const report = runStopPreflight(repoRoot, deps);
  if (!report || !report.status) {
    // Could not run / parse preflight: stay silent (infra), never block.
    return 0;
  }

  const message = buildAdvisory(report.status);
  if (message === null) {
    return 0;
  }

  // Advisory only: systemMessage + additionalContext, NEVER decision:"block".
  const output = {
    systemMessage: message,
    hookSpecificOutput: {
      hookEventName: "Stop",
      additionalContext: message
    }
  };
  process.stdout.write(`${JSON.stringify(output)}\n`);
  return 0;
}

module.exports = {
  REMEDIATION,
  resolveRepoRoot,
  shouldSkip,
  buildAdvisory,
  runStopPreflight,
  run
};

if (require.main === module) {
  readStdin().then((payload) => {
    process.exit(run(payload));
  });
}
