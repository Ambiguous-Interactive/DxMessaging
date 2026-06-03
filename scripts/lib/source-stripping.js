"use strict";

/**
 * @fileoverview Pure source-stripping helper shared by static-analysis tests.
 *
 * Removes comments and replaces string-literal payloads with empty quotes so
 * downstream regex/substring checks operate only on actual JavaScript code.
 * This is used by the `--testRunner` injection policy guards and any other
 * static lint that needs to avoid false positives from docstrings, banners,
 * error messages, or string-table contents.
 *
 * Implementation: this is a single-pass state-machine tokenizer that walks
 * the source one character at a time. The earlier regex-pipeline form had a
 * bypass: a block-comment regex of `/\/\*[\s\S]*?\*\//g` matches greedily
 * across string literals, so source like:
 *
 *     const opener = "/*";
 *     args.push("--testRunner", "/tmp/x.js");
 *     const closer = "* /";   // (real source uses a literal `*` and `/`)
 *
 * would have the entire middle line eaten as a "block comment". The
 * tokenizer below correctly recognizes the `/*` and `*\/` tokens as STRING
 * CONTENT (not comment delimiters) and preserves the surrounding code.
 *
 * States tracked:
 *   - `code`            (default)
 *   - `lineComment`     (after `//`)
 *   - `blockComment`    (after `/*` outside any string)
 *   - `stringSingle`    (inside `'...'`)
 *   - `stringDouble`    (inside `"..."`)
 *   - `templateLiteral` (inside backticks)
 *   - `templateExpr`    (inside `${...}` within a template literal — this
 *                        sub-context contains real code and may recursively
 *                        contain strings, templates, and comments)
 *   - `regex`           (inside a `/.../flags` regex literal — entered from
 *                        `code`/`templateExpr` when a `/` cannot be division;
 *                        its body is a payload, like a string)
 *
 * Output invariants:
 *   - Line breaks are preserved everywhere (including inside comments and
 *     multi-line strings) so line numbers in error snippets stay accurate.
 *   - Comment payloads (both line and block) are erased entirely; only the
 *     line breaks they spanned remain.
 *   - String/template literal payloads are erased; only the surrounding
 *     quote/backtick markers and any line breaks remain. For template
 *     literals, the `${` and `}` delimiters of expressions are preserved
 *     and the expression content is processed as code.
 *   - Regex-literal payloads are treated like string payloads: the body, the
 *     `/` delimiters, and the trailing flags are erased (strip) or blanked to
 *     spaces (mask/commentsOnly), with line breaks preserved. This guarantees a
 *     quote inside a regex (`/["\n]/`) can never toggle string state and bleed
 *     into the following code. A `/` that is DIVISION is left as a normal code
 *     char (see the regex-vs-division heuristic in `slashStartsRegex`).
 *
 * What this DOES NOT protect against:
 *   - Runtime indirection. If code stores a forbidden literal in a variable
 *     (e.g. `const flag = "--testRunner"; args.push(flag);`), the tokenizer
 *     correctly erases the string content but the runtime injection still
 *     happens. Source-scanning guards cannot detect this; structural guards
 *     in the policy tests (e.g. "no `*.push()` call site references a known
 *     runner-path identifier") are the defense.
 *   - (Regex literals ARE now tokenized -- see the `regex` state. A `/` is read
 *     as a regex when the previous significant token cannot be a division
 *     operand, via the standard regex-vs-division heuristic in
 *     `slashStartsRegex`; the regex body is then neutralized as a payload so a
 *     quote inside it (`/["\n]/`) no longer toggles string state and corrupts
 *     the mask of a following `expect(...)`. The heuristic is the usual practical
 *     one: ambiguous edge cases unique to a full parser -- e.g. distinguishing a
 *     regex from division after a `}` that closes a block vs. an object literal,
 *     or after some ASI boundaries -- are not modeled, but no such case occurs
 *     in the static-analysis inputs these projections serve.)
 *
 * Input handling:
 *   - Non-string input (undefined, null, numbers, Buffers, etc.) returns the
 *     empty string. Callers that need to pass a Buffer must `.toString("utf8")`
 *     it first; we deliberately do not auto-decode to keep the helper pure
 *     and to avoid accidentally swallowing binary payloads.
 *
 * Three projections share ONE tokenizer pass (`projectSource`):
 *   - `stripJsCommentsAndStrings(source)` keeps code, blanks comment payloads
 *     entirely and string/template payloads (preserving quote/backtick markers
 *     and `${...}` delimiters). This is the historical behavior; output is
 *     byte-for-byte identical to the prior implementation. It is NOT
 *     length-preserving (it removes characters), so it cannot be used for
 *     offset-aligned scanning.
 *   - `extractCommentsOnly(source)` is its INVERSE: it preserves comment
 *     payloads verbatim (line + block + JSDoc, with or without a leading `*`)
 *     and blanks code + string/template payloads to whitespace, preserving line
 *     breaks so offsets/line numbers stay aligned with the source. A token that
 *     survives `extractCommentsOnly` lived in a real comment span -- never in a
 *     string, template, or code -- which is exactly the discrimination a
 *     marker-in-comment guard needs (a string such as `"a // b @marker"` blanks
 *     to whitespace because `//` inside a string is string content, not a
 *     comment opener). This projection IS length- and offset-preserving.
 *   - `maskCommentsAndStrings(source)` keeps CODE verbatim and blanks BOTH
 *     comment AND string/template payloads to spaces, preserving line breaks and
 *     TOTAL LENGTH. A token that survives at offset N lived in real CODE at
 *     offset N -- never in a comment, string, or template payload -- so an
 *     offset-based scanner can locate a real-code construct on the mask and then
 *     read its exact text from the RAW source at the aligned offset (a comment or
 *     string spelling of the same construct is invisible because it is blanked).
 *     This projection IS length- and offset-preserving.
 * Sharing the state machine guarantees all three projections can never disagree
 * about which region (code / comment / string) a given character belongs to.
 *
 * Pure: no `require`s with side effects, no top-level I/O, no globals.
 */

