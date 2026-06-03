#!/usr/bin/env node
// Fix common formatting issues in a markdown file where content was collapsed:
// - Ensure headings start on their own line
// - Ensure horizontal rules (---) are on their own line
// - Ensure fenced code blocks (```...) open/close on their own lines
// - Insert blank lines around fences and headings
// - Clean stray "textProblems:" markers
// - Ensure language after opening fence remains (e.g., ```csharp)

const fs = require("fs");
const path = require("path");

if (process.argv.length < 3) {
  console.error("Usage: node scripts/fix-markdown-file.js <path-to-md>");
  process.exit(1);
}

const filePath = path.resolve(process.cwd(), process.argv[2]);
let src = fs.readFileSync(filePath, "utf8");

// Normalize line endings in memory for regex ease; project tooling and attributes apply final EOL policy
src = src.replace(/\r\n/g, "\n").replace(/\r/g, "\n");

// Ensure headings start on their own line
src = src.replace(/([^\n]|^)\s*(#+\s)/g, (m, p1, p2) => {
  // If at start, no prefix; otherwise ensure newline before
  return (p1 && p1 !== "\n" ? "\n" : "") + p2;
});

// Put horizontal rules (---) on their own line with blank lines around
src = src.replace(/([^\n])\n?---(?!-)([^\n])/g, (m, before, after) => {
  return `${before}\n\n---\n\n${after}`;
});
// If hr stuck to content without newlines before/after
src = src.replace(/([^\n])---\n/g, "$1\n\n---\n");
src = src.replace(/\n---([^\n])/g, "\n---\n\n$1");

// Ensure opening fences start at line start and are followed by newline
src = src.replace(/```(\w+)?(?=\S)/g, (m) => m + "\n");
// Ensure any inline "```langcode" occurrences get newline before fence
src = src.replace(/([^\n])```/g, "$1\n```");
// Ensure closing fences are on their own line
src = src.replace(/([^\n])```(?!\w)/g, "$1\n```");

// Ensure newline before any fence that is stuck to previous text
src = src.replace(/([^\n])```/g, "$1\n```");
// Ensure newline after opening fence language if the code starts immediately
src = src.replace(/```(\w+)\s*([^\n`])/g, "```$1\n$2");
// Ensure closing fence is on its own line (newline before and after as needed)
src = src.replace(/([^\n])\n?```(\s*)\n?/g, "$1\n```$2\n");

// Fix occurrences of "textProblems:" -> "Problems:" on new line
src = src.replace(/```\s*textProblems:/g, "```\n\nProblems:");
src = src.replace(/\btextProblems:/g, "Problems:");

// Ensure nav line under top heading appears on its own line
src = src.replace(/(#\s[^\n]+)\s*\n?\[← Back to Index\]/, "$1\n\n[← Back to Index]");

// De-dupe excessive blank lines (>2)
src = src.replace(/\n{3,}/g, "\n\n");

// Write back (no BOM); let .gitattributes/.editorconfig enforce project EOL policy on checkout
fs.writeFileSync(filePath, src);
console.log(`Fixed markdown structure: ${path.relative(process.cwd(), filePath)}`);
