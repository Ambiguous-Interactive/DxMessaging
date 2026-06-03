"use strict";

const childProcess = require("child_process");

/**
 * Convert a command name to the platform-specific executable name.
 * npm/npx on Windows are exposed as .cmd shims.
 *
 * This helper only adjusts the command token; it does NOT model the win32
 * `cmd.exe /d /s /c` wrapping that spawnPlatformCommandSync() applies. Using
 * its return value as an expected spawn command in a test therefore drifts
 * from production on Windows and silently passes on Linux/macOS.
 *
 * @deprecated For child_process calls, use spawnPlatformCommandSync(). For
 *   computing expected spawn shapes in tests, use buildSpawnInvocation() so
 *   the assertion tracks production on every platform.
 *
 * @param {string} command - Base command name (for example "npm")
 * @param {string} platform - Process platform string
 * @returns {string} Command adjusted for platform execution
 */
function toShellCommand(command, platform = process.platform) {
  return platform === "win32" ? `${command}.cmd` : command;
}

/**
 * Determine whether a command uses Windows shell shims.
 *
 * @param {string} command - Base command name
 * @returns {boolean} True when command is npm/npx
 */
function isShellShimCommand(command) {
  return command === "npm" || command === "npx";
}

function matchingEnvKeys(env, key) {
  const target = String(key).toLowerCase();
  return Object.keys(env).filter((existing) => existing.toLowerCase() === target);
}

function hasNonEmptyEnvValue(env, key) {
  return matchingEnvKeys(env, key).some((existing) => String(env[existing]).length > 0);
}

function deleteEnvKey(env, key) {
  for (const existing of matchingEnvKeys(env, key)) {
    delete env[existing];
  }
}

/**
 * Remove Node's noisy NO_COLOR/FORCE_COLOR conflict while preserving the
 * caller's explicit FORCE_COLOR behavior. Node writes a process-warning to
 * stderr when both variables are non-empty, which breaks JSON/stdout-only
 * subprocess contracts in hooks and tests.
 *
 * @param {object} baseEnv Environment object to copy.
 * @returns {object} Sanitized environment copy.
 */
function normalizeNodeColorEnv(baseEnv = process.env) {
  const env = { ...baseEnv };
  const hasNoColor = hasNonEmptyEnvValue(env, "NO_COLOR");
  const hasForceColor = hasNonEmptyEnvValue(env, "FORCE_COLOR");

  if (hasNoColor && hasForceColor) {
    deleteEnvKey(env, "NO_COLOR");
  }

  return env;
}

function mergeSanitizedEnv(baseEnv = process.env, overrides = {}, { removeKeys = [] } = {}) {
  const env = normalizeNodeColorEnv(baseEnv);
  for (const key of removeKeys) {
    deleteEnvKey(env, key);
  }
  return normalizeNodeColorEnv({ ...env, ...overrides });
}

/**
 * Resolve a platform-aware command token for spawn-style execution.
 *
 * @param {string} command - Base command name
 * @param {string} platform - Process platform string
 * @returns {string} Resolved command name
 */
function resolveSpawnCommand(command, platform = process.platform) {
  if (platform === "win32" && isShellShimCommand(command)) {
    return toShellCommand(command, platform);
  }

  return command;
}

/**
 * Resolve platform-aware spawn options.
 *
 * Windows npm/npx shims are batch files and must run with shell enabled.
 * Keep this logic centralized so callers cannot forget it.
 *
 * @param {string} command - Base command name
 * @param {object} options - Existing spawn options
 * @param {string} platform - Process platform string
 * @returns {object} Resolved spawn options
 */
function resolveSpawnOptions(command, options = {}, platform = process.platform) {
  const resolvedOptions = { ...options };

  if (platform === "win32" && isShellShimCommand(command)) {
    resolvedOptions.shell = false;

    if (resolvedOptions.windowsHide === undefined) {
      resolvedOptions.windowsHide = true;
    }
  }

  return resolvedOptions;
}

/**
 * Compute the exact `(command, args, options)` triple that
 * spawnPlatformCommandSync() hands to `spawnSync` for a given command on a
 * given platform.
 *
 * This is the SINGLE SOURCE OF TRUTH for spawn invocation shape. On win32 the
 * npm/npx shims are batch files, so to avoid Node CVE-2024-27980 they are run
 * through the command interpreter explicitly:
 *   `<ComSpec> /d /s /c npm.cmd ...args` with `shell:false`, `windowsHide:true`.
 * On non-win32 (or for non-shim commands like `git`) the call is a passthrough.
 *
 * Tests MUST derive their expected spawn assertions from this function rather
 * than from raw command names (`"npm"`, `"npm.cmd"`, `toShellCommand(...)`).
 * Because production (spawnPlatformCommandSync) and the test expectation both
 * flow through this one function, the assertion can never drift from production
 * across platforms, and forcing `platform="win32"` exercises the Windows branch
 * on a Linux/macOS host (where a host-only assertion would silently rot).
 *
 * @param {string} command - Base command name (for example "npm")
 * @param {string[]} args - Command arguments
 * @param {object} options - spawnSync options
 * @param {string} platform - Process platform string
 * @returns {{command: string, args: string[], options: object}} Resolved spawn triple
 */