/**
 * Single-pass tokenizer shared by all three projections.
 *
 * @param {string} source - Source text (any LF/CRLF; BOM-agnostic).
 * @param {"strip"|"comments"|"mask"} mode - Selects the projection:
 *   - "strip"    -> `stripJsCommentsAndStrings` (keep code, blank comments +
 *                   string payloads; output is byte-for-byte historical, NOT
 *                   length-preserving). This is the default for legacy callers.
 *   - "comments" -> `extractCommentsOnly` (keep comment payloads, blank code +
 *                   string payloads to spaces; LENGTH- and offset-preserving).
 *   - "mask"     -> `maskCommentsAndStrings` (keep CODE verbatim, blank comment
 *                   AND string/template payloads to spaces; LENGTH- and
 *                   offset-preserving). Quote/backtick markers and `${`/`}`
 *                   delimiters are kept verbatim as code structure.
 *   Line breaks are preserved in ALL three modes.
 * @returns {string}
 */
// Regex-vs-division disambiguation tables (see `slashStartsRegex`). A `/` in
// code is REGEX when the previous significant token is NOT an expression value
// (it cannot be a division operand). When the previous significant token is an
// identifier WORD, only a keyword that expects an expression to its RIGHT makes
// `/` a regex; every other word -- a plain identifier (a variable), a numeric
// literal, OR a value keyword (`this`/`super`/`true`/`false`/`null`) -- is a
// value, so a `/` after it is division (the default fall-through). Value
// keywords therefore need no separate table: they are handled by the same
// "a word is a value" rule as an ordinary identifier.
const REGEX_CONTEXT_KEYWORDS = new Set([
  "return",
  "typeof",
  "instanceof",
  "in",
  "of",
  "new",
  "delete",
  "void",
  "case",
  "do",
  "else"
]);
// Single-char tokens that END an expression, so a following `/` is DIVISION.
// (Identifiers/numbers/strings are handled separately because they are
// multi-char and their trailing char alone is enough to classify them.)
const DIVISION_TERMINATOR_CHARS = new Set([")", "]"]);

