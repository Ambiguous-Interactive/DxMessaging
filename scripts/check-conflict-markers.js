#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");

const MARKER_RE = /^(<<<<<<<|=======|>>>>>>>)($|\s)/;

function scanContent(filePath, content) {
  const violations = [];
  const lines = content.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
  for (let i = 0; i < lines.length; i++) {
    if (MARKER_RE.test(lines[i])) {
      violations.push({
        file: filePath,
        line: i + 1,
        text: lines[i]
      });
    }
  }
  return violations;
}

function scanFile(filePath) {
  try {
    const content = fs.readFileSync(filePath, "utf8");
    return scanContent(filePath, content);
  } catch {
    return [];
  }
}

function main(argv = process.argv.slice(2)) {
  const violations = [];
  for (const filePath of argv) {
    violations.push(...scanFile(filePath));
  }

  if (violations.length === 0) {
    return 0;
  }

  for (const violation of violations) {
    const display = path.relative(process.cwd(), violation.file) || violation.file;
    process.stderr.write(
      `${display}:${violation.line}: merge conflict marker '${violation.text}'\n`
    );
  }
  process.stderr.write("Conflict markers found. Resolve merges before committing.\n");
  return 1;
}

module.exports = {
  MARKER_RE,
  scanContent,
  scanFile,
  main
};

if (require.main === module) {
  process.exit(main());
}
