#!/usr/bin/env node
"use strict";

/**
 * validate-analyzer-build-contract.js
 *
 * Fast, read-only, plain-Node (NO jest) validator for the SourceGenerators build
 * config contract. It is the EDIT-TIME counterpart of
 * scripts/__tests__/analyzer-payload-reproducibility.test.js: both consume the
 * shared checks in scripts/lib/analyzer-build-contract.js, so the contract lives
 * in exactly one place.
 *
 * The post-edit-validate-guard runs this whenever a SourceGenerators
 * .props/.csproj/.targets file is edited, so a build-config regression surfaces
 * in-loop (well under 1s) instead of slipping through to the last-resort
 * pre-push jest sweep.
 *
 * Exit 0 when every check passes; exit 1 with concise diagnostics otherwise.
 * Cross-platform: pure Node, no spawning, no shell.
 */

const path = require("path");
const { evaluateContract } = require("./lib/analyzer-build-contract");

const REPO_ROOT = path.join(__dirname, "..");

/**
 * Run the contract and print failures.
 *
 * @param {string} repoRoot Absolute repo root.
 * @param {(line:string)=>void} log Output sink.
 * @returns {number} Process exit code (0 ok, 1 violations).
 */
function main(repoRoot, log) {
  const checks = evaluateContract(repoRoot);
  const failures = checks.filter((check) => !check.ok);
  if (failures.length === 0) {
    return 0;
  }
  log("SourceGenerators build-config contract failed:");
  for (const failure of failures) {
    log(`  - ${failure.message}`);
  }
  log(
    "Fix the .props/.csproj so these structural checks pass. " +
      "Assertions are formatting-invariant (parsed via scripts/lib/msbuild-xml.js); " +
      "line-wrapping/attribute order is fine, but the required elements/values must exist."
  );
  return 1;
}

module.exports = { main };

if (require.main === module) {
  process.exit(main(REPO_ROOT, (line) => process.stdout.write(`${line}\n`)));
}