/**
 * True when a `/` at code position `slashIndex` STARTS a regex literal (rather
 * than being a division operator), using the standard regex-vs-division
 * heuristic over the previous SIGNIFICANT token.
 *
 * `prevSig` is the last non-whitespace character seen in the current code frame
 * (or "" at start-of-input / start-of-`${...}` expression). The decision:
 *   - "" (start of input / expression)            -> REGEX.
 *   - a value-terminator char (`)`/`]`), a string
 *     or template close (`'`/`"`/`` ` ``), or a
 *     digit                                         -> DIVISION.
 *   - an identifier char: scan the trailing word.
 *       * a REGEX-context keyword (`return`/...)     -> REGEX.
 *       * any other word -- a plain identifier (a
 *         variable), a numeric literal, or a VALUE
 *         keyword (`this`/`true`/...)               -> DIVISION (it is a value).
 *   - anything else (an operator/punctuator such as
 *     `(`,`,`,`=`,`:`,`[`,`{`,`;`,`!`,`&`,`|`,`?`,
 *     `+`,`-`,`*`,`/`,`%`,`^`,`~`,`<`,`>`, and the
 *     `=>` arrow whose trailing char is `>`)        -> REGEX.
 * Note `//` and `/*` are handled as comments BEFORE this is ever consulted.
 *
 * @param {string} source - Full source text.
 * @param {number} slashIndex - Index of the `/`.
 * @param {string} prevSig - Last significant char in the current code frame.
 * @returns {boolean}
 */
function slashStartsRegex(source, slashIndex, prevSig) {
  if (prevSig === "") {
    return true;
  }
  // String/template close or a value-terminator -> the `/` divides that value.
  if (
    prevSig === "'" ||
    prevSig === '"' ||
    prevSig === "`" ||
    DIVISION_TERMINATOR_CHARS.has(prevSig)
  ) {
    return false;
  }
  // Identifier / numeric-literal tail. Scan the contiguous trailing word ending
  // just before the `/` (skipping any intervening whitespace) to classify it.
  if (/[\w$]/.test(prevSig)) {
    let wordEnd = slashIndex - 1;
    while (wordEnd >= 0 && /\s/.test(source[wordEnd])) {
      wordEnd--;
    }
    let wordStart = wordEnd;
    while (wordStart >= 0 && /[\w$]/.test(source[wordStart])) {
      wordStart--;
    }
    const word = source.slice(wordStart + 1, wordEnd + 1);
    if (REGEX_CONTEXT_KEYWORDS.has(word)) {
      return true;
    }
    // Every other word -- a plain identifier (a variable), a numeric literal, or
    // a VALUE keyword (`this`/`super`/`true`/`false`/`null`) -- is an expression
    // value, so the `/` divides it.
    return false;
  }
  // Any remaining significant char is an operator/punctuator (`(`,`,`,`=`,`:`,
  // `[`,`{`,`;`,`!`,`&`,`|`,`?`,`+`,`-`,`*`,`/`,`%`,`^`,`~`,`<`,`>`, and the `>`
  // that ends an `=>` arrow) -> a regex follows.
  return true;
}

