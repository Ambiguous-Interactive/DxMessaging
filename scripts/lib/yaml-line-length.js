"use strict";

/**
 * yaml-line-length.js
 *
 * Single source of truth for the repository's YAML line-length policy and a
 * faithful Node port of yamllint's `line-length` rule. The port lets agentic
 * and editor flows catch line-length violations WITHOUT shelling out to the
 * full Python `pre-commit`/`yamllint` stack, and powers a safe auto-rewriter
 * for the dominant offender shape in this repo: long PowerShell string
 * literals embedded inside GitHub Actions `run: |` block scalars.
 *
 * Correctness contract for `findLineLengthViolations`:
 *   - EXACT for syntactically-VALID YAML. It is a line-by-line port of
 *     yamllint 1.38.0 `yamllint/rules/line_length.py` (`check` +
 *     `check_inline_mapping`), so the same lines are flagged and the same lines
 *     are exempted via `allow-non-breakable-words` and
 *     `allow-non-breakable-inline-mappings` (a naive `length > max` check would
 *     false-positive on those). Parity over the tracked, valid corpus is
 *     enforced by a Jest test that runs the real `yamllint` binary and asserts
 *     identical findings.
 *   - On syntactically-INVALID YAML the results MAY DIVERGE from yamllint. Real
 *     yamllint runs a full PyYAML scanner first; a syntax error raises a
 *     ScannerError and SUPPRESSES the line-length rule for that document, while
 *     this hand-rolled scanner has no full parser and still reports per-line.
 *     Differential fuzzing (~20k inputs) found ~1019 such divergences, ALL
 *     co-occurring with a YAML syntax error and 0 on valid YAML. This is an
 *     accepted, documented boundary: real yamllint remains AUTHORITATIVE and is
 *     always the final gate (pre-commit + CI). The Node port is a fast
 *     pre-filter for valid YAML, not a yamllint replacement.
 *
 * Pure module: no side effects at load. `resolveYamlLineLengthPolicy` reads a
 * config file; everything else is pure over strings.
 */

const fs = require("fs");
const { normalizeToLf } = require("./quote-parser");

const DEFAULT_MAX_LINE_LENGTH = 200;

// Fallback policy used ONLY when `.yamllint.yaml` is unreadable; the resolved
// policy normally comes from this repo's `.yamllint.yaml` `line-length` block.
// NOTE: these are NOT yamllint's upstream defaults (which are max 80 and
// allow-non-breakable-inline-mappings false). `allow-non-breakable-inline-
// mappings`, when true, implies allow-non-breakable-words -- see
// resolveYamlLineLengthPolicy.
const DEFAULT_POLICY = Object.freeze({
  max: DEFAULT_MAX_LINE_LENGTH,
  allowNonBreakableWords: true,
  allowNonBreakableInlineMappings: false
});

/**
 * Parse a YAML boolean scalar (`true`/`false`, case-insensitive). Returns null
 * for anything else so callers can keep their existing default.
 *
 * @param {string} rawValue Raw scalar text.
 * @returns {boolean|null} Parsed boolean or null when unparseable.
 */
function parseYamlBoolean(rawValue) {
  if (typeof rawValue !== "string") {
    return null;
  }

  const normalized = rawValue.trim().toLowerCase();
  if (normalized === "true") {
    return true;
  }
  if (normalized === "false") {
    return false;
  }

  return null;
}

/**
 * Number of leading-space columns on a line.
 *
 * @param {string} line Raw line text.
 * @returns {number} Count of leading whitespace characters.
 */
function getIndent(line) {
  return line.length - line.trimStart().length;
}

/**
 * Resolve the effective `line-length` policy from a `.yamllint.yaml` file.
 *
 * This is the SINGLE SOURCE OF TRUTH for the policy: both the comment-wrapping
 * fixer and the block-scalar line-length engine consume it. It parses only the
 * `line-length` sub-block (max + the two allow-* booleans) with a small,
 * defensive scanner -- it does not pull in a full YAML parser, matching the
 * existing tooling's zero-runtime-dependency constraint.
 *
 * @param {string} configPath Absolute path to `.yamllint.yaml`.
 * @returns {{max:number, allowNonBreakableWords:boolean,
 *   allowNonBreakableInlineMappings:boolean}} Resolved policy.
 */
