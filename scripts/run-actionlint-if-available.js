#!/usr/bin/env node
"use strict";

const childProcess = require("child_process");

function main(argv = process.argv.slice(2)) {
  const result = childProcess.spawnSync("actionlint", argv, {
    stdio: "inherit"
  });

  if (result.error && result.error.code === "ENOENT") {
    process.stdout.write("actionlint not installed; skipping\n");
    return 0;
  }

  if (result.error) {
    process.stderr.write(`actionlint failed to start: ${result.error.message}\n`);
    return 1;
  }

  return typeof result.status === "number" ? result.status : 1;
}

module.exports = {
  main
};

if (require.main === module) {
  process.exit(main());
}
