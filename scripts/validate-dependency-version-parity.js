#!/usr/bin/env node
"use strict";

/**
 * validate-dependency-version-parity.js
 *
 * Thin CLI over scripts/lib/dependency-version-parity.js. Fails (exit 1) when
 * any exact-pinned direct dependency's installed version (or the local
 * lockfile's resolved version) drifts from the package.json pin, or a range
 * pin is unsatisfied. Pure offline JSON reads -- fast enough for the
 * post-edit guard hot path and a `npm run` target.
 *
 * This is the focused, edit-time-friendly counterpart to the broader
 * validate-node-tooling.js (which also enforces parity alongside its npx /
 * resolver policy checks). Reconcile is always `npm install` (the lockfile is
 * gitignored and may be stale; `npm ci` cannot fix a manifest change) --
 * `npm run repair:node-tooling` does this automatically with zero manual touch.
 */

const {
  probeDependencyVersionParity,
  formatDriftLines
} = require("./lib/dependency-version-parity");

function main() {
  const result = probeDependencyVersionParity();
  if (result.ok) {
    console.log(`Dependency version parity OK (${result.checked} pinned direct deps).`);
    return 0;
  }

  console.error(`Found ${result.drifted.length} dependency version drift(s):`);
  for (const line of formatDriftLines(result)) {
    console.error(`- ${line}`);
  }
  console.error(
    "Reconcile with `npm install` (or `npm run repair:node-tooling`) to align node_modules + " +
      "the local lockfile with package.json. The lockfile is gitignored and may be stale, so " +
      "`npm ci` cannot fix a manifest change."
  );
  return 1;
}

if (require.main === module) {
  process.exit(main());
}

module.exports = { main };