function resolveYamlLineLengthPolicy(configPath) {
  const policy = {
    max: DEFAULT_POLICY.max,
    allowNonBreakableWords: DEFAULT_POLICY.allowNonBreakableWords,
    allowNonBreakableInlineMappings: DEFAULT_POLICY.allowNonBreakableInlineMappings
  };

  let content;
  try {
    content = fs.readFileSync(configPath, "utf8");
  } catch (_error) {
    return policy;
  }

  const lines = normalizeToLf(content).split("\n");
  let inLineLengthBlock = false;
  let blockIndent = -1;

  for (const line of lines) {
    const trimmed = line.trim();

    if (!inLineLengthBlock) {
      if (/^\s*line-length:\s*(?:#.*)?$/.test(line)) {
        inLineLengthBlock = true;
        blockIndent = getIndent(line);
      }
      continue;
    }

    if (trimmed.length === 0 || trimmed.startsWith("#")) {
      continue;
    }

    const indent = getIndent(line);
    if (indent <= blockIndent) {
      break;
    }

    const maxMatch = /^\s*max:\s*([0-9]+)\s*(?:#.*)?$/.exec(line);
    if (maxMatch) {
      const parsedMax = Number.parseInt(maxMatch[1], 10);
      if (Number.isFinite(parsedMax) && parsedMax > 0) {
        policy.max = parsedMax;
      }
      continue;
    }

    const allowWordsMatch = /^\s*allow-non-breakable-words:\s*([^#]+?)\s*(?:#.*)?$/.exec(line);
    if (allowWordsMatch) {
      const parsedBoolean = parseYamlBoolean(allowWordsMatch[1]);
      if (parsedBoolean !== null) {
        policy.allowNonBreakableWords = parsedBoolean;
      }
      continue;
    }

    const allowInlineMappingsMatch =
      /^\s*allow-non-breakable-inline-mappings:\s*([^#]+?)\s*(?:#.*)?$/.exec(line);
    if (!allowInlineMappingsMatch) {
      continue;
    }

    const parsedBoolean = parseYamlBoolean(allowInlineMappingsMatch[1]);
    if (parsedBoolean !== null) {
      policy.allowNonBreakableInlineMappings = parsedBoolean;
    }
  }

  // yamllint: `allow-non-breakable-inline-mappings` implies words.
  if (policy.allowNonBreakableInlineMappings) {
    policy.allowNonBreakableWords = true;
  }

  return policy;
}

// ---------------------------------------------------------------------------
// Canonical comment-line wrapping
//
// SINGLE SOURCE OF TRUTH for wrapping long `#` comment lines to the policy
// ceiling. Both the commit-time CLI (scripts/fix-yaml-comments-line-length.js)
// and the agentic PostToolUse guard (scripts/hooks/yaml-line-length-guard.js)
// import these so the two layers cannot silently diverge. The parity is locked
// by a Jest test (identical function reference / identical output).
// ---------------------------------------------------------------------------

/**
 * Split comment text into non-empty whitespace-delimited words.
 *
 * @param {string} text Comment text (without the `#` prefix).
 * @returns {string[]} Trimmed, non-empty words in order.
 */
function splitWords(text) {
  return text
    .split(/\s+/)
    .map((word) => word.trim())
    .filter((word) => word.length > 0);
}

/**
 * Wrap a single `#` comment line to `maxLength`. Returns `[line]` unchanged when
 * the line fits, is not a comment, or (when `allowNonBreakableWords` is set)
 * contains a non-breakable word wider than the available budget -- matching
 * yamllint's `allow-non-breakable-words` exemption.
 *
 * @param {string} line Raw line text.
 * @param {number} maxLength Policy max line length.
 * @param {{allowNonBreakableWords?:boolean}} [options] Wrap options.
 * @returns {string[]} One or more wrapped lines.
 */
function wrapCommentLine(line, maxLength, options = {}) {
  if (line.length <= maxLength) {
    return [line];
  }

  const commentMatch = /^(\s*#\s?)(.*)$/.exec(line);
  if (!commentMatch) {
    return [line];
  }

  const prefix = commentMatch[1];
  const commentText = commentMatch[2].trim();
  if (commentText.length === 0) {
    return [line];
  }

  const available = maxLength - prefix.length;
  if (available <= 0) {
    return [line];
  }

  const words = splitWords(commentText);
  if (words.length === 0) {
    return [line];
  }

  if (options.allowNonBreakableWords === true && words.some((word) => word.length > available)) {
    return [line];
  }

  const wrapped = [];
  let current = "";

  for (const word of words) {
    if (current.length === 0) {
      current = word;
      continue;
    }

    if (current.length + 1 + word.length <= available) {
      current += ` ${word}`;
      continue;
    }

    wrapped.push(`${prefix}${current}`);
    current = word;
  }

  if (current.length > 0) {
    wrapped.push(`${prefix}${current}`);
  }

  if (wrapped.length === 0) {
    return [line];
  }

  return wrapped;
}

/**
 * Wrap every `#` comment line in a YAML document to the policy ceiling. Shared
 * helper behind both the CLI fixer and the agentic guard so the comment-wrap
 * behavior is sourced from one place.
 *
 * @param {string} content Full file content (any EOL; normalized internally).
 * @param {{max:number, allowNonBreakableWords:boolean}} policy Resolved policy.
 * @returns {{content:string, changedLines:number[]}} The rewritten content (LF)
 *   and the 1-based original line numbers that were wrapped.
 */
function wrapYamlCommentLines(content, policy) {
  const normalized = normalizeToLf(content);
  const lines = normalized.split("\n");
  const rewritten = [];
  const changedLines = [];

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const wrapped = wrapCommentLine(line, policy.max, {
      allowNonBreakableWords: policy.allowNonBreakableWords
    });

    rewritten.push(...wrapped);

    if (wrapped.length !== 1 || wrapped[0] !== line) {
      changedLines.push(index + 1);
    }
  }

  return {
    content: rewritten.join("\n"),
    changedLines
  };
}

/**
 * Split a single physical YAML line into characters by Unicode code point.
 *
 * yamllint measures `line.end - line.start` over a Python `str`, which counts
 * code points, not UTF-16 units. We mirror that so an astral character (e.g. an
 * emoji) counts as one, exactly like yamllint. For all-BMP text this equals
 * `line.length`.
 *
 * @param {string} line Raw line text (no terminator).
 * @returns {string[]} Array of code-point strings.
 */
function toCodePoints(line) {
  return Array.from(line);
}

/**
 * Find the next unquoted YAML value indicator -- a `:` followed by a space, a
 * tab, or end-of-string -- at or after `from`, skipping over quoted spans.
 *
 * @param {string} text Line content.
 * @param {number} from Start index.
 * @returns {number} Index of the `:` separator, or -1 when none.
 */
function findValueSeparator(text, from) {
  let inSingle = false;
  let inDouble = false;
  for (let scan = from; scan < text.length; scan += 1) {
    const ch = text[scan];
    if (inSingle) {
      if (ch === "'") {
        // YAML single-quote escape is '' -- consume the pair.
        if (text[scan + 1] === "'") {
          scan += 1;
        } else {
          inSingle = false;
        }
      }
      continue;
    }
    if (inDouble) {
      if (ch === "\\") {
        scan += 1;
      } else if (ch === '"') {
        inDouble = false;
      }
      continue;
    }
    if (ch === "'") {
      inSingle = true;
      continue;
    }
    if (ch === '"') {
      inDouble = true;
      continue;
    }
    if (ch === "#") {
      // A '#' begins a comment; nothing past it is a value indicator.
      return -1;
    }
    if (ch === ":") {
      const next = scan + 1;
      if (next >= text.length || text[next] === " " || text[next] === "\t") {
        return scan;
      }
    }
  }
  return -1;
}

/**
 * Faithful port of yamllint's `check_inline_mapping(line)`.
 *
 * yamllint runs a YAML SafeLoader over the line content. After the first
 * `BlockMappingStartToken` it walks tokens looking for `ValueToken`s; for the
 * FIRST `ValueToken` whose immediately-following token is a `ScalarToken`, it
 * returns `' ' not in line.content[scalar.start_mark.column:]`. If the token
 * after a `ValueToken` is NOT a scalar (an alias `*`, anchor `&`, tag `!`, or a
 * flow `{`/`[` start), that `ValueToken` yields nothing and the walk continues
 * to the next one (which only exists for nested flow mappings). If no such
 * scalar value is ever found, it returns false.
 *
 * We reproduce that decision without a full PyYAML scanner by locating value
 * indicators (`:` + break) outside quotes and classifying the value's first
 * non-space character the way PyYAML's token stream does:
 *
 *   - `*` (alias), `&` (anchor), `!` (tag): the token after the `ValueToken` is
 *     not a scalar, so this separator yields no result -> continue scanning for
 *     a deeper `:` (e.g. inside a flow mapping); if none, return false. This
 *     matches PyYAML emitting Alias/Anchor/Tag tokens before any scalar.
 *   - `{` (flow mapping): the value is a flow mapping; PyYAML descends into it,
 *     so the result is decided by the first inner `key: value` whose value is a
 *     scalar. We recurse from just inside the `{`.
 *   - `[` (flow sequence): PyYAML emits FlowSequenceStart (not a scalar) and no
 *     ValueToken at that level, so the walk ends without a scalar value ->
 *     false.
 *   - A TAB between the `:` and the value means PyYAML does not produce a clean
 *     inline scalar value here, so we treat it as no result and keep scanning.
 *   - Otherwise the value is a plain or quoted scalar starting at the first
 *     non-space; the "no space after value start" test uses that column through
 *     end of line INCLUDING quote characters (yamllint slices the raw buffer,
 *     so a quoted value like `'a b'` -- which contains a space -- is NOT
 *     exempt).
 *
 * Validated against the real `check_inline_mapping` by a fuzz/parity comparison
 * in the test suite, in addition to the corpus parity against the real yamllint
 * binary.
 *
 * @param {string} lineContent The raw line content (yamllint's line.content).
 * @returns {boolean} True when the line is an exempt non-breakable inline
 *   mapping.
 */
function checkInlineMapping(lineContent) {
  const text = String(lineContent);
  let searchFrom = 0;

  // Walk every value indicator at this level (and into flow mappings) until one
  // yields a scalar value, mirroring PyYAML's "first ValueToken followed by a
  // ScalarToken wins" behavior.
  for (;;) {
    const separatorIndex = findValueSeparator(text, searchFrom);
    if (separatorIndex === -1) {
      return false;
    }

    // Locate the value: first non-space after the separator. A tab in the gap
    // means no clean inline scalar value here.
    let cursor = separatorIndex + 1;
    let sawTab = false;
    while (cursor < text.length && (text[cursor] === " " || text[cursor] === "\t")) {
      if (text[cursor] === "\t") {
        sawTab = true;
      }
      cursor += 1;
    }

    // A tab where a value should start makes PyYAML raise a ScannerError
    // ("found character '\t' that cannot start any token"), which
    // check_inline_mapping catches and turns into false. Bail out entirely.
    if (sawTab) {
      return false;
    }

    // Empty value (e.g. `key:` at EOL): this separator yields nothing; keep
    // scanning for a deeper indicator.
    if (cursor >= text.length) {
      searchFrom = separatorIndex + 1;
      continue;
    }

    const valueChar = text[cursor];
    if (
      valueChar === "*" ||
      valueChar === "&" ||
      valueChar === "!" ||
      valueChar === "[" ||
      valueChar === "," ||
      valueChar === "}" ||
      valueChar === "]"
    ) {
      // Alias/anchor/tag/flow-sequence-start/flow-punctuation: the token after
      // the ValueToken is not a scalar. Keep scanning for a deeper indicator.
      searchFrom = separatorIndex + 1;
      continue;
    }
    if (valueChar === "{") {
      // Flow mapping value: descend; the inner mapping decides the result.
      searchFrom = cursor + 1;
      continue;
    }
    if (valueChar === "|" || valueChar === ">") {
      // Block-scalar indicator. When immediately followed by content (`|x`),
      // PyYAML raises a ScannerError -> false. A bare `|`/`>` produces an empty
      // scalar value with no space after it, so it is exempt (true).
      const after = text[cursor + 1];
      if (after !== undefined && after !== " ") {
        return false;
      }
      return text.indexOf(" ", cursor) === -1;
    }

    // Plain or quoted scalar value: yamllint tests
    // ' ' not in line.content[value_start_column:].
    return text.indexOf(" ", cursor) === -1;
  }
}

/**
 * Faithful port of yamllint's `line-length` rule `check` over a full document.
 *
 * CONTRACT (see the module header for the full statement): EXACT for
 * syntactically-VALID YAML. On syntactically-INVALID YAML this may diverge from
 * yamllint, which raises a syntax error and suppresses the line-length rule for
 * the document; real yamllint remains authoritative and is the final gate
 * (pre-commit + CI). The test suite pins both a known false-negative and a known
 * false-positive on invalid YAML to document this boundary.
 *
 * Mirrors `yamllint/rules/line_length.py` exactly:
 *   length = end - start (code points; CRLF terminator excluded)
 *   if length > max:
 *     allow_words |= allow_inline_mappings
 *     if allow_words:
 *       skip leading spaces -> start
 *       if start != end:
 *         if buffer[start] == '#': skip all '#', then +1
 *         elif buffer[start] == '-': start += 2
 *         if no space in (start, end): return (exempt)
 *         if allow_inline_mappings and check_inline_mapping(line): return
 *     yield problem at column max+1
 *
 * @param {string} content Full file content (any EOL; normalized internally).
 * @param {{max:number, allowNonBreakableWords:boolean,
 *   allowNonBreakableInlineMappings:boolean}} policy Resolved policy.
 * @returns {Array<{line:number, length:number, column:number}>} Violations in
 *   document order, one per offending physical line, with yamllint's reported
 *   column (`max + 1`).
 */
function findLineLengthViolations(content, policy) {
  const maxLength = policy.max;
  const allowNonBreakableInlineMappings = policy.allowNonBreakableInlineMappings === true;
  // yamllint mutates a local copy: allow_words |= allow_inline_mappings.
  const allowNonBreakableWords =
    policy.allowNonBreakableWords === true || allowNonBreakableInlineMappings;

  const normalized = normalizeToLf(content);
  const lines = normalized.split("\n");
  const violations = [];

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const codePoints = toCodePoints(line);
    const length = codePoints.length;

    if (length <= maxLength) {
      continue;
    }

    if (allowNonBreakableWords) {
      // Skip leading spaces.
      let start = 0;
      while (start < length && codePoints[start] === " ") {
        start += 1;
      }

      if (start !== length) {
        if (codePoints[start] === "#") {
          while (start < length && codePoints[start] === "#") {
            start += 1;
          }
          start += 1;
        } else if (codePoints[start] === "-") {
          start += 2;
        }

        // buffer.find(' ', start, end) == -1  ->  no space in remainder.
        let spaceFound = false;
        for (let scan = start; scan < length; scan += 1) {
          if (codePoints[scan] === " ") {
            spaceFound = true;
            break;
          }
        }
        if (!spaceFound) {
          continue;
        }

        if (allowNonBreakableInlineMappings && checkInlineMapping(line)) {
          continue;
        }
      }
    }

    violations.push({
      line: index + 1,
      length,
      column: maxLength + 1
    });
  }

  return violations;
}

// ---------------------------------------------------------------------------
// Safe PowerShell block-scalar line rewriter
//
// Long lines inside GitHub Actions `run: |` block scalars have no automated
// recovery today (the comment fixer deliberately skips code), so they always
// fall through to the last-resort yamllint hook. The dominant offender shape in
// this repo is a single long PowerShell double-quoted string literal, e.g.
//
//     Write-Output "::error title=... very long message ..."
//
// We rewrite ONLY that provably-safe shape: a line of the form
//
//     <indent><prefix>"<double-quoted string>"
//
// (with nothing significant after the closing quote) into a parenthesized
// PowerShell string concatenation that reproduces the IDENTICAL runtime string
// and keeps every physical line within the policy max:
//
//     <indent><prefix>("<piece 1>" +
//     <indent>"<piece 2>" +
//     <indent>"<piece N>")
//
// PowerShell 5.1-safe: only the `+` binary operator is used (no `??`, no
// ternary, no here-strings). A line ending in a binary `+` continues onto the
// next line, so the parentheses + `+` form is a single expression. The split
// points are PLAIN spaces in literal text only -- never inside `$(...)` or
// `${...}` subexpressions, and never a space that is escaped by a backtick --
// so interpolation tokens like `$env:RUNNER_NAME` stay intact and the
// concatenation is byte-identical to the original literal.
// ---------------------------------------------------------------------------

// The continuation suffix appended to every physical line except the last.
const PS_CONCAT_SUFFIX = " +";

// Remediation pointer used when a long block-scalar line cannot be safely
// rewritten. Mirrors the GitHub Actions externalization skill the separate
// workstream adds at .llm/skills/github-actions/.
const BLOCK_SCALAR_REMEDIATION =
  "Shorten the line, or externalize the script to a versioned .ps1/.js/.sh " +
  "file (see .llm/skills/github-actions/).";

/**
 * Scan a PowerShell double-quoted string body and return the indices of plain
 * literal spaces that are SAFE split points: depth 0 (outside every `$(...)`
 * and `${...}` span) and not escaped by a preceding backtick.
 *
 * The string body excludes the surrounding double quotes. PowerShell escape
 * char is the backtick (`` ` ``); `""` is an escaped double quote inside a
 * double-quoted string but cannot appear here because the body is already
 * delimited by the outer quotes (the caller extracts a balanced literal).
 *
 * @param {string} body Double-quoted string contents (without the quotes).
 * @returns {number[]} Indices (into `body`) of safe split spaces.
 */
function findPowerShellSplitSpaces(body) {
  const indices = [];
  let parenDepth = 0; // inside $( ... )
  let braceDepth = 0; // inside ${ ... }
  for (let i = 0; i < body.length; i += 1) {
    const ch = body[i];
    if (ch === "`") {
      // Backtick escapes the next character; skip the pair.
      i += 1;
      continue;
    }
    if (ch === "$" && body[i + 1] === "(") {
      parenDepth += 1;
      i += 1;
      continue;
    }
    if (ch === "$" && body[i + 1] === "{") {
      braceDepth += 1;
      i += 1;
      continue;
    }
    if (ch === ")" && parenDepth > 0) {
      parenDepth -= 1;
      continue;
    }
    if (ch === "}" && braceDepth > 0) {
      braceDepth -= 1;
      continue;
    }
    if (ch === " " && parenDepth === 0 && braceDepth === 0) {
      indices.push(i);
    }
  }
  return indices;
}

/**
 * Parse a candidate physical line into the provably-safe rewrite shape, or
 * return null when it is not that shape.
 *
 * Safe shape: `<indent><prefix>"<body>"` where:
 *   - indent is leading spaces,
 *   - prefix is non-empty PowerShell code that does NOT itself contain a double
 *     quote (so the first `"` we see opens the literal),
 *   - the literal is a balanced double-quoted string (backtick-escaped quotes
 *     respected),
 *   - the closing quote is the END of the line (nothing significant follows).
 *
 * @param {string} line Raw physical line.
 * @returns {{indent:string, prefix:string, body:string}|null} Parsed parts or
 *   null when the line is not the safe shape.
 */
function parsePowerShellStringLine(line) {
  const indentMatch = /^(\s*)(.*)$/.exec(line);
  if (!indentMatch) {
    return null;
  }
  const indent = indentMatch[1];
  const rest = indentMatch[2];

  // Reject leading-tab indentation (YAML block scalars use spaces; we must
  // reproduce indentation verbatim and our width math assumes space columns).
  if (indent.includes("\t")) {
    return null;
  }

  const openQuote = rest.indexOf('"');
  if (openQuote === -1) {
    return null;
  }
  const prefix = rest.slice(0, openQuote);
  if (prefix.length === 0) {
    return null;
  }
  // The prefix must be plain code with no quotes of either kind, so we are sure
  // the first `"` opens the literal (not, say, a single-quoted span containing
  // a double quote).
  if (prefix.includes('"') || prefix.includes("'")) {
    return null;
  }

  // Walk the literal to its matching close quote, honoring backtick escapes.
  let i = openQuote + 1;
  let body = "";
  let closed = false;
  for (; i < rest.length; i += 1) {
    const ch = rest[i];
    if (ch === "`") {
      // Escape: keep both characters in the body verbatim.
      body += ch;
      if (i + 1 < rest.length) {
        body += rest[i + 1];
        i += 1;
      }
      continue;
    }
    if (ch === '"') {
      closed = true;
      break;
    }
    body += ch;
  }

  if (!closed) {
    return null;
  }

  // Nothing significant may follow the closing quote (YAML trailing whitespace
  // is itself a violation, so we require an exact end-of-line here).
  if (i !== rest.length - 1) {
    return null;
  }

  // The body must have BALANCED `$(...)` and `${...}` subexpressions. An
  // unbalanced opener means part of the literal is an interpolation span we
  // cannot reason about; splitting near it risks changing semantics, so we
  // refuse and let the line fall to the unsafe path (manual remediation).
  if (!hasBalancedPowerShellSubexpressions(body)) {
    return null;
  }

  return { indent, prefix, body };
}

/**
 * True when every `$(...)` and `${...}` subexpression in a PowerShell
 * double-quoted body is balanced (all openers closed, no stray closers at the
 * subexpression level), honoring backtick escapes. Used to reject malformed /
 * non-breakable literals from the safe-rewrite path.
 *
 * @param {string} body Double-quoted string contents (without the quotes).
 * @returns {boolean} True when subexpression delimiters are balanced.
 */
function hasBalancedPowerShellSubexpressions(body) {
  let parenDepth = 0; // inside $( ... )
  let braceDepth = 0; // inside ${ ... }
  for (let i = 0; i < body.length; i += 1) {
    const ch = body[i];
    if (ch === "`") {
      i += 1; // backtick escapes the next char
      continue;
    }
    if (ch === "$" && body[i + 1] === "(") {
      parenDepth += 1;
      i += 1;
      continue;
    }
    if (ch === "$" && body[i + 1] === "{") {
      braceDepth += 1;
      i += 1;
      continue;
    }
    if (ch === ")" && parenDepth > 0) {
      parenDepth -= 1;
      continue;
    }
    if (ch === "}" && braceDepth > 0) {
      braceDepth -= 1;
      continue;
    }
  }
  return parenDepth === 0 && braceDepth === 0;
}

/**
 * Attempt a provably semantics-preserving rewrite of one long PowerShell
 * string-literal line into a parenthesized multi-line concatenation that fits
 * within `maxLength` per physical line.
 *
 * Returns one of:
 *   - { status: "rewritten", lines: string[] } on success,
 *   - { status: "unchanged" } when the line already fits or is not the safe
 *     shape (the latter is reported separately via `status: "unsafe"`),
 *   - { status: "unsafe", reason } when the line is too long but cannot be
 *     safely rewritten (e.g. a non-breakable token wider than the budget, or a
 *     shape we do not recognize).
 *
 * Guarantees on success:
 *   - concatenating the produced body pieces yields the EXACT original body
 *     (no spaces added or removed),
 *   - every produced physical line has code-point length <= maxLength,
 *   - the indentation and `prefix` are preserved verbatim,
 *   - interpolation/subexpression tokens are never split.
 *
 * @param {string} line Raw physical line.
 * @param {number} maxLength Policy max line length.
 * @returns {{status:string, lines?:string[], reason?:string}} Outcome.
 */
function rewritePowerShellStringLine(line, maxLength) {
  if (toCodePoints(line).length <= maxLength) {
    return { status: "unchanged" };
  }

  const parsed = parsePowerShellStringLine(line);
  if (!parsed) {
    return {
      status: "unsafe",
      reason:
        "not a single PowerShell double-quoted string of the form " + '`<indent><code>"<string>"`'
    };
  }

  const { indent, prefix, body } = parsed;

  // First physical line carries `<indent><prefix>("`; continuation lines carry
  // `<indent>"`. The closing piece carries a trailing `")`. Compute the literal
  // budget (characters of `body` content) available on each line type.
  const firstOverhead = toCodePoints(indent).length + toCodePoints(prefix).length + 2; // ( and "
  const contOverhead = toCodePoints(indent).length + 1; // leading "
  // Trailing: open piece adds `" +` (3); closing piece adds `")` (2).
  const openTail = 1 + PS_CONCAT_SUFFIX.length; // closing quote + " +"
  const closeTail = 2; // closing quote + )

  const splitSpaces = findPowerShellSplitSpaces(body);

  // Greedy packer: pick the farthest safe split space that keeps the current
  // physical line within budget; emit the piece (including the trailing space
  // at the split point so no character is lost), then continue.
  const pieces = [];
  let segStart = 0;
  let isFirst = true;

  while (segStart < body.length) {
    const overhead = isFirst ? firstOverhead : contOverhead;
    // The remainder fits on one line: it becomes the closing piece (which uses
    // the smaller close tail `")`), so size that decision against closeTail.
    const remainderLen = toCodePoints(body.slice(segStart)).length;
    if (remainderLen + overhead + closeTail <= maxLength) {
      pieces.push(body.slice(segStart));
      segStart = body.length;
      break;
    }

    // Otherwise this is a non-final piece and must end at a safe split space,
    // leaving room for the open tail `" +`. We split AFTER a space, so the space
    // stays at the end of the current piece (no character is added or removed).
    const budget = maxLength - overhead - openTail;
    let bestEnd = -1;
    for (const spaceIdx of splitSpaces) {
      const endExclusive = spaceIdx + 1; // include the space
      if (endExclusive <= segStart) {
        continue;
      }
      const pieceLen = toCodePoints(body.slice(segStart, endExclusive)).length;
      if (pieceLen <= budget) {
        bestEnd = endExclusive;
      } else {
        break;
      }
    }

    if (bestEnd === -1) {
      // No safe split fits: the next non-breakable token is wider than the
      // budget. Refuse rather than break a token / change semantics.
      return {
        status: "unsafe",
        reason:
          "contains a non-breakable token (no safe space split fits within " +
          `the ${maxLength}-char limit)`
      };
    }

    pieces.push(body.slice(segStart, bestEnd));
    segStart = bestEnd;
    isFirst = false;
  }

  if (pieces.length < 2) {
    // A single piece means we could not actually shorten the line; refuse.
    return {
      status: "unsafe",
      reason:
        "contains a non-breakable token (no safe space split fits within " +
        `the ${maxLength}-char limit)`
    };
  }

  // Render. First line opens with `<indent><prefix>("piece" +`, middle lines
  // `<indent>"piece" +`, last line `<indent>"piece")`.
  const rendered = [];
  for (let p = 0; p < pieces.length; p += 1) {
    const piece = pieces[p];
    if (p === 0) {
      rendered.push(`${indent}${prefix}("${piece}"${PS_CONCAT_SUFFIX}`);
    } else if (p === pieces.length - 1) {
      rendered.push(`${indent}"${piece}")`);
    } else {
      rendered.push(`${indent}"${piece}"${PS_CONCAT_SUFFIX}`);
    }
  }

  // Final guard: every produced line must fit, and the reconstructed literal
  // must equal the original body exactly. If either fails, refuse.
  if (rendered.some((renderedLine) => toCodePoints(renderedLine).length > maxLength)) {
    return {
      status: "unsafe",
      reason: `rewrite still exceeds ${maxLength} characters on some line`
    };
  }
  if (pieces.join("") !== body) {
    return {
      status: "unsafe",
      reason: "internal error: reconstructed string differs from original"
    };
  }

  return { status: "rewritten", lines: rendered };
}

// ---------------------------------------------------------------------------
// YAML STRUCTURAL CONTEXT DETECTION (the BLOCKER B1 fix)
//
// The concatenation rewrite above produces valid PowerShell, but applying it
// blindly corrupts any over-length line that merely LOOKS like
// `<indent><code>"<string>"` -- a bash `run:` line, a folded `>-` prose body, a
// plain mapping value. The folded-scalar case is the worst: the injected
// `(" + "` / `")` lands in prose and STILL passes yamllint (every output line
// is short), so the corruption is silent.
//
// A line is eligible for the rewrite ONLY when it is a content line inside a
// block scalar that is the value of a `run:` key, AND the enclosing step has an
// EXPLICIT `shell:` of `pwsh`/`powershell`. We never infer GitHub's default
// shells. `findPwshRunBlockScalarLines` computes the eligible line set with a
// hand-rolled structural pass (no YAML dependency, consistent with the rest of
// this lib).
//
// Step segmentation: a step is a `- ` sequence item. Its sibling keys
// (`shell:`, `run:`, `name:`, ...) share the key column that starts just after
// `- `; the step ends at the next sibling `- ` at the same marker indent or at
// any dedent below that marker indent. A block-scalar body is always indented
// STRICTLY deeper than its `run:` key (which is itself deeper than the `- `), so
// body lines can never be misread as step markers or sibling keys.
// ---------------------------------------------------------------------------

// Block-scalar value indicator: `|` or `>` followed by an OPTIONAL indentation
// indicator (a single digit 1-9) and an OPTIONAL chomping indicator (`-`/`+`),
// in EITHER order and at most one of each, then an optional inline comment, to
// EOL. e.g. `|`, `|-`, `>+`, `|2`, `|2-`, `|-2`, `>-`, `| # keep`. This rejects
// invalid headers like `|2-3` (two digits).
const BLOCK_SCALAR_INDICATOR = /^[|>](?:[1-9][-+]?|[-+][1-9]?)?(?:\s+#.*)?$/;

// An explicit PowerShell shell value: pwsh / powershell, optionally quoted,
// case-insensitive, with an optional trailing comment.
const PWSH_SHELL_VALUE = /^["']?(pwsh|powershell)["']?\s*(?:#.*)?$/i;

/**
 * True when `trimmed` (a line with leading indent already removed) is a block
 * scalar opener for the given key, e.g. `run: |`, `run: >-`, `run: |2 # x`.
 * Returns a boolean (true on a match, false otherwise).
 *
 * @param {string} trimmed The de-indented line content.
 * @param {string} keyName The mapping key to match (e.g. "run").
 * @returns {boolean} True when this is `<keyName>: <block-scalar-indicator>`.
 */
function isBlockScalarOpenerFor(trimmed, keyName) {
  // Allow an optional leading `- ` so a `run:` that is the first key of a step
  // item (`- run: |`) is recognized; the indent math is handled by the caller.
  const stripped = trimmed.replace(/^-\s+/, "");
  const prefix = `${keyName}:`;
  if (!stripped.startsWith(prefix)) {
    return false;
  }
  const after = stripped.slice(prefix.length).trim();
  if (after.length === 0) {
    return false;
  }
  return BLOCK_SCALAR_INDICATOR.test(after);
}

/**
 * Compute the 1-based line numbers that are CONTENT lines of a `run:` block
 * scalar whose enclosing step has an explicit `shell: pwsh`/`shell: powershell`.
 * Only these lines are eligible for the PowerShell concatenation rewrite.
 *
 * Algorithm (two logical passes, single physical scan with a step model):
 *   1. Walk lines. A step begins at a sequence-item marker `- ` (we record the
 *      marker indent and the key column = marker indent + 2). Successive
 *      non-blank lines at the key column are sibling keys of the SAME step until
 *      a `- ` at the marker indent (next step) or a dedent below the marker
 *      indent (end of the steps list / parent dedent).
 *   2. Within a step, detect `shell:` (capture its plain/quoted value) and the
 *      `run:` block-scalar opener (capture the body extent: subsequent lines
 *      that are blank OR indented strictly deeper than the `run:` key column;
 *      the body ends at the first non-blank line indented at or below that
 *      column). Because `shell:` may appear BEFORE or AFTER `run:`, we collect
 *      the run-body line ranges per step first, then emit them only if that
 *      step's resolved shell is PowerShell.
 *
 * Conservative by construction: a `run:` that is NOT inside a `- ` step item
 * (rare, but e.g. a top-level reusable snippet) has no associated `shell:` and
 * is therefore never eligible. We only ever ENABLE the rewrite; on any
 * ambiguity the line is left to the unsafe path.
 *
 * @param {string} content Full file content (any EOL; normalized internally).
 * @returns {Set<number>} 1-based eligible content line numbers.
 */
function findPwshRunBlockScalarLines(content) {
  const normalized = normalizeToLf(content);
  const lines = normalized.split("\n");
  const eligible = new Set();

  // Active step model. When stepKeyIndent === -1 we are not inside a step.
  let stepMarkerIndent = -1; // column of the `-` marker
  let stepKeyIndent = -1; // column where sibling keys start (marker + 2)
  let stepShellIsPwsh = false;
  let stepRunBodies = []; // arrays of 1-based line numbers (run block bodies)

  // Active run block-scalar body capture inside the current step.
  let runKeyIndent = -1; // indentation of the `run:` key (-1 = not capturing)
  let currentRunBody = null; // line numbers being collected for the active run

  const flushStep = () => {
    if (currentRunBody && currentRunBody.length > 0) {
      stepRunBodies.push(currentRunBody);
    }
    currentRunBody = null;
    runKeyIndent = -1;
    if (stepShellIsPwsh) {
      for (const body of stepRunBodies) {
        for (const lineNo of body) {
          eligible.add(lineNo);
        }
      }
    }
    stepMarkerIndent = -1;
    stepKeyIndent = -1;
    stepShellIsPwsh = false;
    stepRunBodies = [];
  };

  for (let index = 0; index < lines.length; index += 1) {
    const lineNo = index + 1;
    const line = lines[index];
    const trimmed = line.trim();
    const indent = getIndent(line);
    const isBlank = trimmed.length === 0;

    // While capturing a run block-scalar body, consume blank lines and lines
    // indented strictly deeper than the `run:` key. The body ends at the first
    // non-blank line at or below the run-key indent.
    if (runKeyIndent !== -1) {
      if (isBlank) {
        // A blank line is part of the body ONLY if a deeper line follows; but
        // for line-length purposes a blank line is never over-length, so it is
        // harmless to record. Record it to keep the body contiguous.
        currentRunBody.push(lineNo);
        continue;
      }
      if (indent > runKeyIndent) {
        currentRunBody.push(lineNo);
        continue;
      }
      // Dedent: the run body ended on the previous line. Close it and fall
      // through to re-classify THIS line as a sibling key / new step / dedent.
      if (currentRunBody.length > 0) {
        stepRunBodies.push(currentRunBody);
      }
      currentRunBody = null;
      runKeyIndent = -1;
    }

    if (isBlank) {
      continue;
    }

    const isSeqMarker = /^-(\s|$)/.test(trimmed);

    if (stepKeyIndent === -1) {
      // Not in a step: a `- ` marker opens one.
      if (isSeqMarker) {
        stepMarkerIndent = indent;
        stepKeyIndent = indent + 2;
        stepShellIsPwsh = false;
        stepRunBodies = [];
        inspectStepKeyLine(trimmed, indent);
      }
      continue;
    }

    // Inside a step.
    if (indent < stepKeyIndent) {
      // Dedent below the step's key column.
      if (isSeqMarker && indent === stepMarkerIndent) {
        // Next sibling step at the same marker indent.
        flushStep();
        stepMarkerIndent = indent;
        stepKeyIndent = indent + 2;
        stepShellIsPwsh = false;
        stepRunBodies = [];
        inspectStepKeyLine(trimmed, indent);
      } else {
        // True dedent out of the steps list (or a deeper-list parent). Close the
        // current step; then re-classify this line as a possible new marker.
        flushStep();
        if (isSeqMarker) {
          stepMarkerIndent = indent;
          stepKeyIndent = indent + 2;
          stepShellIsPwsh = false;
          stepRunBodies = [];
          inspectStepKeyLine(trimmed, indent);
        }
      }
      continue;
    }

    if (isSeqMarker && indent === stepMarkerIndent) {
      // A new step item at the same marker indent.
      flushStep();
      stepMarkerIndent = indent;
      stepKeyIndent = indent + 2;
      stepShellIsPwsh = false;
      stepRunBodies = [];
      inspectStepKeyLine(trimmed, indent);
      continue;
    }

    // A sibling key line of the current step (indent >= stepKeyIndent and not a
    // new marker). Only lines AT the key column are top-level step keys; deeper
    // lines belong to a nested mapping (e.g. under `with:`) and are ignored
    // here (they cannot be a `run:`/`shell:` of THIS step).
    if (indent === stepKeyIndent) {
      inspectStepKeyLine(trimmed, indent);
    }
  }

  // EOF: close any open step.
  if (stepKeyIndent !== -1 || currentRunBody) {
    flushStep();
  }

  return eligible;

  /**
   * Inspect one step-key line for `shell:` (record pwsh) or a `run:` block
   * scalar opener (begin body capture). `keyIndent` is the indentation at which
   * the key (or its `- ` marker) sits.
   */
  function inspectStepKeyLine(lineTrimmed, keyIndent) {
    // Detect an explicit PowerShell shell value (with or without a `- ` marker).
    const shellStripped = lineTrimmed.replace(/^-\s+/, "");
    const shellMatch = /^shell:\s*(.+)$/.exec(shellStripped);
    if (shellMatch && PWSH_SHELL_VALUE.test(shellMatch[1].trim())) {
      stepShellIsPwsh = true;
    }

    // Detect a `run:` block-scalar opener and begin capturing its body. The
    // body is indented strictly deeper than the `run:` key column. When `run:`
    // is the first key of the item (`- run: |`), the key column is keyIndent + 2
    // (just after the marker); otherwise it is keyIndent.
    if (isBlockScalarOpenerFor(lineTrimmed, "run")) {
      runKeyIndent = /^-\s/.test(lineTrimmed) ? keyIndent + 2 : keyIndent;
      currentRunBody = [];
    }
  }
}

/**
 * Rewrite a full YAML document, applying the safe PowerShell concatenation
 * transform to every over-length line that is BOTH (a) structurally eligible --
 * a content line of a `run:` block scalar whose step has an explicit
 * `shell: pwsh`/`shell: powershell` (see `findPwshRunBlockScalarLines`) -- AND
 * (b) the provably-safe string-literal shape that rewrites byte-identically.
 * Every other over-length line is left BYTE-IDENTICAL and reported as `unsafe`
 * (manual remediation required), never rewritten. This is the BLOCKER B1 fix:
 * the previous version rewrote ANY over-length `<indent><code>"<string>"` line
 * regardless of context, corrupting bash `run:` lines, folded scalars, and
 * plain mapping values.
 *
 * Lines that are not violations are passed through untouched. Comment-line
 * wrapping is intentionally NOT done here -- it lives in the existing comment
 * fixer; this engine owns block-scalar code lines.
 *
 * @param {string} content Full file content (any EOL; normalized internally).
 * @param {{max:number, allowNonBreakableWords:boolean,
 *   allowNonBreakableInlineMappings:boolean}} policy Resolved policy.
 * @returns {{content:string, changedLines:number[],
 *   unsafe:Array<{line:number, length:number, reason:string}>}} Result with the
 *   rewritten content, the 1-based original line numbers that were rewritten,
 *   and the over-length lines that could not be safely rewritten.
 */
function rewriteYamlBlockScalarLines(content, policy) {
  const normalized = normalizeToLf(content);
  const lines = normalized.split("\n");
  const violations = findLineLengthViolations(normalized, policy);
  const violationByLine = new Map(violations.map((v) => [v.line, v]));
  const eligibleLines = findPwshRunBlockScalarLines(normalized);

  const rewritten = [];
  const changedLines = [];
  const unsafe = [];

  for (let index = 0; index < lines.length; index += 1) {
    const lineNo = index + 1;
    const line = lines[index];
    const violation = violationByLine.get(lineNo);

    if (!violation) {
      rewritten.push(line);
      continue;
    }

    // Structural gate (B1): only a content line of a pwsh/powershell `run:`
    // block scalar may be rewritten. Anything else stays byte-identical and is
    // surfaced for manual remediation.
    if (!eligibleLines.has(lineNo)) {
      rewritten.push(line);
      unsafe.push({
        line: lineNo,
        length: violation.length,
        reason:
          "not inside a `run:` block scalar of a step with an explicit " +
          "`shell: pwsh`/`shell: powershell` (manual remediation required)"
      });
      continue;
    }

    const result = rewritePowerShellStringLine(line, policy.max);
    if (result.status === "rewritten") {
      rewritten.push(...result.lines);
      changedLines.push(lineNo);
      continue;
    }

    // Eligible context but the line is not the provably-safe shape (or has a
    // non-breakable token): keep it as-is and report.
    rewritten.push(line);
    unsafe.push({
      line: lineNo,
      length: violation.length,
      reason: result.reason || "not a safely-rewritable shape"
    });
  }

  return {
    content: rewritten.join("\n"),
    changedLines,
    unsafe
  };
}

module.exports = {
  DEFAULT_MAX_LINE_LENGTH,
  DEFAULT_POLICY,
  BLOCK_SCALAR_REMEDIATION,
  parseYamlBoolean,
  getIndent,
  resolveYamlLineLengthPolicy,
  splitWords,
  wrapCommentLine,
  wrapYamlCommentLines,
  toCodePoints,
  findValueSeparator,
  checkInlineMapping,
  findLineLengthViolations,
  findPowerShellSplitSpaces,
  hasBalancedPowerShellSubexpressions,
  parsePowerShellStringLine,
  rewritePowerShellStringLine,
  isBlockScalarOpenerFor,
  findPwshRunBlockScalarLines,
  rewriteYamlBlockScalarLines
};
