#!/usr/bin/env node
/**
 * @fileoverview Validates that the runtime-settings docs page references the
 * same set of public properties exposed by `DxMessagingRuntimeSettings.cs`.
 *
 * Source of truth:
 *   - `Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs` defines the
 *     public read-only properties that consumers script against.
 *   - `docs/reference/runtime-settings.md` documents each property in a
 *     parameter table whose first column references the C# property name.
 *
 * This validator pairs the two files: every public property in the C# file
 * must have a matching row in the doc table, and every doc-table row must
 * reference a public property that still exists in the C# file. A drift in
 * either direction is a CI failure with a clear remediation hint.
 *
 * Path resolution policy:
 *   The repo root is resolved as `path.resolve(__dirname, "..")` so the
 *   validator works whether invoked via `npm run` (cwd == repo root) or via a
 *   nested cwd (for example a subagent's worktree). This avoids any reliance
 *   on `git rev-parse --show-toplevel`, keeping the validator usable inside
 *   non-git checkouts too (the failure mode for a missing C# file is then a
 *   clear `parse-error` rather than an opaque git invocation error).
 *
 * Usage:
 *   node scripts/validate-runtime-settings-docs.js
 *   node scripts/validate-runtime-settings-docs.js --check
 *   node scripts/validate-runtime-settings-docs.js --list-properties
 *
 * Exit codes:
 *   0  Source and doc table agree (or both files exist with matching names).
 *   1  Drift detected, parse error, or unknown CLI flag.
 */

"use strict";

const fs = require("fs");
const path = require("path");
const { parseArgs: parseCliArgs } = require("./lib/cli-options");

const REPO_ROOT = path.resolve(__dirname, "..");
const DEFAULT_SOURCE_PATH = path.join(
  REPO_ROOT,
  "Runtime",
  "Core",
  "Configuration",
  "DxMessagingRuntimeSettings.cs"
);
const DEFAULT_DOC_PATH = path.join(REPO_ROOT, "docs", "reference", "runtime-settings.md");

const UTF8_BOM = "﻿";

// Modifiers that may appear between `public` and the type token in a property
// declaration. The parser accepts any combination in any order; the C#
// compiler enforces ordering, so a value file that compiles will satisfy this
// list.
const PROPERTY_MODIFIERS = new Set([
  "static",
  "virtual",
  "override",
  "new",
  "readonly",
  "sealed",
  "abstract",
  "extern",
  "unsafe"
]);

// Type-declaration keywords that distinguish a `public class Foo` line from a
// `public int Foo` property line. When one of these tokens appears at the
// modifier-or-type position, the line is NOT a property and must be skipped.
const TYPE_DECLARATION_KEYWORDS = new Set([
  "class",
  "struct",
  "interface",
  "enum",
  "record",
  "delegate",
  "event"
]);

/**
 * Strip a leading UTF-8 BOM so downstream parsers do not have to special-case
 * it. Files written by some Windows tooling include a BOM that breaks naive
 * regex anchors at the very start of the buffer.
 *
 * @param {string} content
 * @returns {string}
 */
function stripBom(content) {
  if (typeof content !== "string") {
    return "";
  }
  return content.startsWith(UTF8_BOM) ? content.slice(UTF8_BOM.length) : content;
}

/**
 * Normalize CRLF/CR to LF so line-by-line scanning produces consistent
 * coordinates across operating systems.
 *
 * @param {string} content
 * @returns {string}
 */
function normalizeLineEndings(content) {
  return stripBom(content).replace(/\r\n/g, "\n").replace(/\r/g, "\n");
}

/**
 * Replace every C# string-literal body (regular, verbatim, and interpolated)
 * with spaces of equal length so brace counters and other lexical scanners
 * cannot be fooled by characters inside literals (`=> "}";`, `$"{0}"`, etc.).
 *
 * The transformation preserves the input's length so caller-side line/column
 * coordinates remain valid. Newlines inside verbatim strings are also
 * preserved so line-based logic still indexes the right line numbers.
 *
 * Supported forms:
 *   - Regular strings:    `"..."`         with backslash escapes
 *   - Verbatim strings:   `@"..."`        where `""` represents a literal `"`
 *   - Interpolated:       `$"..."`        with `\` escapes; `{{`/`}}` are
 *                                         literal braces (we strip them too)
 *   - Verbatim+interpolated: `$@"..."` and `@$"..."` (handled like verbatim
 *                                         interpolated; `""` is a literal `"`,
 *                                         `{{`/`}}` are literal braces)
 *   - Char literals:      `'.'` and `'\\\\.'`
 *
 * Single-line `//` comments and block `/* *\/` comments are also blanked. We
 * preserve the original content's length so coordinates stay valid.
 *
 * @param {string} content
 * @returns {string}
 */