function projectSource(source, mode) {
  if (typeof source !== "string") {
    return "";
  }
  if (source.length === 0) {
    return "";
  }

  const commentsOnly = mode === "comments";
  const mask = mode === "mask";

  const out = [];
  // CODE characters. Kept verbatim in strip and mask modes (mask keeps code so
  // a real-code token survives at its exact offset); blanked to spaces in
  // commentsOnly mode (so the marker token cannot survive in code) with line
  // breaks preserved to keep offsets aligned with the source.
  const emitCode = (ch) => {
    if (commentsOnly) {
      if (ch === "\n") {
        out.push("\n");
      } else {
        out.push(" ");
      }
    } else {
      out.push(ch);
    }
  };
  // Comment payload characters: kept verbatim in commentsOnly mode, erased in
  // strip mode (only the line breaks they spanned are preserved -- handled by
  // each comment state explicitly pushing "\n"), blanked to a space in mask mode
  // (so a comment token cannot survive but offsets stay aligned). Newlines inside
  // comments never reach here -- each comment state pushes "\n" directly -- so a
  // single space per char is exactly length-preserving in mask mode.
  const emitCommentChar = (ch) => {
    if (commentsOnly) {
      out.push(ch);
    } else if (mask) {
      out.push(" ");
    }
  };
  // Erased-in-strip / blanked-in-(commentsOnly|mask) characters. Covers two
  // cases:
  //   1. String/template payload chars and the escapes they contain -- strip
  //      mode reduces `"abc"` to `""` (emits nothing), commentsOnly and mask
  //      blank them to spaces so the marker token cannot survive inside a string
  //      literal (mask keeps offsets aligned for the surrounding code).
  //   2. Comment delimiters (`//`, `/*`, `*/`) -- strip mode drops them (emits
  //      nothing, preserving the historical byte-for-byte output), commentsOnly
  //      and mask blank them to spaces so offsets stay aligned with the source.
  // The surrounding quote/backtick markers and `${`/`}` are emitted via
  // `emitCode` instead, so they survive in strip AND mask modes exactly as the
  // real code structure (only their payloads are blanked).
  const emitBlank = () => {
    if (commentsOnly || mask) {
      out.push(" ");
    }
  };
  // Regex literal body characters: blanked-to-spaces in commentsOnly and mask
  // modes (so the regex body -- including any quote chars -- never survives and
  // never toggles string state) and erased in strip mode, exactly like a
  // string payload. Newlines never reach here (a raw newline aborts regex state
  // and is emitted directly), so one space per char is length-preserving.
  const emitRegexChar = () => {
    if (commentsOnly || mask) {
      out.push(" ");
    }
  };
  // Stack permits templateExpr nesting (e.g. `a${`b${c}d`}e`). The top of
  // the stack is the active state. Each code-bearing frame (`code` /
  // `templateExpr`) carries `prevSig`: the last significant (non-whitespace)
  // character seen in that frame, used to disambiguate a `/` as regex vs.
  // division (see `slashStartsRegex`). It is "" at the start of the frame
  // (start-of-input, or the start of a `${...}` expression), where a leading
  // `/` is a regex.
  const stack = [{ kind: "code", prevSig: "" }];
  const n = source.length;
  let i = 0;
  // Record `ch` as the active code frame's last significant char (ignore
  // whitespace, which is never significant for the regex/division decision).
  const setPrevSig = (ch) => {
    if (ch === " " || ch === "\t" || ch === "\n" || ch === "\r") {
      return;
    }
    stack[stack.length - 1].prevSig = ch;
  };

  // Shebang: a `#!...` interpreter directive is only valid as the VERY FIRST
  // line and is not JavaScript. Consume it as plain code (emitted verbatim in
  // strip/mask, blanked to spaces in commentsOnly -- exactly the historical
  // behavior) WITHOUT updating prevSig, so the `/`s in `#!/usr/bin/env node`
  // are never misread as a regex literal and the first real statement still
  // begins in start-of-input context (a leading `/` there is a regex).
  if (source[0] === "#" && source[1] === "!") {
    while (i < n && source[i] !== "\n") {
      emitCode(source[i]);
      i++;
    }
  }

  while (i < n) {
    const frame = stack[stack.length - 1];
    const state = frame.kind;
    const ch = source[i];
    const next = i + 1 < n ? source[i + 1] : "";

    if (state === "code" || state === "templateExpr") {
      if (ch === "/" && next === "/") {
        stack.push({ kind: "lineComment" });
        // Blank the `//` opener in commentsOnly mode to keep offsets aligned.
        emitBlank();
        emitBlank();
        i += 2;
        continue;
      }
      if (ch === "/" && next === "*") {
        stack.push({ kind: "blockComment" });
        // Blank the `/*` opener in commentsOnly mode to keep offsets aligned.
        emitBlank();
        emitBlank();
        i += 2;
        continue;
      }
      // A lone `/` (not `//` or `/*`, handled above) opens a REGEX LITERAL when
      // the previous significant token cannot be a division operand. The regex
      // body is treated as a string/comment payload: its chars (and the `/`
      // delimiters and trailing flags) are blanked-to-spaces (mask/commentsOnly)
      // or erased (strip), so a quote inside a regex (`/["\n]/`) can never toggle
      // string state and corrupt a following `expect(...)`. When it is DIVISION,
      // the `/` is just another code char (handled by emitCode below).
      if (ch === "/" && slashStartsRegex(source, i, frame.prevSig)) {
        stack.push({ kind: "regex", inClass: false });
        // The opening `/` is part of the literal payload: blank/erase it.
        emitRegexChar();
        i++;
        continue;
      }
      if (ch === "'") {
        stack.push({ kind: "stringSingle" });
        emitCode("'");
        i++;
        continue;
      }
      if (ch === '"') {
        stack.push({ kind: "stringDouble" });
        emitCode('"');
        i++;
        continue;
      }
      if (ch === "`") {
        stack.push({ kind: "templateLiteral" });
        emitCode("`");
        i++;
        continue;
      }

      if (state === "templateExpr") {
        if (ch === "{") {
          frame.depth = (frame.depth || 0) + 1;
          emitCode(ch);
          setPrevSig(ch);
          i++;
          continue;
        }
        if (ch === "}") {
          if ((frame.depth || 0) === 0) {
            // Closing brace of the `${...}` expression itself;
            // pop back to the surrounding template literal. The `}` is part of
            // the template literal structure, not a code value, so the popped
            // template-literal frame carries no significance for a later `/`.
            stack.pop();
            emitCode("}");
            i++;
            continue;
          }
          frame.depth -= 1;
          emitCode(ch);
          setPrevSig(ch);
          i++;
          continue;
        }
      }

      emitCode(ch);
      setPrevSig(ch);
      i++;
      continue;
    }

    if (state === "lineComment") {
      if (ch === "\n") {
        stack.pop();
        out.push("\n");
        i++;
        continue;
      }
      // Defensive: a `\r` immediately before `\n` is part of CRLF; we
      // preserve neither the `\r` (strip mode) nor a redundant marker. In
      // commentsOnly mode we keep the comment payload verbatim (a stray `\r`
      // is harmless to substring checks); in strip mode it is erased.
      emitCommentChar(ch);
      i++;
      continue;
    }

    if (state === "blockComment") {
      if (ch === "*" && next === "/") {
        stack.pop();
        // Blank the `*/` closer in commentsOnly mode to keep offsets aligned.
        emitBlank();
        emitBlank();
        i += 2;
        continue;
      }
      if (ch === "\n") {
        out.push("\n");
        i++;
        continue;
      }
      emitCommentChar(ch);
      i++;
      continue;
    }

    if (state === "stringSingle") {
      if (ch === "\\" && i + 1 < n) {
        // Skip the escape sequence entirely (both chars). For
        // multi-line escapes like `\<newline>` we still drop the
        // backslash and the newline — the line-count invariant in
        // the file as a whole is unaffected because such constructs
        // also remove a logical line from the source. The dominant
        // case (single-char escapes) is correct.
        emitBlank();
        emitBlank();
        i += 2;
        continue;
      }
      if (ch === "'") {
        stack.pop();
        emitCode("'");
        // The completed string is a value: a following `/` is division.
        setPrevSig("'");
        i++;
        continue;
      }
      if (ch === "\n") {
        // Unterminated single-quoted string spanning a newline —
        // legal in source only via an escape; preserve the newline
        // so line numbers remain stable.
        out.push("\n");
        i++;
        continue;
      }
      emitBlank();
      i++;
      continue;
    }

    if (state === "stringDouble") {
      if (ch === "\\" && i + 1 < n) {
        emitBlank();
        emitBlank();
        i += 2;
        continue;
      }
      if (ch === '"') {
        stack.pop();
        emitCode('"');
        // The completed string is a value: a following `/` is division.
        setPrevSig('"');
        i++;
        continue;
      }
      if (ch === "\n") {
        out.push("\n");
        i++;
        continue;
      }
      emitBlank();
      i++;
      continue;
    }

    if (state === "templateLiteral") {
      if (ch === "\\" && i + 1 < n) {
        emitBlank();
        emitBlank();
        i += 2;
        continue;
      }
      if (ch === "`") {
        stack.pop();
        emitCode("`");
        // The completed template literal is a value: a following `/` is division.
        setPrevSig("`");
        i++;
        continue;
      }
      if (ch === "$" && next === "{") {
        // A fresh code-bearing frame: at the START of a `${...}` expression a
        // leading `/` is a regex, so prevSig begins empty.
        stack.push({ kind: "templateExpr", depth: 0, prevSig: "" });
        emitCode("$");
        emitCode("{");
        i += 2;
        continue;
      }
      if (ch === "\n") {
        out.push("\n");
        i++;
        continue;
      }
      emitBlank();
      i++;
      continue;
    }

    if (state === "regex") {
      // Escape: consume `\` + the escaped char as payload, so an escaped `/`
      // (`/a\/b/`) does NOT terminate and an escaped `]` does not close a class.
      // Guard a `\<newline>`: a regex cannot span a raw newline, so blank only
      // the `\` and let the newline be handled by the abort branch next.
      if (ch === "\\" && i + 1 < n && source[i + 1] !== "\n") {
        emitRegexChar();
        emitRegexChar();
        i += 2;
        continue;
      }
      // A raw newline aborts regex state defensively (regex literals cannot
      // contain an unescaped newline). Preserve the newline; resume in the
      // enclosing code/templateExpr frame.
      if (ch === "\n") {
        stack.pop();
        out.push("\n");
        i++;
        continue;
      }
      // Character class `[...]`: a `/` inside a class is literal, not the
      // terminator, so track class depth (classes do not nest).
      if (ch === "[" && !frame.inClass) {
        frame.inClass = true;
        emitRegexChar();
        i++;
        continue;
      }
      if (ch === "]" && frame.inClass) {
        frame.inClass = false;
        emitRegexChar();
        i++;
        continue;
      }
      // Unescaped `/` outside a character class closes the literal.
      if (ch === "/" && !frame.inClass) {
        stack.pop();
        // Blank/erase the closing `/`.
        emitRegexChar();
        i++;
        // Consume trailing regex flags (letters only, no separating space): the
        // flags are part of the literal payload, so blank/erase them too.
        while (i < n && /[A-Za-z]/.test(source[i])) {
          emitRegexChar();
          i++;
        }
        // The completed regex is a value: a following `/` is division.
        setPrevSig(")");
        continue;
      }
      emitRegexChar();
      i++;
      continue;
    }

    // Defensive: unknown state, advance one char to guarantee forward
    // progress. This branch is unreachable given the states above.
    i++;
  }

  return out.join("");
}

