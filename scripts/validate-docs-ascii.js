#!/usr/bin/env node

/**
 * validate-docs-ascii.js
 *
 * Enforces the ASCII-only documentation policy.
 *
 * Targets:
 *   - All .md files in the repository.
 *   - All .cs files under Runtime/, Editor/, Tests/, SourceGenerators/ -- but
 *     only the contents of /// XML doc comment lines are scanned.
 *   - The generated llms.txt at the repo root.
 *
 * Allowed character set:
 *   - Printable ASCII (0x20-0x7E) and \n \t \r.
 *   - Real emojis (codepoint >= U+1F300) ONLY in callout positions: a line
 *     beginning with ">" (markdown blockquote / GFM admonition).
 *   - Variation selectors U+FE0F and U+FE0E (allowed everywhere).
 *   - BOM U+FEFF tolerated at the start of file only.
 *
 * Banned:
 *   - Geometric/dingbat range U+2300 - U+27BF (arrows, checks, crosses,
 *     warning, bullet, ellipsis-as-symbol, etc.) -- these are the "fake
 *     emoji" set the project explicitly rejects.
 *   - Any other non-ASCII character not covered by the allow list.
 *
 * Per-file emoji cap (warning, not error):
 *   - More than EMOJI_SOFT_CAP real emojis in a single file produces a
 *     warning. The plan's policy is "zero by default; callout-position
 *     exceptions"; the cap exists as a tripwire.
 *
 * ALLOWED_PATHS:
 *   - The two markdown-compatibility split files include emoji-shortcode
 *     example data and are exempt from emoji and codepoint scanning.
 *
 * Usage:
 *   node scripts/validate-docs-ascii.js [--check] [--paths <comma-list>]
 *                                       [files...]
 *
 * Exit codes:
 *   0  No banned characters detected.
 *   1  Banned characters detected (or unrecoverable error).
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { formatCodepointGlyph } = require("./lib/staged-doc-formatters");
const { walkFiles } = require("./lib/repo-files");

const ROOT_DIR = path.resolve(__dirname, "..");

const EXCLUDE_DIRS = new Set([
  ".git",
  "node_modules",
  "Library",
  "Temp",
  "obj",
  "bin",
  "Logs",
  "site",
  "coverage",
  "__pycache__"
]);

const CS_SCAN_ROOTS = ["Runtime", "Editor", "Tests", "SourceGenerators"];

const EXTRA_FILES = [path.join(ROOT_DIR, "llms.txt")];

const ALLOWED_PATHS = new Set([
  path.join(ROOT_DIR, ".llm", "skills", "documentation", "markdown-compatibility-part-1.md"),
  path.join(ROOT_DIR, ".llm", "skills", "documentation", "markdown-compatibility-part-2.md")
]);

const EMOJI_SOFT_CAP = 5;

// --- Codepoint classification ----------------------------------------------

const TAB = 0x09;
const LF = 0x0a;
const CR = 0x0d;
const SPACE = 0x20;
const TILDE = 0x7e;
const BOM = 0xfeff;
const VS15 = 0xfe0e;
const VS16 = 0xfe0f;

const DINGBAT_LO = 0x2300;
const DINGBAT_HI = 0x27bf;
const EMOJI_LO = 0x1f300;

function isAsciiPrintable(cp) {
  return cp >= SPACE && cp <= TILDE;
}

function isAsciiWhitespace(cp) {
  return cp === TAB || cp === LF || cp === CR;
}

function isVariationSelector(cp) {
  return cp === VS15 || cp === VS16;
}

function isDingbat(cp) {
  return cp >= DINGBAT_LO && cp <= DINGBAT_HI;
}

function isEmoji(cp) {
  return cp >= EMOJI_LO;
}

// --- File enumeration -------------------------------------------------------

function defaultFileSet() {
  const out = [];
  out.push(
    ...walkFiles(ROOT_DIR, {
      match: (p) => p.endsWith(".md"),
      excludeDir: (full, dirent) => EXCLUDE_DIRS.has(dirent.name)
    })
  );
  for (const root of CS_SCAN_ROOTS) {
    const abs = path.join(ROOT_DIR, root);
    if (!fs.existsSync(abs)) continue;
    out.push(
      ...walkFiles(abs, {
        match: (p) => p.endsWith(".cs"),
        excludeDir: (full, dirent) => EXCLUDE_DIRS.has(dirent.name)
      })
    );
  }
  for (const extra of EXTRA_FILES) {
    if (fs.existsSync(extra)) out.push(extra);
  }
  return out;
}

// --- Scanning ---------------------------------------------------------------

function isCalloutLine(line) {
  // Markdown blockquote / GFM admonition lead character.
  return /^\s*>/.test(line);
}

function shouldScanLineCs(line) {
  return /^\s*\/\/\//.test(line);
}

function classifyChar(cp, line, isFirstCharOfFile) {
  if (isAsciiPrintable(cp) || isAsciiWhitespace(cp)) {
    return { kind: "ok" };
  }
  if (isVariationSelector(cp)) {
    return { kind: "ok" };
  }
  if (cp === BOM && isFirstCharOfFile) {
    return { kind: "ok" };
  }
  if (isDingbat(cp)) {
    return {
      kind: "banned",
      reason: "dingbat/geometric character (U+2300-U+27BF) is banned outright"
    };
  }
  if (isEmoji(cp)) {
    if (isCalloutLine(line)) {
      return { kind: "emoji" };
    }
    return {
      kind: "banned",
      reason: "emoji outside a callout position (line must start with '>')"
    };
  }
  if (cp < SPACE) {
    return {
      kind: "banned",
      reason: `control character U+${cp.toString(16).toUpperCase().padStart(4, "0")} is not in the allow list`
    };
  }
  return {
    kind: "banned",
    reason: `non-ASCII codepoint U+${cp.toString(16).toUpperCase().padStart(4, "0")} is not in the allow list`
  };
}

function scanContent(filePath, content, isCsharp) {
  const allowedFile = ALLOWED_PATHS.has(filePath);
  const violations = [];
  const lines = content.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
  let emojiCount = 0;

  for (let lineIndex = 0; lineIndex < lines.length; lineIndex++) {
    const line = lines[lineIndex];
    const eligible = isCsharp ? shouldScanLineCs(line) : true;
    if (!eligible) continue;

    let column = 0;
    for (let i = 0; i < line.length; ) {
      const cp = line.codePointAt(i);
      const ch = String.fromCodePoint(cp);
      const isFirstCharOfFile = lineIndex === 0 && i === 0;
      const result = classifyChar(cp, line, isFirstCharOfFile);
      if (result.kind === "banned" && !allowedFile) {
        violations.push({
          file: filePath,
          line: lineIndex + 1,
          column: column + 1,
          codepoint: cp,
          char: ch,
          reason: result.reason
        });
      } else if (result.kind === "emoji") {
        emojiCount++;
      }
      i += ch.length;
      column += ch.length;
    }
  }

  let warning = null;
  if (!allowedFile && emojiCount > EMOJI_SOFT_CAP) {
    warning = {
      file: filePath,
      count: emojiCount,
      cap: EMOJI_SOFT_CAP
    };
  }
  return { violations, emojiCount, warning };
}

function scanFile(filePath) {
  let content;
  try {
    content = fs.readFileSync(filePath, "utf8");
  } catch (error) {
    return { violations: [], warning: null, emojiCount: 0 };
  }
  const isCsharp = filePath.endsWith(".cs");
  return scanContent(filePath, content, isCsharp);
}

// --- CLI --------------------------------------------------------------------

function parseArgs(argv) {
  const args = { check: true, paths: null, files: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--check") {
      args.check = true;
    } else if (a === "--paths") {
      args.paths = argv[++i];
    } else if (a === "--help" || a === "-h") {
      printHelp();
      process.exit(0);
    } else if (a.startsWith("--")) {
      console.error(`Unknown option: ${a}`);
      process.exit(1);
    } else {
      args.files.push(a);
    }
  }
  return args;
}

function printHelp() {
  process.stdout.write(
    [
      "Usage: node scripts/validate-docs-ascii.js [options] [files...]",
      "",
      "Options:",
      "  --check         Default. Exit 1 on any banned char.",
      "  --paths <list>  Comma-separated explicit paths or directory roots.",
      "  -h, --help      Show this message.",
      ""
    ].join("\n")
  );
}

function resolveFileList(args) {
  if (args.files.length > 0) {
    return args.files.map((f) => path.resolve(process.cwd(), f));
  }
  if (args.paths) {
    const out = [];
    for (const entry of args.paths.split(",")) {
      const abs = path.resolve(process.cwd(), entry);
      if (!fs.existsSync(abs)) continue;
      const stat = fs.statSync(abs);
      if (stat.isDirectory()) {
        out.push(
          ...walkFiles(abs, {
            match: (p) =>
              p.endsWith(".md") ||
              (p.endsWith(".cs") &&
                CS_SCAN_ROOTS.some((root) => p.includes(`${path.sep}${root}${path.sep}`))),
            excludeDir: (full, dirent) => EXCLUDE_DIRS.has(dirent.name)
          })
        );
      } else if (stat.isFile()) {
        out.push(abs);
      }
    }
    return out;
  }
  return defaultFileSet();
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const files = resolveFileList(args);

  const allViolations = [];
  const allWarnings = [];

  for (const file of files) {
    const { violations, warning } = scanFile(file);
    if (violations.length > 0) allViolations.push(...violations);
    if (warning) allWarnings.push(warning);
  }

  for (const w of allWarnings) {
    const rel = path.relative(ROOT_DIR, w.file) || w.file;
    process.stderr.write(`WARN ${rel}: ${w.count} emoji(s) found, soft cap is ${w.cap}.\n`);
  }

  if (allViolations.length === 0) {
    process.stdout.write(`validate-docs-ascii: 0 violations across ${files.length} file(s).\n`);
    return 0;
  }

  for (const v of allViolations) {
    const rel = path.relative(ROOT_DIR, v.file) || v.file;
    const cpHex = v.codepoint.toString(16).toUpperCase().padStart(4, "0");
    process.stderr.write(
      `${rel}:${v.line}:${v.column}: U+${cpHex} ${formatCodepointGlyph(
        v.char,
        v.codepoint
      )} -- ${v.reason}\n`
    );
  }
  process.stderr.write(`\nvalidate-docs-ascii: ${allViolations.length} violation(s) found.\n`);
  return 1;
}

if (require.main === module) {
  process.exit(main());
}

module.exports = {
  scanContent,
  classifyChar,
  isCalloutLine,
  EMOJI_SOFT_CAP
};
