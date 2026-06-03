#!/usr/bin/env node
"use strict";

const path = require("path");
const childProcess = require("child_process");
const { spawnPlatformCommandSync } = require("./lib/shell-command");

const SCRIPT_DIR = __dirname;

function run(command, args) {
  return spawnPlatformCommandSync(command, args, { stdio: "inherit" }, childProcess.spawnSync);
}

function tryRun(command, args) {
  const result = run(command, args);
  if (result.error && result.error.code === "ENOENT") {
    return { attempted: false, status: 127 };
  }
  return {
    attempted: true,
    status: typeof result.status === "number" ? result.status : 1
  };
}

function main() {
  const ps1 = path.join(SCRIPT_DIR, "sync-banner-version.ps1");
  const js = path.join(SCRIPT_DIR, "sync-banner-version.js");

  for (const candidate of [
    ["pwsh", ["-NoProfile", "-File", ps1]],
    ["powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps1]],
    [process.execPath, [js]]
  ]) {
    const result = tryRun(candidate[0], candidate[1]);
    if (!result.attempted) {
      continue;
    }
    return result.status;
  }

  process.stderr.write("sync-banner-version-hook: neither PowerShell nor Node.js is available.\n");
  return 1;
}

module.exports = {
  tryRun,
  main
};

if (require.main === module) {
  process.exit(main());
}