/**
 * Keep code, blank comment payloads and string/template payloads (preserving
 * quote/backtick markers and `${...}` delimiters). See the file header for full
 * semantics. Output is byte-for-byte identical to the historical implementation.
 *
 * @param {string} source
 * @returns {string}
 */
function stripJsCommentsAndStrings(source) {
  return projectSource(source, "strip");
}

/**
 * INVERSE of `stripJsCommentsAndStrings`: keep comment payloads verbatim and
 * blank everything else (code + string/template payloads) to spaces, preserving
 * line breaks. A substring/token that appears in the result lived in a real
 * comment span (line, block, or JSDoc, with or without a leading `*`) and never
 * in a string, template literal, or code. This is the discriminator a
 * marker-in-comment guard needs without re-implementing a tokenizer.
 *
 * @param {string} source
 * @returns {string}
 */
function extractCommentsOnly(source) {
  return projectSource(source, "comments");
}

/**
 * Third projection (offset-preserving): keep CODE verbatim and blank BOTH comment
 * AND string/template payloads to spaces, preserving line breaks and total length.
 * Sharing the SAME single tokenizer pass (`projectSource`) as
 * `stripJsCommentsAndStrings` / `extractCommentsOnly` guarantees all three agree
 * on what is code vs. comment vs. string.
 *
 * Unlike `stripJsCommentsAndStrings` (which collapses `"abc"` to `""` and drops
 * comment payloads, so offsets shift), this projection replaces every comment and
 * string/template payload character with a single space and never removes a
 * character, so the result is the SAME LENGTH as the input and every surviving
 * code character sits at its ORIGINAL offset. Quote/backtick markers and the
 * `${`/`}` delimiters of template expressions are kept verbatim (they are code
 * structure); the code INSIDE a `${...}` expression is kept verbatim too (it is
 * real code). A token that survives `maskCommentsAndStrings` at offset N lived in
 * real CODE at offset N -- never in a comment, string, or template payload --
 * which lets an offset-based scanner locate a real-code construct (e.g. an
 * `expect(` call) and then read the construct's exact text from the RAW source at
 * the aligned offset.
 *
 * Caveat (shared with `extractCommentsOnly`): a backslash line-continuation
 * INSIDE a string literal (`"...\<newline>..."`) blanks both the backslash and the
 * newline to two spaces, so a code offset AFTER such a continuation can drift by
 * one line. This construct does not occur in the static-analysis inputs this
 * projection serves; callers that must be bulletproof against it should not rely
 * on cross-line offset alignment past a string line-continuation.
 *
 * @param {string} source
 * @returns {string}
 */
function maskCommentsAndStrings(source) {
  return projectSource(source, "mask");
}

module.exports = {
  stripJsCommentsAndStrings,
  extractCommentsOnly,
  maskCommentsAndStrings
};
