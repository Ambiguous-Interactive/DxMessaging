#!/usr/bin/env node
/**
 * postinstall.js
 *
 * Runs validate-node-tooling.js after npm install to surface drift
 * (missing/broken Jest runner files, etc.) immediately. Always exits 0
 * so a non-fatal warning never blocks `npm install` on broken machines.
 * Cross-platform: uses Node only, no shell-specific syntax.
 */

"use strict";

const path = require("path");
const childProcess = require("child_process");

const VALIDATOR = path.join(__dirname, "validate-node-tooling.js");
const HOOK_INSTALLER = path.join(__dirname, "install-git-hooks.js");

function runNonFatal(label, scriptPath) {
  try {
    const result = childProcess.spawnSync(process.execPath, [scriptPath], { stdio: "inherit" });

    if (result.error) {
      console.warn(`postinstall: ${label} launch failed (${result.error.message}); continuing.`);
    } else if (typeof result.status === "number" && result.status !== 0) {
      console.warn(`postinstall: ${label} reported drift; install continues (non-fatal).`);
    }
  } catch (error) {
    console.warn(`postinstall: unexpected error running ${label} (${error.message}); continuing.`);
  }
}

runNonFatal("validate-node-tooling", VALIDATOR);
runNonFatal("install-git-hooks", HOOK_INSTALLER);

process.exit(0);