function stripStringsAndComments(content) {
  if (typeof content !== "string" || content.length === 0) {
    return content || "";
  }

  const out = new Array(content.length);
  let i = 0;
  const n = content.length;

  // Helper that copies a span unchanged into the output buffer. Used for
  // tokens we do NOT need to mask (so brace counting can still see them).
  function copyChar() {
    out[i] = content[i];
    i += 1;
  }

  // Helper that masks a single char as a space (or preserves a newline so
  // line numbers remain valid).
  function maskChar() {
    out[i] = content[i] === "\n" ? "\n" : " ";
    i += 1;
  }

  while (i < n) {
    const ch = content[i];
    const next = i + 1 < n ? content[i + 1] : "";

    // Line comment: blank everything until the newline.
    if (ch === "/" && next === "/") {
      while (i < n && content[i] !== "\n") {
        maskChar();
      }
      continue;
    }

    // Block comment: blank everything until `*\/`.
    if (ch === "/" && next === "*") {
      maskChar();
      maskChar();
      while (i < n) {
        if (content[i] === "*" && i + 1 < n && content[i + 1] === "/") {
          maskChar();
          maskChar();
          break;
        }
        maskChar();
      }
      continue;
    }

    // Char literal: '.' or '\\\\.'. We keep the lexical shape but blank the
    // body so any brace inside cannot be miscounted.
    if (ch === "'") {
      copyChar(); // opening quote
      while (i < n && content[i] !== "'") {
        if (content[i] === "\\" && i + 1 < n) {
          maskChar();
          maskChar();
          continue;
        }
        if (content[i] === "\n") {
          // Unterminated; bail out gracefully.
          break;
        }
        maskChar();
      }
      if (i < n && content[i] === "'") {
        copyChar(); // closing quote
      }
      continue;
    }

    // Verbatim and interpolated strings: detect the prefix combinations.
    let prefixLen = 0;
    let isVerbatim = false;
    let isInterpolated = false;

    if (ch === "@" && next === '"') {
      prefixLen = 1;
      isVerbatim = true;
    } else if (ch === "$" && next === '"') {
      prefixLen = 1;
      isInterpolated = true;
    } else if (
      (ch === "@" && next === "$" && i + 2 < n && content[i + 2] === '"') ||
      (ch === "$" && next === "@" && i + 2 < n && content[i + 2] === '"')
    ) {
      prefixLen = 2;
      isVerbatim = true;
      isInterpolated = true;
    } else if (ch === '"') {
      prefixLen = 0;
    } else {
      copyChar();
      continue;
    }

    // Copy the prefix (and the opening quote) so identifiers around the
    // string keep their textual layout.
    for (let k = 0; k < prefixLen; k++) {
      copyChar();
    }
    if (i < n && content[i] === '"') {
      copyChar(); // opening "
    } else {
      // Not actually a string literal (the @/$ token stood alone). Continue.
      continue;
    }

    // Walk the body, masking content but preserving newlines.
    while (i < n) {
      const cur = content[i];
      if (isVerbatim) {
        if (cur === '"') {
          if (i + 1 < n && content[i + 1] === '"') {
            // Escaped quote inside verbatim string.
            maskChar();
            maskChar();
            continue;
          }
          // Closing quote.
          copyChar();
          break;
        }
        if (isInterpolated && cur === "{" && i + 1 < n && content[i + 1] === "{") {
          maskChar();
          maskChar();
          continue;
        }
        if (isInterpolated && cur === "}" && i + 1 < n && content[i + 1] === "}") {
          maskChar();
          maskChar();
          continue;
        }
        maskChar();
      } else {
        if (cur === "\\" && i + 1 < n) {
          // Backslash escape: blank both characters.
          maskChar();
          maskChar();
          continue;
        }
        if (cur === '"') {
          copyChar();
          break;
        }
        if (cur === "\n") {
          // Unterminated regular string — bail out so the rest of the file
          // still parses sensibly.
          break;
        }
        if (isInterpolated && cur === "{" && i + 1 < n && content[i + 1] === "{") {
          maskChar();
          maskChar();
          continue;
        }
        if (isInterpolated && cur === "}" && i + 1 < n && content[i + 1] === "}") {
          maskChar();
          maskChar();
          continue;
        }
        maskChar();
      }
    }
  }

  return out.join("");
}

