#!/usr/bin/env node
"use strict";

/**
 * Serial, on-demand pre-push parity command.
 *
 * Keep this as the package.json `preflight:pre-push` target instead of a raw
 * shell `&&` chain so the exhaustive CI/manual validation steps stay
 * cross-platform and centrally testable.
 */

const path = require("path");
const { mergeSanitizedEnv, spawnPlatformCommandSync } = require("./lib/shell-command");

const REPO_ROOT = path.resolve(__dirname, "..");

const STEPS = Object.freeze([
  { command: "npm", args: ["run", "preflight:pre-commit"] },
  { command: "npm", args: ["run", "check:cspell:all"] },
  {
    command: process.execPath,
    args: ["scripts/ensure-pre-commit.js", "run", "--hook-stage", "pre-push", "--all-files"]
  }
]);

function runStep(step, deps = {}) {
  const spawnFn = deps.spawnFn || spawnPlatformCommandSync;
  const result = spawnFn(step.command, step.args, {
    cwd: REPO_ROOT,
    env: mergeSanitizedEnv(process.env, step.env || {}, { removeKeys: ["SKIP"] }),
    stdio: "inherit"
  });

  if (result.error && result.error.code === "ENOENT") {
    process.stderr.write(`Unable to find required command '${step.command}'.\n`);
    return 127;
  }

  return typeof result.status === "number" ? result.status : 1;
}

function main(deps = {}) {
  for (const step of STEPS) {
    const status = runStep(step, deps);
    if (status !== 0) {
      return status;
    }
  }
  return 0;
}

module.exports = {
  REPO_ROOT,
  STEPS,
  runStep,
  main
};

if (require.main === module) {
  process.exit(main());
}
