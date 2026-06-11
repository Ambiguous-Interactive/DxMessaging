"use strict";

/**
 * CJS directory entry point that makes `node --test scripts/` work on every
 * supported Node version.
 *
 * Node 20's test runner walks a directory argument and runs each *.test.js
 * file directly (this file does not match the test-file pattern, so it is
 * ignored there). Node 21+ treats positional arguments as glob patterns and
 * runs the matched path as a module, so `scripts/` resolves to this file via
 * CJS directory resolution. Requiring every *.test.js below registers all
 * node:test suites in-process, keeping the documented invocation green on
 * both behaviors.
 */

const fs = require("fs");
const path = require("path");

function requireTestFiles(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isDirectory() && entry.name !== "node_modules") {
      requireTestFiles(fullPath);
    } else if (entry.isFile() && entry.name.endsWith(".test.js")) {
      require(fullPath);
    }
  }
}

requireTestFiles(__dirname);