/**
 * Locate the `class DxMessagingRuntimeSettings` declaration line.
 *
 * @param {string[]} lines
 * @returns {number} The 0-based index, or -1 if not found.
 */
function findClassStartLine(lines) {
  for (let i = 0; i < lines.length; i++) {
    if (/\bclass\s+DxMessagingRuntimeSettings\b/.test(lines[i])) {
      return i;
    }
  }
  return -1;
}

/**
 * From a class-declaration line index, find the line containing the opening
 * brace of the class body. Returns -1 if not found.
 *
 * @param {string[]} lines
 * @param {number} classStartLine
 * @returns {number}
 */
function findClassBraceLine(lines, classStartLine) {
  for (let i = classStartLine; i < lines.length; i++) {
    if (lines[i].includes("{")) {
      return i;
    }
  }
  return -1;
}

/**
 * Extract a property name from a candidate declaration line (or join of two
 * adjacent lines for multi-line forms). Returns null if the line is not a
 * public property declaration.
 *
 * The parser handles:
 *   - `public int Name => _x;`
 *   - `public static int Name => _x;`
 *   - `public virtual int Name => _x;`
 *   - `public override int Name => _x;`
 *   - `public new int Name => _x;`
 *   - `public readonly int Name => _x;`
 *   - `public List<KeyValuePair<int, string>> Name => _x;`
 *   - `public global::System.Int32 Name => _x;`
 *   - `public int Name { get; }` / `{ get; private set; }` / `{ get; init; }`
 *   - `public int Name\n        => _x;` (multi-line expression body)
 *
 * The parser rejects:
 *   - Methods (an `(` precedes `=>` or `{`)
 *   - Type declarations (`class`, `struct`, etc.)
 *   - Non-`public` lines
 *   - Lines with no recognizable identifier-before-arrow-or-brace
 *
 * @param {string} candidate - The candidate line, optionally joined with the
 *                             next non-blank line for multi-line forms.
 * @returns {string|null}    - The extracted property name, or null on no match.
 */