function buildSpawnInvocation(command, args = [], options = {}, platform = process.platform) {
  let resolvedCommand = resolveSpawnCommand(command, platform);
  let resolvedArgs = args;
  const resolvedOptions = resolveSpawnOptions(command, options, platform);

  if (platform === "win32" && isShellShimCommand(command)) {
    resolvedArgs = ["/d", "/s", "/c", resolvedCommand, ...args];
    resolvedCommand = process.env.ComSpec || "cmd.exe";
  }

  return { command: resolvedCommand, args: resolvedArgs, options: resolvedOptions };
}

/**
 * Spawn a platform-aware child process.
 *
 * The invocation shape is computed by buildSpawnInvocation() so production and
 * test expectations share exactly one code path.
 *
 * @param {string} command - Base command name
 * @param {string[]} args - Command arguments
 * @param {object} options - spawnSync options
 * @param {Function} spawnSyncImpl - Optional spawnSync implementation for tests
 * @param {string} platform - Process platform string
 * @returns {object} spawnSync result object
 */
function spawnPlatformCommandSync(
  command,
  args = [],
  options = {},
  spawnSyncImpl = childProcess.spawnSync,
  platform = process.platform
) {
  const invocation = buildSpawnInvocation(command, args, options, platform);

  return spawnSyncImpl(invocation.command, invocation.args, invocation.options);
}

/**
 * Async sibling of spawnPlatformCommandSync. Spawns a platform-aware child
 * process and resolves with a spawnSync-shaped result
 * (`{status, signal, stdout, stderr, error}`). The invocation shape is computed
 * by the SAME buildSpawnInvocation() as the sync helper, so the Windows
 * `<ComSpec> /d /s /c npm.cmd ...` wrapping and `windowsHide` are identical to
 * production and to the cross-platform test expectations -- no drift.
 *
 * Used by the parallel pre-push orchestrator (a Promise pool of these). stdout
 * and stderr are captured to strings (the orchestrator buffers per-lane output
 * and flushes it atomically so concurrent lanes stay attributable). Pass
 * `options.encoding` (default "utf8") to control decoding. This NEVER sets
 * `shell:true`; argv arrays are passed through unescaped exactly like the sync
 * path.
 *
 * @param {string} command - Base command name (for example "node", "npm")
 * @param {string[]} args - Command arguments
 * @param {object} options - child_process.spawn options (cwd, env, encoding...)
 * @param {Function} spawnImpl - Optional spawn implementation for tests
 * @param {string} platform - Process platform string
 * @returns {Promise<{status:(number|null), signal:(string|null), stdout:string,
 *   stderr:string, error:(Error|null)}>} Resolves (never rejects) with the
 *   process outcome; spawn errors are surfaced as `error`.
 */
function spawnPlatformCommand(
  command,
  args = [],
  options = {},
  spawnImpl = childProcess.spawn,
  platform = process.platform
) {
  const { encoding = "utf8", ...spawnOptions } = options;
  const invocation = buildSpawnInvocation(command, args, spawnOptions, platform);

  return new Promise((resolve) => {
    let child;
    try {
      child = spawnImpl(invocation.command, invocation.args, invocation.options);
    } catch (error) {
      resolve({ status: null, signal: null, stdout: "", stderr: "", error });
      return;
    }

    let stdout = "";
    let stderr = "";
    let settled = false;
    const settle = (result) => {
      if (settled) {
        return;
      }
      settled = true;
      resolve(result);
    };

    if (child.stdout) {
      child.stdout.setEncoding(encoding);
      child.stdout.on("data", (chunk) => {
        stdout += chunk;
      });
    }
    if (child.stderr) {
      child.stderr.setEncoding(encoding);
      child.stderr.on("data", (chunk) => {
        stderr += chunk;
      });
    }

    child.on("error", (error) => {
      settle({ status: null, signal: null, stdout, stderr, error });
    });
    child.on("close", (status, signal) => {
      settle({ status, signal: signal || null, stdout, stderr, error: null });
    });
  });
}

module.exports = {
  toShellCommand,
  isShellShimCommand,
  normalizeNodeColorEnv,
  mergeSanitizedEnv,
  resolveSpawnCommand,
  resolveSpawnOptions,
  buildSpawnInvocation,
  spawnPlatformCommandSync,
  spawnPlatformCommand
};
