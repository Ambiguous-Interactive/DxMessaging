#!/usr/bin/env node
"use strict";

const childProcess = require("child_process");
const { spawnPlatformCommandSync } = require("./lib/shell-command");

function splitArgs(argv) {
  const separator = argv.indexOf("--");
  if (separator === -1) {
    return { command: argv, files: [] };
  }
  return {
    command: argv.slice(0, separator),
    files: argv.slice(separator + 1)
  };
}

function run(command, args, options = {}) {
  return spawnPlatformCommandSync(
    command,
    args,
    {
      stdio: "inherit",
      ...options
    },
    childProcess.spawnSync
  );
}

function runQuiet(command, args, options = {}) {
  return spawnPlatformCommandSync(
    command,
    args,
    {
      stdio: ["ignore", "ignore", "ignore"],
      ...options
    },
    childProcess.spawnSync
  );
}

function main(argv = process.argv.slice(2)) {
  const { command, files } = splitArgs(argv);
  if (command.length === 0) {
    process.stderr.write("run-and-restage: missing command before --.\n");
    return 1;
  }

  const result = run(command[0], [...command.slice(1), ...files]);
  if (result.error) {
    process.stderr.write(`run-and-restage: failed to run ${command[0]}: ${result.error.message}\n`);
    return 1;
  }
  if (result.status !== 0) {
    return typeof result.status === "number" ? result.status : 1;
  }

  if (files.length === 0) {
    return 0;
  }

  const diffResult = runQuiet("git", ["diff", "--quiet", "--", ...files]);
  if (diffResult.status === 0) {
    return 0;
  }

  const addResult = run("git", ["add", "--", ...files]);
  if (addResult.error) {
    process.stderr.write(`run-and-restage: failed to restage files: ${addResult.error.message}\n`);
    return 1;
  }
  return addResult.status === 0 ? 0 : 1;
}

module.exports = {
  splitArgs,
  main
};

if (require.main === module) {
  process.exit(main());
}
