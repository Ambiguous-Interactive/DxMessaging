#!/usr/bin/env node

/**
 * validate-doc-code-patterns.js
 *
 * Lints documentation code samples for known-broken patterns.
 *
 * Targets:
 *   - All .md files in the repository.
 *   - All .cs files under Runtime/, Editor/, Tests/, SourceGenerators/ -- but
 *     only the contents of /// XML doc comment lines are scanned.
 *
 * Sources scanned within each .md file:
 *   - Triple-backtick fenced code blocks.
 *   - Single-backtick inline code spans (including those inside table cells).
 *   - Bare prose lines (some violations slip in as raw text).
 *
 * Pluggable via the BANNED_PATTERNS array. Each pattern carries an id,
 * regex, "why" explanation, and "fix" suggestion. Adding a new pattern is a
 * one-line change.
 *
 * Counter-example marker: lines containing one of the deliberate-counterexample
 * phrases ("won't compile", "will not compile", "does not compile",
 * "do not compile") on the same line as a match are treated as intentional
 * negative documentation and skipped.
 *
 * Usage:
 *   node scripts/validate-doc-code-patterns.js [--check] [--list-rules]
 *                                              [--paths <comma-list>]
 *                                              [files...]
 *
 * Exit codes:
 *   0  No violations.
 *   1  Violations detected (or unrecoverable error).
 */

"use strict";

const fs = require("fs");
const path = require("path");
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

// Files that the normalizer treats as generated. Skip them here too -- their
// contents derive from sources we already scan.
const GENERATED_FILE_PATHS = new Set([path.join(ROOT_DIR, ".llm", "skills", "index.md")]);

// --- Rules ------------------------------------------------------------------

const COUNTEREXAMPLE_MARKERS = [
  /won'?t\s+compile/i,
  /will\s+not\s+compile/i,
  /does\s+not\s+compile/i,
  /do\s+not\s+compile/i,
  /fails?\s+to\s+compile/i
];

