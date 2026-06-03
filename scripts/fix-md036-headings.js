#!/usr/bin/env node
// Safe auto-fix for markdownlint MD036 (no-emphasis-as-heading)
// Converts isolated paragraphs that are just **text** or __text__ into level-3 headings.
// - Skips code fences
// - Requires blank line before and after (or start/end of file)

"use strict";

const fs = require("fs");
const path = require("path");

/**
 * Apply the MD036 auto-fix to a markdown source string. Pure function: does
 * NOT touch the filesystem. The CLI entry point handles file IO; the
 * consolidated md pipeline (`scripts/run-staged-md-pipeline.js`) calls this
 * function directly so it can chain fixers without spawning a child process.
 *
 * @param {string} source Raw markdown source.
 * @returns {{content: string, changed: boolean}}
 */
function processMarkdownContent(source) {
  const lines = source.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
  let inFence = false;
  let lastHeadingLevel = 0;
  let changed = false;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();

    // Track fenced code blocks (``` or ~~~)
    if (/^(```|~~~)/.test(trimmed)) {
      inFence = !inFence;
      continue;
    }
    if (inFence) continue;

    // Track last heading level
    const headingMatch = trimmed.match(/^(#{1,6})\s/);
    if (headingMatch) {
      lastHeadingLevel = headingMatch[1].length;
      continue;
    }

    const prev = i > 0 ? lines[i - 1].trim() : "";
    const next = i < lines.length - 1 ? lines[i + 1].trim() : "";
    const isIsolated = (prev === "" || i === 0) && (next === "" || i === lines.length - 1);

    // Match **text** or __text__ (no extra characters on line)
    const m = trimmed.match(/^(\*\*|__)([^*_].*?)(\1)$/);
    if (isIsolated && m) {
      const text = m[2].trim();
      if (text.length > 0) {
        const level = Math.min((lastHeadingLevel || 2) + 1, 6);
        const hashes = "#".repeat(level);
        lines[i] = `${hashes} ${text}`;
        lastHeadingLevel = level;
        changed = true;
      }
    }
  }

  return { content: lines.join("\n"), changed };
}

function printHelp() {
  process.stdout.write(
    [
      "Usage: node scripts/fix-md036-headings.js <file1.md> [file2.md] ...",
      "",
      "Rewrites isolated bold-only paragraphs (e.g. **Heading**) into level-N",
      "headings to satisfy markdownlint MD036.",
      "",
      "Options:",
      "  -h, --help    Show this message.",
      ""
    ].join("\n")
  );
}

function main(argv) {
  const args = argv || [];
  const wantsHelp = args.some((arg) => arg === "-h" || arg === "--help");
  const files = args.filter((arg) => arg !== "-h" && arg !== "--help");

  if (wantsHelp) {
    printHelp();
    return 0;
  }

  if (files.length === 0) {
    console.error("Usage: node scripts/fix-md036-headings.js <file1.md> [file2.md] ...");
    return 1;
  }

  for (const rel of files) {
    const filePath = path.resolve(process.cwd(), rel);
    let src;
    try {
      src = fs.readFileSync(filePath, "utf8");
    } catch (e) {
      console.error(`Skipping ${rel}: ${e.message}`);
      continue;
    }

    const { content, changed } = processMarkdownContent(src);
    if (changed) {
      fs.writeFileSync(filePath, content);
      console.log(`MD036 fixed: ${path.relative(process.cwd(), filePath)}`);
    }
  }
  return 0;
}

if (require.main === module) {
  process.exit(main(process.argv.slice(2)));
}

module.exports = {
  processMarkdownContent,
  main
};
