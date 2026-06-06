#!/usr/bin/env node
"use strict";

const path = require("path");
const { findPython, runCommand, isSuccess } = require("./ensure-pre-commit");

const REPO_ROOT = path.resolve(__dirname, "..");
const CHECKER = path.join(REPO_ROOT, ".github", "scripts", "check_markdown_links.py");

function main(argv = process.argv.slice(2)) {
  const python = findPython({ runCommandFn: runCommand });
  if (!python) {
    console.error(
      "validate-markdown-link-text: no Python 3 launcher found (tried python, python3, py -3)."
    );
    return 1;
  }

  const inputs = argv.length > 0 ? argv : ["."];
  const result = runCommand(python.command, [...python.args, CHECKER, "--tracked", ...inputs], {
    cwd: REPO_ROOT,
    stdio: "inherit",
    encoding: undefined
  });
  return isSuccess(result) ? 0 : typeof result.status === "number" ? result.status : 1;
}

module.exports = {
  CHECKER,
  main
};

if (require.main === module) {
  process.exit(main());
}
