#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");

const { toPosixPath } = require("./lib/path-classifier");

const FORCE_DELETE_FLAG = "--force-delete-managed-runner";

function resolveManagedRunnerPath(moduleResolver = require.resolve) {
  return moduleResolver("jest-circus/runner");
}

function isManagedRunnerPathSafe(runnerPath, { pathModule = path } = {}) {
  if (typeof runnerPath !== "string" || runnerPath.trim().length === 0) {
    return false;
  }

  const normalizedPath = pathModule.resolve(runnerPath).replace(/\\/g, "/");
  const comparisonPath =
    process.platform === "win32" ? normalizedPath.toLowerCase() : normalizedPath;
  const runnerFilePattern = /\/runner\.(?:[cm]?js)$/;

  return (
    comparisonPath.includes("/node_modules/") &&
    comparisonPath.includes("/jest-circus/") &&
    runnerFilePattern.test(comparisonPath)
  );
}

function assertManagedRunnerPathSafe(runnerPath, { pathModule = path } = {}) {
  if (!isManagedRunnerPathSafe(runnerPath, { pathModule })) {
    throw new Error(
      `Refusing to delete unexpected runner path outside managed jest-circus target: ${toPosixPath(runnerPath)}`
    );
  }
}

function hasForcedDeletionOptIn(argv = process.argv.slice(2)) {
  return Array.isArray(argv) && argv.includes(FORCE_DELETE_FLAG);
}

function removeManagedRunner(
  runnerPath,
  {
    existsSyncFn = fs.existsSync,
    rmSyncFn = fs.rmSync,
    logFn = console.log,
    pathModule = path
  } = {}
) {
  assertManagedRunnerPathSafe(runnerPath, { pathModule });

  logFn(`Deleting managed Jest runner: ${toPosixPath(runnerPath)}`);

  if (!existsSyncFn(runnerPath)) {
    throw new Error("Runner path does not exist before deletion.");
  }

  rmSyncFn(runnerPath);

  if (existsSyncFn(runnerPath)) {
    throw new Error("Runner path still exists after deletion.");
  }
}

function verifyManagedJestFallback(options = {}) {
  const { moduleResolver = require.resolve, existsSyncFn, rmSyncFn, logFn, pathModule } = options;

  const runnerPath = resolveManagedRunnerPath(moduleResolver);
  removeManagedRunner(runnerPath, {
    existsSyncFn,
    rmSyncFn,
    logFn,
    pathModule
  });

  return runnerPath;
}

function main(
  argv = process.argv.slice(2),
  { verifyManagedJestFallbackFn = verifyManagedJestFallback } = {}
) {
  if (!hasForcedDeletionOptIn(argv)) {
    throw new Error(
      `Refusing to delete managed Jest runner without explicit opt-in. Re-run with ${FORCE_DELETE_FLAG}.`
    );
  }

  verifyManagedJestFallbackFn();
}

module.exports = {
  FORCE_DELETE_FLAG,
  resolveManagedRunnerPath,
  isManagedRunnerPathSafe,
  assertManagedRunnerPathSafe,
  hasForcedDeletionOptIn,
  removeManagedRunner,
  verifyManagedJestFallback,
  main
};

if (require.main === module) {
  try {
    main();
  } catch (error) {
    console.error(error.message || error);
    process.exit(1);
  }
}
