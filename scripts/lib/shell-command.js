"use strict";

const childProcess = require("child_process");

/**
 * Determine whether a command uses Windows shell shims.
 *
 * @param {string} command - Base command name
 * @returns {boolean} True when command is npm/npx
 */
function isShellShimCommand(command) {
  return command === "npm" || command === "npx";
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
 * than from raw command names (`"npm"`, `"npm.cmd"`). Because production
 * (spawnPlatformCommandSync) and the test expectation both flow through this
 * one function, the assertion can never drift from production across
 * platforms, and forcing `platform="win32"` exercises the Windows branch on a
 * Linux/macOS host (where a host-only assertion would silently rot).
 *
 * @param {string} command - Base command name (for example "npm")
 * @param {string[]} args - Command arguments
 * @param {object} options - spawnSync options
 * @param {string} platform - Process platform string
 * @returns {{command: string, args: string[], options: object}} Resolved spawn triple
 */
function buildSpawnInvocation(command, args = [], options = {}, platform = process.platform) {
  let resolvedCommand = command;
  let resolvedArgs = args;
  const resolvedOptions = { ...options };

  if (platform === "win32" && isShellShimCommand(command)) {
    // Batch-file shims must never run with shell:true (CVE-2024-27980);
    // wrap them in the command interpreter explicitly instead.
    resolvedOptions.shell = false;
    if (resolvedOptions.windowsHide === undefined) {
      resolvedOptions.windowsHide = true;
    }
    resolvedArgs = ["/d", "/s", "/c", `${command}.cmd`, ...args];
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

module.exports = {
  buildSpawnInvocation,
  spawnPlatformCommandSync
};