const BANNED_PATTERNS = [
  {
    id: "struct-emit-temporary",
    // Catches: "new X().Emit(", "new X().EmitTargeted(", "(new X()).Emit(",
    // "new Namespace.X().Emit(", whitespace variants like "new X () . Emit (".
    // Two alternatives:
    //   1. Bare form: "new X(args).Emit(" with NO leading "(" wrapping.
    //      A negative lookbehind on the "new" rejects "(" or ")" or word
    //      chars immediately preceding, so "someMethod(new X()).Emit("
    //      does not anchor here (the "new" is preceded by "(" from the
    //      method call and there is no balanced wrapping group).
    //   2. Parenthesized form: "(new X(args)).Emit(" -- the leading "("
    //      must NOT be preceded by an identifier or ")" (which would
    //      indicate a function call rather than a grouping paren).
    // Both forms use a balanced (...) group for the constructor args
    // (one level of nesting) to tolerate generic/method-call arguments.
    pattern:
      /(?:(?<![\w)([])new\s+[\w.]+\s*\((?:[^()]|\([^()]*\))*\)|(?<![\w)])\(\s*new\s+[\w.]+\s*\((?:[^()]|\([^()]*\))*\)\s*\))\s*\.\s*Emit\w*\s*\(/g,
    why:
      'Emit on structs takes "this ref TMessage". A "new X()" expression ' +
      "is an rvalue and not addressable, so the call fails to bind " +
      "(CS1612 / CS1510 depending on context). The compilation harness " +
      "cannot reliably catch this class of bug because stub-only " +
      "compilation produces CS1510 noise on legitimate snippets, so this " +
      "pattern lint is the canonical defense.",
    fix: 'Assign to a local first, then call: "var msg = new X(...); ' + 'msg.Emit();".'
  }
];

// --- File enumeration -------------------------------------------------------

function defaultFileSet() {
  const out = [];
  out.push(
    ...walkFiles(ROOT_DIR, {
      match: (p) => {
        if (GENERATED_FILE_PATHS.has(p)) return false;
        return p.endsWith(".md");
      },
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
  return out;
}

// --- Scanning ---------------------------------------------------------------

function isCounterExampleLine(line) {
  return COUNTEREXAMPLE_MARKERS.some((re) => re.test(line));
}

function scanLineForRule(rule, line, lineNumber, filePath, violations) {
  if (isCounterExampleLine(line)) return;
  rule.pattern.lastIndex = 0;
  let match;
  while ((match = rule.pattern.exec(line)) !== null) {
    violations.push({
      file: filePath,
      line: lineNumber,
      column: match.index + 1,
      id: rule.id,
      why: rule.why,
      fix: rule.fix,
      sample: match[0]
    });
    // Avoid infinite loop on zero-width matches.
    if (match.index === rule.pattern.lastIndex) rule.pattern.lastIndex++;
  }
}

function scanMarkdown(filePath, content, violations) {
  const lines = content.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
  // For markdown we scan all non-empty lines: fenced blocks, inline code in
  // table cells, prose, comments. The rules are designed to be specific
  // enough that prose false positives are unlikely.
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    for (const rule of BANNED_PATTERNS) {
      scanLineForRule(rule, line, i + 1, filePath, violations);
    }
  }
}

function scanCSharp(filePath, content, violations) {
  const lines = content.replace(/\r\n/g, "\n").replace(/\r/g, "\n").split("\n");
  for (let i = 0; i < lines.length; i++) {
    const trimmed = lines[i].replace(/^\s+/, "");
    if (!trimmed.startsWith("///")) continue;
    // Strip the leading slashes so we don't accidentally match against
    // syntax noise.
    const docLine = trimmed.slice(3);
    for (const rule of BANNED_PATTERNS) {
      scanLineForRule(rule, docLine, i + 1, filePath, violations);
    }
  }
}

function scanFile(filePath, violations) {
  let content;
  try {
    content = fs.readFileSync(filePath, "utf8");
  } catch (error) {
    return;
  }
  if (filePath.endsWith(".md")) {
    scanMarkdown(filePath, content, violations);
  } else if (filePath.endsWith(".cs")) {
    scanCSharp(filePath, content, violations);
  }
}

// --- CLI --------------------------------------------------------------------

function parseArgs(argv) {
  const args = {
    check: true,
    listRules: false,
    paths: null,
    files: []
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--check") {
      args.check = true;
    } else if (a === "--list-rules") {
      args.listRules = true;
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
      "Usage: node scripts/validate-doc-code-patterns.js [options] [files...]",
      "",
      "Options:",
      "  --check         Default. Exit 1 on any violation.",
      "  --list-rules    Print the configured rule catalog and exit 0.",
      "  --paths <list>  Comma-separated explicit paths or directory roots.",
      "  -h, --help      Show this message.",
      ""
    ].join("\n")
  );
}

function listRules() {
  process.stdout.write(`Configured rules: ${BANNED_PATTERNS.length}\n\n`);
  for (const rule of BANNED_PATTERNS) {
    process.stdout.write(`- id:    ${rule.id}\n`);
    process.stdout.write(`  regex: ${rule.pattern}\n`);
    process.stdout.write(`  why:   ${rule.why}\n`);
    process.stdout.write(`  fix:   ${rule.fix}\n\n`);
  }
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
  if (args.listRules) {
    listRules();
    return 0;
  }

  const files = resolveFileList(args);
  const violations = [];
  for (const file of files) {
    scanFile(file, violations);
  }

  if (violations.length === 0) {
    process.stdout.write(
      `validate-doc-code-patterns: 0 violations across ${files.length} file(s).\n`
    );
    return 0;
  }

  for (const v of violations) {
    const rel = path.relative(ROOT_DIR, v.file) || v.file;
    process.stderr.write(
      `${rel}:${v.line}:${v.column}: [${v.id}] ${v.sample}\n` +
        `    why: ${v.why}\n` +
        `    fix: ${v.fix}\n`
    );
  }
  process.stderr.write(`\nvalidate-doc-code-patterns: ${violations.length} violation(s) found.\n`);
  return 1;
}

if (require.main === module) {
  process.exit(main());
}

module.exports = {
  BANNED_PATTERNS,
  scanMarkdown,
  scanCSharp,
  isCounterExampleLine
};
