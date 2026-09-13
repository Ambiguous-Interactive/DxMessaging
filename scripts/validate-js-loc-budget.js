#!/usr/bin/env node
"use strict";

/**
 * Fails when tracked JavaScript (*.js / *.cjs / *.mjs) exceeds the repo-wide
 * line budget. JavaScript is CI/docs support in this Unity package and must stay small.
 * Budget rationale remains auditable in this file's Git history.
 */
const { execFileSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const TOTAL_BUDGET = 24158;
const LARGEST_FILE_COUNT = 10;
const REPO_ROOT = path.resolve(__dirname, "..");

function countLines(filePath) {
  const text = fs.readFileSync(filePath, "utf8");
  if (text.length === 0) {
    return 0;
  }
  const lines = text.split("\n").length;
  return text.endsWith("\n") ? lines - 1 : lines;
}

function main() {
  const output = execFileSync("git", ["ls-files", "*.js", "*.cjs", "*.mjs"], {
    cwd: REPO_ROOT,
    encoding: "utf8"
  });
  const files = output.split("\n").filter(Boolean);
  let total = 0;
  const counts = [];
  for (const file of files) {
    const lines = countLines(path.join(REPO_ROOT, file));
    total += lines;
    counts.push({ file, lines });
  }
  if (total > TOTAL_BUDGET) {
    const largest = counts
      .sort((a, b) => b.lines - a.lines || a.file.localeCompare(b.file))
      .slice(0, LARGEST_FILE_COUNT)
      .map(({ file, lines }) => `  ${lines.toString().padStart(5)} ${file}`)
      .join("\n");
    console.error(
      `validate-js-loc-budget: tracked JS is ${total} lines across ${files.length} files; ` +
        `budget is ${TOTAL_BUDGET} (${total - TOTAL_BUDGET} over). ` +
        "Delete or slim JS instead of raising the budget.\n" +
        `Largest tracked JS files:\n${largest}`
    );
    process.exit(1);
  }
  console.log(
    `validate-js-loc-budget: OK (${total}/${TOTAL_BUDGET} lines across ${files.length} files).`
  );
}

main();