function extractPropertyNameFromCandidate(candidate) {
  const text = candidate.trim();
  if (!text) {
    return null;
  }

  // Attribute-only lines (e.g. `[Obsolete]`) are not properties on their own.
  if (text.startsWith("[")) {
    return null;
  }

  // Must start with `public` followed by whitespace.
  const publicMatch = text.match(/^public\b\s+/);
  if (!publicMatch) {
    return null;
  }

  let rest = text.slice(publicMatch[0].length).trim();

  // Strip recognized modifiers in any order. We loop because modifiers may
  // appear in any sequence (`public static readonly`, `public readonly
  // static`).
  while (true) {
    const tokenMatch = rest.match(/^([A-Za-z_]\w*)\b\s*/);
    if (!tokenMatch) {
      break;
    }
    const token = tokenMatch[1];
    if (PROPERTY_MODIFIERS.has(token)) {
      rest = rest.slice(tokenMatch[0].length).trim();
      continue;
    }
    if (TYPE_DECLARATION_KEYWORDS.has(token)) {
      // `public class Foo` and friends — not a property.
      return null;
    }
    break;
  }

  // Now `rest` should start with the type token. We must consume the type
  // (which can include qualified names with `::`/`.`, generics with arbitrary
  // nesting, and trailing `?`/`[]`) so the next token is the property name.
  const typeEnd = consumeTypeToken(rest);
  if (typeEnd <= 0) {
    return null;
  }
  rest = rest.slice(typeEnd).trim();

  // The property name is the next identifier.
  const nameMatch = rest.match(/^([A-Za-z_]\w*)\b/);
  if (!nameMatch) {
    return null;
  }
  const name = nameMatch[1];
  let after = rest.slice(nameMatch[0].length);

  // Reject methods: a `(` before `=>` or `{` means this is a method
  // declaration. Compute the earliest of those three tokens; if `(` wins,
  // bail out.
  const arrowIdx = after.indexOf("=>");
  const braceIdx = after.indexOf("{");
  const parenIdx = after.indexOf("(");

  function firstNonNegative(...indices) {
    let best = -1;
    for (const idx of indices) {
      if (idx < 0) continue;
      if (best < 0 || idx < best) best = idx;
    }
    return best;
  }

  const earliest = firstNonNegative(arrowIdx, braceIdx, parenIdx);
  if (earliest < 0) {
    return null;
  }
  if (earliest === parenIdx) {
    // It's a method or a constructor. Not a property.
    return null;
  }

  // For property-block form `{ get; ... }` accept a few common shapes.
  if (earliest === braceIdx) {
    const blockBody = after.slice(braceIdx);
    // Accept `{ get; }`, `{ get; private set; }`, `{ get; init; }`, etc.
    // Reject anything with a body assignment like `{ get; } = expr;` —
    // accept it, since that's still a public property.
    if (!/^\{\s*get\s*;/.test(blockBody)) {
      return null;
    }
  }

  return name;
}

/**
 * Consume a C# type token from the start of `text` and return the number of
 * characters consumed. Handles qualified names, `global::` prefix, generics
 * (with arbitrary nesting), and trailing `?` / `[]` / `[,]` markers.
 *
 * Returns 0 if the start of `text` does not look like a type.
 *
 * @param {string} text
 * @returns {number}
 */
function consumeTypeToken(text) {
  let pos = 0;
  const n = text.length;

  // Optional `global::` prefix.
  if (text.startsWith("global::")) {
    pos = "global::".length;
  }

  // First identifier component is required.
  const firstIdent = text.slice(pos).match(/^[A-Za-z_]\w*/);
  if (!firstIdent) {
    return 0;
  }
  pos += firstIdent[0].length;

  // Optional dotted segments: `.Foo.Bar`.
  while (pos < n) {
    if (text[pos] === ".") {
      const dotMatch = text.slice(pos + 1).match(/^[A-Za-z_]\w*/);
      if (!dotMatch) break;
      pos += 1 + dotMatch[0].length;
      continue;
    }
    break;
  }

  // Optional generic argument list with arbitrary nesting.
  if (pos < n && text[pos] === "<") {
    let depth = 0;
    while (pos < n) {
      const c = text[pos];
      if (c === "<") {
        depth += 1;
      } else if (c === ">") {
        depth -= 1;
        pos += 1;
        if (depth === 0) {
          break;
        }
        continue;
      }
      pos += 1;
    }
  }

  // Optional nullable marker.
  if (pos < n && text[pos] === "?") {
    pos += 1;
  }

  // Optional array-rank specifier: `[]`, `[,]`, `[,,]`, ... possibly multiple.
  while (pos < n && text[pos] === "[") {
    let close = pos + 1;
    while (close < n && text[close] !== "]") {
      close += 1;
    }
    if (close >= n || text[close] !== "]") {
      break;
    }
    pos = close + 1;
  }

  // The next character must be whitespace (so the property identifier can
  // follow). If not, this isn't a valid type-then-name pattern.
  if (pos < n && /\S/.test(text[pos])) {
    return 0;
  }
  return pos;
}

/**
 * Extract the public read-only property names declared on
 * `DxMessagingRuntimeSettings`. The validator matches:
 *   - Expression-bodied properties: `public int X => _x;`
 *   - Auto-properties:              `public int X { get; }`
 *                                    `public int X { get; private set; }`
 *                                    `public int X { get; init; }`
 *
 * Modifiers (`static`, `virtual`, `override`, `new`, `readonly`, ...) are
 * tolerated. Methods (`public int Foo() => 1;`) are excluded by detecting a
 * `(` before `=>` or `{`.
 *
 * @param {string} sourceContent - Raw .cs file contents
 * @returns {{names: string[], lineNumbersByName: Map<string, number>}}
 *   The discovered public-property names in source order, plus a map from
 *   property name to the 1-based line number for diagnostics.
 */
function extractPublicReadOnlyProperties(sourceContent) {
  const normalized = normalizeLineEndings(sourceContent);
  const masked = stripStringsAndComments(normalized);
  const lines = normalized.split("\n");
  const maskedLines = masked.split("\n");

  const classStartLine = findClassStartLine(lines);
  if (classStartLine === -1) {
    return { names: [], lineNumbersByName: new Map() };
  }
  const braceLine = findClassBraceLine(lines, classStartLine);
  if (braceLine === -1) {
    return { names: [], lineNumbersByName: new Map() };
  }

  let depth = 0;
  let inClass = false;
  const names = [];
  const lineNumbersByName = new Map();

  for (let i = braceLine; i < lines.length; i++) {
    const maskedLine = maskedLines[i];
    const sourceLine = lines[i];

    // Brace counting uses the masked line so `=> "}"` cannot fool us.
    for (const ch of maskedLine) {
      if (ch === "{") {
        depth += 1;
        inClass = true;
      } else if (ch === "}") {
        depth -= 1;
        if (inClass && depth === 0) {
          return { names, lineNumbersByName };
        }
      }
    }

    if (!inClass || depth !== 1) {
      continue;
    }

    // Build the candidate. If the masked line does not contain a terminator
    // (`;`, `=>`, `{`), join with the next non-blank line so multi-line
    // expression-bodied properties parse correctly.
    let candidate = maskedLine;
    const hasTerminator =
      candidate.includes(";") || candidate.includes("=>") || candidate.includes("{");
    if (!hasTerminator) {
      // Look ahead for the first non-blank line.
      for (let j = i + 1; j < maskedLines.length; j++) {
        const next = maskedLines[j];
        if (next.trim().length === 0) {
          continue;
        }
        candidate = `${candidate.trimEnd()} ${next.trimStart()}`;
        break;
      }
    }

    const name = extractPropertyNameFromCandidate(candidate);
    if (name && !lineNumbersByName.has(name)) {
      names.push(name);
      // Record the line at which the candidate started, not the joined line.
      lineNumbersByName.set(name, i + 1);
    }

    // We intentionally continue using `sourceLine` only for diagnostics; the
    // masked variant is what drives parsing and brace counting.
    void sourceLine;
  }

  return { names, lineNumbersByName };
}

/**
 * Extract the property names referenced in the runtime-settings doc table.
 *
 * The doc page is markdown with a single parameter table whose first column
 * may either be a friendly name (`Idle Eviction Seconds`) or the property
 * name itself, and whose second column is the C# property name (sometimes
 * wrapped in backticks). We treat the SECOND column as authoritative because
 * it matches the C# names directly; the first column is a human-readable
 * label and is not used by the validator.
 *
 * The table heading row contains `C# property` to identify the table; that
 * heading itself is skipped, as is the separator row of dashes. Section
 * headings (`### \`IdleEvictionSeconds\``) are NOT table rows and are
 * intentionally ignored even when they happen to look like a property name.
 *
 * @param {string} docContent - Raw .md file contents
 * @returns {{names: string[], lineNumbersByName: Map<string, number>}}
 *   The discovered property names in source order plus their 1-based line
 *   numbers in the doc file.
 */
function extractDocPropertyNames(docContent) {
  const normalized = normalizeLineEndings(docContent);
  const lines = normalized.split("\n");

  const names = [];
  const lineNumbersByName = new Map();

  let inTable = false;
  let csharpColumnIndex = -1;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const trimmed = line.trim();

    if (!trimmed.startsWith("|")) {
      // Any non-table line breaks the current table region.
      if (inTable) {
        inTable = false;
        csharpColumnIndex = -1;
      }
      continue;
    }

    const cells = parseMarkdownTableRow(trimmed);

    if (!inTable) {
      // First `|` line is the header. Look for the C# property column.
      const headerIndex = cells.findIndex((cell) => /c#\s*property/i.test(cell));
      if (headerIndex !== -1) {
        inTable = true;
        csharpColumnIndex = headerIndex;
      }
      continue;
    }

    // Skip separator rows like `| --- | --- |`.
    if (cells.every((cell) => /^:?-{3,}:?$/.test(cell))) {
      continue;
    }

    if (csharpColumnIndex < 0 || csharpColumnIndex >= cells.length) {
      continue;
    }

    const rawCell = cells[csharpColumnIndex];
    const propertyName = stripBackticks(rawCell);
    if (!propertyName) {
      continue;
    }

    // Defensive: only treat valid identifier-shaped cells as property names so
    // a stray sentence in the table cannot count.
    if (!/^[A-Za-z_]\w*$/.test(propertyName)) {
      continue;
    }

    if (!lineNumbersByName.has(propertyName)) {
      names.push(propertyName);
      lineNumbersByName.set(propertyName, i + 1);
    }
  }

  return { names, lineNumbersByName };
}

/**
 * Split a markdown table row on `|`, dropping leading/trailing empties caused
 * by the surrounding pipes. Cell contents are trimmed.
 *
 * @param {string} row
 * @returns {string[]}
 */
function parseMarkdownTableRow(row) {
  // Strip the leading and trailing pipe before splitting so empty leading/
  // trailing cells do not appear in the output.
  let inner = row;
  if (inner.startsWith("|")) {
    inner = inner.slice(1);
  }
  if (inner.endsWith("|")) {
    inner = inner.slice(0, -1);
  }
  return inner.split("|").map((cell) => cell.trim());
}

/**
 * Remove a single matching pair of backticks around the cell content if
 * present. The doc table author may write the property name as
 * `IdleEvictionSeconds` or simply IdleEvictionSeconds.
 *
 * @param {string} value
 * @returns {string}
 */
function stripBackticks(value) {
  if (typeof value !== "string") {
    return "";
  }
  const trimmed = value.trim();
  if (trimmed.startsWith("`") && trimmed.endsWith("`") && trimmed.length >= 2) {
    return trimmed.slice(1, -1).trim();
  }
  return trimmed;
}

/**
 * Diff the C# property set against the doc property set.
 *
 * @param {string[]} sourceNames - Names extracted from the C# file
 * @param {string[]} docNames    - Names extracted from the doc file
 * @returns {{missingInDoc: string[], extraInDoc: string[]}}
 */
function diffPropertySets(sourceNames, docNames) {
  const docSet = new Set(docNames);
  const sourceSet = new Set(sourceNames);

  const missingInDoc = sourceNames.filter((name) => !docSet.has(name));
  const extraInDoc = docNames.filter((name) => !sourceSet.has(name));

  return { missingInDoc, extraInDoc };
}

/**
 * Read a file from disk if it exists, returning a structured result rather
 * than throwing so callers can produce a rich diagnostic.
 *
 * @param {string} filePath
 * @returns {{ok: true, content: string} | {ok: false, message: string}}
 */
function readFileIfExists(filePath) {
  try {
    const content = fs.readFileSync(filePath, "utf8");
    return { ok: true, content };
  } catch (error) {
    return {
      ok: false,
      message:
        error && error.code === "ENOENT"
          ? `File not found: ${filePath}`
          : `Unable to read ${filePath}: ${(error && error.message) || error}`
    };
  }
}

/**
 * Run the validator and return a structured result. The CLI entry point uses
 * the `errors` array to drive its exit code.
 *
 * @param {object} [options]
 * @param {string} [options.sourcePath] - Override the default C# file location
 * @param {string} [options.docPath]    - Override the default doc file location
 * @returns {{
 *   valid: boolean,
 *   errors: Array<{type: string, name?: string, message: string}>,
 *   sourceNames: string[],
 *   docNames: string[]
 * }}
 */
function validate(options = {}) {
  const sourcePath = options.sourcePath || DEFAULT_SOURCE_PATH;
  const docPath = options.docPath || DEFAULT_DOC_PATH;

  const sourceRead = readFileIfExists(sourcePath);
  if (!sourceRead.ok) {
    return {
      valid: false,
      errors: [
        {
          type: "parse-error",
          message:
            `${sourceRead.message}. ` +
            `Add the C# settings file or fix the path. The validator expects ` +
            `Runtime/Core/Configuration/DxMessagingRuntimeSettings.cs at the repo root.`
        }
      ],
      sourceNames: [],
      docNames: []
    };
  }

  const { names: sourceNames } = extractPublicReadOnlyProperties(sourceRead.content);
  if (sourceNames.length === 0) {
    return {
      valid: false,
      errors: [
        {
          type: "parse-error",
          message:
            `No public read-only properties found in ${sourcePath}. ` +
            `Either the file shape changed or the regex needs an update.`
        }
      ],
      sourceNames: [],
      docNames: []
    };
  }

  const docRead = readFileIfExists(docPath);
  if (!docRead.ok) {
    return {
      valid: false,
      errors: [
        {
          type: "parse-error",
          message:
            `${docRead.message}. ` +
            `Create docs/reference/runtime-settings.md with a parameter table ` +
            `whose 'C# property' column lists each public property of ` +
            `DxMessagingRuntimeSettings.`
        }
      ],
      sourceNames,
      docNames: []
    };
  }

  const { names: docNames } = extractDocPropertyNames(docRead.content);
  if (docNames.length === 0) {
    return {
      valid: false,
      errors: [
        {
          type: "parse-error",
          message:
            `No property rows found in ${docPath}. ` +
            `The validator looks for a table with a 'C# property' column. ` +
            `Confirm the table heading and that each row's C# property cell ` +
            `is an identifier (optionally wrapped in backticks).`
        }
      ],
      sourceNames,
      docNames: []
    };
  }

  const { missingInDoc, extraInDoc } = diffPropertySets(sourceNames, docNames);

  const errors = [];
  for (const name of missingInDoc) {
    errors.push({
      type: "missing-doc-row",
      name,
      message:
        `Public property '${name}' has no row in ${docPath}. ` +
        `Add a row to the parameter table referencing '${name}' in the C# property column.`
    });
  }
  for (const name of extraInDoc) {
    errors.push({
      type: "extra-doc-row",
      name,
      message:
        `Doc table references '${name}' but no public property by that name exists in ${sourcePath}. ` +
        `Either restore the C# property or remove the doc row.`
    });
  }

  return { valid: errors.length === 0, errors, sourceNames, docNames };
}

/**
 * Pretty-print the validation result. Returns the process exit code so the
 * CLI entry point can use it directly.
 *
 * @param {ReturnType<typeof validate>} result
 * @param {{logger?: typeof console}} [options]
 * @returns {number}
 */
function reportResult(result, options = {}) {
  const logger = options.logger || console;

  if (result.valid) {
    logger.log(
      `validate-runtime-settings-docs: OK (${result.sourceNames.length} properties; ` +
        `${result.docNames.length} doc rows match)`
    );
    return 0;
  }

  logger.log("validate-runtime-settings-docs: FAILED");
  for (const error of result.errors) {
    logger.log(`  - [${error.type}] ${error.message}`);
  }
  logger.log(
    "Remediation: keep DxMessagingRuntimeSettings.cs and " +
      "docs/reference/runtime-settings.md in lockstep. Update both in the same change."
  );
  return 1;
}

/**
 * Parse CLI arguments. The validator currently accepts only `--check` (which
 * is the same as default mode) and treats unknown flags as a usage error so
 * a typo cannot silently disable the check.
 *
 * @param {string[]} argv
 * @returns {{check: boolean, listProperties: boolean, help: boolean, errors: string[]}}
 */
function parseArgs(argv) {
  // Flags only; every other token (a bare word, a lone `-`, or `--`) was an
  // unknown argument in the original loop, in argv order. Collecting all non-flag
  // tokens as positionals (separator and `=` form disabled) and mapping them to
  // that same error in order reproduces it exactly.
  const { values, positionals } = parseCliArgs(argv, {
    options: {
      check: { type: "boolean", aliases: ["--check"] },
      listProperties: { type: "boolean", aliases: ["--list-properties"] },
      help: { type: "boolean", aliases: ["--help", "-h"] }
    },
    allowEquals: false,
    endOfOptions: false,
    unknownOption: "collect"
  });
  const errors = positionals.map((extra) => `Unknown argument: ${extra}`);
  return {
    check: values.check,
    listProperties: values.listProperties,
    help: values.help,
    errors
  };
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    console.log(
      "Usage: node scripts/validate-runtime-settings-docs.js [--check] [--list-properties]\n" +
        "  --check             Same as default; provided so CI scripts can declare intent.\n" +
        "  --list-properties   Print 'source: A,B,C' and 'doc: A,B,D' for debugging."
    );
    return 0;
  }
  if (args.errors.length > 0) {
    for (const message of args.errors) {
      console.error(message);
    }
    return 1;
  }

  if (args.listProperties) {
    const result = validate();
    console.log(`source: ${result.sourceNames.join(",")}`);
    console.log(`doc: ${result.docNames.join(",")}`);
    return 0;
  }

  const result = validate();
  return reportResult(result);
}

if (require.main === module) {
  process.exitCode = main();
}

module.exports = {
  REPO_ROOT,
  DEFAULT_SOURCE_PATH,
  DEFAULT_DOC_PATH,
  PROPERTY_MODIFIERS,
  TYPE_DECLARATION_KEYWORDS,
  stripBom,
  normalizeLineEndings,
  stripStringsAndComments,
  consumeTypeToken,
  extractPropertyNameFromCandidate,
  extractPublicReadOnlyProperties,
  extractDocPropertyNames,
  parseMarkdownTableRow,
  stripBackticks,
  diffPropertySets,
  readFileIfExists,
  validate,
  reportResult,
  parseArgs,
  main
};
